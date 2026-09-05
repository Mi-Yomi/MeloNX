using ARMeilleure.Signal;
using NUnit.Framework;
using Ryujinx.Cpu.Signal;
using Ryujinx.Memory;
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Ryujinx.Tests.Cpu
{
    // Execute the production generated handler directly. No process-wide signal
    // registrations, genuine invalid accesses, or iPhone are needed by these tests.
    public unsafe class NativeSignalHandlerTests
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void Handler(int signal, nint info, nint context);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void OneArgumentHandler(int signal);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate ulong Tracking(ulong offset, ulong size, int write);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate ulong TrackingWithPc(ulong offset, ulong size, int write, ulong pc);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate nint Reset(int signal, nint disposition);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int Raise(int signal);

        [Test]
        public void ConfigAndRangesMatchGeneratedNativeOffsets()
        {
            Assert.Multiple(() =>
            {
                Assert.That(Marshal.OffsetOf<SignalHandlerConfig>(nameof(SignalHandlerConfig.UnixOldSigaction)).ToInt32(), Is.EqualTo(8));
                Assert.That(Marshal.OffsetOf<SignalHandlerConfig>(nameof(SignalHandlerConfig.UnixOldSigaction3Arg)).ToInt32(), Is.EqualTo(16));
                Assert.That(Marshal.OffsetOf<SignalHandlerConfig>(nameof(SignalHandlerConfig.UnixOldBusAction)).ToInt32(), Is.EqualTo(24));
                Assert.That(Marshal.OffsetOf<SignalHandlerConfig>(nameof(SignalHandlerConfig.UnixOldBusAction3Arg)).ToInt32(), Is.EqualTo(32));
                Assert.That(Marshal.OffsetOf<SignalHandlerConfig>(nameof(SignalHandlerConfig.UnixSignal)).ToInt32(), Is.EqualTo(40));
                Assert.That(Marshal.OffsetOf<SignalHandlerConfig>(nameof(SignalHandlerConfig.UnixRaise)).ToInt32(), Is.EqualTo(48));
                Assert.That(Marshal.OffsetOf<SignalHandlerConfig>(nameof(SignalHandlerConfig.UnixExit)).ToInt32(), Is.EqualTo(56));
                Assert.That(Marshal.OffsetOf<SignalHandlerConfig>(nameof(SignalHandlerConfig.Ranges)).ToInt32(), Is.EqualTo(64));
                Assert.That(Unsafe.SizeOf<SignalHandlerRange>(), Is.EqualTo(40));
                Assert.That(Marshal.OffsetOf<SignalHandlerRange>(nameof(SignalHandlerRange.ActionPointer)).ToInt32(), Is.EqualTo(24));
            });
        }

        [Test]
        public void ContextAwareTrackingReceivesActualFaultPcWithoutDereferencingIt()
        {
            using var fixture = new Fixture();
            ulong observedPc = 0;
            TrackingWithPc action = (offset, size, write, pc) => { observedPc = pc; return 0; };
            fixture.Config.Ranges[0].ActionWithFaultAddress = 1;
            fixture.Config.Ranges[0].ActionPointer = Marshal.GetFunctionPointerForDelegate(action);
            fixture.Context.Write(272, 0xfedcba9876543210UL);
            fixture.Run(11);
            Assert.That(observedPc, Is.EqualTo(0xfedcba9876543210UL));
            Assert.That(fixture.SegfaultCalls, Is.EqualTo(1));
            GC.KeepAlive(action);
        }

        [TestCase(10)]
        [TestCase(11)]
        public void UnhandledFaultPreservesContextAndChainsItsOwnSignal(int signal)
        {
            using var fixture = new Fixture();
            fixture.TrackingResult = 0;
            byte[] before = fixture.Context.GetSpan(0, 512).ToArray();
            fixture.Run(signal);
            Assert.Multiple(() =>
            {
                Assert.That(fixture.Context.GetSpan(0, 512).ToArray(), Is.EqualTo(before));
                Assert.That(fixture.TrackingCalls, Is.EqualTo(1));
                Assert.That(fixture.TrackingOffset, Is.EqualTo(0x80));
                Assert.That(fixture.TrackingSize, Is.EqualTo(1));
                Assert.That(fixture.TrackingWrite, Is.EqualTo(1));
                Assert.That(fixture.SegfaultCalls, Is.EqualTo(signal == 11 ? 1 : 0));
                Assert.That(fixture.BusCalls, Is.EqualTo(signal == 10 ? 1 : 0));
                Assert.That(fixture.SeenSignal, Is.EqualTo(signal));
            });
        }

        [TestCase(0xf9400c62U, 3)] // LDR x2,[x3,#24]: retain effective-address offset.
        [TestCase(0xd50b7b29U, 9)] // DC CVAU,x9: address is Rt, not Rn.
        public void HandledFaultRelocatesOnlyTheInstructionAddressRegister(uint instruction, int register)
        {
            using var fixture = new Fixture();
            fixture.Instruction.Write(0, instruction);
            fixture.Context.Write((ulong)(16 + register * 8), 0x1068UL);
            fixture.TrackingResult = 0x9080;
            byte[] expected = fixture.Context.GetSpan(0, 512).ToArray();
            BitConverter.GetBytes(0x9068UL).CopyTo(expected, 16 + register * 8);
            fixture.Run(10);
            Assert.Multiple(() =>
            {
                Assert.That(fixture.Context.GetSpan(0, 512).ToArray(), Is.EqualTo(expected));
                Assert.That(fixture.BusCalls + fixture.SegfaultCalls, Is.Zero);
            });
        }

        [Test]
        public void SameAddressHandledFaultDoesNotInspectInstructionOrForward()
        {
            using var fixture = new Fixture();
            fixture.Context.Write(272, 0UL); // Any accidental instruction read would fault.
            fixture.TrackingResult = 0x1080;
            fixture.Run(11);
            Assert.That(fixture.BusCalls + fixture.SegfaultCalls, Is.Zero);
        }

        [Test]
        public void MissingTrackingActionForwardsWithoutChangingContext()
        {
            using var fixture = new Fixture();
            fixture.Config.Ranges[0].ActionPointer = 0;
            fixture.Run(10);
            Assert.Multiple(() =>
            {
                Assert.That(fixture.TrackingCalls, Is.Zero);
                Assert.That(fixture.BusCalls, Is.EqualTo(1));
                Assert.That(fixture.Context.Read<ulong>(40), Is.EqualTo(0x1068UL));
            });
        }

        [TestCase(0L, false)]
        [TestCase(1L, false)]
        [TestCase(-1L, true)]
        public void SpecialDispositionsResetAndRaiseWithoutCallingInvalidPointer(long previous, bool fail)
        {
            using var fixture = new Fixture();
            fixture.Config.Ranges[0].IsActive = 0;
            fixture.Config.UnixOldBusAction = unchecked((nuint)previous);
            fixture.ResetFails = fail;
            fixture.Run(10);
            Assert.Multiple(() =>
            {
                Assert.That(fixture.ResetCalls, Is.EqualTo(1));
                Assert.That(fixture.RaiseCalls, Is.EqualTo(1));
                Assert.That(fixture.ExitCode, Is.EqualTo(fail ? 138 : 0));
                Assert.That(fixture.BusCalls + fixture.SegfaultCalls, Is.Zero);
            });
        }

        private sealed class Fixture : IDisposable
        {
            private readonly MemoryBlock _config = new(4096);
            private readonly MemoryBlock _info = new(4096);
            private readonly MemoryBlock _ucontext = new(4096);
            public readonly MemoryBlock Context = new(4096);
            public readonly MemoryBlock Instruction = new(4096);
            private readonly MemoryBlock _code;
            private readonly Handler _handler, _segfault;
            private readonly OneArgumentHandler _bus, _exit;
            private readonly Tracking _tracking;
            private readonly Reset _reset;
            private readonly Raise _raise;
            public ulong TrackingResult, TrackingOffset, TrackingSize;
            public int TrackingWrite, TrackingCalls, SegfaultCalls, BusCalls, SeenSignal, ResetCalls, RaiseCalls, ExitCode;
            public bool ResetFails;
            public ref SignalHandlerConfig Config => ref Unsafe.AsRef<SignalHandlerConfig>((void*)_config.Pointer);

            public Fixture()
            {
                _segfault = (signal, info, context) => { SegfaultCalls++; SeenSignal = signal; };
                _bus = signal => { BusCalls++; SeenSignal = signal; };
                _tracking = (offset, size, write) => { TrackingCalls++; TrackingOffset = offset; TrackingSize = size; TrackingWrite = write; return TrackingResult; };
                _reset = (signal, disposition) => { ResetCalls++; return ResetFails ? -1 : 0; };
                _raise = signal => { RaiseCalls++; return 0; };
                _exit = code => ExitCode = code;
                Config = default;
                Config.UnixOldSigaction = (nuint)Marshal.GetFunctionPointerForDelegate(_segfault);
                Config.UnixOldSigaction3Arg = 1;
                Config.UnixOldBusAction = (nuint)Marshal.GetFunctionPointerForDelegate(_bus);
                Config.UnixOldBusAction3Arg = 0;
                // A 64-bit load of the int flag would read this poison padding.
                _config.Write(36, uint.MaxValue);
                Config.UnixSignal = (nuint)Marshal.GetFunctionPointerForDelegate(_reset);
                Config.UnixRaise = (nuint)Marshal.GetFunctionPointerForDelegate(_raise);
                Config.UnixExit = (nuint)Marshal.GetFunctionPointerForDelegate(_exit);
                Config.Ranges[0] = new() { IsActive = 1, RangeAddress = 0x1000, RangeEndAddress = 0x2000, ActionPointer = Marshal.GetFunctionPointerForDelegate(_tracking) };
                _info.Write(24, 0x1080UL);
                _ucontext.Write(48, (ulong)Context.Pointer);
                Context.GetSpan(0, 512).Fill(0xa5);
                Context.Write(8, 0x40U);
                Context.Write(40, 0x1068UL);
                Context.Write(272, (ulong)Instruction.Pointer);
                Instruction.Write(0, 0xf9400c62U);
                byte[] code = NativeSignalHandlerGenerator.GenerateUnixSignalHandler(_config.Pointer, Unsafe.SizeOf<SignalHandlerRange>(), true, Architecture.Arm64);
                ulong pageSize = MemoryBlock.GetPageSize();
                ulong allocation = ((ulong)code.Length + pageSize - 1) & ~(pageSize - 1);
                _code = new MemoryBlock(allocation);
                _code.Write(0, code);
                _code.Reprotect(0, allocation, MemoryPermission.ReadAndExecute);
                _handler = Marshal.GetDelegateForFunctionPointer<Handler>(_code.Pointer);
            }

            public void Run(int signal) => _handler(signal, _info.Pointer, _ucontext.Pointer);
            public void Dispose()
            {
                GC.KeepAlive(_tracking); GC.KeepAlive(_segfault); GC.KeepAlive(_bus);
                GC.KeepAlive(_reset); GC.KeepAlive(_raise); GC.KeepAlive(_exit);
                _code.Dispose(); Instruction.Dispose(); Context.Dispose();
                _ucontext.Dispose(); _info.Dispose(); _config.Dispose();
            }
        }
    }
}
