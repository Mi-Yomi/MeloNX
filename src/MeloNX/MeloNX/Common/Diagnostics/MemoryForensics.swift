import Foundation
import Darwin
import Metal

/// This policy is independent of UIKit and can be exercised by the macOS CI probe.
nonisolated struct MemoryForensicCadence {
    static let criticalHeadroom: UInt64 = 512 * 1024 * 1024
    static let recoveryHeadroom: UInt64 = 768 * 1024 * 1024
    private(set) var intervalSeconds = 2

    mutating func update(availableBytes: UInt64, active: Bool) -> Int {
        if !active || availableBytes >= Self.recoveryHeadroom {
            intervalSeconds = 2
        } else if availableBytes <= Self.criticalHeadroom {
            intervalSeconds = 1
        }
        return intervalSeconds
    }
}

nonisolated enum MemoryForensicTaskFields {
    /// TASK_VM_INFO is revisioned: a successful task_info call need not fill the
    /// entire structure present in the build SDK. Missing fields must stay absent.
    static func containsField(byteOffset: Int?, byteSize: Int, integerCount: UInt32) -> Bool {
        guard let byteOffset, byteOffset >= 0, byteSize > 0 else { return false }
        let (end, overflow) = byteOffset.addingReportingOverflow(byteSize)
        return !overflow && UInt64(end) <= UInt64(integerCount) * UInt64(MemoryLayout<integer_t>.stride)
    }

    static func append(_ info: task_vm_info_data_t, count: UInt32, to record: inout [String: Any]) {
        func add<T>(_ key: String, _ field: KeyPath<task_vm_info_data_t, T>) {
            if containsField(byteOffset: MemoryLayout<task_vm_info_data_t>.offset(of: field),
                             byteSize: MemoryLayout<T>.size, integerCount: count) {
                record[key] = info[keyPath: field]
            }
        }
        add("task_vm_page_size_bytes", \.page_size)
        add("task_vm_device_peak_bytes", \.device_peak)
        add("task_vm_internal_peak_bytes", \.internal_peak)
        add("task_vm_external_peak_bytes", \.external_peak)
        add("task_vm_reusable_peak_bytes", \.reusable_peak)
        add("task_vm_purgeable_volatile_pmap_bytes", \.purgeable_volatile_pmap)
        add("task_vm_purgeable_volatile_resident_bytes", \.purgeable_volatile_resident)
        add("task_vm_purgeable_volatile_virtual_bytes", \.purgeable_volatile_virtual)
        add("task_vm_compressed_peak_bytes", \.compressed_peak)
        add("task_vm_compressed_lifetime_bytes", \.compressed_lifetime)
        add("task_vm_ledger_purgeable_nonvolatile_bytes", \.ledger_purgeable_nonvolatile)
        // The missing 'n' in novolatile is the public SDK field spelling.
        add("task_vm_ledger_purgeable_nonvolatile_compressed_bytes", \.ledger_purgeable_novolatile_compressed)
        add("task_vm_ledger_purgeable_volatile_bytes", \.ledger_purgeable_volatile)
        add("task_vm_ledger_purgeable_volatile_compressed_bytes", \.ledger_purgeable_volatile_compressed)
        add("task_vm_ledger_network_nonvolatile_bytes", \.ledger_tag_network_nonvolatile)
        add("task_vm_ledger_network_nonvolatile_compressed_bytes", \.ledger_tag_network_nonvolatile_compressed)
        add("task_vm_ledger_network_volatile_bytes", \.ledger_tag_network_volatile)
        add("task_vm_ledger_network_volatile_compressed_bytes", \.ledger_tag_network_volatile_compressed)
        add("task_vm_ledger_media_footprint_bytes", \.ledger_tag_media_footprint)
        add("task_vm_ledger_media_footprint_compressed_bytes", \.ledger_tag_media_footprint_compressed)
        add("task_vm_ledger_media_nofootprint_bytes", \.ledger_tag_media_nofootprint)
        add("task_vm_ledger_media_nofootprint_compressed_bytes", \.ledger_tag_media_nofootprint_compressed)
        add("task_vm_ledger_graphics_footprint_bytes", \.ledger_tag_graphics_footprint)
        add("task_vm_ledger_graphics_footprint_compressed_bytes", \.ledger_tag_graphics_footprint_compressed)
        add("task_vm_ledger_graphics_nofootprint_bytes", \.ledger_tag_graphics_nofootprint)
        add("task_vm_ledger_graphics_nofootprint_compressed_bytes", \.ledger_tag_graphics_nofootprint_compressed)
        add("task_vm_ledger_neural_footprint_bytes", \.ledger_tag_neural_footprint)
        add("task_vm_ledger_neural_footprint_compressed_bytes", \.ledger_tag_neural_footprint_compressed)
        add("task_vm_ledger_neural_nofootprint_bytes", \.ledger_tag_neural_nofootprint)
        add("task_vm_ledger_neural_nofootprint_compressed_bytes", \.ledger_tag_neural_nofootprint_compressed)
        add("task_vm_decompressions_count", \.decompressions)
    }
}

/// Regular queue-based diagnostics, never a signal handler. These APIs do not
/// enumerate malloc objects, touch guest pages, or wait for a GPU command buffer.
nonisolated final class MemoryForensicNativeSampler {
    private var metalDevice: (any MTLDevice)?
    private var mallocSample: [String: Any] = [:]
    private var lastMallocSampleUptime: TimeInterval = -Double.infinity

    init() {
        // Retain only the device, not the view/layer/drawables. On iOS this is the
        // same system device used by MoltenVK; its metric overlaps VK memory budget.
        metalDevice = MTLCreateSystemDefaultDevice()
    }

    func append(now: TimeInterval, to record: inout [String: Any]) {
        let started = ProcessInfo.processInfo.systemUptime
        if now - lastMallocSampleUptime >= 10 {
            var stats = malloc_statistics_t()
            let mallocStarted = ProcessInfo.processInfo.systemUptime
            malloc_zone_statistics(nil, &stats)
            mallocSample = [
                "malloc_blocks_in_use": stats.blocks_in_use,
                "malloc_size_in_use_bytes": stats.size_in_use,
                "malloc_max_size_in_use_bytes": stats.max_size_in_use,
                "malloc_size_allocated_reserved_bytes": stats.size_allocated,
                "malloc_statistics_duration_ms": (ProcessInfo.processInfo.systemUptime - mallocStarted) * 1000
            ]
            lastMallocSampleUptime = now
        }
        for (key, value) in mallocSample { record[key] = value }
        record["malloc_statistics_age_seconds"] = max(0, now - lastMallocSampleUptime)
        record["metal_device_available"] = metalDevice != nil
        if let metalDevice {
            record["metal_current_allocated_size_bytes"] = metalDevice.currentAllocatedSize
        }
        record["native_allocator_sample_duration_ms"] = (ProcessInfo.processInfo.systemUptime - started) * 1000
    }
}

nonisolated final class MemoryForensicCoreReader {
    static let capacity = 64 * 1024
    private let buffer = UnsafeMutablePointer<UInt8>.allocate(capacity: MemoryForensicCoreReader.capacity)

    nonisolated struct Sample {
        let status: Int32
        let bytes: Int
        let value: [String: Any]?
    }

    func capture(_ query: (UnsafeMutablePointer<UInt8>, Int32) -> Int32) -> Sample {
        let status = query(buffer, Int32(Self.capacity))
        guard status > 0 else { return Sample(status: status, bytes: 0, value: nil) }
        guard status <= Int32(Self.capacity) else { return Sample(status: -5, bytes: 0, value: nil) }
        let data = Data(bytes: buffer, count: Int(status))
        guard let value = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any] else {
            // Never turn a partial/truncated UTF-8 payload into valid-looking JSON.
            return Sample(status: -5, bytes: Int(status), value: nil)
        }
        return Sample(status: status, bytes: Int(status), value: value)
    }

    deinit { buffer.deallocate() }
}

/// Two bounded, durable segments. A complete last line is usable even when the
/// process is killed without a callback; this does not promise a jetsam handler.
/// The owning diagnostics queue serializes every operation, including export.
nonisolated final class MemoryForensicJournal {
    nonisolated enum JournalError: Error { case recordTooLarge, writeFailed }
    let currentURL: URL
    let previousURL: URL
    let segmentLimit: Int
    let recordLimit: Int
    private var descriptor: Int32 = -1
    private(set) var bytesWritten = 0
    private(set) var synchronizationCount: UInt64 = 0
    private(set) var synchronizationDurationMilliseconds = 0.0

    init(directory: URL, stem: String, segmentLimit: Int, recordLimit: Int) throws {
        precondition(segmentLimit > 0 && recordLimit > 1 && recordLimit <= segmentLimit)
        currentURL = directory.appendingPathComponent(stem + ".jsonl")
        previousURL = directory.appendingPathComponent(stem + "-previous.jsonl")
        self.segmentLimit = segmentLimit
        self.recordLimit = recordLimit
        try openCurrent()
    }

    func write(_ record: [String: Any], durable: Bool = true) throws {
        var data = try JSONSerialization.data(withJSONObject: record, options: [.sortedKeys])
        guard data.count < recordLimit else { throw JournalError.recordTooLarge }
        data.append(0x0a)
        try writeLine(data, durable: durable)
    }

    func writeLine(_ data: Data, durable: Bool = true) throws {
        guard data.count <= recordLimit else { throw JournalError.recordTooLarge }
        guard descriptor >= 0 else { throw JournalError.writeFailed }
        if bytesWritten > segmentLimit - data.count {
            try synchronize()
            close()
            // rename replaces the previous segment atomically. Never delete both
            // segments first: the old current survives a failed rotation.
            guard Darwin.rename(currentURL.path, previousURL.path) == 0 else { throw JournalError.writeFailed }
            try openCurrent()
        }
        try data.withUnsafeBytes { buffer in
            guard let address = buffer.baseAddress else { return }
            var written = 0
            while written < buffer.count {
                let count = Darwin.write(descriptor, address.advanced(by: written), buffer.count - written)
                if count > 0 { written += count }
                else if count < 0 && errno == EINTR { continue }
                else { throw JournalError.writeFailed }
            }
        }
        bytesWritten += data.count
        if durable { try synchronize() }
    }

    func synchronize() throws {
        guard descriptor >= 0 else { return }
        let started = ProcessInfo.processInfo.systemUptime
        synchronizationCount += 1
        defer { synchronizationDurationMilliseconds += (ProcessInfo.processInfo.systemUptime - started) * 1000 }
        while Darwin.fsync(descriptor) != 0 {
            if errno != EINTR { throw JournalError.writeFailed }
        }
    }

    func close() {
        if descriptor >= 0 { _ = Darwin.close(descriptor) }
        descriptor = -1
    }

    private func openCurrent() throws {
        descriptor = Darwin.open(currentURL.path, O_WRONLY | O_CREAT | O_TRUNC | O_APPEND, 0o600)
        guard descriptor >= 0 else { throw JournalError.writeFailed }
        bytesWritten = 0
    }

    deinit { close() }
}
