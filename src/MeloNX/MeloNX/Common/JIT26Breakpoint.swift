//
//  JIT26Breakpoint.swift
//  MeloNX
//
//  Created by Stossy11 on 25/4/2026.
//

import Foundation
import Darwin

/// Exact sites in the bundled BreakpointJIT protocol, checked before installing
/// a handler. An arbitrary memory fault must never become a successful load of 0.
nonisolated struct JITBreakpointSites {
    let getMapping: UInt64
    let detach: UInt64
    let markMapping: UInt64

    func contains(_ pc: UInt64) -> Bool {
        pc != 0 && (pc == getMapping || pc == detach || pc == markMapping)
    }

    static func verified(getMapping: UnsafeRawPointer?, detach: UnsafeRawPointer?, markMapping: UnsafeRawPointer?) -> JITBreakpointSites? {
        guard let getMapping, let detach, let markMapping else { return nil }
        // BreakGet: mov x16,#1; brk #0xf00d; ret
        // BreakDetach: mov x16,#0; brk #0xf00d; ret
        // BreakMark: brk #0x69; ret
        guard getMapping.load(as: UInt32.self) == 0xd2800030,
              getMapping.load(fromByteOffset: 4, as: UInt32.self) == 0xd43e01a0,
              getMapping.load(fromByteOffset: 8, as: UInt32.self) == 0xd65f03c0,
              detach.load(as: UInt32.self) == 0xd2800010,
              detach.load(fromByteOffset: 4, as: UInt32.self) == 0xd43e01a0,
              detach.load(fromByteOffset: 8, as: UInt32.self) == 0xd65f03c0,
              markMapping.load(as: UInt32.self) == 0xd4200d20,
              markMapping.load(fromByteOffset: 4, as: UInt32.self) == 0xd65f03c0 else { return nil }
        return JITBreakpointSites(getMapping: UInt64(UInt(bitPattern: getMapping)) + 4,
                                  detach: UInt64(UInt(bitPattern: detach)) + 4,
                                  markMapping: UInt64(UInt(bitPattern: markMapping)))
    }
}

// Written once during main-thread startup, before either handler is installed.
// The handler reads only POD state: no locks, allocation, logging or symbol lookup.
nonisolated(unsafe) private var jitBreakpointSites = JITBreakpointSites(getMapping: 0, detach: 0, markMapping: 0)
nonisolated(unsafe) private var jitPreviousTrap = sigaction()
nonisolated(unsafe) private var jitPreviousBus = sigaction()
nonisolated(unsafe) private var jitBreakpointInstalled = false
nonisolated(unsafe) private var jitBreakpointLibrary: UnsafeMutableRawPointer?

nonisolated var jitBreakpointFallbackReady: Bool { jitBreakpointInstalled }

nonisolated private func terminateUnhandledJITSignal(_ signalNumber: Int32) {
    // SIG_DFL/SIG_IGN are sentinel values, not callable function pointers. Ignore
    // is undefined for a real synchronous fault and would retry the fault forever.
    var action = sigaction()
    if sigaction(signalNumber, &action, nil) != 0 || raise(signalNumber) != 0 {
        _exit(128 + signalNumber)
    }
    // This signal is masked while its handler executes. The pending signal takes
    // its default action when this handler returns; do not advance the fault PC.
}

nonisolated func jitBreakpointSignalHandler(sig: Int32, info: UnsafeMutablePointer<siginfo_t>?, context: UnsafeMutableRawPointer?) {
    #if arch(arm64)
    if (sig == SIGTRAP || sig == SIGBUS), let context {
        let uc = context.assumingMemoryBound(to: ucontext_t.self)
        if let machine = uc.pointee.uc_mcontext, jitBreakpointSites.contains(machine.pointee.__ss.__pc) {
            machine.pointee.__ss.__pc += 4
            machine.pointee.__ss.__x.0 = 0
            return
        }
    }
    #endif

    let previous = sig == SIGTRAP ? jitPreviousTrap : jitPreviousBus
    let address = unsafeBitCast(previous.__sigaction_u.__sa_handler, to: UInt.self)
    if address <= 1 {
        terminateUnhandledJITSignal(sig)
    } else if previous.sa_flags & SA_SIGINFO != 0 {
        previous.__sigaction_u.__sa_sigaction?(sig, info, context)
    } else {
        previous.__sigaction_u.__sa_handler?(sig)
    }
}

/// Keep this fallback for the three protocol breakpoints, including BreakJITDetach
/// during core initialization. Other faults are forwarded or terminated without
/// changing the faulting instruction pointer or manufacturing a zero result.
/// Returns false before any installation when the bundled protocol has changed.
@discardableResult
func JIT26BreakpointHandler() -> Bool {
    if jitBreakpointInstalled { return true }
    guard let frameworks = Bundle.main.privateFrameworksURL else { return false }
    let path = frameworks.appendingPathComponent("BreakpointJIT.framework/BreakpointJIT").path
    guard let library = dlopen(path, RTLD_NOW | RTLD_LOCAL) else { return false }
    if installJITBreakpointHandler(library: library) {
        // The instruction addresses remain valid for the process lifetime.
        jitBreakpointLibrary = library
        return true
    }
    dlclose(library)
    return false
}

/// Uses the same native installation path for the standalone macOS regression.
/// The caller retains the library until the process exits.
nonisolated func installJITBreakpointHandler(library: UnsafeMutableRawPointer) -> Bool {
    if jitBreakpointInstalled { return true }
    guard let sites = JITBreakpointSites.verified(
        getMapping: dlsym(library, "BreakGetJITMapping").map { UnsafeRawPointer($0) },
        detach: dlsym(library, "BreakJITDetach").map { UnsafeRawPointer($0) },
        markMapping: dlsym(library, "BreakMarkJITMapping").map { UnsafeRawPointer($0) }) else { return false }

    var previousTrap = sigaction()
    var previousBus = sigaction()
    guard sigaction(SIGTRAP, nil, &previousTrap) == 0,
          sigaction(SIGBUS, nil, &previousBus) == 0 else { return false }
    jitBreakpointSites = sites
    jitPreviousTrap = previousTrap
    jitPreviousBus = previousBus

    var action = sigaction()
    action.sa_flags = SA_SIGINFO
    action.__sigaction_u.__sa_sigaction = jitBreakpointSignalHandler
    guard sigaction(SIGTRAP, &action, &previousTrap) == 0 else { return false }
    jitPreviousTrap = previousTrap
    guard sigaction(SIGBUS, &action, &previousBus) == 0 else {
        // Failure to roll back would leave a live handler with unloadable sites.
        if sigaction(SIGTRAP, &previousTrap, nil) != 0 { _exit(1) }
        return false
    }
    jitPreviousBus = previousBus
    jitBreakpointInstalled = true
    return true
}
