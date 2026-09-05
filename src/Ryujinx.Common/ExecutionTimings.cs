using System;
using System.Diagnostics;
using System.Text;
using System.Threading;

namespace Ryujinx.Common
{
    public enum ExecutionStage
    {
        GuestFifoProcess,
        GalQueueBackpressure,
        GalInvokeWait,
        GalExternalInterruptWait,
        GalFrameWait,
        ShaderModuleCompile,
        CommandBufferSubmit,
        FenceWait,
        PresentCpu,
        SwapchainAcquire,
        QueuePresent,
        DiagnosticSnapshot,
        Count,
    }

    /// <summary>
    /// Fixed-size, process-cumulative CPU wall timers. Categories overlap, may run on
    /// different threads and are NOT GPU time, frame latency or an additive frame budget.
    /// No allocation/formatting or new waits occur at the measured operation.
    /// </summary>
    public static class ExecutionTimings
    {
        private static readonly long[] _ticks = new long[(int)ExecutionStage.Count];
        private static readonly long[] _calls = new long[(int)ExecutionStage.Count];

        public readonly struct Scope : IDisposable
        {
            private readonly ExecutionStage _stage;
            private readonly long _start;

            internal Scope(ExecutionStage stage)
            {
                _stage = stage;
                _start = Stopwatch.GetTimestamp();
            }

            public void Dispose()
            {
                Interlocked.Add(ref _ticks[(int)_stage], Stopwatch.GetTimestamp() - _start);
                Interlocked.Increment(ref _calls[(int)_stage]);
            }
        }

        public static Scope Measure(ExecutionStage stage) => new(stage);

        // Independently atomic observations: a concurrently completing operation may
        // cross the sample boundary. The counters never reset between sessions.
        public static (long Calls, long Ticks) Get(ExecutionStage stage) =>
            (Interlocked.Read(ref _calls[(int)stage]), Interlocked.Read(ref _ticks[(int)stage]));

        public static string GetSnapshot()
        {
            StringBuilder result = new("CPU timing v1: accounting=process_cumulative_overlapping_wall_time, gpu_work_us=unknown, guest_cpu_us=unknown");
            for (ExecutionStage stage = 0; stage < ExecutionStage.Count; stage++)
            {
                var value = Get(stage);
                long microseconds = (long)(value.Ticks * (1_000_000.0 / Stopwatch.Frequency));
                result.Append($", {stage}_calls={value.Calls}, {stage}_us={microseconds}");
            }
            return result.ToString();
        }
    }
}
