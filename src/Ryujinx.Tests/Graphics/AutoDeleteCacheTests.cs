using NUnit.Framework;
using Ryujinx.Common.Memory;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Gpu;
using Ryujinx.Graphics.Gpu.Image;
using Ryujinx.Graphics.Gpu.Memory;
using Ryujinx.Graphics.Texture;
using Ryujinx.Memory.Range;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Ryujinx.Tests.Graphics
{
    public class AutoDeleteCacheTests
    {
        private sealed class TestHostTexture : ITexture
        {
            public int Width => 1;
            public int Height => 1;
            public int ReleaseCount { get; private set; }

            public void CopyTo(ITexture destination, int firstLayer, int firstLevel) => throw new System.NotSupportedException();
            public void CopyTo(ITexture destination, int srcLayer, int dstLayer, int srcLevel, int dstLevel) => throw new System.NotSupportedException();
            public void CopyTo(ITexture destination, Extents2D srcRegion, Extents2D dstRegion, bool linearFilter) => throw new System.NotSupportedException();
            public void CopyTo(BufferRange range, int layer, int level, int stride) => throw new System.NotSupportedException();
            public ITexture CreateView(TextureCreateInfo info, int firstLayer, int firstLevel) => throw new System.NotSupportedException();
            public PinnedSpan<byte> GetData() => throw new System.NotSupportedException();
            public PinnedSpan<byte> GetData(int layer, int level) => throw new System.NotSupportedException();
            public void SetData(MemoryOwner<byte> data) => throw new System.NotSupportedException();
            public void SetData(MemoryOwner<byte> data, int layer, int level) => throw new System.NotSupportedException();
            public void SetData(MemoryOwner<byte> data, int layer, int level, Rectangle<int> region) => throw new System.NotSupportedException();
            public void SetStorage(BufferRange buffer) => throw new System.NotSupportedException();
            public void Release() => ReleaseCount++;
        }

        private static Texture CreateOwnedTexture(GpuContext context = null, int width = 1, int height = 1, int levels = 1)
        {
            TextureInfo info = new(
                gpuAddress: 0,
                width: width,
                height: height,
                depthOrLayers: 1,
                levels: levels,
                samplesInX: 1,
                samplesInY: 1,
                stride: width * 4,
                isLinear: true,
                gobBlocksInY: 1,
                gobBlocksInZ: 1,
                gobBlocksInTileX: 1,
                target: Target.Texture2D,
                formatInfo: FormatInfo.Default);

            int size = checked((int)TextureCache.GetCreateInfo(info, default, 1f).GetTotalSize());
            Texture texture = new(context, null, info, new SizeInfo(size), new MultiRange(0, (ulong)size), TextureScaleMode.Blacklisted);

            // Model an independent owner so cache expiry can be verified without allocating host GPU storage.
            texture.IncrementReferenceCount();
            return texture;
        }

        private static Texture CreatePressureTrimmableTexture(int width)
        {
            GpuContext context = (GpuContext)RuntimeHelpers.GetUninitializedObject(typeof(GpuContext));
            TextureInfo info = new(
                gpuAddress: 0,
                width: width,
                height: 1,
                depthOrLayers: 1,
                levels: 1,
                samplesInX: 1,
                samplesInY: 1,
                stride: width * 4,
                isLinear: true,
                gobBlocksInY: 1,
                gobBlocksInZ: 1,
                gobBlocksInTileX: 1,
                target: Target.TextureBuffer,
                formatInfo: FormatInfo.Default);

            int size = width * 4;
            Texture texture = new(context, null, info, new SizeInfo(size), new MultiRange(0, (ulong)size), TextureScaleMode.Blacklisted);
            texture.InitializeGroup(false, false, new List<TextureIncompatibleOverlap>());
            texture.IncrementReferenceCount();
            return texture;
        }

        private static (Texture Texture, TestHostTexture HostTexture) CreatePressureEvictableTexture(int width)
        {
            GpuContext context = (GpuContext)RuntimeHelpers.GetUninitializedObject(typeof(GpuContext));
            PhysicalMemory physicalMemory = (PhysicalMemory)RuntimeHelpers.GetUninitializedObject(typeof(PhysicalMemory));
            TextureCache textureCache = new(context, physicalMemory);
            typeof(PhysicalMemory)
                .GetField("<TextureCache>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(physicalMemory, textureCache);

            TextureInfo info = new(
                gpuAddress: 0,
                width: width,
                height: 1,
                depthOrLayers: 1,
                levels: 1,
                samplesInX: 1,
                samplesInY: 1,
                stride: width * 4,
                isLinear: true,
                gobBlocksInY: 1,
                gobBlocksInZ: 1,
                gobBlocksInTileX: 1,
                target: Target.TextureBuffer,
                formatInfo: FormatInfo.Default);

            int size = width * 4;
            Texture texture = new(context, physicalMemory, info, new SizeInfo(size), new MultiRange(0, (ulong)size), TextureScaleMode.Blacklisted);
            texture.InitializeGroup(false, false, new List<TextureIncompatibleOverlap>());

            TestHostTexture hostTexture = new();
            typeof(Texture)
                .GetProperty(nameof(Texture.HostTexture), BindingFlags.Instance | BindingFlags.Public)
                .SetValue(texture, hostTexture);

            return (texture, hostTexture);
        }

        private static void SetGpuModified(Texture texture, bool modified)
        {
            FieldInfo handlesField = typeof(TextureGroup)
                .GetField("_handles", BindingFlags.Instance | BindingFlags.NonPublic);
            TextureGroupHandle[] handles = (TextureGroupHandle[])handlesField.GetValue(texture.Group);

            // TextureBuffer groups intentionally have no tracking handles. Supply one isolated
            // handle so the test can model GPU-dirty state without a real PhysicalMemory tracker.
            if (handles.Length == 0)
            {
                handles =
                [
                    new TextureGroupHandle(
                        texture.Group,
                        offset: 0,
                        size: texture.Size,
                        views: null,
                        firstLayer: 0,
                        firstLevel: 0,
                        baseSlice: 0,
                        sliceCount: 1,
                        handles: Array.Empty<Ryujinx.Memory.Tracking.RegionHandle>())
                ];
                handlesField.SetValue(texture.Group, handles);
            }

            foreach (TextureGroupHandle handle in handles)
            {
                handle.Modified = modified;
            }
        }

        [Test]
        public void OversizedMostRecentTextureRetainsItsCacheReferenceUntilExplicitRemoval()
        {
            // Size estimation only reads Capabilities. Avoid constructing a renderer or allocating GPU storage.
            GpuContext context = (GpuContext)RuntimeHelpers.GetUninitializedObject(typeof(GpuContext));
            Texture texture = CreateOwnedTexture(context, width: 16384, height: 16384, levels: 2);
            AutoDeleteCache cache = new();

            // The metadata describes 1.25 GiB, exceeding the default 1 GiB cache budget without allocating it.
            Assert.That(texture.GetEstimatedHostSize(), Is.EqualTo(1280UL * 1024 * 1024));

            cache.Add(texture);
            Assert.That(texture.CacheNode, Is.Not.Null);
            Assert.That(texture.HasOneReference(), Is.False);
            CollectionAssert.AreEqual(new[] { texture }, cache);

            cache.Lift(texture);
            Assert.That(texture.CacheNode, Is.Not.Null);
            Assert.That(texture.HasOneReference(), Is.False);
            CollectionAssert.AreEqual(new[] { texture }, cache);

            // Explicit removal still releases the cache's reference, leaving the independent test owner.
            cache.Remove(texture, flush: false);
            Assert.That(texture.CacheNode, Is.Null);
            Assert.That(texture.CacheSize, Is.Zero);
            Assert.That(texture.HasOneReference(), Is.True);
            CollectionAssert.IsEmpty(cache);
        }

        [Test]
        public void PressureTrimSkipsTextureWithAnotherOwner()
        {
            AutoDeleteCache cache = new();
            Texture oldest = CreatePressureTrimmableTexture(64);
            Texture newest = CreatePressureTrimmableTexture(64);

            cache.Add(oldest);
            cache.Add(newest);
            ulong newestSize = newest.CacheSize;

            var result = cache.TrimForMemoryPressure(newestSize);

            Assert.Multiple(() =>
            {
                Assert.That(oldest.CacheNode, Is.Not.Null);
                Assert.That(oldest.HasOneReference(), Is.False);
                Assert.That(newest.CacheNode, Is.Not.Null);
                Assert.That(cache.CachedBytes, Is.GreaterThan(newestSize));
                Assert.That(result.Evicted, Is.Zero);
                Assert.That(result.SkippedReferenced, Is.EqualTo(1));
                Assert.That(result.RetainedMostRecentBytes, Is.EqualTo(newestSize));
            });

            cache.Remove(oldest, flush: false);
            cache.Remove(newest, flush: false);
        }

        [Test]
        public void CriticalPressureTrimSkipsReferencedEntryAndRetainsMostRecentTexture()
        {
            AutoDeleteCache cache = new();
            Texture oldest = CreatePressureTrimmableTexture(64);
            Texture newest = CreatePressureTrimmableTexture(64);

            cache.Add(oldest);
            cache.Add(newest);
            var result = cache.TrimForMemoryPressure(0);

            Assert.Multiple(() =>
            {
                Assert.That(oldest.CacheNode, Is.Not.Null);
                Assert.That(oldest.HasOneReference(), Is.False);
                Assert.That(newest.CacheNode, Is.Not.Null);
                Assert.That(newest.HasOneReference(), Is.False);
                Assert.That(result.Evicted, Is.Zero);
                Assert.That(result.SkippedReferenced, Is.EqualTo(1));
                Assert.That(result.RetainedMostRecentBytes, Is.EqualTo(newest.CacheSize));
            });
            CollectionAssert.AreEqual(new[] { oldest, newest }, cache);

            cache.Remove(oldest, flush: false);
            cache.Remove(newest, flush: false);
        }

        [Test]
        public void PressureTrimEvictsCleanOldestTextureAndReleasesHostStorage()
        {
            AutoDeleteCache cache = new();
            (Texture oldest, TestHostTexture oldestHost) = CreatePressureEvictableTexture(64);
            Texture newest = CreatePressureTrimmableTexture(64);

            cache.Add(oldest);
            cache.Add(newest);
            ulong newestSize = newest.CacheSize;

            var result = cache.TrimForMemoryPressure(newestSize);

            Assert.Multiple(() =>
            {
                Assert.That(oldest.CacheNode, Is.Null);
                Assert.That(oldest.CacheSize, Is.Zero);
                Assert.That(oldestHost.ReleaseCount, Is.EqualTo(1));
                Assert.That(newest.CacheNode, Is.Not.Null);
                Assert.That(cache.CachedBytes, Is.EqualTo(newestSize));
                Assert.That(result.Evicted, Is.EqualTo(1));
                Assert.That(result.SkippedReferenced, Is.Zero);
                Assert.That(result.SkippedModified, Is.Zero);
                Assert.That(result.RetainedMostRecentBytes, Is.Zero);
            });
            CollectionAssert.AreEqual(new[] { newest }, cache);

            cache.Remove(newest, flush: false);
        }

        [Test]
        public void NormalCapacityEvictionBypassesGpuDirtyOldestForOneCleanRelease()
        {
            AutoDeleteCache cache = new();
            (Texture oldest, TestHostTexture oldestHost) = CreatePressureEvictableTexture(64);
            (Texture cleanAlternative, TestHostTexture cleanHost) = CreatePressureEvictableTexture(64);
            Texture newest = CreatePressureTrimmableTexture(64);
            ulong textureSize = oldest.GetEstimatedHostSize();

            SetGpuModified(oldest, true);
            cache.ConfigureMemoryBudget(textureSize * 2, isAppleUnifiedMemory: true);

            cache.Add(oldest);
            cache.Add(cleanAlternative);
            cache.Add(newest);

            var statistics = cache.GetStatistics();

            Assert.Multiple(() =>
            {
                Assert.That(oldest.CacheNode, Is.Not.Null);
                Assert.That(oldestHost.ReleaseCount, Is.Zero);
                Assert.That(cleanAlternative.CacheNode, Is.Null);
                Assert.That(cleanHost.ReleaseCount, Is.EqualTo(1));
                Assert.That(newest.CacheNode, Is.Not.Null);
                Assert.That(statistics.NormalEvictions, Is.EqualTo(1));
                Assert.That(statistics.NormalReadbackEvictions, Is.Zero);
                Assert.That(statistics.NormalCleanBypasses, Is.EqualTo(1));
                Assert.That(statistics.NormalEvictedBytes, Is.EqualTo(textureSize));
            });
            CollectionAssert.AreEqual(new[] { oldest, newest }, cache);

            cache.Remove(oldest, flush: false);
            cache.Remove(newest, flush: false);
        }

        [TestCase(false, true, false, false, true)]
        [TestCase(false, true, true, true, true)]
        [TestCase(false, true, false, true, false)]
        [TestCase(false, false, false, false, false)]
        [TestCase(true, true, false, false, false)]
        public void NormalEvictionAlternativeIsSafeAndCanOnlyDeferOldestOnce(
            bool oldestAlreadyDeferred,
            bool hasOneReference,
            bool cpuModified,
            bool gpuModified,
            bool expected)
        {
            Assert.That(
                NormalTextureEvictionPolicy.CanSelectAlternative(
                    oldestAlreadyDeferred,
                    hasOneReference,
                    cpuModified,
                    gpuModified),
                Is.EqualTo(expected));
        }

        [TestCase(false, false, false)]
        [TestCase(false, true, true)]
        [TestCase(true, false, false)]
        [TestCase(true, true, false)]
        public void NormalEvictionOnlyClassifiesGpuOnlyDirtyDataAsReadback(
            bool cpuModified,
            bool gpuModified,
            bool expected)
        {
            Assert.That(
                NormalTextureEvictionPolicy.RequiresReadback(cpuModified, gpuModified),
                Is.EqualTo(expected));
            Assert.That(NormalTextureEvictionPolicy.CandidateScanLimit, Is.EqualTo(4));
        }

        [TestCase(true, true, true, true)]
        [TestCase(true, true, false, true)]
        [TestCase(true, false, false, true)]
        [TestCase(true, false, true, false)]
        [TestCase(false, true, false, false)]
        [TestCase(false, false, false, false)]
        public void PressureTrimPolicyOnlyEvictsUnreferencedTexturesWithoutRequiredReadback(
            bool hasOneReference,
            bool cpuModified,
            bool gpuModified,
            bool expected)
        {
            Assert.That(
                TexturePressureTrimPolicy.CanEvict(hasOneReference, cpuModified, gpuModified),
                Is.EqualTo(expected));
        }

        [Test]
        public void NewDescriptorlessEntryExpiresAfterTwoTicks()
        {
            AutoDeleteCache cache = new();
            Texture texture = CreateOwnedTexture();

            cache.AddShortCache(texture);

            Assert.That(texture.ShortCacheEntry, Is.Not.Null);
            Assert.That(texture.ShortCacheEntry.IsAutoDelete, Is.True);
            Assert.That(texture.HasOneReference(), Is.False);

            cache.ProcessShortCache();
            Assert.That(texture.ShortCacheEntry, Is.Not.Null);
            Assert.That(texture.HasOneReference(), Is.False);

            cache.ProcessShortCache();
            Assert.That(texture.ShortCacheEntry, Is.Null);
            Assert.That(texture.HasOneReference(), Is.True);
        }

        [Test]
        public void RepeatedAddDoesNotReplaceOrExtendExistingEntry()
        {
            AutoDeleteCache cache = new();
            Texture texture = CreateOwnedTexture();

            cache.AddShortCache(texture);
            ShortTextureCacheEntry entry = texture.ShortCacheEntry;
            cache.AddShortCache(texture);
            Assert.That(texture.ShortCacheEntry, Is.SameAs(entry));

            cache.ProcessShortCache();
            cache.AddShortCache(texture);
            Assert.That(texture.ShortCacheEntry, Is.SameAs(entry));

            cache.ProcessShortCache();
            Assert.That(texture.ShortCacheEntry, Is.Null);
            Assert.That(texture.HasOneReference(), Is.True);

            // A duplicate entry would cause another decrement on the following tick.
            cache.ProcessShortCache();
            Assert.That(texture.HasOneReference(), Is.True);
        }

        [Test]
        public void ExplicitRemovalReleasesOnlyTheShortCacheReference()
        {
            AutoDeleteCache cache = new();
            Texture texture = CreateOwnedTexture();

            cache.AddShortCache(texture);
            cache.ProcessShortCache();
            cache.RemoveShortCache(texture);

            Assert.That(texture.ShortCacheEntry, Is.Null);
            Assert.That(texture.HasOneReference(), Is.True);

            cache.ProcessShortCache();
            cache.ProcessShortCache();
            Assert.That(texture.HasOneReference(), Is.True);
        }

        [Test]
        public void DescriptorlessAddPreservesExistingDescriptorLookup()
        {
            AutoDeleteCache cache = new();
            Texture texture = CreateOwnedTexture();
            TextureDescriptor descriptor = default;

            cache.AddShortCache(texture, ref descriptor);
            ShortTextureCacheEntry entry = texture.ShortCacheEntry;
            cache.AddShortCache(texture);

            Assert.That(texture.ShortCacheEntry, Is.SameAs(entry));
            Assert.That(texture.ShortCacheEntry.IsAutoDelete, Is.False);
            Assert.That(cache.FindShortCache(descriptor), Is.SameAs(texture));

            cache.RemoveShortCache(texture);
            Assert.That(cache.FindShortCache(descriptor), Is.Null);
            Assert.That(texture.HasOneReference(), Is.True);

            cache.ProcessShortCache();
            cache.ProcessShortCache();
            Assert.That(texture.HasOneReference(), Is.True);
        }
    }
}
