using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace Ryujinx.Common.Diagnostics
{
    /// <summary>
    /// Bounded, allocation-free event accounting. IDs must be lifetime IDs, never addresses or handles.
    /// Samples are illustrative; cumulative counters, not the ring, measure event rates.
    /// </summary>
    public sealed class ResourceEventCounters
    {
        public const int EventCapacity = 64;
        private const int BucketCount = 4;
        private readonly string[] _reasons;
        private readonly string[] _kinds;
        private readonly long[] _counts;
        private readonly long[] _bytes;
        private readonly long[] _reasonCounts;
        private readonly object _eventLock = new();
        private readonly Event[] _events = new Event[EventCapacity];
        private long _sequence;
        private long _sampleCount;
        private long _droppedSamples;
        private int _eventCount;
        private int _nextEvent;

        private readonly record struct Event(long Sequence, long Milliseconds, int Reason, int Kind,
            long Id, long RelatedId, long Bytes);

        public ResourceEventCounters(string[] reasons, string[] kinds)
        {
            _reasons = (string[])reasons.Clone();
            _kinds = (string[])kinds.Clone();
            _counts = new long[reasons.Length * kinds.Length * BucketCount];
            _bytes = new long[_counts.Length];
            _reasonCounts = new long[reasons.Length];
        }

        public static int GetSizeBucket(long bytes) => bytes <= 4096 ? 0 : bytes <= 65536 ? 1 : bytes <= 1048576 ? 2 : 3;

        public void Record(int reason, int kind, long id, long bytes, long relatedId = 0)
        {
            int index = (reason * _kinds.Length + kind) * BucketCount + GetSizeBucket(bytes);
            Interlocked.Increment(ref _counts[index]);
            Interlocked.Add(ref _bytes[index], bytes);
            long reasonCount = Interlocked.Increment(ref _reasonCounts[reason]);
            long sequence = Interlocked.Increment(ref _sequence);
            if (reasonCount > 16 && (reasonCount & 63) != 0)
            {
                return;
            }

            if (!Monitor.TryEnter(_eventLock))
            {
                Interlocked.Increment(ref _droppedSamples);
                return;
            }

            try
            {
                _events[_nextEvent] = new Event(sequence, Environment.TickCount64, reason, kind, id, relatedId, bytes);
                _nextEvent = (_nextEvent + 1) % EventCapacity;
                _eventCount = Math.Min(_eventCount + 1, EventCapacity);
                _sampleCount++;
            }
            finally
            {
                Monitor.Exit(_eventLock);
            }
        }

        public long GetCount(int reason) => Interlocked.Read(ref _reasonCounts[reason]);

        public long GetBytes(int reason)
        {
            long bytes = 0;
            int start = reason * _kinds.Length * BucketCount;
            for (int i = start; i < start + _kinds.Length * BucketCount; i++)
            {
                bytes += Interlocked.Read(ref _bytes[i]);
            }
            return bytes;
        }

        /// <summary>Call on the owner only, at a bounded cadence; readers use the owner's cached string.</summary>
        public string CreateSnapshot(Action<Utf8JsonWriter> writeOwnerFields)
        {
            return Encoding.UTF8.GetString(CreateSnapshotUtf8(writeOwnerFields));
        }

        public byte[] CreateSnapshotUtf8(Action<Utf8JsonWriter> writeOwnerFields)
        {
            using MemoryStream stream = new(16384);
            using (Utf8JsonWriter writer = new(stream))
            {
                writer.WriteStartObject();
                writer.WriteNumber("sampled_at_monotonic_ms", Environment.TickCount64);
                writer.WriteString("consistency", "approximate_atomic_counters");
                writeOwnerFields?.Invoke(writer);
                writer.WriteStartObject("cumulative");
                for (int reason = 0; reason < _reasons.Length; reason++)
                {
                    writer.WriteStartObject(_reasons[reason]);
                    writer.WriteNumber("count", GetCount(reason));
                    writer.WriteNumber("logical_bytes", GetBytes(reason));
                    writer.WriteStartObject("by_kind");
                    for (int kind = 0; kind < _kinds.Length; kind++)
                    {
                        writer.WriteStartObject(_kinds[kind]);
                        writer.WriteStartArray("size_bucket_counts");
                        int start = (reason * _kinds.Length + kind) * BucketCount;
                        for (int bucket = 0; bucket < BucketCount; bucket++)
                        {
                            writer.WriteNumberValue(Interlocked.Read(ref _counts[start + bucket]));
                        }
                        writer.WriteEndArray();
                        long bytes = 0;
                        for (int bucket = 0; bucket < BucketCount; bucket++)
                        {
                            bytes += Interlocked.Read(ref _bytes[start + bucket]);
                        }
                        writer.WriteNumber("logical_bytes", bytes);
                        writer.WriteEndObject();
                    }
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                }
                writer.WriteEndObject();
                writer.WriteString("size_buckets", "<=4KiB,<=64KiB,<=1MiB,>1MiB");
                writer.WriteNumber("events_total", Interlocked.Read(ref _sequence));
                writer.WriteNumber("events_contention_dropped", Interlocked.Read(ref _droppedSamples));
                writer.WriteString("event_sampling", "first_16_per_reason_then_every_64th;ring_64");
                bool acquired = Monitor.TryEnter(_eventLock);
                writer.WriteBoolean("events_busy", !acquired);
                try
                {
                    if (acquired) writer.WriteNumber("events_sampled_total", _sampleCount);
                    writer.WriteStartArray("recent_events");
                    if (acquired)
                    {
                        int first = (_nextEvent - _eventCount + EventCapacity) % EventCapacity;
                        for (int i = 0; i < _eventCount; i++)
                        {
                            Event item = _events[(first + i) % EventCapacity];
                            writer.WriteStartObject();
                            writer.WriteNumber("event", item.Sequence);
                            writer.WriteNumber("at_monotonic_ms", item.Milliseconds);
                            writer.WriteString("reason", _reasons[item.Reason]);
                            writer.WriteString("kind", _kinds[item.Kind]);
                            writer.WriteNumber("lifetime_id", item.Id);
                            if (item.RelatedId != 0) writer.WriteNumber("related_lifetime_id", item.RelatedId);
                            writer.WriteNumber("logical_bytes", item.Bytes);
                            writer.WriteEndObject();
                        }
                    }
                    writer.WriteEndArray();
                }
                finally
                {
                    if (acquired) Monitor.Exit(_eventLock);
                }
                writer.WriteEndObject();
            }
            return stream.ToArray();
        }
    }
}
