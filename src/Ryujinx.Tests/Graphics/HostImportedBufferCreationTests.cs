using NUnit.Framework;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Vulkan;
using Silk.NET.Core.Contexts;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace Ryujinx.Tests.Graphics
{
    public class HostImportedBufferCreationTests
    {
        [TestCase(Result.ErrorFeatureNotPresent)]
        [TestCase(Result.ErrorOutOfDeviceMemory)]
        public void FailedNativeCreationReturnsPreparedImportWithoutDestroyingInvalidOutput(Result failure)
        {
            using Fixture fixture = new();
            fixture.Prepare();
            fixture.CreateResult = failure;
            Assert.Throws<VulkanException>(() => fixture.Create());
            Assert.That(fixture.Events, Is.EqualTo(new[] { "create", "free" }));
            Assert.That(fixture.DestroyedBuffers, Is.Empty);
            Assert.That(fixture.Manager.BufferCount, Is.Zero);
            Assert.That(fixture.Imports.GetImportStatistics(), Is.EqualTo((0L, 0L)));
            Assert.Throws<InvalidOperationException>(() => fixture.Imports.GetExistingAllocation(Fixture.Address, Fixture.Size));
        }

        [TestCase(Result.ErrorOutOfDeviceMemory)]
        [TestCase(Result.ErrorMemoryMapFailed)]
        public void FailedNativeBindingDestroysBufferBeforeReturningPreparedImport(Result failure)
        {
            using Fixture fixture = new();
            fixture.Prepare();
            fixture.BindResult = failure;
            Assert.Throws<VulkanException>(() => fixture.Create());
            Assert.That(fixture.Events, Is.EqualTo(new[] { "create", "bind", "destroy", "free" }));
            Assert.That(fixture.DestroyedBuffers, Is.EqualTo(new[] { 42UL }));
            Assert.That(fixture.Manager.BufferCount, Is.Zero);
            Assert.That(fixture.Imports.GetImportStatistics(), Is.EqualTo((0L, 0L)));
        }

        [Test]
        public void SuccessfulImportedBufferKeepsMemoryUntilItsLastCommandReferenceRetires()
        {
            using Fixture fixture = new();
            fixture.Prepare();
            BufferHandle handle = fixture.Create();
            Auto<DisposableBuffer> native = fixture.Manager.GetBuffer(default, handle, false);
            native.IncrementReferenceCount(); // The command pool borrows the same Auto reference.
            fixture.Manager.Delete(handle);
            Assert.That(fixture.Events, Is.EqualTo(new[] { "create", "bind" }));
            Assert.That(fixture.Imports.GetImportStatistics(), Is.EqualTo(((long)Environment.SystemPageSize, 1L)));
            native.DecrementReferenceCount(0);
            Assert.That(fixture.Events, Is.EqualTo(new[] { "create", "bind", "destroy", "free" }));
            Assert.That(fixture.DestroyedBuffers, Is.EqualTo(new[] { 42UL }));
            Assert.That(fixture.Imports.GetImportStatistics(), Is.EqualTo((0L, 0L)));
            Assert.That(native.GetUnsafe().Value.Handle, Is.Zero);
            GC.KeepAlive(native);
        }

        [Test]
        public void FailedCreationReleasesOnlyItsOwnPreparationOfSharedHostRange()
        {
            using Fixture fixture = new();
            fixture.Prepare();
            fixture.Prepare();
            fixture.CreateResult = Result.ErrorFeatureNotPresent;
            Assert.Throws<VulkanException>(() => fixture.Create());
            Assert.That(fixture.Events, Is.EqualTo(new[] { "create" }));
            Assert.That(fixture.Imports.GetImportStatistics(), Is.EqualTo(((long)Environment.SystemPageSize, 1L)));
            fixture.CreateResult = Result.Success;
            BufferHandle handle = fixture.Create();
            fixture.Manager.Delete(handle);
            Assert.That(fixture.Events, Is.EqualTo(new[] { "create", "create", "bind", "destroy", "free" }));
            Assert.That(fixture.Imports.GetImportStatistics(), Is.EqualTo((0L, 0L)));
        }

        [Test]
        public void MissingPreparationDoesNotCreateAnUnownedNativeBuffer()
        {
            using Fixture fixture = new();
            Assert.Throws<InvalidOperationException>(() => fixture.Create());
            Assert.That(fixture.Events, Is.Empty);
            Assert.That(fixture.Manager.BufferCount, Is.Zero);
        }

        private sealed unsafe class Fixture : IDisposable
        {
            internal static nint Address => (nint)0x1000000;
            internal const ulong Size = 256;

            [UnmanagedFunctionPointer(CallingConvention.Winapi)]
            private delegate Result HostPropertiesDelegate(Device device, ExternalMemoryHandleTypeFlags type, void* pointer, MemoryHostPointerPropertiesEXT* properties);
            [UnmanagedFunctionPointer(CallingConvention.Winapi)]
            private delegate Result AllocateDelegate(Device device, MemoryAllocateInfo* info, AllocationCallbacks* allocator, DeviceMemory* memory);
            [UnmanagedFunctionPointer(CallingConvention.Winapi)]
            private delegate void FreeDelegate(Device device, DeviceMemory memory, AllocationCallbacks* allocator);
            [UnmanagedFunctionPointer(CallingConvention.Winapi)]
            private delegate Result CreateDelegate(Device device, BufferCreateInfo* info, AllocationCallbacks* allocator, VkBuffer* buffer);
            [UnmanagedFunctionPointer(CallingConvention.Winapi)]
            private delegate Result BindDelegate(Device device, VkBuffer buffer, DeviceMemory memory, ulong offset);
            [UnmanagedFunctionPointer(CallingConvention.Winapi)]
            private delegate void DestroyDelegate(Device device, VkBuffer buffer, AllocationCallbacks* allocator);

            private readonly HostPropertiesDelegate _properties;
            private readonly AllocateDelegate _allocate;
            private readonly FreeDelegate _free;
            private readonly CreateDelegate _create;
            private readonly BindDelegate _bind;
            private readonly DestroyDelegate _destroy;
            private readonly Vk _api;
            private readonly ExtExternalMemoryHost _extension;
            private readonly VulkanRenderer _renderer;
            public HostMemoryAllocator Imports { get; }
            public BufferManager Manager { get; }
            public Result CreateResult { get; set; } = Result.Success;
            public Result BindResult { get; set; } = Result.Success;
            public List<string> Events { get; } = [];
            public List<ulong> DestroyedBuffers { get; } = [];

            public Fixture()
            {
                _properties = (_, _, _, properties) =>
                {
                    *properties = new MemoryHostPointerPropertiesEXT { SType = StructureType.MemoryHostPointerPropertiesExt, MemoryTypeBits = 1 };
                    return Result.Success;
                };
                _allocate = (_, _, _, memory) =>
                {
                    *memory = new DeviceMemory(9);
                    return Result.Success;
                };
                _free = (_, _, _) => Events.Add("free");
                _create = (_, _, _, buffer) =>
                {
                    Events.Add("create");
                    // Error output is deliberately nonzero: it is NOT a created handle.
                    *buffer = new VkBuffer(CreateResult == Result.Success ? 42UL : 0xdeadUL);
                    return CreateResult;
                };
                _bind = (_, _, _, _) =>
                {
                    Events.Add("bind");
                    return BindResult;
                };
                _destroy = (_, buffer, _) =>
                {
                    Events.Add("destroy");
                    DestroyedBuffers.Add(buffer.Handle);
                };
                nint Resolve(string name) => name switch
                {
                    "vkGetMemoryHostPointerPropertiesEXT" => Marshal.GetFunctionPointerForDelegate(_properties),
                    "vkAllocateMemory" => Marshal.GetFunctionPointerForDelegate(_allocate),
                    "vkFreeMemory" => Marshal.GetFunctionPointerForDelegate(_free),
                    "vkCreateBuffer" => Marshal.GetFunctionPointerForDelegate(_create),
                    "vkBindBufferMemory" => Marshal.GetFunctionPointerForDelegate(_bind),
                    "vkDestroyBuffer" => Marshal.GetFunctionPointerForDelegate(_destroy),
                    _ => throw new InvalidOperationException($"Unexpected native operation: {name}"),
                };
                _api = new Vk(new LamdaNativeContext(Resolve));
                _extension = new ExtExternalMemoryHost(new LamdaNativeContext(Resolve));
                PhysicalDeviceMemoryProperties memoryProperties = new() { MemoryTypeCount = 1 };
                memoryProperties.MemoryTypes[0] = new MemoryType { PropertyFlags = BufferManager.DefaultBufferMemoryFlags };
                object physicalDevice = default(VulkanPhysicalDevice);
                typeof(VulkanPhysicalDevice).GetField("PhysicalDeviceMemoryProperties").SetValue(physicalDevice, memoryProperties);
                MemoryAllocator backing = (MemoryAllocator)RuntimeHelpers.GetUninitializedObject(typeof(MemoryAllocator));
                Set(backing, "_physicalDevice", physicalDevice);
                Imports = new HostMemoryAllocator(backing, _api, _extension, default);
                _renderer = (VulkanRenderer)RuntimeHelpers.GetUninitializedObject(typeof(VulkanRenderer));
                Set(_renderer, "<Api>k__BackingField", _api);
                Set(_renderer, "<HostMemoryAllocator>k__BackingField", Imports);
                Manager = (BufferManager)RuntimeHelpers.GetUninitializedObject(typeof(BufferManager));
                Set(Manager, "_buffers", new IdList<BufferHolder>());
            }

            public void Prepare()
            {
                MemoryRequirements requirements = new() { Size = Size, Alignment = (ulong)Environment.SystemPageSize, MemoryTypeBits = 1 };
                Assert.That(Imports.TryImport(requirements, BufferManager.DefaultBufferMemoryFlags, Address, Size), Is.True);
            }

            public BufferHandle Create() => Manager.CreateHostImported(_renderer, Address, (int)Size);

            public void Dispose()
            {
                _extension.Dispose();
                _api.Dispose();
                GC.KeepAlive(_properties);
                GC.KeepAlive(_allocate);
                GC.KeepAlive(_free);
                GC.KeepAlive(_create);
                GC.KeepAlive(_bind);
                GC.KeepAlive(_destroy);
            }

            private static void Set(object target, string name, object value) =>
                target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);
        }
    }
}
