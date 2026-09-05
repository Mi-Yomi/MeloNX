using Ryujinx.Common.Diagnostics;
using System;
using System.Text;
using System.Threading;

namespace Ryujinx.Graphics.Gpu.Memory
{
    enum BufferCacheEvent
    {
        Created, MergeCreated, MergeRemoved, CapacityEvicted, PressureEvicted,
        RecreatedAfterCapacity, RecreatedAfterPressure, VirtualRebuildRemoved,
        BackingToDevice, BackingToHost, ShutdownRemoved,
    }

    sealed class BufferCacheDiagnostics
    {
        private static long _nextId;
        private readonly ResourceEventCounters _events = new(
            ["created", "merge_created", "merge_removed", "capacity_evicted", "pressure_evicted",
             "recreated_after_capacity", "recreated_after_pressure", "virtual_rebuild_removed",
             "backing_to_device", "backing_to_host", "shutdown_removed"],
            ["physical", "virtual_owned", "sparse_alias"]);
        // Bounded, exact comparison. Collisions lose evidence, never produce a false recreation.
        // These guest ranges are private matching keys and are never serialized.
        private readonly Eviction[] _recentEvictions = new Eviction[512];
        private long _lookupHits;
        private long _lookupMisses;
        private string _snapshot = "{\"status\":\"not_sampled\"}";
        private byte[] _snapshotUtf8 = "{\"status\":\"not_sampled\"}"u8.ToArray();
        private long _publishFailures;
        internal long PublishFailures => Interlocked.Read(ref _publishFailures);
        private readonly record struct Eviction(ulong Address, ulong Size, long Id, bool Pressure);

        internal static long NextId() => Interlocked.Increment(ref _nextId);
        private static int Slot(ulong address) => (int)(((address >> 12) ^ (address >> 21)) & 511);

        internal void Lookup(bool hit)
        {
            if (hit) _lookupHits++;
            else _lookupMisses++;
        }

        internal void Created(long id, ulong address, ulong size, bool merge)
        {
            Record(merge ? BufferCacheEvent.MergeCreated : BufferCacheEvent.Created, 0, id, size);
            ref Eviction previous = ref _recentEvictions[Slot(address)];
            if (previous.Id != 0 && previous.Address == address && previous.Size == size)
            {
                _events.Record((int)(previous.Pressure ? BufferCacheEvent.RecreatedAfterPressure : BufferCacheEvent.RecreatedAfterCapacity),
                    0, id, (long)size, previous.Id);
                previous = default;
            }
        }

        internal void Evicted(long id, ulong address, ulong size, bool pressure)
        {
            Record(pressure ? BufferCacheEvent.PressureEvicted : BufferCacheEvent.CapacityEvicted, 0, id, size);
            _recentEvictions[Slot(address)] = new Eviction(address, size, id, pressure);
        }

        internal void Record(BufferCacheEvent reason, int kind, long id, ulong size) =>
            _events.Record((int)reason, kind, id, (long)size);

        internal void Publish(ulong cached, ulong configured, ulong effective)
        {
            try
            {
                byte[] snapshotUtf8 = _events.CreateSnapshotUtf8(writer =>
                {
                    writer.WriteString("byte_semantics", "cache_owned_logical_bytes_not_physical_residency");
                    writer.WriteNumber("publish_failures", PublishFailures);
                    writer.WriteNumber("cached_logical_bytes", cached);
                    writer.WriteNumber("configured_bytes", configured);
                    writer.WriteNumber("effective_bytes", effective);
                    writer.WriteNumber("creation_lookup_hits", _lookupHits);
                    writer.WriteNumber("creation_lookup_misses", _lookupMisses);
                    writer.WriteNumber("recent_eviction_slots", _recentEvictions.Length);
                    writer.WriteString("recreation_coverage", "exact_physical_ranges;collisions_and_changed_ranges_underestimate");
                });
                if (snapshotUtf8.Length > 24 * 1024)
                {
                    Interlocked.Increment(ref _publishFailures);
                    return;
                }
                string snapshot = Encoding.UTF8.GetString(snapshotUtf8);
                Volatile.Write(ref _snapshot, snapshot);
                Volatile.Write(ref _snapshotUtf8, snapshotUtf8);
            }
            catch (OutOfMemoryException)
            {
                Interlocked.Increment(ref _publishFailures);
                // Reporting memory pressure must not turn it into an emulator failure.
            }
        }

        internal string GetSnapshot() => Volatile.Read(ref _snapshot);
        internal ReadOnlyMemory<byte> GetSnapshotUtf8() => Volatile.Read(ref _snapshotUtf8);
    }
}
