using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
using Ryujinx.Memory.Range;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Ryujinx.Graphics.Gpu.Memory
{
    /// <summary>
    /// Buffer cache.
    /// </summary>
    class BufferCache : IDisposable
    {
        /// <summary>
        /// Initial size for the array holding overlaps.
        /// </summary>
        public const int OverlapsBufferInitialCapacity = 10;

        /// <summary>
        /// Maximum size that an array holding overlaps may have after trimming.
        /// </summary>
        public const int OverlapsBufferMaxCapacity = 10000;

        private const ulong BufferAlignmentSize = 0x1000;
        private const ulong BufferAlignmentMask = BufferAlignmentSize - 1;

        /// <summary>
        /// Alignment required for sparse buffer mappings.
        /// </summary>
        public const ulong SparseBufferAlignmentSize = 0x10000;

        private const ulong MaxDynamicGrowthSize = 0x100000;
        private const ulong MiB = 1024 * 1024;
        private const ulong DefaultMaxCachedBufferBytes = 2UL * 1024 * MiB;

        private readonly GpuContext _context;
        private readonly PhysicalMemory _physicalMemory;
        private readonly BufferCacheDiagnostics _diagnostics = new();

        internal void PublishDiagnosticSnapshot() => _diagnostics.Publish(CachedBytes, Capacity, EffectiveCapacity);
        internal string GetDiagnosticSnapshot() => _diagnostics.GetSnapshot();
        internal ReadOnlyMemory<byte> GetDiagnosticSnapshotUtf8() => _diagnostics.GetSnapshotUtf8();
        internal long DiagnosticPublishFailures => _diagnostics.PublishFailures;

        /// <remarks>
        /// Only modified from the GPU thread. Must lock for add/remove.
        /// Must lock for any access from other threads.
        /// </remarks>
        private readonly NonOverlappingRangeList<Buffer> _buffers;
        private readonly MultiRangeList<MultiRangeBuffer> _multiRangeBuffers;
        private readonly CacheEvictionPolicy<Buffer> _evictionPolicy;
        private readonly CacheEvictionPolicy<MultiRangeBuffer> _multiRangeEvictionPolicy;
        private readonly RecoverableMemoryPressureCapacity _memoryPressureCapacity;
        private bool _memoryBudgetConfigured;
        private bool _isAppleUnifiedMemory;

        internal ulong CachedBytes => _evictionPolicy.CachedBytes + _multiRangeEvictionPolicy.CachedBytes;
        internal ulong Capacity => _memoryPressureCapacity.ConfiguredCapacity;
        internal ulong EffectiveCapacity => _memoryPressureCapacity.EffectiveCapacity;

        private readonly Dictionary<ulong, BufferCacheEntry> _dirtyCache;
        private readonly Dictionary<ulong, BufferCacheEntry> _modifiedCache;
        private bool _pruneCaches;
        private int _virtualModifiedSequenceNumber;

        public event Action NotifyBuffersModified;

        /// <summary>
        /// Creates a new instance of the buffer manager.
        /// </summary>
        /// <param name="context">The GPU context that the buffer manager belongs to</param>
        /// <param name="physicalMemory">Physical memory where the cached buffers are mapped</param>
        public BufferCache(GpuContext context, PhysicalMemory physicalMemory)
        {
            _context = context;
            _physicalMemory = physicalMemory;

            _buffers = [];
            _multiRangeBuffers = [];
            _evictionPolicy = new(
                DefaultMaxCachedBufferBytes,
                static buffer => buffer.Size,
                static buffer => buffer.CacheNode,
                static (buffer, node) => buffer.CacheNode = node);
            _multiRangeEvictionPolicy = new(
                DefaultMaxCachedBufferBytes,
                static buffer => buffer.CacheSize,
                static buffer => buffer.CacheNode,
                static (buffer, node) => buffer.CacheNode = node);
            _memoryPressureCapacity = new(DefaultMaxCachedBufferBytes);

            _dirtyCache = new Dictionary<ulong, BufferCacheEntry>();

            // There are a lot more entries on the modified cache, so it is separate from the one for ForceDirty.
            _modifiedCache = new Dictionary<ulong, BufferCacheEntry>();
        }

        /// <summary>
        /// Configures the buffer cache memory budget without modifying resident entries.
        /// </summary>
        /// <param name="capacity">Maximum number of resident buffer bytes</param>
        /// <param name="isAppleUnifiedMemory">Whether the budget targets Apple unified memory</param>
        internal void ConfigureMemoryBudget(ulong capacity, bool isAppleUnifiedMemory)
        {
            if (_memoryBudgetConfigured &&
                Capacity == capacity &&
                _isAppleUnifiedMemory == isAppleUnifiedMemory)
            {
                return;
            }

            _memoryPressureCapacity.Configure(capacity);
            _evictionPolicy.Capacity = capacity;
            _memoryBudgetConfigured = true;
            _isAppleUnifiedMemory = isAppleUnifiedMemory;

            string memoryKind = isAppleUnifiedMemory ? " (Apple unified memory)" : string.Empty;
            Logger.Info?.Print(
                LogClass.Gpu,
                $"Buffer cache memory limit: configured={Capacity / MiB} MiB, effective={EffectiveCapacity / MiB} MiB{memoryKind}");
        }

        /// <summary>
        /// Lowers the cache ceiling after measured process pressure. Recovery requires a sustained
        /// series of healthy observations on the GPU thread.
        /// </summary>
        /// <param name="capacity">New maximum number of evictable cache bytes</param>
        /// <returns>True if the effective pressure ceiling changed</returns>
        internal bool LatchPressureCapacity(ulong capacity)
        {
            return _memoryPressureCapacity.Latch(capacity);
        }

        internal void ObserveMemoryHeadroom(ulong availableMemoryBytes, long nowMilliseconds)
        {
            if (_memoryPressureCapacity.ObserveHeadroom(availableMemoryBytes, nowMilliseconds))
            {
                Logger.Info?.Print(LogClass.Gpu,
                    $"Buffer cache recovered: effective={EffectiveCapacity / MiB} MiB, available={availableMemoryBytes / MiB} MiB, continuous_healthy_ms=20000.");
            }
        }

        private void AddBuffer(Buffer buffer, bool merged = false)
        {
            _buffers.Add(buffer);
            _diagnostics.Created(buffer.DiagnosticId, buffer.Address, buffer.Size, merged);
            buffer.MarkUsed(_context.SequenceNumber);
            _evictionPolicy.Add(buffer);

            TrimToCapacity();
        }

        private void RemoveBufferTracking(Buffer buffer)
        {
            if (buffer.CacheNode != null)
            {
                _diagnostics.Record(BufferCacheEvent.MergeRemoved, 0, buffer.DiagnosticId, buffer.Size);
                _evictionPolicy.Remove(buffer);
                buffer.SignalCacheRemoved();
                _pruneCaches = true;
            }
        }

        private void RemoveBuffers(Buffer[] buffers)
        {
            _buffers.RemoveRange(buffers[0], buffers[^1]);

            foreach (Buffer buffer in buffers)
            {
                RemoveBufferTracking(buffer);
            }
        }

        private void Touch(Buffer buffer)
        {
            if (buffer != null)
            {
                buffer.MarkUsed(_context.SequenceNumber);
                _evictionPolicy.Touch(buffer);
            }
        }

        private void AddMultiRangeBuffer(MultiRangeBuffer buffer)
        {
            _multiRangeBuffers.Add(buffer);
            _diagnostics.Record(BufferCacheEvent.Created, buffer.IsSparse ? 2 : 1, buffer.DiagnosticId, buffer.CacheSize);
            buffer.MarkUsed(_context.SequenceNumber);
            _multiRangeEvictionPolicy.Add(buffer);

            TrimToCapacity();
        }

        private void RemoveMultiRangeBuffer(MultiRangeBuffer buffer)
        {
            _diagnostics.Record(BufferCacheEvent.VirtualRebuildRemoved, buffer.IsSparse ? 2 : 1, buffer.DiagnosticId, buffer.CacheSize);
            _multiRangeBuffers.Remove(buffer);
            _multiRangeEvictionPolicy.Remove(buffer);
            buffer.Dispose();
        }

        private void Touch(MultiRangeBuffer buffer)
        {
            if (buffer != null)
            {
                buffer.MarkUsed(_context.SequenceNumber);
                _multiRangeEvictionPolicy.Touch(buffer);
            }
        }

        /// <summary>
        /// Evicts clean least-recently-used buffers until the effective memory budget is met.
        /// A recoverable pressure ceiling can make this stricter than the configured budget.
        /// Buffers used by the current GPU sequence and buffers with outstanding data ownership are retained.
        /// </summary>
        internal void TrimToCapacity()
        {
            TrimToCapacity(EffectiveCapacity);
        }

        /// <summary>
        /// Evicts clean least-recently-used buffers against a temporary target without changing the normal cache budget.
        /// </summary>
        /// <param name="capacity">Temporary target in bytes</param>
        internal void TrimToCapacity(ulong capacity)
        {
            ulong physicalBytes = _evictionPolicy.CachedBytes;
            ulong virtualBytes = _multiRangeEvictionPolicy.CachedBytes;
            bool overCapacity = physicalBytes > capacity || virtualBytes > capacity - physicalBytes;

            if (!overCapacity)
            {
                return;
            }

            TrimOverCapacity(capacity, physicalBytes);
        }

        // Keep the common, below-budget path out of the eviction closure/alias allocation path.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void TrimOverCapacity(ulong capacity, ulong physicalBytes)
        {
            bool pressure = capacity < Capacity;
            ulong virtualBytes;
            bool virtualBuffersEvicted = false;

            if (physicalBytes > capacity || _multiRangeEvictionPolicy.CachedBytes > capacity - physicalBytes)
            {
                ulong virtualCapacity = physicalBytes < capacity ? capacity - physicalBytes : 0;

                // Prefer the oldest virtual buffers that own duplicate non-sparse storage.
                virtualBuffersEvicted = _multiRangeEvictionPolicy.TrimTo(
                    virtualCapacity,
                    buffer => buffer.CanEvict(_context.SequenceNumber),
                    buffer =>
                    {
                        _multiRangeBuffers.Remove(buffer);
                        _diagnostics.Record(pressure ? BufferCacheEvent.PressureEvicted : BufferCacheEvent.CapacityEvicted,
                            buffer.IsSparse ? 2 : 1, buffer.DiagnosticId, buffer.CacheSize);
                        buffer.Dispose();
                    });
            }

            virtualBytes = _multiRangeEvictionPolicy.CachedBytes;
            ulong physicalCapacity = virtualBytes < capacity ? capacity - virtualBytes : 0;

            bool physicalBuffersEvicted = _evictionPolicy.TrimTo(
                physicalCapacity,
                buffer => buffer.CanEvict(_context.SequenceNumber),
                buffer =>
                {
                    _buffers.Remove(buffer);
                    _diagnostics.Evicted(buffer.DiagnosticId, buffer.Address, buffer.Size, pressure);
                    buffer.SignalCacheRemoved();
                    buffer.Dispose();
                });

            if (_evictionPolicy.CachedBytes > physicalCapacity)
            {
                // Sparse virtual buffers use no duplicate storage, but their aliases pin physical buffers.
                // Release only complete alias sets that actually unlock clean physical storage.
                HashSet<MultiRangeBuffer> aliasesToRelease = CacheDependencyEvictionPolicy.SelectAliasesToRelease<MultiRangeBuffer, Buffer>(
                    _multiRangeBuffers,
                    _evictionPolicy.OldestFirst,
                    _evictionPolicy.CachedBytes - physicalCapacity,
                    buffer => buffer.IsSparse && buffer.CanEvict(_context.SequenceNumber),
                    buffer => buffer.CacheDependencies,
                    buffer => buffer.CacheDependencyCount,
                    buffer => buffer.CanEvictAfterReleasingCacheDependencies(_context.SequenceNumber),
                    buffer => buffer.Size);

                virtualBuffersEvicted |= _multiRangeEvictionPolicy.EvictEligible(
                    aliasesToRelease.Contains,
                    buffer =>
                    {
                        _multiRangeBuffers.Remove(buffer);
                        _diagnostics.Record(pressure ? BufferCacheEvent.PressureEvicted : BufferCacheEvent.CapacityEvicted,
                            buffer.IsSparse ? 2 : 1, buffer.DiagnosticId, buffer.CacheSize);
                        buffer.Dispose();
                    });

                virtualBytes = _multiRangeEvictionPolicy.CachedBytes;
                physicalCapacity = virtualBytes < capacity ? capacity - virtualBytes : 0;
                physicalBuffersEvicted |= _evictionPolicy.TrimTo(
                    physicalCapacity,
                    buffer => buffer.CanEvict(_context.SequenceNumber),
                    buffer =>
                    {
                        _buffers.Remove(buffer);
                        _diagnostics.Evicted(buffer.DiagnosticId, buffer.Address, buffer.Size, pressure);
                        buffer.SignalCacheRemoved();
                        buffer.Dispose();
                    });
            }

            if (virtualBuffersEvicted || physicalBuffersEvicted)
            {
                _pruneCaches = true;
                NotifyBuffersModified?.Invoke();
            }
        }

        /// <summary>
        /// Handles removal of buffers written to a memory region being unmapped.
        /// </summary>
        /// <param name="sender">Sender object</param>
        /// <param name="e">Event arguments</param>
        public void MemoryUnmappedHandler(object sender, UnmapEventArgs e)
        {
            MultiRange range = ((MemoryManager)sender).GetPhysicalRegions(e.Address, e.Size);

            for (int index = 0; index < range.Count; index++)
            {
                MemoryRange subRange = range.GetSubRange(index);

                ReadOnlySpan<Buffer> overlaps = _buffers.FindOverlapsAsSpan(subRange.Address, subRange.Size);

                for (int i = 0; i < overlaps.Length; i++)
                {
                    overlaps[i].Unmapped(subRange.Address, subRange.Size);
                }
            }
        }

        /// <summary>
        /// Performs address translation of the GPU virtual address, and creates a
        /// new buffer, if needed, for the specified contiguous range.
        /// </summary>
        /// <param name="memoryManager">GPU memory manager where the buffer is mapped</param>
        /// <param name="gpuVa">Start GPU virtual address of the buffer</param>
        /// <param name="size">Size in bytes of the buffer</param>
        /// <param name="stage">The type of usage that created the buffer</param>
        /// <returns>Contiguous physical range of the buffer, after address translation</returns>
        public MultiRange TranslateAndCreateBuffer(MemoryManager memoryManager, ulong gpuVa, ulong size, BufferStage stage)
        {
            if (gpuVa == 0)
            {
                return new MultiRange(MemoryManager.PteUnmapped, size);
            }

            ulong address = memoryManager.Translate(gpuVa);

            if (address != MemoryManager.PteUnmapped)
            {
                CreateBuffer(address, size, stage);
            }

            return new MultiRange(address, size);
        }

        /// <summary>
        /// Performs address translation of the GPU virtual address, and creates
        /// new physical and virtual buffers, if needed, for the specified range.
        /// </summary>
        /// <param name="memoryManager">GPU memory manager where the buffer is mapped</param>
        /// <param name="gpuVa">Start GPU virtual address of the buffer</param>
        /// <param name="size">Size in bytes of the buffer</param>
        /// <param name="stage">The type of usage that created the buffer</param>
        /// <returns>Physical ranges of the buffer, after address translation</returns>
        public MultiRange TranslateAndCreateMultiBuffers(MemoryManager memoryManager, ulong gpuVa, ulong size, BufferStage stage)
        {
            if (gpuVa == 0 || size == 0)
            {
                return new MultiRange(MemoryManager.PteUnmapped, size);
            }

            memoryManager.VirtualRangeCache.TryGetOrAddRange(gpuVa, size, out MultiRange range);

            // A cached virtual translation does not guarantee that its physical host buffer is still resident.
            CreateBuffer(range, stage);

            return range;
        }

        /// <summary>
        /// Performs address translation of the GPU virtual address, and creates
        /// new physical buffers, if needed, for the specified range.
        /// </summary>
        /// <param name="memoryManager">GPU memory manager where the buffer is mapped</param>
        /// <param name="gpuVa">Start GPU virtual address of the buffer</param>
        /// <param name="size">Size in bytes of the buffer</param>
        /// <param name="stage">The type of usage that created the buffer</param>
        /// <returns>Physical ranges of the buffer, after address translation</returns>
        public MultiRange TranslateAndCreateMultiBuffersPhysicalOnly(MemoryManager memoryManager, ulong gpuVa, ulong size, BufferStage stage)
        {
            if (gpuVa == 0)
            {
                return new MultiRange(MemoryManager.PteUnmapped, size);
            }

            memoryManager.VirtualRangeCache.TryGetOrAddRange(gpuVa, size, out MultiRange range);

            // Virtual translations outlive LRU entries, so always restore physical residency as needed.
            for (int i = 0; i < range.Count; i++)
            {
                MemoryRange subRange = range.GetSubRange(i);

                if (subRange.Address != MemoryManager.PteUnmapped)
                {
                    if (range.Count > 1)
                    {
                        CreateBuffer(subRange.Address, subRange.Size, stage, SparseBufferAlignmentSize);
                    }
                    else
                    {
                        CreateBuffer(subRange.Address, subRange.Size, stage);
                    }
                }
            }

            return range;
        }

        /// <summary>
        /// Creates a new buffer for the specified range, if it does not yet exist.
        /// This can be used to ensure the existance of a buffer.
        /// </summary>
        /// <param name="range">Physical ranges of memory where the buffer data is located</param>
        /// <param name="stage">The type of usage that created the buffer</param>
        public void CreateBuffer(MultiRange range, BufferStage stage)
        {
            if (range.Count > 1)
            {
                CreateMultiRangeBuffer(range, stage);
            }
            else
            {
                MemoryRange subRange = range.GetSubRange(0);

                if (subRange.Address != MemoryManager.PteUnmapped)
                {
                    CreateBuffer(subRange.Address, subRange.Size, stage);
                }
            }
        }

        /// <summary>
        /// Creates a new buffer for the specified range, if it does not yet exist.
        /// This can be used to ensure the existance of a buffer.
        /// </summary>
        /// <param name="address">Address of the buffer in memory</param>
        /// <param name="size">Size of the buffer in bytes</param>
        /// <param name="stage">The type of usage that created the buffer</param>
        public void CreateBuffer(ulong address, ulong size, BufferStage stage)
        {
            ulong endAddress = address + size;

            ulong alignedAddress = address & ~BufferAlignmentMask;
            ulong alignedEndAddress = (endAddress + BufferAlignmentMask) & ~BufferAlignmentMask;

            // The buffer must have the size of at least one page.
            if (alignedEndAddress == alignedAddress)
            {
                alignedEndAddress += BufferAlignmentSize;
            }

            CreateBufferAligned(alignedAddress, alignedEndAddress - alignedAddress, stage);
        }

        /// <summary>
        /// Creates a new buffer for the specified range, if it does not yet exist.
        /// This can be used to ensure the existance of a buffer.
        /// </summary>
        /// <param name="address">Address of the buffer in memory</param>
        /// <param name="size">Size of the buffer in bytes</param>
        /// <param name="stage">The type of usage that created the buffer</param>
        /// <param name="alignment">Alignment of the start address of the buffer in bytes</param>
        public void CreateBuffer(ulong address, ulong size, BufferStage stage, ulong alignment)
        {
            ulong alignmentMask = alignment - 1;
            ulong pageAlignmentMask = BufferAlignmentMask;
            ulong endAddress = address + size;

            ulong alignedAddress = address & ~alignmentMask;
            ulong alignedEndAddress = (endAddress + pageAlignmentMask) & ~pageAlignmentMask;

            // The buffer must have the size of at least one page.
            if (alignedEndAddress == alignedAddress)
            {
                alignedEndAddress += pageAlignmentMask;
            }

            CreateBufferAligned(alignedAddress, alignedEndAddress - alignedAddress, stage, alignment);
        }

        /// <summary>
        /// Creates a buffer for a memory region composed of multiple physical ranges,
        /// if it does not exist yet.
        /// </summary>
        /// <param name="range">Physical ranges of memory</param>
        /// <param name="stage">The type of usage that created the buffer</param>
        private void CreateMultiRangeBuffer(MultiRange range, BufferStage stage)
        {
            // Ensure all non-contiguous buffer we might use are sparse aligned.
            for (int i = 0; i < range.Count; i++)
            {
                MemoryRange subRange = range.GetSubRange(i);

                if (subRange.Address != MemoryManager.PteUnmapped)
                {
                    CreateBuffer(subRange.Address, subRange.Size, stage, SparseBufferAlignmentSize);
                }
            }

            // Create sparse buffer.
            MultiRangeBuffer[] overlaps = new MultiRangeBuffer[10];

            int overlapCount = _multiRangeBuffers.FindOverlaps(range, ref overlaps);

            for (int index = 0; index < overlapCount; index++)
            {
                if (overlaps[index].Range.Contains(range))
                {
                    Touch(overlaps[index]);
                    return;
                }
            }

            for (int index = 0; index < overlapCount; index++)
            {
                if (range.Contains(overlaps[index].Range))
                {
                    RemoveMultiRangeBuffer(overlaps[index]);
                }
            }

            MultiRangeBuffer multiRangeBuffer;

            MemoryRange[] alignedSubRanges = new MemoryRange[range.Count];

            ulong alignmentMask = SparseBufferAlignmentSize - 1;

            if (_context.Capabilities.SupportsSparseBuffer)
            {
                BufferRange[] storages = new BufferRange[range.Count];

                for (int i = 0; i < range.Count; i++)
                {
                    MemoryRange subRange = range.GetSubRange(i);

                    if (subRange.Address != MemoryManager.PteUnmapped)
                    {
                        ulong endAddress = subRange.Address + subRange.Size;

                        ulong alignedAddress = subRange.Address & ~alignmentMask;
                        ulong alignedEndAddress = (endAddress + alignmentMask) & ~alignmentMask;
                        ulong alignedSize = alignedEndAddress - alignedAddress;

                        Buffer buffer = _buffers.FindOverlap(alignedAddress, alignedSize);
                        Touch(buffer);
                        BufferRange bufferRange = buffer.GetRange(alignedAddress, alignedSize, false);

                        alignedSubRanges[i] = new MemoryRange(alignedAddress, alignedSize);
                        storages[i] = bufferRange;
                    }
                    else
                    {
                        ulong alignedSize = (subRange.Size + alignmentMask) & ~alignmentMask;

                        alignedSubRanges[i] = new MemoryRange(MemoryManager.PteUnmapped, alignedSize);
                        storages[i] = new BufferRange(BufferHandle.Null, 0, (int)alignedSize);
                    }
                }

                multiRangeBuffer = new(_context, new MultiRange(alignedSubRanges), storages);

                for (int i = 0; i < range.Count; i++)
                {
                    MemoryRange subRange = range.GetSubRange(i);

                    if (subRange.Address != MemoryManager.PteUnmapped)
                    {
                        ulong alignedAddress = subRange.Address & ~alignmentMask;
                        ulong alignedEndAddress = (subRange.Address + subRange.Size + alignmentMask) & ~alignmentMask;
                        Buffer buffer = _buffers.FindOverlap(alignedAddress, alignedEndAddress - alignedAddress);

                        multiRangeBuffer.AddCacheDependency(buffer);
                    }
                }
            }
            else
            {
                for (int i = 0; i < range.Count; i++)
                {
                    MemoryRange subRange = range.GetSubRange(i);

                    if (subRange.Address != MemoryManager.PteUnmapped)
                    {
                        ulong endAddress = subRange.Address + subRange.Size;

                        ulong alignedAddress = subRange.Address & ~alignmentMask;
                        ulong alignedEndAddress = (endAddress + alignmentMask) & ~alignmentMask;
                        ulong alignedSize = alignedEndAddress - alignedAddress;

                        alignedSubRanges[i] = new MemoryRange(alignedAddress, alignedSize);
                    }
                    else
                    {
                        ulong alignedSize = (subRange.Size + alignmentMask) & ~alignmentMask;

                        alignedSubRanges[i] = new MemoryRange(MemoryManager.PteUnmapped, alignedSize);
                    }
                }

                multiRangeBuffer = new(_context, new MultiRange(alignedSubRanges));

                UpdateVirtualBufferDependencies(multiRangeBuffer);
            }

            AddMultiRangeBuffer(multiRangeBuffer);
        }

        /// <summary>
        /// Adds two-way dependencies to all physical buffers contained within a given virtual buffer.
        /// </summary>
        /// <param name="virtualBuffer">Virtual buffer to have dependencies added</param>
        private void UpdateVirtualBufferDependencies(MultiRangeBuffer virtualBuffer)
        {
            virtualBuffer.ClearPhysicalDependencies();

            ulong dstOffset = 0;

            HashSet<Buffer> physicalBuffers = [];

            for (int i = 0; i < virtualBuffer.Range.Count; i++)
            {
                MemoryRange subRange = virtualBuffer.Range.GetSubRange(i);

                if (subRange.Address != MemoryManager.PteUnmapped)
                {
                    Buffer buffer = _buffers.FindOverlap(subRange.Address, subRange.Size);
                    Touch(buffer);

                    virtualBuffer.AddPhysicalDependency(buffer, subRange.Address, dstOffset, subRange.Size);
                    physicalBuffers.Add(buffer);
                }

                dstOffset += subRange.Size;
            }

            foreach (Buffer buffer in physicalBuffers)
            {
                buffer.CopyToDependantVirtualBuffer(virtualBuffer);
            }
        }

        /// <summary>
        /// Performs address translation of the GPU virtual address, and attempts to force
        /// the buffer in the region as dirty.
        /// The buffer lookup for this function is cached in a dictionary for quick access, which
        /// accelerates common UBO updates.
        /// </summary>
        /// <param name="memoryManager">GPU memory manager where the buffer is mapped</param>
        /// <param name="gpuVa">Start GPU virtual address of the buffer</param>
        /// <param name="size">Size in bytes of the buffer</param>
        public void ForceDirty(MemoryManager memoryManager, ulong gpuVa, ulong size)
        {
            if (_pruneCaches)
            {
                Prune();
            }

            if (!_dirtyCache.TryGetValue(gpuVa, out BufferCacheEntry result) ||
                result.EndGpuAddress < gpuVa + size ||
                result.UnmappedSequence != result.Buffer.UnmappedSequence)
            {
                MultiRange range = TranslateAndCreateBuffer(memoryManager, gpuVa, size, BufferStage.Internal);
                ulong address = range.GetSubRange(0).Address;
                result = new BufferCacheEntry(address, gpuVa, GetBuffer(address, size, BufferStage.Internal));

                _dirtyCache[gpuVa] = result;
            }

            Touch(result.Buffer);
            result.Buffer.ForceDirty(result.Address, size);
        }

        /// <summary>
        /// Checks if the given buffer range has been GPU modifed.
        /// </summary>
        /// <param name="memoryManager">GPU memory manager where the buffer is mapped</param>
        /// <param name="gpuVa">Start GPU virtual address of the buffer</param>
        /// <param name="size">Size in bytes of the buffer</param>
        /// <returns>True if modified, false otherwise</returns>
        public bool CheckModified(MemoryManager memoryManager, ulong gpuVa, ulong size, out ulong outAddr)
        {
            if (_pruneCaches)
            {
                Prune();
            }

            // Align the address to avoid creating too many entries on the quick lookup dictionary.
            ulong mask = BufferAlignmentMask;
            ulong alignedGpuVa = gpuVa & (~mask);
            ulong alignedEndGpuVa = (gpuVa + size + mask) & (~mask);

            size = alignedEndGpuVa - alignedGpuVa;

            if (!_modifiedCache.TryGetValue(alignedGpuVa, out BufferCacheEntry result) ||
                result.EndGpuAddress < alignedEndGpuVa ||
                result.UnmappedSequence != result.Buffer.UnmappedSequence)
            {
                MultiRange range = TranslateAndCreateBuffer(memoryManager, alignedGpuVa, size, BufferStage.None);
                ulong address = range.GetSubRange(0).Address;
                result = new BufferCacheEntry(address, alignedGpuVa, GetBuffer(address, size, BufferStage.None));

                _modifiedCache[alignedGpuVa] = result;
            }

            outAddr = result.Address | (gpuVa & mask);

            Touch(result.Buffer);

            return result.Buffer.IsModified(result.Address, size);
        }

        /// <summary>
        /// Creates a new buffer for the specified range, if needed.
        /// If a buffer where this range can be fully contained already exists,
        /// then the creation of a new buffer is not necessary.
        /// </summary>
        /// <param name="address">Address of the buffer in guest memory</param>
        /// <param name="size">Size in bytes of the buffer</param>
        /// <param name="stage">The type of usage that created the buffer</param>
        private void CreateBufferAligned(ulong address, ulong size, BufferStage stage)
        {
            ReadOnlySpan<Buffer> overlaps = _buffers.FindOverlapsAsSpan(address, size);

            if (overlaps.Length != 0)
            {
                // The buffer already exists. We can just return the existing buffer
                // if the buffer we need is fully contained inside the overlapping buffer.
                // Otherwise, we must delete the overlapping buffers and create a bigger buffer
                // that fits all the data we need. We also need to copy the contents from the
                // old buffer(s) to the new buffer.

                ulong endAddress = address + size;

                if (overlaps[0].Address > address || overlaps[0].EndAddress < endAddress)
                {
                    bool anySparseCompatible = false;

                    // Check if the following conditions are met:
                    // - We have a single overlap.
                    // - The overlap starts at or before the requested range. That is, the overlap happens at the end.
                    // - The size delta between the new, merged buffer and the old one is of at most 2 pages.
                    // In this case, we attempt to extend the buffer further than the requested range,
                    // this can potentially avoid future resizes if the application keeps using overlapping
                    // sequential memory.
                    // Allowing for 2 pages (rather than just one) is necessary to catch cases where the
                    // range crosses a page, and after alignment, ends having a size of 2 pages.
                    if (overlaps.Length == 1 &&
                        address >= overlaps[0].Address &&
                        endAddress - overlaps[0].EndAddress <= BufferAlignmentSize * 2)
                    {
                        // Try to grow the buffer by 1.5x of its current size.
                        // This improves performance in the cases where the buffer is resized often by small amounts.
                        ulong existingSize = overlaps[0].Size;
                        ulong growthBytes = Math.Min(existingSize >> 1, MaxDynamicGrowthSize) & ~BufferAlignmentMask;

                        // Anchor the extension to existing storage. Using the request's tail address
                        // here adds the existing prefix a second time and can absorb unrelated neighbours.
                        // Speculative growth may be skipped at the address limit; the requested end remains valid.
                        if (overlaps[0].EndAddress <= ulong.MaxValue - growthBytes)
                        {
                            endAddress = Math.Max(endAddress, overlaps[0].EndAddress + growthBytes);
                        }

                        size = endAddress - address;
                        overlaps = _buffers.FindOverlapsAsSpan(address, size);
                    }

                    address = Math.Min(address, overlaps[0].Address);
                    endAddress = Math.Max(endAddress, overlaps[^1].EndAddress);

                    for (int i = 0; i < overlaps.Length; i++)
                    {
                        anySparseCompatible |= overlaps[i].SparseCompatible;
                    }

                    Buffer[] overlapsArray = overlaps.ToArray();

                    RemoveBuffers(overlapsArray);

                    ulong newSize = endAddress - address;

                    _diagnostics.Lookup(false);
                    AddBuffer(CreateBufferAligned(address, newSize, stage, anySparseCompatible, overlapsArray), merged: true);
                }
                else
                {
                    _diagnostics.Lookup(true);
                    Touch(overlaps[0]);
                }
            }
            else
            {
                // No overlap, just create a new buffer.
                _diagnostics.Lookup(false);
                AddBuffer(new(_context, _physicalMemory, address, size, stage, sparseCompatible: false, []));
            }
        }

        /// <summary>
        /// Creates a new buffer for the specified range, if needed.
        /// If a buffer where this range can be fully contained already exists,
        /// then the creation of a new buffer is not necessary.
        /// </summary>
        /// <param name="address">Address of the buffer in guest memory</param>
        /// <param name="size">Size in bytes of the buffer</param>
        /// <param name="stage">The type of usage that created the buffer</param>
        /// <param name="alignment">Alignment of the start address of the buffer</param>
        private void CreateBufferAligned(ulong address, ulong size, BufferStage stage, ulong alignment)
        {
            bool sparseAligned = alignment >= SparseBufferAlignmentSize;

            ReadOnlySpan<Buffer> overlaps = _buffers.FindOverlapsAsSpan(address, size);

            if (overlaps.Length != 0)
            {
                // If the buffer already exists, make sure if covers the entire range,
                // and make sure it is properly aligned, otherwise sparse mapping may fail.

                ulong endAddress = address + size;

                if (overlaps[0].Address > address ||
                    overlaps[0].EndAddress < endAddress ||
                    (overlaps[0].Address & (alignment - 1)) != 0 ||
                    (!overlaps[0].SparseCompatible && sparseAligned))
                {
                    // We need to make sure the new buffer is properly aligned.
                    // However, after the range is aligned, it is possible that it
                    // overlaps more buffers, so try again after each extension
                    // and ensure we cover all overlaps.

                    endAddress = Math.Max(endAddress, overlaps[^1].EndAddress);
                    int oldOverlapCount;

                    do
                    {
                        address = Math.Min(address, overlaps[0].Address);
                        endAddress = Math.Max(endAddress, overlaps[^1].EndAddress);

                        address &= ~(alignment - 1);

                        oldOverlapCount = overlaps.Length;
                        overlaps = _buffers.FindOverlapsAsSpan(address, endAddress - address);
                    }
                    while (oldOverlapCount != overlaps.Length);

                    ulong newSize = endAddress - address;

                    Buffer[] overlapsArray = overlaps.ToArray();

                    RemoveBuffers(overlapsArray);

                    _diagnostics.Lookup(false);
                    AddBuffer(CreateBufferAligned(address, newSize, stage, sparseAligned, overlapsArray), merged: true);
                }
                else
                {
                    _diagnostics.Lookup(true);
                    Touch(overlaps[0]);
                }
            }
            else
            {
                // No overlap, just create a new buffer.
                _diagnostics.Lookup(false);
                AddBuffer(new(_context, _physicalMemory, address, size, stage, sparseAligned, []));
            }
        }

        /// <summary>
        /// Creates a new buffer for the specified range, if needed.
        /// If a buffer where this range can be fully contained already exists,
        /// then the creation of a new buffer is not necessary.
        /// </summary>
        /// <param name="address">Address of the buffer in guest memory</param>
        /// <param name="size">Size in bytes of the buffer</param>
        /// <param name="stage">The type of usage that created the buffer</param>
        /// <param name="sparseCompatible">Indicates if the buffer can be used in a sparse buffer mapping</param>
        /// <param name="overlaps">Buffers overlapping the range</param>
        private Buffer CreateBufferAligned(ulong address, ulong size, BufferStage stage, bool sparseCompatible, Buffer[] overlaps)
        {
            Buffer newBuffer = new(_context, _physicalMemory, address, size, stage, sparseCompatible, overlaps);

            for (int index = 0; index < overlaps.Length; index++)
            {
                Buffer buffer = overlaps[index];

                int dstOffset = (int)(buffer.Address - newBuffer.Address);

                buffer.CopyTo(newBuffer, dstOffset);
                newBuffer.InheritModifiedRanges(buffer);

                buffer.DecrementReferenceCount();
            }

            newBuffer.SynchronizeMemory(address, size);

            // Existing buffers were modified, we need to rebind everything.
            NotifyBuffersModified?.Invoke();

            RecreateMultiRangeBuffers(address, size);

            return newBuffer;
        }

        /// <summary>
        /// Recreates all the multi-range buffers that overlaps a given physical memory range.
        /// </summary>
        /// <param name="address">Start address of the range</param>
        /// <param name="size">Size of the range in bytes</param>
        private void RecreateMultiRangeBuffers(ulong address, ulong size)
        {
            if ((address & (SparseBufferAlignmentSize - 1)) != 0 || (size & (SparseBufferAlignmentSize - 1)) != 0)
            {
                return;
            }

            MultiRangeBuffer[] overlaps = new MultiRangeBuffer[10];

            int overlapCount = _multiRangeBuffers.FindOverlaps(address, size, ref overlaps);

            for (int index = 0; index < overlapCount; index++)
            {
                RemoveMultiRangeBuffer(overlaps[index]);
            }

            for (int index = 0; index < overlapCount; index++)
            {
                CreateMultiRangeBuffer(overlaps[index].Range, BufferStage.None);
            }
        }

        /// <summary>
        /// Copy a buffer data from a given address to another.
        /// </summary>
        /// <remarks>
        /// This does a GPU side copy.
        /// </remarks>
        /// <param name="memoryManager">GPU memory manager where the buffer is mapped</param>
        /// <param name="srcVa">GPU virtual address of the copy source</param>
        /// <param name="dstVa">GPU virtual address of the copy destination</param>
        /// <param name="size">Size in bytes of the copy</param>
        public void CopyBuffer(MemoryManager memoryManager, ulong srcVa, ulong dstVa, ulong size)
        {
            MultiRange srcRange = TranslateAndCreateMultiBuffersPhysicalOnly(memoryManager, srcVa, size, BufferStage.Copy);
            MultiRange dstRange = TranslateAndCreateMultiBuffersPhysicalOnly(memoryManager, dstVa, size, BufferStage.Copy);

            if (srcRange.Count == 1 && dstRange.Count == 1)
            {
                CopyBufferSingleRange(memoryManager, srcRange.GetSubRange(0).Address, dstRange.GetSubRange(0).Address, size);
            }
            else
            {
                ulong copiedSize = 0;
                ulong srcOffset = 0;
                ulong dstOffset = 0;
                int srcRangeIndex = 0;
                int dstRangeIndex = 0;

                while (copiedSize < size)
                {
                    if (srcRange.GetSubRange(srcRangeIndex).Size == srcOffset)
                    {
                        srcRangeIndex++;
                        srcOffset = 0;
                    }

                    if (dstRange.GetSubRange(dstRangeIndex).Size == dstOffset)
                    {
                        dstRangeIndex++;
                        dstOffset = 0;
                    }

                    MemoryRange srcSubRange = srcRange.GetSubRange(srcRangeIndex);
                    MemoryRange dstSubRange = dstRange.GetSubRange(dstRangeIndex);

                    ulong srcSize = srcSubRange.Size - srcOffset;
                    ulong dstSize = dstSubRange.Size - dstOffset;
                    ulong copySize = Math.Min(srcSize, dstSize);

                    if (TryGetCopyChunkAddresses(srcSubRange, srcOffset, dstSubRange, dstOffset, out ulong srcAddress, out ulong dstAddress))
                    {
                        CopyBufferSingleRange(memoryManager, srcAddress, dstAddress, copySize);
                    }

                    srcOffset += copySize;
                    dstOffset += copySize;
                    copiedSize += copySize;
                }
            }
        }

        /// <summary>
        /// Computes mapped addresses for one multi-range copy chunk without applying offsets to the unmapped sentinel.
        /// </summary>
        internal static bool TryGetCopyChunkAddresses(
            MemoryRange srcRange,
            ulong srcOffset,
            MemoryRange dstRange,
            ulong dstOffset,
            out ulong srcAddress,
            out ulong dstAddress)
        {
            srcAddress = MemoryManager.PteUnmapped;
            dstAddress = MemoryManager.PteUnmapped;

            if (srcRange.Address == MemoryManager.PteUnmapped ||
                dstRange.Address == MemoryManager.PteUnmapped ||
                srcOffset > ulong.MaxValue - srcRange.Address ||
                dstOffset > ulong.MaxValue - dstRange.Address)
            {
                return false;
            }

            srcAddress = srcRange.Address + srcOffset;
            dstAddress = dstRange.Address + dstOffset;

            return true;
        }

        /// <summary>
        /// Copy a buffer data from a given address to another.
        /// </summary>
        /// <remarks>
        /// This does a GPU side copy.
        /// </remarks>
        /// <param name="memoryManager">GPU memory manager where the buffer is mapped</param>
        /// <param name="srcAddress">Physical address of the copy source</param>
        /// <param name="dstAddress">Physical address of the copy destination</param>
        /// <param name="size">Size in bytes of the copy</param>
        private void CopyBufferSingleRange(MemoryManager memoryManager, ulong srcAddress, ulong dstAddress, ulong size)
        {
            if (srcAddress == MemoryManager.PteUnmapped || dstAddress == MemoryManager.PteUnmapped)
            {
                return;
            }

            Buffer srcBuffer = GetBuffer(srcAddress, size, BufferStage.Copy);
            Buffer dstBuffer = GetBuffer(dstAddress, size, BufferStage.Copy);

            int srcOffset = (int)(srcAddress - srcBuffer.Address);
            int dstOffset = (int)(dstAddress - dstBuffer.Address);

            _context.Renderer.Pipeline.CopyBuffer(
                srcBuffer.Handle,
                dstBuffer.Handle,
                srcOffset,
                dstOffset,
                (int)size);

            if (srcBuffer.IsModified(srcAddress, size))
            {
                dstBuffer.SignalModified(dstAddress, size, BufferStage.Copy);
            }
            else
            {
                // Optimization: If the data being copied is already in memory, then copy it directly instead of flushing from GPU.

                dstBuffer.ClearModified(dstAddress, size);
                memoryManager.Physical.WriteTrackedResource(dstAddress, memoryManager.Physical.GetSpan(srcAddress, (int)size), ResourceKind.Buffer);
            }

            dstBuffer.CopyToDependantVirtualBuffers(dstAddress, size);
        }

        /// <summary>
        /// Clears a buffer at a given address with the specified value.
        /// </summary>
        /// <remarks>
        /// Both the address and size must be aligned to 4 bytes.
        /// </remarks>
        /// <param name="memoryManager">GPU memory manager where the buffer is mapped</param>
        /// <param name="gpuVa">GPU virtual address of the region to clear</param>
        /// <param name="size">Number of bytes to clear</param>
        /// <param name="value">Value to be written into the buffer</param>
        public void ClearBuffer(MemoryManager memoryManager, ulong gpuVa, ulong size, uint value)
        {
            MultiRange range = TranslateAndCreateMultiBuffersPhysicalOnly(memoryManager, gpuVa, size, BufferStage.Copy);

            for (int index = 0; index < range.Count; index++)
            {
                MemoryRange subRange = range.GetSubRange(index);

                if (subRange.Address == MemoryManager.PteUnmapped)
                {
                    continue;
                }

                Buffer buffer = GetBuffer(subRange.Address, subRange.Size, BufferStage.Copy);

                int offset = (int)(subRange.Address - buffer.Address);

                _context.Renderer.Pipeline.ClearBuffer(buffer.Handle, offset, (int)subRange.Size, value);

                memoryManager.Physical.FillTrackedResource(subRange.Address, subRange.Size, value, ResourceKind.Buffer);

                buffer.CopyToDependantVirtualBuffers(subRange.Address, subRange.Size);
            }
        }

        /// <summary>
        /// Gets a buffer sub-range starting at a given memory address, aligned to the next page boundary.
        /// </summary>
        /// <param name="range">Physical regions of memory where the buffer is mapped</param>
        /// <param name="stage">Buffer stage that triggered the access</param>
        /// <param name="write">Whether the buffer will be written to by this use</param>
        /// <returns>The buffer sub-range starting at the given memory address</returns>
        public BufferRange GetBufferRangeAligned(MultiRange range, BufferStage stage, bool write = false)
        {
            if (range.IsUnmapped)
            {
                return BufferRange.Empty;
            }

            CreateBuffer(range, stage);

            if (range.Count > 1)
            {
                return GetBuffer(range, stage, write).GetRange(range);
            }
            else
            {
                MemoryRange subRange = range.GetSubRange(0);
                return GetBuffer(subRange.Address, subRange.Size, stage, write).GetRangeAligned(subRange.Address, subRange.Size, write);
            }
        }

        /// <summary>
        /// Gets a buffer sub-range for a given memory range.
        /// </summary>
        /// <param name="range">Physical regions of memory where the buffer is mapped</param>
        /// <param name="stage">Buffer stage that triggered the access</param>
        /// <param name="write">Whether the buffer will be written to by this use</param>
        /// <returns>The buffer sub-range for the given range</returns>
        public BufferRange GetBufferRange(MultiRange range, BufferStage stage, bool write = false)
        {
            if (range.IsUnmapped)
            {
                return BufferRange.Empty;
            }

            CreateBuffer(range, stage);

            if (range.Count > 1)
            {
                return GetBuffer(range, stage, write).GetRange(range);
            }
            else
            {
                MemoryRange subRange = range.GetSubRange(0);
                return GetBuffer(subRange.Address, subRange.Size, stage, write).GetRange(subRange.Address, subRange.Size, write);
            }
        }

        /// <summary>
        /// Gets a buffer for a given memory range.
        /// A buffer overlapping with the specified range is assumed to already exist on the cache.
        /// </summary>
        /// <param name="range">Physical regions of memory where the buffer is mapped</param>
        /// <param name="stage">Buffer stage that triggered the access</param>
        /// <param name="write">Whether the buffer will be written to by this use</param>
        /// <returns>The buffer where the range is fully contained</returns>
        private MultiRangeBuffer GetBuffer(MultiRange range, BufferStage stage, bool write = false)
        {
            for (int i = 0; i < range.Count; i++)
            {
                MemoryRange subRange = range.GetSubRange(i);

                if (subRange.Address == MemoryManager.PteUnmapped)
                {
                    continue;
                }

                Buffer subBuffer = _buffers.FindOverlap(subRange.Address, subRange.Size);
                Touch(subBuffer);

                subBuffer.SynchronizeMemory(subRange.Address, subRange.Size);

                if (write)
                {
                    subBuffer.SignalModified(subRange.Address, subRange.Size, stage);
                }
            }

            MultiRangeBuffer[] overlaps = new MultiRangeBuffer[10];

            int overlapCount = _multiRangeBuffers.FindOverlaps(range, ref overlaps);

            MultiRangeBuffer buffer = null;

            for (int i = 0; i < overlapCount; i++)
            {
                if (overlaps[i].Range.Contains(range))
                {
                    buffer = overlaps[i];
                    break;
                }
            }

            if (write && buffer != null && !_context.Capabilities.SupportsSparseBuffer)
            {
                buffer.AddModifiedRegion(range, ++_virtualModifiedSequenceNumber);
            }

            Touch(buffer);

            return buffer;
        }

        /// <summary>
        /// Gets a buffer for a given memory range.
        /// A buffer overlapping with the specified range is assumed to already exist on the cache.
        /// </summary>
        /// <param name="address">Start address of the memory range</param>
        /// <param name="size">Size in bytes of the memory range</param>
        /// <param name="stage">Buffer stage that triggered the access</param>
        /// <param name="write">Whether the buffer will be written to by this use</param>
        /// <returns>The buffer where the range is fully contained</returns>
        private Buffer GetBuffer(ulong address, ulong size, BufferStage stage, bool write = false)
        {
            Buffer buffer;

            if (size != 0)
            {
                buffer = _buffers.FindOverlap(address, size);

                buffer.CopyFromDependantVirtualBuffers();
                buffer.SynchronizeMemory(address, size);

                if (write)
                {
                    buffer.SignalModified(address, size, stage);
                }
            }
            else
            {
                buffer = _buffers.FindOverlapFast(address, 1);
            }

            Touch(buffer);

            return buffer;
        }

        /// <summary>
        /// Performs guest to host memory synchronization of a given memory range.
        /// </summary>
        /// <param name="range">Physical regions of memory where the buffer is mapped</param>
        public void SynchronizeBufferRange(MultiRange range)
        {
            if (range.IsUnmapped)
            {
                return;
            }

            CreateBuffer(range, BufferStage.None);

            if (range.Count == 1)
            {
                MemoryRange subRange = range.GetSubRange(0);
                SynchronizeBufferRange(subRange.Address, subRange.Size, copyBackVirtual: true);
            }
            else
            {
                for (int index = 0; index < range.Count; index++)
                {
                    MemoryRange subRange = range.GetSubRange(index);

                    if (subRange.Address != MemoryManager.PteUnmapped)
                    {
                        SynchronizeBufferRange(subRange.Address, subRange.Size, copyBackVirtual: false);
                    }
                }
            }
        }

        /// <summary>
        /// Performs guest to host memory synchronization of a given memory range.
        /// </summary>
        /// <param name="address">Start address of the memory range</param>
        /// <param name="size">Size in bytes of the memory range</param>
        /// <param name="copyBackVirtual">Whether virtual buffers that uses this buffer as backing memory should have its data copied back if modified</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void SynchronizeBufferRange(ulong address, ulong size, bool copyBackVirtual)
        {
            if (size != 0)
            {
                Buffer buffer = _buffers.FindOverlap(address, size);
                Touch(buffer);

                if (copyBackVirtual)
                {
                    buffer.CopyFromDependantVirtualBuffers();
                }

                buffer.SynchronizeMemory(address, size);
            }
        }

        /// <summary>
        /// Signal that the given buffer's handle has changed,
        /// forcing rebind and any overlapping multi-range buffers to be recreated.
        /// </summary>
        /// <param name="buffer">The buffer that has changed handle</param>
        public void BufferBackingChanged(Buffer buffer)
        {
            _diagnostics.Record(buffer.BackingState.IsDeviceLocal ? BufferCacheEvent.BackingToDevice : BufferCacheEvent.BackingToHost,
                0, buffer.DiagnosticId, buffer.Size);
            Touch(buffer);
            NotifyBuffersModified?.Invoke();

            RecreateMultiRangeBuffers(buffer.Address, buffer.Size);
        }

        /// <summary>
        /// Prune any invalid entries from a quick access dictionary.
        /// </summary>
        /// <param name="dictionary">Dictionary to prune</param>
        /// <param name="toDelete">List used to track entries to delete</param>
        private static void Prune(Dictionary<ulong, BufferCacheEntry> dictionary, ref List<ulong> toDelete)
        {
            foreach (KeyValuePair<ulong, BufferCacheEntry> entry in dictionary)
            {
                if (entry.Value.UnmappedSequence != entry.Value.Buffer.UnmappedSequence)
                {
                    (toDelete ??= []).Add(entry.Key);
                }
            }

            if (toDelete != null)
            {
                foreach (ulong entry in toDelete)
                {
                    dictionary.Remove(entry);
                }
            }
        }

        /// <summary>
        /// Prune any invalid entries from the quick access dictionaries.
        /// </summary>
        private void Prune()
        {
            List<ulong> toDelete = null;

            Prune(_dirtyCache, ref toDelete);

            toDelete?.Clear();

            Prune(_modifiedCache, ref toDelete);

            _pruneCaches = false;
        }

        /// <summary>
        /// Queues a prune of invalid entries the next time a dictionary cache is accessed.
        /// </summary>
        public void QueuePrune()
        {
            _pruneCaches = true;
        }

        /// <summary>
        /// Disposes all buffers in the cache.
        /// It's an error to use the buffer cache after disposal.
        /// </summary>
        public void Dispose()
        {
            lock (_buffers)
            {
                foreach (MultiRangeBuffer buffer in _multiRangeBuffers)
                {
                    _diagnostics.Record(BufferCacheEvent.ShutdownRemoved, buffer.IsSparse ? 2 : 1, buffer.DiagnosticId, buffer.CacheSize);
                    buffer.Dispose();
                }

                foreach (Buffer buffer in _buffers)
                {
                    _diagnostics.Record(BufferCacheEvent.ShutdownRemoved, 0, buffer.DiagnosticId, buffer.Size);
                    buffer.Dispose();
                }

                _evictionPolicy.Clear();
                _multiRangeEvictionPolicy.Clear();
            }
        }
    }
}
