using System;
using System.Collections.Generic;
using System.Threading;

namespace Ryujinx.Graphics.GAL.Multithreading
{
    class SyncMap : IDisposable
    {
        private readonly HashSet<ulong> _inFlight = [];
        private bool _disposed;

        internal void CreateSyncHandle(ulong id)
        {
            lock (_inFlight)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _inFlight.Add(id);
            }
        }

        internal void AssignSync(ulong id)
        {
            lock (_inFlight)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _inFlight.Remove(id);
                // Different sync IDs share the predicate lock, not a consumable
                // event signal that the wrong waiter can take and lose.
                Monitor.PulseAll(_inFlight);
            }
        }

        internal void WaitSyncAvailability(ulong id)
        {
            // Blocks until the handle is available.

            lock (_inFlight)
            {
                while (_inFlight.Contains(id))
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    Monitor.Wait(_inFlight);
                }
            }
        }

        public void Dispose()
        {
            lock (_inFlight)
            {
                _disposed = true;
                // Teardown must release pending callers as cancellation, never
                // pretend that an uncreated native sync object became available.
                Monitor.PulseAll(_inFlight);
            }
        }
    }
}
