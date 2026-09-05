using NUnit.Framework;
using Ryujinx.Common;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.GAL.Multithreading;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Ryujinx.Tests.Graphics
{
    [NonParallelizable]
    public class ExecutionTimingTests
    {
        [Test]
        public void ScopeRecordsExceptionsAndConcurrentCompletions()
        {
            var before = ExecutionTimings.Get(ExecutionStage.DiagnosticSnapshot);
            Assert.Throws<InvalidOperationException>(() =>
            {
                using var timing = ExecutionTimings.Measure(ExecutionStage.DiagnosticSnapshot);
                throw new InvalidOperationException();
            });
            Parallel.For(0, 10000, _ =>
            {
                using var timing = ExecutionTimings.Measure(ExecutionStage.DiagnosticSnapshot);
            });
            var after = ExecutionTimings.Get(ExecutionStage.DiagnosticSnapshot);
            Assert.That(after.Calls - before.Calls, Is.EqualTo(10001));
            Assert.That(after.Ticks, Is.GreaterThanOrEqualTo(before.Ticks));
            Assert.That(ExecutionTimings.GetSnapshot(), Does.Contain("gpu_work_us=unknown"));
        }

        [Test]
        public void RepeatedMeasurementDoesNotAllocateOrRetainOperationObjects()
        {
            using (ExecutionTimings.Measure(ExecutionStage.DiagnosticSnapshot)) { }
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            long start = Stopwatch.GetTimestamp();
            for (int i = 0; i < 100000; i++)
            {
                using var timing = ExecutionTimings.Measure(ExecutionStage.DiagnosticSnapshot);
            }
            long bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
            TestContext.WriteLine($"100000 timer scopes: {Stopwatch.GetElapsedTime(start).TotalMilliseconds:F3} ms, {bytes} allocated bytes (host microbenchmark, not iPhone overhead).");
            Assert.That(bytes, Is.Zero);
        }

        [Test]
        public void ActualThreadedReadbackRecordsWaitAndPreservesData()
        {
            AuditTestRenderer backend = new();
            ThreadedRenderer renderer = new(backend);
            using ManualResetEventSlim done = new(false), stop = new(false);
            Exception failure = null;
            var before = ExecutionTimings.Get(ExecutionStage.GalInvokeWait);
            Thread owner = new(() => renderer.RunLoop(() =>
            {
                try
                {
                    BufferHandle handle = renderer.CreateBuffer(4, BufferAccess.Default);
                    renderer.SetBufferData(handle, 0, new byte[] { 1, 2, 3, 4 });
                    using (var output = renderer.GetBufferData(handle, 0, 4))
                    {
                        Assert.That(output.Get().ToArray(), Is.EqualTo(new byte[] { 1, 2, 3, 4 }));
                    }
                    renderer.DeleteBuffer(handle);
                }
                catch (Exception error) { failure = error; }
                finally { done.Set(); stop.Wait(); }
            })) { IsBackground = true };
            owner.Start();
            try
            {
                Assert.That(done.Wait(TimeSpan.FromSeconds(15)), Is.True);
                Assert.That(failure, Is.Null);
                Assert.That(ExecutionTimings.Get(ExecutionStage.GalInvokeWait).Calls, Is.GreaterThan(before.Calls));
            }
            finally
            {
                stop.Set();
                renderer.Dispose();
            }
            Assert.That(owner.Join(TimeSpan.FromSeconds(5)), Is.True);
            Assert.That(backend.Buffers, Is.Empty);
        }
    }
}
