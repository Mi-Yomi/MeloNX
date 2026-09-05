#nullable enable
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Ryujinx.Common.Memory
{
    /// <summary>The operation that originally rents an owner, retained until that owner is disposed.</summary>
    public enum MemoryOwnerPurpose
    {
        Unclassified,
        Decode,
        Recompress,
        LayoutConvert,
        Readback,
        GuestBridge,
        Upload,
        Mirror,
        Count,
    }

    /// <summary>Estimated array payload accounting, per closed generic type. Not process residency.</summary>
    public readonly record struct MemoryOwnerPoolStatistics(
        long RetainedBytes, long LeasedBytes, long PeakLeasedBytes,
        int RetainedArrays, long Rents, long Reuses, long DiscardedBytes,
        long CreatedArrays, long CreatedBytes, long DiscardedArrays);

    /// <summary>
    /// Keeps only returned arrays, with hard byte/count bounds. Leased arrays are never reclaimed.
    /// Callers must return each successful rental exactly once; MemoryOwner enforces that contract.
    /// </summary>
    internal sealed class BoundedArrayPool<T>
    {
        // Sparse CPU mirrors rent several 4-KiB pages before returning them together.
        // Keeping only one array of each length makes every following burst allocate
        // all but one page again. Small duplicates share the existing global limits
        // without evicting large conversion arrays to make room for cheap pages.
        // Large buffers still retain only one instance of each length.
        private const int SmallArrayBytes = 4 * 1024;

        private readonly record struct Entry(T[] Array, long ReturnedAt, MemoryOwnerPurpose Purpose);
        private struct PurposeStatistics
        {
            public long RetainedBytes, LeasedBytes, PeakLeasedBytes;
            public int RetainedArrays;
            public long Rents, Reuses, DiscardedBytes, CreatedArrays, CreatedBytes, DiscardedArrays;

            public readonly MemoryOwnerPoolStatistics Snapshot() => new(
                RetainedBytes, LeasedBytes, PeakLeasedBytes, RetainedArrays, Rents, Reuses, DiscardedBytes,
                CreatedArrays, CreatedBytes, DiscardedArrays);
        }

        private readonly Lock _lock = new();
        private readonly List<Entry> _entries = [];
        private readonly PurposeStatistics[] _purposes = new PurposeStatistics[(int)MemoryOwnerPurpose.Count];
        private readonly long _maxRetainedBytes;
        private readonly int _maxRetainedArrays;
        private readonly long _maxArrayBytes;
        private long _retainedBytes;
        private long _leasedBytes;
        private long _peakLeasedBytes;
        private long _rents;
        private long _reuses;
        private long _discardedBytes;
        private long _createdArrays;
        private long _createdBytes;
        private long _discardedArrays;
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

        internal T[] Rent(int minimumLength, MemoryOwnerPurpose purpose = MemoryOwnerPurpose.Unclassified)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(minimumLength);
            ValidatePurpose(purpose);
            T[]? result = null;
            lock (_lock)
            {
                int index = LowerBound(minimumLength);
                // Use a long product: large valid requests must not overflow the fit comparison.
                if (index < _entries.Count && _entries[index].Array.Length <= 4L * minimumLength)
                {
                    Entry entry = _entries[index];
                    result = entry.Array;
                    _entries.RemoveAt(index);
                    _retainedBytes -= Bytes(result);
                    _purposes[(int)entry.Purpose].RetainedBytes -= Bytes(result);
                    _purposes[(int)entry.Purpose].RetainedArrays--;
                    _reuses++;
                    _purposes[(int)purpose].Reuses++;
                }
            }

            // Do not serialize large allocations/zeroing with other threads returning arrays.
            bool created = result == null && minimumLength != 0;
            result ??= minimumLength == 0 ? Array.Empty<T>() : new T[minimumLength];
            lock (_lock)
            {
                long bytes = Bytes(result);
                ref PurposeStatistics statistics = ref _purposes[(int)purpose];
                _rents++;
                _leasedBytes += bytes;
                _peakLeasedBytes = Math.Max(_peakLeasedBytes, _leasedBytes);
                statistics.Rents++;
                statistics.LeasedBytes += bytes;
                statistics.PeakLeasedBytes = Math.Max(statistics.PeakLeasedBytes, statistics.LeasedBytes);
                if (created)
                {
                    _createdArrays++;
                    _createdBytes += bytes;
                    statistics.CreatedArrays++;
                    statistics.CreatedBytes += bytes;
                }
            }
            return result;
        }

        internal void Return(T[] array, MemoryOwnerPurpose purpose = MemoryOwnerPurpose.Unclassified)
        {
            ValidatePurpose(purpose);
            // A cached reference array must not keep unrelated objects alive after its owner exits.
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T>()) Array.Clear(array);
            long bytes = Bytes(array);
            lock (_lock)
            {
                _leasedBytes -= bytes;
                _purposes[(int)purpose].LeasedBytes -= bytes;
                int index = LowerBound(array.Length);
                if (bytes == 0 || bytes > _maxArrayBytes || _maxRetainedArrays == 0 ||
                    (bytes > SmallArrayBytes && index < _entries.Count && _entries[index].Array.Length == array.Length))
                {
                    Discard(bytes, purpose);
                    return;
                }

                while (_entries.Count != 0 &&
                    (_retainedBytes > _maxRetainedBytes - bytes || _entries.Count >= _maxRetainedArrays))
                {
                    if (!RemoveOldest(smallOnly: bytes <= SmallArrayBytes))
                    {
                        Discard(bytes, purpose);
                        return;
                    }
                }

                index = LowerBound(array.Length);
                _entries.Insert(index, new Entry(array, ++_clock, purpose));
                _retainedBytes += bytes;
                _purposes[(int)purpose].RetainedBytes += bytes;
                _purposes[(int)purpose].RetainedArrays++;
            }
        }

        private bool RemoveOldest(bool smallOnly = false)
        {
            int oldest = -1;
            for (int i = 0; i < _entries.Count; i++)
            {
                if (smallOnly && Bytes(_entries[i].Array) > SmallArrayBytes) continue;
                if (oldest < 0 || _entries[i].ReturnedAt < _entries[oldest].ReturnedAt) oldest = i;
            }
            if (oldest < 0) return false;

            Entry entry = _entries[oldest];
            long bytes = Bytes(entry.Array);
            _entries.RemoveAt(oldest);
            _retainedBytes -= bytes;
            _purposes[(int)entry.Purpose].RetainedBytes -= bytes;
            _purposes[(int)entry.Purpose].RetainedArrays--;
            Discard(bytes, entry.Purpose);
            return true;
        }

        private void Discard(long bytes, MemoryOwnerPurpose purpose)
        {
            _discardedBytes += bytes;
            _purposes[(int)purpose].DiscardedBytes += bytes;
            // Array.Empty<T>() is shared, so it is neither created nor discarded by this pool.
            if (bytes != 0)
            {
                _discardedArrays++;
                _purposes[(int)purpose].DiscardedArrays++;
            }
        }

        private static void ValidatePurpose(MemoryOwnerPurpose purpose)
        {
            if ((uint)purpose >= (uint)MemoryOwnerPurpose.Count)
                throw new ArgumentOutOfRangeException(nameof(purpose));
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
                return new(_retainedBytes, _leasedBytes, _peakLeasedBytes, _entries.Count, _rents, _reuses, _discardedBytes,
                    _createdArrays, _createdBytes, _discardedArrays);
        }

        /// <summary>Idle ownership is charged to the last returning purpose; active ownership to the current renter.</summary>
        internal MemoryOwnerPoolStatistics GetStatistics(MemoryOwnerPurpose purpose)
        {
            ValidatePurpose(purpose);
            lock (_lock)
                return _purposes[(int)purpose].Snapshot();
        }
    }
}
