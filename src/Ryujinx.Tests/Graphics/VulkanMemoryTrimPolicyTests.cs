using NUnit.Framework;
using Ryujinx.Graphics.Vulkan;

namespace Ryujinx.Tests.Graphics
{
    public class VulkanMemoryTrimPolicyTests
    {
        private const ulong MiB = 1024 * 1024;

        [Test]
        public void LowPressureDoesNotRunExpensiveIosWork()
        {
            VulkanMemoryTrimDecision decision = Calculate(
                isIos: true,
                critical: false,
                availableMiB: 1200,
                managedMiB: 800,
                nowMilliseconds: 40_000,
                lastRunMilliseconds: 0);

            Assert.That(decision, Is.EqualTo(new VulkanMemoryTrimDecision(false, false, false)));
        }

        [Test]
        public void FirstCriticalRequestRunsCompleteReclamation()
        {
            VulkanMemoryTrimDecision decision = Calculate(
                isIos: true,
                critical: true,
                availableMiB: 1000,
                managedMiB: 300,
                nowMilliseconds: 40_000,
                lastRunMilliseconds: 0);

            Assert.That(decision, Is.EqualTo(new VulkanMemoryTrimDecision(true, true, true)));
        }

        [Test]
        public void NormalCriticalRequestsThrottleDescriptorAndManagedWork()
        {
            VulkanMemoryTrimDecision early = Calculate(
                isIos: true,
                critical: true,
                availableMiB: 700,
                managedMiB: 700,
                nowMilliseconds: 50_000,
                lastRunMilliseconds: 40_000);
            VulkanMemoryTrimDecision due = Calculate(
                isIos: true,
                critical: true,
                availableMiB: 700,
                managedMiB: 512,
                nowMilliseconds: 55_000,
                lastRunMilliseconds: 40_000);

            Assert.Multiple(() =>
            {
                Assert.That(early, Is.EqualTo(new VulkanMemoryTrimDecision(false, false, false)));
                Assert.That(due, Is.EqualTo(new VulkanMemoryTrimDecision(false, true, true)));
            });
        }

        [Test]
        public void NormalManagedCollectionRequiresUsefulHeapSize()
        {
            VulkanMemoryTrimDecision decision = Calculate(
                isIos: true,
                critical: true,
                availableMiB: 700,
                managedMiB: 511,
                nowMilliseconds: 55_000,
                lastRunMilliseconds: 40_000);

            Assert.That(decision, Is.EqualTo(new VulkanMemoryTrimDecision(false, true, false)));
        }

        [Test]
        public void EmergencyHeadroomUsesShorterIntervalsAndLowerHeapThreshold()
        {
            VulkanMemoryTrimDecision decision = Calculate(
                isIos: true,
                critical: true,
                availableMiB: 256,
                managedMiB: 384,
                nowMilliseconds: 48_000,
                lastRunMilliseconds: 40_000);

            Assert.That(decision, Is.EqualTo(new VulkanMemoryTrimDecision(false, true, true)));
        }

        [Test]
        public void DesktopBehaviorRemainsTiedToHeavyTrimCadence()
        {
            VulkanMemoryTrimDecision due = Calculate(
                isIos: false,
                critical: true,
                availableMiB: 100,
                managedMiB: 900,
                nowMilliseconds: 70_000,
                lastRunMilliseconds: 40_000);
            VulkanMemoryTrimDecision throttled = Calculate(
                isIos: false,
                critical: true,
                availableMiB: 100,
                managedMiB: 900,
                nowMilliseconds: 69_999,
                lastRunMilliseconds: 40_000);

            Assert.Multiple(() =>
            {
                Assert.That(due, Is.EqualTo(new VulkanMemoryTrimDecision(true, true, true)));
                Assert.That(throttled, Is.EqualTo(new VulkanMemoryTrimDecision(false, false, false)));
            });
        }

        private static VulkanMemoryTrimDecision Calculate(
            bool isIos,
            bool critical,
            ulong availableMiB,
            long managedMiB,
            long nowMilliseconds,
            long lastRunMilliseconds)
        {
            return VulkanMemoryTrimPolicy.Calculate(
                isIos,
                critical,
                availableMiB * MiB,
                managedMiB * (long)MiB,
                nowMilliseconds,
                lastRunMilliseconds,
                lastRunMilliseconds,
                lastRunMilliseconds);
        }
    }
}
