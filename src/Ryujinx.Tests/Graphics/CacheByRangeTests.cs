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
