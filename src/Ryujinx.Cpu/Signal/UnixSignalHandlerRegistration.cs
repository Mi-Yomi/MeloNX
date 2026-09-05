using System;
using System.Runtime.InteropServices;

namespace Ryujinx.Cpu.Signal
{
    static partial class UnixSignalHandlerRegistration
    {
        // These are libc structures, not the kernel syscall structures. Darwin's
        // sigset_t is 32 bits; using glibc's 128-byte mask silently loses sa_flags.
        [StructLayout(LayoutKind.Sequential)]
        internal struct DarwinSigAction
        {
            public nint sa_handler;
            public uint sa_mask;
            public int sa_flags;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal unsafe struct LinuxSigSet
        {
            public fixed ulong Values[16];
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct LinuxSigAction
        {
            public nint sa_handler;
            public LinuxSigSet sa_mask;
            public int sa_flags;
            public nint sa_restorer;
        }

        // Keep the entire old action, including its mask, flags and restorer.
        // This normalized wrapper is never passed to libc.
        internal readonly struct SigAction
        {
            internal readonly DarwinSigAction Darwin;
            internal readonly LinuxSigAction Linux;
            internal readonly bool IsDarwin;

            internal SigAction(DarwinSigAction action) { Darwin = action; IsDarwin = true; }
            internal SigAction(LinuxSigAction action) { Linux = action; IsDarwin = false; }

            public nint sa_handler => IsDarwin ? Darwin.sa_handler : Linux.sa_handler;
            public int sa_flags => IsDarwin ? Darwin.sa_flags : Linux.sa_flags;
            public bool IsSigInfo => (sa_flags & (IsDarwin ? DarwinSigInfo : LinuxSigInfo)) != 0;
        }

        internal readonly record struct Registration(SigAction Segfault, SigAction Bus, bool HasBus);

        internal const int SegfaultSignal = 11;
        internal const int DarwinBusSignal = 10;
        internal const int DarwinSigInfo = 0x40;
        internal const int LinuxSigInfo = 0x04;

        internal delegate int SigActionInvoker(int signal, nint action, nint oldAction);

        [LibraryImport("libc", EntryPoint = "sigaction", SetLastError = true)]
        private static partial int NativeSigAction(int signal, nint action, nint oldAction);

        private static bool IsDarwin => OperatingSystem.IsMacOS() || OperatingSystem.IsIOS();

        public static SigAction GetSegfaultExceptionHandler() => GetSegfaultExceptionHandler(IsDarwin, NativeSigAction);

        internal static SigAction GetSegfaultExceptionHandler(bool isDarwin, SigActionInvoker invoke) =>
            GetAction(SegfaultSignal, isDarwin, invoke);

        // Capture every chain target before installing either signal. The caller
        // can publish its native handler configuration before the first syscall.
        public static Registration GetExceptionHandlers() => GetExceptionHandlers(IsDarwin, NativeSigAction);

        internal static Registration GetExceptionHandlers(bool isDarwin, SigActionInvoker invoke)
        {
            SigAction segfault = GetAction(SegfaultSignal, isDarwin, invoke);
            return new(segfault, isDarwin ? GetAction(DarwinBusSignal, true, invoke) : default, isDarwin);
        }

        private static unsafe SigAction GetAction(int signal, bool isDarwin, SigActionInvoker invoke)
        {
            DarwinSigAction darwin = default;
            LinuxSigAction linux = default;
            int result = invoke(signal, 0, isDarwin ? (nint)(&darwin) : (nint)(&linux));
            if (result != 0) throw Failure(signal == SegfaultSignal ? "get SIGSEGV" : "get SIGBUS", result);
            return isDarwin ? new(darwin) : new(linux);
        }

        public static Registration RegisterExceptionHandler(nint action) => RegisterExceptionHandler(action, IsDarwin, NativeSigAction);

        internal static Registration RegisterExceptionHandler(nint action, bool isDarwin, SigActionInvoker invoke)
        {
            // An all-zero mask is the empty set in both concrete libc ABIs above.
            SigAction replacement = isDarwin
                ? new(new DarwinSigAction { sa_handler = action, sa_flags = DarwinSigInfo })
                : new(new LinuxSigAction { sa_handler = action, sa_flags = LinuxSigInfo });

            int result = SetAction(SegfaultSignal, replacement, invoke, out SigAction segfault);
            if (result != 0) throw Failure("register SIGSEGV", result);

            if (isDarwin)
            {
                result = SetAction(DarwinBusSignal, replacement, invoke, out SigAction bus);
                if (result != 0)
                {
                    // Do not leave a half-installed pair if the second syscall fails.
                    int error = Marshal.GetLastPInvokeError();
                    int rollback = SetAction(SegfaultSignal, segfault, invoke, out _);
                    throw new InvalidOperationException($"Could not register SIGBUS sigaction. Result: {result}, errno: {error}, SIGSEGV rollback result: {rollback}.");
                }
                return new(segfault, bus, true);
            }

            return new(segfault, default, false);
        }

        public static bool RestoreExceptionHandler(Registration registration) => RestoreExceptionHandler(registration, NativeSigAction);

        internal static bool RestoreExceptionHandler(Registration registration, SigActionInvoker invoke)
        {
            bool segfault = SetAction(SegfaultSignal, registration.Segfault, invoke, out _) == 0;
            // Always attempt both restores, even if the first one failed. SIGBUS
            // has its own previous action and must also be restored on iOS.
            bool bus = !registration.HasBus || SetAction(DarwinBusSignal, registration.Bus, invoke, out _) == 0;
            return segfault && bus;
        }

        private static unsafe int SetAction(int signal, SigAction action, SigActionInvoker invoke, out SigAction oldAction)
        {
            int result;
            if (action.IsDarwin)
            {
                DarwinSigAction replacement = action.Darwin;
                DarwinSigAction previous = default;
                result = invoke(signal, (nint)(&replacement), (nint)(&previous));
                oldAction = new(previous);
            }
            else
            {
                LinuxSigAction replacement = action.Linux;
                LinuxSigAction previous = default;
                result = invoke(signal, (nint)(&replacement), (nint)(&previous));
                oldAction = new(previous);
            }
            return result;
        }

        private static InvalidOperationException Failure(string operation, int result) =>
            new($"Could not {operation} sigaction. Result: {result}, errno: {Marshal.GetLastPInvokeError()}.");
    }
}
