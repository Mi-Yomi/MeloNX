using Ryujinx.Common.Diagnostics;
using Ryujinx.Common.Memory;
using System;
using System.Text.Json;
using System.Threading;

namespace Ryujinx.Graphics.Gpu
{
    public sealed partial class GpuContext
    {
        private readonly BoundedDiagnosticJson _forensicOutput = new();
        private readonly ForensicSnapshotCache _forensicProducer = new();
        private readonly ForensicStage _forensicPressureStage = new();
        private long _forensicPressureReports;
        private long _forensicPressureAccepted;
        private long _forensicPressureProcessed;

        // Invoked only by the GPU producer. No GPU/guest collection is read by the native sampler.
        private void PublishMemoryForensics()
        {
            _forensicProducer.Publish(Environment.TickCount64, writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("accounting", "logical_and_virtual_not_additive_to_physical_footprint");
                writer.WriteNumber("sequence", SequenceNumber);
                writer.WriteNumber("sync", SyncNumber);
                writer.WriteNumber("deferred_actions", DeferredActions.Count);
                writer.WriteNumber("buffer_migrations", BufferMigrations.Count);
                writer.WriteNumber("sync_actions", SyncActions.Count);
                writer.WriteNumber("syncpoint_actions", SyncpointActions.Count);
                writer.WriteString("presentation", Window.GetDiagnosticSnapshot());
                writer.WriteStartArray("physical_memories");
                int included = 0;
                foreach (var entry in PhysicalMemoryRegistry)
                {
                    if (included++ >= 4) break;
                    var memory = entry.Value;
                    memory.BufferCache.PublishDiagnosticSnapshot();
                    writer.WriteStartObject();
                    writer.WriteNumber("pid", entry.Key);
                    writer.WriteString("guest_owners", memory.GetMemoryOwnerSnapshot());
                    writer.WriteNumber("texture_cache_bytes", memory.TextureCache.CachedBytes);
                    writer.WriteNumber("buffer_diagnostic_publish_failures", memory.BufferCache.DiagnosticPublishFailures);
                    writer.WritePropertyName("buffer_cache");
                    writer.WriteRawValue(memory.BufferCache.GetDiagnosticSnapshotUtf8().Span, true);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteNumber("physical_memory_count", PhysicalMemoryRegistry.Count);
                writer.WriteStartArray("scratch_purposes");
                for (MemoryOwnerPurpose purpose = 0; purpose < MemoryOwnerPurpose.Count; purpose++)
                {
                    var pool = MemoryOwner<byte>.GetPoolStatistics(purpose);
                    if (pool.Rents == 0) continue;
                    writer.WriteStartObject();
                    writer.WriteString("purpose", purpose.ToString());
                    writer.WriteNumber("retained_bytes", pool.RetainedBytes);
                    writer.WriteNumber("leased_bytes", pool.LeasedBytes);
                    writer.WriteNumber("peak_leased_bytes", pool.PeakLeasedBytes);
                    writer.WriteNumber("created_arrays", pool.CreatedArrays);
                    writer.WriteNumber("created_bytes", pool.CreatedBytes);
                    writer.WriteNumber("rents", pool.Rents);
                    writer.WriteNumber("reuses", pool.Reuses);
                    writer.WriteNumber("discarded_arrays", pool.DiscardedArrays);
                    writer.WriteNumber("discarded_bytes", pool.DiscardedBytes);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            });
        }

        /// <summary>Bounded diagnostic copy. Never invokes GPU work or reads live owner collections.</summary>
        public int CopyMemoryForensicSnapshot(Span<byte> output) => _forensicOutput.TryCopy(output, writer =>
        {
            long now = Environment.TickCount64;
            writer.WriteStartObject();
            writer.WriteNumber("schema_version", 1);
            writer.WriteNumber("monotonic_ms", now);
            writer.WriteString("accounting", "overlapping_not_additive;_gc_fields_are_last_collection");
            writer.WritePropertyName("producer");
            _forensicProducer.Write(writer, now);
            writer.WritePropertyName("renderer");
            Renderer.WriteMemoryForensicState(writer, now);
            writer.WriteStartObject("pressure");
            writer.WriteNumber("reports", Interlocked.Read(ref _forensicPressureReports));
            writer.WriteNumber("accepted", Interlocked.Read(ref _forensicPressureAccepted));
            writer.WriteNumber("processed", Interlocked.Read(ref _forensicPressureProcessed));
            writer.WritePropertyName("producer_stage");
            _forensicPressureStage.Write(writer, now);
            writer.WriteEndObject();
            WriteManagedForensics(writer);
            writer.WriteEndObject();
        });

        private static void WriteManagedForensics(Utf8JsonWriter writer)
        {
            GCMemoryInfo gc = GC.GetGCMemoryInfo();
            writer.WriteStartObject("managed");
            writer.WriteNumber("allocated_bytes_total", GC.GetTotalAllocatedBytes(false));
            writer.WriteNumber("current_heap_estimate_bytes", GC.GetTotalMemory(false));
            writer.WriteNumber("gen0_collections", GC.CollectionCount(0));
            writer.WriteNumber("gen1_collections", GC.CollectionCount(1));
            writer.WriteNumber("gen2_collections", GC.CollectionCount(2));
            writer.WriteStartObject("last_gc");
            writer.WriteNumber("index", gc.Index);
            writer.WriteNumber("generation", gc.Generation);
            writer.WriteBoolean("concurrent", gc.Concurrent);
            writer.WriteBoolean("compacted", gc.Compacted);
            writer.WriteNumber("heap_bytes", gc.HeapSizeBytes);
            writer.WriteNumber("committed_bytes", gc.TotalCommittedBytes);
            writer.WriteNumber("fragmented_bytes", gc.FragmentedBytes);
            writer.WriteNumber("promoted_bytes", gc.PromotedBytes);
            writer.WriteNumber("pinned_objects", gc.PinnedObjectsCount);
            writer.WriteNumber("finalization_pending", gc.FinalizationPendingCount);
            writer.WriteNumber("memory_load_bytes", gc.MemoryLoadBytes);
            writer.WriteNumber("total_available_memory_bytes", gc.TotalAvailableMemoryBytes);
            writer.WriteStartArray("generation_sizes");
            int index = 0;
            foreach (GCGenerationInfo generation in gc.GenerationInfo)
            {
                writer.WriteStartObject();
                writer.WriteNumber("index", index++); // 0,1,2,LOH,POH; all from the same last GC.
                writer.WriteNumber("before_bytes", generation.SizeBeforeBytes);
                writer.WriteNumber("after_bytes", generation.SizeAfterBytes);
                writer.WriteNumber("fragmented_before_bytes", generation.FragmentationBeforeBytes);
                writer.WriteNumber("fragmented_after_bytes", generation.FragmentationAfterBytes);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartArray("pause_ms");
            foreach (TimeSpan pause in gc.PauseDurations) writer.WriteNumberValue(pause.TotalMilliseconds);
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
    }
}
