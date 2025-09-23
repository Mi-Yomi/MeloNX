//
//  LogCapture.swift
//  MeloNX
//
//  Created by Stossy11 on 22/09/2025.
//


import SwiftUI

class LogCapture {
    static let shared = LogCapture()

    private var stdoutPipe: Pipe?
    private var stderrPipe: Pipe?
    private let originalStdout: Int32
    private let originalStderr: Int32

    var capturedLogs: [String] = [] {
        didSet {
            DispatchQueue.main.async {
                NotificationCenter.default.post(name: .newLogCaptured, object: nil)
            }
        }
    }

    private init() {
        originalStdout = dup(STDOUT_FILENO)
        originalStderr = dup(STDERR_FILENO)
        startCapturing()
    }

    func startCapturing() {
        stdoutPipe = Pipe()
        stderrPipe = Pipe()

        redirectOutput(to: stdoutPipe!, fileDescriptor: STDOUT_FILENO)
        redirectOutput(to: stderrPipe!, fileDescriptor: STDERR_FILENO)

        setupReadabilityHandler(for: stdoutPipe!, isStdout: true)
        setupReadabilityHandler(for: stderrPipe!, isStdout: false)
    }

    func stopCapturing() {
        dup2(originalStdout, STDOUT_FILENO)
        dup2(originalStderr, STDERR_FILENO)

        stdoutPipe?.fileHandleForReading.readabilityHandler = nil
        stderrPipe?.fileHandleForReading.readabilityHandler = nil
    }

    private func redirectOutput(to pipe: Pipe, fileDescriptor: Int32) {
        dup2(pipe.fileHandleForWriting.fileDescriptor, fileDescriptor)
    }

    private func setupReadabilityHandler(for pipe: Pipe, isStdout: Bool) {
        pipe.fileHandleForReading.readabilityHandler = { [weak self] fileHandle in
            let data = fileHandle.availableData
            let originalFD = isStdout ? self?.originalStdout : self?.originalStderr
            write(originalFD ?? STDOUT_FILENO, (data as NSData).bytes, data.count)

            if let logString = String(data: data, encoding: .utf8),
               let cleanedLog = self?.cleanLog(logString), !cleanedLog.isEmpty {
                self?.capturedLogs.append(cleanedLog)
            }
        }
    }

    private func cleanLog(_ raw: String) -> String? {
        let lines = raw.split(separator: "\n")
        let filteredLines = lines.filter { line in
            !line.contains("SwiftUI") &&
            !line.contains("ForEach") &&
            !line.contains("VStack") &&
            !line.contains("Invalid frame dimension (negative or non-finite).")
        }

        let cleaned = filteredLines.map { line -> String in
            if let tabRange = line.range(of: "\t") {
                return line[tabRange.upperBound...].trimmingCharacters(in: .whitespacesAndNewlines)
            }
            return line.trimmingCharacters(in: .whitespacesAndNewlines)
        }.joined(separator: "\n")

        return cleaned.isEmpty ? nil : cleaned.replacingOccurrences(of: "\n\n", with: "\n")
    }

    deinit {
        stopCapturing()
    }
}


extension Notification.Name {
    static let newLogCaptured = Notification.Name("newLogCaptured")
}
