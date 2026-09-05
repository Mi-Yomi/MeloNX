using NUnit.Framework;
using Ryujinx.Cpu.Jit.HostTracked;
using Ryujinx.Memory;
using System;
using System.Reflection;

namespace Ryujinx.Tests.Cpu
{
    [NonParallelizable] // Native tracked-region registration is process global.
    public class NativePageTableTests
    {
        private const ulong GuestPageSize = 4096;
        private const ulong AddressSpaceSize = 1UL << 39;
        private static ulong HostPageSize => MemoryBlock.GetPageSize();
        private static ulong GuestBytesPerPtPage => HostPageSize / sizeof(ulong) * GuestPageSize;

        private static MemoryBlock Storage(NativePageTable table) =>
            (MemoryBlock)typeof(NativePageTable).GetField("_nativePageTable", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(table);

        private static Func<ulong, ulong, bool, ulong, ulong> FaultCallback(NativePageTable table) =>
            typeof(NativePageTable).GetMethod("VirtualMemoryEvent", BindingFlags.Instance | BindingFlags.NonPublic)
                .CreateDelegate<Func<ulong, ulong, bool, ulong, ulong>>(table);

        private static void PrepareCanary(NativePageTable table, ulong ptPage, ulong pages)
        {
            // Make raw storage accessible WITHOUT setting the production commitment
            // bitmap. Pre-fix Unmap can then be observed overwriting these canaries,
            // rather than crashing the Windows test runner with an access violation.
            MemoryBlock memory = Storage(table);
            memory.Commit(ptPage * HostPageSize, pages * HostPageSize);
            memory.Fill(ptPage * HostPageSize, pages * HostPageSize, 0x5a);
        }

        private static void AssertCanary(NativePageTable table, ulong ptPage)
        {
            MemoryBlock memory = Storage(table);
            Assert.That(memory.Read<ulong>(ptPage * HostPageSize), Is.EqualTo(0x5a5a5a5a5a5a5a5aUL));
            Assert.That(memory.Read<ulong>((ptPage + 1) * HostPageSize - sizeof(ulong)), Is.EqualTo(0x5a5a5a5a5a5a5a5aUL));
        }

        [TestCase(0UL)]
        [TestCase(4096UL)]
        public void UnmapNeverMappedSpanSkipsEveryUntouchedPteChunk(ulong start)
        {
            using NativePageTable table = new(AddressSpaceSize);
            const ulong chunks = 128;
            PrepareCanary(table, 0, chunks);
            table.Unmap(start, chunks * GuestBytesPerPtPage - start);
            table.Unmap(start, chunks * GuestBytesPerPtPage - start);
            Assert.That(table.CommittedBytes, Is.Zero);
            Assert.That(table.ManagedLeafCount, Is.Zero);
            for (ulong page = 0; page < chunks; page++) AssertCanary(table, page);
            Assert.That(table.GetFaultStatistics().LazyWrites, Is.Zero);
        }

        [Test]
        public void UnmapAcrossUncommittedHolesPreservesAdjacentMappingsAndClearsOnlyTarget()
        {
            using NativePageTable table = new(AddressSpaceSize);
            using MemoryBlock backing = new(4 * GuestPageSize);
            ulong chunk = GuestBytesPerPtPage;
            ulong left = chunk - GuestPageSize;
            ulong middle = 2 * chunk + GuestPageSize;
            ulong right = 4 * chunk;
            table.Map(left, GuestPageSize, GuestPageSize, null, backing, false);
            table.Map(middle, 2 * GuestPageSize, GuestPageSize, null, backing, false);
            table.Map(right, 3 * GuestPageSize, GuestPageSize, null, backing, false);
            PrepareCanary(table, 1, 1);
            PrepareCanary(table, 3, 1);

            for (int repeat = 0; repeat < 2; repeat++)
            {
                table.Unmap(chunk + GuestPageSize, 3 * chunk - GuestPageSize);
                Assert.That(table.Read(left), Is.EqualTo((ulong)backing.Pointer + GuestPageSize));
                Assert.That(table.Read(right), Is.EqualTo((ulong)backing.Pointer + 3 * GuestPageSize));
                Assert.That(table.GetPhysicalAddress(left), Is.EqualTo(GuestPageSize));
                Assert.That(table.GetPhysicalAddress(right), Is.EqualTo(3 * GuestPageSize));
                Assert.That(table.GetPhysicalAddress(middle), Is.Zero);
                Assert.That(table.Read(middle), Is.EqualTo((ulong)table.PageTablePointer + table.ReservedBytes - HostPageSize));
                Assert.That(table.CommittedBytes, Is.EqualTo(3 * (long)HostPageSize));
                Assert.That(table.ManagedLeafCount, Is.EqualTo(2));
                AssertCanary(table, 1);
                AssertCanary(table, 3);
            }

            table.Map(middle, 2 * GuestPageSize, GuestPageSize, null, backing, false);
            Assert.That(table.Read(middle), Is.EqualTo((ulong)backing.Pointer + 2 * GuestPageSize));
            Assert.That(table.CommittedBytes, Is.EqualTo(3 * (long)HostPageSize));
        }

        [Test]
        public void FirstMappingAfterSkippedUnmapInitializesOnlyItsContainingPtePage()
        {
            using NativePageTable table = new(AddressSpaceSize);
            using MemoryBlock backing = new(4 * GuestPageSize);
            PrepareCanary(table, 0, 3);
            table.Unmap(0, 3 * GuestBytesPerPtPage);
            ulong address = GuestBytesPerPtPage + GuestPageSize;
            table.Map(address, GuestPageSize, GuestPageSize, null, backing, false);
            Assert.That(table.Read(address), Is.EqualTo((ulong)backing.Pointer + GuestPageSize));
            Assert.That(table.Read(address + GuestPageSize), Is.EqualTo((ulong)table.PageTablePointer + table.ReservedBytes - HostPageSize));
            Assert.That(table.CommittedBytes, Is.EqualTo((long)HostPageSize));
            AssertCanary(table, 0);
            AssertCanary(table, 2);
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        public void InvalidRangesFailBeforeAnyMappingOrCommitmentMutation(int operation)
        {
            using NativePageTable table = new(AddressSpaceSize);
            using MemoryBlock backing = new(4 * GuestPageSize);
            table.Map(GuestPageSize, GuestPageSize, GuestPageSize, null, backing, false);
            (ulong Va, ulong Size)[] invalid =
            [
                (AddressSpaceSize, GuestPageSize),
                (AddressSpaceSize - GuestPageSize, 2 * GuestPageSize),
                (ulong.MaxValue - GuestPageSize + 1, 2 * GuestPageSize),
                (GuestPageSize, ulong.MaxValue - GuestPageSize + 1),
                (1, GuestPageSize),
                (0, 1),
                (AddressSpaceSize + GuestPageSize, 0),
            ];
            foreach (var range in invalid)
            {
                Assert.Throws<InvalidMemoryRegionException>(() => Apply(table, backing, operation, range.Va, range.Size));
                Assert.That(table.Read(GuestPageSize), Is.EqualTo((ulong)backing.Pointer + GuestPageSize));
                Assert.That(table.GetPhysicalAddress(GuestPageSize), Is.EqualTo(GuestPageSize));
                Assert.That(table.ManagedLeafCount, Is.EqualTo(1));
                Assert.That(table.CommittedBytes, Is.EqualTo((long)HostPageSize));
            }
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        public void EmptyRangeAtAddressSpaceEndHasNoSideEffects(int operation)
        {
            using NativePageTable table = new(AddressSpaceSize);
            using MemoryBlock backing = new(4 * GuestPageSize);
            Apply(table, backing, operation, AddressSpaceSize, 0);
            Assert.That(table.CommittedBytes, Is.Zero);
            Assert.That(table.ManagedLeafCount, Is.Zero);
        }

        private static void Apply(NativePageTable table, MemoryBlock backing, int operation, ulong va, ulong size)
        {
            switch (operation)
            {
                case 0: table.Map(va, 0, size, null, backing, false); break;
                case 1: table.Unmap(va, size); break;
                case 2: table.Update(va, backing.Pointer, size); break;
                default: throw new ArgumentOutOfRangeException(nameof(operation));
            }
        }

        [Test]
        public void LazyFaultCountersDistinguishReadsWritesAndCountOnlyOneCommitPerHostPage()
        {
            using NativePageTable table = new(AddressSpaceSize);
            var fault = FaultCallback(table);
            Assert.That(fault(0, 1, false, 0x1234), Is.EqualTo((ulong)table.PageTablePointer));
            fault(sizeof(ulong), 1, false, 0x2345);
            fault(HostPageSize + sizeof(ulong), 1, true, 0x3456);
            var stats = table.GetFaultStatistics();
            Assert.That(stats.LazyReads, Is.EqualTo(2));
            Assert.That(stats.LazyWrites, Is.EqualTo(1));
            Assert.That(stats.GuardFaults, Is.Zero);
            Assert.That(stats.LastOffset, Is.EqualTo((long)HostPageSize + sizeof(ulong)));
            Assert.That(stats.LastWrite, Is.True);
            Assert.That(stats.LastFaultPc, Is.EqualTo(0x3456UL));
            Assert.That(table.CommittedBytes, Is.EqualTo(2 * (long)HostPageSize));
            Assert.That(table.ManagedLeafCount, Is.Zero);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void GuardFaultRemainsInvalidAndReportsCommitmentWithoutCommittingGuard(bool write)
        {
            using NativePageTable table = new(AddressSpaceSize);
            var fault = FaultCallback(table);
            fault(0, 1, false, 0x1234);
            ulong offset = table.ReservedBytes - HostPageSize + 17;
            InvalidMemoryRegionException exception = Assert.Throws<InvalidMemoryRegionException>(() => fault(offset, 1, write, 0x5678));
            Assert.That(exception.Message, Does.Contain($"table_relative_offset=0x{offset:X}"));
            Assert.That(exception.Message, Does.Contain($"table_committed_bytes={HostPageSize}"));
            Assert.That(exception.Message, Does.Contain("lazy_read_faults=1"));
            Assert.That(exception.Message, Does.Contain("lazy_write_faults=0"));
            Assert.That(exception.Message, Does.Contain("guard_faults=1"));
            Assert.That(exception.Message, Does.Contain("fault_pc=0x5678"));
            Assert.That(table.CommittedBytes, Is.EqualTo((long)HostPageSize));
            Assert.That(table.GetFaultStatistics().GuardFaults, Is.EqualTo(1));
            Assert.That(table.GetFaultStatistics().LastOffset, Is.EqualTo((long)offset));
            Assert.That(table.GetFaultStatistics().LastFaultPc, Is.EqualTo(0x5678UL));
        }
    }
}
