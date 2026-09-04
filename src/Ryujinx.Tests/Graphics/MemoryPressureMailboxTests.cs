using NUnit.Framework;
using Ryujinx.Graphics.Gpu.Memory;
using System.Threading.Tasks;

namespace Ryujinx.Tests.Graphics
{
    public class MemoryPressureMailboxTests
    {
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

        [TestCase(0, 1)]
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

        [TestCase(256UL, (int)MemoryPressureSeverity.Low, 128UL)]
        [TestCase(257UL, (int)MemoryPressureSeverity.Low, 128UL)]
        [TestCase(256UL, (int)MemoryPressureSeverity.Critical, 0UL)]
        public void CalculatesTemporaryBufferTarget(ulong capacity, int severity, ulong expected)
        {
            Assert.That(MemoryPressureTrimPolicy.CalculateBufferTarget(capacity, (MemoryPressureSeverity)severity), Is.EqualTo(expected));
        }
    }
}
