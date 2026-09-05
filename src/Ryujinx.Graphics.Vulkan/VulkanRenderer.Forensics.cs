using Ryujinx.Common.Diagnostics;
using System;
using System.Text.Json;

namespace Ryujinx.Graphics.Vulkan
{
    public sealed partial class VulkanRenderer
    {
        private readonly ForensicSnapshotCache _forensicBackend = new();
        private readonly ForensicStage _forensicTrimStage = new();

        // Only the backend thread visits these owners, between complete GAL commands.
        private void PublishMemoryForensics()
        {
            if (!OperatingSystem.IsIOS() || !_initialized || BufferManager == null) return;
            _forensicBackend.Publish(Environment.TickCount64, writer =>
            {
                BufferManager.PublishDiagnosticSnapshot();
                var textures = TextureStorage.GetOwnerStatistics();
                var imported = HostMemoryAllocator.GetImportStatistics();
                var allocation = MemoryAllocator.GetStatistics();
                var device = GetDeviceMemoryBudget();
                writer.WriteStartObject();
                writer.WriteString("accounting", "overlapping_not_additive;_driver_usage_overlaps_native_metal_allocated");
                writer.WriteNumber("texture_storage_owners", textures.Owners);
                writer.WriteNumber("texture_owner_logical_bytes", textures.LogicalBytes);
                writer.WriteNumber("texture_view_owners", textures.Views);
                writer.WriteNumber("host_import_count", imported.Count);
                writer.WriteNumber("host_import_mapped_bytes", imported.Bytes);
                writer.WriteNumber("allocator_reserved_bytes", allocation.ReservedBytes);
                writer.WriteNumber("allocator_used_bytes", allocation.UsedBytes);
                writer.WriteNumber("allocator_free_bytes", allocation.FreeBytes);
                writer.WriteNumber("allocator_blocks", allocation.Blocks);
                writer.WriteNumber("allocator_free_ranges", allocation.FreeRanges);
                writer.WriteNumber("allocator_largest_free_range_bytes", allocation.LargestFreeRangeBytes);
                writer.WriteNumber("driver_usage_bytes", device.Usage);
                writer.WriteNumber("driver_budget_bytes", device.Budget);
                writer.WriteNumber("buffer_diagnostic_publish_failures", BufferManager.DiagnosticPublishFailures);
                writer.WriteString("progress", GetDiagnosticSnapshot());
                writer.WritePropertyName("buffers");
                writer.WriteRawValue(BufferManager.GetDiagnosticSnapshotUtf8().Span, true);
                writer.WriteEndObject();
            });
        }

        // Sampler path: no owner getters, waits, GPU calls or cache locks here.
        public void WriteMemoryForensicState(Utf8JsonWriter writer, long now)
        {
            writer.WriteStartObject();
            writer.WritePropertyName("backend");
            _forensicBackend.Write(writer, now);
            writer.WritePropertyName("trim_stage");
            _forensicTrimStage.Write(writer, now);
            writer.WriteEndObject();
        }
    }
}
