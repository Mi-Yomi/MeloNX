using System;
using System.Threading;

namespace Ryujinx.Graphics.Gpu.Memory
{
    enum MemoryPressureSeverity
    {
        Observe = 0, // ABI: headroom sample only; never schedules a renderer trim.
        Low = 1,
        Critical = 2,
    }

    [Flags]
    enum MemoryPressureSource
    {
        Sample = 1,
        UIKitWarning = 2,
    }

    readonly record struct MemoryPressureRequest(
        MemoryPressureSeverity Severity,
        MemoryPressureSource Sources,
        ulong AvailableMemoryBytes);

    static class MemoryPressureTrimPolicy
    {
        private const ulong MiB = 1024 * 1024;
        internal const ulong EmergencyAvailableMemory = 256 * MiB;

        public static ulong CalculateBufferTarget(ulong configuredCapacity, MemoryPressureSeverity severity, ulong availableMemoryBytes)
        {
            // A system warning is not evidence that this small cache exhausted the process.
            // v9's 1-GiB latch turned a fitting working set into per-draw reallocation churn.
            if (severity == MemoryPressureSeverity.Observe || availableMemoryBytes > 512 * MiB)
            {
                return configuredCapacity;
            }

            return availableMemoryBytes <= EmergencyAvailableMemory
                ? configuredCapacity / 4
                : configuredCapacity / 2;
        }

        public static ulong? CalculatePersistentBufferCapacity(
            ulong configuredCapacity,
            MemoryPressureSeverity severity,
            ulong availableMemoryBytes)
        {
            if (severity != MemoryPressureSeverity.Critical || availableMemoryBytes > 512 * MiB)
            {
                return null;
            }

            // UIKit can warn about system-wide pressure with ample process headroom. Reclaim
            // expendable buffers on that pass, but latch only from measured process pressure.
            return availableMemoryBytes <= EmergencyAvailableMemory
                ? configuredCapacity / 4
                : configuredCapacity / 2;
        }

        public static ulong CalculateTextureTarget(ulong configuredCapacity, MemoryPressureSeverity severity)
        {
            return severity == MemoryPressureSeverity.Critical ? 0 : configuredCapacity / 2;
        }
    }

    /// <summary>
    /// Tracks a pressure ceiling with conservative, sampled recovery. Mutated only by the GPU producer.
    /// A brief healthy spike or a gap in observations cannot restore a larger working set.
    /// </summary>
    sealed class RecoverableMemoryPressureCapacity
    {
        private ulong _pressureCapacity = ulong.MaxValue;
        private long _healthySince = -1;
        private long _lastObservation = -1;
        internal const long RecoveryMilliseconds = 20_000;
        internal const long MaximumObservationGapMilliseconds = 5_000;

        public bool ObserveHeadroom(ulong availableMemoryBytes, long nowMilliseconds)
        {
            ulong recoveryThreshold = EffectiveCapacity < ConfiguredCapacity / 2
                ? 512UL * 1024 * 1024
                : 768UL * 1024 * 1024;

            bool continuous = _lastObservation >= 0 && nowMilliseconds >= _lastObservation &&
                nowMilliseconds - _lastObservation <= MaximumObservationGapMilliseconds;
            _lastObservation = nowMilliseconds;

            if (!IsLatched || availableMemoryBytes < recoveryThreshold)
            {
                _healthySince = -1;
                return false;
            }

            if (!continuous || _healthySince < 0)
            {
                _healthySince = nowMilliseconds;
                return false;
            }

            if (nowMilliseconds - _healthySince < RecoveryMilliseconds)
            {
                return false;
            }

            // Recover one tier at a time; never exceed the user's configured capacity.
            _pressureCapacity = EffectiveCapacity < ConfiguredCapacity / 2
                ? ConfiguredCapacity / 2
                : ulong.MaxValue;
            _healthySince = -1;
            return true;
        }

        public ulong ConfiguredCapacity { get; private set; }
        public ulong EffectiveCapacity => Math.Min(ConfiguredCapacity, _pressureCapacity);
        public bool IsLatched => _pressureCapacity != ulong.MaxValue;

        public RecoverableMemoryPressureCapacity(ulong configuredCapacity)
        {
            ConfiguredCapacity = configuredCapacity;
        }

        /// <summary>
        /// Changes the normal cache budget without clearing or relaxing a pressure ceiling.
        /// </summary>
        public void Configure(ulong configuredCapacity)
        {
            ConfiguredCapacity = configuredCapacity;
            _healthySince = -1;

            if (IsLatched)
            {
                _pressureCapacity = Math.Min(_pressureCapacity, configuredCapacity);
            }
        }

        /// <summary>
        /// Lowers the pressure ceiling if the supplied value is more restrictive.
        /// </summary>
        /// <returns>True if the pressure ceiling changed</returns>
        public bool Latch(ulong pressureCapacity)
        {
            ulong newCapacity = Math.Min(_pressureCapacity, pressureCapacity);
            if (newCapacity == _pressureCapacity)
            {
                return false;
            }

            _pressureCapacity = newCapacity;
            _healthySince = -1;
            return true;
        }
    }

    /// <summary>
    /// Coalesces memory-pressure reports from non-GPU threads for consumption by the GPU thread.
    /// </summary>
    sealed class MemoryPressureMailbox
    {
        private readonly Lock _lock = new();
        private bool _accepting = true;
        private bool _pending;
        private MemoryPressureSeverity _severity;
        private MemoryPressureSource _sources;
        private ulong _availableMemoryBytes;

        public bool Report(ulong availableMemoryBytes, int severity, int source)
        {
            if (!Enum.IsDefined((MemoryPressureSeverity)severity) ||
                !Enum.IsDefined((MemoryPressureSource)source))
            {
                return false;
            }

            lock (_lock)
            {
                if (!_accepting)
                {
                    return false;
                }

                MemoryPressureSeverity reportedSeverity = (MemoryPressureSeverity)severity;
                MemoryPressureSource reportedSource = (MemoryPressureSource)source;

                if (!_pending || _severity == MemoryPressureSeverity.Observe)
                {
                    _pending = true;
                    _severity = reportedSeverity;
                    _sources = reportedSource;
                    _availableMemoryBytes = availableMemoryBytes;
                }
                else if (reportedSeverity != MemoryPressureSeverity.Observe)
                {
                    _severity = (MemoryPressureSeverity)Math.Max((int)_severity, severity);
                    _sources |= reportedSource;
                    _availableMemoryBytes = Math.Min(_availableMemoryBytes, availableMemoryBytes);
                }
            }

            return true;
        }

        public void StopAccepting()
        {
            lock (_lock)
            {
                _accepting = false;
                _pending = false;
                _severity = default;
                _sources = default;
                _availableMemoryBytes = 0;
            }
        }

        public bool TryConsume(out MemoryPressureRequest request)
        {
            lock (_lock)
            {
                if (!_pending)
                {
                    request = default;
                    return false;
                }

                request = new MemoryPressureRequest(_severity, _sources, _availableMemoryBytes);
                _pending = false;
                _severity = default;
                _sources = default;
                _availableMemoryBytes = 0;
                return true;
            }
        }
    }
}
