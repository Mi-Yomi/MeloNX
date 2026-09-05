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
            BoundedArrayPool<byte> pool = new(4096, 4, 2048);
            byte[] huge = pool.Rent(8192), first = pool.Rent(1024), second = pool.Rent(1024);
            pool.Return(huge);
            pool.Return(first);
            pool.Return(second);
            Assert.That(pool.GetStatistics().RetainedBytes, Is.EqualTo(1024));
            Assert.That(pool.GetStatistics().RetainedArrays, Is.EqualTo(1));
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
