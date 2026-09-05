from pathlib import Path
import subprocess


def run(*args):
    subprocess.run(args, check=True)


def replace(path, old, new, count=1):
    p = Path(path)
    text = p.read_text()
    actual = text.count(old)
    if actual != count:
        raise RuntimeError(f'{path}: expected {count} anchors, found {actual}: {old[:100]!r}')
    p.write_text(text.replace(old, new))


def write(path, text):
    p = Path(path)
    if p.exists():
        raise RuntimeError(f'Refusing to overwrite {path}')
    p.parent.mkdir(parents=True, exist_ok=True)
    p.write_text(text)


def commit(message):
    run('git', 'diff', '--check')
    run('git', 'add', 'src', 'tests')
    run('git', 'commit', '-m', message)


patch = 'distribution/ios/v9-audit/01-readback.patch'
run('git', 'apply', '--check', patch)
run('git', 'apply', patch)
commit('fix: reconcile background buffer readback on backend owner; preserve single GAL producer')

p = 'src/Ryujinx.Memory/PageTable.cs'
replace(p, 'namespace Ryujinx.Memory', 'using System.Collections.Generic;\nusing System.Threading;\n\nnamespace Ryujinx.Memory')
replace(p, 'private readonly T[][][][] _pageTable;', '''private readonly T[][][][] _pageTable;
        private int _allocatedLeafCount;

        public int AllocatedLeafCount => Volatile.Read(ref _allocatedLeafCount);''')
replace(p, '_pageTable[l0][l1][l2] = new T[PtLevelSize];', '_pageTable[l0][l1][l2] = new T[PtLevelSize];\n                Interlocked.Increment(ref _allocatedLeafCount);')
replace(p, 'empty &= _pageTable[l0][l1][l2][i].Equals(default);', '''// Equals(object) with an untyped default compares a boxed value with null.
                // Compare with default(T), and stop scanning as soon as a live entry is found.
                if (!EqualityComparer<T>.Default.Equals(_pageTable[l0][l1][l2][i], default))
                {
                    empty = false;
                    break;
                }''')
replace(p, '_pageTable[l0][l1][l2] = null;', '_pageTable[l0][l1][l2] = null;\n                Interlocked.Decrement(ref _allocatedLeafCount);')
write('src/Ryujinx.Tests/Graphics/PageTableReclamationTests.cs', '''using NUnit.Framework;
using Ryujinx.Memory;

namespace Ryujinx.Tests.Graphics
{
    public class PageTableReclamationTests
    {
        [Test]
        public void EmptyValueTypeLeafIsReleased()
        {
            PageTable<ulong> table = new();
            table.Map(0x1000, 0x1234);
            Assert.That(table.AllocatedLeafCount, Is.EqualTo(1));
            table.Unmap(0x1000);
            Assert.That(table.AllocatedLeafCount, Is.Zero);
            Assert.That(table.Read(0x1000), Is.Zero);
        }

        [Test]
        public void LiveNeighbourPreventsPrematureReclamation()
        {
            PageTable<ulong> table = new();
            table.Map(0x1000, 0x1234);
            table.Map(0x2000, 0x5678);
            table.Unmap(0x1000);
            Assert.That(table.AllocatedLeafCount, Is.EqualTo(1));
            Assert.That(table.Read(0x2000), Is.EqualTo(0x5678));
            table.Unmap(0x2000);
            Assert.That(table.AllocatedLeafCount, Is.Zero);
        }

        [Test]
        public void SparseStreamingUnmapsDoNotRetainEmptyLeaves()
        {
            PageTable<ulong> table = new();
            for (int round = 0; round < 4; round++)
            {
                for (ulong i = 0; i < 1024; i++) table.Map(i << 21, i + 1);
                Assert.That(table.AllocatedLeafCount, Is.EqualTo(1024));
                for (ulong i = 0; i < 1024; i++) table.Unmap(i << 21);
                Assert.That(table.AllocatedLeafCount, Is.Zero);
            }
        }

        [Test]
        public void NativeIntegerLeafCanBeReclaimedAndRemapped()
        {
            PageTable<nuint> table = new();
            table.Map(1UL << 40, 42);
            table.Unmap(1UL << 40);
            Assert.That(table.AllocatedLeafCount, Is.Zero);
            table.Map(1UL << 40, 99);
            Assert.That(table.Read(1UL << 40), Is.EqualTo((nuint)99));
        }
    }
}
''')
commit('fix: reclaim empty value-type page-table leaves and stop boxing during unmap')

p = 'src/Ryujinx.Graphics.Gpu/Memory/MemoryPressureMailbox.cs'
replace(p, '''public static ulong CalculateBufferTarget(ulong configuredCapacity, MemoryPressureSeverity severity)
        {
            return severity == MemoryPressureSeverity.Critical ? 0 : configuredCapacity / 2;
        }''', '''public static ulong CalculateBufferTarget(ulong configuredCapacity, MemoryPressureSeverity severity, ulong availableMemoryBytes)
        {
            // Keep the pressure-sized hot set. A temporary zero target otherwise empties even
            // the retained 64/32 MiB working set at EVERY critical sample, forcing reuploads.
            return severity == MemoryPressureSeverity.Critical && availableMemoryBytes <= EmergencyAvailableMemory
                ? configuredCapacity / 4
                : configuredCapacity / 2;
        }''')
replace(p, 'if (severity != MemoryPressureSeverity.Critical)', 'if (severity != MemoryPressureSeverity.Critical || availableMemoryBytes > 1024 * MiB)')
replace(p, '''// Keep a small hot working set even in the emergency zone. The current pressure
            // pass still performs a one-shot trim to zero, while a persistent zero ceiling would
            // recreate every clean buffer on the next sequence and increase transient overlap.''', '''// UIKit can warn about system-wide pressure with ample process headroom. Reclaim
            // expendable buffers on that pass, but latch only from measured process pressure.''')
p = 'src/Ryujinx.Graphics.Gpu/Memory/PhysicalMemory.cs'
replace(p, 'MemoryPressureTrimPolicy.CalculateBufferTarget(BufferCache.Capacity, severity)', 'MemoryPressureTrimPolicy.CalculateBufferTarget(BufferCache.Capacity, severity, availableMemoryBytes)')
replace(p, 'BufferCache.TrimToCapacity(bufferTarget);', 'bufferTarget = Math.Min(bufferTarget, BufferCache.EffectiveCapacity);\n            BufferCache.TrimToCapacity(bufferTarget);')
p = 'src/Ryujinx.Graphics.Vulkan/VulkanMemoryTrimPolicy.cs'
replace(p, 'DescriptorTrimIntervalMilliseconds = 15_000;', 'DescriptorTrimIntervalMilliseconds = 30_000;')
replace(p, 'bool emergency = availableMemoryBytes <= EmergencyAvailableMemory;', '''bool emergency = availableMemoryBytes <= EmergencyAvailableMemory;
            // Pipeline variants are expensive to recreate. Reserve full invalidation for the
            // emergency zone rather than every UIKit notification with >1 GiB headroom.
            runHeavyCacheTrim &= emergency;''')
replace(p, '''bool runDescriptorTrim = runHeavyCacheTrim ||
                IsDue(nowMilliseconds, lastDescriptorTrimMilliseconds, descriptorInterval);''', '''bool runDescriptorTrim = availableMemoryBytes <= 512 * MiB &&
                (runHeavyCacheTrim || IsDue(nowMilliseconds, lastDescriptorTrimMilliseconds, descriptorInterval));''')
p = 'src/Ryujinx.Graphics.Vulkan/VulkanRenderer.cs'
replace(p, '''            FlushAllCommands();
            WaitForDeviceIdleSynchronized();

            CommandBufferPoolTrimResult commandBufferTrim = CommandBufferPool.Trim();''', '''            bool deviceIdleRequired = !OperatingSystem.IsIOS() || runAggressiveTrim || runReusableDescriptorTrim;
            if (deviceIdleRequired)
            {
                FlushAllCommands();
                WaitForDeviceIdleSynchronized();
            }

            // Trim retires only signalled fences. Unsubmitted/in-flight dependencies survive.
            CommandBufferPoolTrimResult commandBufferTrim = CommandBufferPool.Trim();''')
replace(p, '$"descriptor_trim={runReusableDescriptorTrim}, managed_gc={runManagedCollection}, " +', '$"descriptor_trim={runReusableDescriptorTrim}, managed_gc={runManagedCollection}, device_idle_wait={deviceIdleRequired}, " +')
p = 'src/Ryujinx.Tests/Graphics/MemoryPressureMailboxTests.cs'
replace(p, '[TestCase(256UL, (int)MemoryPressureSeverity.Critical, 0UL)]', '[TestCase(256UL, (int)MemoryPressureSeverity.Critical, 128UL)]')
replace(p, 'MemoryPressureTrimPolicy.CalculateBufferTarget(capacity, (MemoryPressureSeverity)severity)', 'MemoryPressureTrimPolicy.CalculateBufferTarget(capacity, (MemoryPressureSeverity)severity, 1000 * MiB)')
replace(p, '        [Test]\n        public void LowPressureDoesNotLatchPersistentBufferCapacity()', '''        [TestCase(0UL, 32UL)]
        [TestCase(133UL, 32UL)]
        [TestCase(256UL, 32UL)]
        [TestCase(512UL, 64UL)]
        [TestCase(1024UL, 64UL)]
        [TestCase(1229UL, 64UL)]
        public void RepeatedCriticalSamplesNeverEmptyHotSet(ulong availableMiB, ulong expectedMiB)
        {
            for (int i = 0; i < 100; i++)
                Assert.That(MemoryPressureTrimPolicy.CalculateBufferTarget(128 * MiB,
                    MemoryPressureSeverity.Critical, availableMiB * MiB), Is.EqualTo(expectedMiB * MiB));
        }

        [Test]
        public void UIKitWarningWithProcessHeadroomDoesNotLatch()
        {
            Assert.That(MemoryPressureTrimPolicy.CalculatePersistentBufferCapacity(128 * MiB,
                MemoryPressureSeverity.Critical, 1229 * MiB), Is.Null);
        }

        [Test]
        public void LowPressureDoesNotLatchPersistentBufferCapacity()''')
p = 'src/Ryujinx.Tests/Graphics/VulkanMemoryTrimPolicyTests.cs'
replace(p, 'FirstCriticalRequestRunsCompleteReclamation', 'FirstCriticalRequestWithHeadroomPreservesCompiledPipelines')
replace(p, 'Assert.That(decision, Is.EqualTo(new VulkanMemoryTrimDecision(true, true, true)));', 'Assert.That(decision, Is.EqualTo(new VulkanMemoryTrimDecision(false, false, false)));')
replace(p, 'Assert.That(due, Is.EqualTo(new VulkanMemoryTrimDecision(false, true, true)));', 'Assert.That(due, Is.EqualTo(new VulkanMemoryTrimDecision(false, false, true)));')
replace(p, 'Assert.That(decision, Is.EqualTo(new VulkanMemoryTrimDecision(false, true, false)));', 'Assert.That(decision, Is.EqualTo(new VulkanMemoryTrimDecision(false, false, false)));')
replace(p, '        private static VulkanMemoryTrimDecision Calculate(', '''        [Test]
        public void EmergencyStillAllowsFullReclamation()
        {
            Assert.That(Calculate(true, true, 133, 400, 70_000, 40_000),
                Is.EqualTo(new VulkanMemoryTrimDecision(true, true, true)));
        }

        [Test]
        public void DescriptorRetirementStartsBeforeEmergencyWithoutResettingPipelines()
        {
            Assert.That(Calculate(true, true, 512, 300, 70_000, 40_000),
                Is.EqualTo(new VulkanMemoryTrimDecision(false, true, false)));
        }

        private static VulkanMemoryTrimDecision Calculate(''')
commit('fix: retain pressure-sized hot buffers and avoid repeated iOS device-idle and pipeline churn')

p = 'src/Ryujinx.Graphics.GAL/Multithreading/Resources/ThreadedTexture.cs'
replace(p, 'using Ryujinx.Common.Memory;', 'using Ryujinx.Common.Memory;\nusing System;')
replace(p, 'private readonly ThreadedRenderer _renderer;', '''private readonly ThreadedRenderer _renderer;
        private readonly object _copyReleaseGate = new();
        private bool _releaseRequested;''')
replace(p, '''            _renderer.New<TextureCopyToBufferCommand>()->Set(Ref(this), range, layer, level, stride);
            _renderer.QueueCommand();''', '''            lock (_copyReleaseGate)
            {
                ObjectDisposedException.ThrowIf(_releaseRequested, this);
                if (_renderer.IsGpuThread())
                {
                    _renderer.New<TextureCopyToBufferCommand>()->Set(Ref(this), range, layer, level, stride);
                    _renderer.QueueCommand();
                }
                else
                {
                    _renderer.CopyTextureForReadback(this, range, layer, level, stride);
                }
            }''')
replace(p, '''            _renderer.New<TextureReleaseCommand>()->Set(Ref(this));
            _renderer.QueueCommand();''', '''            lock (_copyReleaseGate)
            {
                if (_releaseRequested) return;
                _releaseRequested = true;
                _renderer.New<TextureReleaseCommand>()->Set(Ref(this));
                _renderer.QueueCommand();
            }''')
p = 'src/Ryujinx.Graphics.GAL/Multithreading/ThreadedRenderer.cs'
replace(p, '        public unsafe Capabilities GetCapabilities()', '''        internal void CopyTextureForReadback(ThreadedTexture texture, BufferRange range, int layer, int level, int stride)
        {
            // ThreadedTexture holds its copy/release gate. Complete native work before allowing
            // Release to be published, without injecting a second producer into the GAL ring.
            ThreadedHelpers.SpinUntilNonNull(ref texture.Base);
            Buffers.MapBufferBlocking(range.Handle);
            Interrupt(() =>
            {
                if (!Buffers.TryMapBufferRange(range, out BufferRange mapped))
                    throw new InvalidOperationException("Missing texture readback target. " + GetDiagnosticSnapshot());
                texture.Base.CopyTo(mapped, layer, level, stride);
            });
        }

        public unsafe Capabilities GetCapabilities()''')
p = 'src/Ryujinx.Tests/Graphics/ThreadedTextureLifetimeTests.cs'
replace(p, '''                    texture.Release();
                    renderer.DeleteBuffer(target);''', '''                    texture.Release();
                    texture.Release();
                    Assert.Throws<ObjectDisposedException>(() => texture.CopyTo(new BufferRange(target, 0, 16), 0, 0, 4));
                    renderer.DeleteBuffer(target);''')
p = 'src/Ryujinx.Tests/Graphics/TextureGroupLifecycleTests.cs'
replace(p, 'using System.Runtime.CompilerServices;', 'using System.Runtime.CompilerServices;\nusing System.Reflection;')
replace(p, '        [Test]\n        public void TextureDisposesGroupBeforeReleasingHostTexture()', '''        [TestCase(false)]
        [TestCase(true)]
        public void FlushBufferUnmapThenDisposeDeletesOnceAndRejectsLateCopy(bool imported)
        {
            var created = CreateTexture();
            AuditTestRenderer renderer = new();
            const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(GpuContext).GetField("<Renderer>k__BackingField", fields).SetValue(created.Context, renderer);
            BufferHandle buffer = renderer.CreateBuffer(16, BufferAccess.Default);
            TextureGroup group = created.Texture.Group;
            typeof(TextureGroup).GetField("_flushBuffer", fields).SetValue(group, buffer);
            typeof(TextureGroup).GetField("_flushBufferImported", fields).SetValue(group, imported);
            group.Unmapped();
            group.Dispose();
            group.Unmapped();
            group.Dispose();
            TextureGroupHandle handle = CreateHandle(group);
            Assert.That(group.TryFlushIntoBuffer(handle), Is.False);
            Assert.That(renderer.Buffers, Is.Empty);
            Assert.That(renderer.Events.Count, Is.EqualTo(2));
            handle.Dispose();
            created.Texture.Dispose();
        }

        [Test]
        public void TextureDisposesGroupBeforeReleasingHostTexture()''')
commit('fix: reject late texture readback and preserve FIFO copy-release ordering')

p = 'src/Ryujinx.Cpu/PrivateMemoryAllocator.cs'
replace(p, 'using System.Diagnostics;', 'using System.Diagnostics;\nusing System.Threading;')
replace(p, 'private readonly List<T> _blocks;', '''private static long _reservedBytes;
        private static long _allocatedBytes;
        private static long _blocksLive;
        private long _ownedBytes;

        // Logical ownership across allocators of this concrete block type; not resident RAM.
        internal static (long Reserved, long Allocated, long Blocks) GetProcessStatistics() =>
            (Interlocked.Read(ref _reservedBytes), Interlocked.Read(ref _allocatedBytes), Interlocked.Read(ref _blocksLive));

        private readonly List<T> _blocks;''')
replace(p, 'return new Allocation(block, offset, size, reusedSize);', '''Interlocked.Add(ref _allocatedBytes, (long)size);
                        _ownedBytes += (long)size;
                        return new Allocation(block, offset, size, reusedSize);''')
replace(p, 'InsertBlock(newBlock);', '''InsertBlock(newBlock);
            Interlocked.Add(ref _reservedBytes, (long)blockAlignedSize);
            Interlocked.Increment(ref _blocksLive);''')
replace(p, 'return new Allocation(newBlock, newBlockOffset, size, newBlockReusedSize);', '''Interlocked.Add(ref _allocatedBytes, (long)size);
            _ownedBytes += (long)size;
            return new Allocation(newBlock, newBlockOffset, size, newBlockReusedSize);''')
replace(p, 'block.Free(offset, size);', '''block.Free(offset, size);
            Interlocked.Add(ref _allocatedBytes, -(long)size);
            _ownedBytes -= (long)size;''')
replace(p, 'block.Destroy();', '''block.Destroy();
                Interlocked.Add(ref _reservedBytes, -(long)block.Size);
                Interlocked.Decrement(ref _blocksLive);''')
replace(p, '_blocks[i].Destroy();', '''_blocks[i].Destroy();
                Interlocked.Add(ref _reservedBytes, -(long)_blocks[i].Size);
                Interlocked.Decrement(ref _blocksLive);''')
replace(p, '_blocks.Clear();', '''_blocks.Clear();
            Interlocked.Add(ref _allocatedBytes, -_ownedBytes);
            _ownedBytes = 0;''')
p = 'src/Ryujinx.Cpu/Jit/HostTracked/NativePageTable.cs'
replace(p, 'using System.Runtime.InteropServices;', 'using System.Runtime.InteropServices;\nusing System.Threading;')
replace(p, 'private bool _disposed;', '''private bool _disposed;
        private long _committedBytes;
        public ulong ReservedBytes => _nativePageTable.Size;
        public long CommittedBytes => Interlocked.Read(ref _committedBytes);
        public int ManagedLeafCount => _pageTable.AllocatedLeafCount;''')
replace(p, '_nativePageTable.Commit(bit * _hostPageSize, _hostPageSize);', '_nativePageTable.Commit(bit * _hostPageSize, _hostPageSize);\n                    Interlocked.Add(ref _committedBytes, (long)_hostPageSize);')
p = 'src/Ryujinx.Cpu/Jit/MemoryManagerHostTracked.cs'
replace(p, '        public event Action<ulong, ulong> UnmapEvent;', '''        // Observational counters: never acquire guest mapping locks on the GPU thread.
        public string GetMemoryOwnerSnapshot()
        {
            var stats = PrivateMemoryAllocator.GetProcessStatistics();
            return $"cpu_manager={Type}, guest_backing_virtual_bytes={_backingMemory.Size}, " +
                $"native_pt_virtual_bytes={_nativePageTable.ReservedBytes}, native_pt_committed_bytes={_nativePageTable.CommittedBytes}, " +
                $"managed_pt_leaf_arrays={_nativePageTable.ManagedLeafCount}, " +
                $"private_process_reserved_bytes={stats.Reserved}, private_process_allocated_bytes={stats.Allocated}, private_process_blocks={stats.Blocks}";
        }

        public event Action<ulong, ulong> UnmapEvent;''')
p = 'src/Ryujinx.Graphics.Gpu/Memory/PhysicalMemory.cs'
replace(p, 'using Ryujinx.Cpu;', 'using Ryujinx.Cpu;\nusing Ryujinx.Cpu.Jit;')
replace(p, '        private bool _hasCpuMemorySize;', '''        private bool _hasCpuMemorySize;

        internal string GetMemoryOwnerSnapshot() => _cpuMemory is MemoryManagerHostTracked tracked
            ? tracked.GetMemoryOwnerSnapshot()
            : $"cpu_manager={_cpuMemory.GetType().Name}, guest_backing_virtual_bytes={_cpuMemorySize}";''')
p = 'src/Ryujinx.Graphics.Gpu/GpuContext.cs'
replace(p, '        internal void AdvanceSequence()', '''        private long _lastOwnerSampleMilliseconds;

        internal void AdvanceSequence()''')
replace(p, '''            SequenceNumber++;

            if (_memoryPressureMailbox''', '''            SequenceNumber++;

            if (OperatingSystem.IsIOS() && (SequenceNumber & 1023) == 0)
            {
                long now = Environment.TickCount64;
                if (now - _lastOwnerSampleMilliseconds >= 10_000)
                {
                    _lastOwnerSampleMilliseconds = now;
                    Logger.Info?.Print(LogClass.Gpu, "GPU memory owners v1: accounting=logical_not_additive, " + GetCrashDiagnosticSnapshot());
                    foreach (var entry in PhysicalMemoryRegistry)
                        Logger.Info?.Print(LogClass.Gpu, $"Guest memory owners v1: pid={entry.Key}, accounting=virtual_or_logical_not_resident, " + entry.Value.GetMemoryOwnerSnapshot());
                }
            }

            if (_memoryPressureMailbox''')
p = 'src/Ryujinx.Graphics.Vulkan/TextureStorage.cs'
replace(p, 'using System.Runtime.CompilerServices;', 'using System.Runtime.CompilerServices;\nusing System.Threading;')
replace(p, '        private struct TextureSliceInfo', '''        private static long _ownerCount;
        private static long _ownerBytes;
        private static long _viewCount;
        internal static (long Owners, long LogicalBytes, long Views) GetOwnerStatistics() =>
            (Interlocked.Read(ref _ownerCount), Interlocked.Read(ref _ownerBytes), Interlocked.Read(ref _viewCount));

        private struct TextureSliceInfo''')
replace(p, '_slices = new TextureSliceInfo[levels * _depthOrLayers];', '''_slices = new TextureSliceInfo[levels * _depthOrLayers];
            Interlocked.Increment(ref _ownerCount);
            Interlocked.Add(ref _ownerBytes, (long)_size);''')
replace(p, '_viewsCount++;', '_viewsCount++;\n            Interlocked.Increment(ref _viewCount);')
replace(p, 'if (--_viewsCount == 0)', 'Interlocked.Decrement(ref _viewCount);\n            if (--_viewsCount == 0)')
replace(p, 'Disposed = true;', 'Disposed = true;\n            Interlocked.Decrement(ref _ownerCount);\n            Interlocked.Add(ref _ownerBytes, -(long)_size);')
p = 'src/Ryujinx.Graphics.Vulkan/HostMemoryAllocator.cs'
replace(p, '        private readonly MemoryAllocator _allocator;', '''        private long _importedBytes;
        private long _importedCount;
        internal (long Bytes, long Count) GetImportStatistics() =>
            (Interlocked.Read(ref _importedBytes), Interlocked.Read(ref _importedCount));

        private readonly MemoryAllocator _allocator;''')
replace(p, '_allocations.Add(hostAlloc);', '_allocations.Add(hostAlloc);\n                Interlocked.Add(ref _importedBytes, (long)pageAlignedSize);\n                Interlocked.Increment(ref _importedCount);')
replace(p, '_allocationTree.Remove(allocation.Start, allocation);', '_allocationTree.Remove(allocation.Start, allocation);\n                        Interlocked.Add(ref _importedBytes, -(long)allocation.Size);\n                        Interlocked.Decrement(ref _importedCount);')
p = 'src/Ryujinx.Graphics.Vulkan/VulkanRenderer.cs'
replace(p, '        public void PreFrame()', '''        private long _lastNativeOwnerSampleMilliseconds;
        private long _lastNativeOwnerAllocatedBytes;

        public void PreFrame()''')
replace(p, '''            SyncManager.Cleanup();
            TryRunPendingMemoryTrim();''', '''            SyncManager.Cleanup();
            TryRunPendingMemoryTrim();
            long now = Environment.TickCount64;
            if (OperatingSystem.IsIOS() && now - _lastNativeOwnerSampleMilliseconds >= 10_000)
            {
                long elapsed = now - _lastNativeOwnerSampleMilliseconds;
                _lastNativeOwnerSampleMilliseconds = now;
                var textures = TextureStorage.GetOwnerStatistics();
                var imported = HostMemoryAllocator.GetImportStatistics();
                var allocation = MemoryAllocator.GetStatistics();
                var device = GetDeviceMemoryBudget();
                var gc = GC.GetGCMemoryInfo();
                long allocated = GC.GetTotalAllocatedBytes(false);
                long rate = _lastNativeOwnerAllocatedBytes == 0 ? 0 : (allocated - _lastNativeOwnerAllocatedBytes) * 1000 / Math.Max(1, elapsed);
                _lastNativeOwnerAllocatedBytes = allocated;
                Logger.Info?.Print(LogClass.Gpu,
                    $"Native memory owners v1: accounting=overlapping_not_additive, texture_storage_owners={textures.Owners}, " +
                    $"texture_owner_logical_bytes={textures.LogicalBytes}, texture_view_owners={textures.Views}, " +
                    $"host_import_count={imported.Count}, host_import_mapped_bytes={imported.Bytes}, " +
                    $"allocator_reserved_bytes={allocation.ReservedBytes}, allocator_used_bytes={allocation.UsedBytes}, allocator_blocks={allocation.Blocks}, " +
                    $"driver_usage_bytes={device.Usage}, driver_budget_bytes={device.Budget}, " +
                    $"managed_heap_bytes={gc.HeapSizeBytes}, managed_committed_bytes={gc.TotalCommittedBytes}, " +
                    $"managed_fragmented_bytes={gc.FragmentedBytes}, managed_allocation_bytes_per_second={rate}");
            }''')
p = 'src/MeloNX/MeloNX/Common/Diagnostics/MemoryDiagnostics.swift'
replace(p, '"schema_version": 4,', '"schema_version": 5,')
replace(p, 'record["core_active"] = coreActive', '''record["core_active"] = coreActive
        record["thermal_state_raw"] = ProcessInfo.processInfo.thermalState.rawValue
        record["low_power_mode"] = ProcessInfo.processInfo.isLowPowerModeEnabled''')
replace(p, '"expand_ram_requested": metadata.launchSettings.expandRAMRequested,', '''"expand_ram_requested": metadata.launchSettings.expandRAMRequested,
                    "expand_ram_ui_requested": metadata.launchSettings.expandRAMRequested,
                    "memory_owner_log_schema": 1,
                    "pressure_buffer_preserves_hot_set": true,
                    "pressure_heavy_available_bytes": 256 * 1024 * 1024,
                    "pressure_descriptor_available_bytes": 512 * 1024 * 1024,''')
commit('diag: sample guest-private, page-table, texture, import and managed ownership without forcing GC')

write('src/Ryujinx.Tests/Cpu/PrivateMemoryOwnershipTests.cs', '''using NUnit.Framework;
using Ryujinx.Cpu;
using Ryujinx.Memory;

namespace Ryujinx.Tests.Cpu
{
    public class PrivateMemoryOwnershipTests
    {
        [Test]
        public void FreeAndDisposeRestoreProcessOwnershipBaseline()
        {
            ulong page = MemoryBlock.GetPageSize();
            var before = PrivateMemoryAllocator.GetProcessStatistics();
            using (PrivateMemoryAllocator allocator = new(8 * page, MemoryAllocationFlags.Mirrorable))
            {
                var first = allocator.Allocate(page, page);
                var second = allocator.Allocate(page, page);
                var live = PrivateMemoryAllocator.GetProcessStatistics();
                Assert.That(live.Reserved - before.Reserved, Is.EqualTo(8 * (long)page));
                Assert.That(live.Allocated - before.Allocated, Is.EqualTo(2 * (long)page));
                Assert.That(live.Blocks - before.Blocks, Is.EqualTo(1));
                first.Dispose();
                second.Dispose();
                Assert.That(PrivateMemoryAllocator.GetProcessStatistics(), Is.EqualTo(before));
            }
            Assert.That(PrivateMemoryAllocator.GetProcessStatistics(), Is.EqualTo(before));
        }
    }
}
''')
commit('test: verify private allocation ownership counters return to baseline')
