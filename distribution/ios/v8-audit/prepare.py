from pathlib import Path
import re
import subprocess

root = Path(__file__).resolve().parents[3]

def write(path, text):
    target = root / path
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_text(text)

def replace(path, old, new, count=1):
    target = root / path
    text = target.read_text()
    if text.count(old) != count:
        raise RuntimeError(f'Baseline mismatch: {path}: {old[:60]}')
    target.write_text(text.replace(old, new))

def commit(message, paths):
    subprocess.run(['git', 'add', '--', *paths], cwd=root, check=True)
    subprocess.run(['git', 'diff', '--cached', '--check'], cwd=root, check=True)
    subprocess.run(['git', 'commit', '-m', message], cwd=root, check=True)

# This preparation recipe runs once on the isolated audit branch. Every replacement
# checks its exact baseline; it never guesses at changes on another revision.
p = 'src/Ryujinx.Graphics.Gpu/Memory/Buffer.cs'
replace(p, '_context.Renderer.Pipeline.CopyBuffer(virtualBuffer.Handle, Handle, virtualOffset, physicalOffset, (int)size);', '_context.Renderer.Pipeline.CopyBuffer(virtualBuffer.Handle, Handle, virtualOffset, physicalOffset, (int)mSize);')
replace(p, 'private ReadOnlySpan<byte> CopyFromDependantVirtualBuffers(ReadOnlySpan<byte> dataSpan, ulong address, ulong size)', 'internal ReadOnlySpan<byte> CopyFromDependantVirtualBuffers(ReadOnlySpan<byte> dataSpan, ulong address, ulong size)')
commit('fix: clamp virtual buffer copy to the actual modified intersection', [p])

subprocess.run(['git', 'apply', '--check', 'distribution/ios/v8-audit/02-owned-shutdown.patch'], cwd=root, check=True)
subprocess.run(['git', 'apply', 'distribution/ios/v8-audit/02-owned-shutdown.patch'], cwd=root, check=True)
commit('fix: coordinate emulation stop and dispose backend on its owner', ['src/Ryujinx.Graphics.GAL/Multithreading/ThreadedRenderer.cs', 'src/Ryujinx.Library/Library.cs', 'src/Ryujinx.Library/Window/WindowBase.cs'])

p = 'src/MeloNX/MeloNX/Common/Diagnostics/MemoryDiagnostics.swift'
replace(p, '    private var timer: DispatchSourceTimer?', '''    private var timer: DispatchSourceTimer?
    private var coreActive = false
    private var coreReturnedAtUptime: TimeInterval?
    private var coreExitCode: Int?
    private var postStopMilestones: Set<Int> = []''')
replace(p, '                startedAtUptime = ProcessInfo.processInfo.systemUptime', '''                startedAtUptime = ProcessInfo.processInfo.systemUptime
                coreActive = true
                coreReturnedAtUptime = nil
                coreExitCode = nil
                postStopMilestones.removeAll()''')
replace(p, '                    "sample_interval_seconds": 2,', '                    "sample_interval_seconds": 2,\n                    "post_stop_sample_seconds": 60,')
replace(p, '                    self?.recordSample(event: "sample")', '                    self?.sampleTick()')
replace(p, '''    func stopSession(exitCode: Int) {
        queue.sync { finishSession(exitCode: exitCode) }
    }''', r'''    func markStopRequested() {
        queue.sync {
            coreActive = false
            recordSample(event: "stop_requested")
        }
    }

    func stopSession(exitCode: Int) {
        queue.sync {
            guard file != nil, coreReturnedAtUptime == nil else { return }
            coreActive = false
            coreReturnedAtUptime = ProcessInfo.processInfo.systemUptime
            coreExitCode = exitCode
            recordSample(event: "main_returned", exitCode: exitCode)
        }
    }

    private func sampleTick() {
        guard let returnedAt = coreReturnedAtUptime else {
            recordSample(event: "sample")
            return
        }
        let elapsed = ProcessInfo.processInfo.systemUptime - returnedAt
        for milestone in [10, 30, 60] where elapsed >= Double(milestone) {
            if postStopMilestones.insert(milestone).inserted {
                recordSample(event: "post_stop_\(milestone)s", exitCode: coreExitCode)
            }
        }
        if elapsed >= 60 {
            finishSession(exitCode: coreExitCode)
        } else {
            recordSample(event: "post_stop_sample", exitCode: coreExitCode)
        }
    }''')
replace(p, '        appendJitCacheUsage(to: &record)', '''        record["core_active"] = coreActive
        if let returnedAt = coreReturnedAtUptime {
            record["post_stop_elapsed_seconds"] = ProcessInfo.processInfo.systemUptime - returnedAt
        }
        if coreActive {
            appendJitCacheUsage(to: &record)
        }''')
replace(p, '''        let now = ProcessInfo.processInfo.systemUptime

        if event == "memory_warning"''', '''        guard coreActive else { return nil }
        let now = ProcessInfo.processInfo.systemUptime

        if event == "memory_warning"''')
replace(p, '''    private func closeSession() {
        timer?.cancel()''', '''    private func closeSession() {
        coreActive = false
        coreReturnedAtUptime = nil
        coreExitCode = nil
        postStopMilestones.removeAll()
        timer?.cancel()''')
replace('src/MeloNX/MeloNX/Core/Ryujinx.swift', '''    static func stopEmulation() {
        MeloNX.stop_emulation()
    }''', '''    static func stopEmulation() {
        MemoryDiagnostics.shared.markStopRequested()
        MeloNX.stop_emulation()
    }''', count=2)
p = 'src/MeloNX/MeloNX/Core/RyujinxController.swift'
replace(p, '@Published var isRunning: RunningState = .stopped', '@Published var isRunning: RunningState = .stopped\n    @Published var isStopping = false')
replace(p, '    func startGame(_ game: GameInfo) {', '''    func stopGame() {
        guard emulationThreadActive, !isStopping else { return }
        isStopping = true
        Ryujinx.stopEmulation()
    }

    func startGame(_ game: GameInfo) {''')
replace(p, '        emulationThreadActive = true', '        isStopping = false\n        emulationThreadActive = true')
replace(p, '                self.emulationThreadActive = false', '                self.emulationThreadActive = false\n                self.isStopping = false\n                Ryujinx.emulationView = nil')
p = 'src/MeloNX/MeloNX/UI/Emulation/Config/InGameConfigView.swift'
replace(p, '''            Ryujinx.stopEmulation()
            Ryujinx.emulationView = nil
            ryujinxController.isRunning = .stopped''', '            ryujinxController.stopGame()')
replace(p, '                Text("Exit (Unstable)")', '                Text(ryujinxController.isStopping ? "Stopping…" : "Exit")')
replace(p, '''                Image(systemName: "x.circle")
            }
        }''', '''                Image(systemName: "x.circle")
            }
        }
        .disabled(ryujinxController.isStopping)''')
commit('fix: retain Metal view until teardown and sample memory after core return', ['src/MeloNX/MeloNX/Common/Diagnostics/MemoryDiagnostics.swift', 'src/MeloNX/MeloNX/Core/Ryujinx.swift', 'src/MeloNX/MeloNX/Core/RyujinxController.swift', p])

write('src/MeloNX/MeloNX/Common/MemoryLimits/MemoryBenchmarkRun.swift', '''import Foundation

// Allocation ownership and cancellation are independent of the UI actor.
nonisolated final class MemoryBenchmarkRun: @unchecked Sendable {
    private let lock = NSLock()
    private var cancelled = false

    var isCancelled: Bool {
        lock.lock()
        defer { lock.unlock() }
        return cancelled
    }

    func cancel() {
        lock.lock()
        cancelled = true
        lock.unlock()
    }

    func execute(
        chunkSize: Int,
        allocate: (Int) -> UnsafeMutableRawPointer? = { malloc($0) },
        release: (UnsafeMutableRawPointer) -> Void = { free($0) },
        initialize: (UnsafeMutableRawPointer, Int) -> Void = { memset($0, 0xA5, $1) },
        pause: () -> Void = { Thread.sleep(forTimeInterval: 0.5) },
        progress: (UInt64) -> Void
    ) {
        guard chunkSize > 0 else { return }
        var allocations: [UnsafeMutableRawPointer] = []
        var total: UInt64 = 0
        defer {
            for pointer in allocations { release(pointer) }
        }
        while !isCancelled {
            guard let pointer = allocate(chunkSize) else { break }
            allocations.append(pointer)
            guard !isCancelled else { break }
            initialize(pointer, chunkSize)
            total += UInt64(chunkSize)
            guard !isCancelled else { break }
            progress(total)
            pause()
        }
    }
}
''')
write('src/MeloNX/MeloNX/Common/MemoryLimits/MemoryLimitManager.swift', '''import SwiftUI
import Combine

@MainActor
class MemoryLimitManager: ObservableObject {
    @Published var memoryLimit: UInt64 = 0
    @Published var started = false
    private var currentRun: MemoryBenchmarkRun?

    nonisolated var userDefaultsMemoryLimit: UInt64 {
        get {
            (UserDefaults.standard.value(forKey: "memoryLimit") as? NSNumber)?.uint64Value ?? 0
        }
        set {
            UserDefaults.standard.set(NSNumber(value: newValue), forKey: "memoryLimit")
        }
    }

    init() {
        memoryLimit = userDefaultsMemoryLimit
    }

    func testRAMLimit(chunkSizeMB: Int = 128) {
        guard !started, chunkSizeMB > 0, chunkSizeMB <= Int.max / (1024 * 1024) else { return }
        let run = MemoryBenchmarkRun()
        let chunkSize = chunkSizeMB * 1024 * 1024
        currentRun = run
        started = true
        Thread.detachNewThread {
            run.execute(chunkSize: chunkSize) { allocated in
                DispatchQueue.main.async {
                    guard self.currentRun === run, !run.isCancelled else { return }
                    self.userDefaultsMemoryLimit = allocated
                    self.memoryLimit = allocated
                }
            }
            DispatchQueue.main.async {
                guard self.currentRun === run else { return }
                self.currentRun = nil
                self.started = false
            }
        }
    }

    func stop() {
        currentRun?.cancel()
        userDefaultsMemoryLimit = 0
        memoryLimit = 0
    }

    func formatMemorySize() -> String {
        String(format: "%.2f GB", Double(memoryLimit) / 1024 / 1024 / 1024)
    }
}
''')
commit('fix: release every RAM benchmark allocation on stop and failure', ['src/MeloNX/MeloNX/Common/MemoryLimits'])

# Generate the uninteresting interface stubs from the checked-out interfaces.
helper = '''using Ryujinx.Common.Configuration;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Shader;
using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Ryujinx.Tests.Graphics
{
    internal sealed class AuditTestRenderer : IRenderer
    {
        internal readonly ConcurrentDictionary<BufferHandle, byte[]> Buffers = new();
        internal readonly ConcurrentQueue<(string Operation, int Thread)> Events = new();
        internal readonly ConcurrentQueue<(int Source, int Destination, int Size)> Copies = new();
        private long _nextHandle;
        public event EventHandler<ScreenCaptureImageInfo> ScreenCaptured { add { } remove { } }
        public bool PreferThreading => true;
        public IPipeline Pipeline { get; }
        public IWindow Window => null;
        public uint ProgramCount => 0;
        public AuditTestRenderer() => Pipeline = new AuditTestPipeline(this);
        public BufferHandle CreateBuffer(int size, BufferAccess access = BufferAccess.Default)
        {
            ulong value = (ulong)Interlocked.Increment(ref _nextHandle);
            BufferHandle handle = Unsafe.As<ulong, BufferHandle>(ref value);
            Buffers[handle] = GC.AllocateArray<byte>(size, pinned: true);
            Events.Enqueue(("create", Environment.CurrentManagedThreadId));
            return handle;
        }
        public void DeleteBuffer(BufferHandle buffer)
        {
            if (!Buffers.TryRemove(buffer, out _)) throw new InvalidOperationException("Missing backing");
            Events.Enqueue(("delete", Environment.CurrentManagedThreadId));
        }
        public PinnedSpan<byte> GetBufferData(BufferHandle buffer, int offset, int size) =>
            PinnedSpan<byte>.UnsafeFromSpan(Buffers[buffer].AsSpan(offset, size));
        public void SetBufferData(BufferHandle buffer, int offset, ReadOnlySpan<byte> data) => data.CopyTo(Buffers[buffer].AsSpan(offset));
        public void SetInterruptAction(Action<Action> action) { }
        public void BackgroundContextAction(Action action, bool alwaysBackground = false) => action();
        public void Dispose() => Events.Enqueue(("dispose", Environment.CurrentManagedThreadId));
'''
implemented = {'CreateBuffer', 'DeleteBuffer', 'GetBufferData', 'SetBufferData', 'SetInterruptAction', 'BackgroundContextAction'}
for ret, name, args in re.findall(r'^        ([\w<>]+) (\w+)\(([^;{]*?)\);', (root/'src/Ryujinx.Graphics.GAL/IRenderer.cs').read_text(), re.M):
    if name in implemented and (name != 'CreateBuffer' or not args.startswith('nint')):
        continue
    helper += f'        public {ret} {name}({args}) => throw new NotSupportedException();\n'
helper += '''    }
    internal sealed class AuditTestPipeline : IPipeline
    {
        private readonly AuditTestRenderer _renderer;
        internal AuditTestPipeline(AuditTestRenderer renderer) => _renderer = renderer;
        public void CopyBuffer(BufferHandle source, BufferHandle destination, int srcOffset, int dstOffset, int size)
        {
            _renderer.Buffers[source].AsSpan(srcOffset, size).CopyTo(_renderer.Buffers[destination].AsSpan(dstOffset, size));
            _renderer.Copies.Enqueue((srcOffset, dstOffset, size));
            _renderer.Events.Enqueue(("copy", Environment.CurrentManagedThreadId));
        }
'''
for ret, name, args in re.findall(r'^        ([\w<>]+) (\w+)\(([^;{]*?)\);', (root/'src/Ryujinx.Graphics.GAL/IPipeline.cs').read_text(), re.M):
    if name != 'CopyBuffer':
        helper += f'        public {ret} {name}({" ".join(args.split())}) => throw new NotSupportedException();\n'
helper += '    }\n}\n'
write('src/Ryujinx.Tests/Graphics/AuditTestRenderer.cs', helper)
write('src/Ryujinx.Tests/Graphics/VirtualBufferCopyTests.cs', '''using NUnit.Framework;
using Ryujinx.Graphics.Gpu;
using Ryujinx.Graphics.Gpu.Memory;
using Ryujinx.Memory.Range;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using GpuBuffer = Ryujinx.Graphics.Gpu.Memory.Buffer;

namespace Ryujinx.Tests.Graphics
{
    public class VirtualBufferCopyTests
    {
        private static void Set(object obj, string field, object value) =>
            obj.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(obj, value);

        [TestCase(0, 4096, 0, 65536)]
        [TestCase(32768, 4096, 0, 65536)]
        [TestCase(61440, 4096, 0, 65536)]
        [TestCase(61440, 4096, 63488, 2048)]
        public void CopiesOnlyTheModifiedIntersection(int modifiedOffset, int modifiedLength, int readOffset, int readLength)
        {
            const ulong address = 0x10000;
            AuditTestRenderer renderer = new();
            GpuContext context = (GpuContext)RuntimeHelpers.GetUninitializedObject(typeof(GpuContext));
            Set(context, "<Renderer>k__BackingField", renderer);
            GpuBuffer physical = (GpuBuffer)RuntimeHelpers.GetUninitializedObject(typeof(GpuBuffer));
            Set(physical, "_context", context);
            Set(physical, "<Address>k__BackingField", address);
            Set(physical, "<Size>k__BackingField", 65536UL);
            Set(physical, "<Handle>k__BackingField", renderer.CreateBuffer(65536));
            using ReaderWriterLockSlim dependencyLock = new();
            Set(physical, "_virtualDependenciesLock", dependencyLock);
            MultiRangeBuffer virtualBuffer = new(context, new MultiRange(address, 65536UL));
            Set(physical, "_virtualDependencies", new HashSet<MultiRangeBuffer> { virtualBuffer });
            Array.Fill(renderer.Buffers[physical.Handle], (byte)0x22);
            Array.Fill(renderer.Buffers[virtualBuffer.Handle], (byte)0x7b);
            virtualBuffer.AddModifiedRegion(new MultiRange(address + (ulong)modifiedOffset, (ulong)modifiedLength), 1);
            byte[] baseline = renderer.Buffers[physical.Handle].AsSpan(readOffset, readLength).ToArray();
            byte[] result = physical.CopyFromDependantVirtualBuffers(baseline, address + (ulong)readOffset, (ulong)readLength).ToArray();
            int start = Math.Max(modifiedOffset, readOffset);
            int length = Math.Min(modifiedOffset + modifiedLength, readOffset + readLength) - start;
            Assert.That(renderer.Copies.ToArray(), Is.EqualTo(new[] { (start, start, length) }));
            Assert.That(result.Take(start - readOffset), Is.All.EqualTo((byte)0x22));
            Assert.That(result.Skip(start - readOffset).Take(length), Is.All.EqualTo((byte)0x7b));
            Assert.That(result.Skip(start - readOffset + length), Is.All.EqualTo((byte)0x22));
            Assert.That(renderer.Buffers[physical.Handle].Take(start), Is.All.EqualTo((byte)0x22));
            Assert.That(renderer.Buffers[physical.Handle].Skip(start + length), Is.All.EqualTo((byte)0x22));
        }
    }
}
''')
write('src/Ryujinx.Tests/Graphics/ThreadedRendererShutdownTests.cs', '''using NUnit.Framework;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.GAL.Multithreading;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Ryujinx.Tests.Graphics
{
    public class ThreadedRendererShutdownTests
    {
        [TestCase(0)]
        [TestCase(64)]
        [TestCase(12000)]
        public void LateDeletesDrainBeforeBackendDisposalOnOwnerThread(int copies)
        {
            AuditTestRenderer backend = new();
            ThreadedRenderer renderer = new(backend);
            using ManualResetEventSlim producerDone = new(false);
            BufferHandle first = default, second = default;
            Exception producerError = null, backendError = null;
            int producerThread = 0;
            Thread owner = new(() =>
            {
                try
                {
                    renderer.RunLoop(() =>
                    {
                        producerThread = Environment.CurrentManagedThreadId;
                        try
                        {
                            first = renderer.CreateBuffer(16);
                            second = renderer.CreateBuffer(16);
                            renderer.SetBufferData(first, 0, new byte[] { 1, 2, 3, 4 });
                            for (int i = 0; i < copies; i++) renderer.Pipeline.CopyBuffer(first, second, 0, 0, 4);
                        }
                        catch (Exception error) { producerError = error; }
                        finally { producerDone.Set(); }
                    });
                }
                catch (Exception error) { backendError = error; }
            }) { IsBackground = true };
            owner.Start();
            Assert.That(producerDone.Wait(TimeSpan.FromSeconds(15)), Is.True);
            Task teardown = Task.Run(() =>
            {
                renderer.DeleteBuffer(first);
                renderer.DeleteBuffer(second);
                renderer.Dispose();
                renderer.Dispose();
            });
            Assert.That(teardown.Wait(TimeSpan.FromSeconds(15)), Is.True, "Teardown deadlocked");
            Assert.That(owner.Join(TimeSpan.FromSeconds(1)), Is.True);
            Assert.That(producerError, Is.Null);
            Assert.That(backendError, Is.Null);
            Assert.That(backend.Buffers, Is.Empty);
            var events = backend.Events.ToArray();
            Assert.That(events.Count(e => e.Operation == "copy"), Is.EqualTo(copies));
            Assert.That(events.TakeLast(3).Select(e => e.Operation), Is.EqualTo(new[] { "delete", "delete", "dispose" }));
            Assert.That(events.Count(e => e.Operation == "dispose"), Is.EqualTo(1));
            Assert.That(events.Select(e => e.Thread).Distinct().ToArray(), Is.EqualTo(new[] { owner.ManagedThreadId }));
            Assert.That(producerThread, Is.Not.EqualTo(owner.ManagedThreadId));
        }
    }
}
''')
write('tests/swift/MemoryBenchmarkRunTests.swift', r'''import Foundation

@main
struct MemoryBenchmarkRunTests {
    static func main() {
        var passed = 0
        for cancelAt in 0...3 {
            let run = MemoryBenchmarkRun()
            var allocated = 0
            var freed = 0
            var touched = 0
            if cancelAt == 0 { run.cancel() }
            run.execute(chunkSize: 16, allocate: { size in
                allocated += 1
                if allocated == cancelAt { run.cancel() }
                return malloc(size)
            }, release: {
                freed += 1
                free($0)
            }, initialize: { _, _ in touched += 1 }, pause: {}, progress: { _ in })
            precondition(allocated == cancelAt && freed == allocated)
            precondition(touched == max(0, cancelAt - 1))
            passed += 1
        }
        do {
            let run = MemoryBenchmarkRun()
            var allocated = 0
            var freed = 0
            var totals: [UInt64] = []
            run.execute(chunkSize: 16, allocate: { size in
                if allocated == 3 { return nil }
                allocated += 1
                return malloc(size)
            }, release: { freed += 1; free($0) }, pause: {}, progress: { totals.append($0) })
            precondition(freed == 3 && totals == [16, 32, 48])
            passed += 1
        }
        do {
            let run = MemoryBenchmarkRun()
            var freed = 0
            run.execute(chunkSize: 16, release: { freed += 1; free($0) }, initialize: { _, _ in
                run.cancel()
            }, pause: {}, progress: { _ in preconditionFailure("Progress after cancellation") })
            precondition(freed == 1)
            passed += 1
        }
        do {
            let run = MemoryBenchmarkRun()
            run.execute(chunkSize: 0, allocate: { _ in preconditionFailure("Invalid allocation") }, progress: { _ in })
            passed += 1
        }
        print("MemoryBenchmarkRunTests: \(passed) passed")
    }
}
''')
commit('test: cover partial copies, threaded drain and RAM allocation ownership', ['src/Ryujinx.Tests/Graphics/AuditTestRenderer.cs', 'src/Ryujinx.Tests/Graphics/VirtualBufferCopyTests.cs', 'src/Ryujinx.Tests/Graphics/ThreadedRendererShutdownTests.cs', 'tests/swift'])

write('docs/GTA-V-V8-AUDIT-RELEASE.ru.md', '''# MeloNX GTA V v8 audit — experimental

Основа: v7 3bc06315dd25467dfc0081426f5344241f0ff65d. Изолированная ветка codex/gta-v8-audit-fixes; master и v7 не изменены.

Включены: исправление длины GPU-копирования по пересечению, request-only Stop, завершение producer перед drain и backend disposal на owner thread, удержание Metal view до завершения core, OS-only memory sampling до 60 секунд после возврата core, освобождение всех RAM benchmark allocations при Stop и malloc failure.

Сохранены Auto→On, восемь command buffers, 128/128 MiB caches, JIT Auto и bundled MoltenVK. Новые тесты проверяют фактический copy/readback через CPU fake renderer, реальную threaded queue с 0/64/12000 copies и поздними deletes, а также семь Swift cancellation/failure cases. Результат конкретного запуска находится в приложенных CI logs/TRX, а не предполагается заранее.

ВАЖНО: общий background-flush путь, способный записывать с guest thread в single-producer GAL queue, пока НЕ исправлен. Расширенный prototype отозван: простой lock/deferred-consumption вариант может создать deadlock либо stale read. Из этого release нельзя делать вывод, что все вылеты закрыты. Native device run и прохождение Майкл→Франклин здесь не выполнены. Teardown integration протестирован с CPU backend, а не Metal на iPhone.

IPA unprovisioned: требуется повторная подпись для sideload. Для первого A/B полностью перезапустить приложение, сохранить настройки и прогретый Shader Cache. После Stop оставить приложение открытым на 60 секунд без нового запуска; background suspension может отложить samples, фактическое elapsed записывается. Экспортировать session.json, все memory*.jsonl, core log и matching .ips при наличии.
''')
# Keep ordinary manual packaging useful for the resulting source branch too.
p = ' .github/workflows/ios-experimental.yml'.strip()
replace(p, 'FullyQualifiedName~VulkanMemoryTrimPolicyTests\'', 'FullyQualifiedName~VulkanMemoryTrimPolicyTests|FullyQualifiedName~VirtualBufferCopyTests|FullyQualifiedName~ThreadedRendererShutdownTests\'')
commit('docs: record experimental scope and extend manual regression gate', ['docs/GTA-V-V8-AUDIT-RELEASE.ru.md', p])
