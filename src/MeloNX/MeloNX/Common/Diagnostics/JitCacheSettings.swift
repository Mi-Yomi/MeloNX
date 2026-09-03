import Foundation
import Darwin

nonisolated enum JitCacheChoice: Int, CaseIterable, Identifiable, Sendable {
    case automatic = 0
    case mib512 = 512
    case mib768 = 768
    case mib1024 = 1024

    var id: Int { rawValue }

    var title: String {
        self == .automatic ? "Automatic" : "\(rawValue) MiB"
    }
}

nonisolated struct JitCacheLaunchSelection: Sendable {
    let selected: JitCacheChoice
    let appliedMiB: Int
    let hasTXM: Bool
    let environmentApplied: Bool
}

@MainActor
enum JitCacheSettings {
    static let defaultsKey = "experimentalJitCacheMiB"
    private(set) static var launchSelection: JitCacheLaunchSelection?

    /// Set once, before the native runtime can initialize its process-wide JIT cache.
    static func applyAtLaunch() {
        guard launchSelection == nil else { return }

        let selected = JitCacheChoice(rawValue: UserDefaults.standard.integer(forKey: defaultsKey)) ?? .automatic
        let result: Int32
        if selected == .automatic {
            result = unsetenv("MELONX_JIT_CACHE_MIB")
        } else {
            result = setenv("MELONX_JIT_CACHE_MIB", String(selected.rawValue), 1)
        }

        let hasTXM = getenv("HAS_TXM").map { String(cString: $0) == "1" } ?? false
        let actualChoice = getenv("MELONX_JIT_CACHE_MIB")
            .flatMap { Int(String(cString: $0)) }
            .flatMap { JitCacheChoice(rawValue: $0) } ?? .automatic

        launchSelection = JitCacheLaunchSelection(
            selected: selected,
            appliedMiB: actualChoice == .automatic ? (hasTXM ? 512 : 1024) : actualChoice.rawValue,
            hasTXM: hasTXM,
            environmentApplied: result == 0
        )
    }
}
