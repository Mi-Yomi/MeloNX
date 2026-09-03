import SwiftUI
import UIKit

struct ExperimentalMemorySettingsView: View {
    @AppStorage(JitCacheSettings.defaultsKey) private var selectedMiB = JitCacheChoice.automatic.rawValue
    @State private var export: DiagnosticExport?
    @State private var isExporting = false
    @State private var showingExportError = false

    private var selection: Binding<JitCacheChoice> {
        Binding(
            get: { JitCacheChoice(rawValue: selectedMiB) ?? .automatic },
            set: { selectedMiB = $0.rawValue }
        )
    }

    var body: some View {
        Form {
            Section {
                Picker("JIT Cache", selection: selection) {
                    ForEach(JitCacheChoice.allCases) { choice in
                        Text(choice.title).tag(choice)
                    }
                }
                .pickerStyle(.menu)

                if let applied = JitCacheSettings.launchSelection {
                    HStack {
                        Text("Requested at App Launch")
                        Spacer()
                        Text("\(applied.appliedMiB) MiB")
                            .foregroundColor(.secondary)
                    }
                    if selection.wrappedValue != applied.selected {
                        Label("Restart required", systemImage: "arrow.clockwise")
                            .foregroundColor(.orange)
                    }
                }
            } header: {
                Text("Experimental JIT Cache")
            } footer: {
                Text("Global setting for all games. Force-close MeloNX and reopen it after changing this value. Automatic keeps the upstream default: 512 MiB with TXM, 1024 MiB otherwise. A larger JIT cache can consume more RAM and reduce memory available to the game.")
            }

            Section {
                Button {
                    isExporting = true
                    Task {
                        do {
                            let files = try await MemoryDiagnostics.shared.exportLatestSession()
                            export = DiagnosticExport(files: files)
                        } catch {
                            showingExportError = true
                        }
                        isExporting = false
                    }
                } label: {
                    HStack {
                        Label("Export Latest Memory Diagnostics", systemImage: "square.and.arrow.up")
                        if isExporting { ProgressView() }
                    }
                }
                .disabled(isExporting)
            } header: {
                Text("Memory Diagnostics")
            } footer: {
                Text("Records memory every 2 seconds from game loading until emulation stops, plus memory warnings and background/foreground events. Keeps the last 5 sessions, up to 1 MiB of samples each. Export includes the latest session and its emulation log when available. The emulation log may contain game names and file paths.")
            }
        }
        .navigationTitle("Experimental")
        .navigationBarTitleDisplayMode(.inline)
        .sheet(item: $export) { item in
            DiagnosticShareSheet(files: item.files)
        }
        .alert("Diagnostics Unavailable", isPresented: $showingExportError) {
            Button("OK", role: .cancel) {}
        } message: {
            Text("Run a game first, then try exporting again. If recording or export failed, check that the device has free storage.")
        }
    }
}

private struct DiagnosticExport: Identifiable {
    let id = UUID()
    let files: [URL]
}

private struct DiagnosticShareSheet: UIViewControllerRepresentable {
    let files: [URL]

    func makeUIViewController(context: Context) -> UIActivityViewController {
        UIActivityViewController(activityItems: files, applicationActivities: nil)
    }

    func updateUIViewController(_ controller: UIActivityViewController, context: Context) {}
}
