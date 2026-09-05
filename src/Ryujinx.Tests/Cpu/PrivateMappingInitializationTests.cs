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

        [TestCase(false)]
        [TestCase(true)]
        public void CrossPartitionUnmapPreservesLiveViewsAndDiscardsOnlyWholeHostPages(bool releaseWholeHostPage)
        {
            const ulong PartitionSize = 1UL << 25;
            const byte LeftValue = 0x35;
            const byte RightValue = 0x7a;
            ulong hostPageSize = MemoryBlock.GetPageSize();
            ulong releasedSize = releaseWholeHostPage ? hostPageSize : GuestPageSize;
            ulong leftAddress = PartitionSize - releasedSize;
            ulong rightAddress = PartitionSize + (2 * releasedSize);

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
                    Assert.That(offset % hostPageSize, Is.Zero);
                    Assert.That(size % hostPageSize, Is.Zero);
                    Assert.That(memory.GetSpan(leftAddress, (int)releasedSize).ToArray(), Is.All.EqualTo(LeftValue));
                    Assert.That(memory.GetSpan(rightAddress, (int)releasedSize).ToArray(), Is.All.EqualTo(RightValue));
                    return true;
                });

            memory.IncrementReferenceCount();

            try
            {
                MapFreshHeap(memory, leftAddress, releasedSize);
                MapFreshHeap(memory, PartitionSize, releasedSize);
                MapFreshHeap(memory, rightAddress, releasedSize);
                memory.Write(leftAddress, Enumerable.Repeat(LeftValue, (int)releasedSize).ToArray());
                memory.Write(rightAddress, Enumerable.Repeat(RightValue, (int)releasedSize).ToArray());
                memory.Unmap(PartitionSize, releasedSize);

                // On a 16 KiB host the 4 KiB hole shares its native page with the live right
                // neighbour. No discard is permitted. A full host-page hole must be reclaimed.
                bool sharesHostPage = releasedSize < hostPageSize && rightAddress < PartitionSize + hostPageSize;
                Assert.That(discardRequests, Has.Count.EqualTo(sharesHostPage ? 0 : 1));
                if (!sharesHostPage)
                {
                    Assert.That(discardRequests[0].Size, Is.EqualTo(hostPageSize));
                }

                Assert.That(memory.GetSpan(leftAddress, (int)releasedSize).ToArray(), Is.All.EqualTo(LeftValue));
                Assert.That(memory.GetSpan(rightAddress, (int)releasedSize).ToArray(), Is.All.EqualTo(RightValue));
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
