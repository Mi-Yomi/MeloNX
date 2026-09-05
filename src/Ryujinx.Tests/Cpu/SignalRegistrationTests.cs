using NUnit.Framework;
using Ryujinx.Cpu.Signal;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using static Ryujinx.Cpu.Signal.UnixSignalHandlerRegistration;

namespace Ryujinx.Tests.Cpu
{
    public class SignalRegistrationTests
    {
        [Test]
        public void DarwinLibcLayoutUsesFourByteMaskAndNativeFlagsOffset()
        {
            Assert.Multiple(() =>
            {
                Assert.That(Marshal.SizeOf<DarwinSigAction>(), Is.EqualTo(16));
                Assert.That(Marshal.OffsetOf<DarwinSigAction>(nameof(DarwinSigAction.sa_mask)).ToInt32(), Is.EqualTo(8));
                Assert.That(Marshal.OffsetOf<DarwinSigAction>(nameof(DarwinSigAction.sa_flags)).ToInt32(), Is.EqualTo(12));
                Assert.That(DarwinSigInfo, Is.EqualTo(0x40));
            });
        }

        [Test]
        public void LinuxLibcRestorerKeepsNativeAlignment()
        {
            Assert.Multiple(() =>
            {
                Assert.That(Marshal.SizeOf<LinuxSigSet>(), Is.EqualTo(128));
                Assert.That(Marshal.SizeOf<LinuxSigAction>(), Is.EqualTo(152));
                Assert.That(Marshal.OffsetOf<LinuxSigAction>(nameof(LinuxSigAction.sa_flags)).ToInt32(), Is.EqualTo(136));
                Assert.That(Marshal.OffsetOf<LinuxSigAction>(nameof(LinuxSigAction.sa_restorer)).ToInt32(), Is.EqualTo(144));
            });
        }

        [TestCase(true)]
        [TestCase(false)]
        public void QueryPreservesActionAndUsesCorrectSigInfoBit(bool darwin)
        {
            var native = new FakeNative(darwin);
            SigAction queried = GetSegfaultExceptionHandler(darwin, native.Invoke);
            Assert.Multiple(() =>
            {
                Assert.That(queried.sa_handler, Is.EqualTo((nint)0x1234));
                Assert.That(queried.IsSigInfo, Is.True);
                Assert.That(native.Signals, Is.EqualTo(new[] { SegfaultSignal }));
                Assert.That(native.Writes, Is.Zero);
            });
        }

        [TestCase(true)]
        [TestCase(false)]
        public void QueryAllHandlersPrecedesAnyInstallation(bool darwin)
        {
            var native = new FakeNative(darwin);
            Registration snapshot = GetExceptionHandlers(darwin, native.Invoke);
            Assert.Multiple(() =>
            {
                Assert.That(snapshot.Segfault.sa_handler, Is.EqualTo((nint)0x1234));
                Assert.That(snapshot.Segfault.IsSigInfo, Is.True);
                Assert.That(snapshot.HasBus, Is.EqualTo(darwin));
                Assert.That(snapshot.Bus.sa_handler, Is.EqualTo(darwin ? (nint)0x5678 : 0));
                Assert.That(snapshot.Bus.IsSigInfo, Is.False);
                Assert.That(native.Signals, Is.EqualTo(darwin ? new[] { 11, 10 } : new[] { 11 }));
                Assert.That(native.Writes, Is.Zero);
            });
        }

        [TestCase(1)]
        [TestCase(2)]
        public void FailedPreinstallationQueryNeverChangesAnAction(int failedCall)
        {
            var native = new FakeNative(true) { FailCall = failedCall };
            DarwinSigAction segfault = native.Darwin[SegfaultSignal];
            DarwinSigAction bus = native.Darwin[DarwinBusSignal];
            Assert.That(() => GetExceptionHandlers(true, native.Invoke), Throws.InvalidOperationException);
            Assert.Multiple(() =>
            {
                Assert.That(native.Writes, Is.Zero);
                Assert.That(native.Signals.Count, Is.EqualTo(failedCall));
                Assert.That(native.Darwin[SegfaultSignal], Is.EqualTo(segfault));
                Assert.That(native.Darwin[DarwinBusSignal], Is.EqualTo(bus));
            });
        }

        [Test]
        public void RegistrationStillReturnsActualPreviousActionsAfterEarlierSnapshot()
        {
            var native = new FakeNative(true);
            Registration snapshot = GetExceptionHandlers(true, native.Invoke);
            DarwinSigAction changedBus = new() { sa_handler = (nint)0x9999, sa_mask = 0x5000, sa_flags = 0x42 };
            native.Darwin[DarwinBusSignal] = changedBus;
            Registration actual = RegisterExceptionHandler((nint)0x9876, true, native.Invoke);
            Assert.Multiple(() =>
            {
                Assert.That(snapshot.Bus.sa_handler, Is.EqualTo((nint)0x5678));
                Assert.That(actual.Bus.sa_handler, Is.EqualTo(changedBus.sa_handler));
                Assert.That(actual.Bus.IsSigInfo, Is.True);
            });
            Assert.That(RestoreExceptionHandler(actual, native.Invoke), Is.True);
            Assert.That(native.Darwin[DarwinBusSignal], Is.EqualTo(changedBus));
        }

        [Test]
        public void DarwinRegistersTwoThreeArgumentHandlersAndRestoresDistinctActions()
        {
            var native = new FakeNative(true);
            DarwinSigAction initialSegfault = native.Darwin[SegfaultSignal];
            DarwinSigAction initialBus = native.Darwin[DarwinBusSignal];
            Registration registration = RegisterExceptionHandler((nint)0x9876, true, native.Invoke);
            Assert.Multiple(() =>
            {
                Assert.That(registration.HasBus, Is.True);
                Assert.That(registration.Segfault.sa_handler, Is.EqualTo(initialSegfault.sa_handler));
                Assert.That(registration.Bus.sa_handler, Is.EqualTo(initialBus.sa_handler));
                Assert.That(registration.Segfault.IsSigInfo, Is.True);
                Assert.That(registration.Bus.IsSigInfo, Is.False);
                Assert.That(native.Darwin[SegfaultSignal].sa_flags, Is.EqualTo(0x40));
                Assert.That(native.Darwin[DarwinBusSignal].sa_flags, Is.EqualTo(0x40));
                Assert.That(native.Darwin[SegfaultSignal].sa_mask, Is.Zero);
                Assert.That(native.Darwin[DarwinBusSignal].sa_mask, Is.Zero);
            });
            Assert.That(RestoreExceptionHandler(registration, native.Invoke), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(native.Darwin[SegfaultSignal], Is.EqualTo(initialSegfault));
                Assert.That(native.Darwin[DarwinBusSignal], Is.EqualTo(initialBus));
                Assert.That(native.Signals, Is.EqualTo(new[] { 11, 10, 11, 10 }));
            });
        }

        [Test]
        public void LinuxRegistersOnlySegfaultAndRestoresMaskFlagsAndRestorer()
        {
            var native = new FakeNative(false);
            LinuxSigAction initial = native.Linux;
            Registration registration = RegisterExceptionHandler((nint)0x9876, false, native.Invoke);
            Assert.Multiple(() =>
            {
                Assert.That(registration.HasBus, Is.False);
                Assert.That(native.Linux.sa_flags, Is.EqualTo(4));
                Assert.That(native.Linux.sa_restorer, Is.EqualTo(nint.Zero));
                Assert.That(native.Signals, Is.EqualTo(new[] { 11 }));
            });
            Assert.That(RestoreExceptionHandler(registration, native.Invoke), Is.True);
            Assert.That(Bytes(native.Linux), Is.EqualTo(Bytes(initial)));
        }

        [Test]
        public void BusRegistrationFailureRestoresPreviousSegfault()
        {
            var native = new FakeNative(true) { FailCall = 2 };
            DarwinSigAction initial = native.Darwin[SegfaultSignal];
            Assert.That(() => RegisterExceptionHandler((nint)0x9876, true, native.Invoke), Throws.InvalidOperationException);
            Assert.Multiple(() =>
            {
                Assert.That(native.Darwin[SegfaultSignal], Is.EqualTo(initial));
                Assert.That(native.Signals, Is.EqualTo(new[] { 11, 10, 11 }));
            });
        }

        [Test]
        public void FirstRegistrationFailureDoesNotReplaceBus()
        {
            var native = new FakeNative(true) { FailCall = 1 };
            Assert.That(() => RegisterExceptionHandler((nint)0x9876, true, native.Invoke), Throws.InvalidOperationException);
            Assert.That(native.Signals, Is.EqualTo(new[] { 11 }));
        }

        [Test]
        public void RestoreAttemptsBusEvenWhenSegfaultRestoreFails()
        {
            var native = new FakeNative(true);
            DarwinSigAction initialBus = native.Darwin[DarwinBusSignal];
            Registration registration = RegisterExceptionHandler((nint)0x9876, true, native.Invoke);
            native.FailCall = 3;
            Assert.That(RestoreExceptionHandler(registration, native.Invoke), Is.False);
            Assert.Multiple(() =>
            {
                Assert.That(native.Darwin[DarwinBusSignal], Is.EqualTo(initialBus));
                Assert.That(native.Signals, Is.EqualTo(new[] { 11, 10, 11, 10 }));
            });
        }

        [Test]
        public void SigInfoCheckDoesNotConfuseDarwinResetHandWithLinuxSigInfo()
        {
            Assert.That(new SigAction(new DarwinSigAction { sa_flags = 4 }).IsSigInfo, Is.False);
            Assert.That(new SigAction(new LinuxSigAction { sa_flags = 4 }).IsSigInfo, Is.True);
        }

        private static byte[] Bytes<T>(T value) where T : unmanaged => MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref value, 1)).ToArray();

        private sealed class FakeNative
        {
            private readonly bool _darwin;
            public readonly Dictionary<int, DarwinSigAction> Darwin = new();
            public LinuxSigAction Linux;
            public readonly List<int> Signals = new();
            public int FailCall;
            public int Writes;

            public unsafe FakeNative(bool darwin)
            {
                _darwin = darwin;
                Darwin[SegfaultSignal] = new() { sa_handler = (nint)0x1234, sa_mask = 0x800, sa_flags = 0x42 };
                Darwin[DarwinBusSignal] = new() { sa_handler = (nint)0x5678, sa_mask = 0x1000, sa_flags = 0x04 };
                Linux = new() { sa_handler = (nint)0x1234, sa_flags = 0x10000004, sa_restorer = (nint)0x4321 };
                fixed (ulong* mask = Linux.sa_mask.Values)
                {
                    mask[0] = 0x800;
                    mask[15] = 0xfeed;
                }
            }

            public unsafe int Invoke(int signal, nint action, nint oldAction)
            {
                Signals.Add(signal);
                if (Signals.Count == FailCall) return -1;
                if (_darwin)
                {
                    *(DarwinSigAction*)oldAction = Darwin[signal];
                    if (action != 0) Darwin[signal] = *(DarwinSigAction*)action;
                }
                else
                {
                    Assert.That(signal, Is.EqualTo(SegfaultSignal));
                    *(LinuxSigAction*)oldAction = Linux;
                    if (action != 0) Linux = *(LinuxSigAction*)action;
                }
                if (action != 0) Writes++;
                return 0;
            }
        }
    }
}
