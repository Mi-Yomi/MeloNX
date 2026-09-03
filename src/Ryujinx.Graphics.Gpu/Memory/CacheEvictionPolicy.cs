using System;
using System.Collections.Generic;

namespace Ryujinx.Graphics.Gpu.Memory
{
    /// <summary>
    /// Tracks cached resources in least-recently-used order and removes eligible entries to meet a byte budget.
    /// Resource lifetime rules remain with the owner and are supplied to <see cref="Trim"/>.
    /// </summary>
    /// <typeparam name="T">Type of cached resource</typeparam>
    class CacheEvictionPolicy<T> where T : class
    {
        private readonly LinkedList<T> _items;
        private readonly Func<T, ulong> _getSize;
        private readonly Func<T, LinkedListNode<T>> _getNode;
        private readonly Action<T, LinkedListNode<T>> _setNode;

        public ulong Capacity { get; set; }
        public ulong CachedBytes { get; private set; }
        public IEnumerable<T> OldestFirst => _items;

        public CacheEvictionPolicy(
            ulong capacity,
            Func<T, ulong> getSize,
            Func<T, LinkedListNode<T>> getNode,
            Action<T, LinkedListNode<T>> setNode)
        {
            Capacity = capacity;
            _getSize = getSize;
            _getNode = getNode;
            _setNode = setNode;
            _items = [];
        }

        public void Add(T item)
        {
            if (_getNode(item) != null)
            {
                throw new InvalidOperationException("The cache entry is already tracked.");
            }

            CachedBytes += _getSize(item);
            _setNode(item, _items.AddLast(item));
        }

        public void Remove(T item)
        {
            LinkedListNode<T> node = _getNode(item);

            if (node != null)
            {
                _items.Remove(node);
                _setNode(item, null);
                CachedBytes -= _getSize(item);
            }
        }

        public void Touch(T item)
        {
            LinkedListNode<T> node = _getNode(item);

            if (node != null && node != _items.Last)
            {
                _items.Remove(node);
                _items.AddLast(node);
            }
        }

        /// <summary>
        /// Removes the oldest eligible entries until the cache meets its budget or no candidate remains.
        /// </summary>
        /// <param name="canEvict">Checks whether an entry can be discarded at this point</param>
        /// <param name="evict">Discards storage owned by an entry after it is untracked</param>
        /// <returns>True if at least one entry was evicted</returns>
        public bool Trim(Func<T, bool> canEvict, Action<T> evict)
        {
            return TrimTo(Capacity, canEvict, evict);
        }

        /// <summary>
        /// Removes the oldest eligible entries until the cache meets the supplied byte budget.
        /// </summary>
        public bool TrimTo(ulong capacity, Func<T, bool> canEvict, Action<T> evict)
        {
            LinkedListNode<T> node = _items.First;
            bool removed = false;

            while (CachedBytes > capacity && node != null)
            {
                LinkedListNode<T> next = node.Next;
                T item = node.Value;

                if (canEvict(item))
                {
                    Remove(item);
                    evict(item);
                    removed = true;
                }

                node = next;
            }

            return removed;
        }

        /// <summary>
        /// Removes every eligible entry. This is used to release resources that keep entries in another cache alive.
        /// </summary>
        public bool EvictEligible(Func<T, bool> canEvict, Action<T> evict)
        {
            LinkedListNode<T> node = _items.First;
            bool removed = false;

            while (node != null)
            {
                LinkedListNode<T> next = node.Next;
                T item = node.Value;

                if (canEvict(item))
                {
                    Remove(item);
                    evict(item);
                    removed = true;
                }

                node = next;
            }

            return removed;
        }

        public void Clear()
        {
            LinkedListNode<T> node = _items.First;

            while (node != null)
            {
                LinkedListNode<T> next = node.Next;
                _setNode(node.Value, null);
                node = next;
            }

            _items.Clear();
            CachedBytes = 0;
        }
    }

    /// <summary>
    /// Selects sparse aliases whose removal is sufficient to make at least one physical resource evictable.
    /// </summary>
    static class CacheDependencyEvictionPolicy
    {
        public static HashSet<TAlias> SelectAliasesToRelease<TAlias, TStorage>(
            IEnumerable<TAlias> aliases,
            IEnumerable<TStorage> storageInEvictionOrder,
            ulong bytesToFree,
            Func<TAlias, bool> canReleaseAlias,
            Func<TAlias, IReadOnlyList<TStorage>> getDependencies,
            Func<TStorage, int> getDependencyCount,
            Func<TStorage, bool> canEvictAfterRelease,
            Func<TStorage, ulong> getStorageSize)
            where TAlias : class
            where TStorage : class
        {
            Dictionary<TStorage, List<TAlias>> releasableAliasesByStorage = [];

            foreach (TAlias alias in aliases)
            {
                if (!canReleaseAlias(alias))
                {
                    continue;
                }

                foreach (TStorage storage in getDependencies(alias))
                {
                    if (!releasableAliasesByStorage.TryGetValue(storage, out List<TAlias> releasableAliases))
                    {
                        releasableAliases = [];
                        releasableAliasesByStorage.Add(storage, releasableAliases);
                    }

                    releasableAliases.Add(alias);
                }
            }

            HashSet<TAlias> selectedAliases = [];

            foreach (TStorage storage in storageInEvictionOrder)
            {
                if (releasableAliasesByStorage.TryGetValue(storage, out List<TAlias> releasableAliases) &&
                    releasableAliases.Count == getDependencyCount(storage) &&
                    canEvictAfterRelease(storage))
                {
                    foreach (TAlias alias in releasableAliases)
                    {
                        selectedAliases.Add(alias);
                    }

                    ulong storageSize = getStorageSize(storage);

                    if (storageSize >= bytesToFree)
                    {
                        break;
                    }

                    bytesToFree -= storageSize;
                }
            }

            return selectedAliases;
        }
    }
}
