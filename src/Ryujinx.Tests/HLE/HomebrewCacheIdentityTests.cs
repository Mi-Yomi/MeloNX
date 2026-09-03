using NUnit.Framework;
using Ryujinx.HLE.Loaders.Processes;

namespace Ryujinx.Tests.HLE
{
    public class HomebrewCacheIdentityTests
    {
        [Test]
        public void NacpProgramIdIsUsedForTitleDirectory()
        {
            Assert.That(
                HomebrewCacheIdentity.GetTitleId(0x0100123456789000),
                Is.EqualTo("0100123456789000"));
        }

        [Test]
        public void MissingProgramIdDoesNotCreateSyntheticIdentity()
        {
            Assert.That(HomebrewCacheIdentity.GetTitleId(0), Is.Null);
        }

        [Test]
        public void BuildIdIsUsedAsPtcCacheSelector()
        {
            bool success = HomebrewCacheIdentity.TryGetCacheSelector([1, 2, 3], out string cacheSelector);

            Assert.Multiple(() =>
            {
                Assert.That(success, Is.True);
                Assert.That(cacheSelector, Is.EqualTo("010203"));
            });
        }

        [Test]
        public void EmptyBuildIdDoesNotEnablePtcCache()
        {
            bool success = HomebrewCacheIdentity.TryGetCacheSelector(new byte[32], out string cacheSelector);

            Assert.Multiple(() =>
            {
                Assert.That(success, Is.False);
                Assert.That(cacheSelector, Is.Null);
            });
        }
    }
}
