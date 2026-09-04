using NUnit.Framework;
using Ryujinx.Cpu;
using Ryujinx.Memory;
using System.Collections.Generic;

namespace Ryujinx.Tests.Cpu
{
    class PrivateMemoryAllocatorTests
    {
        private const ulong PageSize = 0x4000;

        [Test]
        public void FreshTailDoesNotRequireClearing()
        {
            PrivateMemoryAllocator.Block block = new(null, 8 * PageSize);

            Assert.AreEqual(0UL, block.Allocate(PageSize, PageSize, out ulong firstReusedSize));
            Assert.AreEqual(0UL, firstReusedSize);
            Assert.AreEqual(PageSize, block.Allocate(2 * PageSize, PageSize, out ulong tailReusedSize));
            Assert.AreEqual(0UL, tailReusedSize,
                "An untouched tail in an existing OS allocation must not require eager zeroing.");
        }

        [Test]
        public void FreedRangeRequiresClearingWhenReused()
        {
            PrivateMemoryAllocator.Block block = new(null, 8 * PageSize);
            block.Allocate(PageSize, PageSize);
            ulong releasedOffset = block.Allocate(2 * PageSize, PageSize);
            block.Free(releasedOffset, 2 * PageSize);

            Assert.AreEqual(releasedOffset, block.Allocate(PageSize, PageSize, out ulong reusedSize));
            Assert.AreEqual(PageSize, reusedSize,
                "Freeing a range must retain its initialization history.");
        }

        [Test]
        public void AllocationSpanningReusedPrefixAndFreshTailClearsOnlyPrefix()
        {
            PrivateMemoryAllocator.Block block = new(null, 8 * PageSize);
            block.Allocate(PageSize, PageSize);
            ulong releasedOffset = block.Allocate(2 * PageSize, PageSize);
            block.Free(releasedOffset, 2 * PageSize);

            Assert.AreEqual(releasedOffset, block.Allocate(4 * PageSize, PageSize, out ulong reusedSize));
            Assert.AreEqual(2 * PageSize, reusedSize,
                "Coalescing a freed range with fresh tail space must preserve the old prefix length.");

            block.Free(releasedOffset, 4 * PageSize);
            block.Allocate(4 * PageSize, PageSize, out ulong secondReusedSize);
            Assert.AreEqual(4 * PageSize, secondReusedSize,
                "The formerly fresh suffix is part of the used region after allocation.");
        }

        [Test]
        public void FailedAllocationDoesNotAdvanceInitializationHistory()
        {
            PrivateMemoryAllocator.Block block = new(null, 4 * PageSize);
            block.Allocate(PageSize, PageSize);

            Assert.AreEqual(PrivateMemoryAllocator.InvalidOffset,
                block.Allocate(4 * PageSize, PageSize, out ulong failedReusedSize));
            Assert.AreEqual(0UL, failedReusedSize);
            Assert.AreEqual(PageSize, block.Allocate(3 * PageSize, PageSize, out ulong reusedSize));
            Assert.AreEqual(0UL, reusedSize);
        }

        [Test]
        public void AlignmentUsesTheActualAllocationOffset()
        {
            PrivateMemoryAllocator.Block block = new(null, 8 * PageSize);
            block.Allocate(PageSize, PageSize);

            Assert.AreEqual(4 * PageSize, block.Allocate(PageSize, 4 * PageSize, out ulong freshReusedSize));
            Assert.AreEqual(0UL, freshReusedSize);

            block.Free(4 * PageSize, PageSize);
            Assert.AreEqual(4 * PageSize, block.Allocate(2 * PageSize, 4 * PageSize, out ulong reusedSize));
            Assert.AreEqual(PageSize, reusedSize);
        }

        [Test]
        public void PartialBlockFreeRequestsDiscardButWholeBlockFreeDoesNot()
        {
            List<(MemoryBlock Memory, ulong Offset, ulong Size)> discardRequests = [];

            using PrivateMemoryAllocator allocator = new(
                8 * PageSize,
                MemoryAllocationFlags.Mirrorable,
                (memory, offset, size) =>
                {
                    discardRequests.Add((memory, offset, size));
                    return true;
                });

            PrivateMemoryAllocation first = allocator.Allocate(PageSize, PageSize);
            PrivateMemoryAllocation second = allocator.Allocate(PageSize, PageSize);

            MemoryBlock block = first.Memory;

            first.Dispose();

            Assert.That(discardRequests, Has.Count.EqualTo(1));
            Assert.Multiple(() =>
            {
                Assert.That(discardRequests[0].Memory, Is.SameAs(block));
                Assert.That(discardRequests[0].Offset, Is.EqualTo(0UL));
                Assert.That(discardRequests[0].Size, Is.EqualTo(PageSize));
            });

            second.Dispose();

            Assert.That(discardRequests, Has.Count.EqualTo(1),
                "Destroying a wholly free backing block should replace a redundant discard request.");
        }
    }
}
