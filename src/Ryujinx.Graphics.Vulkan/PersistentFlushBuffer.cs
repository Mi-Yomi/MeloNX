using Ryujinx.Graphics.GAL;
using Silk.NET.Vulkan;
using System;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace Ryujinx.Graphics.Vulkan
{
    internal class PersistentFlushBuffer : IDisposable
    {
        private readonly VulkanRenderer _gd;

        private BufferHolder _flushStorage;

        public PersistentFlushBuffer(VulkanRenderer gd)
        {
            _gd = gd;
        }

        private BufferHolder ResizeIfNeeded(int size)
        {
            BufferHolder flushStorage = _flushStorage;

            if (flushStorage == null || size > _flushStorage.Size)
            {
                flushStorage?.Dispose();

                flushStorage = _gd.BufferManager.Create(_gd, size, purpose: BufferAllocationPurpose.Readback);
                _flushStorage = flushStorage;
            }

            return flushStorage;
        }

        public Span<byte> GetBufferData(CommandBufferPool cbp, BufferHolder buffer, int offset, int size)
        {
            BufferHolder flushStorage = ResizeIfNeeded(size);
            Auto<DisposableBuffer> srcBuffer;

            using (CommandBufferScoped cbs = cbp.Rent())
            {
                srcBuffer = buffer.GetBuffer(cbs.CommandBuffer);
                Auto<DisposableBuffer> dstBuffer = flushStorage.GetBuffer(cbs.CommandBuffer);

                if (srcBuffer.TryIncrementReferenceCount())
                {
                    BufferHolder.Copy(_gd, cbs, srcBuffer, dstBuffer, offset, 0, size, registerSrcUsage: false);
                }
                else
                {
                    // Source buffer is no longer alive, don't copy anything to flush storage.
                    srcBuffer = null;
                }
            }

            flushStorage.WaitForFences();
            srcBuffer?.DecrementReferenceCount();
            return flushStorage.GetDataStorage(0, size);
        }

        public Span<byte> GetTextureData(CommandBufferPool cbp, TextureView view, int size)
        {
            TextureCreateInfo info = view.Info;

            BufferHolder flushStorage = ResizeIfNeeded(size);

            using (CommandBufferScoped cbs = cbp.Rent())
            {
                Buffer buffer = flushStorage.GetBuffer(cbs.CommandBuffer).Get(cbs).Value;
                Image image = view.GetImage().Get(cbs).Value;

                view.CopyFromOrToBuffer(cbs.CommandBuffer, buffer, image, size, true, 0, 0, info.GetLayers(), info.Levels, singleSlice: false);
            }

            flushStorage.WaitForFences();
            return flushStorage.GetDataStorage(0, size);
        }

        public Span<byte> GetTextureData(CommandBufferPool cbp, TextureView view, int size, int layer, int level)
        {
            BufferHolder flushStorage = ResizeIfNeeded(size);

            using (CommandBufferScoped cbs = cbp.Rent())
            {
                Buffer buffer = flushStorage.GetBuffer(cbs.CommandBuffer).Get(cbs).Value;
                Image image = view.GetImage().Get(cbs).Value;

                view.CopyFromOrToBuffer(cbs.CommandBuffer, buffer, image, size, true, layer, level, 1, 1, singleSlice: true);
            }

            flushStorage.WaitForFences();
            return flushStorage.GetDataStorage(0, size);
        }

        /// <summary>
        /// Releases the reusable readback allocation. The caller must ensure that a previously
        /// returned span is no longer in use; by contract that is true before the next readback.
        /// </summary>
        /// <returns>The capacity in bytes of the released buffer</returns>
        public long Trim()
        {
            BufferHolder flushStorage = _flushStorage;
            _flushStorage = null;
            flushStorage?.Dispose();

            return flushStorage?.Size ?? 0;
        }

        public void Dispose()
        {
            Trim();
        }
    }
}
