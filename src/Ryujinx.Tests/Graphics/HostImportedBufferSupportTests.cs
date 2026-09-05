using NUnit.Framework;
using Ryujinx.Graphics.Vulkan;
using Silk.NET.Core.Contexts;
using Silk.NET.Vulkan;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace Ryujinx.Tests.Graphics
{
    public class HostImportedBufferSupportTests
    {
        [Test]
        public void MissingExtensionSkipsEveryNativeCall()
        {
            using NativeFixture fixture = new();

            bool supported = HostImportedBufferSupport.TryGetRequirements(
                fixture.Api, fixture.Device, false, true, out MemoryRequirements requirements);

            Assert.Multiple(() =>
            {
                Assert.That(supported, Is.False);
                Assert.That(requirements, Is.EqualTo(default(MemoryRequirements)));
                Assert.That(fixture.Events, Is.Empty);
                Assert.That(fixture.Requests, Is.Empty);
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ProbeAndRuntimeCreateUseTheSameExternalHostBufferContract(bool indirect)
        {
            using NativeFixture fixture = new();

            Assert.That(HostImportedBufferSupport.TryGetRequirements(
                fixture.Api, fixture.Device, true, indirect, out MemoryRequirements requirements), Is.True);

            Assert.Multiple(() =>
            {
                Assert.That(requirements.Size, Is.EqualTo(fixture.Requirements.Size));
                Assert.That(requirements.Alignment, Is.EqualTo(fixture.Requirements.Alignment));
                Assert.That(requirements.MemoryTypeBits, Is.EqualTo(fixture.Requirements.MemoryTypeBits));
                Assert.That(fixture.Events, Is.EqualTo(new[] { "create:1", "requirements:1", "destroy:1" }));
            });

            const int realBufferSize = 65536;
            Assert.That(HostImportedBufferSupport.Create(
                fixture.Api, fixture.Device, realBufferSize, indirect, out VkBuffer runtimeBuffer), Is.EqualTo(Result.Success));
            Assert.That(runtimeBuffer.Handle, Is.EqualTo(2UL));
            Assert.That(fixture.Events, Is.EqualTo(new[] { "create:1", "requirements:1", "destroy:1", "create:2" }),
                "The runtime creation transfers ownership to its caller; it must not destroy that buffer.");

            BufferUsageFlags expectedUsage = BufferUsageFlags.TransferSrcBit | BufferUsageFlags.TransferDstBit;
            if (indirect)
            {
                expectedUsage |= BufferUsageFlags.IndirectBufferBit;
            }

            Assert.That(HostImportedBufferSupport.GetUsage(indirect), Is.EqualTo(expectedUsage));
            Assert.That(fixture.Requests, Has.Count.EqualTo(2));
            Assert.That(fixture.Requests[0].Size, Is.EqualTo((ulong)Environment.SystemPageSize));
            Assert.That(fixture.Requests[1].Size, Is.EqualTo((ulong)realBufferSize));
            foreach (CreateRequest request in fixture.Requests)
            {
                Assert.Multiple(() =>
                {
                    Assert.That(request.Device, Is.EqualTo(fixture.Device));
                    Assert.That(request.SType, Is.EqualTo(StructureType.BufferCreateInfo));
                    Assert.That(request.Flags, Is.EqualTo((BufferCreateFlags)0));
                    Assert.That(request.Usage, Is.EqualTo(expectedUsage));
                    Assert.That(request.SharingMode, Is.EqualTo(SharingMode.Exclusive));
                    Assert.That(request.QueueFamilyCount, Is.Zero);
                    Assert.That(request.QueueFamiliesNull, Is.True);
                    Assert.That(request.AllocatorNull, Is.True);
                    Assert.That(request.ExternalPresent, Is.True, "A normal buffer probe would miss the device regression.");
                    Assert.That(request.ExternalSType, Is.EqualTo(StructureType.ExternalMemoryBufferCreateInfo));
                    Assert.That(request.ExternalHandleTypes, Is.EqualTo(ExternalMemoryHandleTypeFlags.HostAllocationBitExt));
                    Assert.That(request.ExternalNextNull, Is.True);
                });
            }

            fixture.DestroyRuntimeBuffer(runtimeBuffer);
            Assert.That(fixture.Events[^1], Is.EqualTo("destroy:2"));
        }

        [TestCase(Result.ErrorFeatureNotPresent)]
        [TestCase(Result.ErrorExtensionNotPresent)]
        [TestCase(Result.ErrorFormatNotSupported)]
        public void UnsupportedCreationReturnsFalseWithoutQueryingOrDestroyingInvalidBuffer(Result result)
        {
            using NativeFixture fixture = new() { CreateResult = result };

            bool supported = HostImportedBufferSupport.TryGetRequirements(
                fixture.Api, fixture.Device, true, false, out MemoryRequirements requirements);

            Assert.Multiple(() =>
            {
                Assert.That(supported, Is.False);
                Assert.That(requirements, Is.EqualTo(default(MemoryRequirements)));
                Assert.That(fixture.Events, Is.EqualTo(new[] { "create:1" }),
                    "A failed create supplies no owned native handle: do not query it or call destroy(null).");
            });
        }

        [TestCase(Result.ErrorOutOfHostMemory)]
        [TestCase(Result.ErrorOutOfDeviceMemory)]
        [TestCase(Result.ErrorDeviceLost)]
        public void ResourceFailuresPropagateInsteadOfPretendingImportsAreUnsupported(Result result)
        {
            using NativeFixture fixture = new() { CreateResult = result };

            VulkanException error = Assert.Throws<VulkanException>(() =>
                HostImportedBufferSupport.TryGetRequirements(fixture.Api, fixture.Device, true, false, out _));

            Assert.That(error.Message, Does.Contain(result.ToString()));
            Assert.That(fixture.Events, Is.EqualTo(new[] { "create:1" }));
        }

        [TestCase(0UL, 4096UL, 5U)]
        [TestCase(8192UL, 0UL, 5U)]
        [TestCase(8192UL, 4096UL, 0U)]
        public void InvalidRequirementsRejectImportAndReleaseSuccessfulProbeExactlyOnce(ulong size, ulong alignment, uint memoryTypes)
        {
            using NativeFixture fixture = new()
            {
                Requirements = new MemoryRequirements { Size = size, Alignment = alignment, MemoryTypeBits = memoryTypes },
            };

            Assert.That(HostImportedBufferSupport.TryGetRequirements(
                fixture.Api, fixture.Device, true, false, out _), Is.False);
            Assert.That(fixture.Events, Is.EqualTo(new[] { "create:1", "requirements:1", "destroy:1" }));
        }

        [TestCase(0)]
        [TestCase(-1)]
        public void InvalidSizeNeverReachesNativeCreate(int size)
        {
            using NativeFixture fixture = new();
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                HostImportedBufferSupport.Create(fixture.Api, fixture.Device, size, false, out _));
            Assert.That(fixture.Events, Is.Empty);
        }

        private readonly record struct CreateRequest(
            Device Device, StructureType SType, BufferCreateFlags Flags, ulong Size, BufferUsageFlags Usage,
            SharingMode SharingMode, uint QueueFamilyCount, bool QueueFamiliesNull, bool AllocatorNull,
            bool ExternalPresent, StructureType ExternalSType, ExternalMemoryHandleTypeFlags ExternalHandleTypes,
            bool ExternalNextNull);

        // Fake only Vulkan's external ABI boundary. Both probe and real creation
        // execute the production helper, so an ordinary-buffer probe cannot pass.
        // Callbacks record values synchronously; no stack pNext pointer escapes.
        private sealed unsafe class NativeFixture : IDisposable
        {
            [UnmanagedFunctionPointer(CallingConvention.Winapi)]
            private delegate Result CreateBufferDelegate(Device device, BufferCreateInfo* info, AllocationCallbacks* allocator, VkBuffer* buffer);
            [UnmanagedFunctionPointer(CallingConvention.Winapi)]
            private delegate void GetRequirementsDelegate(Device device, VkBuffer buffer, MemoryRequirements* requirements);
            [UnmanagedFunctionPointer(CallingConvention.Winapi)]
            private delegate void DestroyBufferDelegate(Device device, VkBuffer buffer, AllocationCallbacks* allocator);

            private readonly CreateBufferDelegate _create;
            private readonly GetRequirementsDelegate _getRequirements;
            private readonly DestroyBufferDelegate _destroy;
            private int _createCount;

            internal Vk Api { get; }
            internal Device Device { get; } = new((nint)0x1234);
            internal Result CreateResult { get; set; } = Result.Success;
            internal MemoryRequirements Requirements { get; set; } = new() { Size = 8192, Alignment = 4096, MemoryTypeBits = 5 };
            internal List<CreateRequest> Requests { get; } = [];
            internal List<string> Events { get; } = [];

            internal NativeFixture()
            {
                _create = CreateBuffer;
                _getRequirements = (_, buffer, requirements) =>
                {
                    Events.Add($"requirements:{buffer.Handle}");
                    *requirements = Requirements;
                };
                _destroy = (_, buffer, _) => Events.Add($"destroy:{buffer.Handle}");
                Api = new Vk(new LamdaNativeContext(name => name switch
                {
                    "vkCreateBuffer" => Marshal.GetFunctionPointerForDelegate(_create),
                    "vkGetBufferMemoryRequirements" => Marshal.GetFunctionPointerForDelegate(_getRequirements),
                    "vkDestroyBuffer" => Marshal.GetFunctionPointerForDelegate(_destroy),
                    _ => throw new InvalidOperationException($"Unexpected native operation: {name}"),
                }));
            }

            private Result CreateBuffer(Device device, BufferCreateInfo* info, AllocationCallbacks* allocator, VkBuffer* buffer)
            {
                int id = ++_createCount;
                Events.Add($"create:{id}");
                ExternalMemoryBufferCreateInfo* external = (ExternalMemoryBufferCreateInfo*)info->PNext;
                Requests.Add(new CreateRequest(device, info->SType, info->Flags, info->Size, info->Usage,
                    info->SharingMode, info->QueueFamilyIndexCount, info->PQueueFamilyIndices == null, allocator == null,
                    external != null, external != null ? external->SType : default,
                    external != null ? external->HandleTypes : default, external == null || external->PNext == null));
                *buffer = CreateResult == Result.Success ? new VkBuffer((ulong)id) : default;
                return CreateResult;
            }

            internal void DestroyRuntimeBuffer(VkBuffer buffer) => Api.DestroyBuffer(Device, buffer, null);

            public void Dispose()
            {
                Api.Dispose();
                GC.KeepAlive(_create);
                GC.KeepAlive(_getRequirements);
                GC.KeepAlive(_destroy);
            }
        }
    }
}
