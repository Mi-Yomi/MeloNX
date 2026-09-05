import Foundation
import Darwin

@main
struct MemoryForensicsTests {
    static func main() throws {
        var passed = 0
        func test(_ body: () throws -> Void) rethrows { try body(); passed += 1 }

        test {
            var cadence = MemoryForensicCadence()
            precondition(cadence.update(availableBytes: 2 * 1024 * 1024 * 1024, active: true) == 2)
            precondition(cadence.update(availableBytes: MemoryForensicCadence.criticalHeadroom, active: true) == 1)
            precondition(cadence.update(availableBytes: 0, active: true) == 1)
        }
        test {
            var cadence = MemoryForensicCadence()
            precondition(cadence.update(availableBytes: 600 * 1024 * 1024, active: true) == 2)
            _ = cadence.update(availableBytes: 10, active: true)
            precondition(cadence.update(availableBytes: 600 * 1024 * 1024, active: true) == 1)
            precondition(cadence.update(availableBytes: MemoryForensicCadence.recoveryHeadroom, active: true) == 2)
        }
        test {
            var cadence = MemoryForensicCadence()
            _ = cadence.update(availableBytes: 0, active: true)
            precondition(cadence.update(availableBytes: 0, active: false) == 2)
        }
        test {
            precondition(!MemoryForensicTaskFields.containsField(byteOffset: nil, byteSize: 8, integerCount: .max))
            precondition(!MemoryForensicTaskFields.containsField(byteOffset: Int.max, byteSize: 8, integerCount: .max))
            precondition(!MemoryForensicTaskFields.containsField(byteOffset: 8, byteSize: 8, integerCount: 3))
            precondition(MemoryForensicTaskFields.containsField(byteOffset: 8, byteSize: 8, integerCount: 4))
        }
        test {
            var info = task_vm_info_data_t()
            info.ledger_tag_graphics_footprint = 987654321
            info.ledger_tag_graphics_footprint_compressed = -7 // Preserve the raw signed ledger.
            var record: [String: Any] = [:]
            MemoryForensicTaskFields.append(info, count: 0, to: &record)
            precondition(record.isEmpty)
            let offset = MemoryLayout<task_vm_info_data_t>.offset(of: \.ledger_tag_graphics_footprint)!
            MemoryForensicTaskFields.append(info, count: UInt32(offset / 4), to: &record)
            precondition(record["task_vm_ledger_graphics_footprint_bytes"] == nil)
            MemoryForensicTaskFields.append(info, count: UInt32(MemoryLayout<task_vm_info_data_t>.stride / 4), to: &record)
            precondition(record["task_vm_ledger_graphics_footprint_bytes"] as? Int64 == 987654321)
            precondition(record["task_vm_ledger_graphics_footprint_compressed_bytes"] as? Int64 == -7)
        }
        test {
            let reader = MemoryForensicCoreReader()
            let json = Data("{\"schema_version\":1,\"bytes\":6425000000}".utf8)
            let result = reader.capture { buffer, capacity in
                precondition(capacity == 64 * 1024)
                json.copyBytes(to: buffer, count: json.count)
                return Int32(json.count)
            }
            precondition(Int(result.status) == json.count && result.bytes == json.count)
            precondition((result.value?["bytes"] as? NSNumber)?.uint64Value == 6425000000)
            for status in [Int32(-1), -2, -3, -4] {
                let missing = reader.capture { _, _ in status }
                precondition(missing.status == status && missing.value == nil && missing.bytes == 0)
            }
        }
        test {
            let reader = MemoryForensicCoreReader()
            let tooLong = reader.capture { _, capacity in capacity + 1 }
            precondition(tooLong.status == -5 && tooLong.value == nil)
            let incomplete = Data("{\"owner\":".utf8)
            let result = reader.capture { buffer, _ in
                incomplete.copyBytes(to: buffer, count: incomplete.count)
                return Int32(incomplete.count)
            }
            precondition(result.status == -5 && result.value == nil)
        }
        test {
            let reader = MemoryForensicCoreReader()
            let json = Data(("{\"text\":\"" + String(repeating: "x", count: MemoryForensicCoreReader.capacity - 11) + "\"}").utf8)
            precondition(json.count == MemoryForensicCoreReader.capacity)
            let result = reader.capture { buffer, _ in
                json.copyBytes(to: buffer, count: json.count)
                return Int32(json.count)
            }
            precondition(result.bytes == json.count && result.value != nil)
        }

        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("MeloNX-Forensic-Tests-" + UUID().uuidString)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: directory) }

        try test {
            let journal = try MemoryForensicJournal(directory: directory, stem: "durable", segmentLimit: 1024, recordLimit: 128)
            try journal.write(["phase": "before_core_snapshot", "sequence": 1])
            precondition(journal.synchronizationCount == 1 && journal.synchronizationDurationMilliseconds >= 0)
            // Read while the writer is still open; no normal shutdown is needed.
            let data = try Data(contentsOf: journal.currentURL)
            precondition(data.last == 10)
            let value = try JSONSerialization.jsonObject(with: data) as! [String: Any]
            precondition(value["phase"] as? String == "before_core_snapshot")
        }
        try test {
            let journal = try MemoryForensicJournal(directory: directory, stem: "rotate", segmentLimit: 80, recordLimit: 80)
            for number in 1...20 { try journal.write(["sequence": number, "phase": "sample_complete"]) }
            for url in [journal.currentURL, journal.previousURL] {
                let data = try Data(contentsOf: url)
                precondition(data.count <= 80 && data.last == 10)
                for line in data.split(separator: 10) { _ = try JSONSerialization.jsonObject(with: Data(line)) }
            }
            let last = try JSONSerialization.jsonObject(with: Data(contentsOf: journal.currentURL)) as! [String: Any]
            precondition(last["sequence"] as? Int == 20)
        }
        try test {
            let journal = try MemoryForensicJournal(directory: directory, stem: "oversized", segmentLimit: 128, recordLimit: 64)
            do {
                try journal.write(["text": String(repeating: "x", count: 100)])
                preconditionFailure("Oversized packet accepted")
            } catch MemoryForensicJournal.JournalError.recordTooLarge {}
            precondition(journal.bytesWritten == 0)
            try journal.write(["status": "after_rejection"])
            let data = try Data(contentsOf: journal.currentURL)
            precondition(String(decoding: data, as: UTF8.self).contains("after_rejection"))
        }
        try test {
            let journal = try MemoryForensicJournal(directory: directory, stem: "closed", segmentLimit: 128, recordLimit: 64)
            journal.close()
            journal.close()
            do {
                try journal.write(["status": "closed"])
                preconditionFailure("Closed descriptor accepted a packet")
            } catch MemoryForensicJournal.JournalError.writeFailed {}
        }
        test {
            // Compile and exercise the real public Mach and allocator APIs on the
            // macOS runner. No asserted RAM delta: background allocations may move.
            var info = task_vm_info_data_t()
            let capacity = MemoryLayout<task_vm_info_data_t>.stride / MemoryLayout<integer_t>.stride
            var count = UInt32(capacity)
            let status = withUnsafeMutablePointer(to: &info) { pointer in
                pointer.withMemoryRebound(to: integer_t.self, capacity: capacity) {
                    task_info(mach_task_self_, task_flavor_t(TASK_VM_INFO), $0, &count)
                }
            }
            precondition(status == KERN_SUCCESS && info.phys_footprint > 0)
            var record: [String: Any] = [:]
            MemoryForensicTaskFields.append(info, count: count, to: &record)
            precondition(record["task_vm_page_size_bytes"] != nil)
            let sampler = MemoryForensicNativeSampler()
            let now = ProcessInfo.processInfo.systemUptime
            sampler.append(now: now, to: &record)
            precondition(record["malloc_size_in_use_bytes"] != nil)
            precondition(record["metal_device_available"] is Bool)
            let firstCost = record["malloc_statistics_duration_ms"] as! Double
            sampler.append(now: now + 2, to: &record)
            precondition(record["malloc_statistics_age_seconds"] as? Double == 2)
            precondition(record["malloc_statistics_duration_ms"] as? Double == firstCost)
        }
        print("MemoryForensicsTests: \(passed) passed")
    }
}
