using NUnit.Framework;
using Ryujinx.Graphics.Vulkan;
using Silk.NET.Vulkan;
using System;

namespace Ryujinx.Tests.Graphics
{
    public class SwapchainPolicyTests
    {
        [TestCase(0u, 0u, false)]
        [TestCase(1280u, 0u, false)]
        [TestCase(0u, 720u, false)]
        [TestCase(1280u, 720u, true)]
        public void FixedExtentMustHaveTwoNonZeroDimensions(uint width, uint height, bool expected)
        {
            SurfaceCapabilitiesKHR capabilities = new()
            {
                CurrentExtent = new Extent2D(width, height),
            };

            bool usable = Window.TryChooseSwapExtent(capabilities, out Extent2D extent);

            Assert.That(usable, Is.EqualTo(expected));
            Assert.That(extent.Width, Is.EqualTo(width));
            Assert.That(extent.Height, Is.EqualTo(height));
        }

        [TestCase(0u, 0u, false)]
        [TestCase(1920u, 1080u, true)]
        public void VariableExtentUsesClampedFallback(uint maxWidth, uint maxHeight, bool expected)
        {
            SurfaceCapabilitiesKHR capabilities = new()
            {
                CurrentExtent = new Extent2D(uint.MaxValue, uint.MaxValue),
                MinImageExtent = new Extent2D(0, 0),
                MaxImageExtent = new Extent2D(maxWidth, maxHeight),
            };

            bool usable = Window.TryChooseSwapExtent(capabilities, out Extent2D extent);

            Assert.That(usable, Is.EqualTo(expected));
            Assert.That(extent.Width, Is.EqualTo(Math.Min(maxWidth, 1280u)));
            Assert.That(extent.Height, Is.EqualTo(Math.Min(maxHeight, 720u)));
        }
    }
}
