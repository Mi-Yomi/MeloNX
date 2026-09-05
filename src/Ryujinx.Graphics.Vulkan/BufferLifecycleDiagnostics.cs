using Ryujinx.Common.Diagnostics;
using System;
using System.Text;
using System.Threading;

namespace Ryujinx.Graphics.Vulkan
{
    enum BufferAllocationPurpose { General, Query, Staging, Transient, Conversion, Imported, SparseAlias, Readback }

    sealed class BufferLifecycleDiagnostics
    {
        private readonly ResourceEventCounters _events = new(
            ["create_attempt", "created", "dispose_requested", "native_destroyed", "create_failed", "pressure_cache_trim"],
            ["general", "query", "staging", "transient", "conversion", "imported", "sparse_alias", "readback"]);
        private long _ownedNativeLogicalBytes;
        private long _aliasNativeLogicalBytes;
        private string _snapshot = "{\"status\":\"not_sampled\"}";
        private byte[] _snapshotUtf8 = "{\"status\":\"not_sampled\"}"u8.ToArray();
        private long _publishFailures;
        internal long PublishFailures => Interlocked.Read(ref _publishFailures);

        internal void Attempt(int size, BufferAllocationPurpose purpose) => _events.Record(0, (int)purpose, 0, size);
        internal void Failed(int size, BufferAllocationPurpose purpose) => _events.Record(4, (int)purpose, 0, size);

        private static bool IsAlias(BufferAllocationPurpose purpose) =>
            purpose == BufferAllocationPurpose.Imported || purpose == BufferAllocationPurpose.SparseAlias;

        internal void Created(long id, int size, BufferAllocationPurpose purpose)
        {
            _events.Record(1, (int)purpose, id, size);
            if (IsAlias(purpose)) Interlocked.Add(ref _aliasNativeLogicalBytes, size);
            else Interlocked.Add(ref _ownedNativeLogicalBytes, size);
        }

        internal void DisposeRequested(long id, int size, BufferAllocationPurpose purpose) => _events.Record(2, (int)purpose, id, size);

        internal void NativeDestroyed(long id, int size, BufferAllocationPurpose purpose)
        {
            _events.Record(3, (int)purpose, id, size);
            if (IsAlias(purpose)) Interlocked.Add(ref _aliasNativeLogicalBytes, -size);
            else Interlocked.Add(ref _ownedNativeLogicalBytes, -size);
        }

        internal void PressureTrim(long id, int size, BufferAllocationPurpose purpose) => _events.Record(5, (int)purpose, id, size);

        internal void Publish()
        {
            try
            {
                byte[] snapshotUtf8 = _events.CreateSnapshotUtf8(writer =>
                {
                    // Read retirement before creation. Each reason is cumulative and a
                    // resource is published only after Created; this order prevents a
                    // concurrent create+retire from inventing a negative derived gauge.
                    long destroyed = _events.GetCount(3);
                    long disposed = _events.GetCount(2);
                    long created = _events.GetCount(1);
                    long destroyedBytes = _events.GetBytes(3);
                    long disposedBytes = _events.GetBytes(2);
                    long createdBytes = _events.GetBytes(1);
                    writer.WriteString("byte_semantics", "VkBuffer_logical_sizes_not_allocation_or_physical_residency;aliases_not_additive");
                    writer.WriteNumber("publish_failures", PublishFailures);
                    writer.WriteNumber("active_logical_count", created - disposed);
                    writer.WriteNumber("active_logical_bytes", createdBytes - disposedBytes);
                    writer.WriteNumber("native_alive_count", created - destroyed);
                    writer.WriteNumber("native_alive_logical_bytes", createdBytes - destroyedBytes);
                    writer.WriteNumber("dispose_pending_count", disposed - destroyed);
                    writer.WriteNumber("dispose_pending_logical_bytes", disposedBytes - destroyedBytes);
                    writer.WriteNumber("native_alive_owned_logical_bytes", Interlocked.Read(ref _ownedNativeLogicalBytes));
                    writer.WriteNumber("native_alive_alias_logical_bytes", Interlocked.Read(ref _aliasNativeLogicalBytes));
                    writer.WriteString("native_destroyed_semantics", "after_vkDestroyBuffer;allocation_may_remain_shared_by_aliases");
                    writer.WriteString("pressure_cache_trim_semantics", "parent_buffers_visited;not_evicted_derived_bytes");
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
                // Keep the last published snapshot under terminal memory pressure.
            }
        }

        internal string GetSnapshot() => Volatile.Read(ref _snapshot);
        internal ReadOnlyMemory<byte> GetSnapshotUtf8() => Volatile.Read(ref _snapshotUtf8);
    }
}
