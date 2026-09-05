using System;
using System.Buffers;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;

namespace Ryujinx.Common.Diagnostics
{
    /// <summary>
    /// Reusable, hard-capped diagnostic storage. Failure never exposes a partial JSON packet.
    /// Only diagnostic writers share this gate; a competing capture returns immediately.
    /// </summary>
    public sealed class BoundedDiagnosticJson
    {
        public const int Unavailable = -1;
        public const int TooSmall = -2;
        public const int Busy = -3;
        public const int Failed = -4;
        public const int MaximumCapacity = 64 * 1024;

        private sealed class FixedWriter : IBufferWriter<byte>
        {
            internal readonly byte[] Buffer;
            internal int Length;

            internal FixedWriter(int capacity) => Buffer = new byte[capacity];
            public void Advance(int count)
            {
                if (count < 0 || count > Buffer.Length - Length) throw new InsufficientMemoryException();
                Length += count;
            }
            public Memory<byte> GetMemory(int sizeHint = 0)
            {
                if (Math.Max(1, sizeHint) > Buffer.Length - Length) throw new InsufficientMemoryException();
                return Buffer.AsMemory(Length);
            }
            public Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;
        }

        private readonly FixedWriter _buffer;
        private readonly object _gate = new();
        private bool _writing; // Monitor is reentrant; recursive entry must still fail.
        private readonly Utf8JsonWriter _writer;

        public BoundedDiagnosticJson(int capacity = MaximumCapacity)
        {
            if (capacity < 256 || capacity > MaximumCapacity) throw new ArgumentOutOfRangeException(nameof(capacity));
            _buffer = new FixedWriter(capacity);
            _writer = new Utf8JsonWriter(_buffer);
        }

        public int TryCopy(Span<byte> destination, Action<Utf8JsonWriter> write)
        {
            if (!Monitor.TryEnter(_gate)) return Busy;
            try
            {
                if (_writing) return Busy;
                _writing = true;
                try
                {
                    _buffer.Length = 0;
                    _writer.Reset(_buffer);
                    write(_writer);
                    _writer.Flush();
                    if (_buffer.Length == 0 || _writer.CurrentDepth != 0) return Failed;
                    if (_buffer.Length > destination.Length) return TooSmall;
                    _buffer.Buffer.AsSpan(0, _buffer.Length).CopyTo(destination);
                    return _buffer.Length;
                }
                catch (InsufficientMemoryException) { return TooSmall; }
                catch (Exception) { return Failed; }
                finally { _writing = false; }
            }
            finally { Monitor.Exit(_gate); }
        }
    }

    /// <summary>
    /// The owner thread publishes immutable JSON. Readers never visit renderer/cache locks.
    /// A stalled or failed publisher leaves its last sample with its original timestamp.
    /// </summary>
    public sealed class ForensicSnapshotCache
    {
        private sealed record Packet(byte[] Json, long CapturedAt, double DurationMs);
        private readonly BoundedDiagnosticJson _builder = new(24 * 1024);
        private readonly byte[] _output = new byte[24 * 1024];
        private Packet _packet;
        private long _lastAttempt = long.MinValue;
        private long _failures;

        public void Publish(long now, Action<Utf8JsonWriter> write)
        {
            if (_lastAttempt != long.MinValue && now - _lastAttempt < 1000) return;
            _lastAttempt = now; // Owner thread only, including the callback and output buffer.
            long started = Stopwatch.GetTimestamp();
            int size = _builder.TryCopy(_output, write);
            if (size <= 0)
            {
                Interlocked.Increment(ref _failures);
                return;
            }
            try
            {
                Volatile.Write(ref _packet, new Packet(_output.AsSpan(0, size).ToArray(), now,
                    Stopwatch.GetElapsedTime(started).TotalMilliseconds));
            }
            catch (Exception)
            {
                Interlocked.Increment(ref _failures);
            }
        }

        public void Write(Utf8JsonWriter writer, long now)
        {
            Packet packet = Volatile.Read(ref _packet);
            writer.WriteStartObject();
            writer.WriteBoolean("observed", packet != null);
            writer.WriteNumber("publish_failures", Interlocked.Read(ref _failures));
            if (packet != null)
            {
                writer.WriteNumber("captured_at_monotonic_ms", packet.CapturedAt);
                writer.WriteNumber("age_ms", Math.Max(0, now - packet.CapturedAt));
                writer.WriteNumber("capture_duration_ms", packet.DurationMs);
                writer.WritePropertyName("data");
                writer.WriteRawValue(packet.Json, skipInputValidation: true);
            }
            writer.WriteEndObject();
        }
    }

    public sealed class ForensicStage
    {
        private string _name = "not_started";
        private long _startedAt = Environment.TickCount64;
        private long _version;

        // Single owner writes literal phase names. No allocations during a low-memory trim.
        public void Set(string name)
        {
            Interlocked.Increment(ref _version);
            Volatile.Write(ref _name, name);
            Interlocked.Exchange(ref _startedAt, Environment.TickCount64);
            Interlocked.Increment(ref _version);
        }

        public void Write(Utf8JsonWriter writer, long now)
        {
            long version = Interlocked.Read(ref _version);
            string name = Volatile.Read(ref _name);
            long startedAt = Interlocked.Read(ref _startedAt);
            bool consistent = (version & 1) == 0 && version == Interlocked.Read(ref _version);
            writer.WriteStartObject();
            writer.WriteString("phase", name);
            writer.WriteNumber("sequence", version / 2);
            writer.WriteBoolean("consistent", consistent);
            writer.WriteNumber("age_ms", Math.Max(0, now - startedAt));
            writer.WriteEndObject();
        }
    }
}
