using NUnit.Framework;
using Ryujinx.Graphics.Vulkan;

namespace Ryujinx.Tests.Graphics
{
    public class MemoryAllocatorStatisticsTests
    {
        [Test]
        public void BlockStatisticsTrackAlignmentHolesAndCoalescing()
        {
            MemoryAllocatorBlockList.Block block = new(default, 0, 1024);

            Assert.That(block.Allocate(100, 64), Is.Zero);
            Assert.That(block.Allocate(100, 64), Is.EqualTo(128));

            var fragmented = block.GetFreeStatistics();
            Assert.Multiple(() =>
            {
                Assert.That(fragmented.RangeCount, Is.EqualTo(2));
                Assert.That(fragmented.FreeBytes, Is.EqualTo(824));
                Assert.That(fragmented.LargestFreeRangeBytes, Is.EqualTo(796));
            });

            block.Free(0, 100);
            block.Free(128, 100);

            var coalesced = block.GetFreeStatistics();
            Assert.Multiple(() =>
            {
                Assert.That(coalesced.RangeCount, Is.EqualTo(1));
                Assert.That(coalesced.FreeBytes, Is.EqualTo(1024));
                Assert.That(coalesced.LargestFreeRangeBytes, Is.EqualTo(1024));
                Assert.That(block.IsTotallyFree(), Is.True);
            });
        }
    }
}
