using NUnit.Framework;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.GAL.Multithreading;
using System;
using System.Linq;
using System.Threading;

namespace Ryujinx.Tests.Graphics
{
    public class ThreadedRendererIdleTests
    {
        [Test]
        public void IdleServiceUnblocksProducerWithoutAnotherCommandOnBackendOwnerThread()
        {
            AuditTestRenderer backend = new();
            ThreadedRenderer renderer = new(backend);
            using ManualResetEventSlim idleCompleted = new(false);
            using ManualResetEventSlim producerDone = new(false);
            int pending = 0;
            backend.IdleHandler = () =>
            {
                if (Interlocked.Exchange(ref pending, 0) != 0)
                {
                    backend.Events.Enqueue(("idle", Environment.CurrentManagedThreadId));
                    idleCompleted.Set();
                }
            };
            Exception producerError = null, backendError = null;
            Thread owner = new(() =>
            {
                try
                {
                    renderer.RunLoop(() =>
                    {
                        try
                        {
                            BufferHandle first = renderer.CreateBuffer(16, BufferAccess.Default);
                            BufferHandle second = renderer.CreateBuffer(16, BufferAccess.Default);
                            renderer.Pipeline.CopyBuffer(first, second, 0, 0, 16);
                            renderer.BackgroundContextAction(() => Volatile.Write(ref pending, 1));
                            // No more GAL commands until the backend services its pending work.
                            Assert.That(idleCompleted.Wait(TimeSpan.FromSeconds(2)), Is.True);
                            renderer.DeleteBuffer(first);
                            renderer.DeleteBuffer(second);
                        }
                        catch (Exception error) { producerError = error; }
                        finally { producerDone.Set(); }
                    });
                }
                catch (Exception error) { backendError = error; }
            }) { IsBackground = true };
            owner.Start();
            try
            {
                Assert.That(producerDone.Wait(TimeSpan.FromSeconds(5)), Is.True);
            }
            finally
            {
                idleCompleted.Set();
                Assert.That(producerDone.Wait(TimeSpan.FromSeconds(5)), Is.True);
                renderer.Dispose();
            }
            Assert.That(owner.Join(TimeSpan.FromSeconds(2)), Is.True);
            Assert.That(producerError, Is.Null);
            Assert.That(backendError, Is.Null);
            var events = backend.Events.ToArray();
            Assert.That(events.Select(e => e.Operation), Is.EqualTo(new[] { "create", "create", "copy", "idle", "delete", "delete", "dispose" }));
            Assert.That(events.Select(e => e.Thread).Distinct(), Is.EqualTo(new[] { owner.ManagedThreadId }));
        }

        [Test]
        public void IdleServiceCannotInterleaveWithACommandOrRunOnTheProducer()
        {
            AuditTestRenderer backend = new();
            ThreadedRenderer renderer = new(backend);
            using ManualResetEventSlim commandEntered = new(false);
            using ManualResetEventSlim commandExit = new(false);
            using ManualResetEventSlim producerDone = new(false);
            using ManualResetEventSlim idleCompleted = new(false);
            int armed = 0, active = 0, overlapping = 0;
            Exception producerError = null, backendError = null;
            backend.IdleHandler = () =>
            {
                if (Volatile.Read(ref armed) == 0) return;
                if (Volatile.Read(ref active) != 0) Interlocked.Increment(ref overlapping);
                idleCompleted.Set();
            };
            Thread owner = new(() =>
            {
                try
                {
                    renderer.RunLoop(() =>
                    {
                        try
                        {
                            renderer.BackgroundContextAction(() =>
                            {
                                Volatile.Write(ref active, 1);
                                Volatile.Write(ref armed, 1);
                                commandEntered.Set();
                                if (!commandExit.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
                                Volatile.Write(ref active, 0);
                            });
                            Assert.That(idleCompleted.Wait(TimeSpan.FromSeconds(2)), Is.True);
                        }
                        catch (Exception error) { producerError = error; }
                        finally { producerDone.Set(); }
                    });
                }
                catch (Exception error) { backendError = error; }
            }) { IsBackground = true };
            owner.Start();
            try
            {
                Assert.That(commandEntered.Wait(TimeSpan.FromSeconds(2)), Is.True);
                Assert.That(renderer.GetDiagnosticSnapshot(), Does.Contain("active_command=Action"));
                // The frontend idle call is deliberately a no-op on ThreadedRenderer.
                ((IRenderer)renderer).FlushPendingCommands();
                Assert.That(idleCompleted.IsSet, Is.False);
            }
            finally
            {
                commandExit.Set();
                Assert.That(producerDone.Wait(TimeSpan.FromSeconds(5)), Is.True);
                renderer.Dispose();
            }
            Assert.That(owner.Join(TimeSpan.FromSeconds(2)), Is.True);
            Assert.That(overlapping, Is.Zero);
            Assert.That(producerError, Is.Null);
            Assert.That(backendError, Is.Null);
        }
    }
}
