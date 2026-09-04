using Ryujinx.Common.Logging;
using System.Collections;
using System.Collections.Generic;

namespace Ryujinx.Graphics.Gpu.Image
{
    static class TexturePressureTrimPolicy
    {
        public static bool CanEvict(bool hasOneReference, bool cpuModified, bool gpuModified)
        {
            return hasOneReference && (cpuModified || !gpuModified);
        }
    }

    static class NormalTextureEvictionPolicy
    {
        public const int CandidateScanLimit = 4;

        public static bool RequiresReadback(bool cpuModified, bool gpuModified)
        {
            return !cpuModified && gpuModified;
        }

        public static bool CanSelectAlternative(
            bool oldestAlreadyDeferred,
            bool hasOneReference,
            bool cpuModified,
            bool gpuModified)
        {
            return !oldestAlreadyDeferred &&
                   TexturePressureTrimPolicy.CanEvict(hasOneReference, cpuModified, gpuModified);
        }
    }

    /// <summary>
    /// An entry on the short duration texture cache.
    /// </summary>
    class ShortTextureCacheEntry
    {
        public bool IsAutoDelete;
        public readonly TextureDescriptor Descriptor;
        public readonly int InvalidatedSequence;
        public readonly Texture Texture;

        /// <summary>
        /// Create a new entry on the short duration texture cache.
        /// </summary>
        /// <param name="descriptor">Last descriptor that referenced the texture</param>
        /// <param name="texture">The texture</param>
        public ShortTextureCacheEntry(TextureDescriptor descriptor, Texture texture)
        {
            Descriptor = descriptor;
            InvalidatedSequence = texture.InvalidatedSequence;
            Texture = texture;
        }

        /// <summary>
        /// Create a new entry on the short duration texture cache from the auto delete cache.
        /// </summary>
        /// <param name="texture">The texture</param>
        public ShortTextureCacheEntry(Texture texture)
        {
            IsAutoDelete = true;
            InvalidatedSequence = texture.InvalidatedSequence;
            Texture = texture;
        }
    }

    /// <summary>
    /// A texture cache that automatically removes older textures that are not used for some time.
    /// The cache works with a rotated list with a fixed size. When new textures are added, the
    /// old ones at the bottom of the list are deleted.
    /// </summary>
    class AutoDeleteCache : IEnumerable<Texture>
    {
        private const int MaxCapacity = 2048;
        private const ulong MiB = 1024 * 1024;
        private const ulong DefaultTextureSizeCapacity = 1UL * 1024 * MiB;
        private ulong _maxCacheMemoryUsage = DefaultTextureSizeCapacity;
        private bool _memoryBudgetConfigured;
        private bool _isAppleUnifiedMemory;

        private readonly LinkedList<Texture> _textures;
        private ulong _totalSize;
        private Texture _lastReadbackDeferredTexture;
        private ulong _normalEvictions;
        private ulong _normalEvictedBytes;
        private ulong _normalReadbackEvictions;
        private ulong _normalCleanBypasses;

        internal ulong CachedBytes => _totalSize;
        internal ulong Capacity => _maxCacheMemoryUsage;

        internal (
            int Entries,
            ulong LargestEntryBytes,
            ulong NormalEvictions,
            ulong NormalEvictedBytes,
            ulong NormalReadbackEvictions,
            ulong NormalCleanBypasses) GetStatistics()
        {
            ulong largestEntryBytes = 0;

            foreach (Texture texture in _textures)
            {
                largestEntryBytes = ulong.Max(largestEntryBytes, texture.CacheSize);
            }

            return (
                _textures.Count,
                largestEntryBytes,
                _normalEvictions,
                _normalEvictedBytes,
                _normalReadbackEvictions,
                _normalCleanBypasses);
        }

        private HashSet<ShortTextureCacheEntry> _shortCacheBuilder;
        private HashSet<ShortTextureCacheEntry> _shortCache;

        private readonly Dictionary<TextureDescriptor, ShortTextureCacheEntry> _shortCacheLookup;

        /// <summary>
        /// Configures the cache memory budget without modifying resident entries.
        /// </summary>
        /// <param name="capacity">Maximum number of resident texture bytes</param>
        /// <param name="isAppleUnifiedMemory">Whether the budget targets Apple unified memory</param>
        public void ConfigureMemoryBudget(ulong capacity, bool isAppleUnifiedMemory)
        {
            if (_memoryBudgetConfigured &&
                _maxCacheMemoryUsage == capacity &&
                _isAppleUnifiedMemory == isAppleUnifiedMemory)
            {
                return;
            }

            _maxCacheMemoryUsage = capacity;
            _memoryBudgetConfigured = true;
            _isAppleUnifiedMemory = isAppleUnifiedMemory;

            string memoryKind = isAppleUnifiedMemory ? " (Apple unified memory)" : string.Empty;
            Logger.Info?.Print(LogClass.Gpu, $"AutoDelete cache memory limit: {_maxCacheMemoryUsage / MiB} MiB{memoryKind}");
        }

        /// <summary>
        /// Creates a new instance of the automatic deletion cache.
        /// </summary>
        public AutoDeleteCache()
        {
            _textures = [];

            _shortCacheBuilder = [];
            _shortCache = [];

            _shortCacheLookup = new Dictionary<TextureDescriptor, ShortTextureCacheEntry>();
        }

        /// <summary>
        /// Adds a new texture to the cache, even if the texture added is already on the cache.
        /// </summary>
        /// <remarks>
        /// Using this method is only recommended if you know that the texture is not yet on the cache,
        /// otherwise it would store the same texture more than once.
        /// </remarks>
        /// <param name="texture">The texture to be added to the cache</param>
        public void Add(Texture texture)
        {
            texture.CacheSize = texture.GetEstimatedHostSize();
            _totalSize += texture.CacheSize;

            texture.IncrementReferenceCount();
            texture.CacheNode = _textures.AddLast(texture);

            EnforceCapacity();
        }

        /// <summary>
        /// Adds a new texture to the cache, or just moves it to the top of the list if the
        /// texture is already on the cache.
        /// </summary>
        /// <remarks>
        /// Moving the texture to the top of the list prevents it from being deleted,
        /// as the textures on the bottom of the list are deleted when new ones are added.
        /// </remarks>
        /// <param name="texture">The texture to be added, or moved to the top</param>
        public void Lift(Texture texture)
        {
            if (texture.CacheNode != null)
            {
                ulong oldSize = texture.CacheSize;
                texture.CacheSize = texture.GetEstimatedHostSize();
                _totalSize = _totalSize - oldSize + texture.CacheSize;

                if (texture.CacheNode != _textures.Last)
                {
                    _textures.Remove(texture.CacheNode);
                    _textures.AddLast(texture.CacheNode);
                }

                EnforceCapacity();
            }
            else
            {
                Add(texture);
            }
        }

        /// <summary>
        /// Removes the least used texture from the cache.
        /// </summary>
        private void RemoveLeastUsedTexture()
        {
            Texture oldest = _textures.First.Value;
            Texture candidate = oldest;
            bool oldestCpuModified = oldest.CheckModified(false);
            bool oldestGpuModified = !oldestCpuModified && oldest.Group.HasGpuModifiedData(oldest);
            bool oldestRequiresReadback = NormalTextureEvictionPolicy.RequiresReadback(
                oldestCpuModified,
                oldestGpuModified);
            bool oldestAlreadyDeferred = oldest == _lastReadbackDeferredTexture;

            // A GPU-dirty LRU can require a synchronous readback. Before paying that cost, inspect
            // a few nearby entries for storage that can actually be released without readback.
            // Never inspect the MRU, and never defer the same dirty LRU twice: unbounded clean-first
            // eviction would make streaming workloads continually recreate their newest textures.
            if (oldestRequiresReadback && !oldestAlreadyDeferred)
            {
                LinkedListNode<Texture> node = _textures.First.Next;
                LinkedListNode<Texture> mostRecent = _textures.Last;
                int scanned = 0;

                while (node != null && node != mostRecent && scanned < NormalTextureEvictionPolicy.CandidateScanLimit)
                {
                    Texture alternative = node.Value;
                    bool alternativeHasOneReference = alternative.HasOneReference();
                    bool alternativeCpuModified = false;
                    bool alternativeGpuModified = false;

                    if (alternativeHasOneReference)
                    {
                        alternativeCpuModified = alternative.CheckModified(false);
                        alternativeGpuModified = !alternativeCpuModified &&
                            alternative.Group.HasGpuModifiedData(alternative);
                    }

                    if (NormalTextureEvictionPolicy.CanSelectAlternative(
                        oldestAlreadyDeferred,
                        alternativeHasOneReference,
                        alternativeCpuModified,
                        alternativeGpuModified))
                    {
                        candidate = alternative;
                        break;
                    }

                    node = node.Next;
                    scanned++;
                }
            }

            if (candidate == oldest)
            {
                _lastReadbackDeferredTexture = null;

                if (oldestRequiresReadback)
                {
                    _normalReadbackEvictions++;
                }
            }
            else
            {
                _lastReadbackDeferredTexture = oldest;
                _normalCleanBypasses++;
            }

            _normalEvictions++;
            _normalEvictedBytes += candidate.CacheSize;

            RemoveCachedTexture(candidate, synchronizeModifiedData: true);
        }

        /// <summary>
        /// Removes a texture and releases the reference owned by this cache.
        /// </summary>
        /// <param name="texture">Texture currently linked into the cache</param>
        /// <param name="synchronizeModifiedData">Whether normal eviction writeback should run</param>
        private void RemoveCachedTexture(Texture texture, bool synchronizeModifiedData)
        {
            if (texture == _lastReadbackDeferredTexture)
            {
                _lastReadbackDeferredTexture = null;
            }

            _totalSize -= texture.CacheSize;
            texture.CacheSize = 0;

            if (synchronizeModifiedData && !texture.CheckModified(false))
            {
                // The texture must be flushed if it falls out of the auto delete cache.
                // Flushes out of the auto delete cache do not trigger write tracking,
                // as it is expected that other overlapping textures exist that have more up-to-date contents.

                texture.Group.SynchronizeDependents(texture);
                texture.FlushModified(false);
            }

            _textures.Remove(texture.CacheNode);

            texture.DecrementReferenceCount();
            texture.CacheNode = null;
        }

        /// <summary>
        /// Removes old textures until the limits are satisfied, retaining the most recently used texture.
        /// </summary>
        private void EnforceCapacity()
        {
            // Add/Lift may hold the only reference to a texture still needed by the caller.
            // A single oversized texture must remain alive until it can be replaced or explicitly removed.
            while (_textures.Count > MaxCapacity ||
                   (_totalSize > _maxCacheMemoryUsage && _textures.Count > 1))
            {
                RemoveLeastUsedTexture();
            }
        }

        /// <summary>
        /// Removes safe least-recently-used textures until the requested temporary capacity is met.
        /// GPU-modified textures are skipped because their readback can allocate more memory at exactly
        /// the point where the host is under pressure. Referenced textures and the MRU entry are retained.
        /// </summary>
        /// <param name="capacity">Temporary maximum number of resident texture bytes</param>
        public (int Evicted, int SkippedReferenced, int SkippedModified, ulong RetainedMostRecentBytes) TrimForMemoryPressure(ulong capacity)
        {
            int evicted = 0;
            int skippedReferenced = 0;
            int skippedModified = 0;
            LinkedListNode<Texture> node = _textures.First;
            LinkedListNode<Texture> mostRecent = _textures.Last;

            while (_totalSize > capacity && node != null && node != mostRecent)
            {
                LinkedListNode<Texture> next = node.Next;
                Texture texture = node.Value;
                bool hasOneReference = texture.HasOneReference();
                bool cpuModified = false;
                bool gpuModified = false;

                if (hasOneReference)
                {
                    cpuModified = texture.CheckModified(false);
                    gpuModified = !cpuModified && texture.Group.HasGpuModifiedData(texture);
                }

                if (TexturePressureTrimPolicy.CanEvict(hasOneReference, cpuModified, gpuModified))
                {
                    if (!cpuModified)
                    {
                        texture.Group.SynchronizeDependents(texture);
                    }

                    RemoveCachedTexture(texture, synchronizeModifiedData: false);
                    evicted++;
                }
                else if (!hasOneReference)
                {
                    skippedReferenced++;
                }
                else
                {
                    skippedModified++;
                }

                node = next;
            }

            ulong retainedMostRecentBytes = _totalSize > capacity && mostRecent != null
                ? mostRecent.Value.CacheSize
                : 0;

            return (evicted, skippedReferenced, skippedModified, retainedMostRecentBytes);
        }

        /// <summary>
        /// Removes a texture from the cache.
        /// </summary>
        /// <param name="texture">The texture to be removed from the cache</param>
        /// <param name="flush">True to remove the texture if it was on the cache</param>
        /// <returns>True if the texture was found and removed, false otherwise</returns>
        public bool Remove(Texture texture, bool flush)
        {
            if (texture.CacheNode == null)
            {
                return false;
            }

            if (texture == _lastReadbackDeferredTexture)
            {
                _lastReadbackDeferredTexture = null;
            }

            // Remove our reference to this texture.
            if (flush)
            {
                texture.FlushModified(false);
            }

            _textures.Remove(texture.CacheNode);

            _totalSize -= texture.CacheSize;
            texture.CacheSize = 0;

            texture.CacheNode = null;

            return texture.DecrementReferenceCount();
        }

        /// <summary>
        /// Attempt to find a texture on the short duration cache.
        /// </summary>
        /// <param name="descriptor">The texture descriptor</param>
        /// <returns>The texture if found, null otherwise</returns>
        public Texture FindShortCache(in TextureDescriptor descriptor)
        {
            if (_shortCacheLookup.Count > 0 && _shortCacheLookup.TryGetValue(descriptor, out ShortTextureCacheEntry entry))
            {
                if (entry.InvalidatedSequence == entry.Texture.InvalidatedSequence)
                {
                    return entry.Texture;
                }
                else
                {
                    _shortCacheLookup.Remove(descriptor);
                }
            }

            return null;
        }

        /// <summary>
        /// Removes a texture from the short duration cache.
        /// </summary>
        /// <param name="texture">Texture to remove from the short cache</param>
        public void RemoveShortCache(Texture texture)
        {
            bool removed = _shortCache.Remove(texture.ShortCacheEntry);
            removed |= _shortCacheBuilder.Remove(texture.ShortCacheEntry);

            if (removed)
            {
                texture.DecrementReferenceCount();

                if (!texture.ShortCacheEntry.IsAutoDelete)
                {
                    _shortCacheLookup.Remove(texture.ShortCacheEntry.Descriptor);
                }

                texture.ShortCacheEntry = null;
            }
        }

        /// <summary>
        /// Adds a texture to the short duration cache.
        /// It starts in the builder set, and it is moved into the deletion set on next process.
        /// </summary>
        /// <param name="texture">Texture to add to the short cache</param>
        /// <param name="descriptor">Last used texture descriptor</param>
        public void AddShortCache(Texture texture, ref TextureDescriptor descriptor)
        {
            ShortTextureCacheEntry entry = new(descriptor, texture);

            _shortCacheBuilder.Add(entry);
            _shortCacheLookup.Add(entry.Descriptor, entry);

            texture.ShortCacheEntry = entry;

            texture.IncrementReferenceCount();
        }

        /// <summary>
        /// Adds a texture to the short duration cache without a descriptor. This typically keeps it alive for two ticks.
        /// On expiry, it will be removed from the AutoDeleteCache.
        /// </summary>
        /// <param name="texture">Texture to add to the short cache</param>
        public void AddShortCache(Texture texture)
        {
            if (texture.ShortCacheEntry == null)
            {
                ShortTextureCacheEntry entry = new(texture);

                _shortCacheBuilder.Add(entry);

                texture.ShortCacheEntry = entry;

                texture.IncrementReferenceCount();
            }
        }

        /// <summary>
        /// Delete textures from the short duration cache.
        /// Moves the builder set to be deleted on next process.
        /// </summary>
        public void ProcessShortCache()
        {
            HashSet<ShortTextureCacheEntry> toRemove = _shortCache;

            foreach (ShortTextureCacheEntry entry in toRemove)
            {
                entry.Texture.DecrementReferenceCount();

                if (entry.IsAutoDelete)
                {
                    Remove(entry.Texture, false);
                }
                else
                {
                    _shortCacheLookup.Remove(entry.Descriptor);
                }

                entry.Texture.ShortCacheEntry = null;
            }

            toRemove.Clear();
            _shortCache = _shortCacheBuilder;
            _shortCacheBuilder = toRemove;
        }

        public IEnumerator<Texture> GetEnumerator()
        {
            return _textures.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return _textures.GetEnumerator();
        }
    }
}
