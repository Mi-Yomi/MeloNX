using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Ryujinx.Common.Logging.Targets
{
    public enum AsyncLogTargetOverflowAction
    {
        /// <summary>
        /// Block until there's more room in the queue
        /// </summary>
        Block = 0,

        /// <summary>
        /// Discard the overflowing item
        /// </summary>
        Discard = 1,
    }

    public class AsyncLogTargetWrapper : ILogTarget
    {
        private const int FlushTimeoutMilliseconds = 1000;

        private readonly ILogTarget _target;

        private readonly Thread _messageThread;

        private readonly BlockingCollection<LogEventArgs> _messageQueue;

        private readonly int _overflowTimeout;

        private sealed class FlushEventArgs : LogEventArgs
        {
            public readonly TaskCompletionSource Completion;

            public FlushEventArgs()
                : base(LogLevel.Notice, TimeSpan.Zero, string.Empty, string.Empty)
            {
                Completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        string ILogTarget.Name => _target.Name;

        public AsyncLogTargetWrapper(ILogTarget target, int queueLimit = -1, AsyncLogTargetOverflowAction overflowAction = AsyncLogTargetOverflowAction.Block)
        {
            _target = target;
            _messageQueue = queueLimit == -1
                ? new BlockingCollection<LogEventArgs>()
                : new BlockingCollection<LogEventArgs>(queueLimit);
            _overflowTimeout = overflowAction == AsyncLogTargetOverflowAction.Block ? -1 : 0;

            _messageThread = new Thread(() =>
            {
                while (!_messageQueue.IsCompleted)
                {
                    try
                    {
                        LogEventArgs item = _messageQueue.Take();

                        if (item is FlushEventArgs flush)
                        {
                            flush.Completion.TrySetResult();
                            continue;
                        }

                        _target.Log(this, item);
                    }
                    catch (InvalidOperationException)
                    {
                        // IOE means that Take() was called on a completed collection.
                        // Some other thread can call CompleteAdding after we pass the
                        // IsCompleted check but before we call Take.
                        // We can simply catch the exception since the loop will break
                        // on the next iteration.
                    }
                }
            })
            {
                Name = "Logger.MessageThread",
                IsBackground = true,
            };
            _messageThread.Start();
        }

        public void Log(object sender, LogEventArgs e)
        {
            try
            {
                _messageQueue.TryAdd(e, _overflowTimeout);
            }
            catch (InvalidOperationException)
            {
                // CompleteAdding can race with TryAdd, including while a blocking
                // producer is waiting for room in the queue. Logs submitted during
                // shutdown are intentionally ignored.
            }
        }

        public void Flush()
        {
            // A target may request a flush while it is being called by this wrapper.
            // All earlier items have already been consumed at that point, and waiting
            // for a marker on this same thread would deadlock.
            if (Thread.CurrentThread == _messageThread)
            {
                return;
            }

            long startedAt = Environment.TickCount64;
            FlushEventArgs flush = new();

            try
            {
                // Use the same finite budget for queueing and waiting. In particular,
                // a full queue or a failed message thread must not block a crash path.
                if (!_messageQueue.TryAdd(flush, FlushTimeoutMilliseconds))
                {
                    return;
                }
            }
            catch (InvalidOperationException)
            {
                // CompleteAdding can race with TryAdd. There is nothing left that a
                // flush marker can safely do once shutdown has started.
                return;
            }

            long elapsed = Environment.TickCount64 - startedAt;
            int remaining = (int)Math.Max(0, FlushTimeoutMilliseconds - elapsed);

            if (remaining > 0)
            {
                flush.Completion.Task.Wait(remaining);
            }
        }

        public void Dispose()
        {
            GC.SuppressFinalize(this);
            _messageQueue.CompleteAdding();
            _messageThread.Join();
        }
    }
}
