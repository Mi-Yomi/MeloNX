using System;
using System.Runtime.CompilerServices;
using System.Threading;

[assembly: InternalsVisibleTo("Ryujinx.Tests")]

namespace Ryujinx.Cpu.LightningJit.Cache
{
    readonly record struct DualMappedJitCacheConfiguration(int SizeMiB, bool IsOverride, bool InvalidOverride)
    {
        public const string EnvironmentVariable = "MELONX_JIT_CACHE_MIB";

        public int CapacityBytes => SizeMiB * 1024 * 1024;

        public static DualMappedJitCacheConfiguration Resolve(string value, bool hasTxm)
        {
            int defaultSizeMiB = hasTxm ? 512 : 1024;

            if (string.IsNullOrWhiteSpace(value))
            {
                return new(defaultSizeMiB, false, false);
            }

            return value.Trim() switch
            {
                "512" => new(512, true, false),
                "768" => new(768, true, false),
                "1024" => new(1024, true, false),
                _ => new(defaultSizeMiB, false, true),
            };
        }
    }

    public readonly record struct DualMappedJitCacheUsage(
        int CapacityBytes,
        int UsedBytes,
        int FreeBytes,
        int AddressHighWaterBytes);

    /// <summary>
    /// Exposes read-only process-wide JIT cache accounting to the iOS diagnostics boundary.
    /// These are allocator bytes, not a measurement of resident physical pages.
    /// </summary>
    public static class DualMappedJitCacheDiagnostics
    {
        private static SharedJitCacheAllocator _allocator;

        internal static void Register(SharedJitCacheAllocator allocator)
        {
            Volatile.Write(ref _allocator, allocator);
        }

        internal static void Unregister(SharedJitCacheAllocator allocator)
        {
            Interlocked.CompareExchange(ref _allocator, null, allocator);
        }

        public static bool TryGetUsage(out DualMappedJitCacheUsage usage)
        {
            SharedJitCacheAllocator allocator = Volatile.Read(ref _allocator);
            if (allocator == null)
            {
                usage = default;
                return false;
            }

            int usedBytes = allocator.UsedBytes;
            int capacityBytes = allocator.CapacityBytes;
            usage = new(
                capacityBytes,
                usedBytes,
                Math.Max(0, capacityBytes - usedBytes),
                allocator.AddressHighWaterBytes);
            return true;
        }
    }

    // Managed allocation accounting, separate from the native executable mapping so it can be tested on any host.
    sealed class SharedJitCacheAllocator
    {
        private const int CodeAlignment = 4;
        private static readonly int[] UsageThresholds = [10, 25, 50, 75, 90, 95];

        private readonly CacheMemoryAllocator _allocator;
        private readonly Action<int, SharedJitCacheAllocator> _usageThresholdReached;
        private int _nextUsageThreshold;

        public int CapacityBytes { get; }
        private int _usedBytes;
        private int _addressHighWaterBytes;

        public int UsedBytes => Volatile.Read(ref _usedBytes);
        public int FreeBytes => CapacityBytes - UsedBytes;

        // Highest allocated end offset; unlike UsedBytes, this also spans reusable alignment gaps.
        public int AddressHighWaterBytes => Volatile.Read(ref _addressHighWaterBytes);

        public SharedJitCacheAllocator(int capacityBytes, Action<int, SharedJitCacheAllocator> usageThresholdReached = null)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacityBytes);
            CapacityBytes = capacityBytes;
            _allocator = new(capacityBytes);
            _usageThresholdReached = usageThresholdReached;
        }

        public int Allocate(int codeSize)
        {
            return AllocateCore(codeSize, CodeAlignment, pageAligned: false);
        }

        public int AllocateAligned(int codeSize, int alignment)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(alignment);
            if ((alignment & (alignment - 1)) != 0)
            {
                throw new ArgumentException("Alignment must be a power of two.", nameof(alignment));
            }

            return AllocateCore(codeSize, alignment, pageAligned: true);
        }

        private int AllocateCore(int requestedBytes, int alignment, bool pageAligned)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(requestedBytes);
            long alignedBytes = ((long)requestedBytes + CodeAlignment - 1) & ~(CodeAlignment - 1L);

            int offset = -1;
            if (alignedBytes <= CapacityBytes)
            {
                offset = pageAligned
                    ? _allocator.AllocateAligned((int)alignedBytes, alignment)
                    : _allocator.Allocate((int)alignedBytes);
            }

            if (offset < 0)
            {
                throw new OutOfMemoryException(
                    $"Dual-mapped JIT cache exhausted: requested={requestedBytes} bytes, aligned={alignedBytes} bytes, " +
                    $"alignment={alignment}, capacity={CapacityBytes} bytes, used={UsedBytes} bytes, " +
                    $"free={FreeBytes} bytes, addressHighWater={AddressHighWaterBytes} bytes. " +
                    "Free bytes may be split by alignment gaps; this is executable cache exhaustion, not a measured iOS memory limit.");
            }

            _usedBytes += (int)alignedBytes;
            _addressHighWaterBytes = Math.Max(_addressHighWaterBytes, offset + (int)alignedBytes);

            while (_nextUsageThreshold < UsageThresholds.Length &&
                   (long)UsedBytes * 100 >= (long)CapacityBytes * UsageThresholds[_nextUsageThreshold])
            {
                int threshold = UsageThresholds[_nextUsageThreshold++];
                _usageThresholdReached?.Invoke(threshold, this);
            }

            return offset;
        }
    }
}
