using Ryujinx.HLE.Exceptions;
using Ryujinx.HLE.HOS.Tamper.Conditions;
using Ryujinx.HLE.HOS.Tamper.Operations;
using System;
using System.Globalization;

namespace Ryujinx.HLE.HOS.Tamper
{
    class InstructionHelper
    {
        private const int CodeTypeIndex = 0;

        public static void Emit(IOperation operation, CompilationContext context)
        {
            context.CurrentOperations.Add(operation);
        }

        public static void Emit(Type instruction, byte width, CompilationContext context, params Object[] operands)
        {
            Emit((IOperation)Create(instruction, width, operands), context);
        }

        public static void EmitMov(byte width, CompilationContext context, IOperand destination, IOperand source)
        {
            Emit(typeof(OpMov<>), width, context, destination, source);
        }

        public static ICondition CreateCondition(Comparison comparison, byte width, IOperand lhs, IOperand rhs)
        {
            ICondition Create(Type conditionType)
            {
                return (ICondition)InstructionHelper.Create(conditionType, width, lhs, rhs);
            }

            return comparison switch
            {
                Comparison.Greater => Create(typeof(CondGT<>)),
                Comparison.GreaterOrEqual => Create(typeof(CondGE<>)),
                Comparison.Less => Create(typeof(CondLT<>)),
                Comparison.LessOrEqual => Create(typeof(CondLE<>)),
                Comparison.Equal => Create(typeof(CondEQ<>)),
                Comparison.NotEqual => Create(typeof(CondNE<>)),
                _ => throw new TamperCompilationException($"Invalid comparison {comparison} in Atmosphere cheat"),
            };
        }

        public static Object Create(Type instruction, byte width, params Object[] operands)
        {
            // Use explicit factory methods to avoid reflection and support Native AOT compilation
            if (instruction == typeof(OpMov<>))
            {
                return CreateOpMov(width, operands);
            }
            else if (instruction == typeof(CondGT<>))
            {
                return CreateCondGT(width, operands);
            }
            else if (instruction == typeof(CondGE<>))
            {
                return CreateCondGE(width, operands);
            }
            else if (instruction == typeof(CondLT<>))
            {
                return CreateCondLT(width, operands);
            }
            else if (instruction == typeof(CondLE<>))
            {
                return CreateCondLE(width, operands);
            }
            else if (instruction == typeof(CondEQ<>))
            {
                return CreateCondEQ(width, operands);
            }
            else if (instruction == typeof(CondNE<>))
            {
                return CreateCondNE(width, operands);
            }
            else
            {
                throw new TamperCompilationException($"Unsupported instruction type {instruction.Name} in Atmosphere cheat");
            }
        }

        private static IOperation CreateOpMov(byte width, params Object[] operands)
        {
            IOperand destination = (IOperand)operands[0];
            IOperand source = (IOperand)operands[1];
            
            return width switch
            {
                1 => new OpMov<byte>(destination, source),
                2 => new OpMov<ushort>(destination, source),
                4 => new OpMov<uint>(destination, source),
                8 => new OpMov<ulong>(destination, source),
                _ => throw new TamperCompilationException($"Invalid instruction width {width} in Atmosphere cheat"),
            };
        }

        private static ICondition CreateCondGT(byte width, params Object[] operands)
        {
            IOperand lhs = (IOperand)operands[0];
            IOperand rhs = (IOperand)operands[1];
            
            return width switch
            {
                1 => new CondGT<byte>(lhs, rhs),
                2 => new CondGT<ushort>(lhs, rhs),
                4 => new CondGT<uint>(lhs, rhs),
                8 => new CondGT<ulong>(lhs, rhs),
                _ => throw new TamperCompilationException($"Invalid instruction width {width} in Atmosphere cheat"),
            };
        }

        private static ICondition CreateCondGE(byte width, params Object[] operands)
        {
            IOperand lhs = (IOperand)operands[0];
            IOperand rhs = (IOperand)operands[1];
            
            return width switch
            {
                1 => new CondGE<byte>(lhs, rhs),
                2 => new CondGE<ushort>(lhs, rhs),
                4 => new CondGE<uint>(lhs, rhs),
                8 => new CondGE<ulong>(lhs, rhs),
                _ => throw new TamperCompilationException($"Invalid instruction width {width} in Atmosphere cheat"),
            };
        }

        private static ICondition CreateCondLT(byte width, params Object[] operands)
        {
            IOperand lhs = (IOperand)operands[0];
            IOperand rhs = (IOperand)operands[1];
            
            return width switch
            {
                1 => new CondLT<byte>(lhs, rhs),
                2 => new CondLT<ushort>(lhs, rhs),
                4 => new CondLT<uint>(lhs, rhs),
                8 => new CondLT<ulong>(lhs, rhs),
                _ => throw new TamperCompilationException($"Invalid instruction width {width} in Atmosphere cheat"),
            };
        }

        private static ICondition CreateCondLE(byte width, params Object[] operands)
        {
            IOperand lhs = (IOperand)operands[0];
            IOperand rhs = (IOperand)operands[1];
            
            return width switch
            {
                1 => new CondLE<byte>(lhs, rhs),
                2 => new CondLE<ushort>(lhs, rhs),
                4 => new CondLE<uint>(lhs, rhs),
                8 => new CondLE<ulong>(lhs, rhs),
                _ => throw new TamperCompilationException($"Invalid instruction width {width} in Atmosphere cheat"),
            };
        }

        private static ICondition CreateCondEQ(byte width, params Object[] operands)
        {
            IOperand lhs = (IOperand)operands[0];
            IOperand rhs = (IOperand)operands[1];
            
            return width switch
            {
                1 => new CondEQ<byte>(lhs, rhs),
                2 => new CondEQ<ushort>(lhs, rhs),
                4 => new CondEQ<uint>(lhs, rhs),
                8 => new CondEQ<ulong>(lhs, rhs),
                _ => throw new TamperCompilationException($"Invalid instruction width {width} in Atmosphere cheat"),
            };
        }

        private static ICondition CreateCondNE(byte width, params Object[] operands)
        {
            IOperand lhs = (IOperand)operands[0];
            IOperand rhs = (IOperand)operands[1];
            
            return width switch
            {
                1 => new CondNE<byte>(lhs, rhs),
                2 => new CondNE<ushort>(lhs, rhs),
                4 => new CondNE<uint>(lhs, rhs),
                8 => new CondNE<ulong>(lhs, rhs),
                _ => throw new TamperCompilationException($"Invalid instruction width {width} in Atmosphere cheat"),
            };
        }

        public static ulong GetImmediate(byte[] instruction, int index, int nybbleCount)
        {
            ulong value = 0;

            for (int i = 0; i < nybbleCount; i++)
            {
                value <<= 4;
                value |= instruction[index + i];
            }

            return value;
        }

        public static CodeType GetCodeType(byte[] instruction)
        {
            int codeType = instruction[CodeTypeIndex];

            if (codeType >= 0xC)
            {
                byte extension = instruction[CodeTypeIndex + 1];
                codeType = (codeType << 4) | extension;

                if (extension == 0xF)
                {
                    extension = instruction[CodeTypeIndex + 2];
                    codeType = (codeType << 4) | extension;
                }
            }

            return (CodeType)codeType;
        }

        public static byte[] ParseRawInstruction(string rawInstruction)
        {
            const int WordSize = 2 * sizeof(uint);

            // Instructions are multi-word, with 32bit words. Split the raw instruction
            // and parse each word into individual nybbles of bits.

            var words = rawInstruction.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);

            byte[] instruction = new byte[WordSize * words.Length];

            if (words.Length == 0)
            {
                throw new TamperCompilationException("Empty instruction in Atmosphere cheat");
            }

            for (int wordIndex = 0; wordIndex < words.Length; wordIndex++)
            {
                string word = words[wordIndex];

                if (word.Length != WordSize)
                {
                    throw new TamperCompilationException($"Invalid word length for {word} in Atmosphere cheat");
                }

                for (int nybbleIndex = 0; nybbleIndex < WordSize; nybbleIndex++)
                {
                    int index = wordIndex * WordSize + nybbleIndex;

                    instruction[index] = byte.Parse(word.AsSpan(nybbleIndex, 1), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                }
            }

            return instruction;
        }
    }
}
