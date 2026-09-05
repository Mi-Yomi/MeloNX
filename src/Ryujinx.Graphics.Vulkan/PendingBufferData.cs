using Ryujinx.Common.Memory;
using System;
using System.Collections.Generic;

namespace Ryujinx.Graphics.Vulkan
{
    internal interface IPendingBufferUpload
    {
        // The implementation consumes/copies the span before returning; it cannot
        // retain managed scratch as a future GPU source without its own ownership.
        void Upload(int offset, ReadOnlySpan<byte> data);
    }

    /// <summary>
    /// CPU mirror data owned by the renderer thread. Only dirty 4-KiB pages are rented,
    /// rather than a zero-filled array as large as the entire GPU buffer. Published
    /// staging mirrors keep their existing fence-bound ownership independently.
    /// </summary>
    internal sealed class PendingBufferData : IDisposable
    {
        internal const int PageSize = 4096;
        internal const int MaxUploadBatchSize = 64 * 1024;
        private readonly int _size;
        private readonly Dictionary<int, MemoryOwner<byte>> _pages = new();
        private readonly Func<int, MemoryOwner<byte>> _rent;
        private BufferMirrorRangeList _dirty;
        private int _uploadDepth;
        private bool _disposed;

        internal PendingBufferData(int size, Func<int, MemoryOwner<byte>> rent = null)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(size);
            _size = size;
            _rent = rent ?? (static count => MemoryOwner<byte>.Rent(count, MemoryOwnerPurpose.Mirror));
        }

        internal int PageCount => _pages.Count;
        internal long LogicalPageBytes => (long)_pages.Count * PageSize;
        internal bool HasData => _dirty.Count() != 0;
        internal bool Overlaps(int offset, int size)
        {
            Validate(offset, size);
            return size != 0 && _dirty.OverlapsWith(offset, size);
        }

        private MemoryOwner<byte> Rent(int size)
        {
            MemoryOwner<byte> owner = _rent(size);
            if (owner == null || owner.Length < size)
            {
                owner?.Dispose();
                throw new InvalidOperationException("Invalid pending-data rental.");
            }

            return owner;
        }

        private void Validate(int offset, int length)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (offset < 0 || length < 0 || offset > _size - length)
                throw new ArgumentOutOfRangeException(nameof(offset));
        }

        internal void Write(int offset, ReadOnlySpan<byte> data)
        {
            Validate(offset, data.Length);
            if (data.IsEmpty) return;
            int first = offset / PageSize;
            int last = (offset + data.Length - 1) / PageSize;
            int missing = 0;
            for (int page = first; page <= last; page++)
                if (!_pages.ContainsKey(page)) missing++;

            if (missing != 0)
            {
                // Allocate every needed page before changing existing data. A failed
                // rental cannot leave a partially overwritten pending CPU update.
                _pages.EnsureCapacity(checked(_pages.Count + missing));
                List<(int Index, MemoryOwner<byte> Owner)> added = new(missing);
                try
                {
                    for (int page = first; page <= last; page++)
                    {
                        if (_pages.ContainsKey(page)) continue;
                        MemoryOwner<byte> owner = Rent(PageSize);
                        added.Add((page, owner));
                    }
                }
                catch
                {
                    foreach (var item in added) item.Owner.Dispose();
                    throw;
                }
                foreach (var item in added) _pages.Add(item.Index, item.Owner);
            }

            // Reserve dirty metadata before modifying bytes as Add may grow a list.
            try
            {
                _dirty.Add(offset, data.Length);
            }
            catch
            {
                if (_uploadDepth == 0) TrimCleanPages();
                throw;
            }
            while (!data.IsEmpty)
            {
                int inPage = offset % PageSize;
                int count = Math.Min(PageSize - inPage, data.Length);
                data[..count].CopyTo(_pages[offset / PageSize].Span.Slice(inPage, count));
                offset += count;
                data = data[count..];
            }
        }

        internal bool Remove(int offset, int size)
        {
            Validate(offset, size);
            if (size == 0) return false;
            bool removed = _dirty.Remove(offset, size);
            if (removed && _uploadDepth == 0) TrimCleanPages();
            return removed;
        }

        internal void FillData(ReadOnlySpan<byte> baseData, int offset, Span<byte> destination)
        {
            Validate(offset, baseData.Length);
            if (destination.Length < baseData.Length) throw new ArgumentException("Short mirror destination.");
            baseData.CopyTo(destination);
            int end = offset + baseData.Length;
            int cursor = offset;
            while (cursor < end && _dirty.TryFindFirstOverlap(cursor, end - cursor, out var range))
            {
                int start = Math.Max(cursor, range.Offset);
                int rangeEnd = Math.Min(end, range.End);
                CopyPages(start, destination.Slice(start - offset, rangeEnd - start));
                cursor = rangeEnd;
            }
        }

        internal void Upload<T>(int offset, int size, ref T sink) where T : struct, IPendingBufferUpload
        {
            Validate(offset, size);
            if (!_dirty.TryFindFirstOverlap(offset, size, out _)) return;

            // Recombine sparse storage into bounded contiguous submissions. One large
            // dirty update must not turn into a native command for every 4-KiB page.
            // The separate owner also keeps the callback span stable if it reenters a
            // write/remove/upload of the source pages.
            using MemoryOwner<byte> batch = Rent(Math.Min(size, MaxUploadBatchSize));
            _uploadDepth++;
            try
            {
                int end = offset + size;
                int cursor = offset;
                while (cursor < end && _dirty.TryFindFirstOverlap(cursor, end - cursor, out var range))
                {
                    int start = Math.Max(cursor, range.Offset);
                    int count = Math.Min(MaxUploadBatchSize, Math.Min(end, range.End) - start);
                    CopyPages(start, batch.Span[..count]);

                    // Hide only this captured batch before calling the backend. Recheck
                    // the live dirty list afterward: a nested flush/direct write may
                    // already have consumed/replaced the remaining original ranges.
                    _dirty.Remove(start, count);
                    try
                    {
                        sink.Upload(start, batch.Span[..count]);
                    }
                    catch
                    {
                        _dirty.Add(start, count);
                        throw;
                    }
                    cursor = start + count;
                }
            }
            finally
            {
                if (--_uploadDepth == 0) TrimCleanPages();
            }
        }

        private void CopyPages(int offset, Span<byte> destination)
        {
            while (!destination.IsEmpty)
            {
                int inPage = offset % PageSize;
                int count = Math.Min(PageSize - inPage, destination.Length);
                _pages[offset / PageSize].Span.Slice(inPage, count).CopyTo(destination[..count]);
                offset += count;
                destination = destination[count..];
            }
        }

        private void TrimCleanPages()
        {
            // .NET 10 Dictionary.Remove does not invalidate its enumerator. There is
            // no concurrent writer, no per-page temporary key list and no GPU wait.
            foreach (var page in _pages)
            {
                int offset = page.Key * PageSize;
                if (!_dirty.OverlapsWith(offset, Math.Min(PageSize, _size - offset)))
                {
                    _pages.Remove(page.Key);
                    page.Value.Dispose();
                }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            if (_uploadDepth != 0) throw new InvalidOperationException("Cannot dispose an active pending upload.");
            _disposed = true;
            foreach (var page in _pages.Values) page.Dispose();
            _pages.Clear();
            _dirty.Clear();
        }
    }
}
