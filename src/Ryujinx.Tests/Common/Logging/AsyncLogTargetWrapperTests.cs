using NUnit.Framework;
using Ryujinx.Common.Logging;
using Ryujinx.Common.Logging.Targets;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Ryujinx.Tests.Common.Logging
{
    public class AsyncLogTargetWrapperTests
    {
        private sealed class CallbackLogTarget : ILogTarget
        {
            private readonly Action<LogEventArgs> _callback;

            public string Name => nameof(CallbackLogTarget);

            public CallbackLogTarget(Action<LogEventArgs> callback)
            {
                _callback = callback;
            }

            public void Log(object sender, LogEventArgs args)
            {
                _callback(args);
            }

            public void Dispose()
            {
            }
        }

        private static LogEventArgs CreateLogEvent(string message)
        {
            return new LogEventArgs(LogLevel.Info, TimeSpan.Zero, "TestThread", message);
        }

        [Test]
        public void FlushWaitsForQueuedMessages()
        {
            int messageCount = 0;
            CallbackLogTarget target = new(_ => Interlocked.Increment(ref messageCount));
            AsyncLogTargetWrapper wrapper = new(target);

            for (int index = 0; index < 32; index++)
            {
                wrapper.Log(this, CreateLogEvent(index.ToString()));
            }

            wrapper.Flush();

            int flushedMessageCount = Volatile.Read(ref messageCount);
            wrapper.Dispose();

            Assert.That(flushedMessageCount, Is.EqualTo(32));
        }

        [Test]
        public void FlushFromMessageThreadDoesNotDeadlock()
        {
            using ManualResetEventSlim secondMessageProcessed = new(false);
            AsyncLogTargetWrapper wrapper = null;
            int messageCount = 0;

            CallbackLogTarget target = new(_ =>
            {
                if (Interlocked.Increment(ref messageCount) == 1)
                {
                    // Ensure an item is waiting behind the current callback. A flush
                    // that waits for a marker here would wait on its own worker.
                    wrapper.Log(this, CreateLogEvent("second"));
                    wrapper.Flush();
                }
                else
                {
                    secondMessageProcessed.Set();
                }
            });

            wrapper = new AsyncLogTargetWrapper(target);
            wrapper.Log(this, CreateLogEvent("first"));

            bool completed = secondMessageProcessed.Wait(TimeSpan.FromSeconds(3));

            // Do not join a deadlocked worker if this assertion ever regresses.
            if (completed)
            {
                wrapper.Dispose();
            }

            Assert.That(completed, Is.True);
        }

        [Test]
        public void TimedOutFlushCanBeCompletedLateWithoutKillingMessageThread()
        {
            using ManualResetEventSlim firstMessageEntered = new(false);
            using ManualResetEventSlim releaseFirstMessage = new(false);
            using ManualResetEventSlim secondMessageProcessed = new(false);
            int messageCount = 0;

            CallbackLogTarget target = new(_ =>
            {
                if (Interlocked.Increment(ref messageCount) == 1)
                {
                    firstMessageEntered.Set();
                    releaseFirstMessage.Wait();
                }
                else
                {
                    secondMessageProcessed.Set();
                }
            });

            AsyncLogTargetWrapper wrapper = new(target);
            wrapper.Log(this, CreateLogEvent("blocking"));
            Assert.That(firstMessageEntered.Wait(TimeSpan.FromSeconds(3)), Is.True);

            Stopwatch stopwatch = Stopwatch.StartNew();
            Task firstFlush = Task.Run(wrapper.Flush);
            bool returnedWhileTargetWasBlocked = SpinWait.SpinUntil(
                () => firstFlush.IsCompleted,
                TimeSpan.FromSeconds(3));
            stopwatch.Stop();

            releaseFirstMessage.Set();
            SpinWait.SpinUntil(() => firstFlush.IsCompleted, TimeSpan.FromSeconds(3));

            wrapper.Log(this, CreateLogEvent("after-timeout"));
            wrapper.Flush();
            bool workerSurvivedLateMarker = secondMessageProcessed.Wait(TimeSpan.FromSeconds(3));
            wrapper.Dispose();

            Assert.Multiple(() =>
            {
                Assert.That(returnedWhileTargetWasBlocked, Is.True, "Flush exceeded its bounded wait.");
                Assert.That(stopwatch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(3)));
                Assert.That(firstFlush.Exception, Is.Null);
                Assert.That(workerSurvivedLateMarker, Is.True, "The late flush marker stopped the message thread.");
            });
        }

        [Test]
        public void CompletingQueueReleasesConcurrentProducersWithoutExceptions()
        {
            using ManualResetEventSlim firstMessageEntered = new(false);
            using ManualResetEventSlim releaseFirstMessage = new(false);
            int messageCount = 0;

            CallbackLogTarget target = new(_ =>
            {
                if (Interlocked.Increment(ref messageCount) == 1)
                {
                    firstMessageEntered.Set();
                    releaseFirstMessage.Wait();
                }
            });

            AsyncLogTargetWrapper wrapper = new(target, queueLimit: 1);
            wrapper.Log(this, CreateLogEvent("blocking"));
            Assert.That(firstMessageEntered.Wait(TimeSpan.FromSeconds(3)), Is.True);

            // Fill the only queue slot, then race two blocked producers with shutdown.
            wrapper.Log(this, CreateLogEvent("queued"));
            Task lateLog = Task.Run(() => wrapper.Log(this, CreateLogEvent("late")));
            Task flush = Task.Run(wrapper.Flush);
            Task dispose = Task.Run(wrapper.Dispose);

            bool producersReleased = SpinWait.SpinUntil(
                () => lateLog.IsCompleted && flush.IsCompleted,
                TimeSpan.FromSeconds(3));

            releaseFirstMessage.Set();
            bool disposeCompleted = SpinWait.SpinUntil(() => dispose.IsCompleted, TimeSpan.FromSeconds(3));

            Assert.Multiple(() =>
            {
                Assert.That(producersReleased, Is.True);
                Assert.That(lateLog.Exception, Is.Null);
                Assert.That(flush.Exception, Is.Null);
                Assert.That(disposeCompleted, Is.True);
                Assert.That(dispose.Exception, Is.Null);
            });
        }
    }
}
