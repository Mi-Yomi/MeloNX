using NUnit.Framework;
using Ryujinx.Cpu.Jit;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Gpu;
using Ryujinx.Graphics.Gpu.Memory;
using Ryujinx.Memory;
using Ryujinx.Memory.Range;
using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Ryujinx.Tests.Graphics
{
    public class BufferCacheStabilityTests
    {
        private const ulong MiB = 1024 * 1024;
        private const ulong Address = 0x10000000;
        private const int BufferSize = 65536;
        private static readonly FieldInfo SequenceField = typeof(GpuContext).GetField("<SequenceNumber>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);

        private static void Set(object owner, string field, object value) =>
            owner.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(owner, value);

        [TestCase(969UL)]
        [TestCase(1229UL)]
        public void FittingWorkingSetSurvivesReportedProloguePressureWithoutReuploads(ulong availableMiB)
        {
            using CacheFixture fixture = new();
            BufferHandle[] handles = new BufferHandle[24];
            for (int i = 0; i < handles.Length; i++) handles[i] = fixture.Read(i).Handle;

            for (int cycle = 0; cycle < 40; cycle++)
            {
                fixture.ApplyPressure(availableMiB * MiB);
                for (int i = 0; i < handles.Length; i++)
                {
                    BufferRange range = fixture.Read(i);
                    Assert.That(range.Handle, Is.EqualTo(handles[i]), "Fitting working set was evicted by pressure policy");
                    Assert.That(fixture.Backend.Buffers[range.Handle][range.Offset], Is.EqualTo((byte)(i + 1)));
                }
            }

            Assert.That(fixture.Backend.Events.Count(e => e.Operation == "create"), Is.EqualTo(24));
            Assert.That(fixture.Cache.CachedBytes, Is.EqualTo(24UL * BufferSize));
            Assert.That(fixture.Cache.EffectiveCapacity, Is.EqualTo(2 * MiB));
        }

        [TestCase(512UL)]
        [TestCase(256UL)]
        public void EmergencyStillEvictsCleanStorageAndReuploadsCorrectGuestBytes(ulong availableMiB)
        {
            using CacheFixture fixture = new();
            for (int i = 0; i < 24; i++) fixture.Read(i);
            fixture.NextSequence();
            fixture.ApplyPressure(availableMiB * MiB);
            ulong expectedCapacity = availableMiB <= 256 ? MiB / 2 : MiB;
            Assert.That(fixture.Cache.EffectiveCapacity, Is.EqualTo(expectedCapacity));
            Assert.That(fixture.Cache.CachedBytes, Is.LessThanOrEqualTo(expectedCapacity));
            Assert.That(fixture.Backend.Events.Count(e => e.Operation == "delete"), Is.GreaterThan(0));
            for (int i = 0; i < 24; i++)
            {
                BufferRange range = fixture.Read(i);
                Assert.That(fixture.Backend.Buffers[range.Handle].AsSpan(range.Offset, BufferSize).ToArray(), Is.All.EqualTo((byte)(i + 1)));
            }
        }

        [Test]
        public void BelowBudgetSequenceMaintenanceDoesNotAllocate()
        {
            using CacheFixture fixture = new();
            fixture.Read(0);
            for (int i = 0; i < 100; i++) fixture.Cache.TrimToCapacity();
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 20000; i++) fixture.Cache.TrimToCapacity();
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.That(allocated, Is.Zero, "An under-budget cache must not construct eviction delegates per GPU sequence");
        }

        private sealed class CacheFixture : IDisposable
        {
            internal readonly AuditTestRenderer Backend = new();
            internal readonly BufferCache Cache;
            private readonly MemoryBlock _backing = new(8 * MiB, MemoryAllocationFlags.Reserve);
            private readonly MemoryManagerHostTracked _memory;
            private readonly GpuContext _context;
            private int _sequence;

            internal CacheFixture()
            {
                _memory = new MemoryManagerHostTracked(_backing, 1UL << 32, false, null);
                _memory.IncrementReferenceCount();
                _memory.MapZeroed(Address, 0x1000, 4 * MiB);
                for (int i = 0; i < 24; i++)
                    _memory.Write(Address + (ulong)(i * BufferSize), Enumerable.Repeat((byte)(i + 1), BufferSize).ToArray());
                _context = (GpuContext)RuntimeHelpers.GetUninitializedObject(typeof(GpuContext));
                Set(_context, "<Renderer>k__BackingField", Backend);
                PhysicalMemory physical = (PhysicalMemory)RuntimeHelpers.GetUninitializedObject(typeof(PhysicalMemory));
                Set(physical, "_context", _context);
                Set(physical, "_cpuMemory", _memory);
                Cache = new BufferCache(_context, physical);
                Set(physical, "<BufferCache>k__BackingField", Cache);
                Cache.ConfigureMemoryBudget(2 * MiB, true);
            }

            internal void NextSequence() => SequenceField.SetValue(_context, ++_sequence);

            internal BufferRange Read(int index)
            {
                NextSequence();
                return Cache.GetBufferRange(new MultiRange(Address + (ulong)(index * BufferSize), BufferSize), BufferStage.None);
            }

            internal void ApplyPressure(ulong available)
            {
                ulong? limit = MemoryPressureTrimPolicy.CalculatePersistentBufferCapacity(Cache.Capacity, MemoryPressureSeverity.Critical, available);
                if (limit.HasValue) Cache.LatchPressureCapacity(limit.Value);
                Cache.TrimToCapacity(MemoryPressureTrimPolicy.CalculateBufferTarget(Cache.Capacity, MemoryPressureSeverity.Critical, available));
            }

            public void Dispose()
            {
                Cache.Dispose();
                _memory.DecrementReferenceCount();
                _backing.Dispose();
                Assert.That(Backend.Buffers, Is.Empty);
            }
        }
    }
}
