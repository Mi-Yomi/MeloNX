using NUnit.Framework;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Vulkan;
using Silk.NET.Core.Contexts;
using Silk.NET.Vulkan;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using GalFormat = Ryujinx.Graphics.GAL.Format;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace Ryujinx.Tests.Graphics
{
    public class BufferViewStorageIdentityTests
    {
        [Test]
        public void UnrelatedAllocationsKeepNativeViewAndSubmissionKeepsItAlive()
        {
            using Fixture fixture = new();
            BufferRange range = fixture.AddStorage(42);
            fixture.Texture.SetStorage(range);
            BufferView view = fixture.Texture.GetBufferView(fixture.Commands, false);

            for (int i = 0; i < 256; i++)
            {
                fixture.AddStorage((ulong)(100 + i));
                fixture.Texture.SetStorage(range);
                Assert.That(fixture.Texture.GetBufferView(fixture.Commands, false), Is.EqualTo(view));
            }

            Assert.That(fixture.CreatedViews, Is.EqualTo(1));
            fixture.Texture.Release();
            fixture.Texture.Release();
            Assert.That(fixture.DestroyedViews, Is.Zero, "The submitted command still owns the view.");
            fixture.RetireCommands();
            Assert.That(fixture.DestroyedViews, Is.EqualTo(1));
        }

        [Test]
        public void RecycledListAndNativeHandlesStillInvalidateOldStorage()
        {
            using Fixture fixture = new();
            BufferRange original = fixture.AddStorage(42);
            fixture.Texture.SetStorage(original);
            BufferView first = fixture.Texture.GetBufferView(fixture.Commands, false);
            long oldIdentity = fixture.Manager.GetStorageIdentity(original.Handle);
            fixture.RemoveStorage(original.Handle);

            BufferRange replacement = fixture.AddStorage(42);
            Assert.That(replacement.Handle, Is.EqualTo(original.Handle), "Exercise actual IdList handle reuse.");
            Assert.That(fixture.Manager.GetStorageIdentity(replacement.Handle), Is.Not.EqualTo(oldIdentity));
            fixture.Texture.SetStorage(replacement);
            Assert.That(fixture.Texture.GetBufferView(fixture.Commands, false), Is.Not.EqualTo(first));
            Assert.That(fixture.CreatedViews, Is.EqualTo(2));
            Assert.That(fixture.DestroyedViews, Is.Zero);
            fixture.RetireCommands();
            Assert.That(fixture.DestroyedViews, Is.EqualTo(1));
        }

        [TestCase(16, 64)]
        [TestCase(0, 32)]
        public void ChangedRangeRecreatesNativeView(int offset, int size)
        {
            using Fixture fixture = new();
            BufferRange range = fixture.AddStorage(42);
            fixture.Texture.SetStorage(range);
            BufferView first = fixture.Texture.GetBufferView(fixture.Commands, false);
            fixture.RetireCommands();
            fixture.Texture.SetStorage(new BufferRange(range.Handle, offset, size));
            Assert.That(fixture.DestroyedViews, Is.EqualTo(1));
            Assert.That(fixture.Texture.GetBufferView(fixture.Commands, true), Is.Not.EqualTo(first));
            Assert.That(fixture.LastOffset, Is.EqualTo((ulong)offset));
            Assert.That(fixture.LastSize, Is.EqualTo((ulong)size));
        }

        [Test]
        public void RemovedStorageDropsViewAndDoesNotRecreateIt()
        {
            using Fixture fixture = new();
            BufferRange range = fixture.AddStorage(42);
            fixture.Texture.SetStorage(range);
            fixture.Texture.GetBufferView(fixture.Commands, false);
            fixture.RetireCommands();
            fixture.RemoveStorage(range.Handle);
            fixture.Texture.SetStorage(range);
            Assert.That(fixture.Manager.GetStorageIdentity(range.Handle), Is.Zero);
            Assert.That(fixture.Texture.GetBufferView(fixture.Commands, false).Handle, Is.Zero);
            Assert.That(fixture.CreatedViews, Is.EqualTo(1));
            Assert.That(fixture.DestroyedViews, Is.EqualTo(1));
        }

        [Test]
        public void FailedNativeCreationCanRetryWithoutLeakingAView()
        {
            using Fixture fixture = new();
            fixture.Texture.SetStorage(fixture.AddStorage(42));
            fixture.FailCreate = true;
            Assert.Throws<VulkanException>(() => fixture.Texture.GetBufferView(fixture.Commands, false));
            Assert.That(fixture.CreatedViews, Is.Zero);
            fixture.FailCreate = false;
            Assert.That(fixture.Texture.GetBufferView(fixture.Commands, false).Handle, Is.Not.Zero);
            fixture.Texture.Release();
            fixture.RetireCommands();
            Assert.That(fixture.CreatedViews, Is.EqualTo(1));
            Assert.That(fixture.DestroyedViews, Is.EqualTo(1));
        }

        // Exercise TextureBuffer -> BufferManager -> BufferHolder -> Silk's native
        // entry points and Auto's submission lifetime, without requiring a GPU.
        // Reflection replaces only device/bootstrap state and one unsubmitted pool
        // entry; storage, view creation, binding, and dependency registration are real.
        // Retirement below reproduces the pool's post-fence dependency-release order.
        private sealed unsafe class Fixture : IDisposable
        {
            [UnmanagedFunctionPointer(CallingConvention.Winapi)]
            private delegate Result CreateViewDelegate(Device device, BufferViewCreateInfo* info, AllocationCallbacks* allocator, BufferView* view);
            [UnmanagedFunctionPointer(CallingConvention.Winapi)]
            private delegate void DestroyViewDelegate(Device device, BufferView view, AllocationCallbacks* allocator);
            [UnmanagedFunctionPointer(CallingConvention.Winapi)]
            private delegate void DestroyBufferDelegate(Device device, VkBuffer buffer, AllocationCallbacks* allocator);

            private readonly CreateViewDelegate _createView;
            private readonly DestroyViewDelegate _destroyView;
            private readonly DestroyBufferDelegate _destroyBuffer;
            private readonly Vk _api;
            private readonly VulkanRenderer _renderer;
            private readonly IdList<BufferHolder> _buffers = new();
            private readonly List<IAuto> _dependants = [];
            private readonly List<MultiFenceHolder> _waitables = [];
            private readonly HashSet<BufferHolder> _liveHolders = [];
            public BufferManager Manager { get; }
            public TextureBuffer Texture { get; }
            public CommandBufferScoped Commands { get; }
            public int CreatedViews { get; private set; }
            public int DestroyedViews { get; private set; }
            public ulong LastOffset { get; private set; }
            public ulong LastSize { get; private set; }
            public bool FailCreate { get; set; }

            public Fixture()
            {
                _createView = CreateView;
                _destroyView = (_, _, _) => DestroyedViews++;
                _destroyBuffer = (_, _, _) => { };
                _api = new Vk(new LamdaNativeContext(name => name switch
                {
                    "vkCreateBufferView" => Marshal.GetFunctionPointerForDelegate(_createView),
                    "vkDestroyBufferView" => Marshal.GetFunctionPointerForDelegate(_destroyView),
                    "vkDestroyBuffer" => Marshal.GetFunctionPointerForDelegate(_destroyBuffer),
                    _ => throw new InvalidOperationException($"Unexpected native operation: {name}"),
                }));
                _renderer = (VulkanRenderer)RuntimeHelpers.GetUninitializedObject(typeof(VulkanRenderer));
                Manager = (BufferManager)RuntimeHelpers.GetUninitializedObject(typeof(BufferManager));
                SetField(Manager, "_buffers", _buffers);
                SetField(_renderer, "<Api>k__BackingField", _api);
                SetField(_renderer, "<BufferManager>k__BackingField", Manager);
                SetField(_renderer, "<Textures>k__BackingField", new HashSet<ITexture>());
                Texture = new TextureBuffer(_renderer, new TextureCreateInfo(16, 1, 1, 1, 1, 1, 1, 4,
                    GalFormat.R8G8B8A8Unorm, DepthStencilMode.Depth, Target.TextureBuffer,
                    SwizzleComponent.Red, SwizzleComponent.Green, SwizzleComponent.Blue, SwizzleComponent.Alpha));

                CommandBufferPool pool = (CommandBufferPool)RuntimeHelpers.GetUninitializedObject(typeof(CommandBufferPool));
                Type entryType = typeof(CommandBufferPool).GetNestedType("ReservedCommandBuffer", BindingFlags.NonPublic);
                object entry = Activator.CreateInstance(entryType);
                entryType.GetField("Dependants").SetValue(entry, _dependants);
                entryType.GetField("Waitables").SetValue(entry, _waitables);
                Array entries = Array.CreateInstance(entryType, 1);
                entries.SetValue(entry, 0);
                SetField(pool, "_commandBuffers", entries);
                Commands = new CommandBufferScoped(pool, default, 0);
            }

            public BufferRange AddStorage(ulong nativeHandle)
            {
                BufferHolder holder = new(_renderer, default, new VkBuffer(nativeHandle), 256, []);
                _liveHolders.Add(holder);
                ulong id = (uint)_buffers.Add(holder);
                // Mirror BufferManager's accounting, so this test also regresses
                // the previous global-count implementation rather than bypassing it.
                SetField(Manager, "<BufferCount>k__BackingField", Manager.BufferCount + 1);
                return new BufferRange(Unsafe.As<ulong, BufferHandle>(ref id), 0, 64);
            }

            public void RemoveStorage(BufferHandle handle)
            {
                Assert.That(_buffers.TryGetValue(handle, out BufferHolder holder), Is.True);
                _buffers.Remove(handle);
                _liveHolders.Remove(holder);
                holder.Dispose();
            }

            private Result CreateView(Device device, BufferViewCreateInfo* info, AllocationCallbacks* allocator, BufferView* view)
            {
                *view = default;
                if (FailCreate)
                {
                    return Result.ErrorOutOfDeviceMemory;
                }
                LastOffset = info->Offset;
                LastSize = info->Range;
                *view = new BufferView((ulong)++CreatedViews);
                return Result.Success;
            }

            public void RetireCommands()
            {
                foreach (IAuto dependant in _dependants)
                {
                    dependant.DecrementReferenceCount(0);
                }
                foreach (MultiFenceHolder waitable in _waitables)
                {
                    waitable.RemoveFence(0);
                    waitable.RemoveBufferUses(0);
                }
                _dependants.Clear();
                _waitables.Clear();
            }

            public void Dispose()
            {
                Texture.Release();
                RetireCommands();
                foreach (BufferHolder holder in _liveHolders)
                {
                    holder.Dispose();
                }
                _liveHolders.Clear();
                _api.Dispose();
                GC.KeepAlive(_createView);
                GC.KeepAlive(_destroyView);
                GC.KeepAlive(_destroyBuffer);
            }

            private static void SetField(object target, string name, object value)
            {
                target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);
            }
        }
    }
}
