using System;

namespace Ryujinx.Graphics.Gpu.Shader.DiskCache
{
    internal static class DiskCacheLoadPolicy
    {
        private const int DefaultTranslationThreadCount = 8;
        private const int IosTranslationThreadLimit = 2;

        public static int GetTranslationThreadCount(bool isIos, int processorCount)
        {
            if (!isIos)
            {
                return DefaultTranslationThreadCount;
            }

            return Math.Clamp(processorCount, 1, IosTranslationThreadLimit);
        }

        public static int GetBackendParallelCompileThreadCount(bool isIos, int processorCount)
        {
            return Math.Clamp(processorCount, 1, isIos ? IosTranslationThreadLimit : DefaultTranslationThreadCount);
        }
    }
}
