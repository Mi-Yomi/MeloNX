using System;

namespace Ryujinx.HLE.Loaders.Processes
{
    internal static class HomebrewCacheIdentity
    {
        public static string GetTitleId(ulong programId)
        {
            return programId != 0 ? programId.ToString("X16") : null;
        }

        public static bool TryGetCacheSelector(ReadOnlySpan<byte> buildId, out string cacheSelector)
        {
            foreach (byte value in buildId)
            {
                if (value != 0)
                {
                    cacheSelector = Convert.ToHexString(buildId);
                    return true;
                }
            }

            cacheSelector = null;
            return false;
        }
    }
}
