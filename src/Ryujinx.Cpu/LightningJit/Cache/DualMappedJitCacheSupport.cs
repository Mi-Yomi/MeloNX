using System;
using System.Runtime.CompilerServices;

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

    // Managed allocation accounting, separate from the native executable mapping so it can be tested on any host.
    sealed class SharedJitCacheAllocator
    {
        private const int CodeAlignment = 4;
        private static readonly int[] UsageThresholds = [75, 90, 95];

        private readonly CacheMemoryAllocator _allocator;
        private readonly Action<int, SharedJitCacheAllocator> _usageThresholdReached;
        private int _nextUsageThreshold;

        public int CapacityBytes { get; }
        public int UsedBytes { get; private set; }
        public int FreeBytes => CapacityBytes - UsedBytes;

        // Highest allocated end offset; unlike UsedBytes, this also spans reusable alignment gaps.
        public int AddressHighWaterBytes { get; private set; }

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

            UsedBytes += (int)alignedBytes;
            AddressHighWaterBytes = Math.Max(AddressHighWaterBytes, offset + (int)alignedBytes);

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
