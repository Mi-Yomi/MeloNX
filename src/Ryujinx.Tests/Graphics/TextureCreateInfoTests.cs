using NUnit.Framework;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Vulkan.Effects;

namespace Ryujinx.Tests.Graphics
{
    public class TextureCreateInfoTests
    {
        [Test]
        public void TotalSizeIncludesMipLevelsLayersAndSamples()
        {
            TextureCreateInfo info = new(
                width: 64,
                height: 32,
                depth: 3,
                levels: 3,
                samples: 4,
                blockWidth: 1,
                blockHeight: 1,
                bytesPerPixel: 4,
                format: Format.R8G8B8A8Unorm,
                depthStencilMode: DepthStencilMode.Depth,
                target: Target.Texture2DMultisampleArray,
                swizzleR: SwizzleComponent.Red,
                swizzleG: SwizzleComponent.Green,
                swizzleB: SwizzleComponent.Blue,
                swizzleA: SwizzleComponent.Alpha);

            Assert.That(info.GetTotalSize(), Is.EqualTo(129024UL));
        }

        [Test]
        public void TotalSizeUsesCompressedBlockDimensions()
        {
            TextureCreateInfo info = new(
                width: 64,
                height: 32,
                depth: 1,
                levels: 3,
                samples: 1,
                blockWidth: 4,
                blockHeight: 4,
                bytesPerPixel: 16,
                format: Format.Bc7Unorm,
                depthStencilMode: DepthStencilMode.Depth,
                target: Target.Texture2D,
                swizzleR: SwizzleComponent.Red,
                swizzleG: SwizzleComponent.Green,
                swizzleB: SwizzleComponent.Blue,
                swizzleA: SwizzleComponent.Alpha);

            Assert.That(info.GetTotalSize(), Is.EqualTo(2688UL));
        }

        [Test]
        public void FsrIntermediaryIdentityUsesOutputDimensions()
        {
            TextureCreateInfo source = new(
                width: 1280,
                height: 720,
                depth: 1,
                levels: 1,
                samples: 1,
                blockWidth: 1,
                blockHeight: 1,
                bytesPerPixel: 4,
                format: Format.R8G8B8A8Unorm,
                depthStencilMode: DepthStencilMode.Depth,
                target: Target.Texture2D,
                swizzleR: SwizzleComponent.Red,
                swizzleG: SwizzleComponent.Green,
                swizzleB: SwizzleComponent.Blue,
                swizzleA: SwizzleComponent.Alpha);

            TextureCreateInfo first = FsrScalingFilter.CreateIntermediaryInfo(source, 2346, 1320);
            TextureCreateInfo nextFrame = FsrScalingFilter.CreateIntermediaryInfo(source, 2346, 1320);

            Assert.Multiple(() =>
            {
                Assert.That(first.Width, Is.EqualTo(2346));
                Assert.That(first.Height, Is.EqualTo(1320));
                Assert.That(first, Is.Not.EqualTo(source));
                Assert.That(nextFrame, Is.EqualTo(first), "Identical output must reuse the FSR intermediary.");
            });
        }
    }
}
