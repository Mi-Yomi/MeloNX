using NUnit.Framework;
using Ryujinx.Graphics.Vulkan;
using System;

namespace Ryujinx.Tests.Graphics
{
    public class CacheByRangeTests
    {
        private sealed class TestCacheKey : ICacheKey
        {
            private readonly Action _onDispose;

            public int DisposeCount { get; private set; }

            public TestCacheKey(Action onDispose = null)
            {
                _onDispose = onDispose;
            }

            public bool KeyEqual(ICacheKey other)
            {
                return ReferenceEquals(this, other);
            }

            public void Dispose()
            {
                DisposeCount++;
                _onDispose?.Invoke();
            }
        }

        private sealed class TestValue : IDisposable
        {
            public int DisposeCount { get; private set; }

            public void Dispose()
            {
                DisposeCount++;
            }
        }

        [Test]
        public void MissingLookupsDoNotRetainAnEmptyRangePerGuestAddress()
        {
            CacheByRange<TestValue> cache = new();
            TestCacheKey presentKey = new();
            TestCacheKey absentKey = new();
            TestValue value = new();
            cache.Add(0, 4, presentKey, value);

            // Warm the lookup and assertion paths before measuring. A read miss must not
            // allocate/retain a dictionary entry and List for each streamed guest range.
            cache.TryGetValue(4, 4, absentKey, out _);
            long before = GC.GetAllocatedBytesForCurrentThread();
            bool found = false;
            for (int i = 1; i <= 20_000; i++)
                found |= cache.TryGetValue(i * 4, 4, absentKey, out _);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.That(found, Is.False);
            Assert.That(allocated, Is.Zero, "Read misses must not create retained empty ranges.");
            Assert.That(cache.TryGetValue(0, 4, presentKey, out TestValue stillPresent), Is.True);
            Assert.That(stillPresent, Is.SameAs(value));
            cache.Clear();
            Assert.That(value.DisposeCount, Is.EqualTo(1));
        }

        [Test]
        public void MissingRemovalDoesNotAllocateOrDisposeLiveEntries()
        {
            CacheByRange<TestValue> cache = new();
            TestCacheKey presentKey = new();
            TestCacheKey absentKey = new();
            TestValue value = new();
            cache.Add(0, 4, presentKey, value);
            cache.Remove(4, 4, absentKey);

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 1; i <= 20_000; i++)
                cache.Remove(i * 4, 4, absentKey);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.That(allocated, Is.Zero);
            Assert.That(value.DisposeCount, Is.Zero);
            cache.Clear();
            Assert.That(value.DisposeCount, Is.EqualTo(1));
        }

        [Test]
        public void DependencyForAbsentOwnerDoesNotCreatePhantomRanges()
        {
            CacheByRange<TestValue> cache = new();
            TestCacheKey presentKey = new();
            TestCacheKey absentKey = new();
            TestValue value = new();
            cache.Add(0, 4, presentKey, value);
            cache.AddDependency(4, 4, absentKey, default);

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 1; i <= 20_000; i++)
                cache.AddDependency(i * 4, 4, absentKey, default);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.That(allocated, Is.Zero);
            Assert.That(cache.TryGetValue(0, 4, presentKey, out TestValue stillPresent), Is.True);
            Assert.That(stillPresent, Is.SameAs(value));
            cache.Clear();
            Assert.That(value.DisposeCount, Is.EqualTo(1));
        }

        [Test]
        public void ClearToleratesEntryDisposalReenteringTheCache()
        {
            CacheByRange<TestValue> cache = new();
            TestCacheKey secondKey = new();
            TestValue firstValue = new();
            TestValue secondValue = new();
            bool reentered = false;

            TestCacheKey firstKey = new(() =>
            {
                reentered = true;
                cache.Remove(16, 4, secondKey);
            });

            cache.Add(0, 4, firstKey, firstValue);
            cache.Add(16, 4, secondKey, secondValue);

            Assert.DoesNotThrow(() => cache.Clear());
            Assert.Multiple(() =>
            {
                Assert.That(reentered, Is.True);
                Assert.That(firstKey.DisposeCount, Is.EqualTo(1));
                Assert.That(secondKey.DisposeCount, Is.EqualTo(1));
                Assert.That(firstValue.DisposeCount, Is.EqualTo(1));
                Assert.That(secondValue.DisposeCount, Is.EqualTo(1));
            });
        }
    }
}
