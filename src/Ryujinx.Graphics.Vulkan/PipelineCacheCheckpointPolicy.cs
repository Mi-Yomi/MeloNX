using System;
using System.Threading;

namespace Ryujinx.Graphics.Vulkan
{
    /// <summary>
    /// Limits checkpoint allocations while each worker retains exclusive access to its native cache.
    /// </summary>
    internal sealed class PipelineCacheCheckpointPolicy
    {
        private const int PipelineThreshold = 64;
        private static readonly TimeSpan MinimumInterval = TimeSpan.FromSeconds(30);

        private readonly WorkerState[] _workers;
        private readonly TimeProvider _timeProvider;
        private int _saveInProgress;

        private struct WorkerState
        {
            public int PendingPipelines;
            public bool HasAttemptedSave;
            public long LastAttemptTimestamp;
        }

        public PipelineCacheCheckpointPolicy(int workerCount, TimeProvider timeProvider = null)
        {
            _workers = new WorkerState[workerCount];
            _timeProvider = timeProvider ?? TimeProvider.System;
        }

        // The worker owning workerIndex must call both methods; Complete must run in a finally block.
        public bool TryBegin(int workerIndex)
        {
            ref WorkerState worker = ref _workers[workerIndex];
            worker.PendingPipelines = Math.Min(worker.PendingPipelines + 1, PipelineThreshold);

            if (worker.PendingPipelines < PipelineThreshold)
            {
                return false;
            }

            long now = _timeProvider.GetTimestamp();

            if (worker.HasAttemptedSave &&
                _timeProvider.GetElapsedTime(worker.LastAttemptTimestamp, now) < MinimumInterval)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _saveInProgress, 1, 0) != 0)
            {
                return false;
            }

            // Failed saves retain their pending work, but wait before retrying expensive serialization.
            worker.HasAttemptedSave = true;
            worker.LastAttemptTimestamp = now;
            return true;
        }

        public void Complete(int workerIndex, bool saved)
        {
            if (saved)
            {
                _workers[workerIndex].PendingPipelines = 0;
            }

            Volatile.Write(ref _saveInProgress, 0);
        }
    }
}
