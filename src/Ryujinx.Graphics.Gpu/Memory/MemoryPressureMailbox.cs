using System;
using System.Threading;

namespace Ryujinx.Graphics.Gpu.Memory
{
    enum MemoryPressureSeverity
    {
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

        public static ulong CalculateBufferTarget(ulong configuredCapacity, MemoryPressureSeverity severity)
        {
            return severity == MemoryPressureSeverity.Critical ? 0 : configuredCapacity / 2;
        }

        public static ulong? CalculatePersistentBufferCapacity(
            ulong configuredCapacity,
            MemoryPressureSeverity severity,
            ulong availableMemoryBytes)
        {
            if (severity != MemoryPressureSeverity.Critical)
            {
                return null;
            }

            // Keep a small hot working set even in the emergency zone. The current pressure
            // pass still performs a one-shot trim to zero, while a persistent zero ceiling would
            // recreate every clean buffer on the next sequence and increase transient overlap.
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
    /// Tracks a configured cache budget and an optional session-long pressure ceiling.
    /// The pressure ceiling can only decrease and is reset by constructing a new cache.
    /// </summary>
    sealed class MonotonicMemoryPressureCapacity
    {
        private ulong _pressureCapacity = ulong.MaxValue;

        public ulong ConfiguredCapacity { get; private set; }
        public ulong EffectiveCapacity => Math.Min(ConfiguredCapacity, _pressureCapacity);
        public bool IsLatched => _pressureCapacity != ulong.MaxValue;

        public MonotonicMemoryPressureCapacity(ulong configuredCapacity)
        {
            ConfiguredCapacity = configuredCapacity;
        }

        /// <summary>
        /// Changes the normal cache budget without clearing or relaxing a pressure ceiling.
        /// </summary>
        public void Configure(ulong configuredCapacity)
        {
            ConfiguredCapacity = configuredCapacity;

            if (IsLatched)
            {
                _pressureCapacity = Math.Min(_pressureCapacity, configuredCapacity);
            }
        }

        /// <summary>
        /// Lowers the session-long pressure ceiling if the supplied value is more restrictive.
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

                if (!_pending)
                {
                    _pending = true;
                    _severity = reportedSeverity;
                    _sources = reportedSource;
                    _availableMemoryBytes = availableMemoryBytes;
                }
                else
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
