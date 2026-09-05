using NUnit.Framework;
using Ryujinx.Common.Diagnostics;
using Ryujinx.Graphics.Gpu.Memory;
using Ryujinx.Graphics.Vulkan;
using Silk.NET.Core.Contexts;
using Silk.NET.Vulkan;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace Ryujinx.Tests.Graphics
{
    public class BufferForensicsTests
    {
        [TestCase(4096, 0)]
        [TestCase(4097, 1)]
        [TestCase(65536, 1)]
        [TestCase(65537, 2)]
        [TestCase(1048576, 2)]
        [TestCase(1048577, 3)]
        public void SizeBucketsKeepBoundaryAllocationsSeparate(int bytes, int expected) =>
            Assert.That(ResourceEventCounters.GetSizeBucket(bytes), Is.EqualTo(expected));

        [Test]
        public void HotEventAccountingAllocatesNothingAndHistoryIsBounded()
        {
            ResourceEventCounters counters = new(["created"], ["general"]);
            for (int i = 0; i < 1000; i++) counters.Record(0, 0, i, 4096);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 20000; i++) counters.Record(0, 0, i, 4096);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.That(allocated, Is.Zero);
            using JsonDocument document = JsonDocument.Parse(counters.CreateSnapshot(null));
            JsonElement root = document.RootElement;
            Assert.That(root.GetProperty("cumulative").GetProperty("created").GetProperty("count").GetInt64(), Is.EqualTo(21000));
            Assert.That(root.GetProperty("recent_events").GetArrayLength(), Is.EqualTo(ResourceEventCounters.EventCapacity));
            Assert.That(root.GetProperty("events_sampled_total").GetInt64(), Is.LessThan(400));
        }

        [Test]
        public void ConcurrentRetirementCountersAreCompleteEvenWhenSamplesAreDropped()
        {
            ResourceEventCounters counters = new(["created", "destroyed"], ["general"]);
            Parallel.For(0, 10000, i =>
            {
                counters.Record(0, 0, i + 1, 65536);
                counters.Record(1, 0, i + 1, 65536);
            });
            Assert.That(counters.GetCount(0), Is.EqualTo(10000));
            Assert.That(counters.GetCount(1), Is.EqualTo(10000));
            Assert.That(counters.GetBytes(1), Is.EqualTo(655360000));
            using JsonDocument document = JsonDocument.Parse(counters.CreateSnapshot(null));
            Assert.That(document.RootElement.GetProperty("recent_events").GetArrayLength(), Is.LessThanOrEqualTo(64));
        }

        [Test]
        public void ChangedOrCollidingGuestRangesDoNotInventRecreationEvidence()
        {
            BufferCacheDiagnostics diagnostics = new();
            diagnostics.Evicted(1, 0x1000, 4096, true);
            diagnostics.Created(2, 0x1000, 8192, false);
            // 0x200000 and 0x1000 hash to the same bounded table slot.
            diagnostics.Evicted(3, 0x200000, 4096, false);
            diagnostics.Created(4, 0x1000, 4096, false);
            diagnostics.Publish(4096, 8192, 4096);
            using JsonDocument document = JsonDocument.Parse(diagnostics.GetSnapshot());
            JsonElement totals = document.RootElement.GetProperty("cumulative");
            Assert.That(totals.GetProperty("recreated_after_pressure").GetProperty("count").GetInt64(), Is.Zero);
            Assert.That(totals.GetProperty("recreated_after_capacity").GetProperty("count").GetInt64(), Is.Zero);
            Assert.That(diagnostics.GetSnapshot(), Does.Not.Contain("\"address\""));
        }

        [Test]
        public void SaturatedSnapshotsFitPerOwnerExportLimitAndCachedReadsAllocateNothing()
        {
            BufferCacheDiagnostics cache = new();
            BufferLifecycleDiagnostics native = new();
            for (int i = 1; i < 10000; i++)
            {
                cache.Evicted(i, 0x1000, 65536, true);
                cache.Created(i + 10000, 0x1000, 65536, false);
                native.Created(long.MaxValue - i, int.MaxValue, BufferAllocationPurpose.General);
                native.DisposeRequested(long.MaxValue - i, int.MaxValue, BufferAllocationPurpose.General);
                native.NativeDestroyed(long.MaxValue - i, int.MaxValue, BufferAllocationPurpose.General);
            }
            SaturateCounterDigits(cache);
            SaturateCounterDigits(native);
            cache.Publish(4096, 65536, 4096);
            native.Publish();
            using JsonDocument cacheDocument = JsonDocument.Parse(cache.GetSnapshotUtf8());
            using JsonDocument nativeDocument = JsonDocument.Parse(native.GetSnapshotUtf8());
            Assert.That(cacheDocument.RootElement.GetProperty("recent_events").GetArrayLength(), Is.EqualTo(64));
            Assert.That(nativeDocument.RootElement.GetProperty("recent_events").GetArrayLength(), Is.EqualTo(64));
            Assert.That(cache.PublishFailures, Is.Zero);
            Assert.That(native.PublishFailures, Is.Zero);
            Assert.That(Encoding.UTF8.GetByteCount(cache.GetSnapshot()), Is.LessThan(24 * 1024));
            Assert.That(Encoding.UTF8.GetByteCount(native.GetSnapshot()), Is.LessThan(24 * 1024));
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 10000; i++)
            {
                _ = cache.GetSnapshot();
                _ = native.GetSnapshot();
                _ = cache.GetSnapshotUtf8();
                _ = native.GetSnapshotUtf8();
            }
            Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero);
        }

        private static void SaturateCounterDigits(object diagnostics)
        {
            const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
            ResourceEventCounters counters = (ResourceEventCounters)diagnostics.GetType().GetField("_events", fields).GetValue(diagnostics);
            foreach (string name in new[] { "_counts", "_bytes", "_reasonCounts" })
            {
                Array.Fill((long[])typeof(ResourceEventCounters).GetField(name, fields).GetValue(counters), 100_000_000_000_000_000L);
            }
        }

        [Test]
        public void NativeRetirementWaitsForActualCommandDependencyAndDoesNotCountAliasAsOwnedBytes()
        {
            using NativeFixture fixture = new();
            Auto<DisposableBuffer> native = fixture.Holder.GetBuffer();
            native.Get(fixture.Commands, 0, 65536);
            fixture.Holder.Dispose();
            fixture.Diagnostics.Publish();
            using (JsonDocument pending = JsonDocument.Parse(fixture.Diagnostics.GetSnapshot()))
            {
                Assert.That(pending.RootElement.GetProperty("active_logical_count").GetInt64(), Is.Zero);
                Assert.That(pending.RootElement.GetProperty("native_alive_count").GetInt64(), Is.EqualTo(1));
                Assert.That(pending.RootElement.GetProperty("dispose_pending_logical_bytes").GetInt64(), Is.EqualTo(65536));
                Assert.That(pending.RootElement.GetProperty("native_alive_owned_logical_bytes").GetInt64(), Is.Zero);
                Assert.That(pending.RootElement.GetProperty("native_alive_alias_logical_bytes").GetInt64(), Is.EqualTo(65536));
            }
            Assert.That(fixture.Destroyed, Is.Zero);
            fixture.RetireCommands();
            fixture.Holder.Dispose(); // Existing ordinary/sparse disposal can be repeated; diagnostics remain once-only.
            fixture.Diagnostics.Publish();
            using JsonDocument final = JsonDocument.Parse(fixture.Diagnostics.GetSnapshot());
            Assert.That(fixture.Destroyed, Is.EqualTo(1));
            Assert.That(final.RootElement.GetProperty("dispose_pending_count").GetInt64(), Is.Zero);
            Assert.That(final.RootElement.GetProperty("native_alive_count").GetInt64(), Is.Zero);
            Assert.That(final.RootElement.GetProperty("cumulative").GetProperty("dispose_requested").GetProperty("count").GetInt64(), Is.EqualTo(1));
            Assert.That(final.RootElement.GetProperty("cumulative").GetProperty("native_destroyed").GetProperty("count").GetInt64(), Is.EqualTo(1));
            Assert.That(native.GetUnsafe().Value.Handle, Is.Zero);
        }

        private sealed unsafe class NativeFixture : IDisposable
        {
            [UnmanagedFunctionPointer(CallingConvention.Winapi)]
            private delegate void DestroyDelegate(Device device, VkBuffer buffer, AllocationCallbacks* allocator);
            private readonly DestroyDelegate _destroy;
            private readonly Vk _api;
            private readonly List<IAuto> _dependants = [];
            private readonly List<MultiFenceHolder> _waitables = [];
            internal readonly BufferLifecycleDiagnostics Diagnostics = new();
            internal readonly BufferHolder Holder;
            internal readonly CommandBufferScoped Commands;
            internal int Destroyed;

            internal NativeFixture()
            {
                _destroy = (_, _, _) => Destroyed++;
                _api = new Vk(new LamdaNativeContext(name => name == "vkDestroyBuffer"
                    ? Marshal.GetFunctionPointerForDelegate(_destroy) : throw new InvalidOperationException(name)));
                VulkanRenderer renderer = (VulkanRenderer)RuntimeHelpers.GetUninitializedObject(typeof(VulkanRenderer));
                Set(renderer, "<Api>k__BackingField", _api);
                Holder = new BufferHolder(renderer, default, new VkBuffer(42), 65536, [], Diagnostics);
                CommandBufferPool pool = (CommandBufferPool)RuntimeHelpers.GetUninitializedObject(typeof(CommandBufferPool));
                Type entryType = typeof(CommandBufferPool).GetNestedType("ReservedCommandBuffer", BindingFlags.NonPublic);
                object entry = Activator.CreateInstance(entryType);
                entryType.GetField("Dependants").SetValue(entry, _dependants);
                entryType.GetField("Waitables").SetValue(entry, _waitables);
                Array entries = Array.CreateInstance(entryType, 1);
                entries.SetValue(entry, 0);
                Set(pool, "_commandBuffers", entries);
                Commands = new CommandBufferScoped(pool, default, 0);
            }

            internal void RetireCommands()
            {
                foreach (IAuto dependant in _dependants) dependant.DecrementReferenceCount(0);
                foreach (MultiFenceHolder waitable in _waitables) waitable.RemoveBufferUses(0);
                _dependants.Clear();
                _waitables.Clear();
            }

            public void Dispose()
            {
                RetireCommands();
                Holder.Dispose();
                _api.Dispose();
                GC.KeepAlive(_destroy);
            }

            private static void Set(object owner, string field, object value) =>
                owner.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(owner, value);
        }
    }
}
