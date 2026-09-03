using NUnit.Framework;
using Ryujinx.Graphics.Gpu.Shader.DiskCache;

namespace Ryujinx.Tests.Graphics
{
    public class DiskCacheLoadPolicyTests
    {
        [TestCase(1, 1)]
        [TestCase(2, 2)]
        [TestCase(6, 2)]
        [TestCase(16, 2)]
        public void IosTranslationWorkersAreBounded(int processorCount, int expected)
        {
            Assert.That(
                DiskCacheLoadPolicy.GetTranslationThreadCount(isIos: true, processorCount),
                Is.EqualTo(expected));
        }

        [TestCase(1)]
        [TestCase(6)]
        [TestCase(32)]
        public void OtherPlatformsKeepExistingWorkerCount(int processorCount)
        {
            Assert.That(
                DiskCacheLoadPolicy.GetTranslationThreadCount(isIos: false, processorCount),
                Is.EqualTo(8));
        }

        [TestCase(1, 1)]
        [TestCase(2, 2)]
        [TestCase(6, 2)]
        [TestCase(16, 2)]
        public void IosBackendCompilationsInFlightAreBounded(int processorCount, int expected)
        {
            Assert.That(
                DiskCacheLoadPolicy.GetBackendParallelCompileThreadCount(isIos: true, processorCount),
                Is.EqualTo(expected));
        }

        [TestCase(1, 1)]
        [TestCase(6, 6)]
        [TestCase(8, 8)]
        [TestCase(32, 8)]
        public void OtherPlatformsKeepExistingBackendCompileLimit(int processorCount, int expected)
        {
            Assert.That(
                DiskCacheLoadPolicy.GetBackendParallelCompileThreadCount(isIos: false, processorCount),
                Is.EqualTo(expected));
        }
    }
}
