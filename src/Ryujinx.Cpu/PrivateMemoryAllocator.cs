using Ryujinx.Common;
using Ryujinx.Memory;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace Ryujinx.Cpu
{
    class PrivateMemoryAllocator : PrivateMemoryAllocatorImpl<PrivateMemoryAllocator.Block>
    {
        public const ulong InvalidOffset = ulong.MaxValue;

        private readonly Func<MemoryBlock, ulong, ulong, bool> _discardCallback;

        public class Block : IComparable<Block>
        {
            public MemoryBlock Memory { get; private set; }
            public ulong Size { get; }

            private readonly struct Range : IComparable<Range>
            {
                public ulong Offset { get; }
                public ulong Size { get; }

                public Range(ulong offset, ulong size)
                {
                    Offset = offset;
                    Size = size;
                }

                public int CompareTo(Range other)
                {
                    return Offset.CompareTo(other.Offset);
                }
            }

            private readonly List<Range> _freeRanges;
            private ulong _allocatedEnd;

            public Block(MemoryBlock memory, ulong size)
            {
                Memory = memory;
                Size = size;
                _freeRanges =
                [
                    new(0, size)
                ];
            }

            public ulong Allocate(ulong size, ulong alignment)
            {
                return Allocate(size, alignment, out _);
            }

            public ulong Allocate(ulong size, ulong alignment, out ulong reusedSize)
            {
                reusedSize = 0;

                for (int i = 0; i < _freeRanges.Count; i++)
                {
                    Range range = _freeRanges[i];

                    ulong alignedOffset = BitUtils.AlignUp(range.Offset, alignment);
                    ulong sizeDelta = alignedOffset - range.Offset;
                    ulong usableSize = range.Size - sizeDelta;

                    if (sizeDelta < range.Size && usableSize >= size)
                    {
                        _freeRanges.RemoveAt(i);

                        if (sizeDelta != 0)
                        {
                            InsertFreeRange(range.Offset, sizeDelta);
                        }

                        ulong endOffset = range.Offset + range.Size;
                        ulong remainingSize = endOffset - (alignedOffset + size);
                        if (remainingSize != 0)
                        {
                            InsertFreeRange(endOffset - remainingSize, remainingSize);
                        }

                        // Only the prefix below the previous high-water mark can contain old data.
                        // Keep the mark on free so a later, larger allocation clears its reused prefix.
                        reusedSize = alignedOffset < _allocatedEnd ? Math.Min(size, _allocatedEnd - alignedOffset) : 0;
                        _allocatedEnd = Math.Max(_allocatedEnd, alignedOffset + size);

                        return alignedOffset;
                    }
                }

                return InvalidOffset;
            }

            public void Free(ulong offset, ulong size)
            {
                InsertFreeRangeComingled(offset, size);
            }

            private void InsertFreeRange(ulong offset, ulong size)
            {
                Range range = new(offset, size);
                int index = _freeRanges.BinarySearch(range);
                if (index < 0)
                {
                    index = ~index;
                }

                _freeRanges.Insert(index, range);
            }

            private void InsertFreeRangeComingled(ulong offset, ulong size)
            {
                ulong endOffset = offset + size;
                Range range = new(offset, size);
                int index = _freeRanges.BinarySearch(range);
                if (index < 0)
                {
                    index = ~index;
                }

                if (index < _freeRanges.Count && _freeRanges[index].Offset == endOffset)
                {
                    endOffset = _freeRanges[index].Offset + _freeRanges[index].Size;
                    _freeRanges.RemoveAt(index);
                }

                if (index > 0 && _freeRanges[index - 1].Offset + _freeRanges[index - 1].Size == offset)
                {
                    offset = _freeRanges[index - 1].Offset;
                    _freeRanges.RemoveAt(--index);
                }

                range = new Range(offset, endOffset - offset);

                _freeRanges.Insert(index, range);
            }

            public bool IsTotallyFree()
            {
                if (_freeRanges.Count == 1 && _freeRanges[0].Size == Size)
                {
                    Debug.Assert(_freeRanges[0].Offset == 0);
                    return true;
                }

                return false;
            }

            public int CompareTo(Block other)
            {
                return Size.CompareTo(other.Size);
            }

            public virtual void Destroy()
            {
                Memory.Dispose();
            }
        }

        public PrivateMemoryAllocator(
            ulong blockAlignment,
            MemoryAllocationFlags allocationFlags,
            Func<MemoryBlock, ulong, ulong, bool> discardCallback = null) : base(blockAlignment, allocationFlags)
        {
            _discardCallback = discardCallback ??
                (static (memory, offset, size) => memory.TryDiscard(offset, size));
        }

        public PrivateMemoryAllocation Allocate(ulong size, ulong alignment, bool zeroFill = false)
        {
            Allocation allocation = Allocate(size, alignment, CreateBlock);

            // The never-allocated tail remains OS-zero and can stay physically uncommitted.
            // Clear only the prefix that may contain an earlier guest allocation.
            if (zeroFill && allocation.ReusedSize != 0)
            {
                allocation.Block.Memory.Fill(allocation.Offset, allocation.ReusedSize, 0);
            }

            return new PrivateMemoryAllocation(this, allocation.Block, allocation.Offset, allocation.Size);
        }

        private Block CreateBlock(MemoryBlock memory, ulong size)
        {
            return new Block(memory, size);
        }

        protected override void OnFree(Block block, ulong offset, ulong size, bool isTotallyFree)
        {
            if (!isTotallyFree)
            {
                _discardCallback(block.Memory, offset, size);
            }
        }
    }

    class PrivateMemoryAllocatorImpl<T> : IDisposable where T : PrivateMemoryAllocator.Block
    {
        private const ulong InvalidOffset = ulong.MaxValue;

        public readonly struct Allocation
        {
            public T Block { get; }
            public ulong Offset { get; }
            public ulong Size { get; }
            public ulong ReusedSize { get; }

            public Allocation(T block, ulong offset, ulong size, ulong reusedSize)
            {
                Block = block;
                Offset = offset;
                Size = size;
                ReusedSize = reusedSize;
            }
        }

        private static long _reservedBytes;
        private static long _allocatedBytes;
        private static long _blocksLive;
        private long _ownedBytes;

        // Logical ownership across allocators of this concrete block type; not resident RAM.
        internal static (long Reserved, long Allocated, long Blocks) GetProcessStatistics() =>
            (Interlocked.Read(ref _reservedBytes), Interlocked.Read(ref _allocatedBytes), Interlocked.Read(ref _blocksLive));

        private readonly List<T> _blocks;

        private readonly ulong _blockAlignment;
        private readonly MemoryAllocationFlags _allocationFlags;

        public PrivateMemoryAllocatorImpl(ulong blockAlignment, MemoryAllocationFlags allocationFlags)
        {
            _blocks = [];
            _blockAlignment = blockAlignment;
            _allocationFlags = allocationFlags;
        }

        protected Allocation Allocate(ulong size, ulong alignment, Func<MemoryBlock, ulong, T> createBlock)
        {
            // Ensure we have a sane alignment value.
            if ((ulong)(int)alignment != alignment || (int)alignment <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(alignment), $"Invalid alignment 0x{alignment:X}.");
            }

            for (int i = 0; i < _blocks.Count; i++)
            {
                T block = _blocks[i];

                if (block.Size >= size)
                {
                    ulong offset = block.Allocate(size, alignment, out ulong reusedSize);
                    if (offset != InvalidOffset)
                    {
                        Interlocked.Add(ref _allocatedBytes, (long)size);
                        _ownedBytes += (long)size;
                        return new Allocation(block, offset, size, reusedSize);
                    }
                }
            }

            ulong blockAlignedSize = BitUtils.AlignUp(size, _blockAlignment);

            MemoryBlock memory = new(blockAlignedSize, _allocationFlags);
            T newBlock = createBlock(memory, blockAlignedSize);

            InsertBlock(newBlock);
            Interlocked.Add(ref _reservedBytes, (long)blockAlignedSize);
            Interlocked.Increment(ref _blocksLive);

            ulong newBlockOffset = newBlock.Allocate(size, alignment, out ulong newBlockReusedSize);
            Debug.Assert(newBlockOffset != InvalidOffset);

            Interlocked.Add(ref _allocatedBytes, (long)size);
            _ownedBytes += (long)size;
            return new Allocation(newBlock, newBlockOffset, size, newBlockReusedSize);
        }

        public void Free(T block, ulong offset, ulong size)
        {
            block.Free(offset, size);
            Interlocked.Add(ref _allocatedBytes, -(long)size);
            _ownedBytes -= (long)size;

            bool isTotallyFree = block.IsTotallyFree();

            OnFree(block, offset, size, isTotallyFree);

            if (isTotallyFree)
            {
                for (int i = 0; i < _blocks.Count; i++)
                {
                    if (_blocks[i] == block)
                    {
                        _blocks.RemoveAt(i);
                        break;
                    }
                }

                block.Destroy();
                Interlocked.Add(ref _reservedBytes, -(long)block.Size);
                Interlocked.Decrement(ref _blocksLive);
            }
        }

        protected virtual void OnFree(T block, ulong offset, ulong size, bool isTotallyFree)
        {
        }

        private void InsertBlock(T block)
        {
            int index = _blocks.BinarySearch(block);
            if (index < 0)
            {
                index = ~index;
            }

            _blocks.Insert(index, block);
        }

        public void Dispose()
        {
            for (int i = 0; i < _blocks.Count; i++)
            {
                _blocks[i].Destroy();
                Interlocked.Add(ref _reservedBytes, -(long)_blocks[i].Size);
                Interlocked.Decrement(ref _blocksLive);
            }

            _blocks.Clear();
            Interlocked.Add(ref _allocatedBytes, -_ownedBytes);
            _ownedBytes = 0;
        }
    }
}
