using NUnit.Framework;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Gpu.Memory;
using Ryujinx.Memory.Range;
using System.Collections.Generic;

namespace Ryujinx.Tests.Graphics
{
    public class CacheEvictionPolicyTests
    {
        private sealed class Entry
        {
            public string Name { get; }
            public ulong Size { get; }
            public int LastUseSequence { get; set; }
            public bool HasOwner { get; set; }
            public LinkedListNode<Entry> CacheNode { get; set; }

            public Entry(string name, ulong size, int lastUseSequence = -1)
            {
                Name = name;
                Size = size;
                LastUseSequence = lastUseSequence;
            }
        }

        private sealed class Storage
        {
            public ulong Size { get; set; } = 80;
            public int DependencyCount { get; set; }
            public bool CanEvictAfterRelease { get; set; }
        }

        private sealed class Alias
        {
            public bool CanRelease { get; set; } = true;
            public List<Storage> Dependencies { get; } = [];

            public Alias(params Storage[] dependencies)
            {
                Dependencies.AddRange(dependencies);
            }
        }

        private static CacheEvictionPolicy<Entry> CreatePolicy(ulong capacity)
        {
            return new(
                capacity,
                static entry => entry.Size,
                static entry => entry.CacheNode,
                static (entry, node) => entry.CacheNode = node);
        }

        [Test]
        public void RendererCapabilityRefreshAppliesFourGiBAppleUnifiedBudgets()
        {
            const ulong MiB = 1024 * 1024;
            const ulong GiB = 1024 * MiB;

            CacheMemoryBudget beforeRendererInitialization = CacheMemoryBudgetPolicy.Calculate(
                4 * GiB,
                0,
                SystemMemoryType.BackendManaged,
                isApplePlatform: true,
                isIosPlatform: true);
            CacheMemoryBudget afterRendererInitialization = CacheMemoryBudgetPolicy.Calculate(
                4 * GiB,
                4 * GiB,
                SystemMemoryType.UnifiedMemory,
                isApplePlatform: true,
                isIosPlatform: true);

            Assert.Multiple(() =>
            {
                Assert.That(beforeRendererInitialization.BufferCapacity, Is.EqualTo(2 * GiB));
                Assert.That(beforeRendererInitialization.TextureCapacity, Is.EqualTo(1 * GiB));
                Assert.That(beforeRendererInitialization.IsAppleUnifiedMemory, Is.False);
                Assert.That(afterRendererInitialization.BufferCapacity, Is.EqualTo(64 * MiB));
                Assert.That(afterRendererInitialization.TextureCapacity, Is.EqualTo(64 * MiB));
                Assert.That(afterRendererInitialization.IsAppleUnifiedMemory, Is.True);
            });
        }

        [TestCase(4UL)]
        [TestCase(8UL)]
        [TestCase(16UL)]
        public void AppleUnifiedBudgetsKeepFixedIosPressureLimit(ulong physicalMemoryGiB)
        {
            const ulong MiB = 1024 * 1024;
            const ulong GiB = 1024 * MiB;

            CacheMemoryBudget budget = CacheMemoryBudgetPolicy.Calculate(
                physicalMemoryGiB * GiB,
                physicalMemoryGiB * GiB,
                SystemMemoryType.UnifiedMemory,
                isApplePlatform: true,
                isIosPlatform: true);

            Assert.Multiple(() =>
            {
                Assert.That(budget.BufferCapacity, Is.EqualTo(64 * MiB));
                Assert.That(budget.TextureCapacity, Is.EqualTo(64 * MiB));
                Assert.That(budget.IsAppleUnifiedMemory, Is.True);
            });
        }

        [Test]
        public void AppleSiliconMacKeepsScaledUnifiedBudgets()
        {
            const ulong MiB = 1024 * 1024;
            const ulong GiB = 1024 * MiB;

            CacheMemoryBudget budget = CacheMemoryBudgetPolicy.Calculate(
                8 * GiB,
                8 * GiB,
                SystemMemoryType.UnifiedMemory,
                isApplePlatform: true,
                isIosPlatform: false);

            Assert.Multiple(() =>
            {
                Assert.That(budget.BufferCapacity, Is.EqualTo(512 * MiB));
                Assert.That(budget.TextureCapacity, Is.EqualTo(512 * MiB));
                Assert.That(budget.IsAppleUnifiedMemory, Is.True);
            });
        }

        [Test]
        public void CurrentSequenceResourcesSurviveCompositeOperation()
        {
            CacheEvictionPolicy<Entry> policy = CreatePolicy(100);
            Entry source = new("source", 80, lastUseSequence: 7);
            Entry destination = new("destination", 80, lastUseSequence: 7);
            List<string> evicted = [];

            policy.Add(source);
            policy.Add(destination);
            policy.Trim(entry => entry.LastUseSequence != 7, entry => evicted.Add(entry.Name));

            Assert.That(evicted, Is.Empty);
            Assert.That(policy.CachedBytes, Is.EqualTo(160));
            Assert.That(source.CacheNode, Is.Not.Null);
            Assert.That(destination.CacheNode, Is.Not.Null);
        }

        [Test]
        public void NextSequenceEvictsOldestUntilUnderBudget()
        {
            CacheEvictionPolicy<Entry> policy = CreatePolicy(100);
            Entry source = new("source", 80, lastUseSequence: 7);
            Entry destination = new("destination", 80, lastUseSequence: 7);
            List<string> evicted = [];

            policy.Add(source);
            policy.Add(destination);
            policy.Trim(entry => entry.LastUseSequence != 8, entry => evicted.Add(entry.Name));

            CollectionAssert.AreEqual(new[] { "source" }, evicted);
            Assert.That(policy.CachedBytes, Is.EqualTo(80));
            Assert.That(source.CacheNode, Is.Null);
            Assert.That(destination.CacheNode, Is.Not.Null);
        }

        [Test]
        public void TouchKeepsRecentlyUsedEntryResident()
        {
            CacheEvictionPolicy<Entry> policy = CreatePolicy(100);
            Entry first = new("first", 60);
            Entry second = new("second", 60);
            List<string> evicted = [];

            policy.Add(first);
            policy.Add(second);
            policy.Touch(first);
            policy.Trim(_ => true, entry => evicted.Add(entry.Name));

            CollectionAssert.AreEqual(new[] { "second" }, evicted);
            Assert.That(first.CacheNode, Is.Not.Null);
            Assert.That(second.CacheNode, Is.Null);
        }

        [Test]
        public void OutstandingOwnerIsSkippedWithoutBlockingLaterCandidate()
        {
            CacheEvictionPolicy<Entry> policy = CreatePolicy(100);
            Entry dirtyOrReferenced = new("owned", 80) { HasOwner = true };
            Entry clean = new("clean", 80);
            List<string> evicted = [];

            policy.Add(dirtyOrReferenced);
            policy.Add(clean);
            policy.Trim(entry => !entry.HasOwner, entry => evicted.Add(entry.Name));

            CollectionAssert.AreEqual(new[] { "clean" }, evicted);
            Assert.That(policy.CachedBytes, Is.EqualTo(80));
            Assert.That(dirtyOrReferenced.CacheNode, Is.Not.Null);
        }

        [Test]
        public void RemovalAndClearReleaseTrackingExactlyOnce()
        {
            CacheEvictionPolicy<Entry> policy = CreatePolicy(100);
            Entry first = new("first", 40);
            Entry second = new("second", 50);

            policy.Add(first);
            policy.Add(second);
            policy.Remove(first);
            policy.Remove(first);

            Assert.That(policy.CachedBytes, Is.EqualTo(50));
            Assert.That(first.CacheNode, Is.Null);

            policy.Clear();

            Assert.That(policy.CachedBytes, Is.Zero);
            Assert.That(second.CacheNode, Is.Null);
        }

        [Test]
        public void DirtyPhysicalStorageDoesNotReleaseSparseAlias()
        {
            Storage dirtyStorage = new() { DependencyCount = 1, CanEvictAfterRelease = false };
            Alias alias = new(dirtyStorage);

            HashSet<Alias> selected = SelectAliases([alias]);

            Assert.That(selected, Is.Empty);
        }

        [Test]
        public void CleanPhysicalStorageSelectsItsOnlySparseAlias()
        {
            Storage cleanStorage = new() { DependencyCount = 1, CanEvictAfterRelease = true };
            Alias alias = new(cleanStorage);

            HashSet<Alias> selected = SelectAliases([alias]);

            CollectionAssert.AreEquivalent(new[] { alias }, selected);
        }

        [Test]
        public void SharedStorageRequiresEveryAliasToBeReleasable()
        {
            Storage cleanStorage = new() { DependencyCount = 2, CanEvictAfterRelease = true };
            Alias first = new(cleanStorage);
            Alias inUse = new(cleanStorage) { CanRelease = false };

            Assert.That(SelectAliases([first, inUse]), Is.Empty);

            inUse.CanRelease = true;

            CollectionAssert.AreEquivalent(new[] { first, inUse }, SelectAliases([first, inUse]));
        }

        [Test]
        public void UnrelatedSparseAliasIsNotReleased()
        {
            Storage unlockable = new() { DependencyCount = 1, CanEvictAfterRelease = true };
            Storage stillOwned = new() { DependencyCount = 2, CanEvictAfterRelease = true };
            Alias selectedAlias = new(unlockable);
            Alias unrelatedAlias = new(stillOwned);

            HashSet<Alias> selected = SelectAliases([selectedAlias, unrelatedAlias]);

            CollectionAssert.AreEquivalent(new[] { selectedAlias }, selected);
        }

        [Test]
        public void AliasReleaseStopsAfterMeetingBudgetDeficit()
        {
            Storage firstStorage = new() { DependencyCount = 1, CanEvictAfterRelease = true };
            Storage secondStorage = new() { DependencyCount = 1, CanEvictAfterRelease = true };
            Alias firstAlias = new(firstStorage);
            Alias secondAlias = new(secondStorage);

            HashSet<Alias> selected = SelectAliases([firstAlias, secondAlias], [firstStorage, secondStorage], bytesToFree: 40);

            CollectionAssert.AreEquivalent(new[] { firstAlias }, selected);
        }

        [Test]
        public void CopyChunkPreservesUnmappedSentinelBeforeOffsetArithmetic()
        {
            MemoryRange unmapped = new(MemoryManager.PteUnmapped, 0x1000);
            MemoryRange mapped = new(0x8000, 0x1000);

            bool canCopy = BufferCache.TryGetCopyChunkAddresses(unmapped, 0x800, mapped, 0x400, out ulong srcAddress, out ulong dstAddress);

            Assert.That(canCopy, Is.False);
            Assert.That(srcAddress, Is.EqualTo(MemoryManager.PteUnmapped));
            Assert.That(dstAddress, Is.EqualTo(MemoryManager.PteUnmapped));
        }

        [Test]
        public void CopyChunkAddsOffsetsForMappedRanges()
        {
            bool canCopy = BufferCache.TryGetCopyChunkAddresses(
                new MemoryRange(0x1000, 0x1000),
                0x120,
                new MemoryRange(0x8000, 0x1000),
                0x340,
                out ulong srcAddress,
                out ulong dstAddress);

            Assert.That(canCopy, Is.True);
            Assert.That(srcAddress, Is.EqualTo(0x1120));
            Assert.That(dstAddress, Is.EqualTo(0x8340));
        }

        private static HashSet<Alias> SelectAliases(IEnumerable<Alias> aliases)
        {
            List<Storage> storageInEvictionOrder = [];

            foreach (Alias alias in aliases)
            {
                foreach (Storage storage in alias.Dependencies)
                {
                    if (!storageInEvictionOrder.Contains(storage))
                    {
                        storageInEvictionOrder.Add(storage);
                    }
                }
            }

            return SelectAliases(aliases, storageInEvictionOrder, ulong.MaxValue);
        }

        private static HashSet<Alias> SelectAliases(
            IEnumerable<Alias> aliases,
            IEnumerable<Storage> storageInEvictionOrder,
            ulong bytesToFree)
        {
            return CacheDependencyEvictionPolicy.SelectAliasesToRelease<Alias, Storage>(
                aliases,
                storageInEvictionOrder,
                bytesToFree,
                alias => alias.CanRelease,
                alias => alias.Dependencies,
                storage => storage.DependencyCount,
                storage => storage.CanEvictAfterRelease,
                storage => storage.Size);
        }
    }
}
