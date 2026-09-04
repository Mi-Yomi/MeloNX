using NUnit.Framework;
using Ryujinx.Graphics.Vulkan;
using Ryujinx.Graphics.Vulkan.MoltenVK;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Ryujinx.Tests.Graphics
{
    public class MoltenVKConfigurationTests
    {
        [Test]
        public void ConfigurationPrefixMatchesBundledMoltenVkAbi()
        {
            int capturePathOffset = System.IntPtr.Size == 8 ? 104 : 100;
            int minimumPrefixSize = System.IntPtr.Size == 8 ? 152 : 144;

            Assert.Multiple(() =>
            {
                Assert.That(Marshal.SizeOf<Bool32>(), Is.EqualTo(4));
                Assert.That(Marshal.OffsetOf<MVKConfiguration>(nameof(MVKConfiguration.SynchronousQueueSubmits)).ToInt32(), Is.EqualTo(8));
                Assert.That(Marshal.OffsetOf<MVKConfiguration>(nameof(MVKConfiguration.PrefillMetalCommandBuffers)).ToInt32(), Is.EqualTo(12));
                Assert.That(Marshal.OffsetOf<MVKConfiguration>(nameof(MVKConfiguration.MaxActiveMetalCommandBuffersPerQueue)).ToInt32(), Is.EqualTo(16));
                Assert.That(Marshal.OffsetOf<MVKConfiguration>(nameof(MVKConfiguration.MetalCompileTimeout)).ToInt32(), Is.EqualTo(32));
                Assert.That(Marshal.OffsetOf<MVKConfiguration>(nameof(MVKConfiguration.AutoGPUCaptureOutputFilepath)).ToInt32(), Is.EqualTo(capturePathOffset));
                Assert.That(Marshal.OffsetOf<MVKConfiguration>(nameof(MVKConfiguration.UseCommandPooling)).ToInt32(), Is.EqualTo(capturePathOffset + System.IntPtr.Size + 8));
                Assert.That(Marshal.OffsetOf<MVKConfiguration>(nameof(MVKConfiguration.ShaderSourceCompressionAlgorithm)).ToInt32(), Is.EqualTo(capturePathOffset + System.IntPtr.Size + 36));
                Assert.That(Marshal.SizeOf<MVKConfiguration>(), Is.GreaterThanOrEqualTo(minimumPrefixSize));
            });
        }

        [Test]
        public void IosMemoryProfileUsesMoltenVkAbiValues()
        {
            Assert.Multiple(() =>
            {
                Assert.That((int)MVKPrefillMetalCommandBuffersStyle.ImmediateEncoding, Is.EqualTo(2));
                Assert.That((int)MVKConfigCompressionAlgorithm.Lzfse, Is.EqualTo(1));
                Assert.That((int)MVKConfigTraceVulkanCalls.EnterExit, Is.EqualTo(3));
                Assert.That((int)MVKConfigTraceVulkanCalls.Duration, Is.EqualTo(5));
                Assert.That((int)MVKConfigAutoGPUCaptureScope.OnDemand, Is.EqualTo(3));
                Assert.That((uint)MVKConfigAdvertiseExtensions.WSI, Is.EqualTo(2));
                Assert.That((uint)MVKConfigAdvertiseExtensions.Portability, Is.EqualTo(4));
            });
        }

        [TestCase(false, false, false)]
        [TestCase(false, true, false)]
        [TestCase(true, false, false)]
        [TestCase(true, true, true)]
        public void NativeVulkanPrimitiveRestartFollowsGuestAndTopology(
            bool requestedEnable,
            bool topologySupportsRestart,
            bool expected)
        {
            bool actual = VulkanPlatformPolicy.GetPrimitiveRestartEnable(
                requestedEnable,
                topologySupportsRestart,
                isMoltenVk: false);

            Assert.That(actual, Is.EqualTo(expected));
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void MoltenVkPrimitiveRestartAlwaysMatchesMetal(
            bool requestedEnable,
            bool topologySupportsRestart)
        {
            bool actual = VulkanPlatformPolicy.GetPrimitiveRestartEnable(
                requestedEnable,
                topologySupportsRestart,
                isMoltenVk: true);

            Assert.That(actual, Is.True);
        }

        [TestCase(true, false)]
        [TestCase(false, true)]
        public void IosDoesNotUseDriverPipelineCache(bool isIos, bool expected)
        {
            Assert.That(VulkanPlatformPolicy.ShouldUseDriverPipelineCache(isIos), Is.EqualTo(expected));
        }

        [TestCase(true, false)]
        [TestCase(false, true)]
        public void BackendThreadingAutoPrefersSingleThreadedRendererOnIos(bool isIos, bool expected)
        {
            Assert.That(VulkanPlatformPolicy.ShouldPreferThreading(isIos), Is.EqualTo(expected));
        }

        [TestCase(true, true, 2)]
        [TestCase(true, false, 2)]
        [TestCase(false, true, 4)]
        [TestCase(false, false, 16)]
        public void IosUsesFourMainCommandBuffers(bool isLight, bool isIos, int expected)
        {
            Assert.That(CommandBufferPool.GetTotalCommandBuffers(isLight, isIos), Is.EqualTo(expected));
        }

        [Test]
        [SupportedOSPlatform("ios")]
        public void MoltenVkAndRendererUseTheSameIosCommandBufferLimit()
        {
            Assert.That(MVKInitialization.IosMaxActiveMetalCommandBuffersPerQueue, Is.EqualTo(CommandBufferPool.IosCommandBuffers));
        }
    }
}
