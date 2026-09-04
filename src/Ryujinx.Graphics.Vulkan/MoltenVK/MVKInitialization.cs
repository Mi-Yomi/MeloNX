using Silk.NET.Core.Loader;
using Silk.NET.Vulkan;
using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Ryujinx.Graphics.Vulkan.MoltenVK
{
    [SupportedOSPlatform("macos")]
    [SupportedOSPlatform("ios")]
    public static partial class MVKInitialization
    {
        private const string VulkanLib = "libvulkan.dylib";
        private const uint DefaultMaxActiveMetalCommandBuffersPerQueue = 32;
        private const uint IosMaxActiveMetalCommandBuffersPerQueue = 8;

        [LibraryImport("libMoltenVK.dylib")]
        private static partial Result vkGetMoltenVKConfigurationMVK(nint unusedInstance, out MVKConfiguration config, ref nuint configSize);

        [LibraryImport("libMoltenVK.dylib")]
        private static partial Result vkSetMoltenVKConfigurationMVK(nint unusedInstance, in MVKConfiguration config, ref nuint configSize);

        public static void Initialize()
        {
            nuint configSize = (nuint)Marshal.SizeOf<MVKConfiguration>();

            vkGetMoltenVKConfigurationMVK(nint.Zero, out MVKConfiguration config, ref configSize);

            config.UseMetalArgumentBuffers = true;
            config.FastMathEnabled = MVKConfigFastMath.Always;

            config.SemaphoreSupportStyle = MVKVkSemaphoreSupportStyle.MVK_CONFIG_VK_SEMAPHORE_SUPPORT_STYLE_SINGLE_QUEUE;

            if (OperatingSystem.IsIOS())
            {
                config.SynchronousQueueSubmits = true;
                config.PrefillMetalCommandBuffers = MVKPrefillMetalCommandBuffersStyle.ImmediateEncoding;
                config.MaxActiveMetalCommandBuffersPerQueue = IosMaxActiveMetalCommandBuffersPerQueue;
                config.UseCommandPooling = true;
                config.ShaderSourceCompressionAlgorithm = MVKConfigCompressionAlgorithm.Lzfse;
            }
            else
            {
                config.SynchronousQueueSubmits = false;
                config.MaxActiveMetalCommandBuffersPerQueue = DefaultMaxActiveMetalCommandBuffersPerQueue;
            }

            config.ResumeLostDevice = true;

            vkSetMoltenVKConfigurationMVK(nint.Zero, config, ref configSize);
        }

        private static string[] Resolver(string path)
        {
            if (path.EndsWith(VulkanLib))
            {
                path = path[..^VulkanLib.Length] + "libMoltenVK.dylib";
                return [path];
            }

            return [];
        }

        public static void InitializeResolver()
        {
            ((DefaultPathResolver)PathResolver.Default).Resolvers.Insert(0, Resolver);
        }
    }
}
