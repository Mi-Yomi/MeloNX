using NUnit.Framework;
using Ryujinx.Graphics.Vulkan.MoltenVK;
using System.Runtime.InteropServices;

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
    }
}
