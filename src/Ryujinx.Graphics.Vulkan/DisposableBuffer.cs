using Silk.NET.Vulkan;
using System;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace Ryujinx.Graphics.Vulkan
{
    readonly struct DisposableBuffer : IDisposable
    {
        private readonly Vk _api;
        private readonly Device _device;
        private readonly BufferHolder _owner;

        public Buffer Value { get; }

        public DisposableBuffer(Vk api, Device device, Buffer buffer, BufferHolder owner = null)
        {
            _api = api;
            _device = device;
            _owner = owner;
            Value = buffer;
        }

        public void Dispose()
        {
            try
            {
                _api.DestroyBuffer(_device, Value, Span<AllocationCallbacks>.Empty);
                _owner?.RecordNativeDestroyed();
            }
            finally
            {
                // Auto calls this only after all command-buffer/view references retire.
                // A dispose request on BufferHolder alone is too early to return mirrors.
                _owner?.ReleasePendingData();
            }
        }
    }
}
