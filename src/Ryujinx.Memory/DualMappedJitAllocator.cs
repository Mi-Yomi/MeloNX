using System;
using System.Runtime.InteropServices;
using System.Diagnostics;
using Ryujinx.Common.Logging;
using static Ryujinx.Memory.MemoryManagerUnixHelper;
using System.Runtime.Versioning;

namespace Ryujinx.Memory
{
    /// <summary>
    /// Class for JIT memory allocation on iOS.
    /// Intended to allocate memory with both r/x and r/w permissions,
    /// as a workaround for stricter W^X (Write XOR Execute) enforcement introduced in iOS 26.
    /// 
    /// Specifically targets iOS 26, where the traditional method of reprotecting
    /// memory from writable to executable (RX) no longer works for JIT code.
    /// </summary>
    ///     
    [SupportedOSPlatform("macos")]
    [SupportedOSPlatform("ios")]
    public class DualMappedJitAllocator : IDisposable
    {
        private readonly Action<nint, ulong> _unmap;
        private nint? _rwPtr;
        private nint? _rxPtr;

        public nint RwPtr => _rwPtr.GetValueOrDefault();
        public nint RxPtr => _rxPtr.GetValueOrDefault();
        public ulong Size { get; private set; }

        [DllImport("BreakpointJIT.framework/BreakpointJIT", EntryPoint = "BreakGetJITMapping")]
        public static extern unsafe byte* BreakGetJITMappingPub(byte* addr, nuint bytes);

        [DllImport("BreakpointJIT.framework/BreakpointJIT", EntryPoint = "BreakMarkJITMapping")]
        public static extern unsafe byte* BreakMarkJITMapping(nuint bytes);

        [DllImport("BreakpointJIT.framework/BreakpointJIT", EntryPoint = "BreakJITDetach")]
        public static extern unsafe void BreakJITDetach();

        static public bool hasTXM => Environment.GetEnvironmentVariable("HAS_TXM") == "1"; 

        static public bool dualMappingEnabled => Environment.GetEnvironmentVariable("DUAL_MAPPED_JIT") == "1"; 

        static private bool usingNewMapping = false;

        public DualMappedJitAllocator(ulong size)
        {
            var stackTrace = new StackTrace(1, false);
            var callingMethod = stackTrace.GetFrame(0)?.GetMethod();

            Logger.Info?.Print(LogClass.Cpu,
                $"Allocating dual-mapped JIT memory of size {size} bytes, called by {callingMethod?.DeclaringType?.FullName}.{callingMethod?.Name} with {hasTXM}, {dualMappingEnabled}");
            Size = size;
            _unmap = Unmap;
            AllocateDualMapping(AllocateRxMapping, RemapRxMapping, ProtectRwMapping);
        }

        // Inject only native operations; ownership and failure cleanup use the same path as production.
        internal DualMappedJitAllocator(
            ulong size,
            Func<ulong, nint?> map,
            Func<nint, ulong, nint> remap,
            Action<nint, ulong> protect,
            Action<nint, ulong> unmap)
        {
            Size = size;
            _unmap = unmap;
            AllocateDualMapping(map, remap, protect);
        }

        static nint? BreakGetJITMapping(nuint bytes)
        {
            unsafe
            {
                byte* ptr = usingNewMapping ? (byte*)0 : (byte*)BreakMarkJITMapping(bytes);
                Logger.Info?.Print(LogClass.Cpu, $"testing for BreakGetJITMapping, got {(ulong)ptr}");
                if (ptr == null || ptr == (byte*)0 || ptr == (byte*)-1 || ptr == (byte*)14757395257293275360 || ptr == (byte*)1761607904)
                {
                    ptr = BreakGetJITMappingPub(null, bytes);
                    Logger.Info?.Print(LogClass.Cpu, $"testing for BreakGetJITMapping Again, got {(ulong)ptr}");
                    if (ptr == null || ptr == (byte*)0 || ptr == (byte*)-1)
                    {
                        Logger.Info?.Print(LogClass.Cpu, "Failed to get JIT mapping from BreakGetJITMapping.");
                        return null;
                    } else { usingNewMapping = true; }
                }

                return (nint)ptr;
            }
        }

        private static nint? AllocateRxMapping(ulong size)
        {
            if (hasTXM)
            {
                return BreakGetJITMapping((nuint)size);
            }

            return Mmap(0, size, MmapProts.PROT_READ | MmapProts.PROT_EXEC, MmapFlags.MAP_ANONYMOUS | MmapFlags.MAP_PRIVATE, -1, 0);
        }

        private void AllocateDualMapping(
            Func<ulong, nint?> map,
            Func<nint, ulong, nint> remap,
            Action<nint, ulong> protect)
        {
            nint? rxPtr = map(Size);
            if (rxPtr == null || rxPtr == MAP_FAILED)
            {
                throw new Exception("Failed to mmap memory");
            }

            // Record ownership immediately, so a later native failure cannot abandon a mapping.
            _rxPtr = rxPtr;
            try
            {
                _rwPtr = remap(rxPtr.Value, Size);
                protect(_rwPtr.Value, Size);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        private static nint RemapRxMapping(nint rxPtr, ulong size)
        {
            ulong bufRW = 0;
            uint curProt = 0, maxProt = 0;

            int remapResult = vm_remap(mach_task_self(), ref bufRW, size, 0, VM_FLAGS_ANYWHERE,
                                      mach_task_self(), (ulong)rxPtr, 0, ref curProt, ref maxProt, VM_INHERIT_NONE);
            if (remapResult != KERN_SUCCESS)
            {
                throw new Exception($"Failed to remap RX region: {remapResult}");
            }

            return (nint)bufRW;
        }

        private static void ProtectRwMapping(nint rwPtr, ulong size)
        {
            int protectRWResult = vm_protect(mach_task_self(), (ulong)rwPtr, size, 0, VM_PROT_READ | VM_PROT_WRITE);
            if (protectRWResult != KERN_SUCCESS)
            {
                throw new Exception($"Failed to set RW protection: {protectRWResult}");
            }
        }

        private static void Unmap(nint pointer, ulong size)
        {
            munmap(pointer, size);
        }

        public void Dispose()
        {
            nint? rwPtr = _rwPtr;
            nint? rxPtr = _rxPtr;
            _rwPtr = null;
            _rxPtr = null;

            try
            {
                if (rwPtr.HasValue)
                {
                    _unmap(rwPtr.Value, Size);
                }
            }
            finally
            {
                if (rxPtr.HasValue)
                {
                    _unmap(rxPtr.Value, Size);
                }
            }
        }

        private const int MAP_ANON = 0x1000;
        private const int MAP_PRIVATE = 0x2;

        private const int VM_FLAGS_ANYWHERE = 1 << 0;
        private const int VM_INHERIT_NONE = 2;
        private const int KERN_SUCCESS = 0;
        private const int VM_PROT_READ = 1;
        private const int VM_PROT_WRITE = 2;

        [DllImport("libc")]
        private static extern ulong mach_task_self();

        [DllImport("libc")]
        private static extern int vm_remap(
            ulong target_task,
            ref ulong target_address,
            ulong size,
            ulong mask,
            int anywhere,
            ulong src_task,
            ulong src_address,
            int copy,
            ref uint cur_protection,
            ref uint max_protection,
            int inheritance
        );

        [DllImport("libc")]
        private static extern int vm_protect(
            ulong task,
            ulong address,
            ulong size,
            int set_maximum,
            int new_protection
        );
    }
}
