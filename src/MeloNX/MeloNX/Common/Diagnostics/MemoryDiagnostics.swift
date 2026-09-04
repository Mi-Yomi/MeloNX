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
    private var managedCrashMarkerLock = os_unfair_lock_s()
    private var managedCrashMarkerFD: Int32 = -1
    private let managedCrashEntryRecord = Array("{\"schema_version\":1,\"event\":\"managed_crash_entry\"}\n".utf8)
    @MainActor private var managedCrashCallbackInstalled = false

    private static let managedCrashEntryCallback: DataCallbackFn = { _, userData in
        guard let userData else { return }
        Unmanaged<MemoryDiagnostics>
            .fromOpaque(userData)
            .takeUnretainedValue()
            .writeManagedCrashEntryMarker()
    }

    private init() {
        queue.setSpecific(key: queueKey, value: true)
    }

    @MainActor
    func installManagedCrashCallback() {
        guard !managedCrashCallbackInstalled else { return }
        managedCrashCallbackInstalled = true

        // This callback intentionally lives for the process lifetime. Replacing it would release
        // CallbackBox while a managed fatal callback could still be using its native userData.
        let userData = Unmanaged.passUnretained(self).toOpaque()
        "managed_crash_entry".withCString {
            RegisterCallback($0, Self.managedCrashEntryCallback, userData)
        }

        CallbackManager.register(name: "managed_crash") { data in
            // CallbackRegistry pins managed bytes only for the duration of this call.
            // CallbackData.data copies them before returning across the ABI boundary.
            Self.shared.recordManagedCrashPayload(data.data)
        }
    }

    private var logsDirectory: URL {
        FileManager.default.urls(for: .documentDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("logs", isDirectory: true)
    }

    private var diagnosticsDirectory: URL {
        logsDirectory.appendingPathComponent("MemoryDiagnostics", isDirectory: true)
    }

    @MainActor
    func startSession(coreLogURL: URL?, settings: Options) {
        let jit = JitCacheSettings.launchSelection
        let launchSettings = LaunchSettingsMetadata(
            resolutionScale: settings.resScale,
            scalingFilter: settings.scalingFilter.displayName,
            scalingFilterRaw: Int(settings.scalingFilter.rawValue),
            scalingFilterLevel: settings.scalingFilterLevel,
            antiAliasing: settings.antiAliasing.displayName,
            antiAliasingRaw: Int(settings.antiAliasing.rawValue),
            backendThreading: settings.backendThreading.displayName,
            backendThreadingRaw: Int(settings.backendThreading.rawValue),
            vSync: settings.disableVSync ? "Unbounded" : settings.vSyncMode.displayName,
            vSyncRaw: settings.disableVSync ? Int(VSyncMode.unbounded.rawValue) : Int(settings.vSyncMode.rawValue),
            customVSyncInterval: settings.customVSyncInterval,
            shaderCache: !settings.disableShaderCache,
            asyncShaderCompilation: settings.enableAsyncShaderCompilation,
            textureRecompression: settings.enableTextureRecompression,
            dockedMode: !settings.disableDockedMode,
            maxAnisotropy: settings.maxAnisotropy,
            memoryManager: settings.memoryManagerMode.displayName,
            memoryManagerRaw: Int(settings.memoryManagerMode.rawValue),
            expandRAMRequested: settings.expandRAM,
            graphicsDebug: settings.loggingGraphicsDebugLevel.displayName,
            debugLogging: settings.loggingEnableDebug,
            traceLogging: settings.loggingEnableTrace,
            fileLoggingOption: !settings.disableFileLog,
            coreLogCaptured: coreLogURL != nil
        )
        // modelName describes the hardware model, never the user-assigned device name.
        let metadata = SessionMetadata(
            model: UIDevice.modelName,
            os: ProcessInfo.processInfo.operatingSystemVersionString,
            physicalMemory: ProcessInfo.processInfo.physicalMemory,
            jit: jit,
            selectedJit: JitCacheChoice(rawValue: UserDefaults.standard.integer(forKey: JitCacheSettings.defaultsKey)) ?? .automatic,
            increasedMemoryLimit: checkAppEntitlement("com.apple.developer.kernel.increased-memory-limit"),
            extendedVirtualAddressing: checkAppEntitlement("com.apple.developer.kernel.extended-virtual-addressing"),
            sourceCommit: Self.sourceCommit(),
            launchSettings: launchSettings
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
                try openManagedCrashMarker(in: directory)
                try openSegment()

                var header: [String: Any] = [
                    "schema_version": 4,
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
                    "memory_pressure_critical_repeat_seconds": criticalTrimRepeatInterval,
                    "resolution_scale": metadata.launchSettings.resolutionScale,
                    "scaling_filter": metadata.launchSettings.scalingFilter,
                    "scaling_filter_raw": metadata.launchSettings.scalingFilterRaw,
                    "scaling_filter_level": metadata.launchSettings.scalingFilterLevel,
                    "anti_aliasing": metadata.launchSettings.antiAliasing,
                    "anti_aliasing_raw": metadata.launchSettings.antiAliasingRaw,
                    "backend_threading_requested": metadata.launchSettings.backendThreading,
                    "backend_threading_raw": metadata.launchSettings.backendThreadingRaw,
                    "vsync_requested": metadata.launchSettings.vSync,
                    "vsync_raw": metadata.launchSettings.vSyncRaw,
                    "custom_vsync_interval": metadata.launchSettings.customVSyncInterval,
                    "shader_cache_enabled": metadata.launchSettings.shaderCache,
                    "async_shader_compilation": metadata.launchSettings.asyncShaderCompilation,
                    "texture_recompression": metadata.launchSettings.textureRecompression,
                    "docked_mode": metadata.launchSettings.dockedMode,
                    "max_anisotropy": metadata.launchSettings.maxAnisotropy,
                    "memory_manager": metadata.launchSettings.memoryManager,
                    "memory_manager_raw": metadata.launchSettings.memoryManagerRaw,
                    "expand_ram_requested": metadata.launchSettings.expandRAMRequested,
                    "effective_guest_memory_gib": 4,
                    "graphics_debug": metadata.launchSettings.graphicsDebug,
                    "debug_logging": metadata.launchSettings.debugLogging,
                    "trace_logging": metadata.launchSettings.traceLogging,
                    "file_logging_option": metadata.launchSettings.fileLoggingOption,
                    "core_log_captured": metadata.launchSettings.coreLogCaptured,
                    "managed_crash_entry_file": "managed-crash-entry.jsonl",
                    "pressure_texture_eviction_enabled": false,
                    "ios_buffer_cache_limit_mib": 64,
                    "ios_buffer_cache_critical_limit_mib": 32,
                    "ios_buffer_cache_emergency_limit_mib": 16,
                    "ios_texture_cache_limit_mib": 64,
                    "ios_vulkan_command_buffers": 4,
                    "backend_threading_auto_effective": "Off",
                    "pressure_descriptor_trim_enabled": true,
                    "pressure_managed_gc_enabled": true,
                    "jit_pressure_snapshots_in_core_log": true,
                    "jit_usage_in_memory_samples": true
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

    /// Last-chance path used from an arbitrary managed thread. All storage is prepared when the
    /// session starts; this path deliberately avoids Data, JSONSerialization and Foundation I/O.
    private func writeManagedCrashEntryMarker() {
        os_unfair_lock_lock(&managedCrashMarkerLock)
        defer { os_unfair_lock_unlock(&managedCrashMarkerLock) }

        let descriptor = managedCrashMarkerFD
        guard descriptor >= 0 else { return }

        managedCrashEntryRecord.withUnsafeBytes { buffer in
            guard let baseAddress = buffer.baseAddress else { return }

            var offset = 0
            while offset < buffer.count {
                let result = Darwin.write(
                    descriptor,
                    baseAddress.advanced(by: offset),
                    buffer.count - offset
                )

                if result > 0 {
                    offset += result
                } else if result < 0 && errno == EINTR {
                    continue
                } else {
                    break
                }
            }
        }

        while Darwin.fsync(descriptor) != 0 && errno == EINTR {}
    }

    private func recordManagedCrashPayload(_ payload: Data?) {
        if DispatchQueue.getSpecific(key: queueKey) == true {
            writeManagedCrashRecord(payload)
        } else {
            // The managed process can terminate immediately after its AppDomain callback returns.
            // Synchronously persist and fsync this marker before crossing back into NativeAOT.
            queue.sync { writeManagedCrashRecord(payload) }
        }
    }

    private func writeManagedCrashRecord(_ payload: Data?) {
        guard file != nil else { return }

        var record: [String: Any] = [:]
        if let payload,
           let decoded = try? JSONSerialization.jsonObject(with: payload) as? [String: Any] {
            record = decoded
        } else {
            record["managed_payload_decode_error"] = true
        }

        if let schema = record.removeValue(forKey: "schema_version") {
            record["managed_crash_schema_version"] = schema
        }
        if let managedTime = record["time_utc"] {
            record["managed_time_utc"] = managedTime
        }

        let availableMemory = UInt64(os_proc_available_memory())
        record["event"] = "managed_crash"
        record["time_utc"] = Self.timestamp()
        record["elapsed_seconds"] = (ProcessInfo.processInfo.systemUptime - startedAtUptime).rounded()
        record["os_proc_available_memory_bytes"] = availableMemory
        record["managed_payload_bytes"] = payload?.count ?? 0

        var info = task_vm_info_data_t()
        let capacity = MemoryLayout<task_vm_info_data_t>.stride / MemoryLayout<integer_t>.stride
        var count = mach_msg_type_number_t(capacity)
        let result = withUnsafeMutablePointer(to: &info) { pointer in
            pointer.withMemoryRebound(to: integer_t.self, capacity: capacity) {
                task_info(mach_task_self_, task_flavor_t(TASK_VM_INFO), $0, &count)
            }
        }

        if result == KERN_SUCCESS {
            sampledPeak = max(sampledPeak, info.phys_footprint)
            record["phys_footprint_bytes"] = info.phys_footprint
            record["session_sampled_peak_bytes"] = sampledPeak
            record["process_phys_footprint_peak_bytes"] = max(0, info.ledger_phys_footprint_peak)
            appendTaskVmInfo(info, infoCount: count, availableMemory: availableMemory, to: &record)
        } else {
            record["task_info_error"] = result
        }

        do {
            try writeRecord(record)
        } catch {
            // Avoid Foundation error formatting in the fatal path; make one final sync attempt.
            try? file?.synchronize()
        }
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
        let launchSettings: LaunchSettingsMetadata
    }

    private struct LaunchSettingsMetadata: Sendable {
        let resolutionScale: Float
        let scalingFilter: String
        let scalingFilterRaw: Int
        let scalingFilterLevel: Int32
        let antiAliasing: String
        let antiAliasingRaw: Int
        let backendThreading: String
        let backendThreadingRaw: Int
        let vSync: String
        let vSyncRaw: Int
        let customVSyncInterval: Int32
        let shaderCache: Bool
        let asyncShaderCompilation: Bool
        let textureRecompression: Bool
        let dockedMode: Bool
        let maxAnisotropy: Float
        let memoryManager: String
        let memoryManagerRaw: Int
        let expandRAMRequested: Bool
        let graphicsDebug: String
        let debugLogging: Bool
        let traceLogging: Bool
        let fileLoggingOption: Bool
        let coreLogCaptured: Bool
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
        appendJitCacheUsage(to: &record)
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
            appendTaskVmInfo(info, infoCount: count, availableMemory: availableMemory, to: &record)
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

    private func appendJitCacheUsage(to record: inout [String: Any]) {
        let usage = Ryujinx.getJitCacheUsage()
        record["jit_cache_available"] = usage.available
        record["jit_cache_capacity_bytes"] = usage.capacityBytes
        record["jit_cache_used_bytes"] = usage.usedBytes
        record["jit_cache_free_bytes"] = usage.freeBytes
        record["jit_cache_address_high_water_bytes"] = usage.addressHighWaterBytes

        if usage.queryStatus < 0 {
            record["jit_cache_query_status"] = usage.queryStatus
        }
    }

    private func appendTaskVmInfo(
        _ info: task_vm_info_data_t,
        infoCount: mach_msg_type_number_t,
        availableMemory: UInt64,
        to record: inout [String: Any]
    ) {
        record["task_vm_info_count"] = infoCount
        if let limitOffset = MemoryLayout<task_vm_info_data_t>.offset(of: \.limit_bytes_remaining) {
            let integerStride = MemoryLayout<integer_t>.stride
            let requiredCount = mach_msg_type_number_t(
                (limitOffset + MemoryLayout<UInt64>.size + integerStride - 1) / integerStride
            )
            let hasLimitBytesRemaining = infoCount >= requiredCount
            record["task_vm_limit_bytes_remaining_available"] = hasLimitBytesRemaining
            if hasLimitBytesRemaining {
                record["task_vm_limit_bytes_remaining"] = info.limit_bytes_remaining
            }
        } else {
            record["task_vm_limit_bytes_remaining_available"] = false
        }
        record["estimated_process_limit_bytes"] = info.phys_footprint + availableMemory
        record["task_vm_virtual_size_bytes"] = info.virtual_size
        record["task_vm_resident_size_bytes"] = info.resident_size
        record["task_vm_resident_size_peak_bytes"] = info.resident_size_peak
        record["task_vm_internal_bytes"] = info.internal
        record["task_vm_compressed_bytes"] = info.compressed
        record["task_vm_reusable_bytes"] = info.reusable
        record["task_vm_external_bytes"] = info.external
        record["task_vm_device_bytes"] = info.device
        record["task_vm_region_count"] = info.region_count
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

    private func openManagedCrashMarker(in directory: URL) throws {
        let url = directory.appendingPathComponent("managed-crash-entry.jsonl")
        let descriptor = Darwin.open(
            url.path,
            O_WRONLY | O_CREAT | O_TRUNC | O_APPEND,
            0o600
        )
        guard descriptor >= 0 else { throw CocoaError(.fileWriteUnknown) }
        replaceManagedCrashMarkerFD(with: descriptor)
    }

    private func replaceManagedCrashMarkerFD(with descriptor: Int32) {
        os_unfair_lock_lock(&managedCrashMarkerLock)
        let previousDescriptor = managedCrashMarkerFD
        managedCrashMarkerFD = descriptor
        os_unfair_lock_unlock(&managedCrashMarkerLock)

        // Every callback holds the same lock through write+fsync, so no callback can still be
        // using the previous descriptor after the swap completes.
        if previousDescriptor >= 0 {
            _ = Darwin.close(previousDescriptor)
        }
    }

    private func synchronizeManagedCrashMarker() {
        os_unfair_lock_lock(&managedCrashMarkerLock)
        if managedCrashMarkerFD >= 0 {
            while Darwin.fsync(managedCrashMarkerFD) != 0 && errno == EINTR {}
        }
        os_unfair_lock_unlock(&managedCrashMarkerLock)
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
        replaceManagedCrashMarkerFD(with: -1)
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
        synchronizeManagedCrashMarker()
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
        for name in ["session.json", "managed-crash-entry.jsonl", "memory-previous.jsonl", "memory.jsonl"] {
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
