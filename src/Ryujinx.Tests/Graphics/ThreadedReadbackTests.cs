using NUnit.Framework;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.GAL.Multithreading;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Ryujinx.Tests.Graphics
{
    public class ThreadedReadbackTests
    {
        [TestCase(0)]
        [TestCase(24000)]
        public void ExternalReadbackDoesNotBecomeSecondQueueProducer(int producerCopies)
        {
            AuditTestRenderer backend = new();
            ThreadedRenderer renderer = new(backend);
            using ManualResetEventSlim ready = new(false), stop = new(false), produced = new(false);
            BufferHandle source = default, target = default, mainSource = default, mainTarget = default;
            Exception failure = null;
            Thread owner = new(() => renderer.RunLoop(() =>
            {
                try
                {
                    source = renderer.CreateBuffer(65536, BufferAccess.Default);
                    target = renderer.CreateBuffer(65536, BufferAccess.Default);
                    mainSource = renderer.CreateBuffer(64, BufferAccess.Default);
                    mainTarget = renderer.CreateBuffer(64, BufferAccess.Default);
                    renderer.SetBufferData(source, 0, Enumerable.Repeat((byte)0x7b, 65536).ToArray());
                    renderer.SetBufferData(target, 0, Enumerable.Repeat((byte)0x22, 65536).ToArray());
                    using (renderer.GetBufferData(source, 0, 1)) { }
                    ready.Set();
                    for (int i = 0; i < producerCopies; i++)
                    {
                        renderer.Pipeline.CopyBuffer(mainSource, mainTarget, 0, 0, 4);
                    }
                    produced.Set();
                    stop.Wait();
                    renderer.DeleteBuffer(source);
                    renderer.DeleteBuffer(target);
                    renderer.DeleteBuffer(mainSource);
                    renderer.DeleteBuffer(mainTarget);
                }
                catch (Exception error) { failure = error; ready.Set(); produced.Set(); }
            })) { IsBackground = true };
            owner.Start();
            Assert.That(ready.Wait(TimeSpan.FromSeconds(15)), Is.True);
            Assert.That(failure, Is.Null);
            try
            {
                Task readbacks = Task.Run(() =>
                {
                    for (int i = 0; i < 128; i++)
                        renderer.CopyBufferForReadback(source, target, 61440, 61440, 4096);
                });
                Assert.That(readbacks.Wait(TimeSpan.FromSeconds(15)), Is.True, "Background reconciliation deadlocked");
                Assert.That(produced.Wait(TimeSpan.FromSeconds(15)), Is.True);
                using PinnedSpan<byte> output = renderer.GetBufferData(target, 0, 65536);
                Assert.That(output.Get()[..61440].ToArray(), Is.All.EqualTo((byte)0x22));
                Assert.That(output.Get()[61440..].ToArray(), Is.All.EqualTo((byte)0x7b));
            }
            finally
            {
                stop.Set();
                renderer.Dispose();
            }
            Assert.That(owner.Join(TimeSpan.FromSeconds(2)), Is.True);
            Assert.That(failure, Is.Null);
            Assert.That(backend.Copies.Count, Is.EqualTo(producerCopies + 128));
            Assert.That(backend.Events.Select(e => e.Thread).Distinct(), Is.EqualTo(new[] { owner.ManagedThreadId }));
            Assert.That(backend.Buffers, Is.Empty);
        }

        [Test]
        public void FailedReadbackInterruptWakesCallerAndDoesNotPoisonNextInterrupt()
        {
            AuditTestRenderer backend = new();
            ThreadedRenderer renderer = new(backend);
            using ManualResetEventSlim ready = new(false), stop = new(false);
            BufferHandle source = default, target = default;
            Thread owner = new(() => renderer.RunLoop(() =>
            {
                source = renderer.CreateBuffer(16, BufferAccess.Default);
                target = renderer.CreateBuffer(16, BufferAccess.Default);
                using (renderer.GetBufferData(source, 0, 1)) { }
                ready.Set();
                stop.Wait();
                renderer.DeleteBuffer(source);
                renderer.DeleteBuffer(target);
            })) { IsBackground = true };
            owner.Start();
            Assert.That(ready.Wait(TimeSpan.FromSeconds(15)), Is.True);
            try
            {
                Task attempt = Task.Run(() =>
                {
                    Assert.Throws<ArgumentOutOfRangeException>(() => renderer.CopyBufferForReadback(source, target, 0, 0, 17));
                    renderer.CopyBufferForReadback(source, target, 0, 0, 16);
                });
                Assert.That(attempt.Wait(TimeSpan.FromSeconds(15)), Is.True);
            }
            finally
            {
                stop.Set();
                renderer.Dispose();
            }
            Assert.That(owner.Join(TimeSpan.FromSeconds(2)), Is.True);
            Assert.That(backend.Copies.Count, Is.EqualTo(1));
            Assert.That(backend.Events.Where(e => e.Operation == "copy").All(e => e.Thread == owner.ManagedThreadId), Is.True);
        }
    }
}
