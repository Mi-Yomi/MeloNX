using NUnit.Framework;
using Ryujinx.Graphics.Gpu.Memory;

namespace Ryujinx.Tests.Graphics
{
    public class MemoryPressureRecoveryTests
    {
        private const ulong MiB = 1024 * 1024;

        [Test]
        public void ObservationCannotHideQueuedCriticalAndLatestObservationWinsOtherwise()
        {
            MemoryPressureMailbox box = new();
            box.Report(100 * MiB, 0, 1);
            box.Report(900 * MiB, 0, 1);
            Assert.That(box.TryConsume(out MemoryPressureRequest observation), Is.True);
            Assert.That(observation.Severity, Is.EqualTo(MemoryPressureSeverity.Observe));
            Assert.That(observation.AvailableMemoryBytes, Is.EqualTo(900 * MiB));
            box.Report(200 * MiB, 2, 2);
            box.Report(1500 * MiB, 0, 1);
            Assert.That(box.TryConsume(out MemoryPressureRequest pressure), Is.True);
            Assert.That(pressure.Severity, Is.EqualTo(MemoryPressureSeverity.Critical));
            Assert.That(pressure.AvailableMemoryBytes, Is.EqualTo(200 * MiB));
            box.StopAccepting();
            Assert.That(box.Report(2000 * MiB, 0, 1), Is.False);
        }

        [Test]
        public void HealthyObservationsRecoverOnlyAfterTwentyContinuousSeconds()
        {
            RecoverableMemoryPressureCapacity capacity = new(128 * MiB);
            capacity.Latch(64 * MiB);
            for (long t = 0; t < 20000; t += 2000)
                Assert.That(capacity.ObserveHeadroom(800 * MiB, t), Is.False);
            Assert.That(capacity.ObserveHeadroom(800 * MiB, 20000), Is.True);
            Assert.That(capacity.EffectiveCapacity, Is.EqualTo(128 * MiB));
            Assert.That(capacity.IsLatched, Is.False);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void InterruptedHealthyPeriodDoesNotRecover(bool lowMemory)
        {
            RecoverableMemoryPressureCapacity capacity = new(128 * MiB);
            capacity.Latch(64 * MiB);
            for (long t = 0; t <= 18000; t += 2000) capacity.ObserveHeadroom(800 * MiB, t);
            if (lowMemory) capacity.ObserveHeadroom(700 * MiB, 19000);
            Assert.That(capacity.ObserveHeadroom(800 * MiB, lowMemory ? 20000 : 30000), Is.False);
            Assert.That(capacity.EffectiveCapacity, Is.EqualTo(64 * MiB));
        }

        [Test]
        public void EmergencyRecoversOneTierAndNeverExceedsConfiguration()
        {
            RecoverableMemoryPressureCapacity capacity = new(128 * MiB);
            capacity.Latch(32 * MiB);
            for (long t = 0; t <= 20000; t += 2000) capacity.ObserveHeadroom(600 * MiB, t);
            Assert.That(capacity.EffectiveCapacity, Is.EqualTo(64 * MiB));
            for (long t = 22000; t <= 50000; t += 2000) capacity.ObserveHeadroom(600 * MiB, t);
            Assert.That(capacity.EffectiveCapacity, Is.EqualTo(64 * MiB));
            capacity.Configure(48 * MiB);
            for (long t = 52000; t <= 74000; t += 2000) capacity.ObserveHeadroom(900 * MiB, t);
            Assert.That(capacity.EffectiveCapacity, Is.EqualTo(48 * MiB));
        }

        [TestCase(0UL)]
        [TestCase(128UL)]
        [TestCase(1000UL)]
        public void ObservationNeverSelectsEvictionOrPersistentReduction(ulong availableMiB)
        {
            Assert.That(MemoryPressureTrimPolicy.CalculateBufferTarget(128 * MiB, MemoryPressureSeverity.Observe, availableMiB * MiB), Is.EqualTo(128 * MiB));
            Assert.That(MemoryPressureTrimPolicy.CalculatePersistentBufferCapacity(128 * MiB, MemoryPressureSeverity.Observe, availableMiB * MiB), Is.Null);
        }
    }
}
