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
        public static ulong CalculateBufferTarget(ulong configuredCapacity, MemoryPressureSeverity severity)
        {
            return severity == MemoryPressureSeverity.Critical ? 0 : configuredCapacity / 2;
        }

        public static ulong CalculateTextureTarget(ulong configuredCapacity, MemoryPressureSeverity severity)
        {
            return severity == MemoryPressureSeverity.Critical ? 0 : configuredCapacity / 2;
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
