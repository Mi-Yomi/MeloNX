import Foundation
import Darwin

@main
struct JITBreakpointTests {
    static func main() {
        precondition(CommandLine.arguments.count == 2, "Pass the native probe dylib path")
        var passed = 0
        func test(_ body: () -> Void) { body(); passed += 1 }
        let bytes = UnsafeMutableRawPointer.allocate(byteCount: 36, alignment: 4)
        defer { bytes.deallocate() }
        let valid: [UInt32] = [0xd2800030, 0xd43e01a0, 0xd65f03c0,
                               0xd2800010, 0xd43e01a0, 0xd65f03c0,
                               0xd4200d20, 0xd65f03c0, 0]
        func reset() { valid.withUnsafeBytes { bytes.copyMemory(from: $0.baseAddress!, byteCount: $0.count) } }
        func sites() -> JITBreakpointSites? {
            JITBreakpointSites.verified(getMapping: bytes, detach: bytes.advanced(by: 12), markMapping: bytes.advanced(by: 24))
        }
        reset()
        test {
            let verified = sites()!
            precondition(verified.contains(UInt64(UInt(bitPattern: bytes)) + 4))
            precondition(verified.contains(UInt64(UInt(bitPattern: bytes)) + 16))
            precondition(verified.contains(UInt64(UInt(bitPattern: bytes)) + 24))
            precondition(!verified.contains(0))
            precondition(!verified.contains(UInt64(UInt(bitPattern: bytes)) + 8))
            precondition(!verified.contains(UInt64(UInt(bitPattern: bytes)) + 20))
        }
        for index in [0, 1, 2, 3, 4, 5, 6, 7] {
            test {
                reset()
                bytes.storeBytes(of: UInt32(0), toByteOffset: index * 4, as: UInt32.self)
                precondition(sites() == nil)
            }
        }
        test {
            precondition(JITBreakpointSites.verified(getMapping: nil, detach: bytes, markMapping: bytes) == nil)
        }

        guard let library = dlopen(CommandLine.arguments[1], RTLD_NOW | RTLD_LOCAL) else {
            fatalError("Could not load native JIT probe")
        }
        func function(_ name: String) -> @convention(c) () -> Int32 {
            unsafeBitCast(dlsym(library, name)!, to: (@convention(c) () -> Int32).self)
        }
        precondition(function("jit_probe_begin")() == 0)
        defer {
            precondition(function("jit_probe_end")() == 0)
            // Production keeps the verified code image loaded. Keep the test
            // image loaded until process exit too, even after native restoration.
        }
        test {
            precondition(!jitBreakpointFallbackReady)
            precondition(installJITBreakpointHandler(library: library))
            precondition(jitBreakpointFallbackReady)
            precondition(installJITBreakpointHandler(library: library))
        }
        test { precondition(function("jit_probe_protocol")() == 0) }
        test { precondition(function("jit_probe_unrelated_delivery")() == 0) }
        typealias Handler = @convention(c) (Int32, UnsafeMutablePointer<siginfo_t>?, UnsafeMutableRawPointer?) -> Void
        typealias ContextProbe = @convention(c) (Handler, Int32) -> Int32
        let contextProbe = unsafeBitCast(dlsym(library, "jit_probe_context")!, to: ContextProbe.self)
        test { precondition(contextProbe(jitBreakpointSignalHandler, 1) == 0) }
        test { precondition(contextProbe(jitBreakpointSignalHandler, 0) == 0) }
        print("JITBreakpointTests: \(passed) passed (actual arm64 BRK delivery, unrelated signal forwarding, exact context checks)")
    }
}
