import SwiftUI
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
