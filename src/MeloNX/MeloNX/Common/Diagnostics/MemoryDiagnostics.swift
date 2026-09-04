import Foundation
import UIKit
import Darwin
import os

/// All file handles, timer state and samples are confined to `queue`.
/// UIKit metadata is captured on the main actor before entering that queue.
nonisolated final class MemoryDiagnostics: @unchecked Sendable {
    static let shared = MemoryDiagnostics()

    private let queue = DispatchQueue(label: "MeloNX.MemoryDiagnostics", qos: .utility)
    private let queueKey = DispatchSpecificKey<Bool>()
    private let segmentLimit = 512 * 1024
    private let sessionsToKeep = 5
    // GTA V can add 200-285 MiB between two-second samples. Start reclaim while enough
    // headroom remains for a transient Metal allocation and repeat before the next burst.
    private let lowAvailableMemory: UInt64 = 1536 * 1024 * 1024
    private let criticalAvailableMemory: UInt64 = 1024 * 1024 * 1024
    private let lowTrimRepeatInterval: TimeInterval = 20
    private let criticalTrimRepeatInterval: TimeInterval = 6
    private var timer: DispatchSourceTimer?
    private var observers: [NSObjectProtocol] = []
    private var file: FileHandle?
    private var sessionDirectory: URL?
    private var bytesWritten = 0
    private var sampledPeak: UInt64 = 0
    private var startedAtUptime: TimeInterval = 0
    private var lowSampleStreak = 0
    private var lastAcceptedTrimUptime: TimeInterval = -Double.infinity
    private var lastAcceptedCriticalTrimUptime: TimeInterval = -Double.infinity

    private init() {
        queue.setSpecific(key: queueKey, value: true)
    }

    private var logsDirectory: URL {
        FileManager.default.urls(for: .documentDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("logs", isDirectory: true)
    }

    private var diagnosticsDirectory: URL {
        logsDirectory.appendingPathComponent("MemoryDiagnostics", isDirectory: true)
    }

    @MainActor
    func startSession(coreLogURL: URL?) {
        let jit = JitCacheSettings.launchSelection
        // modelName describes the hardware model, never the user-assigned device name.
        let metadata = SessionMetadata(
            model: UIDevice.modelName,
            os: ProcessInfo.processInfo.operatingSystemVersionString,
            physicalMemory: ProcessInfo.processInfo.physicalMemory,
            jit: jit,
            selectedJit: JitCacheChoice(rawValue: UserDefaults.standard.integer(forKey: JitCacheSettings.defaultsKey)) ?? .automatic,
            increasedMemoryLimit: checkAppEntitlement("com.apple.developer.kernel.increased-memory-limit"),
            extendedVirtualAddressing: checkAppEntitlement("com.apple.developer.kernel.extended-virtual-addressing"),
            sourceCommit: Self.sourceCommit()
        )

        // Synchronous startup ensures the first record exists before game loading starts.
        queue.sync {
            finishSession(exitCode: nil)
            do {
                let directory = try createSessionDirectory()
                sessionDirectory = directory
                pruneSessions()
                startedAtUptime = ProcessInfo.processInfo.systemUptime
                sampledPeak = 0
                lowSampleStreak = 0
                lastAcceptedTrimUptime = -Double.infinity
                lastAcceptedCriticalTrimUptime = -Double.infinity
                try openSegment()

                var header: [String: Any] = [
                    "schema_version": 2,
                    "event": "session_start",
                    "time_utc": Self.timestamp(),
                    "device_model": metadata.model,
                    "os": metadata.os,
                    "physical_memory_bytes": metadata.physicalMemory,
                    "sample_interval_seconds": 2,
                    "selected_jit_cache": metadata.selectedJit.title,
                    "increased_memory_limit_entitlement": metadata.increasedMemoryLimit,
                    "extended_virtual_addressing_entitlement": metadata.extendedVirtualAddressing,
                    "memory_pressure_low_available_bytes": lowAvailableMemory,
                    "memory_pressure_critical_available_bytes": criticalAvailableMemory,
                    "memory_pressure_low_repeat_seconds": lowTrimRepeatInterval,
                    "memory_pressure_critical_repeat_seconds": criticalTrimRepeatInterval
                ]
                if let sourceCommit = metadata.sourceCommit {
                    header["source_commit"] = sourceCommit
                }
                if let jit = metadata.jit {
                    header["launch_jit_cache_selection"] = jit.selected.title
                    header["requested_jit_cache_mib"] = jit.appliedMiB
                    header["jit_environment_applied"] = jit.environmentApplied
                    header["has_txm"] = jit.hasTXM
                }
                // Only a generated log basename is persisted; never a ROM or sandbox path.
                if let coreLogURL, coreLogURL.lastPathComponent.hasPrefix("MeloNX-Log-") {
                    header["emulation_log_file"] = coreLogURL.lastPathComponent
                }
                let headerData = try JSONSerialization.data(withJSONObject: header, options: [.sortedKeys])
                try headerData.write(to: directory.appendingPathComponent("session.json"), options: .atomic)
                try writeRecord(header)
                recordSample(event: "loading_started")
                guard file != nil else { return }
                installObservers()
                let sampler = DispatchSource.makeTimerSource(queue: queue)
                sampler.schedule(deadline: .now() + 2, repeating: 2, leeway: .milliseconds(100))
                sampler.setEventHandler { [weak self] in
                    self?.recordSample(event: "sample")
                }
                timer = sampler
                sampler.resume()
            } catch {
                closeSession()
                // Do not put NSError descriptions (which can contain paths) in this log.
                print("Memory diagnostics could not start.")
            }
        }
    }

    func stopSession(exitCode: Int) {
        queue.sync { finishSession(exitCode: exitCode) }
    }

    /// Copy a stable snapshot on the same queue that serializes sample writes.
    func exportLatestSession() async throws -> [URL] {
        try await withCheckedThrowingContinuation { continuation in
            queue.async {
                do {
                    let urls = try self.makeExportSnapshot()
                    continuation.resume(returning: urls)
                } catch {
                    continuation.resume(throwing: error)
                }
            }
        }
    }

    private struct SessionMetadata: Sendable {
        let model: String
        let os: String
        let physicalMemory: UInt64
        let jit: JitCacheLaunchSelection?
        let selectedJit: JitCacheChoice
        let increasedMemoryLimit: Bool
        let extendedVirtualAddressing: Bool
        let sourceCommit: String?
    }

    private func installObservers() {
        let events: [(Notification.Name, String)] = [
            (UIApplication.didReceiveMemoryWarningNotification, "memory_warning"),
            (UIApplication.didEnterBackgroundNotification, "background"),
            (UIApplication.willEnterForegroundNotification, "foreground"),
            (UIApplication.willTerminateNotification, "app_terminating")
        ]
        for (name, event) in events {
            observers.append(NotificationCenter.default.addObserver(forName: name, object: nil, queue: nil) { [weak self] _ in
                guard let self else { return }
                // Flush lifecycle events before UIKit can suspend the app.
                if DispatchQueue.getSpecific(key: self.queueKey) == true {
                    self.recordSample(event: event)
                } else {
                    self.queue.sync { self.recordSample(event: event) }
                }
            })
        }
    }

    private func recordSample(event: String, exitCode: Int? = nil) {
        guard file != nil else { return }
        let availableMemory = UInt64(os_proc_available_memory())
        var info = task_vm_info_data_t()
        let capacity = MemoryLayout<task_vm_info_data_t>.stride / MemoryLayout<integer_t>.stride
        var count = mach_msg_type_number_t(capacity)
        let result = withUnsafeMutablePointer(to: &info) { pointer in
            pointer.withMemoryRebound(to: integer_t.self, capacity: capacity) {
                task_info(mach_task_self_, task_flavor_t(TASK_VM_INFO), $0, &count)
            }
        }
        var record: [String: Any] = [
            "event": event,
            "time_utc": Self.timestamp(),
            "elapsed_seconds": (ProcessInfo.processInfo.systemUptime - startedAtUptime).rounded(),
            "os_proc_available_memory_bytes": availableMemory
        ]
        if let pressure = evaluateMemoryPressure(event: event, availableMemory: availableMemory) {
            record["gpu_trim_request"] = pressure.level
            record["gpu_trim_source"] = pressure.source
            record["gpu_trim_request_result"] = pressure.result
        }
        if result == KERN_SUCCESS {
            sampledPeak = max(sampledPeak, info.phys_footprint)
            record["phys_footprint_bytes"] = info.phys_footprint
            record["session_sampled_peak_bytes"] = sampledPeak
            // Kernel peak covers the whole process lifetime, including earlier games.
            record["process_phys_footprint_peak_bytes"] = max(0, info.ledger_phys_footprint_peak)
        } else {
            record["task_info_error"] = result
        }
        if let exitCode { record["emulation_exit_code"] = exitCode }
        do {
            try writeRecord(record)
        } catch {
            closeSession()
            print("Memory diagnostics stopped after a write error.")
        }
    }

    private func evaluateMemoryPressure(event: String, availableMemory: UInt64) -> (level: String, source: String, result: Int32)? {
        let now = ProcessInfo.processInfo.systemUptime

        if event == "memory_warning", now - lastAcceptedCriticalTrimUptime >= criticalTrimRepeatInterval {
            let result = Ryujinx.reportMemoryPressure(availableBytes: availableMemory, severity: 2, source: 2)
            if result > 0 {
                lastAcceptedTrimUptime = now
                lastAcceptedCriticalTrimUptime = now
            }
            return ("critical", "uikit_warning", result)
        }

        guard event == "sample" else { return nil }

        if availableMemory <= criticalAvailableMemory {
            lowSampleStreak = 0
            guard now - lastAcceptedCriticalTrimUptime >= criticalTrimRepeatInterval else {
                return nil
            }
            let result = Ryujinx.reportMemoryPressure(availableBytes: availableMemory, severity: 2, source: 1)
            if result > 0 {
                lastAcceptedTrimUptime = now
                lastAcceptedCriticalTrimUptime = now
            }
            return ("critical", "available_memory", result)
        }

        guard availableMemory <= lowAvailableMemory else {
            lowSampleStreak = 0
            return nil
        }

        lowSampleStreak += 1
        guard lowSampleStreak >= 2,
              now - lastAcceptedTrimUptime >= lowTrimRepeatInterval else {
            return nil
        }

        let result = Ryujinx.reportMemoryPressure(availableBytes: availableMemory, severity: 1, source: 1)
        if result > 0 {
            lastAcceptedTrimUptime = now
        }
        return ("low", "available_memory", result)
    }

    private func writeRecord(_ record: [String: Any]) throws {
        var data = try JSONSerialization.data(withJSONObject: record, options: [.sortedKeys])
        data.append(0x0a)
        if bytesWritten + data.count > segmentLimit {
            try rotateSegment()
        }
        guard let file else { return }
        try file.write(contentsOf: data)
        // A forced termination skips normal shutdown. Persist every sample directly.
        try file.synchronize()
        bytesWritten += data.count
    }

    private func openSegment() throws {
        guard let sessionDirectory else { return }
        let url = sessionDirectory.appendingPathComponent("memory.jsonl")
        guard FileManager.default.createFile(atPath: url.path, contents: nil) else {
            throw CocoaError(.fileWriteUnknown)
        }
        file = try FileHandle(forWritingTo: url)
        bytesWritten = 0
    }

    private func rotateSegment() throws {
        guard let sessionDirectory else { return }
        try file?.close()
        file = nil
        let current = sessionDirectory.appendingPathComponent("memory.jsonl")
        let previous = sessionDirectory.appendingPathComponent("memory-previous.jsonl")
        if FileManager.default.fileExists(atPath: previous.path) {
            try FileManager.default.removeItem(at: previous)
        }
        try FileManager.default.moveItem(at: current, to: previous)
        try openSegment()
    }

    private func finishSession(exitCode: Int?) {
        if file != nil { recordSample(event: "session_end", exitCode: exitCode) }
        closeSession()
    }

    private func closeSession() {
        timer?.cancel()
        timer = nil
        for observer in observers { NotificationCenter.default.removeObserver(observer) }
        observers.removeAll()
        try? file?.close()
        file = nil
        sessionDirectory = nil
    }

    private func createSessionDirectory() throws -> URL {
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.timeZone = TimeZone(secondsFromGMT: 0)
        formatter.dateFormat = "yyyyMMdd-HHmmss-SSS"
        let name = "session-" + formatter.string(from: Date())
        var directory = diagnosticsDirectory.appendingPathComponent(name, isDirectory: true)
        var suffix = 0
        while FileManager.default.fileExists(atPath: directory.path) {
            suffix += 1
            directory = diagnosticsDirectory.appendingPathComponent("\(name)-\(suffix)", isDirectory: true)
        }
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        return directory
    }

    private func sessionDirectories() -> [URL] {
        let keys: Set<URLResourceKey> = [.isDirectoryKey, .creationDateKey]
        let entries = (try? FileManager.default.contentsOfDirectory(
            at: diagnosticsDirectory, includingPropertiesForKeys: Array(keys), options: .skipsHiddenFiles
        )) ?? []
        return entries.filter {
            $0.lastPathComponent.hasPrefix("session-") && (try? $0.resourceValues(forKeys: keys).isDirectory) == true
        }.sorted {
            let first = (try? $0.resourceValues(forKeys: keys).creationDate) ?? .distantPast
            let second = (try? $1.resourceValues(forKeys: keys).creationDate) ?? .distantPast
            return first == second ? $0.lastPathComponent > $1.lastPathComponent : first > second
        }
    }

    private func pruneSessions() {
        for directory in sessionDirectories().dropFirst(sessionsToKeep) {
            try? FileManager.default.removeItem(at: directory)
        }
    }

    private func makeExportSnapshot() throws -> [URL] {
        try file?.synchronize()
        guard let latest = sessionDirectory ?? sessionDirectories().first else {
            throw ExportError.noSessions
        }
        let fileManager = FileManager.default
        let exportDirectory = fileManager.temporaryDirectory.appendingPathComponent("MeloNX-Memory-Export", isDirectory: true)
        if fileManager.fileExists(atPath: exportDirectory.path) {
            try fileManager.removeItem(at: exportDirectory)
        }
        try fileManager.createDirectory(at: exportDirectory, withIntermediateDirectories: true)

        var exported: [URL] = []
        for name in ["session.json", "memory-previous.jsonl", "memory.jsonl"] {
            let source = latest.appendingPathComponent(name)
            if fileManager.fileExists(atPath: source.path) {
                let destination = exportDirectory.appendingPathComponent(name)
                try fileManager.copyItem(at: source, to: destination)
                exported.append(destination)
            }
        }
        // Pair only with the exact stdout log saved in this session's metadata.
        if let data = try? Data(contentsOf: latest.appendingPathComponent("session.json")),
           let header = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
           let name = header["emulation_log_file"] as? String,
           name.hasPrefix("MeloNX-Log-"), name.hasSuffix(".log"),
           (name as NSString).lastPathComponent == name {
            let source = logsDirectory.appendingPathComponent(name)
            if fileManager.fileExists(atPath: source.path) {
                let destination = exportDirectory.appendingPathComponent(name)
                try fileManager.copyItem(at: source, to: destination)
                exported.append(destination)
            }
        }
        return exported
    }

    private enum ExportError: Error {
        case noSessions
    }

    private static func timestamp() -> String {
        ISO8601DateFormatter().string(from: Date())
    }

    private static func sourceCommit() -> String? {
        guard let value = Bundle.main.object(forInfoDictionaryKey: "MeloNXSourceCommit") as? String else {
            return nil
        }
        let commit = value.trimmingCharacters(in: .whitespacesAndNewlines)
        return commit.isEmpty || commit.contains("$(") ? nil : commit
    }
}
