using NUnit.Framework;
using Ryujinx.Graphics.Gpu.Memory;
using System.Threading.Tasks;

namespace Ryujinx.Tests.Graphics
{
    public class MemoryPressureMailboxTests
    {
        private const ulong MiB = 1024 * 1024;

        [Test]
        public void CoalescesStrongestSeveritySourcesAndLowestAvailableMemory()
        {
            MemoryPressureMailbox mailbox = new();

            Assert.That(mailbox.Report(900, (int)MemoryPressureSeverity.Low, (int)MemoryPressureSource.Sample), Is.True);
            Assert.That(mailbox.Report(1200, (int)MemoryPressureSeverity.Critical, (int)MemoryPressureSource.UIKitWarning), Is.True);
            Assert.That(mailbox.Report(300, (int)MemoryPressureSeverity.Low, (int)MemoryPressureSource.Sample), Is.True);

            Assert.That(mailbox.TryConsume(out MemoryPressureRequest request), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(request.Severity, Is.EqualTo(MemoryPressureSeverity.Critical));
                Assert.That(request.Sources, Is.EqualTo(MemoryPressureSource.Sample | MemoryPressureSource.UIKitWarning));
                Assert.That(request.AvailableMemoryBytes, Is.EqualTo(300));
            });
            Assert.That(mailbox.TryConsume(out _), Is.False);
        }

        [TestCase(-1, 1)]
        [TestCase(3, 1)]
        [TestCase(1, 0)]
        [TestCase(1, 3)]
        public void RejectsUnknownAbiValues(int severity, int source)
        {
            MemoryPressureMailbox mailbox = new();

            Assert.That(mailbox.Report(100, severity, source), Is.False);
            Assert.That(mailbox.TryConsume(out _), Is.False);
        }

        [Test]
        public void StopAcceptingRejectsNewReportsAndDropsPendingRequest()
        {
            MemoryPressureMailbox mailbox = new();

            Assert.That(mailbox.Report(100, (int)MemoryPressureSeverity.Low, (int)MemoryPressureSource.Sample), Is.True);

            mailbox.StopAccepting();

            Assert.That(mailbox.Report(50, (int)MemoryPressureSeverity.Critical, (int)MemoryPressureSource.UIKitWarning), Is.False);
            Assert.That(mailbox.TryConsume(out _), Is.False);
        }

        [Test]
        public void AcceptsConcurrentReportsWithoutLosingTheStrongestRequest()
        {
            MemoryPressureMailbox mailbox = new();

            Parallel.For(0, 1000, index =>
            {
                int severity = index == 777 ? (int)MemoryPressureSeverity.Critical : (int)MemoryPressureSeverity.Low;
                int source = index % 2 == 0 ? (int)MemoryPressureSource.Sample : (int)MemoryPressureSource.UIKitWarning;
                mailbox.Report((ulong)(2000 - index), severity, source);
            });

            Assert.That(mailbox.TryConsume(out MemoryPressureRequest request), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(request.Severity, Is.EqualTo(MemoryPressureSeverity.Critical));
                Assert.That(request.Sources, Is.EqualTo(MemoryPressureSource.Sample | MemoryPressureSource.UIKitWarning));
                Assert.That(request.AvailableMemoryBytes, Is.EqualTo(1001));
            });
        }

        [TestCase(256UL, (int)MemoryPressureSeverity.Low, 256UL)]
        [TestCase(257UL, (int)MemoryPressureSeverity.Low, 257UL)]
        [TestCase(256UL, (int)MemoryPressureSeverity.Critical, 256UL)]
        public void CalculatesTemporaryBufferTarget(ulong capacity, int severity, ulong expected)
        {
            Assert.That(MemoryPressureTrimPolicy.CalculateBufferTarget(capacity, (MemoryPressureSeverity)severity, 1000 * MiB), Is.EqualTo(expected));
        }

        [TestCase(0UL, 32UL)]
        [TestCase(133UL, 32UL)]
        [TestCase(256UL, 32UL)]
        [TestCase(512UL, 64UL)]
        [TestCase(1024UL, 128UL)]
        [TestCase(1229UL, 128UL)]
        public void RepeatedCriticalSamplesNeverEmptyHotSet(ulong availableMiB, ulong expectedMiB)
        {
            for (int i = 0; i < 100; i++)
                Assert.That(MemoryPressureTrimPolicy.CalculateBufferTarget(128 * MiB,
                    MemoryPressureSeverity.Critical, availableMiB * MiB), Is.EqualTo(expectedMiB * MiB));
        }

        [Test]
        public void UIKitWarningWithProcessHeadroomDoesNotLatch()
        {
            Assert.That(MemoryPressureTrimPolicy.CalculatePersistentBufferCapacity(128 * MiB,
                MemoryPressureSeverity.Critical, 1229 * MiB), Is.Null);
        }

        [Test]
        public void LowPressureDoesNotLatchPersistentBufferCapacity()
        {
            ulong? target = MemoryPressureTrimPolicy.CalculatePersistentBufferCapacity(
                64 * MiB,
                MemoryPressureSeverity.Low,
                1 * MiB);

            Assert.That(target, Is.Null);
        }

        [TestCase(257UL, 32UL)]
        [TestCase(256UL, 16UL)]
        [TestCase(1UL, 16UL)]
        public void CriticalPressureSelectsStagedPersistentBufferCapacity(ulong availableMiB, ulong expectedMiB)
        {
            ulong? target = MemoryPressureTrimPolicy.CalculatePersistentBufferCapacity(
                64 * MiB,
                MemoryPressureSeverity.Critical,
                availableMiB * MiB);

            Assert.That(target, Is.EqualTo(expectedMiB * MiB));
        }

        [Test]
        public void PersistentCapacityCanOnlyTightenAndConfigurationDoesNotResetIt()
        {
            RecoverableMemoryPressureCapacity capacity = new(64 * MiB);

            Assert.Multiple(() =>
            {
                Assert.That(capacity.IsLatched, Is.False);
                Assert.That(capacity.ConfiguredCapacity, Is.EqualTo(64 * MiB));
                Assert.That(capacity.EffectiveCapacity, Is.EqualTo(64 * MiB));
            });

            bool firstLatchChanged = capacity.Latch(32 * MiB);
            bool attemptedRaiseChanged = capacity.Latch(64 * MiB);
            capacity.Configure(128 * MiB);

            Assert.Multiple(() =>
            {
                Assert.That(firstLatchChanged, Is.True);
                Assert.That(attemptedRaiseChanged, Is.False);
                Assert.That(capacity.IsLatched, Is.True);
                Assert.That(capacity.ConfiguredCapacity, Is.EqualTo(128 * MiB));
                Assert.That(capacity.EffectiveCapacity, Is.EqualTo(32 * MiB));
            });

            Assert.That(capacity.Latch(0), Is.True);
            capacity.Configure(256 * MiB);
            Assert.That(capacity.EffectiveCapacity, Is.Zero);
        }

        [Test]
        public void LowerConfiguredCapacityPermanentlyTightensLatchedCapacity()
        {
            RecoverableMemoryPressureCapacity capacity = new(64 * MiB);
            capacity.Latch(32 * MiB);

            capacity.Configure(16 * MiB);
            capacity.Configure(128 * MiB);

            Assert.That(capacity.EffectiveCapacity, Is.EqualTo(16 * MiB));
        }

        [Test]
        public void NewPersistentCapacityStartsUnlatched()
        {
            RecoverableMemoryPressureCapacity oldSession = new(64 * MiB);
            oldSession.Latch(0);

            RecoverableMemoryPressureCapacity newSession = new(64 * MiB);

            Assert.Multiple(() =>
            {
                Assert.That(oldSession.EffectiveCapacity, Is.Zero);
                Assert.That(newSession.IsLatched, Is.False);
                Assert.That(newSession.EffectiveCapacity, Is.EqualTo(64 * MiB));
            });
        }

        [TestCase(256UL, (int)MemoryPressureSeverity.Low, 128UL)]
        [TestCase(257UL, (int)MemoryPressureSeverity.Low, 128UL)]
        [TestCase(256UL, (int)MemoryPressureSeverity.Critical, 0UL)]
        public void CalculatesTemporaryTextureTarget(ulong capacity, int severity, ulong expected)
        {
            Assert.That(MemoryPressureTrimPolicy.CalculateTextureTarget(capacity, (MemoryPressureSeverity)severity), Is.EqualTo(expected));
        }
    }
}
