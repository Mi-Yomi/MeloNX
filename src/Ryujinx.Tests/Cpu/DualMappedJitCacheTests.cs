using NUnit.Framework;
using Ryujinx.Cpu.LightningJit.Cache;
using System;
using System.Collections.Generic;

namespace Ryujinx.Tests.Cpu
{
    class DualMappedJitCacheTests
    {
        [TestCase(null, true, 512)]
        [TestCase(null, false, 1024)]
        [TestCase("", true, 512)]
        [TestCase(" ", false, 1024)]
        public void UnsetOverridePreservesPlatformDefault(string value, bool hasTxm, int expectedSizeMiB)
        {
            DualMappedJitCacheConfiguration configuration = DualMappedJitCacheConfiguration.Resolve(value, hasTxm);

            Assert.AreEqual(expectedSizeMiB, configuration.SizeMiB);
            Assert.IsFalse(configuration.IsOverride);
            Assert.IsFalse(configuration.InvalidOverride);
        }

        [TestCase("512", 512)]
        [TestCase("768", 768)]
        [TestCase("1024", 1024)]
        [TestCase(" 768 ", 768)]
        public void ExplicitOverrideIsTheSameOnBothMappingPaths(string value, int expectedSizeMiB)
        {
            foreach (bool hasTxm in new[] { true, false })
            {
                DualMappedJitCacheConfiguration configuration = DualMappedJitCacheConfiguration.Resolve(value, hasTxm);

                Assert.AreEqual(expectedSizeMiB * 1024 * 1024, configuration.CapacityBytes);
                Assert.IsTrue(configuration.IsOverride);
                Assert.IsFalse(configuration.InvalidOverride);
            }
        }

        [TestCase("0")]
        [TestCase("-1")]
        [TestCase("256")]
        [TestCase("2048")]
        [TestCase("512.0")]
        [TestCase("512MiB")]
        [TestCase("999999999999999999999")]
        public void InvalidOverrideFallsBackAndRequestsWarning(string value)
        {
            foreach (bool hasTxm in new[] { true, false })
            {
                DualMappedJitCacheConfiguration configuration = DualMappedJitCacheConfiguration.Resolve(value, hasTxm);

                Assert.AreEqual(hasTxm ? 512 : 1024, configuration.SizeMiB);
                Assert.IsFalse(configuration.IsOverride);
                Assert.IsTrue(configuration.InvalidOverride);
            }
        }

        [Test]
        public void AlignmentGapsRemainReusableAndDoNotInflateUsedBytes()
        {
            SharedJitCacheAllocator allocator = new(64);

            Assert.AreEqual(0, allocator.Allocate(5));
            Assert.AreEqual(16, allocator.AllocateAligned(16, 16));
            Assert.AreEqual(24, allocator.UsedBytes);
            Assert.AreEqual(32, allocator.AddressHighWaterBytes);

            Assert.AreEqual(8, allocator.Allocate(8));
            Assert.AreEqual(32, allocator.UsedBytes);
            Assert.AreEqual(32, allocator.AddressHighWaterBytes);
            Assert.AreEqual(32, allocator.AllocateAligned(32, 16));
            Assert.AreEqual(64, allocator.UsedBytes);
            Assert.AreEqual(0, allocator.FreeBytes);

            OutOfMemoryException error = Assert.Throws<OutOfMemoryException>(() => allocator.Allocate(1));
            StringAssert.Contains("requested=1 bytes, aligned=4 bytes", error.Message);
            StringAssert.Contains("capacity=64 bytes, used=64 bytes", error.Message);
            Assert.AreEqual(64, allocator.UsedBytes);
        }

        [Test]
        public void FragmentedFreeSpaceReportsAlignedFailureWithoutConsumingMemory()
        {
            SharedJitCacheAllocator allocator = new(64);
            allocator.Allocate(4);
            allocator.AllocateAligned(32, 32);

            OutOfMemoryException error = Assert.Throws<OutOfMemoryException>(() => allocator.AllocateAligned(16, 32));
            StringAssert.Contains("requested=16 bytes", error.Message);
            StringAssert.Contains("alignment=32", error.Message);
            StringAssert.Contains("capacity=64 bytes, used=36 bytes, free=28 bytes", error.Message);
            Assert.AreEqual(36, allocator.UsedBytes);
            Assert.AreEqual(64, allocator.AddressHighWaterBytes);
            Assert.AreEqual(4, allocator.Allocate(28));
            Assert.AreEqual(64, allocator.UsedBytes);
        }

        [Test]
        public void ThresholdsFireOnceAtTheirBoundaries()
        {
            List<int> thresholds = [];
            SharedJitCacheAllocator allocator = new(400, (threshold, _) => thresholds.Add(threshold));

            allocator.Allocate(296);
            CollectionAssert.AreEqual(new[] { 10, 25, 50 }, thresholds);
            allocator.Allocate(4);
            CollectionAssert.AreEqual(new[] { 10, 25, 50, 75 }, thresholds);
            allocator.Allocate(60);
            CollectionAssert.AreEqual(new[] { 10, 25, 50, 75, 90 }, thresholds);
            allocator.Allocate(20);
            allocator.Allocate(20);
            CollectionAssert.AreEqual(new[] { 10, 25, 50, 75, 90, 95 }, thresholds);
            Assert.AreEqual(400, allocator.UsedBytes);
            Assert.AreEqual(400, allocator.AddressHighWaterBytes);
        }

        [Test]
        public void AJumpPastAllThresholdsReportsEachOnce()
        {
            List<int> thresholds = [];
            SharedJitCacheAllocator allocator = new(100, (threshold, _) => thresholds.Add(threshold));

            allocator.Allocate(96);
            allocator.Allocate(4);

            CollectionAssert.AreEqual(new[] { 10, 25, 50, 75, 90, 95 }, thresholds);
            Assert.Throws<OutOfMemoryException>(() => allocator.Allocate(int.MaxValue));
            Assert.AreEqual(100, allocator.UsedBytes);
            CollectionAssert.AreEqual(new[] { 10, 25, 50, 75, 90, 95 }, thresholds);
        }

        [Test]
        public void ProcessWideDiagnosticsExposeCurrentAllocatorUsage()
        {
            SharedJitCacheAllocator allocator = new(100);
            DualMappedJitCacheDiagnostics.Register(allocator);

            try
            {
                allocator.Allocate(28);

                Assert.That(DualMappedJitCacheDiagnostics.TryGetUsage(out DualMappedJitCacheUsage usage), Is.True);
                Assert.Multiple(() =>
                {
                    Assert.That(usage.CapacityBytes, Is.EqualTo(100));
                    Assert.That(usage.UsedBytes, Is.EqualTo(28));
                    Assert.That(usage.FreeBytes, Is.EqualTo(72));
                    Assert.That(usage.AddressHighWaterBytes, Is.EqualTo(28));
                });
            }
            finally
            {
                DualMappedJitCacheDiagnostics.Unregister(allocator);
            }

            Assert.That(DualMappedJitCacheDiagnostics.TryGetUsage(out DualMappedJitCacheUsage unavailableUsage), Is.False);
            Assert.That(unavailableUsage, Is.EqualTo(default(DualMappedJitCacheUsage)));
        }

        [Test]
        public void StaleSessionCannotUnregisterNewDiagnosticsAllocator()
        {
            SharedJitCacheAllocator oldAllocator = new(100);
            SharedJitCacheAllocator newAllocator = new(200);
            newAllocator.Allocate(40);

            DualMappedJitCacheDiagnostics.Register(oldAllocator);
            DualMappedJitCacheDiagnostics.Register(newAllocator);
            DualMappedJitCacheDiagnostics.Unregister(oldAllocator);

            try
            {
                Assert.That(DualMappedJitCacheDiagnostics.TryGetUsage(out DualMappedJitCacheUsage usage), Is.True);
                Assert.Multiple(() =>
                {
                    Assert.That(usage.CapacityBytes, Is.EqualTo(200));
                    Assert.That(usage.UsedBytes, Is.EqualTo(40));
                });
            }
            finally
            {
                DualMappedJitCacheDiagnostics.Unregister(newAllocator);
            }
        }
    }
}
