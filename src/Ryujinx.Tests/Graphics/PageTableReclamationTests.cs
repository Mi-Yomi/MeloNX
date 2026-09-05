using NUnit.Framework;
using Ryujinx.Memory;

namespace Ryujinx.Tests.Graphics
{
    public class PageTableReclamationTests
    {
        [Test]
        public void EmptyValueTypeLeafIsReleased()
        {
            PageTable<ulong> table = new();
            table.Map(0x1000, 0x1234);
            Assert.That(table.AllocatedLeafCount, Is.EqualTo(1));
            table.Unmap(0x1000);
            Assert.That(table.AllocatedLeafCount, Is.Zero);
            Assert.That(table.Read(0x1000), Is.Zero);
        }

        [Test]
        public void LiveNeighbourPreventsPrematureReclamation()
        {
            PageTable<ulong> table = new();
            table.Map(0x1000, 0x1234);
            table.Map(0x2000, 0x5678);
            table.Unmap(0x1000);
            Assert.That(table.AllocatedLeafCount, Is.EqualTo(1));
            Assert.That(table.Read(0x2000), Is.EqualTo(0x5678));
            table.Unmap(0x2000);
            Assert.That(table.AllocatedLeafCount, Is.Zero);
        }

        [Test]
        public void SparseStreamingUnmapsDoNotRetainEmptyLeaves()
        {
            PageTable<ulong> table = new();
            for (int round = 0; round < 4; round++)
            {
                for (ulong i = 0; i < 1024; i++) table.Map(i << 21, i + 1);
                Assert.That(table.AllocatedLeafCount, Is.EqualTo(1024));
                for (ulong i = 0; i < 1024; i++) table.Unmap(i << 21);
                Assert.That(table.AllocatedLeafCount, Is.Zero);
            }
        }

        [Test]
        public void NativeIntegerLeafCanBeReclaimedAndRemapped()
        {
            PageTable<nuint> table = new();
            table.Map(1UL << 40, 42);
            table.Unmap(1UL << 40);
            Assert.That(table.AllocatedLeafCount, Is.Zero);
            table.Map(1UL << 40, 99);
            Assert.That(table.Read(1UL << 40), Is.EqualTo((nuint)99));
        }
    }
}
