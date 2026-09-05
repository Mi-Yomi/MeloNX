using Ryujinx.Graphics.GAL;
using Silk.NET.Vulkan;
using System;
using System.Collections.Generic;
using System.Threading;

namespace Ryujinx.Graphics.Vulkan.Queries
{
    class CounterQueue : IDisposable
    {
        private const int QueryPoolInitialSize = 100;

        private readonly VulkanRenderer _gd;
        private readonly Device _device;
        private readonly PipelineFull _pipeline;

        public CounterType Type { get; }
        private volatile bool _disposed;
        public bool Disposed => _disposed;

        private readonly Queue<CounterQueueEvent> _events = new();
        private CounterQueueEvent _current;

        private ulong _accumulatedCounter;
        private int _waiterCount;
        private CounterQueueEvent _activeEvent;
        private long _reportsQueued;
        private long _reportsRetired;
        private long _waitTimeouts;
        private readonly CancellationTokenSource _disposeCancellation = new();

        internal CancellationToken DisposalToken => _disposeCancellation.Token;

        internal void RegisterWaitTimeout() => Interlocked.Increment(ref _waitTimeouts);

        internal string GetDiagnosticSnapshot() =>
            $"counter_{(int)Type}_pending={Math.Max(0, Interlocked.Read(ref _reportsQueued) - Interlocked.Read(ref _reportsRetired))}, " +
            $"counter_{(int)Type}_active={Volatile.Read(ref _activeEvent) != null}, " +
            $"counter_{(int)Type}_retired={Interlocked.Read(ref _reportsRetired)}, counter_{(int)Type}_timeouts={Interlocked.Read(ref _waitTimeouts)}";

        private readonly Lock _lock = new();

        private readonly Queue<BufferedQuery> _queryPool;
        private readonly AutoResetEvent _queuedEvent = new(false);
        private readonly AutoResetEvent _wakeSignal = new(false);
        private readonly AutoResetEvent _eventConsumed = new(false);

        private readonly Thread _consumerThread;

        public int ResetSequence { get; private set; }

        internal CounterQueue(VulkanRenderer gd, Device device, PipelineFull pipeline, CounterType type)
        {
            _gd = gd;
            _device = device;
            _pipeline = pipeline;

            Type = type;

            _queryPool = new Queue<BufferedQuery>(QueryPoolInitialSize);
            for (int i = 0; i < QueryPoolInitialSize; i++)
            {
                // AMD Polaris GPUs on Windows seem to have issues reporting 64-bit query results.
                _queryPool.Enqueue(new BufferedQuery(_gd, _device, _pipeline, type, gd.IsAmdWindows));
            }

            _current = new CounterQueueEvent(this, type, 0);

            _consumerThread = new Thread(EventConsumer) { Name = "CPU.CounterQueue." + (int)type };
            _consumerThread.Start();
        }

        public void ResetCounterPool()
        {
            ResetSequence++;
        }

        public void ResetFutureCounters(CommandBuffer cmd, int count)
        {
            // Pre-emptively reset queries to avoid render pass splitting.
            lock (_lock)
            {
                count = Math.Min(count, _queryPool.Count);

                if (count > 0)
                {
                    foreach (BufferedQuery query in _queryPool)
                    {
                        query.PoolReset(cmd, ResetSequence);

                        if (--count == 0)
                        {
                            break;
                        }
                    }
                }
            }
        }

        private void EventConsumer()
        {
            CounterQueueEvent evt = null;
            try
            {
                while (!Disposed)
                {
                    if (evt == null)
                    {
                        lock (_lock)
                        {
                            if (_events.Count > 0)
                            {
                                evt = _events.Dequeue();
                                Volatile.Write(ref _activeEvent, evt);
                            }
                        }
                    }

                    if (evt == null)
                    {
                        _queuedEvent.WaitOne(); // No more events to go through, wait for more.
                    }
                    else if (evt.TryConsume(ref _accumulatedCounter, true,
                        Volatile.Read(ref _waiterCount) == 0 ? _wakeSignal : null))
                    {
                        Interlocked.Increment(ref _reportsRetired);
                        Volatile.Write(ref _activeEvent, null);
                        evt = null;
                    }
                    // A timed-out query remains the current event. Preserve ordering,
                    // do not publish its sentinel, and retry until completion or shutdown.

                    if (Volatile.Read(ref _waiterCount) > 0)
                    {
                        _eventConsumed.Set();
                    }
                }
            }
            finally
            {
                if (evt != null)
                {
                    evt.Dispose();
                    Interlocked.Increment(ref _reportsRetired);
                }

                Volatile.Write(ref _activeEvent, null);
                _eventConsumed.Set();
            }
        }

        internal BufferedQuery GetQueryObject()
        {
            // Creating/disposing query objects on a context we're sharing with will cause issues.
            // So instead, make a lot of query objects on the main thread and reuse them.

            lock (_lock)
            {
                if (_queryPool.Count > 0)
                {
                    BufferedQuery result = _queryPool.Dequeue();
                    return result;
                }

                return new BufferedQuery(_gd, _device, _pipeline, Type, _gd.IsAmdWindows);
            }
        }

        internal void ReturnQueryObject(BufferedQuery query)
        {
            lock (_lock)
            {
                // The query will be reset when it dequeues.
                _queryPool.Enqueue(query);
            }
        }

        public CounterQueueEvent QueueReport(EventHandler<ulong> resultHandler, float divisor, ulong lastDrawIndex, bool hostReserved)
        {
            CounterQueueEvent result;
            ulong draws = lastDrawIndex - _current.DrawIndex;

            lock (_lock)
            {
                // A query's result only matters if more than one draw was performed during it.
                // Otherwise, dummy it out and return 0 immediately.

                if (hostReserved)
                {
                    // This counter event is guaranteed to be available for host conditional rendering.
                    _current.ReserveForHostAccess();
                }

                _current.Complete(draws > 0 && Type != CounterType.TransformFeedbackPrimitivesWritten, divisor);
                _events.Enqueue(_current);
                Interlocked.Increment(ref _reportsQueued);

                _current.OnResult += resultHandler;

                result = _current;

                _current = new CounterQueueEvent(this, Type, lastDrawIndex);
            }

            _queuedEvent.Set();

            return result;
        }

        public void QueueReset(ulong lastDrawIndex)
        {
            ulong draws = lastDrawIndex - _current.DrawIndex;

            lock (_lock)
            {
                _current.Clear(draws != 0);
            }
        }

        public void Flush(bool blocking)
        {
            if (!blocking)
            {
                // Just wake the consumer thread - it will update the queries.
                _wakeSignal.Set();
                return;
            }

            CounterQueueEvent last;
            lock (_lock)
            {
                last = _activeEvent;
                foreach (CounterQueueEvent evt in _events)
                {
                    last = evt;
                }
            }

            // Only EventConsumer may update the accumulated counter. Consuming here
            // could overtake the event that the consumer already removed from the queue.
            if (last != null)
            {
                FlushTo(last);
            }
        }

        public void FlushTo(CounterQueueEvent evt)
        {
            // Flush the counter queue on the main thread.
            Interlocked.Increment(ref _waiterCount);

            _wakeSignal.Set();

            try
            {
                while (!evt.Disposed && !Disposed)
                {
                    _eventConsumed.WaitOne(1);
                }
            }
            finally
            {
                Interlocked.Decrement(ref _waiterCount);
            }
        }

        public void Dispose()
        {
            // Wake a pending query before taking any queue/event locks. Shutdown
            // must not depend on a GPU result which might never become available.
            _disposeCancellation.Cancel();
            _wakeSignal.Set();
            lock (_lock)
            {
                while (_events.Count > 0)
                {
                    CounterQueueEvent evt = _events.Dequeue();

                    evt.Dispose();
                    Interlocked.Increment(ref _reportsRetired);
                }

                _disposed = true;
            }

            _queuedEvent.Set();

            _consumerThread.Join();

            _current?.Dispose();

            foreach (BufferedQuery query in _queryPool)
            {
                query.Dispose();
            }

            _queuedEvent.Dispose();
            _wakeSignal.Dispose();
            _eventConsumed.Dispose();
            _disposeCancellation.Dispose();
        }
    }
}
