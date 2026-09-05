using NUnit.Framework;
using Ryujinx.Graphics.Vulkan;
using Silk.NET.Vulkan;

namespace Ryujinx.Tests.Graphics
{
    public class BufferUsageBitmapTests
    {
        private const int PageSize = 4096;
        private const int BufferSize = 5 * PageSize + 17;
        private static int WholeSize => unchecked((int)Vk.WholeSize);

        [TestCase(false)]
        [TestCase(true)]
        public void WholeSizeTracksEveryRemainingPageInRequestedAccessPlane(bool write)
        {
            BufferUsageBitmap bitmap = new(BufferSize, PageSize);
            bitmap.Add(0, PageSize + 20, WholeSize, write);
            Assert.That(bitmap.OverlapsWith(0, 0, PageSize, write), Is.False);
            for (int page = 1; page <= 5; page++)
            {
                Assert.That(bitmap.OverlapsWith(0, page * PageSize, 1, write), Is.True, $"Page {page}");
                Assert.That(bitmap.OverlapsWith(0, page * PageSize, 1, !write), Is.False);
            }
            Assert.That(bitmap.OverlapsWith(0, BufferSize - 1, WholeSize, write), Is.True);
            Assert.That(bitmap.OverlapsWith(0, BufferSize, WholeSize, write), Is.False);
            Assert.That(bitmap.OverlapsWith(1, 0, WholeSize, write), Is.False);
        }

        [Test]
        public void WholeSizeQueryFindsUsageInLastPageRatherThanOnlyFirstPage()
        {
            BufferUsageBitmap bitmap = new(BufferSize, PageSize);
            bitmap.Add(3, BufferSize - 1, 1, false);
            Assert.That(bitmap.OverlapsWith(3, 0, WholeSize), Is.True);
            Assert.That(bitmap.OverlapsWith(0, WholeSize, false), Is.True);
            Assert.That(bitmap.OverlapsWith(3, 0, PageSize), Is.False);
        }

        [Test]
        public void ClearingOneCommandKeepsOtherCommandsReadAndWriteDependencies()
        {
            MultiFenceHolder usage = new(BufferSize);
            usage.AddBufferUse(0, 0, WholeSize, true);
            usage.AddBufferUse(1, PageSize, WholeSize, true);
            usage.RemoveBufferUses(0);
            Assert.That(usage.IsBufferRangeInUse(0, 0, WholeSize), Is.False);
            Assert.That(usage.IsBufferRangeInUse(1, BufferSize - 1, 1), Is.True);
            Assert.That(usage.IsBufferRangeInUse(BufferSize - 1, 1, false), Is.True);
            Assert.That(usage.IsBufferRangeInUse(BufferSize - 1, 1, true), Is.True);
            Assert.That(usage.IsBufferRangeInUse(0, PageSize, false), Is.False);
            usage.RemoveBufferUses(1);
            Assert.That(usage.IsBufferRangeInUse(0, WholeSize, false), Is.False);
            Assert.That(usage.IsBufferRangeInUse(0, WholeSize, true), Is.False);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void OversizedRequestIsClampedWithoutWrappingOrTouchingNextCommand(bool write)
        {
            BufferUsageBitmap bitmap = new(BufferSize, PageSize);
            bitmap.Add(0, 2 * PageSize, int.MaxValue, write);
            Assert.That(bitmap.OverlapsWith(0, 0, 2 * PageSize, write), Is.False);
            Assert.That(bitmap.OverlapsWith(0, BufferSize - 1, int.MaxValue, write), Is.True);
            Assert.That(bitmap.OverlapsWith(1, 0, WholeSize, write), Is.False);
            Assert.That(bitmap.OverlapsWith(0, 0, WholeSize, !write), Is.False);
        }

        [TestCase(-1, 1)]
        [TestCase(0, -2)]
        [TestCase(0, int.MinValue)]
        [TestCase(1, 0)]
        [TestCase(BufferSize, -1)]
        [TestCase(BufferSize + 1, 10)]
        [TestCase(int.MaxValue, int.MaxValue)]
        public void InvalidRangesDoNotMutateTrackingOrOverlapAnyAccess(int offset, int size)
        {
            BufferUsageBitmap bitmap = new(BufferSize, PageSize);
            bitmap.Add(0, offset, size, false);
            bitmap.Add(0, offset, size, true);
            Assert.That(bitmap.OverlapsWith(0, 0, WholeSize, false), Is.False);
            Assert.That(bitmap.OverlapsWith(0, 0, WholeSize, true), Is.False);
            bitmap.Add(0, 0, WholeSize, false);
            bitmap.Add(0, 0, WholeSize, true);
            Assert.That(bitmap.OverlapsWith(0, offset, size, false), Is.False);
            Assert.That(bitmap.OverlapsWith(0, offset, size, true), Is.False);
            Assert.That(bitmap.OverlapsWith(offset, size, true), Is.False);
        }

        [TestCase(int.MaxValue, int.MaxValue - 2, int.MaxValue, 2)]
        [TestCase(int.MaxValue, int.MaxValue - 2, -1, 2)]
        [TestCase(100, 23, -1, 77)]
        [TestCase(100, 23, 101, 77)]
        [TestCase(100, 23, 12, 12)]
        public void BoundsNormalizeUsingRemainingLengthWithoutOverflow(int bufferSize, int offset, int requested, int expected)
        {
            Assert.That(BufferRangeBounds.TryNormalize(bufferSize, offset, ref requested), Is.True);
            Assert.That(requested, Is.EqualTo(expected));
        }

        [TestCase(0, 0, -1)]
        [TestCase(-1, 0, 1)]
        [TestCase(10, -1, 1)]
        [TestCase(10, 10, -1)]
        [TestCase(10, 1, -2)]
        [TestCase(10, 1, 0)]
        public void InvalidNormalizationDoesNotChangeCallersSize(int bufferSize, int offset, int requested)
        {
            int original = requested;
            Assert.That(BufferRangeBounds.TryNormalize(bufferSize, offset, ref requested), Is.False);
            Assert.That(requested, Is.EqualTo(original));
        }
    }
}
