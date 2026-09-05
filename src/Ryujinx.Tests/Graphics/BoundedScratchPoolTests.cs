using NUnit.Framework;
using Ryujinx.Common.Memory;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Ryujinx.Tests.Graphics
{
    public class BoundedScratchPoolTests
    {
        [Test]
        public void ManyDistinctTextureSizesCannotGrowIdlePoolWithoutLimit()
        {
            BoundedArrayPool<byte> pool = new(65536, 16, 32768);
            List<byte[]> leased = new();
            for (int i = 1; i <= 200; i++) leased.Add(pool.Rent(i * 137));
            foreach (byte[] array in leased)
            {
                pool.Return(array);
                MemoryOwnerPoolStatistics stats = pool.GetStatistics();
                Assert.That(stats.RetainedBytes, Is.LessThanOrEqualTo(65536));
                Assert.That(stats.RetainedArrays, Is.LessThanOrEqualTo(16));
            }
            Assert.That(pool.GetStatistics().LeasedBytes, Is.Zero);
            Assert.That(pool.GetStatistics().DiscardedBytes, Is.GreaterThan(0));
        }

        [Test]
        public void TrimDoesNotReclaimArrayHeldByAnInFlightOwner()
        {
            BoundedArrayPool<byte> pool = new(65536, 16, 32768);
            byte[] held = pool.Rent(16384);
            held.AsSpan().Fill(0x7b);
            byte[] idle = pool.Rent(8192);
            pool.Return(idle);
            Assert.That(pool.Trim(0), Is.EqualTo(8192));
            Assert.That(pool.GetStatistics().LeasedBytes, Is.EqualTo(16384));
            Assert.That(held, Is.All.EqualTo((byte)0x7b));
            pool.Return(held);
            Assert.That(pool.GetStatistics().LeasedBytes, Is.Zero);
        }

        [Test]
        public void ReusesClosestFitButNeverLeasesOneArrayToTwoOwners()
        {
            BoundedArrayPool<byte> pool = new(65536, 16, 32768);
            byte[] first = pool.Rent(1024);
            pool.Return(first);
            byte[] reused = pool.Rent(900);
            byte[] other = pool.Rent(900);
            Assert.That(reused, Is.SameAs(first));
            Assert.That(other, Is.Not.SameAs(reused));
            pool.Return(reused);
            pool.Return(other);
        }

        [Test]
        public void LargeOneOffAndDuplicateSizesDoNotRemainRetained()
        {
            BoundedArrayPool<byte> pool = new(16384, 4, 8192);
            byte[] huge = pool.Rent(32768), first = pool.Rent(8192), second = pool.Rent(8192);
            pool.Return(huge);
            pool.Return(first);
            pool.Return(second);
            Assert.That(pool.GetStatistics().RetainedBytes, Is.EqualTo(8192));
            Assert.That(pool.GetStatistics().RetainedArrays, Is.EqualTo(1));
        }

        [Test]
        public void RepeatedSparsePageBurstsReuseEveryReturnedPage()
        {
            BoundedArrayPool<byte> pool = new(64L * 1024 * 1024, 64, 32L * 1024 * 1024);
            const int pageCount = 32;
            const int pageBytes = 4096;
            byte[][] first = Enumerable.Range(0, pageCount)
                .Select(_ => pool.Rent(pageBytes, MemoryOwnerPurpose.Mirror)).ToArray();
            foreach (byte[] page in first) pool.Return(page, MemoryOwnerPurpose.Mirror);
            MemoryOwnerPoolStatistics warm = pool.GetStatistics();

            for (int burst = 0; burst < 3; burst++)
            {
                // All pages are leased at once, as when one upload retires many
                // dirty pages. Sequential rent/return would hide a one-entry pool.
                byte[][] pages = Enumerable.Range(0, pageCount)
                    .Select(_ => pool.Rent(pageBytes, MemoryOwnerPurpose.Mirror)).ToArray();
                Assert.That(pages.Distinct().Count(), Is.EqualTo(pageCount));
                Assert.That(pages.All(page => first.Contains(page)), Is.True);
                for (int i = 0; i < pages.Length; i++) pages[i].AsSpan().Fill((byte)(i + 1));
                for (int i = 0; i < pages.Length; i++)
                {
                    Assert.That(pages[i], Is.All.EqualTo((byte)(i + 1)));
                    pool.Return(pages[i], MemoryOwnerPurpose.Mirror);
                }
            }

            MemoryOwnerPoolStatistics after = pool.GetStatistics();
            Assert.That(after.CreatedArrays, Is.EqualTo(warm.CreatedArrays));
            Assert.That(after.CreatedBytes, Is.EqualTo(warm.CreatedBytes));
            Assert.That(after.Reuses - warm.Reuses, Is.EqualTo(3 * pageCount));
            Assert.That(after.LeasedBytes, Is.Zero);
            Assert.That(after.RetainedBytes, Is.EqualTo(pageCount * pageBytes));
        }

        [TestCase(3 * 4096, 16, 3)] // Byte limit binds before the count limit.
        [TestCase(16 * 4096, 3, 3)] // Count limit binds before the byte limit.
        public void DuplicatePagesRespectBothBudgetsAndTrimPreservesLeasedData(long byteLimit, int arrayLimit, int retainedCount)
        {
            BoundedArrayPool<byte> pool = new(byteLimit, arrayLimit, 4096);
            byte[][] pages = Enumerable.Range(0, 10).Select(_ => pool.Rent(4096)).ToArray();
            byte[] held = pages[0];
            held.AsSpan().Fill(0x7b);
            foreach (byte[] page in pages.Skip(1))
            {
                pool.Return(page);
                Assert.That(pool.GetStatistics().RetainedBytes, Is.LessThanOrEqualTo(byteLimit));
                Assert.That(pool.GetStatistics().RetainedArrays, Is.LessThanOrEqualTo(arrayLimit));
            }

            Assert.That(pool.GetStatistics().RetainedArrays, Is.EqualTo(retainedCount));
            Assert.That(pool.Trim(0), Is.EqualTo(retainedCount * 4096));
            Assert.That(pool.GetStatistics().RetainedBytes, Is.Zero);
            Assert.That(pool.GetStatistics().LeasedBytes, Is.EqualTo(4096));
            Assert.That(held, Is.All.EqualTo((byte)0x7b));
            pool.Return(held);
            Assert.That(pool.GetStatistics().LeasedBytes, Is.Zero);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void SparsePageBurstsCannotEvictWarmLargeConversionArrays(bool countLimited)
        {
            const int arrayLimit = 64;
            const int pageSize = 4096;
            long byteLimit = countLimited ? 64L * 1024 * 1024 : 65536;
            BoundedArrayPool<byte> pool = new(byteLimit, arrayLimit, 32L * 1024 * 1024);
            // Keep these outside the existing 4x best-fit range for a 4-KiB rent,
            // so this workload isolates return-time eviction of idle conversions.
            int[] largeSizes = countLimited
                ? Enumerable.Range(5, 54).Select(pages => pages * pageSize).ToArray()
                : new[] { 32768, 24576 };
            byte[][] large = largeSizes.Select(size => pool.Rent(size, MemoryOwnerPurpose.GuestBridge)).ToArray();
            for (int i = 0; i < large.Length; i++)
            {
                large[i][0] = (byte)(i + 1);
                pool.Return(large[i], MemoryOwnerPurpose.GuestBridge);
            }
            long largeBytes = large.Sum(array => (long)array.Length);
            int smallCapacity = countLimited ? arrayLimit - large.Length : (int)((byteLimit - largeBytes) / pageSize);

            for (int burst = 0; burst < 2; burst++)
            {
                MemoryOwnerPoolStatistics before = pool.GetStatistics(MemoryOwnerPurpose.Mirror);
                byte[][] pages = Enumerable.Range(0, 128)
                    .Select(_ => pool.Rent(pageSize, MemoryOwnerPurpose.Mirror)).ToArray();
                if (burst != 0)
                {
                    Assert.That(pool.GetStatistics(MemoryOwnerPurpose.Mirror).Reuses - before.Reuses, Is.EqualTo(smallCapacity));
                }
                foreach (byte[] page in pages)
                {
                    pool.Return(page, MemoryOwnerPurpose.Mirror);
                    Assert.That(pool.GetStatistics().RetainedBytes, Is.LessThanOrEqualTo(byteLimit));
                    Assert.That(pool.GetStatistics().RetainedArrays, Is.LessThanOrEqualTo(arrayLimit));
                    Assert.That(pool.GetStatistics(MemoryOwnerPurpose.GuestBridge).RetainedBytes, Is.EqualTo(largeBytes));
                }
            }

            Assert.That(pool.GetStatistics(MemoryOwnerPurpose.Mirror).RetainedArrays, Is.EqualTo(smallCapacity));
            Assert.That(pool.GetStatistics(MemoryOwnerPurpose.GuestBridge).DiscardedArrays, Is.Zero);
            MemoryOwnerPoolStatistics warm = pool.GetStatistics();
            for (int i = 0; i < large.Length; i++)
            {
                byte[] reused = pool.Rent(largeSizes[i], MemoryOwnerPurpose.LayoutConvert);
                Assert.That(reused, Is.SameAs(large[i]), "A page burst must not turn the next large conversion into a fresh allocation.");
                Assert.That(reused[0], Is.EqualTo((byte)(i + 1)));
                pool.Return(reused, MemoryOwnerPurpose.LayoutConvert);
            }
            Assert.That(pool.GetStatistics().CreatedArrays, Is.EqualTo(warm.CreatedArrays));
            Assert.That(pool.GetStatistics().LeasedBytes, Is.Zero);
        }

        [Test]
        public void SmallPageIsDiscardedWhenOnlyLargeArraysFillTheBudget()
        {
            BoundedArrayPool<byte> pool = new(65536, 64, 65536);
            byte[] first = pool.Rent(40960, MemoryOwnerPurpose.GuestBridge);
            byte[] second = pool.Rent(24576, MemoryOwnerPurpose.GuestBridge);
            pool.Return(first, MemoryOwnerPurpose.GuestBridge);
            pool.Return(second, MemoryOwnerPurpose.GuestBridge);
            byte[] page = pool.Rent(4096, MemoryOwnerPurpose.Mirror);
            pool.Return(page, MemoryOwnerPurpose.Mirror);

            Assert.That(pool.GetStatistics().RetainedBytes, Is.EqualTo(65536));
            Assert.That(pool.GetStatistics().RetainedArrays, Is.EqualTo(2));
            Assert.That(pool.GetStatistics(MemoryOwnerPurpose.Mirror).DiscardedArrays, Is.EqualTo(1));
            Assert.That(pool.GetStatistics(MemoryOwnerPurpose.GuestBridge).DiscardedArrays, Is.Zero);
            byte[] reusedFirst = pool.Rent(first.Length), reusedSecond = pool.Rent(second.Length);
            Assert.That(reusedFirst, Is.SameAs(first));
            Assert.That(reusedSecond, Is.SameAs(second));
            pool.Return(reusedFirst);
            pool.Return(reusedSecond);
        }

        [Test]
        public void CountAndByteBudgetChargeActualElementSizeAndClearReferences()
        {
            BoundedArrayPool<object> pool = new(1024, 4, 1024);
            object[] array = pool.Rent(16);
            array[0] = new object();
            pool.Return(array);
            Assert.That(pool.GetStatistics().RetainedBytes, Is.EqualTo(16 * IntPtr.Size));
            object[] reused = pool.Rent(16);
            Assert.That(reused, Is.All.Null);
            pool.Return(reused);
            Assert.That(pool.GetStatistics().LeasedBytes, Is.Zero);
        }

        [Test]
        public void ConcurrentRentReturnAndTrimPreserveLiveDataAndAccounting()
        {
            BoundedArrayPool<byte> pool = new(32768, 16, 16384);
            Parallel.For(0, 1000, i =>
            {
                byte[] array = pool.Rent(256 + i % 16 * 64);
                byte value = (byte)(i % 251);
                array.AsSpan().Fill(value);
                if (i % 7 == 0) pool.Trim(8192);
                Assert.That(array.All(v => v == value), Is.True);
                pool.Return(array);
            });
            Assert.That(pool.GetStatistics().LeasedBytes, Is.Zero);
            Assert.That(pool.GetStatistics().RetainedBytes, Is.LessThanOrEqualTo(32768));
        }

        [Test]
        public void PurposeAccountingFollowsActualCapacityAcrossReuseAndTrim()
        {
            BoundedArrayPool<ushort> pool = new(4096, 8, 2048);
            ushort[] decoded = pool.Rent(512, MemoryOwnerPurpose.Decode);
            Assert.That(pool.GetStatistics(MemoryOwnerPurpose.Decode).CreatedBytes, Is.EqualTo(1024));
            pool.Return(decoded, MemoryOwnerPurpose.Decode);

            ushort[] upload = pool.Rent(300, MemoryOwnerPurpose.Upload);
            Assert.That(upload, Is.SameAs(decoded));
            Assert.That(pool.GetStatistics(MemoryOwnerPurpose.Decode).RetainedBytes, Is.Zero);
            MemoryOwnerPoolStatistics active = pool.GetStatistics(MemoryOwnerPurpose.Upload);
            Assert.That(active.LeasedBytes, Is.EqualTo(1024), "Charge capacity, not requested length.");
            Assert.That(active.PeakLeasedBytes, Is.EqualTo(1024));
            Assert.That(active.Reuses, Is.EqualTo(1));
            Assert.That(active.CreatedArrays, Is.Zero);
            pool.Trim(0);
            Assert.That(pool.GetStatistics(MemoryOwnerPurpose.Upload).LeasedBytes, Is.EqualTo(1024));
            pool.Return(upload, MemoryOwnerPurpose.Upload);
            Assert.That(pool.GetStatistics(MemoryOwnerPurpose.Upload).RetainedBytes, Is.EqualTo(1024));
            pool.Trim(0);
            Assert.That(pool.GetStatistics(MemoryOwnerPurpose.Upload).DiscardedBytes, Is.EqualTo(1024));
            Assert.That(pool.GetStatistics(MemoryOwnerPurpose.Upload).DiscardedArrays, Is.EqualTo(1));
            Assert.That(pool.GetStatistics().LeasedBytes, Is.Zero);
            Assert.That(pool.GetStatistics().RetainedBytes, Is.Zero);
        }

        [Test]
        public void ConcurrentPurposeTotalsReconcileAfterOutstandingJobsReturn()
        {
            BoundedArrayPool<byte> pool = new(32768, 16, 16384);
            Parallel.For(0, 1000, i =>
            {
                MemoryOwnerPurpose purpose = (MemoryOwnerPurpose)(i % (int)MemoryOwnerPurpose.Count);
                byte[] array = pool.Rent(256 + i % 16 * 64, purpose);
                array.AsSpan().Fill((byte)i);
                if (i % 7 == 0) pool.Trim(4096);
                Assert.That(array[0], Is.EqualTo((byte)i));
                pool.Return(array, purpose);
            });

            MemoryOwnerPoolStatistics total = pool.GetStatistics();
            MemoryOwnerPoolStatistics[] purposes = Enumerable.Range(0, (int)MemoryOwnerPurpose.Count)
                .Select(i => pool.GetStatistics((MemoryOwnerPurpose)i)).ToArray();
            Assert.That(purposes.Sum(p => p.LeasedBytes), Is.Zero);
            Assert.That(purposes.Sum(p => p.RetainedBytes), Is.EqualTo(total.RetainedBytes));
            Assert.That(purposes.Sum(p => p.Rents), Is.EqualTo(total.Rents));
            Assert.That(purposes.Sum(p => p.Reuses), Is.EqualTo(total.Reuses));
            Assert.That(purposes.Sum(p => p.CreatedBytes), Is.EqualTo(total.CreatedBytes));
            Assert.That(purposes.Sum(p => p.CreatedArrays), Is.EqualTo(total.CreatedArrays));
            Assert.That(purposes.Sum(p => p.DiscardedBytes), Is.EqualTo(total.DiscardedBytes));
            Assert.That(purposes.Sum(p => p.DiscardedArrays), Is.EqualTo(total.DiscardedArrays));
            Assert.That(total.CreatedBytes - total.DiscardedBytes, Is.EqualTo(total.RetainedBytes));
        }

        [Test]
        public void PurposeSurvivesOwnershipTransferAndDoubleDispose()
        {
            MemoryOwnerPoolStatistics before = MemoryOwner<long>.GetPoolStatistics(MemoryOwnerPurpose.GuestBridge);
            MemoryOwner<long> owner = MemoryOwner<long>.RentCopy(new long[] { 1, 2, 3 }, MemoryOwnerPurpose.GuestBridge);
            Task.Run(() =>
            {
                Assert.That(owner.Span[2], Is.EqualTo(3));
                owner.Dispose();
                owner.Dispose();
            }).GetAwaiter().GetResult();
            MemoryOwnerPoolStatistics after = MemoryOwner<long>.GetPoolStatistics(MemoryOwnerPurpose.GuestBridge);
            Assert.That(after.Rents - before.Rents, Is.EqualTo(1));
            Assert.That(after.LeasedBytes, Is.EqualTo(before.LeasedBytes));
        }

        [Test]
        public void InvalidPurposeDoesNotMutatePoolAndEmptyRentCreatesNoArray()
        {
            BoundedArrayPool<byte> pool = new(4096, 8, 2048);
            Assert.Throws<ArgumentOutOfRangeException>(() => pool.Rent(32, MemoryOwnerPurpose.Count));
            Assert.That(pool.GetStatistics().Rents, Is.Zero);
            byte[] empty = pool.Rent(0, MemoryOwnerPurpose.Readback);
            pool.Return(empty, MemoryOwnerPurpose.Readback);
            Assert.That(pool.GetStatistics().CreatedArrays, Is.Zero);
            Assert.That(pool.GetStatistics().DiscardedArrays, Is.Zero);
            Assert.That(pool.GetStatistics().LeasedBytes, Is.Zero);
        }

        [Test]
        public void MemoryOwnerDisposeIsIdempotentAndDisposedAccessIsRejected()
        {
            MemoryOwner<byte> owner = MemoryOwner<byte>.RentCleared(127);
            Assert.That(owner.Span.ToArray(), Is.All.Zero);
            owner.Dispose();
            owner.Dispose();
            Assert.Throws<ObjectDisposedException>(() => { _ = owner.Memory; });
            Assert.Throws<ArgumentOutOfRangeException>(() => MemoryOwner<byte>.Rent(-1));
        }
    }
}
