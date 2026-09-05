import Foundation

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
