import Foundation

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
