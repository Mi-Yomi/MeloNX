using NUnit.Framework;
using Ryujinx.Cpu;
using Ryujinx.Memory;

namespace Ryujinx.Tests.Cpu
{
    public class PrivateMemoryOwnershipTests
    {
        [Test]
        public void FreeAndDisposeRestoreProcessOwnershipBaseline()
        {
            ulong page = MemoryBlock.GetPageSize();
            var before = PrivateMemoryAllocator.GetProcessStatistics();
            using (PrivateMemoryAllocator allocator = new(8 * page, MemoryAllocationFlags.Mirrorable))
            {
                var first = allocator.Allocate(page, page);
                var second = allocator.Allocate(page, page);
                var live = PrivateMemoryAllocator.GetProcessStatistics();
                Assert.That(live.Reserved - before.Reserved, Is.EqualTo(8 * (long)page));
                Assert.That(live.Allocated - before.Allocated, Is.EqualTo(2 * (long)page));
                Assert.That(live.Blocks - before.Blocks, Is.EqualTo(1));
                first.Dispose();
                second.Dispose();
                Assert.That(PrivateMemoryAllocator.GetProcessStatistics(), Is.EqualTo(before));
            }
            Assert.That(PrivateMemoryAllocator.GetProcessStatistics(), Is.EqualTo(before));
        }
    }
}
