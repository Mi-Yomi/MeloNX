using Ryujinx.Graphics.GAL;
using System;

namespace Ryujinx.Graphics.Gpu.Memory
{
    readonly struct CacheMemoryBudget
    {
        public ulong BufferCapacity { get; }
        public ulong TextureCapacity { get; }
        public bool IsAppleUnifiedMemory { get; }

        public CacheMemoryBudget(ulong bufferCapacity, ulong textureCapacity, bool isAppleUnifiedMemory)
        {
            BufferCapacity = bufferCapacity;
            TextureCapacity = textureCapacity;
            IsAppleUnifiedMemory = isAppleUnifiedMemory;
        }
    }

    /// <summary>
    /// Computes cache memory limits from host memory information and initialized renderer capabilities.
    /// </summary>
    static class CacheMemoryBudgetPolicy
    {
        private const ulong MiB = 1024 * 1024;
        private const ulong GiB = 1024 * MiB;

        private const ulong DefaultBufferCapacity = 2 * GiB;
        private const ulong IosUnifiedBufferCapacity = 64 * MiB;
        private const ulong MinUnifiedBufferCapacity = 256 * MiB;
        private const ulong MaxUnifiedBufferCapacity = 768 * MiB;

        private const ulong DefaultTextureCapacity = 1 * GiB;
        private const ulong MinTextureCapacity = 512 * MiB;
        private const ulong IosUnifiedTextureCapacity = 64 * MiB;
        private const ulong MinUnifiedTextureCapacity = 256 * MiB;
        private const ulong MaxUnifiedTextureCapacity = 768 * MiB;
        private const ulong TextureCapacity6GiB = 4 * GiB;
        private const ulong TextureCapacity8GiB = 6 * GiB;
        private const ulong TextureCapacity12GiB = 12 * GiB;

        private const int UnifiedMemoryDivisor = 16;

        public static CacheMemoryBudget Calculate(
            ulong cpuMemorySize,
            ulong maximumGpuMemory,
            SystemMemoryType memoryType,
            bool isApplePlatform,
            bool isIosPlatform)
        {
            bool isAppleUnifiedMemory = isApplePlatform && memoryType == SystemMemoryType.UnifiedMemory;

            if (isAppleUnifiedMemory)
            {
                if (isIosPlatform)
                {
                    return new CacheMemoryBudget(
                        IosUnifiedBufferCapacity,
                        IosUnifiedTextureCapacity,
                        true);
                }

                return new CacheMemoryBudget(
                    Math.Clamp(cpuMemorySize / UnifiedMemoryDivisor, MinUnifiedBufferCapacity, MaxUnifiedBufferCapacity),
                    Math.Clamp(cpuMemorySize / UnifiedMemoryDivisor, MinUnifiedTextureCapacity, MaxUnifiedTextureCapacity),
                    true);
            }

            return new CacheMemoryBudget(
                DefaultBufferCapacity,
                CalculateTextureCapacity(cpuMemorySize, maximumGpuMemory),
                false);
        }

        private static ulong CalculateTextureCapacity(ulong cpuMemorySize, ulong maximumGpuMemory)
        {
            ulong cpuMemorySizeGiB = cpuMemorySize / GiB;

            if (cpuMemorySizeGiB < 6 || maximumGpuMemory == 0)
            {
                return DefaultTextureCapacity;
            }

            ulong maximumTextureCapacity = cpuMemorySizeGiB switch
            {
                6 => TextureCapacity6GiB,
                8 => TextureCapacity8GiB,
                _ => TextureCapacity12GiB,
            };

            return Math.Clamp(maximumGpuMemory / 2, MinTextureCapacity, maximumTextureCapacity);
        }
    }
}
