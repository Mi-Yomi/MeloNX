using NUnit.Framework;
using Ryujinx.Cpu.Jit;
using Ryujinx.Memory;
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

        private static void MapFreshHeap(MemoryManagerHostTracked memory, ulong address, ulong size)
        {
            memory.MapZeroed(address, GuestPageSize, size);
        }
    }
}
