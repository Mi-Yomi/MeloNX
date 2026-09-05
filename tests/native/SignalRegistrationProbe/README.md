# Darwin signal registration ABI probe

The console project links the production `UnixSignalHandlerRegistration.cs` directly. Its helper is compiled against the current Apple SDK, which supplies the native `struct sigaction`, `sigset_t`, flag values and signal delivery implementation. Run it as a separate process: it temporarily owns SIGSEGV/SIGBUS and restores both previous process actions in `finally`.

From the repository root on a macOS arm64 runner with the configured .NET SDK:

```bash
mkdir -p artifacts/test-results
xcrun --sdk macosx clang -std=c11 -Wall -Wextra -Werror -dynamiclib tests/native/signal-registration-probe.c -o artifacts/test-results/libSignalRegistrationProbe.dylib
dotnet run --project tests/native/SignalRegistrationProbe/SignalRegistrationProbe.csproj -c Release -- "$PWD/artifacts/test-results/libSignalRegistrationProbe.dylib"
xcrun --sdk iphoneos clang -std=c11 -Wall -Wextra -Werror -arch arm64 -isysroot "$(xcrun --sdk iphoneos --show-sdk-path)" -fsyntax-only tests/native/signal-registration-probe.c
```

The last command also checks the actual iOS arm64 SDK ABI at compile time. It does not claim device runtime coverage.

The runtime probe verifies the managed/native layouts, queries a saved one-argument handler, installs the production replacement for both signals, checks native flags, delivers both signals to native three-argument callbacks, then restores two distinct previous handlers, flags and masks and delivers both again. Handlers only update `volatile sig_atomic_t` counters; they never call managed code or Objective-C.

The negative control recreates the previous managed byte layout, including `sa_flags` at offset 136. Darwin libc reads flags at offset 12 and reports the installed flags as zero. The probe restores that action before delivering any signal. This demonstrates the registration ABI defect; it does not reproduce or attribute the GTA V guard-page exception.

The separate Swift breakpoint regression uses native arm64 instructions matching the bundled BreakpointJIT image, including both `brk #0xf00d` sites and `brk #0x69`. It checks that only those three exact PCs get the protocol's zero-result fallback, while an unrelated SIGBUS/SIGTRAP is forwarded to the distinct saved native handlers without altering the fault context:

```bash
xcrun --sdk macosx clang -std=c11 -Wall -Wextra -Werror -dynamiclib tests/native/jit-breakpoint-probe.c -o artifacts/test-results/libJITBreakpointProbe.dylib
swiftc -swift-version 6 src/MeloNX/MeloNX/Common/JIT26Breakpoint.swift tests/swift/JITBreakpointTests.swift -o artifacts/test-results/JITBreakpointTests
artifacts/test-results/JITBreakpointTests "$PWD/artifacts/test-results/libJITBreakpointProbe.dylib"
```

This standalone process retains its fixture library until exit. The application similarly retains the verified framework, since `BreakJITDetach` is called later during core initialization, after the initial UI JIT check.

Primary ABI references:

- [Apple XNU public signal.h](https://github.com/apple-oss-distributions/xnu/blob/main/bsd/sys/signal.h): public `struct sigaction` and Darwin signal flags, distinct from kernel `struct __sigaction` with a trampoline field.
- [Apple XNU sigset_t](https://github.com/apple-oss-distributions/xnu/blob/main/bsd/sys/_types/_sigset_t.h).
- [glibc public sigaction layout](https://sourceware.org/git/?p=glibc.git;a=blob;f=sysdeps/unix/sysv/linux/bits/sigaction.h;hb=HEAD): Linux mask, flags and naturally aligned restorer.
