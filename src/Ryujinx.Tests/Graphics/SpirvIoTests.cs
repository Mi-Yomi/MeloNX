using NUnit.Framework;
using Ryujinx.Graphics.Shader;
using Ryujinx.Graphics.Shader.CodeGen;
using Ryujinx.Graphics.Shader.CodeGen.Spirv;
using Ryujinx.Graphics.Shader.IntermediateRepresentation;
using Ryujinx.Graphics.Shader.StructuredIr;
using Ryujinx.Graphics.Shader.Translation;
using System;

namespace Ryujinx.Tests.Graphics
{
    public class SpirvIoTests
    {
        [Test]
        public void FragmentClipDistanceInputGeneratesSpirv()
        {
            // The old SPIR-V path canonicalized this input to Position even though
            // fragment ClipDistance is declared as a standalone built-in array.
            StructuredProgramInfo info = new();
            info.IoDefinitions.Add(new IoDefinition(StorageKind.Input, IoVariable.ClipDistance));

            AstBlock main = new(AstBlockType.Main);
            main.Add(new AstOperation(
                Instruction.Load,
                StorageKind.Input,
                false,
                [
                    new AstOperand(OperandType.Constant, (int)IoVariable.ClipDistance),
                    new AstOperand(OperandType.Constant, 0),
                ],
                2));

            info.Functions.Add(new StructuredFunction(main, "main", AggregateType.Void, [], []));

            ShaderDefinitions definitions = new(ShaderStage.Fragment, default(GpuGraphicsState), false, 1, default, 0);
            CodeGenParameters parameters = new(
                new AttributeUsage(null),
                definitions,
                new ShaderProperties(),
                new HostCapabilities(false, false, false, false, false, false, false, false, false),
                null,
                TargetApi.Vulkan);

            byte[] spirv = SpirvGenerator.Generate(info, parameters);

            Assert.Multiple(() =>
            {
                Assert.That(spirv.Length, Is.GreaterThan(20));
                Assert.That(BitConverter.ToUInt32(spirv), Is.EqualTo(0x07230203u));
                Assert.That(HasCapability(spirv, Spv.Specification.Capability.ClipDistance), Is.True);
            });
        }

        private static bool HasCapability(byte[] spirv, Spv.Specification.Capability capability)
        {
            for (int offset = 5 * sizeof(uint); offset + sizeof(uint) <= spirv.Length;)
            {
                uint instruction = BitConverter.ToUInt32(spirv, offset);
                int wordCount = (int)(instruction >> 16);
                int byteCount = wordCount * sizeof(uint);

                if (wordCount == 0 || offset + byteCount > spirv.Length)
                {
                    return false;
                }

                if ((instruction & 0xffff) == (uint)Spv.Specification.Op.OpCapability &&
                    wordCount >= 2 &&
                    BitConverter.ToUInt32(spirv, offset + sizeof(uint)) == (uint)capability)
                {
                    return true;
                }

                offset += byteCount;
            }

            return false;
        }
    }
}
