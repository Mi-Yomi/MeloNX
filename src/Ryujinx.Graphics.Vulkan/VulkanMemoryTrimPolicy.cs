namespace Ryujinx.Graphics.Vulkan
{
    internal readonly record struct VulkanMemoryTrimDecision(
        bool RunHeavyCacheTrim,
        bool RunDescriptorTrim,
        bool RunManagedCollection);

    internal static class VulkanMemoryTrimPolicy
    {
        private const ulong MiB = 1024 * 1024;
        private const long ManagedMiB = 1024 * 1024;
        private const ulong EmergencyAvailableMemory = 256 * MiB;
        private const long HeavyTrimIntervalMilliseconds = 30_000;
        private const long DescriptorTrimIntervalMilliseconds = 15_000;
        private const long EmergencyDescriptorTrimIntervalMilliseconds = 8_000;
        private const long ManagedCollectionIntervalMilliseconds = 15_000;
        private const long EmergencyManagedCollectionIntervalMilliseconds = 8_000;
        private const long ManagedCollectionThresholdBytes = 512 * ManagedMiB;
        private const long EmergencyManagedCollectionThresholdBytes = 384 * ManagedMiB;

        public static VulkanMemoryTrimDecision Calculate(
            bool isIos,
            bool critical,
            ulong availableMemoryBytes,
            long managedHeapBytes,
            long nowMilliseconds,
            long lastHeavyTrimMilliseconds,
            long lastDescriptorTrimMilliseconds,
            long lastManagedCollectionMilliseconds)
        {
            bool runHeavyCacheTrim = critical &&
                IsDue(nowMilliseconds, lastHeavyTrimMilliseconds, HeavyTrimIntervalMilliseconds);

            if (!isIos)
            {
                return new(runHeavyCacheTrim, runHeavyCacheTrim, runHeavyCacheTrim);
            }

            if (!critical)
            {
                return new(false, false, false);
            }

            bool emergency = availableMemoryBytes <= EmergencyAvailableMemory;
            long descriptorInterval = emergency
                ? EmergencyDescriptorTrimIntervalMilliseconds
                : DescriptorTrimIntervalMilliseconds;
            bool runDescriptorTrim = runHeavyCacheTrim ||
                IsDue(nowMilliseconds, lastDescriptorTrimMilliseconds, descriptorInterval);

            bool regularManagedCollection = managedHeapBytes >= ManagedCollectionThresholdBytes &&
                IsDue(nowMilliseconds, lastManagedCollectionMilliseconds, ManagedCollectionIntervalMilliseconds);
            bool emergencyManagedCollection = emergency &&
                managedHeapBytes >= EmergencyManagedCollectionThresholdBytes &&
                IsDue(nowMilliseconds, lastManagedCollectionMilliseconds, EmergencyManagedCollectionIntervalMilliseconds);

            return new(
                runHeavyCacheTrim,
                runDescriptorTrim,
                runHeavyCacheTrim || regularManagedCollection || emergencyManagedCollection);
        }

        private static bool IsDue(long nowMilliseconds, long lastRunMilliseconds, long intervalMilliseconds)
        {
            return lastRunMilliseconds == 0 || nowMilliseconds - lastRunMilliseconds >= intervalMilliseconds;
        }
    }
}
