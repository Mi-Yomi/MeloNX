using NUnit.Framework;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Gpu;
using Ryujinx.Graphics.Gpu.Image;
using Ryujinx.Graphics.Texture;
using Ryujinx.Memory.Range;
using System.Runtime.CompilerServices;

namespace Ryujinx.Tests.Graphics
{
    public class AutoDeleteCacheTests
    {
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
