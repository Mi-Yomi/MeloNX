using NUnit.Framework;
using Ryujinx.Common.Memory;
using Ryujinx.Cpu.Jit;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Gpu;
using Ryujinx.Graphics.Gpu.Image;
using Ryujinx.Graphics.Gpu.Memory;
using Ryujinx.Graphics.Texture;
using Ryujinx.Memory;
using Ryujinx.Memory.Range;
using Ryujinx.Memory.Tracking;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Reflection;

namespace Ryujinx.Tests.Graphics
{
    public class TextureGroupLifecycleTests
    {
        private sealed class TestHostTexture : ITexture
        {
            private readonly Action _releaseAction;

            public int Width => 1;
            public int Height => 1;
            public int ReleaseCount { get; private set; }
            public List<(BufferRange Range, int Layer, int Level, int Stride)> BufferCopies { get; } = [];

            public TestHostTexture(Action releaseAction = null)
            {
                _releaseAction = releaseAction;
            }

            public void CopyTo(ITexture destination, int firstLayer, int firstLevel) => throw new NotSupportedException();
            public void CopyTo(ITexture destination, int srcLayer, int dstLayer, int srcLevel, int dstLevel) => throw new NotSupportedException();
            public void CopyTo(ITexture destination, Extents2D srcRegion, Extents2D dstRegion, bool linearFilter) => throw new NotSupportedException();
            public void CopyTo(BufferRange range, int layer, int level, int stride) => BufferCopies.Add((range, layer, level, stride));
            public ITexture CreateView(TextureCreateInfo info, int firstLayer, int firstLevel) => throw new NotSupportedException();
            public PinnedSpan<byte> GetData() => throw new NotSupportedException();
            public PinnedSpan<byte> GetData(int layer, int level) => throw new NotSupportedException();
            public void SetData(MemoryOwner<byte> data) => throw new NotSupportedException();
            public void SetData(MemoryOwner<byte> data, int layer, int level) => throw new NotSupportedException();
            public void SetData(MemoryOwner<byte> data, int layer, int level, Rectangle<int> region) => throw new NotSupportedException();
            public void SetStorage(BufferRange buffer) => throw new NotSupportedException();

            public void Release()
            {
                ReleaseCount++;
                _releaseAction?.Invoke();
            }
        }

        private static (GpuContext Context, Texture Texture, TestHostTexture HostTexture) CreateTexture(Action releaseAction = null)
        {
            GpuContext context = (GpuContext)RuntimeHelpers.GetUninitializedObject(typeof(GpuContext));
            TextureInfo info = new(
                gpuAddress: 0,
                width: 1,
                height: 1,
                depthOrLayers: 1,
                levels: 1,
                samplesInX: 1,
                samplesInY: 1,
                stride: 4,
                isLinear: true,
                gobBlocksInY: 1,
                gobBlocksInZ: 1,
                gobBlocksInTileX: 1,
                target: Target.TextureBuffer,
                formatInfo: FormatInfo.Default);

            Texture texture = new(
                context,
                null,
                info,
                new SizeInfo(4),
                new MultiRange(0, 4UL),
                TextureScaleMode.Blacklisted);

            texture.InitializeGroup(false, false, new List<TextureIncompatibleOverlap>());

            TestHostTexture hostTexture = new(releaseAction);
            typeof(Texture)
                .GetProperty(nameof(Texture.HostTexture))
                .SetValue(texture, hostTexture);

            return (context, texture, hostTexture);
        }

        private static TextureGroupHandle CreateHandle(TextureGroup group)
        {
            return new TextureGroupHandle(
                group,
                offset: 0,
                size: group.Storage.Size,
                views: null,
                firstLayer: 0,
                firstLevel: 0,
                baseSlice: 0,
                sliceCount: 1,
                handles: Array.Empty<RegionHandle>());
        }

        [Test]
        public void PostDisposeSyncAndSignalActionsAreNoOps()
        {
            var created = CreateTexture();
            GpuContext context = created.Context;
            Texture texture = created.Texture;
            TextureGroup group = texture.Group;
            TextureGroupHandle handle = CreateHandle(group);

            group.Dispose();

            Assert.Multiple(() =>
            {
                Assert.That(group.IsDisposed, Is.True);
                Assert.That(group.TryFlushIntoBuffer(handle), Is.False);
                Assert.DoesNotThrow(() => handle.SignalModified(context));
                Assert.DoesNotThrow(() => handle.SignalModifying(false, context));
                Assert.That(handle.Modified, Is.False);
                Assert.That(handle.Sync(context), Is.False);
                Assert.That(handle.SyncPreAction(syncpoint: true), Is.True);
                Assert.That(handle.SyncAction(syncpoint: true), Is.True);
            });

            handle.Dispose();
            texture.Dispose();
        }

        [Test]
        public void TextureGroupDisposeIsIdempotent()
        {
            Texture texture = CreateTexture().Texture;
            TextureGroup group = texture.Group;

            Assert.DoesNotThrow(group.Dispose);
            Assert.DoesNotThrow(group.Dispose);
            Assert.That(group.IsDisposed, Is.True);

            texture.Dispose();
        }

        [TestCase(false)]
        [TestCase(true)]
        public void FlushBufferUnmapThenDisposeDeletesOnceAndRejectsLateCopy(bool imported)
        {
            var created = CreateTexture();
            AuditTestRenderer renderer = new();
            const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(GpuContext).GetField("<Renderer>k__BackingField", fields).SetValue(created.Context, renderer);
            BufferHandle buffer = renderer.CreateBuffer(16, BufferAccess.Default);
            TextureGroup group = created.Texture.Group;
            typeof(TextureGroup).GetField("_flushBuffer", fields).SetValue(group, buffer);
            typeof(TextureGroup).GetField("_flushBufferImported", fields).SetValue(group, imported);
            group.Unmapped();
            group.Dispose();
            group.Unmapped();
            group.Dispose();
            TextureGroupHandle handle = CreateHandle(group);
            Assert.That(group.TryFlushIntoBuffer(handle), Is.False);
            Assert.That(renderer.Buffers, Is.Empty);
            Assert.That(renderer.Events.Count, Is.EqualTo(2));
            handle.Dispose();
            created.Texture.Dispose();
        }

        [Test]
        public void RejectedHostMappingSelectsOrdinaryReadbackBeforeCopyAndKeepsZeroStride()
        {
            using MemoryBlock backing = new(0x4000, MemoryAllocationFlags.Reserve);
            MemoryManagerHostTracked memory = new(backing, 1UL << 32, false, null);
            memory.IncrementReferenceCount();
            var created = CreateTexture();
            TextureGroupHandle handle = null;
            AuditTestRenderer renderer = new();

            try
            {
                memory.MapZeroed(0, 0x1000, 0x1000);
                const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
                PhysicalMemory physical = (PhysicalMemory)RuntimeHelpers.GetUninitializedObject(typeof(PhysicalMemory));
                typeof(PhysicalMemory).GetField("_cpuMemory", fields).SetValue(physical, memory);
                typeof(GpuContext).GetField("<Renderer>k__BackingField", fields).SetValue(created.Context, renderer);
                TextureGroup group = created.Texture.Group;
                typeof(TextureGroup).GetField("_physicalMemory", fields).SetValue(group, physical);

                int preparationCalls = 0;
                nint preparedPointer = 0;
                ulong preparedSize = 0;
                renderer.PrepareHostMappingHandler = (pointer, size) =>
                {
                    preparationCalls++;
                    preparedPointer = pointer;
                    preparedSize = size;
                    return false;
                };

                handle = CreateHandle(group);
                Assert.That(group.TryFlushIntoBuffer(handle), Is.True);
                Assert.That(preparedPointer, Is.Not.EqualTo(nint.Zero), "Exercise an available guest pointer, not the unmapped-range bypass.");
                Assert.That(preparedSize, Is.EqualTo(created.Texture.Size));
                Assert.That(preparationCalls, Is.EqualTo(1));
                Assert.That(renderer.LastBufferAccess, Is.EqualTo(BufferAccess.HostMemory));
                Assert.That(renderer.Buffers, Has.Count.EqualTo(1));
                Assert.That((bool)typeof(TextureGroup).GetField("_flushBufferImported", fields).GetValue(group), Is.False);

                // Ordinary buffers remain valid across guest remaps. A second flush must
                // retain that path; imported-buffer creation in this renderer always throws.
                group.Unmapped();
                Assert.That(group.TryFlushIntoBuffer(handle), Is.True);
                Assert.That(preparationCalls, Is.EqualTo(1));
                Assert.That(renderer.Events, Has.Count.EqualTo(1));
                Assert.That(created.HostTexture.BufferCopies, Has.Count.EqualTo(2));
                BufferHandle firstHandle = created.HostTexture.BufferCopies[0].Range.Handle;
                foreach (var copy in created.HostTexture.BufferCopies)
                {
                    Assert.Multiple(() =>
                    {
                        Assert.That(copy.Range.Handle, Is.EqualTo(firstHandle));
                        Assert.That(renderer.Buffers.ContainsKey(copy.Range.Handle), Is.True);
                        Assert.That(copy.Range.Offset, Is.Zero);
                        Assert.That(copy.Range.Size, Is.EqualTo(4));
                        Assert.That(copy.Layer, Is.Zero);
                        Assert.That(copy.Level, Is.Zero);
                        Assert.That(copy.Stride, Is.Zero, "Copied readback must not use the imported guest-memory stride.");
                    });
                }
            }
            finally
            {
                handle?.Dispose();
                created.Texture.Dispose();
                memory.DecrementReferenceCount();
            }

            Assert.That(renderer.Buffers, Is.Empty);
            Assert.That(renderer.Events, Has.Count.EqualTo(2), "One ordinary allocation and one release.");
        }

        [Test]
        public void TextureDisposesGroupBeforeReleasingHostTexture()
        {
            Texture texture = null;
            bool? groupDisposedAtRelease = null;

            var created = CreateTexture(() =>
            {
                groupDisposedAtRelease = texture.Group.IsDisposed;
            });
            texture = created.Texture;
            TestHostTexture hostTexture = created.HostTexture;

            texture.Dispose();

            Assert.Multiple(() =>
            {
                Assert.That(hostTexture.ReleaseCount, Is.EqualTo(1));
                Assert.That(groupDisposedAtRelease, Is.True);
            });
        }
    }
}
