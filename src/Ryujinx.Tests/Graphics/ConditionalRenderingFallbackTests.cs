using NUnit.Framework;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.GAL.Multithreading;
using System;
using System.Linq;
using System.Threading;

namespace Ryujinx.Tests.Graphics
{
    public class ConditionalRenderingFallbackTests
    {
        private sealed class CounterEvent : ICounterEvent
        {
            public bool Invalid { get; set; }
            public int Reservations { get; private set; }
            public int Releases { get; private set; }
            public int Flushes { get; private set; }
            public bool Reserved { get; private set; }
            public bool ReserveForHostAccess() { Reservations++; Reserved = true; return true; }
            public void Release() { Releases++; Reserved = false; }
            public void Flush() => Flushes++;
            public void Dispose() { }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ThreadedDecisionWaitsForBackendAndDoesNotReserveOnProducer(bool backendSupportsCondition)
        {
            AuditTestRenderer backend = new();
            ThreadedRenderer renderer = new(backend);
            CounterEvent nativeCounter = new();
            using ManualResetEventSlim backendEntered = new(false), releaseBackend = new(false);
            using ManualResetEventSlim producerReturned = new(false), producerDone = new(false);
            bool wasPreReserved = false, nativeEventMatched = false, backendWaitCompleted = false;
            bool actualDecision = !backendSupportsCondition;
            Exception producerFailure = null, backendFailure = null;
            backend.ReportCounterHandler = (_, _, _, hostReserved) =>
            {
                wasPreReserved = hostReserved;
                if (hostReserved) nativeCounter.ReserveForHostAccess();
                backend.Events.Enqueue(("counter_report", Environment.CurrentManagedThreadId));
                return nativeCounter;
            };
            backend.ConditionalRenderingHandler = (counter, compare, isEqual) =>
            {
                nativeEventMatched = ReferenceEquals(counter, nativeCounter) && compare == 0 && !isEqual;
                backend.Events.Enqueue(("conditional_decision", Environment.CurrentManagedThreadId));
                backendEntered.Set();
                backendWaitCompleted = releaseBackend.Wait(TimeSpan.FromSeconds(5));
                if (backendSupportsCondition) counter.ReserveForHostAccess();
                return backendSupportsCondition;
            };
            backend.EndConditionalRenderingHandler = () =>
            {
                nativeCounter.Release();
                backend.Events.Enqueue(("conditional_end", Environment.CurrentManagedThreadId));
            };

            Thread owner = new(() =>
            {
                try
                {
                    renderer.RunLoop(() =>
                    {
                        try
                        {
                            // This is exactly the old optimistic branch: clear
                            // samples-passed query compared with zero.
                            ICounterEvent counter = renderer.ReportCounter(CounterType.SamplesPassed, (_, _) => { }, 1, false);
                            actualDecision = renderer.Pipeline.TryHostConditionalRendering(counter, 0UL, false);
                            producerReturned.Set();
                            if (actualDecision) renderer.Pipeline.EndHostConditionalRendering();
                            else counter.Flush(); // The engine evaluates the completed value on the CPU.
                        }
                        catch (Exception exception) { producerFailure = exception; }
                        finally { producerDone.Set(); }
                    });
                }
                catch (Exception exception) { backendFailure = exception; }
            }) { IsBackground = true };

            bool returnedBeforeDecision = false;
            owner.Start();
            try
            {
                Assert.That(backendEntered.Wait(TimeSpan.FromSeconds(3)), Is.True);
                returnedBeforeDecision = producerReturned.Wait(TimeSpan.FromMilliseconds(50));
            }
            finally
            {
                releaseBackend.Set();
                // Always release the deliberately blocked backend before teardown;
                // the same test also completes on the incorrect optimistic baseline.
                renderer.Dispose();
            }

            Assert.That(owner.Join(TimeSpan.FromSeconds(2)), Is.True);
            Assert.That(producerDone.IsSet, Is.True);
            Assert.That(producerFailure, Is.Null);
            Assert.That(backendFailure, Is.Null);
            Assert.That(backendWaitCompleted, Is.True);
            Assert.That(returnedBeforeDecision, Is.False, "Producer guessed success before the backend's response.");
            Assert.That(actualDecision, Is.EqualTo(backendSupportsCondition));
            Assert.That(nativeEventMatched, Is.True);
            Assert.That(wasPreReserved, Is.False, "An unsupported native path must not inherit a producer reservation.");
            Assert.That(nativeCounter.Reservations, Is.EqualTo(backendSupportsCondition ? 1 : 0));
            Assert.That(nativeCounter.Releases, Is.EqualTo(backendSupportsCondition ? 1 : 0));
            Assert.That(nativeCounter.Flushes, Is.EqualTo(backendSupportsCondition ? 0 : 1));
            Assert.That(nativeCounter.Reserved, Is.False);
            Assert.That(backend.Events.Select(evt => evt.Thread).Distinct(), Is.EqualTo(new[] { owner.ManagedThreadId }));
        }
    }
}
