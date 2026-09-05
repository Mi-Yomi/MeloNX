using Ryujinx.Graphics.GAL;
using System;
using System.Threading;

namespace Ryujinx.Graphics.Vulkan.Queries
{
    class CounterQueueEvent : ICounterEvent
    {
        public event EventHandler<ulong> OnResult;

        public CounterType Type { get; }
        public bool ClearCounter { get; private set; }

        private volatile bool _disposed;
        public bool Disposed => _disposed;
        public bool Invalid { get; set; }

        public ulong DrawIndex { get; }

        private readonly CounterQueue _queue;
        private readonly BufferedQuery _counter;

        private bool _hostAccessReserved;
        private int _refCount = 1; // Starts with a reference from the counter queue.

        private readonly Lock _lock = new();
        private ulong _result = ulong.MaxValue;
        private double _divisor = 1f;

        public CounterQueueEvent(CounterQueue queue, CounterType type, ulong drawIndex)
        {
            _queue = queue;

            _counter = queue.GetQueryObject();
            Type = type;

            DrawIndex = drawIndex;

            _counter.Begin(_queue.ResetSequence);
        }

        public Auto<DisposableBuffer> GetBuffer()
        {
            return _counter.GetBuffer();
        }

        internal void Clear(bool counterReset)
        {
            if (counterReset)
            {
                _counter.Reset();
            }

            ClearCounter = true;
        }

        internal void Complete(bool withResult, double divisor)
        {
            _counter.End(withResult);

            _divisor = divisor;
        }

        internal bool TryConsume(ref ulong result, bool block, AutoResetEvent wakeSignal = null,
            int timeoutMilliseconds = BufferedQuery.QueryWaitTimeoutMilliseconds)
        {
            lock (_lock)
            {
                if (Disposed)
                {
                    return true;
                }

                // Wait without holding the event lock: an explicit dispose command
                // on the backend must not delay submission of this query. Keep a
                // temporary reference so Dispose cannot return it to the pool while
                // the consumer is still reading the mapped result.
                Interlocked.Increment(ref _refCount);
            }

            try
            {
                long queryResult;

                if (block)
                {
                    if (!_counter.TryAwaitResult(out queryResult, wakeSignal, _queue.DisposalToken, timeoutMilliseconds))
                    {
                        if (!_queue.DisposalToken.IsCancellationRequested)
                        {
                            _queue.RegisterWaitTimeout();
                        }

                        return Disposed;
                    }
                }
                else
                {
                    if (!_counter.TryGetResult(out queryResult))
                    {
                        return Disposed;
                    }
                }

                lock (_lock)
                {
                    if (Disposed)
                    {
                        return true;
                    }

                    if (ClearCounter)
                    {
                        result = 0;
                    }

                    result += _divisor == 1 ? (ulong)queryResult : (ulong)Math.Ceiling(queryResult / _divisor);

                    _result = result;

                    OnResult?.Invoke(this, result);

                    Dispose(); // Release the queue reference; the read lease retires below.

                    return true;
                }
            }
            finally
            {
                DecrementRefCount();
            }
        }

        public void Flush()
        {
            if (Disposed)
            {
                return;
            }

            // Tell the queue to process all events up to this one.
            _queue.FlushTo(this);
        }

        public void DecrementRefCount()
        {
            if (Interlocked.Decrement(ref _refCount) == 0)
            {
                DisposeInternal();
            }
        }

        public bool ReserveForHostAccess()
        {
            if (_hostAccessReserved)
            {
                return true;
            }

            if (IsValueAvailable())
            {
                return false;
            }

            if (Interlocked.Increment(ref _refCount) == 1)
            {
                Interlocked.Decrement(ref _refCount);

                return false;
            }

            _hostAccessReserved = true;

            return true;
        }

        public void ReleaseHostAccess()
        {
            _hostAccessReserved = false;

            DecrementRefCount();
        }

        private void DisposeInternal()
        {
            _queue.ReturnQueryObject(_counter);
        }

        private bool IsValueAvailable()
        {
            return _result != ulong.MaxValue || _counter.TryGetResult(out _);
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                DecrementRefCount();
            }
        }
    }
}
