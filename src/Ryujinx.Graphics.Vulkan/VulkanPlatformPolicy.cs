namespace Ryujinx.Graphics.Vulkan
{
    internal static class VulkanPlatformPolicy
    {
        public static bool ShouldUseDriverPipelineCache(bool isIos)
        {
            return !isIos;
        }

        public static bool ShouldPreferThreading(bool isIos)
        {
            // Explicit Backend Threading=On is still honored by IRenderer.TryMakeThreaded.
            // Auto stays single-threaded on iOS so queued commands cannot retain a second
            // generation of transient resources under the platform's strict process limit.
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
