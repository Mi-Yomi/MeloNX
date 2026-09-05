using NUnit.Framework;
using Ryujinx.Graphics.Vulkan;
using Silk.NET.Core.Contexts;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Ryujinx.Tests.Graphics
{
    public class HostMemoryAllocatorLifetimeTests
    {
        [Test]
        public void FinalNativeReleaseRemovesRegistryAndAllowsFreshImportAtSameHostPointer()
        {
            using Fixture fixture = new();
            nint address = (nint)0x1000000;
            ulong size = (ulong)Environment.SystemPageSize;
            Auto<MemoryAllocation> first = fixture.Import(address, size);
            DeviceMemory firstMemory = first.GetUnsafe().Memory;

            // TryImport owns one reference. A native buffer borrows another, just
            // as Auto<DisposableBuffer>'s allocation dependency does in production.
            first.IncrementReferenceCount();
            first.DecrementReferenceCount();
            Assert.That(fixture.Allocator.GetImportStatistics(), Is.EqualTo(((long)size, 1L)));
            Assert.That(fixture.Allocator.GetExistingAllocation(address, size).Item1, Is.SameAs(first));
            Assert.That(fixture.Freed, Is.Empty);

            first.DecrementReferenceCount();
            Assert.That(fixture.Freed, Is.EqualTo(new[] { firstMemory.Handle }));
            Assert.That(fixture.Allocator.GetImportStatistics(), Is.EqualTo((0L, 0L)),
                "Native memory must not leave a stale registry owner after Auto clears its value.");
            Assert.That(fixture.StatisticsAtFree[0], Is.EqualTo((0L, 0L)));
            Assert.Throws<InvalidOperationException>(() => fixture.Allocator.GetExistingAllocation(address, size));
            Assert.That(first.GetUnsafe().Memory.Handle, Is.Zero);
            Assert.That(first.TryIncrementReferenceCount(), Is.False);

            Auto<MemoryAllocation> second = fixture.Import(address, size);
            Assert.That(second, Is.Not.SameAs(first));
            Assert.That(second.GetUnsafe().Memory.Handle, Is.Not.EqualTo(firstMemory.Handle));
            Assert.That(fixture.Allocations, Is.EqualTo(2));
            Assert.That(fixture.Allocator.GetImportStatistics(), Is.EqualTo(((long)size, 1L)));
            second.DecrementReferenceCount();
            Assert.That(fixture.Allocator.GetImportStatistics(), Is.EqualTo((0L, 0L)));
            Assert.That(fixture.Freed.Count, Is.EqualTo(2));
            GC.KeepAlive(first); // A descriptor may retain the dead wrapper indefinitely.
        }

        [Test]
        public void OverlappingPreparedRangesShareOneImportUntilEveryOwnerReleases()
        {
            using Fixture fixture = new();
            nint address = (nint)0x1000000;
            ulong page = (ulong)Environment.SystemPageSize;
            Auto<MemoryAllocation> first = fixture.Import(address, 2 * page);
            Auto<MemoryAllocation> overlapping = fixture.Import(address + 31, page);
            Assert.That(overlapping, Is.SameAs(first));
            Assert.That(fixture.Allocations, Is.EqualTo(1));
            Assert.That(fixture.Allocator.GetExistingAllocation(address + 31, page).Item2, Is.EqualTo(31UL));
            Assert.That(fixture.Allocator.GetImportStatistics(), Is.EqualTo(((long)(2 * page), 1L)));
            first.DecrementReferenceCount();
            Assert.That(fixture.Freed, Is.Empty);
            Assert.That(fixture.Allocator.GetExistingAllocation(address, 2 * page).Item1, Is.SameAs(first));
            overlapping.DecrementReferenceCount();
            Assert.That(fixture.Freed.Count, Is.EqualTo(1));
            Assert.That(fixture.Allocator.GetImportStatistics(), Is.EqualTo((0L, 0L)));
            Assert.Throws<InvalidOperationException>(() => fixture.Allocator.GetExistingAllocation(address + 31, page));
        }

        [Test]
        public void RetiringOneDeviceMemoryDoesNotRemoveOtherLiveHostMappings()
        {
            using Fixture fixture = new();
            nint firstAddress = (nint)0x1000000;
            nint secondAddress = (nint)0x2000000;
            ulong page = (ulong)Environment.SystemPageSize;
            Auto<MemoryAllocation> first = fixture.Import(firstAddress, page);
            Auto<MemoryAllocation> second = fixture.Import(secondAddress, 2 * page);
            ulong secondHandle = second.GetUnsafe().Memory.Handle;
            first.DecrementReferenceCount();
            Assert.Throws<InvalidOperationException>(() => fixture.Allocator.GetExistingAllocation(firstAddress, page));
            Assert.That(fixture.Allocator.GetExistingAllocation(secondAddress, 2 * page).Item1, Is.SameAs(second));
            Assert.That(second.GetUnsafe().Memory.Handle, Is.EqualTo(secondHandle));
            Assert.That(fixture.Allocator.GetImportStatistics(), Is.EqualTo(((long)(2 * page), 1L)));
            second.DecrementReferenceCount();
            Assert.That(fixture.Allocator.GetImportStatistics(), Is.EqualTo((0L, 0L)));
            Assert.That(fixture.Freed.Count, Is.EqualTo(2));
        }

        // Replace only Vulkan device/bootstrap with native callbacks. Import lookup,
        // memory type choice, allocation, Auto lifetime and registry cleanup are real.
        private sealed unsafe class Fixture : IDisposable
        {
            [UnmanagedFunctionPointer(CallingConvention.Winapi)]
            private delegate Result HostPropertiesDelegate(Device device, ExternalMemoryHandleTypeFlags handleType,
                void* pointer, MemoryHostPointerPropertiesEXT* properties);
            [UnmanagedFunctionPointer(CallingConvention.Winapi)]
            private delegate Result AllocateDelegate(Device device, MemoryAllocateInfo* info, AllocationCallbacks* allocator, DeviceMemory* memory);
            [UnmanagedFunctionPointer(CallingConvention.Winapi)]
            private delegate void FreeDelegate(Device device, DeviceMemory memory, AllocationCallbacks* allocator);

            private readonly HostPropertiesDelegate _properties;
            private readonly AllocateDelegate _allocate;
            private readonly FreeDelegate _free;
            private readonly Vk _api;
            private readonly ExtExternalMemoryHost _extension;
            public HostMemoryAllocator Allocator { get; }
            public int Allocations { get; private set; }
            public List<ulong> Freed { get; } = [];
            public List<(long Bytes, long Count)> StatisticsAtFree { get; } = [];

            public Fixture()
            {
                _properties = (_, _, _, properties) =>
                {
                    *properties = new MemoryHostPointerPropertiesEXT
                    {
                        SType = StructureType.MemoryHostPointerPropertiesExt,
                        MemoryTypeBits = 1,
                    };
                    return Result.Success;
                };
                _allocate = (_, _, _, memory) =>
                {
                    *memory = new DeviceMemory((ulong)++Allocations);
                    return Result.Success;
                };
                _free = (_, memory, _) =>
                {
                    Freed.Add(memory.Handle);
                    StatisticsAtFree.Add(Allocator.GetImportStatistics());
                };
                nint Resolve(string name) => name switch
                {
                    "vkGetMemoryHostPointerPropertiesEXT" => Marshal.GetFunctionPointerForDelegate(_properties),
                    "vkAllocateMemory" => Marshal.GetFunctionPointerForDelegate(_allocate),
                    "vkFreeMemory" => Marshal.GetFunctionPointerForDelegate(_free),
                    _ => throw new InvalidOperationException($"Unexpected native operation: {name}"),
                };
                _api = new Vk(new LamdaNativeContext(Resolve));
                _extension = new ExtExternalMemoryHost(new LamdaNativeContext(Resolve));

                PhysicalDeviceMemoryProperties memoryProperties = new() { MemoryTypeCount = 1 };
                memoryProperties.MemoryTypes[0] = new MemoryType { PropertyFlags = BufferManager.DefaultBufferMemoryFlags };
                object physicalDevice = default(VulkanPhysicalDevice);
                typeof(VulkanPhysicalDevice).GetField("PhysicalDeviceMemoryProperties").SetValue(physicalDevice, memoryProperties);
                MemoryAllocator backing = (MemoryAllocator)RuntimeHelpers.GetUninitializedObject(typeof(MemoryAllocator));
                typeof(MemoryAllocator).GetField("_physicalDevice", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(backing, physicalDevice);
                Allocator = new HostMemoryAllocator(backing, _api, _extension, default);
            }

            public Auto<MemoryAllocation> Import(nint address, ulong size)
            {
                MemoryRequirements requirements = new() { Size = size, Alignment = (ulong)Environment.SystemPageSize, MemoryTypeBits = 1 };
                Assert.That(Allocator.TryImport(requirements, BufferManager.DefaultBufferMemoryFlags, address, size), Is.True);
                return Allocator.GetExistingAllocation(address, size).Item1;
            }

            public void Dispose()
            {
                _extension.Dispose();
                _api.Dispose();
                GC.KeepAlive(_properties);
                GC.KeepAlive(_allocate);
                GC.KeepAlive(_free);
            }
        }
    }
}
