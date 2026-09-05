using NUnit.Framework;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Gpu.Memory;
using System.Collections.Generic;

namespace Ryujinx.Tests.Graphics
{
    public class CounterCacheTests
    {
        private sealed class CounterEvent : ICounterEvent
        {
            public bool Invalid { get; set; }
            public int Flushes { get; private set; }
            public int Disposals { get; private set; }
            public List<ulong> Writes { get; } = [];
            public bool ReserveForHostAccess() => !Invalid;
            public void Dispose() => Disposals++;
            public void Flush() => Flushes++;

            public void Complete(ulong result)
            {
                // SemaphoreUpdater checks this flag before its delayed callback
                // writes the query value into guest memory.
                if (!Invalid) Writes.Add(result);
            }
        }

        [Test]
        public void LowestAddressEventIsFoundAndFlushedIncludingAfterReplacement()
        {
            CounterCache cache = new();
            CounterEvent first = new(), replacement = new(), higher = new();
            cache.AddOrUpdate(0x200, higher);
            cache.AddOrUpdate(0x100, first);
            Assert.That(cache.FindEvent(0x100), Is.SameAs(first));
            Assert.That(cache.FindAndFlush(0x100), Is.True);
            Assert.That(first.Flushes, Is.EqualTo(1));
            cache.AddOrUpdate(0x100, replacement);
            Assert.That(cache.FindEvent(0x100), Is.SameAs(replacement));
            Assert.That(cache.FindAndFlush(0x100), Is.True);
            Assert.That(replacement.Flushes, Is.EqualTo(1));
            Assert.That(first.Flushes, Is.EqualTo(1));
            Assert.That(higher.Flushes, Is.Zero);
        }

        [Test]
        public void MissingAddressDiffersFromCachedImmediatePayload()
        {
            CounterCache cache = new();
            cache.AddOrUpdate(0x100, null);
            Assert.That(cache.Contains(0x100), Is.True);
            Assert.That(cache.FindEvent(0x100), Is.Null);
            Assert.That(cache.FindAndFlush(0x100), Is.True);
            Assert.That(cache.Contains(0x101), Is.False);
            Assert.That(cache.FindEvent(0x101), Is.Null);
            Assert.That(cache.FindAndFlush(0x101), Is.False);
        }

        [Test]
        public void UnmapEndBetweenEntriesInvalidatesOnlyCoveredCallbacks()
        {
            CounterCache cache = new();
            CounterEvent before = new(), coveredFirst = new(), coveredLast = new(), after = new();
            cache.AddOrUpdate(0x100, before);
            cache.AddOrUpdate(0x120, coveredFirst);
            cache.AddOrUpdate(0x180, coveredLast);
            cache.AddOrUpdate(0x200, after);

            cache.MemoryUnmappedHandler(null, new UnmapEventArgs(0x110, 0x80));

            Assert.That(cache.Contains(0x120), Is.False);
            Assert.That(cache.Contains(0x180), Is.False);
            Assert.That(cache.FindEvent(0x100), Is.SameAs(before));
            Assert.That(cache.FindEvent(0x200), Is.SameAs(after));
            Assert.That(coveredFirst.Invalid, Is.True);
            Assert.That(coveredLast.Invalid, Is.True);
            Assert.That(before.Invalid, Is.False);
            Assert.That(after.Invalid, Is.False);
            // Unmapping cancels delayed writes; it must not wait for GPU work or
            // release native events that the renderer still owns.
            Assert.That(coveredFirst.Flushes + coveredLast.Flushes, Is.Zero);
            Assert.That(coveredFirst.Disposals + coveredLast.Disposals, Is.Zero);
            coveredFirst.Complete(1);
            coveredLast.Complete(2);
            before.Complete(3);
            after.Complete(4);
            Assert.That(coveredFirst.Writes, Is.Empty);
            Assert.That(coveredLast.Writes, Is.Empty);
            Assert.That(before.Writes, Is.EqualTo(new ulong[] { 3 }));
            Assert.That(after.Writes, Is.EqualTo(new ulong[] { 4 }));
        }

        [Test]
        public void UnmapBeyondLastEntryRemovesCountersAndAllowsFreshMappingAtSameAddress()
        {
            CounterCache cache = new();
            CounterEvent old = new(), replacement = new();
            cache.AddOrUpdate(0x100, old);
            cache.MemoryUnmappedHandler(null, new UnmapEventArgs(0, 0x1000));
            Assert.That(cache.Contains(0x100), Is.False);
            Assert.That(old.Invalid, Is.True);
            cache.AddOrUpdate(0x100, replacement);
            old.Complete(7);
            replacement.Complete(8);
            Assert.That(cache.FindEvent(0x100), Is.SameAs(replacement));
            Assert.That(old.Writes, Is.Empty);
            Assert.That(replacement.Writes, Is.EqualTo(new ulong[] { 8 }));
        }

        [TestCase(0UL)]
        [TestCase(ulong.MaxValue)]
        public void EmptyRangeNeverInvalidatesCounters(ulong start)
        {
            CounterCache cache = new();
            CounterEvent low = new(), high = new();
            cache.AddOrUpdate(0, low);
            cache.AddOrUpdate(ulong.MaxValue, high);
            cache.MemoryUnmappedHandler(null, new UnmapEventArgs(start, 0));
            Assert.That(cache.FindEvent(0), Is.SameAs(low));
            Assert.That(cache.FindEvent(ulong.MaxValue), Is.SameAs(high));
            Assert.That(low.Invalid || high.Invalid, Is.False);
        }

        [TestCase(1UL)]
        [TestCase(64UL)]
        public void RangeAtAddressLimitDoesNotWrapToLowAddresses(ulong size)
        {
            CounterCache cache = new();
            CounterEvent low = new(), before = new(), high = new();
            cache.AddOrUpdate(0, low);
            cache.AddOrUpdate(ulong.MaxValue - 1, before);
            cache.AddOrUpdate(ulong.MaxValue, high);
            cache.MemoryUnmappedHandler(null, new UnmapEventArgs(ulong.MaxValue, size));
            Assert.That(high.Invalid, Is.True);
            Assert.That(cache.Contains(ulong.MaxValue), Is.False);
            Assert.That(low.Invalid || before.Invalid, Is.False);
            Assert.That(cache.FindEvent(0), Is.SameAs(low));
            Assert.That(cache.FindEvent(ulong.MaxValue - 1), Is.SameAs(before));
        }

        [Test]
        public void EmptyCacheAndNonoverlappingRangesAreSafe()
        {
            CounterCache cache = new();
            cache.MemoryUnmappedHandler(null, new UnmapEventArgs(0x100, 0x100));
            CounterEvent counter = new();
            cache.AddOrUpdate(0x100, counter);
            cache.MemoryUnmappedHandler(null, new UnmapEventArgs(0, 0x100));
            cache.MemoryUnmappedHandler(null, new UnmapEventArgs(0x101, 0x100));
            Assert.That(cache.FindEvent(0x100), Is.SameAs(counter));
            Assert.That(counter.Invalid, Is.False);
        }
    }
}
