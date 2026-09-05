from pathlib import Path
import subprocess

root = Path('.')
staged = root / 'distribution/ios/v10-audit'
def edit(path, old, new, count=1):
    p = root / path
    s = p.read_text()
    assert s.count(old) == count, (path, old[:90], s.count(old))
    p.write_text(s.replace(old, new))
def install(name, destination):
    target = root / destination
    assert not target.exists(), str(target)
    target.write_text((staged / (name + '.txt')).read_text())
def commit(message):
    subprocess.run(['git', 'add', 'src'], check=True)
    subprocess.run(['git', 'diff', '--cached', '--check'], check=True)
    subprocess.run(['git', 'commit', '-m', message], check=True)

# Stage 1: exact v9 cache policy patch, then ABI integration and its tests.
patch = (staged / '01-core.patch').read_text().replace('\n diff --git ', '\ndiff --git ')
subprocess.run(['git', 'apply', '--check', '-'], input=patch, text=True, check=True)
subprocess.run(['git', 'apply', '-'], input=patch, text=True, check=True)
p = root / 'src/Ryujinx.Graphics.Gpu/GpuContext.cs'
s = p.read_text()
anchor = '            if (_memoryPressureMailbox.TryConsume(out MemoryPressureRequest request))\n            {\n'
a = s.index(anchor) + len(anchor)
b = s.index('\n            }\n\n            foreach (PhysicalMemory physicalMemory', a)
body = s[a:b]
s = s[:a] + '''                long observedAt = Environment.TickCount64;
                foreach (PhysicalMemory physicalMemory in PhysicalMemoryRegistry.Values)
                {
                    physicalMemory.BufferCache.ObserveMemoryHeadroom(request.AvailableMemoryBytes, observedAt);
                }

                if (request.Severity != MemoryPressureSeverity.Observe)
                {
''' + '\n'.join('    ' + line if line else '' for line in body.splitlines()) + '\n                }' + s[b:]
p.write_text(s)
edit('src/Ryujinx.Library/Library.cs', 'if (accepted && DualMappedJitCacheDiagnostics.TryGetUsage', 'if (accepted && severity > 0 && DualMappedJitCacheDiagnostics.TryGetUsage')
p = 'src/MeloNX/MeloNX/Common/Diagnostics/MemoryDiagnostics.swift'
edit(p, '"schema_version": 5', '"schema_version": 6')
edit(p, '        guard event == "sample" else { return nil }\n\n        if availableMemory', '''        guard event == "sample" else { return nil }

        // Observation-only ABI: updates recovery without scheduling GPU work.
        _ = Ryujinx.reportMemoryPressure(availableBytes: availableMemory, severity: 0, source: 1)

        if availableMemory''')
edit(p, '                    "ios_buffer_cache_critical_limit_mib": 64,', '''                    "ios_buffer_cache_critical_limit_mib": 64,
                    "buffer_cache_reduction_available_bytes": 512 * 1024 * 1024,
                    "buffer_cache_recovery_available_bytes": 768 * 1024 * 1024,
                    "buffer_cache_recovery_seconds": 20,
                    "headroom_observation_only_abi": true,''')
p = 'src/Ryujinx.Tests/Graphics/MemoryPressureMailboxTests.cs'
edit(p, '[TestCase(0, 1)]', '[TestCase(-1, 1)]')
edit(p, 'MonotonicMemoryPressureCapacity', 'RecoverableMemoryPressureCapacity', 4)
edit(p, '''[TestCase(256UL, (int)MemoryPressureSeverity.Low, 128UL)]
        [TestCase(257UL, (int)MemoryPressureSeverity.Low, 128UL)]
        [TestCase(256UL, (int)MemoryPressureSeverity.Critical, 128UL)]''', '''[TestCase(256UL, (int)MemoryPressureSeverity.Low, 256UL)]
        [TestCase(257UL, (int)MemoryPressureSeverity.Low, 257UL)]
        [TestCase(256UL, (int)MemoryPressureSeverity.Critical, 256UL)]''')
edit(p, '[TestCase(1024UL, 64UL)]', '[TestCase(1024UL, 128UL)]')
edit(p, '[TestCase(1229UL, 64UL)]', '[TestCase(1229UL, 128UL)]')
for name in ['BufferCacheStabilityTests.cs', 'MemoryPressureRecoveryTests.cs']:
    install(name, 'src/Ryujinx.Tests/Graphics/' + name)
commit('fix: preserve buffer working sets and recover pressure ceilings after sustained headroom')

# Stage 2: independent fix for unbounded idle MemoryOwner arrays.
install('BoundedArrayPool.cs', 'src/Ryujinx.Common/Memory/BoundedArrayPool.cs')
install('BoundedScratchPoolTests.cs', 'src/Ryujinx.Tests/Graphics/BoundedScratchPoolTests.cs')
p = root / 'src/Ryujinx.Common/Memory/MemoryOwner.cs'
s = p.read_text()
a = s.index('        private static class ArrayPooling')
b = s.index('        private readonly int _length;', a)
s = s[:a] + '''        // Idle payload per closed generic type; in-flight conversion jobs are not reclaimed.
        private static readonly BoundedArrayPool<T> Pool = new(
            maxRetainedBytes: typeof(T) == typeof(byte) ? 64L * 1024 * 1024 : 16L * 1024 * 1024,
            maxRetainedArrays: 64,
            maxArrayBytes: 32L * 1024 * 1024);

        public static MemoryOwnerPoolStatistics GetPoolStatistics() => Pool.GetStatistics();

        /// <summary>Releases only idle references; existing owners and queued transfers stay valid.</summary>
        public static long TrimPool(long targetRetainedBytes = 0) => Pool.Trim(targetRetainedBytes);

''' + s[b:]
s = s.replace('ArrayPooling.Get(length)', 'Pool.Rent(length)').replace('ArrayPooling.Return(array)', 'Pool.Return(array)').replace('from <see cref="ArrayPooling"/>', 'from a bounded idle-array pool').replace('using System.Collections.Generic;\n', '')
p.write_text(s)
edit('src/Ryujinx.Common/Ryujinx.Common.csproj', '  <ItemGroup>', '  <ItemGroup>\n    <InternalsVisibleTo Include="Ryujinx.Tests" />')
p = 'src/Ryujinx.Graphics.Vulkan/VulkanRenderer.cs'
edit(p, 'using Ryujinx.Common.Logging;', 'using Ryujinx.Common.Logging;\nusing Ryujinx.Common.Memory;')
edit(p, '            var deviceMemoryBefore = GetDeviceMemoryBudget();', '''            // Release idle scratch roots before scheduled GC. No live owner is invalidated.
            long scratchReleased = OperatingSystem.IsIOS() && availableMemoryBytes <= 512UL * 1024 * 1024
                ? MemoryOwner<byte>.TrimPool(16L * 1024 * 1024)
                : 0;
            var deviceMemoryBefore = GetDeviceMemoryBudget();''')
edit(p, '$"available_memory={availableMemoryBytes}, managed_at_decision={managedAtDecision}, " +', '$"available_memory={availableMemoryBytes}, managed_at_decision={managedAtDecision}, scratch_idle_released_bytes={scratchReleased}, " +')
p = 'src/Ryujinx.Graphics.Gpu/GpuContext.cs'
edit(p, 'using Ryujinx.Common.Logging;', 'using Ryujinx.Common.Logging;\nusing Ryujinx.Common.Memory;')
anchor = '                    Logger.Info?.Print(LogClass.Gpu, "GPU memory owners v1: accounting=logical_not_additive, " + GetCrashDiagnosticSnapshot());'
edit(p, anchor, anchor + '''
                    MemoryOwnerPoolStatistics scratch = MemoryOwner<byte>.GetPoolStatistics();
                    Logger.Info?.Print(LogClass.Gpu,
                        $"Scratch byte pool v1: accounting=managed_array_payload_not_resident, retained_bytes={scratch.RetainedBytes}, leased_bytes={scratch.LeasedBytes}, peak_leased_bytes={scratch.PeakLeasedBytes}, retained_arrays={scratch.RetainedArrays}, rents={scratch.Rents}, reuses={scratch.Reuses}, discarded_bytes={scratch.DiscardedBytes}.");''')
p = 'src/MeloNX/MeloNX/Common/Diagnostics/MemoryDiagnostics.swift'
edit(p, '                    "headroom_observation_only_abi": true,', '''                    "headroom_observation_only_abi": true,
                    "scratch_byte_pool_max_idle_bytes": 64 * 1024 * 1024,
                    "scratch_pool_max_idle_arrays_per_type": 64,''')
commit('fix: cap idle scratch arrays and preserve leased uploads under memory pressure')
