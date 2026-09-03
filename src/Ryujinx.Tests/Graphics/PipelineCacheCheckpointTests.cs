using NUnit.Framework;
using Ryujinx.Graphics.Vulkan;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Ryujinx.Tests.Graphics
{
    public class PipelineCacheCheckpointTests
    {
        private sealed class ManualTimeProvider : TimeProvider
        {
            private long _timestamp;

            public override long TimestampFrequency => TimeSpan.TicksPerSecond;
            public override long GetTimestamp() => Interlocked.Read(ref _timestamp);

            public void Advance(TimeSpan elapsed)
            {
                Interlocked.Add(ref _timestamp, elapsed.Ticks);
            }
        }

        [Test]
        public void FirstCheckpointRequires64PipelinesWithoutWaiting()
        {
            PipelineCacheCheckpointPolicy policy = new(1, new ManualTimeProvider());

            NotifyBelowThreshold(policy, 0);
            Assert.That(policy.TryBegin(0), Is.True);
            policy.Complete(0, saved: true);
        }

        [Test]
        public void ThrottledCheckpointRetainsPendingPipelinesUntilIntervalElapses()
        {
            ManualTimeProvider time = new();
            PipelineCacheCheckpointPolicy policy = new(1, time);
            CompleteFirstCheckpoint(policy, 0);

            for (int index = 0; index < 1000; index++)
            {
                Assert.That(policy.TryBegin(0), Is.False);
            }

            time.Advance(TimeSpan.FromSeconds(30) - TimeSpan.FromTicks(1));
            Assert.That(policy.TryBegin(0), Is.False);
            time.Advance(TimeSpan.FromTicks(1));
            Assert.That(policy.TryBegin(0), Is.True);
            policy.Complete(0, saved: true);
        }

        [Test]
        public void SuccessfulCheckpointRequires64NewPipelinesEvenAfterInterval()
        {
            ManualTimeProvider time = new();
            PipelineCacheCheckpointPolicy policy = new(1, time);
            CompleteFirstCheckpoint(policy, 0);
            time.Advance(TimeSpan.FromSeconds(30));

            NotifyBelowThreshold(policy, 0);
            Assert.That(policy.TryBegin(0), Is.True);
            policy.Complete(0, saved: true);
        }

        [Test]
        public void BusyCheckpointRetainsOtherWorkersPendingPipelines()
        {
            PipelineCacheCheckpointPolicy policy = new(2, new ManualTimeProvider());
            NotifyBelowThreshold(policy, 0);
            NotifyBelowThreshold(policy, 1);

            Assert.That(policy.TryBegin(0), Is.True);
            Assert.That(policy.TryBegin(1), Is.False);
            policy.Complete(0, saved: true);

            Assert.That(policy.TryBegin(1), Is.True);
            policy.Complete(1, saved: true);
        }

        [Test]
        public void FailedCheckpointReleasesGateAndRetriesPendingWorkAfterBackoff()
        {
            ManualTimeProvider time = new();
            PipelineCacheCheckpointPolicy policy = new(2, time);
            NotifyBelowThreshold(policy, 0);
            NotifyBelowThreshold(policy, 1);

            Assert.That(policy.TryBegin(0), Is.True);
            policy.Complete(0, saved: false);
            Assert.That(policy.TryBegin(0), Is.False);
            Assert.That(policy.TryBegin(1), Is.True);
            policy.Complete(1, saved: true);

            time.Advance(TimeSpan.FromSeconds(30));
            Assert.That(policy.TryBegin(0), Is.True);
            policy.Complete(0, saved: true);
        }

        [Test]
        public async Task ConcurrentWorkersOnlySerializeOneCheckpoint()
        {
            PipelineCacheCheckpointPolicy policy = new(2, new ManualTimeProvider());
            NotifyBelowThreshold(policy, 0);
            NotifyBelowThreshold(policy, 1);
            using Barrier barrier = new(2);

            bool[] results = await Task.WhenAll(
                Task.Run(() => TryConcurrentCheckpoint(policy, barrier, 0)),
                Task.Run(() => TryConcurrentCheckpoint(policy, barrier, 1)));

            Assert.That(results[0] ^ results[1], Is.True);
            int deniedWorker = results[0] ? 1 : 0;
            Assert.That(policy.TryBegin(deniedWorker), Is.True);
            policy.Complete(deniedWorker, saved: true);
        }

        private static bool TryConcurrentCheckpoint(PipelineCacheCheckpointPolicy policy, Barrier barrier, int workerIndex)
        {
            Assert.That(barrier.SignalAndWait(TimeSpan.FromSeconds(5)), Is.True);
            bool started = policy.TryBegin(workerIndex);

            try
            {
                // Neither worker may release the gate until both have attempted acquisition.
                Assert.That(barrier.SignalAndWait(TimeSpan.FromSeconds(5)), Is.True);
                return started;
            }
            finally
            {
                if (started)
                {
                    policy.Complete(workerIndex, saved: true);
                }
            }
        }

        private static void CompleteFirstCheckpoint(PipelineCacheCheckpointPolicy policy, int workerIndex)
        {
            NotifyBelowThreshold(policy, workerIndex);
            Assert.That(policy.TryBegin(workerIndex), Is.True);
            policy.Complete(workerIndex, saved: true);
        }

        private static void NotifyBelowThreshold(PipelineCacheCheckpointPolicy policy, int workerIndex)
        {
            for (int index = 0; index < 63; index++)
            {
                Assert.That(policy.TryBegin(workerIndex), Is.False);
            }
        }
    }
}
