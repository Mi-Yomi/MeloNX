using NUnit.Framework;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Gpu;
using Ryujinx.Graphics.Gpu.Image;
using Ryujinx.Graphics.Gpu.Memory;
using Ryujinx.Graphics.Texture;
using Ryujinx.Memory.Range;
using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Threading;

namespace Ryujinx.Tests.Graphics
{
    public class TextureFormatCensusTests
    {
        private static TextureFormatKey Key(int width = 64, TextureAllocationRole role = TextureAllocationRole.Storage) =>
            new(Format.Astc4x4Unorm, Format.R8G8B8A8Unorm, TextureFallbackReason.AstcFamilyCapability,
                Target.Texture2D, width, 64, width, 64, 1, 1, 1, 1, role);

        [Test]
        public void ViewsNeverDuplicateStorageAndReleaseIsIdempotent()
        {
            TextureFormatCensus census = new();
            var storage = census.Add(Key(), 16384);
            var view = census.Add(Key(role: TextureAllocationRole.View), 16384);
            Assert.That(census.GetTotals(), Is.EqualTo((2L, 0L, 1L, 1L, 16384UL)));
            census.Release(ref view);
            census.Release(ref view);
            Assert.That(census.GetTotals(), Is.EqualTo((2L, 1L, 1L, 0L, 16384UL)));
            census.Release(ref storage);
            Assert.That(census.GetTotals(), Is.EqualTo((2L, 2L, 0L, 0L, 0UL)));
        }

        [Test]
        public void OverflowPreservesLifetimeTotalsWithoutUnboundedKeys()
        {
            TextureFormatCensus census = new();
            for (int index = 0; index < 10000; index++)
            {
                var registration = census.Add(Key(index + 1), 256);
                census.Release(ref registration);
            }

            Assert.Multiple(() =>
            {
                Assert.That(census.KeyCount, Is.EqualTo(TextureFormatCensus.MaxKeys));
                Assert.That(census.GetTotals(), Is.EqualTo((10000L, 10000L, 0L, 0L, 0UL)));
                Assert.That(census.GetDiagnosticSnapshot(), Does.Contain("key=overflow_unknown"));
            });
        }

        [Test]
        public void WarmCensusUpdatesAndObservedUsageAllocateNothing()
        {
            TextureFormatCensus census = new();
            TextureFormatKey key = Key();
            for (int index = 0; index < 100; index++)
            {
                var registration = census.Add(key, 256);
                census.MarkUsage(registration, TextureObservedUsage.ShaderAccess);
                census.Release(ref registration);
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int index = 0; index < 20000; index++)
            {
                var registration = census.Add(key, 256);
                census.MarkUsage(registration, TextureObservedUsage.ShaderAccess);
                census.RecordConversion(TextureFallbackReason.Native, 256, 256, 20, false);
                census.Release(ref registration);
            }
            Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero);
        }

        [Test]
        public void ConverterTotalsSurviveStorageReleaseAndIncludeFailure()
        {
            TextureFormatCensus census = new();
            var registration = census.Add(Key(), 1024);
            Parallel.For(0, 1000, index =>
                census.RecordConversion(TextureFallbackReason.AstcFamilyCapability, 16, index % 2 == 0 ? 64 : 0, 10, index % 2 != 0));
            census.Release(ref registration);
            Assert.That(census.GetConversionStatistics(TextureFallbackReason.AstcFamilyCapability),
                Is.EqualTo((1000L, 500L, 16000L, 32000L, 10000L)));
        }

        [Test]
        public void BackgroundAuxiliaryCreationAndOwnerSnapshotHaveCoherentLifetimes()
        {
            TextureFormatCensus census = new();
            using Barrier start = new(3);
            Task background = Task.Run(() =>
            {
                start.SignalAndWait();
                for (int index = 0; index < 10000; index++)
                {
                    var registration = census.Add(Key(index % 128 + 1, TextureAllocationRole.ReadbackAuxiliary), 256);
                    census.MarkUsage(registration, TextureObservedUsage.Readback);
                    census.Release(ref registration);
                }
            });
            Task owner = Task.Run(() =>
            {
                start.SignalAndWait();
                for (int index = 0; index < 10000; index++)
                {
                    var storage = census.Add(Key(), 1024);
                    var view = census.Add(Key(role: TextureAllocationRole.View), 1024);
                    census.Release(ref view);
                    census.Release(ref storage);
                }
            });
            start.SignalAndWait();
            for (int index = 0; index < 30; index++)
            {
                // Add/Release and snapshot share only the diagnostic lock; the snapshot formats
                // outside it. Every locked state obeys created - released = live objects.
                var totals = census.GetTotals();
                Assert.That(totals.Created - totals.Released, Is.EqualTo(totals.LiveStorage + totals.LiveViews));
                Assert.That(census.GetDiagnosticSnapshot(), Does.Contain("scope=gal_issued_lifetimes"));
            }
            Assert.That(Task.WaitAll([background, owner], TimeSpan.FromSeconds(10)), Is.True);
            Assert.That(census.GetTotals(), Is.EqualTo((30000L, 30000L, 0L, 0L, 0UL)));
            Assert.That(census.KeyCount, Is.EqualTo(TextureFormatCensus.MaxKeys));
        }

        private static Capabilities Caps(params string[] enabled)
        {
            // The production readonly struct has a deliberately exhaustive constructor.
            // Set only the capability gates this test exercises without coupling to all others.
            object caps = default(Capabilities);
            foreach (string field in enabled)
            {
                typeof(Capabilities).GetField(field).SetValue(caps, true);
            }
            return (Capabilities)caps;
        }

        private static TextureInfo Info(Format format, Target target = Target.Texture2D) => new(
            0, 64, 64, 1, 1, 1, 1, 64, true, 1, 1, 1, target, new FormatInfo(format, 4, 4, 16, 4));

        private static (Texture Texture, TextureFormatCensus Census) CreateProductionTexture(TextureInfo info, int size)
        {
            const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
            GpuContext context = (GpuContext)RuntimeHelpers.GetUninitializedObject(typeof(GpuContext));
            PhysicalMemory physical = (PhysicalMemory)RuntimeHelpers.GetUninitializedObject(typeof(PhysicalMemory));
            TextureCache cache = new(context, physical);
            typeof(PhysicalMemory).GetField("<TextureCache>k__BackingField", fields).SetValue(physical, cache);
            typeof(GpuContext).GetField("<Renderer>k__BackingField", fields).SetValue(context, new AuditTestRenderer());
            Texture texture = new(context, physical, info, new SizeInfo(size), new MultiRange(0, (ulong)size), TextureScaleMode.Blacklisted);
            texture.InitializeGroup(false, false, []);
            return (texture, cache.FormatCensus);
        }

        [Test]
        public void ActualTextureCreationFailedViewAndDisposalBalanceCensus()
        {
            TextureInfo info = new(0, 1, 1, 1, 1, 1, 1, 4, true, 1, 1, 1, Target.TextureBuffer, FormatInfo.Default);
            var (texture, census) = CreateProductionTexture(info, 4);
            texture.InitializeData(false);
            try
            {
                Assert.That(census.GetTotals(), Is.EqualTo((1L, 0L, 1L, 0L, 4UL)));
                // The existing audit backend throws before returning a view. Failed creation must
                // not leave a phantom live view or drop the parent storage's accounting.
                Assert.Throws<NotSupportedException>(() => texture.CreateView(info, new SizeInfo(4), new MultiRange(0, 4UL), 0, 0));
                Assert.That(census.GetTotals(), Is.EqualTo((1L, 0L, 1L, 0L, 4UL)));
            }
            finally
            {
                texture.Dispose();
            }
            Assert.That(census.GetTotals(), Is.EqualTo((1L, 1L, 0L, 0L, 0UL)));
        }

        [Test]
        public void ActualBc1DecodeRecordsBytesAndSurvivesTextureDisposal()
        {
            TextureInfo info = new(0, 4, 4, 1, 1, 1, 1, 8, true, 1, 1, 1,
                Target.TextureBuffer, new FormatInfo(Format.Bc1RgbaUnorm, 4, 4, 8, 4));
            var (texture, census) = CreateProductionTexture(info, 8);
            texture.InitializeData(false);
            try
            {
                using var output = texture.ConvertToHostCompatibleFormat(new byte[8]);
                Assert.That(output.Length, Is.EqualTo(64));
                Assert.That(output.Span[3], Is.EqualTo(255));
            }
            finally
            {
                texture.Dispose();
            }
            var statistics = census.GetConversionStatistics(TextureFallbackReason.BcFamilyCapability);
            Assert.Multiple(() =>
            {
                Assert.That(statistics.Calls, Is.EqualTo(1));
                Assert.That(statistics.Failed, Is.Zero);
                Assert.That(statistics.SourceBytes, Is.EqualTo(8));
                Assert.That(statistics.OutputBytes, Is.EqualTo(64));
                Assert.That(census.GetTotals().LogicalBytes, Is.Zero);
            });
        }

        [Test]
        public void FailedAuxiliaryCopyDoesNotReportAnUnissuedReleaseDuringCleanup()
        {
            TextureInfo info = new(0, 1, 1, 1, 1, 1, 1, 4, true, 1, 1, 1, Target.TextureBuffer, FormatInfo.Default);
            var (texture, census) = CreateProductionTexture(info, 4);
            texture.InitializeData(false);
            typeof(Texture).GetProperty(nameof(Texture.ScaleFactor)).SetValue(texture, 2f);
            try
            {
                // The audit backend creates an auxiliary object but does not implement scaled
                // copies. The failed local object has not become _flushHostTexture.
                Assert.Throws<NotSupportedException>(() => texture.GetFlushTexture());
                Assert.That(census.GetTotals(), Is.EqualTo((2L, 0L, 2L, 0L, 8UL)));
            }
            finally
            {
                texture.Dispose();
            }
            // This diagnostic must preserve the outstanding allocation after a failing copy;
            // releasing the parent's old texture is not evidence the failed auxiliary was freed.
            Assert.That(census.GetTotals(), Is.EqualTo((2L, 1L, 1L, 0L, 4UL)));
        }

        [Test]
        public void FallbackReasonsDistinguishFamilyGateFrom3DGateAndRecompression()
        {
            Capabilities family = Caps(nameof(Capabilities.SupportsBc45Compression));
            Capabilities target = Caps(nameof(Capabilities.Supports3DTextureCompression));
            Assert.Multiple(() =>
            {
                Assert.That(TextureFormatCensus.GetFallbackReason(Info(Format.Bc4Snorm, Target.Texture3D), Format.R8Snorm, family),
                    Is.EqualTo(TextureFallbackReason.Bc3DTargetCapability));
                Assert.That(TextureFormatCensus.GetFallbackReason(Info(Format.Bc4Snorm, Target.Texture3D), Format.R8Snorm, target),
                    Is.EqualTo(TextureFallbackReason.BcFamilyCapability));
                Assert.That(TextureFormatCensus.GetFallbackReason(Info(Format.Bc4Snorm, Target.Texture3D), Format.R8Snorm, default),
                    Is.EqualTo(TextureFallbackReason.BcFamilyAnd3DTargetCapability));
                Assert.That(TextureFormatCensus.GetFallbackReason(Info(Format.Astc4x4Srgb), Format.Bc7Srgb, default),
                    Is.EqualTo(TextureFallbackReason.AstcFamilyCapabilityToBc7));
                Assert.That(TextureFormatCensus.GetFallbackReason(Info(Format.Astc4x4Srgb), Format.R8G8B8A8Srgb, default),
                    Is.EqualTo(TextureFallbackReason.AstcFamilyCapability));
                Assert.That(TextureFormatCensus.GetFallbackReason(Info(Format.Astc4x4Srgb), Format.Astc4x4Srgb, default),
                    Is.EqualTo(TextureFallbackReason.Native), "The recorded GAL format, not guessed support, defines whether fallback actually happened.");
            });
        }

        [Test]
        public void ViewKeyPreservesHostDimensionsLayersAndSignedFormat()
        {
            TextureFormatCensus census = new();
            TextureCreateInfo host = new(32, 16, 3, 3, 1, 1, 1, 2, Format.R8G8Snorm,
                DepthStencilMode.Depth, Target.Texture2DArray, SwizzleComponent.Red, SwizzleComponent.Green, SwizzleComponent.Blue, SwizzleComponent.Alpha);
            census.Add(Info(Format.Bc5Snorm, Target.Texture2DArray), host, default, TextureAllocationRole.View);
            string snapshot = census.GetDiagnosticSnapshot();
            Assert.Multiple(() =>
            {
                Assert.That(snapshot, Does.Contain("guest=Bc5Snorm, host_gal=R8G8Snorm"));
                Assert.That(snapshot, Does.Contain("host_width=32, host_height=16, depth=1, layers=3, levels=3"));
                Assert.That(census.GetTotals().LogicalBytes, Is.Zero);
            });
        }
    }
}
