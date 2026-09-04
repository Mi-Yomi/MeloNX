namespace Ryujinx.Graphics.Vulkan
{
    internal static class VulkanPlatformPolicy
    {
        public static bool ShouldUseDriverPipelineCache(bool isIos)
        {
            return !isIos;
        }

        public static bool GetPrimitiveRestartEnable(
            bool requestedEnable,
            bool topologySupportsRestart,
            bool isMoltenVk)
        {
            // Metal always enables primitive restart and MoltenVK cannot disable it.
            // Match the effective Metal state so equivalent guest states share one pipeline.
            return isMoltenVk || (requestedEnable && topologySupportsRestart);
        }
    }
}
