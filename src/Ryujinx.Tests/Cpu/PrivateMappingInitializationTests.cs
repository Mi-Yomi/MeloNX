using NUnit.Framework;
using Ryujinx.Cpu.Jit;
using Ryujinx.Memory;
using System.Collections.Generic;
using System.Linq;

namespace Ryujinx.Tests.Cpu
{
    class PrivateMappingInitializationTests
    {
        private const ulong GuestAddress = 0x20000000;
        private const ulong GuestPageSize = 0x1000;

        [TestCase(0x1000UL)]
        [TestCase(0x4000UL)]
        public void ReallocatedHeapPagesAreZeroAndLiveNeighboursArePreserved(ulong releasedSize)
        {
            using MemoryBlock backing = new(0x20000, MemoryAllocationFlags.Reserve);
            MemoryManagerHostTracked memory = new(backing, 1UL << 32, false, null);
            memory.IncrementReferenceCount();

            try
            {
                ulong mappedSize = releasedSize + 0x4000;
                MapFreshHeap(memory, GuestAddress, mappedSize);
                memory.Write(GuestAddress, Enumerable.Repeat((byte)0xa5, (int)releasedSize).ToArray());
                memory.Write(GuestAddress + releasedSize, Enumerable.Repeat((byte)0x67, 0x4000).ToArray());

                memory.Unmap(GuestAddress, releasedSize);
                MapFreshHeap(memory, GuestAddress, releasedSize);

                Assert.IsTrue(memory.GetSpan(GuestAddress, (int)releasedSize).ToArray().All(value => value == 0),
                    "A new guest heap mapping must not expose the previous allocation's contents.");
                Assert.IsTrue(memory.GetSpan(GuestAddress + releasedSize, 0x4000).ToArray().All(value => value == 0x67),
                    "Clearing a guest page must preserve a live neighbour sharing a larger host page.");
            }
            finally
            {
                memory.DecrementReferenceCount();
            }
        }

        [Test]
        public void CrossPartitionUnmapDefersDiscardUntilLiveViewsArePreserved()
        {
            const ulong PartitionSize = 1UL << 25;
            const byte LeftValue = 0x35;
            const byte RightValue = 0x7a;

            using MemoryBlock backing = new(0x20000, MemoryAllocationFlags.Reserve);
            List<(ulong Offset, ulong Size)> discardRequests = [];
            MemoryManagerHostTracked memory = null;

            memory = new MemoryManagerHostTracked(
                backing,
                1UL << 32,
                false,
                null,
                (block, offset, size) =>
                {
                    discardRequests.Add((offset, size));

                    Assert.That(memory.GetSpan(PartitionSize - GuestPageSize, 1)[0], Is.EqualTo(LeftValue));
                    Assert.That(memory.GetSpan(PartitionSize + (2 * GuestPageSize), 1)[0], Is.EqualTo(RightValue));

                    return true;
                });

            memory.IncrementReferenceCount();

            try
            {
                MapFreshHeap(memory, PartitionSize - GuestPageSize, GuestPageSize);
                MapFreshHeap(memory, PartitionSize, GuestPageSize);
                MapFreshHeap(memory, PartitionSize + (2 * GuestPageSize), GuestPageSize);

                memory.Write(PartitionSize - GuestPageSize, LeftValue);
                memory.Write(PartitionSize + (2 * GuestPageSize), RightValue);

                memory.Unmap(PartitionSize, GuestPageSize);

                Assert.That(discardRequests, Has.Count.EqualTo(1));
                Assert.That(discardRequests[0].Size, Is.EqualTo(MemoryBlock.GetPageSize()));
            }
            finally
            {
                memory.DecrementReferenceCount();
            }
        }

        private static void MapFreshHeap(MemoryManagerHostTracked memory, ulong address, ulong size)
        {
            memory.MapZeroed(address, GuestPageSize, size);
        }
    }
}
