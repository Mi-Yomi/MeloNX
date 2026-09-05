using ARMeilleure.Signal;
using Ryujinx.Common;
using Ryujinx.Memory;
using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Ryujinx.Cpu.Signal
{
    [StructLayout(LayoutKind.Sequential)]
    struct SignalHandlerRange
    {
        public int IsActive;
        public nuint RangeAddress;
        public nuint RangeEndAddress;
        public nint ActionPointer;
        public int ActionWithFaultAddress;
    }

    [InlineArray(NativeSignalHandlerGenerator.MaxTrackedRanges)]
    struct SignalHandlerRangeArray
    {
        public SignalHandlerRange Range0;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SignalHandlerConfig
    {
        /// <summary>
        /// The byte offset of the faulting address in the SigInfo or ExceptionRecord struct.
        /// </summary>
        public int StructAddressOffset;

        /// <summary>
        /// The byte offset of the write flag in the SigInfo or ExceptionRecord struct.
        /// </summary>
        public int StructWriteOffset;

        /// <summary>
        /// The sigaction handler that was registered before this one. (unix only)
        /// </summary>
        public nuint UnixOldSigaction;

        /// <summary>
        /// The type of the previous sigaction. True for the 3 argument variant. (unix only)
        /// </summary>
        public int UnixOldSigaction3Arg;

        public nuint UnixOldBusAction;
        public int UnixOldBusAction3Arg;

        // Native libc entry points used only when the previous disposition is
        // SIG_DFL/SIG_IGN. These must never be invoked as function pointers.
        public nuint UnixSignal;
        public nuint UnixRaise;
        public nuint UnixExit;

        /// <summary>
        /// Fixed size array of tracked ranges.
        /// </summary>
        public SignalHandlerRangeArray Ranges;
    }

    static class NativeSignalHandler
    {
        private static readonly nint _handlerConfig;
        private static nint _signalHandlerPtr;

        private static MemoryBlock _codeBlock;

        private static readonly Lock _lock = new();
        private static bool _initialized;

        static NativeSignalHandler()
        {
            _handlerConfig = Marshal.AllocHGlobal(Unsafe.SizeOf<SignalHandlerConfig>());
            ref SignalHandlerConfig config = ref GetConfigRef();

            config = new SignalHandlerConfig();
        }

        public static void InitializeSignalHandler(Func<nint, nint, nint> customSignalHandlerFactory = null)
        {
            if (_initialized)
            {
                return;
            }

            lock (_lock)
            {
                if (_initialized)
                {
                    return;
                }

                int rangeStructSize = Unsafe.SizeOf<SignalHandlerRange>();

                ref SignalHandlerConfig config = ref GetConfigRef();

                if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsIOS())
                {
                    // Populate chaining state before installing either signal.
                    // A fault on another thread can arrive as soon as sigaction returns.
                    SetPreviousHandlers(ref config, UnixSignalHandlerRegistration.GetExceptionHandlers());
                    nint libc = NativeLibrary.Load(OperatingSystem.IsLinux() ? "libc.so.6" : "/usr/lib/libSystem.B.dylib");
                    config.UnixSignal = (nuint)NativeLibrary.GetExport(libc, "signal");
                    config.UnixRaise = (nuint)NativeLibrary.GetExport(libc, "raise");
                    config.UnixExit = (nuint)NativeLibrary.GetExport(libc, "_exit");
                    _signalHandlerPtr = MapCode(NativeSignalHandlerGenerator.GenerateUnixSignalHandler(_handlerConfig, rangeStructSize));

                    if (customSignalHandlerFactory != null)
                    {
                        _signalHandlerPtr = customSignalHandlerFactory(UnixSignalHandlerRegistration.GetSegfaultExceptionHandler().sa_handler, _signalHandlerPtr);
                    }

                    SetPreviousHandlers(ref config, UnixSignalHandlerRegistration.RegisterExceptionHandler(_signalHandlerPtr));
                }
                else
                {
                    config.StructAddressOffset = 40; // ExceptionInformation1
                    config.StructWriteOffset = 32; // ExceptionInformation0

                    _signalHandlerPtr = MapCode(NativeSignalHandlerGenerator.GenerateWindowsSignalHandler(_handlerConfig, rangeStructSize));

                    if (customSignalHandlerFactory != null)
                    {
                        _signalHandlerPtr = customSignalHandlerFactory(nint.Zero, _signalHandlerPtr);
                    }

                    WindowsSignalHandlerRegistration.RegisterExceptionHandler(_signalHandlerPtr);
                }

                _initialized = true;
            }
        }

        private static void SetPreviousHandlers(ref SignalHandlerConfig config, UnixSignalHandlerRegistration.Registration previous)
        {
            config.UnixOldSigaction = (nuint)previous.Segfault.sa_handler;
            config.UnixOldSigaction3Arg = previous.Segfault.IsSigInfo ? 1 : 0;
            config.UnixOldBusAction = (nuint)previous.Bus.sa_handler;
            config.UnixOldBusAction3Arg = previous.Bus.IsSigInfo ? 1 : 0;
        }

        private static nint MapCode(ReadOnlySpan<byte> code)
        {
            Debug.Assert(_codeBlock == null);

            ulong codeSizeAligned = BitUtils.AlignUp((ulong)code.Length, MemoryBlock.GetPageSize());

            _codeBlock = new MemoryBlock(codeSizeAligned, MemoryBlock.DualMappedEnabled() ? MemoryAllocationFlags.DualMapping : MemoryAllocationFlags.None);

            Console.WriteLine($"Code length: {code.Length}, aligned: {codeSizeAligned}, memoryBlockSize: {_codeBlock.Size}");

            _codeBlock.Write(0, code);
            _codeBlock.Reprotect(0, codeSizeAligned, MemoryPermission.ReadAndExecute);

            _codeBlock.Detach();

            return _codeBlock.RxPointer;
        }

        private static unsafe ref SignalHandlerConfig GetConfigRef()
        {
            return ref Unsafe.AsRef<SignalHandlerConfig>((void*)_handlerConfig);
        }

        public static bool AddTrackedRegion(nuint address, nuint endAddress, nint action, bool actionWithFaultAddress = false)
        {
            Span<SignalHandlerRange> ranges = GetConfigRef().Ranges;

            for (int i = 0; i < NativeSignalHandlerGenerator.MaxTrackedRanges; i++)
            {
                if (ranges[i].IsActive == 0)
                {
                    ranges[i].RangeAddress = address;
                    ranges[i].RangeEndAddress = endAddress;
                    ranges[i].ActionPointer = action;
                    ranges[i].ActionWithFaultAddress = actionWithFaultAddress ? 1 : 0;
                    ranges[i].IsActive = 1;

                    return true;
                }
            }

            return false;
        }

        public static bool RemoveTrackedRegion(nuint address)
        {
            Span<SignalHandlerRange> ranges = GetConfigRef().Ranges;

            for (int i = 0; i < NativeSignalHandlerGenerator.MaxTrackedRanges; i++)
            {
                if (ranges[i].IsActive == 1 && ranges[i].RangeAddress == address)
                {
                    ranges[i].IsActive = 0;

                    return true;
                }
            }

            return false;
        }

        public static bool SupportsFaultAddressPatching()
        {
            return NativeSignalHandlerGenerator.SupportsFaultAddressPatchingForHost();
        }
    }
}
