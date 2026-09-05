using NUnit.Framework;
using Ryujinx.Common.Memory;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.GAL.Multithreading;
using System;
using System.Linq;
using System.Threading;

namespace Ryujinx.Tests.Graphics
{
    public class ThreadedTextureLifetimeTests
    {
        [TestCase(1)]
        [TestCase(24000)]
        public void CopyQueuedBeforeReleaseAndDeleteSeesLiveTextureAndBuffer(int count)
        {
            AuditTestRenderer backend = new();
            ThreadedRenderer renderer = new(backend);
            using ManualResetEventSlim producerDone = new(false);
            Exception failure = null;
            Thread owner = new(() => renderer.RunLoop(() =>
            {
                try
                {
                    ITexture texture = renderer.CreateTexture(default);
                    BufferHandle target = renderer.CreateBuffer(16, BufferAccess.Default);
                    for (int i = 0; i < count; i++)
                        texture.CopyTo(new BufferRange(target, 0, 16), 0, 0, 4);
                    texture.Release();
                    texture.Release();
                    Assert.Throws<ObjectDisposedException>(() => texture.CopyTo(new BufferRange(target, 0, 16), 0, 0, 4));
                    renderer.DeleteBuffer(target);
                }
                catch (Exception error) { failure = error; }
                finally { producerDone.Set(); }
            })) { IsBackground = true };
            owner.Start();
            Assert.That(producerDone.Wait(TimeSpan.FromSeconds(15)), Is.True);
            renderer.Dispose();
            Assert.That(owner.Join(TimeSpan.FromSeconds(2)), Is.True);
            Assert.That(failure, Is.Null);
            var events = backend.Events.ToArray();
            Assert.That(events.Count(e => e.Operation == "texture_copy"), Is.EqualTo(count));
            Assert.That(events.TakeLast(3).Select(e => e.Operation), Is.EqualTo(new[] { "texture_release", "delete", "dispose" }));
            Assert.That(events.Select(e => e.Thread).Distinct(), Is.EqualTo(new[] { owner.ManagedThreadId }));
        }
    }

    internal sealed class AuditTestTexture : ITexture
    {
        private readonly AuditTestRenderer _renderer;
        private bool _released;
        public int Width => 1;
        public int Height => 1;
        public AuditTestTexture(AuditTestRenderer renderer) => _renderer = renderer;
        public void CopyTo(BufferRange range, int layer, int level, int stride)
        {
            if (_released) throw new InvalidOperationException("Copy after texture release");
            _renderer.Buffers[range.Handle].AsSpan(range.Offset, range.Size).Fill(0x7b);
            _renderer.Events.Enqueue(("texture_copy", Environment.CurrentManagedThreadId));
        }
        public void Release()
        {
            if (_released) throw new InvalidOperationException("Double texture release");
            _released = true;
            _renderer.Events.Enqueue(("texture_release", Environment.CurrentManagedThreadId));
        }
        public void CopyTo(ITexture destination, int firstLayer, int firstLevel) => throw new NotSupportedException();
        public void CopyTo(ITexture destination, int srcLayer, int dstLayer, int srcLevel, int dstLevel) => throw new NotSupportedException();
        public void CopyTo(ITexture destination, Extents2D srcRegion, Extents2D dstRegion, bool linearFilter) => throw new NotSupportedException();
        public ITexture CreateView(TextureCreateInfo info, int firstLayer, int firstLevel) => throw new NotSupportedException();
        public PinnedSpan<byte> GetData() => throw new NotSupportedException();
        public PinnedSpan<byte> GetData(int layer, int level) => throw new NotSupportedException();
        public void SetData(MemoryOwner<byte> data) => throw new NotSupportedException();
        public void SetData(MemoryOwner<byte> data, int layer, int level) => throw new NotSupportedException();
        public void SetData(MemoryOwner<byte> data, int layer, int level, Rectangle<int> region) => throw new NotSupportedException();
        public void SetStorage(BufferRange buffer) => throw new NotSupportedException();
    }
}
