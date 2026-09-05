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
    public class BufferCacheGrowthTests
    {
        private const ulong Address = 0x10000000;
        private const ulong MiB = 1024 * 1024;

        [TestCase(65536, 98304)]
        [TestCase(2097152, 3145728)]
        public void TailGrowthIsAnchoredToExistingStorageAndPreservesData(int existingSize, int expectedSize)
        {
            using CacheFixture fixture = new();
            fixture.Fill(0, existingSize, 0x31);
            fixture.Read(0, existingSize);
            // Dirty data in the old tracking region and newly covered data must both survive migration.
            fixture.Fill(0x1000, 0x1000, 0x52);
            fixture.Fill((ulong)existingSize, 0x1000, 0x73);

            BufferRange tail = fixture.Read((ulong)existingSize - 0x1000, 0x2000);
            byte[] backing = fixture.Backend.Buffers[tail.Handle];

            Assert.Multiple(() =>
            {
                Assert.That(backing.Length, Is.EqualTo(expectedSize), "Speculative growth is relative to existing storage, not the tail request address");
                Assert.That(backing[0], Is.EqualTo(0x31));
                Assert.That(backing[0x1000], Is.EqualTo(0x52), "Inherited dirty tracking must upload the new guest bytes");
                Assert.That(backing[existingSize], Is.EqualTo(0x73));
                Assert.That(tail.Offset, Is.EqualTo(existingSize - 0x1000));
                Assert.That(fixture.Backend.Buffers.Count, Is.EqualTo(1));
                Assert.That(fixture.Backend.Events.Count(e => e.Operation == "create"), Is.EqualTo(2));
            });
        }

        [Test]
        public void TailGrowthDoesNotAbsorbAnUnneededNeighbourOrInvalidateItsHandle()
        {
            using CacheFixture fixture = new();
            fixture.Fill(0, 0x10000, 0x31);
            fixture.Fill(0x20000, 0x1000, 0x64);
            fixture.Read(0, 0x10000);
            BufferRange neighbour = fixture.Read(0x20000, 0x1000);

            fixture.Read(0xf000, 0x2000);
            BufferRange afterGrowth = fixture.Read(0x20000, 0x1000);

            Assert.Multiple(() =>
            {
                Assert.That(afterGrowth.Handle, Is.EqualTo(neighbour.Handle), "Unnecessary speculative overlap recreated a neighbour");
                Assert.That(fixture.Backend.Buffers[afterGrowth.Handle][afterGrowth.Offset], Is.EqualTo(0x64));
                Assert.That(fixture.Backend.Buffers.Count, Is.EqualTo(2));
                Assert.That(fixture.Cache.CachedBytes, Is.EqualTo(0x19000UL));
                Assert.That(fixture.Backend.Events.Count(e => e.Operation == "create"), Is.EqualTo(3));
            });
        }

        [Test]
        public void NecessaryOverlapInsideGrowthIsMergedAndKeepsNeighbourBytes()
        {
            using CacheFixture fixture = new();
            fixture.Fill(0, 0x10000, 0x31);
            fixture.Fill(0x14000, 0x1000, 0x64);
            fixture.Read(0, 0x10000);
            fixture.Read(0x14000, 0x1000);

            BufferRange tail = fixture.Read(0xf000, 0x2000);
            byte[] backing = fixture.Backend.Buffers[tail.Handle];

            Assert.Multiple(() =>
            {
                Assert.That(backing.Length, Is.EqualTo(0x18000));
                Assert.That(backing[0], Is.EqualTo(0x31));
                Assert.That(backing[0x14000], Is.EqualTo(0x64));
                Assert.That(fixture.Backend.Buffers.Count, Is.EqualTo(1));
            });
        }

        private sealed class CacheFixture : IDisposable
        {
            internal readonly AuditTestRenderer Backend = new();
            internal readonly BufferCache Cache;
            private readonly MemoryBlock _backing = new(16 * MiB, MemoryAllocationFlags.Reserve);
            private readonly MemoryManagerHostTracked _memory;
            private readonly GpuContext _context;
            private int _sequence;

            private static void Set(object owner, string field, object value) =>
                owner.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(owner, value);

            internal CacheFixture()
            {
                _memory = new MemoryManagerHostTracked(_backing, 1UL << 32, false, null);
                _memory.IncrementReferenceCount();
                _memory.MapZeroed(Address, 0x1000, 8 * MiB);
                _context = (GpuContext)RuntimeHelpers.GetUninitializedObject(typeof(GpuContext));
                Set(_context, "<Renderer>k__BackingField", Backend);
                PhysicalMemory physical = (PhysicalMemory)RuntimeHelpers.GetUninitializedObject(typeof(PhysicalMemory));
                Set(physical, "_context", _context);
                Set(physical, "_cpuMemory", _memory);
                Cache = new BufferCache(_context, physical);
                Set(physical, "<BufferCache>k__BackingField", Cache);
                Cache.ConfigureMemoryBudget(8 * MiB, true);
            }

            internal void Fill(ulong offset, int size, byte value) =>
                _memory.Write(Address + offset, Enumerable.Repeat(value, size).ToArray());

            internal BufferRange Read(ulong offset, int size)
            {
                Set(_context, "<SequenceNumber>k__BackingField", ++_sequence);
                return Cache.GetBufferRange(new MultiRange(Address + offset, (ulong)size), BufferStage.None);
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
