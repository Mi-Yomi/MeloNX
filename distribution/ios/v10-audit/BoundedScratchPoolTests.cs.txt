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
