using ARMeilleure.IntermediateRepresentation;
using ARMeilleure.Translation;
using System;
using System.Runtime.InteropServices;
using static ARMeilleure.IntermediateRepresentation.Operand.Factory;

namespace ARMeilleure.Signal
{
    public static class NativeSignalHandlerGenerator
    {
        public const int MaxTrackedRanges = 16;

        private const int StructAddressOffset = 0;
        private const int StructWriteOffset = 4;
        private const int UnixOldSigaction = 8;
        private const int UnixOldSigaction3Arg = 16;
        private const int UnixOldBusAction = 24;
        private const int UnixOldBusAction3Arg = 32;
        private const int UnixSignal = 40;
        private const int UnixRaise = 48;
        private const int UnixExit = 56;
        private const int RangeOffset = 64;

        private const int EXCEPTION_CONTINUE_SEARCH = 0;
        private const int EXCEPTION_CONTINUE_EXECUTION = -1;

        private const uint EXCEPTION_ACCESS_VIOLATION = 0xc0000005;

        private static Operand EmitGenericRegionCheck(EmitterContext context, nint signalStructPtr, Operand faultAddress, Operand faultPc, Operand isWrite, int rangeStructSize, bool unix, bool darwin, Architecture contextArchitecture)
        {
            Operand inRegionLocal = context.AllocateLocal(OperandType.I32);
            context.Copy(inRegionLocal, Const(0));

            Operand endLabel = Label();

            for (int i = 0; i < MaxTrackedRanges; i++)
            {
                ulong rangeBaseOffset = (ulong)(RangeOffset + i * rangeStructSize);

                Operand nextLabel = Label();

                Operand isActive = context.Load(OperandType.I32, Const((ulong)signalStructPtr + rangeBaseOffset));

                context.BranchIfFalse(nextLabel, isActive);

                Operand rangeAddress = context.Load(OperandType.I64, Const((ulong)signalStructPtr + rangeBaseOffset + 8));
                Operand rangeEndAddress = context.Load(OperandType.I64, Const((ulong)signalStructPtr + rangeBaseOffset + 16));

                // Is the fault address within this tracked region?
                Operand inRange = context.BitwiseAnd(
                    context.ICompare(faultAddress, rangeAddress, Comparison.GreaterOrEqualUI),
                    context.ICompare(faultAddress, rangeEndAddress, Comparison.LessUI));

                // Only call tracking if in range.
                context.BranchIfFalse(nextLabel, inRange, BasicBlockFrequency.Cold);

                Operand offset = context.Subtract(faultAddress, rangeAddress);

                // Call the tracking action, with the pointer's relative offset to the base address.
                Operand trackingActionPtr = context.Load(OperandType.I64, Const((ulong)signalStructPtr + rangeBaseOffset + 24));

                context.Copy(inRegionLocal, Const(0));

                Operand skipActionLabel = Label();

                // Tracking action should be non-null to call it, otherwise assume false return.
                context.BranchIfFalse(skipActionLabel, trackingActionPtr);
                Operand result = context.AllocateLocal(OperandType.I64);
                Operand threeArgumentAction = Label();
                Operand actionComplete = Label();
                Operand withFaultAddress = context.Load(OperandType.I32, Const((ulong)signalStructPtr + rangeBaseOffset + 32));
                context.BranchIfFalse(threeArgumentAction, withFaultAddress);
                context.Copy(result, context.Call(trackingActionPtr, OperandType.I64, offset, Const(1UL), isWrite, faultPc));
                context.Branch(actionComplete);
                context.MarkLabel(threeArgumentAction);
                context.Copy(result, context.Call(trackingActionPtr, OperandType.I64, offset, Const(1UL), isWrite));
                context.MarkLabel(actionComplete);
                context.Copy(inRegionLocal, context.ICompareNotEqual(result, Const(0UL)));

                // Zero means unhandled: forwarding must see the ORIGINAL machine
                // context, never an address register relocated towards address zero.
                context.BranchIfFalse(skipActionLabel, inRegionLocal);
                if (unix)
                {
                    GenerateFaultAddressPatchCode(context, faultAddress, result, darwin, contextArchitecture);
                }

                context.MarkLabel(skipActionLabel);

                // If the tracking action returns false or does not exist, it might be an invalid access due to a partial overlap on Windows.
                if (!unix)
                {
                    context.BranchIfTrue(endLabel, inRegionLocal);

                    context.Copy(inRegionLocal, WindowsPartialUnmapHandler.EmitRetryFromAccessViolation(context));
                }

                context.Branch(endLabel);

                context.MarkLabel(nextLabel);
            }

            context.MarkLabel(endLabel);

            return context.Copy(inRegionLocal);
        }

        private static Operand GenerateUnixFaultAddress(EmitterContext context, Operand sigInfoPtr, bool darwin)
        {
            ulong structAddressOffset = darwin ? 24ul : 16ul; // si_addr
            return context.Load(OperandType.I64, context.Add(sigInfoPtr, Const(structAddressOffset)));
        }

        private static Operand GenerateUnixFaultPc(EmitterContext context, Operand ucontextPtr, bool darwin, Architecture architecture)
        {
            if (darwin)
            {
                Operand machine = context.Load(OperandType.I64, context.Add(ucontextPtr, Const(48UL)));
                return context.Load(OperandType.I64, context.Add(machine, Const(architecture == Architecture.Arm64 ? 272UL : 144UL)));
            }
            return context.Load(OperandType.I64, context.Add(ucontextPtr, Const(architecture == Architecture.Arm64 ? 440UL : 168UL)));
        }

        private static Operand GenerateUnixWriteFlag(EmitterContext context, Operand ucontextPtr, bool darwin, Architecture contextArchitecture)
        {
            if (darwin)
            {
                const ulong McontextOffset = 48; // uc_mcontext
                Operand ctxPtr = context.Load(OperandType.I64, context.Add(ucontextPtr, Const(McontextOffset)));

                if (contextArchitecture == Architecture.Arm64)
                {
                    const ulong EsrOffset = 8; // __es.__esr
                    Operand esr = context.Load(OperandType.I32, context.Add(ctxPtr, Const(EsrOffset)));
                    return context.ZeroExtend32(OperandType.I64, context.BitwiseAnd(esr, Const(0x40)));
                }
                else if (contextArchitecture == Architecture.X64)
                {
                    const ulong ErrOffset = 4; // __es.__err
                    Operand err = context.Load(OperandType.I64, context.Add(ctxPtr, Const(ErrOffset)));
                    return context.BitwiseAnd(err, Const(2ul));
                }
            }
            else
            {
                if (contextArchitecture == Architecture.Arm64)
                {
                    Operand auxPtr = context.AllocateLocal(OperandType.I64);

                    Operand loopLabel = Label();
                    Operand successLabel = Label();

                    const ulong AuxOffset = 464; // uc_mcontext.__reserved
                    const uint EsrMagic = 0x45535201;

                    context.Copy(auxPtr, context.Add(ucontextPtr, Const(AuxOffset)));

                    context.MarkLabel(loopLabel);

                    // _aarch64_ctx::magic
                    Operand magic = context.Load(OperandType.I32, auxPtr);
                    // _aarch64_ctx::size
                    Operand size = context.Load(OperandType.I32, context.Add(auxPtr, Const(4ul)));

                    context.BranchIf(successLabel, magic, Const(EsrMagic), Comparison.Equal);

                    context.Copy(auxPtr, context.Add(auxPtr, context.ZeroExtend32(OperandType.I64, size)));

                    context.Branch(loopLabel);

                    context.MarkLabel(successLabel);

                    // esr_context::esr
                    Operand esr = context.Load(OperandType.I64, context.Add(auxPtr, Const(8ul)));
                    return context.BitwiseAnd(esr, Const(0x40ul));
                }
                else if (contextArchitecture == Architecture.X64)
                {
                    const int ErrOffset = 192; // uc_mcontext.gregs[REG_ERR]
                    Operand err = context.Load(OperandType.I64, context.Add(ucontextPtr, Const(ErrOffset)));
                    return context.BitwiseAnd(err, Const(2ul));
                }
            }

            throw new PlatformNotSupportedException();
        }

        public static byte[] GenerateUnixSignalHandler(nint signalStructPtr, int rangeStructSize)
            => GenerateUnixSignalHandler(signalStructPtr, rangeStructSize,
                OperatingSystem.IsMacOS() || OperatingSystem.IsIOS(), RuntimeInformation.ProcessArchitecture);

        // The signal context ABI and the generated code architecture are separate:
        // this also lets the complete handler run against synthetic Darwin contexts
        // in platform-independent regression tests, without installing OS handlers.
        public static byte[] GenerateUnixSignalHandler(nint signalStructPtr, int rangeStructSize, bool darwin, Architecture contextArchitecture)
        {
            EmitterContext context = new();

            // (int sig, SigInfo* sigInfo, void* ucontext)
            Operand sigInfoPtr = context.LoadArgument(OperandType.I64, 1);
            Operand ucontextPtr = context.LoadArgument(OperandType.I64, 2);

            Operand faultAddress = GenerateUnixFaultAddress(context, sigInfoPtr, darwin);
            Operand writeFlag = GenerateUnixWriteFlag(context, ucontextPtr, darwin, contextArchitecture);
            Operand faultPc = GenerateUnixFaultPc(context, ucontextPtr, darwin, contextArchitecture);

            Operand isWrite = context.ICompareNotEqual(writeFlag, Const(0L)); // Normalize to 0/1.

            Operand isInRegion = EmitGenericRegionCheck(context, signalStructPtr, faultAddress, faultPc, isWrite, rangeStructSize, true, darwin, contextArchitecture);

            Operand endLabel = Label();

            context.BranchIfTrue(endLabel, isInRegion);

            Operand signal = context.LoadArgument(OperandType.I32, 0);
            Operand unixOldSigaction = context.AllocateLocal(OperandType.I64);
            Operand unixOldSigaction3Arg = context.AllocateLocal(OperandType.I32);
            context.Copy(unixOldSigaction, context.Load(OperandType.I64, Const((ulong)signalStructPtr + UnixOldSigaction)));
            context.Copy(unixOldSigaction3Arg, context.Load(OperandType.I32, Const((ulong)signalStructPtr + UnixOldSigaction3Arg)));
            if (darwin)
            {
                Operand selectedLabel = Label();
                context.BranchIf(selectedLabel, signal, Const(10), Comparison.NotEqual); // Darwin SIGBUS
                context.Copy(unixOldSigaction, context.Load(OperandType.I64, Const((ulong)signalStructPtr + UnixOldBusAction)));
                context.Copy(unixOldSigaction3Arg, context.Load(OperandType.I32, Const((ulong)signalStructPtr + UnixOldBusAction3Arg)));
                context.MarkLabel(selectedLabel);
            }

            Operand callableLabel = Label();
            Operand specialDisposition = context.BitwiseOr(
                context.ICompare(unixOldSigaction, Const(1UL), Comparison.LessOrEqualUI),
                context.ICompareEqual(unixOldSigaction, Const(ulong.MaxValue)));
            context.BranchIfFalse(callableLabel, specialDisposition);

            // Synchronous memory faults cannot safely resume with SIG_IGN. Reset
            // to default and re-raise; the blocked signal is delivered on return.
            Operand reset = context.Call(context.Load(OperandType.I64, Const((ulong)signalStructPtr + UnixSignal)),
                OperandType.I64, signal, Const(0UL));
            Operand raised = context.Call(context.Load(OperandType.I64, Const((ulong)signalStructPtr + UnixRaise)),
                OperandType.I32, signal);
            Operand failed = context.BitwiseOr(context.ICompareEqual(reset, Const(ulong.MaxValue)), context.ICompareNotEqual(raised, Const(0)));
            context.BranchIfFalse(endLabel, failed);
            context.Call(context.Load(OperandType.I64, Const((ulong)signalStructPtr + UnixExit)), OperandType.None, context.Add(Const(128), signal));
            context.Branch(endLabel);
            context.MarkLabel(callableLabel);
            Operand threeArgLabel = Label();

            context.BranchIfTrue(threeArgLabel, unixOldSigaction3Arg);

            context.Call(unixOldSigaction, OperandType.None, context.LoadArgument(OperandType.I32, 0));
            context.Branch(endLabel);

            context.MarkLabel(threeArgLabel);

            context.Call(unixOldSigaction,
                OperandType.None,
                context.LoadArgument(OperandType.I32, 0),
                sigInfoPtr,
                context.LoadArgument(OperandType.I64, 2)
                );

            context.MarkLabel(endLabel);

            context.Return();

            ControlFlowGraph cfg = context.GetControlFlowGraph();

            OperandType[] argTypes = [OperandType.I32, OperandType.I64, OperandType.I64];

            return Compiler.Compile(cfg, argTypes, OperandType.None, CompilerOptions.HighCq, RuntimeInformation.ProcessArchitecture).Code;
        }

        public static byte[] GenerateWindowsSignalHandler(nint signalStructPtr, int rangeStructSize)
        {
            EmitterContext context = new();

            // (ExceptionPointers* exceptionInfo)
            Operand exceptionInfoPtr = context.LoadArgument(OperandType.I64, 0);
            Operand exceptionRecordPtr = context.Load(OperandType.I64, exceptionInfoPtr);

            // First thing's first - this catches a number of exceptions, but we only want access violations.
            Operand validExceptionLabel = Label();

            Operand exceptionCode = context.Load(OperandType.I32, exceptionRecordPtr);

            context.BranchIf(validExceptionLabel, exceptionCode, Const(EXCEPTION_ACCESS_VIOLATION), Comparison.Equal);

            context.Return(Const(EXCEPTION_CONTINUE_SEARCH)); // Don't handle this one.

            context.MarkLabel(validExceptionLabel);

            // Next, read the address of the invalid access, and whether it is a write or not.

            Operand structAddressOffset = context.Load(OperandType.I32, Const((ulong)signalStructPtr + StructAddressOffset));
            Operand structWriteOffset = context.Load(OperandType.I32, Const((ulong)signalStructPtr + StructWriteOffset));

            Operand faultAddress = context.Load(OperandType.I64, context.Add(exceptionRecordPtr, context.ZeroExtend32(OperandType.I64, structAddressOffset)));
            Operand writeFlag = context.Load(OperandType.I64, context.Add(exceptionRecordPtr, context.ZeroExtend32(OperandType.I64, structWriteOffset)));
            Operand faultPc = context.Load(OperandType.I64, context.Add(exceptionRecordPtr, Const(16UL))); // ExceptionAddress

            Operand isWrite = context.ICompareNotEqual(writeFlag, Const(0L)); // Normalize to 0/1.

            Operand isInRegion = EmitGenericRegionCheck(context, signalStructPtr, faultAddress, faultPc, isWrite, rangeStructSize, false, false, RuntimeInformation.ProcessArchitecture);

            Operand endLabel = Label();

            // If the region check result is false, then run the next vectored exception handler.

            context.BranchIfTrue(endLabel, isInRegion);

            context.Return(Const(EXCEPTION_CONTINUE_SEARCH));

            context.MarkLabel(endLabel);

            // Otherwise, return to execution.

            context.Return(Const(EXCEPTION_CONTINUE_EXECUTION));

            // Compile and return the function.

            ControlFlowGraph cfg = context.GetControlFlowGraph();

            OperandType[] argTypes = [OperandType.I64];

            return Compiler.Compile(cfg, argTypes, OperandType.I32, CompilerOptions.HighCq, RuntimeInformation.ProcessArchitecture).Code;
        }

        private static void GenerateFaultAddressPatchCode(EmitterContext context, Operand faultAddress, Operand newAddress, bool darwin, Architecture contextArchitecture)
        {
            if (contextArchitecture == Architecture.Arm64)
            {
                {
                    Operand lblSkip = Label();

                    context.BranchIf(lblSkip, faultAddress, newAddress, Comparison.Equal);

                    Operand ucontextPtr = context.LoadArgument(OperandType.I64, 2);
                    Operand pcCtxAddress = default;
                    ulong baseRegsOffset = 0;

                    if (!darwin)
                    {
                        pcCtxAddress = context.Add(ucontextPtr, Const(440UL));
                        baseRegsOffset = 184UL;
                    }
                    else
                    {
                        ucontextPtr = context.Load(OperandType.I64, context.Add(ucontextPtr, Const(48UL)));

                        pcCtxAddress = context.Add(ucontextPtr, Const(272UL));
                        baseRegsOffset = 16UL;
                    }

                    Operand pc = context.Load(OperandType.I64, pcCtxAddress);

                    Operand reg = GetAddressRegisterFromArm64Instruction(context, pc);
                    Operand reg64 = context.ZeroExtend32(OperandType.I64, reg);
                    Operand regCtxAddress = context.Add(ucontextPtr, context.Add(context.ShiftLeft(reg64, Const(3)), Const(baseRegsOffset)));
                    Operand regAddress = context.Load(OperandType.I64, regCtxAddress);

                    Operand addressDelta = context.Subtract(regAddress, faultAddress);

                    context.Store(regCtxAddress, context.Add(newAddress, addressDelta));

                    context.MarkLabel(lblSkip);
                }
            }
        }

        private static Operand GetAddressRegisterFromArm64Instruction(EmitterContext context, Operand pc)
        {
            Operand inst = context.Load(OperandType.I32, pc);
            Operand reg = context.AllocateLocal(OperandType.I32);

            Operand isSysInst = context.ICompareEqual(context.BitwiseAnd(inst, Const(0xFFF80000)), Const(0xD5080000));

            Operand lblSys = Label();
            Operand lblEnd = Label();

            context.BranchIfTrue(lblSys, isSysInst, BasicBlockFrequency.Cold);

            context.Copy(reg, context.BitwiseAnd(context.ShiftRightUI(inst, Const(5)), Const(0x1F)));
            context.Branch(lblEnd);

            context.MarkLabel(lblSys);
            context.Copy(reg, context.BitwiseAnd(inst, Const(0x1F)));

            context.MarkLabel(lblEnd);

            return reg;
        }

        public static bool SupportsFaultAddressPatchingForHost()
        {
            return SupportsFaultAddressPatchingForHostArch() && SupportsFaultAddressPatchingForHostOs();
        }

        private static bool SupportsFaultAddressPatchingForHostArch()
        {
            return RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
        }

        private static bool SupportsFaultAddressPatchingForHostOs()
        {
            return OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsIOS();
        }
    }
}
