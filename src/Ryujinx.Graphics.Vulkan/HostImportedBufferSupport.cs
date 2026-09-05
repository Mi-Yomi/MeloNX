using Ryujinx.Common.Logging;
using Silk.NET.Vulkan;
using System;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace Ryujinx.Graphics.Vulkan
{
    internal static class HostImportedBufferSupport
    {
        internal static BufferUsageFlags GetUsage(bool indirect) =>
            BufferUsageFlags.TransferSrcBit | BufferUsageFlags.TransferDstBit |
            (indirect ? BufferUsageFlags.IndirectBufferBit : 0);

        internal static unsafe Result Create(Vk api, Device device, int size, bool indirect, out VkBuffer buffer)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size);
            ExternalMemoryBufferCreateInfo external = new()
            {
                SType = StructureType.ExternalMemoryBufferCreateInfo,
                HandleTypes = ExternalMemoryHandleTypeFlags.HostAllocationBitExt,
            };
            BufferCreateInfo info = new()
            {
                SType = StructureType.BufferCreateInfo,
                Size = (ulong)size,
                Usage = GetUsage(indirect),
                SharingMode = SharingMode.Exclusive,
                PNext = &external,
            };
            return api.CreateBuffer(device, in info, null, out buffer);
        }

        internal static unsafe bool TryGetRequirements(
            Vk api, Device device, bool extensionSupported, bool indirect, out MemoryRequirements requirements)
        {
            requirements = default;
            if (!extensionSupported) return false;

            // Extension advertisement and host-pointer allocation alone are insufficient:
            // MoltenVK 1.4.0 rejected this exact pNext despite advertising host imports.
            // Probe the SAME creation path before TextureGroup commits to aliased readback.
            Result result = Create(api, device, Environment.SystemPageSize, indirect, out VkBuffer buffer);
            if (result is Result.ErrorFeatureNotPresent or Result.ErrorExtensionNotPresent or Result.ErrorFormatNotSupported)
            {
                Logger.Warning?.PrintMsg(LogClass.Gpu,
                    $"Host-import buffer probe rejected HOST_ALLOCATION: {result}; using copied texture readback.");
                return false;
            }
            result.ThrowOnError();

            try
            {
                api.GetBufferMemoryRequirements(device, buffer, out requirements);
                bool supported = requirements.MemoryTypeBits != 0 && requirements.Alignment != 0 && requirements.Size != 0;
                Logger.Info?.PrintMsg(LogClass.Gpu,
                    $"Host-import buffer probe: HOST_ALLOCATION create={result}, enabled={supported}, " +
                    $"memory_types=0x{requirements.MemoryTypeBits:x}, alignment={requirements.Alignment}.");
                return supported;
            }
            finally
            {
                api.DestroyBuffer(device, buffer, null);
            }
        }
    }
}
