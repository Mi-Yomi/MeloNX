using NUnit.Framework;
using Ryujinx.Common.Memory;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Gpu;
using Ryujinx.Graphics.Gpu.Image;
using Ryujinx.Graphics.Texture;
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

            public TestHostTexture(Action releaseAction = null)
            {
                _releaseAction = releaseAction;
            }

            public void CopyTo(ITexture destination, int firstLayer, int firstLevel) => throw new NotSupportedException();
            public void CopyTo(ITexture destination, int srcLayer, int dstLayer, int srcLevel, int dstLevel) => throw new NotSupportedException();
            public void CopyTo(ITexture destination, Extents2D srcRegion, Extents2D dstRegion, bool linearFilter) => throw new NotSupportedException();
            public void CopyTo(BufferRange range, int layer, int level, int stride) => throw new NotSupportedException();
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
