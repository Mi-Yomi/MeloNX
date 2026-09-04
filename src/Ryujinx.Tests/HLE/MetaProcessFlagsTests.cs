using NUnit.Framework;
using Ryujinx.HLE.HOS.Kernel.Process;
using Ryujinx.HLE.Loaders.Processes;

namespace Ryujinx.Tests.HLE
{
    class MetaProcessFlagsTests
    {
        [TestCase(0x00, ProcessCreationFlags.AddressSpace32Bit)]
        [TestCase(0x03, ProcessCreationFlags.Is64Bit | ProcessCreationFlags.AddressSpace64BitDeprecated)]
        [TestCase(0x04, ProcessCreationFlags.AddressSpace32BitWithoutAlias)]
        [TestCase(0x07, ProcessCreationFlags.Is64Bit | ProcessCreationFlags.AddressSpace64Bit)]
        public void PreservesInstructionAndAddressSpaceFlags(byte metaFlags, ProcessCreationFlags expected)
        {
            Assert.AreEqual(expected | ProcessCreationFlags.IsApplication, MetaProcessFlags.ToApplicationFlags(metaFlags));
        }

        [TestCase(0x17, ProcessCreationFlags.OptimizeMemoryAllocation)]
        [TestCase(0x27, ProcessCreationFlags.DisableDeviceAddressSpaceMerge)]
        [TestCase(0x47, ProcessCreationFlags.EnableAliasRegionExtraSize)]
        [TestCase(0x77, ProcessCreationFlags.OptimizeMemoryAllocation | ProcessCreationFlags.DisableDeviceAddressSpaceMerge | ProcessCreationFlags.EnableAliasRegionExtraSize)]
        public void TranslatesMemoryOptionsWithoutEnablingDebugOrAslr(byte metaFlags, ProcessCreationFlags expected)
        {
            ProcessCreationFlags actual = MetaProcessFlags.ToApplicationFlags(metaFlags);

            Assert.AreEqual(ProcessCreationFlags.Is64Bit | ProcessCreationFlags.AddressSpace64Bit |
                ProcessCreationFlags.IsApplication | expected, actual);
            Assert.AreEqual(0, (int)(actual & (ProcessCreationFlags.EnableDebug | ProcessCreationFlags.EnableAslr)));
        }

        [Test]
        public void HighMetaBitDoesNotSelectTheAppletPool()
        {
            Assert.AreEqual(MetaProcessFlags.ToApplicationFlags(0x07), MetaProcessFlags.ToApplicationFlags(0x87));
        }
    }
}
