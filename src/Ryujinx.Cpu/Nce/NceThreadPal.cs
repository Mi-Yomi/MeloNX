using Ryujinx.Common;
using System;

namespace Ryujinx.Cpu.Nce
{
    static class NceThreadPal
    {
        private const int SigUsr2Linux = 12;
        private const int SigUsr2MacOS = 31;

        public static int UnixSuspendSignal => OperatingSystem.IsMacOS() || OperatingSystem.IsIOS() ? SigUsr2MacOS : SigUsr2Linux;

        public static IntPtr GetCurrentThreadHandle()
        {
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsIOS())
            {
                return NceThreadPalUnix.GetCurrentThreadHandle();
            }
            else
            {
                throw new PlatformNotSupportedException();
            }
        }

        public static void SuspendThread(IntPtr handle)
        {
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsIOS())
            {
                NceThreadPalUnix.SuspendThread(handle);
            }
            else
            {
                throw new PlatformNotSupportedException();
            }
        }
    }
}
