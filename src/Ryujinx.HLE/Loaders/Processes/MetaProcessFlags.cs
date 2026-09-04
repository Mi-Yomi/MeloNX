using Ryujinx.HLE.HOS.Kernel.Process;

namespace Ryujinx.HLE.Loaders.Processes
{
    static class MetaProcessFlags
    {
        public static ProcessCreationFlags ToApplicationFlags(byte metaFlags)
        {
            // Only the instruction/address-space bits share their positions with CreateProcess.
            // META bits 4-6 are memory options, not EnableDebug, EnableAslr or IsApplication.
            ProcessCreationFlags flags = (ProcessCreationFlags)(metaFlags & 0x0f) | ProcessCreationFlags.IsApplication;

            if ((metaFlags & (1 << 4)) != 0)
            {
                flags |= ProcessCreationFlags.OptimizeMemoryAllocation;
            }

            if ((metaFlags & (1 << 5)) != 0)
            {
                flags |= ProcessCreationFlags.DisableDeviceAddressSpaceMerge;
            }

            if ((metaFlags & (1 << 6)) != 0)
            {
                flags |= ProcessCreationFlags.EnableAliasRegionExtraSize;
            }

            return flags;
        }
    }
}
