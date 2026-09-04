using Ryujinx.Common;
using Ryujinx.Common.Logging;
using Ryujinx.Memory;
using System;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Threading;

namespace Ryujinx.Cpu.LightningJit.Cache
{
    [SupportedOSPlatform("macos")]
    [SupportedOSPlatform("ios")]
    class DualMappedNoWxCache : IDisposable
    {
        // Capture the setting on the first mapping attempt, before game launch. Live code cannot be resized safely.
        private static readonly Lazy<DualMappedJitCacheConfiguration> CacheConfiguration = new(() =>
            DualMappedJitCacheConfiguration.Resolve(
                Environment.GetEnvironmentVariable(DualMappedJitCacheConfiguration.EnvironmentVariable),
                DualMappedJitAllocator.hasTXM));
        private static readonly Lock InitializationLock = new();

        private class MemoryCache : IDisposable
        {
            private readonly DualMappedJitAllocator _allocator;
            private readonly SharedJitCacheAllocator _cacheAllocator;
            public IntPtr RwPointer => _allocator.RwPtr;
            public IntPtr RxPointer => _allocator.RxPtr;

            public MemoryCache(DualMappedJitAllocator alloc, int size)
            {
                _allocator = alloc;
                _cacheAllocator = new(size, LogUsageThreshold);
                DualMappedJitCacheDiagnostics.Register(_cacheAllocator);
            }

            public int Allocate(int codeSize)
            {
                return _cacheAllocator.Allocate(codeSize);
            }

            public int AllocateAligned(int codeSize, int alignment)
            {
                return _cacheAllocator.AllocateAligned(codeSize, alignment);
            }

            public void SysIcacheInvalidate(int offset, int size)
            {
                if (OperatingSystem.IsMacOS() || OperatingSystem.IsIOS())
                {
                    JitSupportDarwin.SysIcacheInvalidate(_allocator.RxPtr + offset, size);
                }
                else
                {
                    throw new PlatformNotSupportedException();
                }
            }

            public void DetachDiagnostics()
            {
                DualMappedJitCacheDiagnostics.Unregister(_cacheAllocator);
            }

            private static void LogUsageThreshold(int threshold, SharedJitCacheAllocator allocator)
            {
                Logger.Warning?.Print(LogClass.Cpu,
                    $"Dual-mapped JIT cache reached {threshold}% usage: used={allocator.UsedBytes} bytes, " +
                    $"capacity={allocator.CapacityBytes} bytes, free={allocator.FreeBytes} bytes, " +
                    $"addressHighWater={allocator.AddressHighWaterBytes} bytes.");
            }

            protected virtual void Dispose(bool disposing)
            {
                if (disposing)
                {
                    DualMappedJitCacheDiagnostics.Unregister(_cacheAllocator);
                    _allocator.Dispose();
                }
            }

            public void Dispose()
            {
                Dispose(disposing: true);
                GC.SuppressFinalize(this);
            }
        }

        private readonly Translator _translator;
        private MemoryCache _sharedCache;
        private static DualMappedJitAllocator _sharedCacheAlloc;
        private readonly Lock _lock = new();

        public DualMappedNoWxCache(Translator translator)
        {
            _translator = translator;
            InitMemoryCache();
            DualMappedJitCacheConfiguration configuration = CacheConfiguration.Value;
            _sharedCache = new(_sharedCacheAlloc, configuration.CapacityBytes);

            // Mapping normally happens before the app configures file logging; repeat the effective setting at game launch.
            if (configuration.InvalidOverride)
            {
                Logger.Warning?.Print(LogClass.Cpu,
                    $"Invalid {DualMappedJitCacheConfiguration.EnvironmentVariable}; expected 512, 768 or 1024. " +
                    $"Using the default {configuration.SizeMiB} MiB JIT cache.");
            }

            Logger.Info?.Print(LogClass.Cpu,
                $"Dual-mapped JIT cache: {configuration.SizeMiB} MiB " +
                $"({(configuration.IsOverride ? "process-start override" : "default")}). " +
                "Changing the size requires restarting the app; this does not change guest RAM.");
        }

        public static void InitMemoryCache()
        {
            lock (InitializationLock)
            {
                if (_sharedCacheAlloc != null)
                {
                    return;
                }

                _sharedCacheAlloc = new((ulong)CacheConfiguration.Value.CapacityBytes);
            }
        }

        public unsafe nint Map(ReadOnlySpan<byte> code, ulong guestAddress, ulong guestSize)
        {
            if (TryGetCachedFunction(guestAddress, out nint funcPtr))
            {
                return funcPtr;
            }

            lock (_lock)
            {
                if (TryGetSharedFunction(guestAddress, out funcPtr))
                {
                    return funcPtr;
                }

                int funcOffset = _sharedCache.Allocate(code.Length);

                funcPtr = _sharedCache.RwPointer + funcOffset;
                code.CopyTo(new Span<byte>((void*)funcPtr, code.Length));
                _sharedCache.SysIcacheInvalidate(funcOffset, code.Length);

                funcPtr = _sharedCache.RxPointer + funcOffset;
                RegisterFunction(guestAddress, new TranslatedFunction(funcPtr, guestSize));

                return funcPtr;
            }
        }

        public unsafe nint MapPageAligned(ReadOnlySpan<byte> code)
        {
            lock (_lock)
            {
                int sizeAligned = BitUtils.AlignUp(code.Length, (int)MemoryBlock.GetPageSize());
                int funcOffset = _sharedCache.AllocateAligned(sizeAligned, (int)MemoryBlock.GetPageSize());

                Debug.Assert((funcOffset & ((int)MemoryBlock.GetPageSize() - 1)) == 0);

                nint funcPtr = _sharedCache.RwPointer + funcOffset;
                code.CopyTo(new Span<byte>((void*)funcPtr, code.Length));
                funcPtr = _sharedCache.RxPointer + funcOffset;

                _sharedCache.SysIcacheInvalidate(funcOffset, sizeAligned);

                return funcPtr;
            }
        }

        internal bool TryGetCachedFunction(ulong guestAddress, out nint funcPtr)
        {
            return TryGetSharedFunction(guestAddress, out funcPtr);
        }

        private bool TryGetSharedFunction(ulong guestAddress, out nint funcPtr)
        {
            if (_translator.Functions.TryGetValue(guestAddress, out TranslatedFunction function))
            {
                funcPtr = function.FuncPointer;

                return true;
            }

            funcPtr = nint.Zero;

            return false;
        }

        private void RegisterFunction(ulong address, TranslatedFunction func)
        {
            TranslatedFunction oldFunc = _translator.Functions.GetOrAdd(address, func.GuestSize, func);

            Debug.Assert(oldFunc == func);

            _translator.RegisterFunction(address, func);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                // The process-wide TXM mapping is intentionally reused by the next game, so do
                // not dispose MemoryCache here. Only stop exposing this session's logical usage.
                _sharedCache?.DetachDiagnostics();
                _sharedCache = null;
            }
        }

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}
