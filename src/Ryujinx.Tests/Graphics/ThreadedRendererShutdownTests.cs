using NUnit.Framework;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.GAL.Multithreading;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Ryujinx.Tests.Graphics
{
    public class ThreadedRendererShutdownTests
    {
        [TestCase(0)]
        [TestCase(64)]
        [TestCase(12000)]
        public void LateDeletesDrainBeforeBackendDisposalOnOwnerThread(int copies)
        {
            AuditTestRenderer backend = new();
            ThreadedRenderer renderer = new(backend);
            using ManualResetEventSlim producerDone = new(false);
            BufferHandle first = default, second = default;
            Exception producerError = null, backendError = null;
            int producerThread = 0;
            Thread owner = new(() =>
            {
                try
                {
                    renderer.RunLoop(() =>
                    {
                        producerThread = Environment.CurrentManagedThreadId;
                        try
                        {
                            first = renderer.CreateBuffer(16, BufferAccess.Default);
                            second = renderer.CreateBuffer(16, BufferAccess.Default);
                            renderer.SetBufferData(first, 0, new byte[] { 1, 2, 3, 4 });
                            for (int i = 0; i < copies; i++) renderer.Pipeline.CopyBuffer(first, second, 0, 0, 4);
                        }
                        catch (Exception error) { producerError = error; }
                        finally { producerDone.Set(); }
                    });
                }
                catch (Exception error) { backendError = error; }
            }) { IsBackground = true };
            owner.Start();
            Assert.That(producerDone.Wait(TimeSpan.FromSeconds(15)), Is.True);
            Task teardown = Task.Run(() =>
            {
                renderer.DeleteBuffer(first);
                renderer.DeleteBuffer(second);
                renderer.Dispose();
                renderer.Dispose();
            });
            Assert.That(teardown.Wait(TimeSpan.FromSeconds(15)), Is.True, "Teardown deadlocked");
            Assert.That(owner.Join(TimeSpan.FromSeconds(1)), Is.True);
            Assert.That(producerError, Is.Null);
            Assert.That(backendError, Is.Null);
            Assert.That(backend.Buffers, Is.Empty);
            var events = backend.Events.ToArray();
            Assert.That(events.Count(e => e.Operation == "copy"), Is.EqualTo(copies));
            Assert.That(events.TakeLast(3).Select(e => e.Operation), Is.EqualTo(new[] { "delete", "delete", "dispose" }));
            Assert.That(events.Count(e => e.Operation == "dispose"), Is.EqualTo(1));
            Assert.That(events.Select(e => e.Thread).Distinct().ToArray(), Is.EqualTo(new[] { owner.ManagedThreadId }));
            Assert.That(producerThread, Is.Not.EqualTo(owner.ManagedThreadId));
        }
    }
}
