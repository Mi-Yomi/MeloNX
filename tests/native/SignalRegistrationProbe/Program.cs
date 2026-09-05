using System;
using System.IO;
using System.Runtime.InteropServices;
using Ryujinx.Cpu.Signal;

internal static partial class Program
{
    private static int Main(string[] args)
    {
        if (!OperatingSystem.IsMacOS() || args.Length != 1)
        {
            Console.Error.WriteLine("Run this isolated probe on macOS with an absolute native helper path.");
            return 2;
        }
        nint library = NativeLibrary.Load(Path.GetFullPath(args[0]));
        NativeLibrary.SetDllImportResolver(typeof(Program).Assembly, (name, _, _) => name == "SignalRegistrationProbe" ? library : 0);
        bool begun = false;
        try
        {
            Equal(Marshal.SizeOf<UnixSignalHandlerRegistration.DarwinSigAction>(), probe_layout(0), "native size");
            Equal(4, probe_layout(1), "native sigset size");
            Equal(Marshal.OffsetOf<UnixSignalHandlerRegistration.DarwinSigAction>("sa_mask").ToInt64(), probe_layout(2), "native mask offset");
            Equal(Marshal.OffsetOf<UnixSignalHandlerRegistration.DarwinSigAction>("sa_flags").ToInt64(), probe_layout(3), "native flags offset");
            Equal(UnixSignalHandlerRegistration.DarwinSigInfo, probe_layout(4), "native SA_SIGINFO");
            Equal(0, probe_begin(), "fixture installation");
            begun = true;
            var queried = UnixSignalHandlerRegistration.GetSegfaultExceptionHandler();
            Equal(0, queried.IsSigInfo ? 1 : 0, "saved one-argument handler");
            var beforeInstallation = UnixSignalHandlerRegistration.GetExceptionHandlers();
            Equal(1, beforeInstallation.HasBus ? 1 : 0, "preinstallation saved pair");
            Equal(queried.sa_handler, beforeInstallation.Segfault.sa_handler, "preinstallation segfault handler");
            Equal(0, beforeInstallation.Segfault.IsSigInfo ? 1 : 0, "preinstallation segfault ABI");
            Equal(1, beforeInstallation.Bus.IsSigInfo ? 1 : 0, "preinstallation bus ABI");
            Equal(0, probe_legacy_layout_flags(), "legacy wrong-layout negative control");

            var registration = UnixSignalHandlerRegistration.RegisterExceptionHandler(probe_replacement_handler());
            try
            {
                Equal(1, registration.HasBus ? 1 : 0, "saved signal pair");
                Equal(0, registration.Segfault.IsSigInfo ? 1 : 0, "saved segfault ABI");
                Equal(1, registration.Bus.IsSigInfo ? 1 : 0, "saved bus ABI");
                Equal(0x40, probe_current_flags(11), "installed SIGSEGV flags");
                Equal(0x40, probe_current_flags(10), "installed SIGBUS flags");
                Equal(0, probe_raise_replacement(), "three-argument native delivery for both signals");
            }
            finally
            {
                Equal(1, UnixSignalHandlerRegistration.RestoreExceptionHandler(registration) ? 1 : 0, "production restore");
            }
            Equal(0, probe_check_restored_and_raise(), "distinct restored masks, flags, handlers and delivery");
            Console.WriteLine("{\"schema_version\":1,\"status\":\"passed\",\"legacy_installed_flags\":0,\"candidate_installed_flags\":64,\"signals_verified\":[11,10],\"production_registration\":true,\"saved_actions_restored\":true}");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
        finally
        {
            if (begun && probe_end() != 0) Environment.FailFast("Could not restore process signal fixture.");
            NativeLibrary.Free(library);
        }
    }

    private static void Equal(long expected, long actual, string operation)
    {
        if (expected != actual) throw new InvalidOperationException($"{operation}: expected {expected}, actual {actual}");
    }

    [LibraryImport("SignalRegistrationProbe")] private static partial long probe_layout(int field);
    [LibraryImport("SignalRegistrationProbe")] private static partial nint probe_replacement_handler();
    [LibraryImport("SignalRegistrationProbe")] private static partial int probe_begin();
    [LibraryImport("SignalRegistrationProbe")] private static partial int probe_end();
    [LibraryImport("SignalRegistrationProbe")] private static partial int probe_current_flags(int signal);
    [LibraryImport("SignalRegistrationProbe")] private static partial int probe_legacy_layout_flags();
    [LibraryImport("SignalRegistrationProbe")] private static partial int probe_raise_replacement();
    [LibraryImport("SignalRegistrationProbe")] private static partial int probe_check_restored_and_raise();
}
