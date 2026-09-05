#nullable enable
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Ryujinx.Common.Memory
{
    /// <summary>Estimated array payload accounting, per closed generic type. Not process residency.</summary>
    public readonly record struct MemoryOwnerPoolStatistics(
        long RetainedBytes, long LeasedBytes, long PeakLeasedBytes,
        int RetainedArrays, long Rents, long Reuses, long DiscardedBytes);

    /// <summary>
    /// Keeps only returned arrays, with hard byte/count bounds. Leased arrays are never reclaimed.
    /// Callers must return each successful rental exactly once; MemoryOwner enforces that contract.
    /// </summary>
    internal sealed class BoundedArrayPool<T>
    {
        private readonly record struct Entry(T[] Array, long ReturnedAt);
        private readonly Lock _lock = new();
        private readonly List<Entry> _entries = [];
        private readonly long _maxRetainedBytes;
        private readonly int _maxRetainedArrays;
        private readonly long _maxArrayBytes;
        private long _retainedBytes;
        private long _leasedBytes;
        private long _peakLeasedBytes;
        private long _rents;
        private long _reuses;
        private long _discardedBytes;
        private long _clock;

        internal BoundedArrayPool(long maxRetainedBytes, int maxRetainedArrays, long maxArrayBytes)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(maxRetainedBytes);
            ArgumentOutOfRangeException.ThrowIfNegative(maxRetainedArrays);
            ArgumentOutOfRangeException.ThrowIfNegative(maxArrayBytes);
            _maxRetainedBytes = maxRetainedBytes;
            _maxRetainedArrays = maxRetainedArrays;
            _maxArrayBytes = Math.Min(maxArrayBytes, maxRetainedBytes);
        }

        private static long Bytes(T[] array) => (long)array.Length * Unsafe.SizeOf<T>();

        private int LowerBound(int length)
        {
            int lo = 0, hi = _entries.Count;
            while (lo < hi)
            {
                int mid = lo + ((hi - lo) >> 1);
                if (_entries[mid].Array.Length < length) lo = mid + 1;
                else hi = mid;
            }
            return lo;
        }

        internal T[] Rent(int minimumLength)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(minimumLength);
            T[]? result = null;
            lock (_lock)
            {
                int index = LowerBound(minimumLength);
                // Use a long product: large valid requests must not overflow the fit comparison.
                if (index < _entries.Count && _entries[index].Array.Length <= 4L * minimumLength)
                {
                    result = _entries[index].Array;
                    _entries.RemoveAt(index);
                    _retainedBytes -= Bytes(result);
                    _reuses++;
                }
            }

            // Do not serialize large allocations/zeroing with other threads returning arrays.
            result ??= minimumLength == 0 ? Array.Empty<T>() : new T[minimumLength];
            lock (_lock)
            {
                _rents++;
                _leasedBytes += Bytes(result);
                _peakLeasedBytes = Math.Max(_peakLeasedBytes, _leasedBytes);
            }
            return result;
        }

        internal void Return(T[] array)
        {
            // A cached reference array must not keep unrelated objects alive after its owner exits.
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T>()) Array.Clear(array);
            long bytes = Bytes(array);
            lock (_lock)
            {
                _leasedBytes -= bytes;
                int index = LowerBound(array.Length);
                if (bytes == 0 || bytes > _maxArrayBytes || _maxRetainedArrays == 0 ||
                    (index < _entries.Count && _entries[index].Array.Length == array.Length))
                {
                    _discardedBytes += bytes;
                    return;
                }

                while (_entries.Count != 0 &&
                    (_retainedBytes > _maxRetainedBytes - bytes || _entries.Count >= _maxRetainedArrays))
                {
                    RemoveOldest();
                }

                index = LowerBound(array.Length);
                _entries.Insert(index, new Entry(array, ++_clock));
                _retainedBytes += bytes;
            }
        }

        private void RemoveOldest()
        {
            int oldest = 0;
            for (int i = 1; i < _entries.Count; i++)
                if (_entries[i].ReturnedAt < _entries[oldest].ReturnedAt) oldest = i;
            long bytes = Bytes(_entries[oldest].Array);
            _entries.RemoveAt(oldest);
            _retainedBytes -= bytes;
            _discardedBytes += bytes;
        }

        internal long Trim(long targetRetainedBytes)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(targetRetainedBytes);
            lock (_lock)
            {
                long before = _retainedBytes;
                while (_retainedBytes > targetRetainedBytes && _entries.Count != 0) RemoveOldest();
                return before - _retainedBytes;
            }
        }

        internal MemoryOwnerPoolStatistics GetStatistics()
        {
            lock (_lock)
                return new(_retainedBytes, _leasedBytes, _peakLeasedBytes, _entries.Count, _rents, _reuses, _discardedBytes);
        }
    }
}
