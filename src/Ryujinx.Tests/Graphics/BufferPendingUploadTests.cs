using NUnit.Framework;
using Ryujinx.Graphics.Vulkan;
using Silk.NET.Core.Contexts;
using Silk.NET.Vulkan;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace Ryujinx.Tests.Graphics
{
    public class BufferPendingUploadTests
    {
        [Test]
        public void SlowUploadEndsPassAndRetainsSourceWithoutFlushingCallerSubmission()
        {
            using Fixture fixture = new();
            byte[] bytes = [4, 5, 6]; // Unaligned update must be valid on the slow path.
            fixture.SetDisposalWeight(256 * 1024 * 1024);
            BufferHolder.UploadTransientBuffer(fixture.Renderer, fixture.Commands, fixture.EndPass,
                fixture.Source, fixture.Destination, 1, bytes);

            Assert.That(fixture.Events, Is.EqualTo(new[] { "end", "barrier", "copy", "barrier" }));
            Assert.That(fixture.CopyInsideRenderPass, Is.False);
            Assert.That(fixture.ReadDestination(1, 3), Is.EqualTo(bytes));
            Assert.That(fixture.DisposalWeight, Is.EqualTo(256 * 1024 * 1024UL + 256));
            Assert.That(fixture.DestroyedSources, Is.Zero);
            Assert.That(fixture.Source.GetBuffer().HasCommandBufferDependency(fixture.Commands), Is.True);
            fixture.RetireCommands();
            Assert.That(fixture.DestroyedSources, Is.EqualTo(1));
        }

        [Test]
        public void SlowUploadFailureReleasesUnsubmittedSourceOnce()
        {
            using Fixture fixture = new();
            Assert.Throws<InvalidOperationException>(() => BufferHolder.UploadTransientBuffer(
                fixture.Renderer, fixture.Commands, () => throw new InvalidOperationException("end-pass failed"),
                fixture.Source, fixture.Destination, 1, new byte[] { 1, 2, 3 }));
            Assert.That(fixture.Events, Is.Empty);
            Assert.That(fixture.DestroyedSources, Is.EqualTo(1));
            fixture.Source.Dispose();
            Assert.That(fixture.DestroyedSources, Is.EqualTo(1));
        }

        [TestCase(-1)]
        [TestCase(int.MaxValue)]
        public void ClearMirrorsNormalizesWholeSizeAndOversizedBindingBeforeSparseUpload(int range)
        {
            using Fixture fixture = new();
            fixture.SetPending(new PendingBufferData(256));
            fixture.Pending.Write(248, new byte[] { 9, 8, 7, 6 });
            BufferHolder cached = fixture.AddCachedConversion();
            fixture.SetDisposalWeight(256 * 1024 * 1024);
            // No command references: the actual BufferHolder.SetData callback takes
            // its mapped write path and reenters PendingBufferData.Remove.
            fixture.Destination.ClearMirrors(fixture.Commands, 240, range);
            Assert.That(fixture.ReadDestination(248, 4), Is.EqualTo(new byte[] { 9, 8, 7, 6 }));
            Assert.That(fixture.Pending.HasData, Is.False);
            Assert.That(fixture.Pending.PageCount, Is.Zero);
            Assert.That(fixture.CachedKeyDisposed, Is.True);
            Assert.That(fixture.DisposalWeight, Is.EqualTo(256 * 1024 * 1024UL + 256));
            Assert.That(fixture.Pipeline.RegisterDisposalWeight(cached.GetBuffer(), 0), Is.True,
                "The next ordinary disposal must request a flush after the upload scope exits.");
        }

        [Test]
        public void NestedDisposalScopesDoNotFlushWhenInnerUploadReturns()
        {
            using Fixture fixture = new();
            fixture.AddCachedConversion();
            fixture.SetDisposalWeight(256 * 1024 * 1024);
            using (fixture.Pipeline.DeferDisposalFlushes())
            {
                BufferHolder.UploadTransientBuffer(fixture.Renderer, fixture.Commands, fixture.EndPass,
                    fixture.Source, fixture.Destination, 1, new byte[] { 1, 2, 3 });
                Assert.That(fixture.CachedKeyDisposed, Is.True);
                Assert.That(fixture.Pipeline.RegisterDisposalWeight(fixture.Destination.GetBuffer(), 0), Is.False);
            }
            Assert.That(fixture.DisposalWeight, Is.EqualTo(256 * 1024 * 1024UL + 512));
            Assert.That(fixture.Pipeline.RegisterDisposalWeight(fixture.Destination.GetBuffer(), 0), Is.True);
        }

        [TestCase(-1, 16)]
        [TestCase(256, -1)]
        [TestCase(0, -2)]
        [TestCase(0, 0)]
        public void InvalidOrEmptyBindingDoesNotConsumePendingBytes(int offset, int size)
        {
            using Fixture fixture = new();
            fixture.SetPending(new PendingBufferData(256));
            fixture.Pending.Write(8, new byte[] { 1, 2, 3 });
            fixture.Destination.ClearMirrors(fixture.Commands, offset, size);
            Assert.That(fixture.Pending.HasData, Is.True);
            Assert.That(fixture.ReadDestination(8, 3), Is.EqualTo(new byte[3]));
        }

        private sealed unsafe class Fixture : IDisposable
        {
            [UnmanagedFunctionPointer(CallingConvention.Winapi)]
            private delegate void DestroyBufferDelegate(Device device, VkBuffer buffer, AllocationCallbacks* allocator);
            [UnmanagedFunctionPointer(CallingConvention.Winapi)]
            private delegate void BarrierDelegate(CommandBuffer commandBuffer, PipelineStageFlags source, PipelineStageFlags destination,
                DependencyFlags flags, uint memoryCount, MemoryBarrier* memory, uint bufferCount, BufferMemoryBarrier* buffers,
                uint imageCount, ImageMemoryBarrier* images);
            [UnmanagedFunctionPointer(CallingConvention.Winapi)]
            private delegate void CopyDelegate(CommandBuffer commandBuffer, VkBuffer source, VkBuffer destination, uint count, BufferCopy* regions);

            private readonly DestroyBufferDelegate _destroy;
            private readonly BarrierDelegate _barrier;
            private readonly CopyDelegate _copy;
            private readonly Vk _api;
            private readonly PipelineFull _pipeline;
            private readonly List<IAuto> _dependants = [];
            private readonly List<MultiFenceHolder> _waitables = [];
            private readonly nint _sourceMemory = (nint)NativeMemory.AllocZeroed(256);
            private readonly nint _destinationMemory = (nint)NativeMemory.AllocZeroed(256);
            private bool _renderPassActive = true;
            public List<string> Events { get; } = [];
            public bool CopyInsideRenderPass { get; private set; }
            public int DestroyedSources { get; private set; }
            public bool CachedKeyDisposed { get; private set; }
            public PipelineFull Pipeline => _pipeline;
            public VulkanRenderer Renderer { get; }
            public BufferHolder Source { get; }
            public BufferHolder Destination { get; }
            public CommandBufferScoped Commands { get; }
            public PendingBufferData Pending { get; private set; }
            public ulong DisposalWeight => (ulong)typeof(PipelineFull).GetField("_byteWeight", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(_pipeline);

            public Fixture()
            {
                _destroy = (_, buffer, _) => { if (buffer.Handle == 42) DestroyedSources++; };
                _barrier = (_, _, _, _, _, _, _, _, _, _) => Events.Add("barrier");
                _copy = (_, _, _, _, regions) =>
                {
                    Events.Add("copy");
                    CopyInsideRenderPass |= _renderPassActive;
                    new ReadOnlySpan<byte>((void*)(_sourceMemory + (int)regions[0].SrcOffset), (int)regions[0].Size)
                        .CopyTo(new Span<byte>((void*)(_destinationMemory + (int)regions[0].DstOffset), (int)regions[0].Size));
                };
                _api = new Vk(new LamdaNativeContext(name => name switch
                {
                    "vkDestroyBuffer" => Marshal.GetFunctionPointerForDelegate(_destroy),
                    "vkCmdPipelineBarrier" => Marshal.GetFunctionPointerForDelegate(_barrier),
                    "vkCmdCopyBuffer" => Marshal.GetFunctionPointerForDelegate(_copy),
                    _ => throw new InvalidOperationException($"Unexpected native operation: {name}"),
                }));
                Renderer = (VulkanRenderer)RuntimeHelpers.GetUninitializedObject(typeof(VulkanRenderer));
                _pipeline = (PipelineFull)RuntimeHelpers.GetUninitializedObject(typeof(PipelineFull));
                Set(Renderer, "<Api>k__BackingField", _api);
                Set(Renderer, "_pipeline", _pipeline);
                Source = new BufferHolder(Renderer, default, new VkBuffer(42), 256, []);
                Destination = new BufferHolder(Renderer, default, new VkBuffer(43), 256, []);
                Set(Source, "_map", _sourceMemory);
                Set(Destination, "_map", _destinationMemory);

                CommandBufferPool pool = (CommandBufferPool)RuntimeHelpers.GetUninitializedObject(typeof(CommandBufferPool));
                Type entryType = typeof(CommandBufferPool).GetNestedType("ReservedCommandBuffer", BindingFlags.NonPublic);
                object entry = Activator.CreateInstance(entryType);
                entryType.GetField("Dependants").SetValue(entry, _dependants);
                entryType.GetField("Waitables").SetValue(entry, _waitables);
                Array entries = Array.CreateInstance(entryType, 1);
                entries.SetValue(entry, 0);
                Set(pool, "_commandBuffers", entries);
                Set(Renderer, "<CommandBufferPool>k__BackingField", pool);
                Commands = new CommandBufferScoped(pool, default, 0);
                typeof(PipelineBase).GetField("Cbs", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(_pipeline, Commands);
            }

            public void EndPass()
            {
                Events.Add("end");
                _renderPassActive = false;
            }

            public void SetDisposalWeight(ulong weight) => Set(_pipeline, "_byteWeight", weight);

            private sealed class CacheKey(Fixture fixture) : ICacheKey
            {
                public bool KeyEqual(ICacheKey other) => ReferenceEquals(this, other);
                public void Dispose() => fixture.CachedKeyDisposed = true;
            }

            public BufferHolder AddCachedConversion()
            {
                BufferHolder cached = new(Renderer, default, new VkBuffer(44), 256, []);
                cached.GetBuffer().Get(Commands, 0, 256);
                Destination.AddCachedConvertedBuffer(0, 256, new CacheKey(this), cached);
                return cached;
            }

            public void SetPending(PendingBufferData pending)
            {
                Pending = pending;
                Set(Destination, "_pendingData", pending);
                Set(Destination, "_mirrors", new Dictionary<ulong, StagingBufferReserved>());
            }

            public byte[] ReadDestination(int offset, int size) => new ReadOnlySpan<byte>((void*)(_destinationMemory + offset), size).ToArray();

            public void RetireCommands()
            {
                foreach (IAuto dependant in _dependants) dependant.DecrementReferenceCount(0);
                foreach (MultiFenceHolder waitable in _waitables)
                {
                    waitable.RemoveFence(0);
                    waitable.RemoveBufferUses(0);
                }
                _dependants.Clear();
                _waitables.Clear();
            }

            public void Dispose()
            {
                RetireCommands();
                Source.Dispose();
                Destination.Dispose();
                NativeMemory.Free((void*)_sourceMemory);
                NativeMemory.Free((void*)_destinationMemory);
                _api.Dispose();
                GC.KeepAlive(_destroy);
                GC.KeepAlive(_barrier);
                GC.KeepAlive(_copy);
            }

            private static void Set(object target, string name, object value) =>
                target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);
        }
    }
}
