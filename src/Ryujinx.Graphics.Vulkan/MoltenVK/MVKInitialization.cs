using Ryujinx.Common.Logging;
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
        internal const uint IosMaxActiveMetalCommandBuffersPerQueue = 4;

        [LibraryImport("libMoltenVK.dylib")]
        private static partial Result vkGetMoltenVKConfigurationMVK(nint unusedInstance, out MVKConfiguration config, ref nuint configSize);

        [LibraryImport("libMoltenVK.dylib")]
        private static partial Result vkSetMoltenVKConfigurationMVK(nint unusedInstance, in MVKConfiguration config, ref nuint configSize);

        public static void Initialize()
        {
            nuint configPrefixSize = (nuint)Marshal.SizeOf<MVKConfiguration>();
            nuint configSize = configPrefixSize;

            Result initialGetResult = vkGetMoltenVKConfigurationMVK(nint.Zero, out MVKConfiguration config, ref configSize);
            initialGetResult.ThrowOnError();

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

            // MoltenVK can report its newer, larger structure size from Get. We only own the
            // verified prefix, so never pass that larger returned size back with this C# value.
            configSize = configPrefixSize;
            Result setResult = vkSetMoltenVKConfigurationMVK(nint.Zero, config, ref configSize);
            setResult.ThrowOnError();

            configSize = configPrefixSize;
            Result verifyGetResult = vkGetMoltenVKConfigurationMVK(nint.Zero, out MVKConfiguration effectiveConfig, ref configSize);
            verifyGetResult.ThrowOnError();

            Logger.Info?.Print(
                LogClass.Gpu,
                $"MoltenVK configuration applied: get_result={initialGetResult}, set_result={setResult}, " +
                $"verify_result={verifyGetResult}, prefix_size={configPrefixSize}, copied_size={configSize}, " +
                $"max_active_metal_command_buffers={effectiveConfig.MaxActiveMetalCommandBuffersPerQueue}, " +
                $"synchronous_queue_submits={(bool)effectiveConfig.SynchronousQueueSubmits}, " +
                $"prefill_metal_command_buffers={effectiveConfig.PrefillMetalCommandBuffers}, " +
                $"use_command_pooling={(bool)effectiveConfig.UseCommandPooling}, " +
                $"shader_compression={effectiveConfig.ShaderSourceCompressionAlgorithm}.");

            if (OperatingSystem.IsIOS() &&
                effectiveConfig.MaxActiveMetalCommandBuffersPerQueue != IosMaxActiveMetalCommandBuffersPerQueue)
            {
                Logger.Warning?.Print(
                    LogClass.Gpu,
                    $"MoltenVK command-buffer limit mismatch: requested={IosMaxActiveMetalCommandBuffersPerQueue}, " +
                    $"effective={effectiveConfig.MaxActiveMetalCommandBuffersPerQueue}.");
            }
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
