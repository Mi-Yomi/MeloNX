using NUnit.Framework;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.GAL.Multithreading;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Ryujinx.Tests.Graphics
{
    public class ThreadedAvailabilityWaitTests
    {
        // Calls the real maps directly: no mocked event, scheduler, or wait loop.
        private sealed class Availability : IDisposable
        {
            private readonly BufferMap _buffers;
            private readonly SyncMap _syncs;
            private readonly Dictionary<ulong, BufferHandle> _handles = new();
            private readonly HashSet<ulong> _pending = [];

            public Availability(bool buffers)
            {
                if (buffers) _buffers = new BufferMap();
                else _syncs = new SyncMap();
            }

            public void Register(ulong id)
            {
                _pending.Add(id);
                if (_buffers != null) _handles.Add(id, _buffers.CreateBufferHandle());
                else _syncs.CreateSyncHandle(id);
            }

            public void Publish(ulong id)
            {
                if (!_pending.Remove(id)) return;
                if (_buffers != null) _buffers.AssignBuffer(_handles[id], _handles[id]);
                else _syncs.AssignSync(id);
            }

            public void Wait(ulong id)
            {
                if (_buffers != null)
                {
                    BufferHandle mapped = _buffers.MapBufferBlocking(_handles[id]);
                    Assert.That(mapped, Is.EqualTo(_handles[id]), "Wait returned before its mapping existed");
                }
                else _syncs.WaitSyncAvailability(id);
            }

            public void PublishAll()
            {
                foreach (ulong id in _pending.ToArray()) Publish(id);
            }

            public void Dispose()
            {
                PublishAll();
                _syncs?.Dispose();
            }
        }

        private static void JoinWorkersForCleanup(IEnumerable<Waiter> waiters)
        {
            foreach (Waiter waiter in waiters)
            {
                if (!waiter.Thread.Join(TimeSpan.FromMilliseconds(200)))
                {
                    // A baseline with the old lost-wakeup bug can still have a
                    // worker asleep after all identifiers have been published.
                    // Interrupt only these test-owned threads, then prove they
                    // exited before disposing wait resources or leaving the test.
                    waiter.Thread.Interrupt();
                    Assert.That(waiter.Thread.Join(TimeSpan.FromSeconds(2)), Is.True,
                        "Test cleanup could not stop a blocked availability worker");
                }
            }
        }

        private static void Cleanup(Availability availability, IEnumerable<Waiter> waiters)
        {
            try
            {
                availability.PublishAll();
                JoinWorkersForCleanup(waiters);
            }
            finally
            {
                availability.Dispose();
            }
        }

        private sealed class Waiter
        {
            public Thread Thread { get; }
            public Exception Failure { get; private set; }

            public Waiter(Action wait)
            {
                Thread = new Thread(() =>
                {
                    try { wait(); }
                    catch (Exception error) { Failure = error; }
                }) { IsBackground = true };
                Thread.Start();
            }

            public void ConfirmBlocked()
            {
                Assert.That(SpinWait.SpinUntil(() => (Thread.ThreadState & ThreadState.WaitSleepJoin) != 0 ||
                    !Thread.IsAlive, TimeSpan.FromSeconds(2)), Is.True, "Waiter did not reach a wait boundary");
                Assert.That(Thread.IsAlive, Is.True, "An unpublished identifier completed early");
                Assert.That(Failure, Is.Null);
            }

            public void ConfirmCompleted()
            {
                Assert.That(Thread.Join(TimeSpan.FromSeconds(2)), Is.True, "Ready identifier was not awakened");
                Assert.That(Failure, Is.Null);
            }
        }

        [TestCase(true)]
        [TestCase(false)]
        public void ReadyIdentifierWakesEveryWaiterWhileOtherIdentifierRemainsPending(bool buffers)
        {
            using Availability availability = new(buffers);
            availability.Register(1);
            availability.Register(2);
            List<Waiter> waiters = [];
            try
            {
                // Start an unrelated waiter first: the old shared AutoResetEvent
                // could let this waiter consume the only notification for ID 2.
                Waiter unrelated = new(() => availability.Wait(1));
                waiters.Add(unrelated);
                unrelated.ConfirmBlocked();
                for (int i = 0; i < 4; i++)
                {
                    Waiter waiter = new(() => availability.Wait(2));
                    waiters.Add(waiter);
                    waiter.ConfirmBlocked();
                }

                availability.Publish(2);
                foreach (Waiter waiter in waiters.Skip(1)) waiter.ConfirmCompleted();
                Assert.That(unrelated.Thread.IsAlive, Is.True, "An unrelated sync was incorrectly completed");
                availability.Publish(1);
                unrelated.ConfirmCompleted();
            }
            finally
            {
                Cleanup(availability, waiters);
            }
        }

        [TestCase(true)]
        [TestCase(false)]
        public void PublicationBeforeWaitNeedsNoRetainedEventSignal(bool buffers)
        {
            using Availability availability = new(buffers);
            availability.Register(1);
            availability.Register(2);
            availability.Publish(1);
            availability.Publish(2);
            Waiter first = new(() => availability.Wait(1));
            Waiter second = new(() => availability.Wait(2));
            try
            {
                first.ConfirmCompleted();
                second.ConfirmCompleted();
            }
            finally
            {
                Cleanup(availability, new[] { first, second });
            }
        }

        [TestCase(true)]
        [TestCase(false)]
        public void UnrelatedPublicationsCannotCompleteUnpublishedIdentifier(bool buffers)
        {
            using Availability availability = new(buffers);
            availability.Register(1);
            availability.Register(2);
            availability.Register(3);
            Waiter waiter = new(() => availability.Wait(1));
            try
            {
                waiter.ConfirmBlocked();
                availability.Publish(2);
                availability.Publish(3);
                waiter.ConfirmBlocked();
                availability.Publish(1);
                waiter.ConfirmCompleted();
            }
            finally
            {
                Cleanup(availability, new[] { waiter });
            }
        }

        [Test]
        public void SyncDisposalWakesPendingWaitersWithoutReportingAvailability()
        {
            using SyncMap sync = new();
            sync.CreateSyncHandle(1);
            sync.CreateSyncHandle(2);
            Waiter first = new(() => sync.WaitSyncAvailability(1));
            Waiter second = new(() => sync.WaitSyncAvailability(2));
            try
            {
                first.ConfirmBlocked();
                second.ConfirmBlocked();
                sync.Dispose();
                Assert.That(first.Thread.Join(TimeSpan.FromSeconds(2)), Is.True);
                Assert.That(second.Thread.Join(TimeSpan.FromSeconds(2)), Is.True);
                Assert.That(first.Failure, Is.TypeOf<ObjectDisposedException>());
                Assert.That(second.Failure, Is.TypeOf<ObjectDisposedException>());
            }
            finally
            {
                sync.Dispose();
                JoinWorkersForCleanup(new[] { first, second });
            }
        }
    }
}
