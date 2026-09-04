using System;
using System.Runtime.InteropServices;

namespace Ryujinx.Graphics.Vulkan.MoltenVK
{
    enum MVKConfigLogLevel
    {
        None = 0,
        Error = 1,
        Warning = 2,
        Info = 3,
        Debug = 4,
    }

    enum MVKConfigTraceVulkanCalls
    {
        None = 0,
        Enter = 1,
        EnterThreadId = 2,
        EnterExit = 3,
        EnterExitThreadId = 4,
        Duration = 5,
        DurationThreadId = 6,
    }

    enum MVKConfigAutoGPUCaptureScope
    {
        None = 0,
        Device = 1,
        Frame = 2,
        OnDemand = 3,
    }

    enum MVKPrefillMetalCommandBuffersStyle
    {
        NoPrefill = 0,
        DeferredEncoding = 1,
        ImmediateEncoding = 2,
        ImmediateEncodingNoAutorelease = 3,
    }

    enum MVKConfigFastMath
    {
        Never = 0,
        Always = 1,
        OnDemand = 2,
    }

    enum MVKConfigCompressionAlgorithm
    {
        None = 0,
        Lzfse = 1,
        Zlib = 2,
        Lz4 = 3,
        Lzma = 4,
    }

    enum MVKConfigActivityPerformanceLoggingStyle
    {
        FrameCount = 0,
        Immediate = 1,
        DeviceLifetime = 2,
        DeviceLifetimeAccumulate = 3,
    }

    enum MVKConfigUseMTLHeap
    {
        Never = 0,
        WhereSafe = 1,
        Always = 2,
    }

    [Flags]
    enum MVKConfigAdvertiseExtensions : uint
    {
        All = 0x00000001,
        WSI = 0x00000002,
        Portability = 0x00000004,
    }

    enum MVKVkSemaphoreSupportStyle
    {
        MVK_CONFIG_VK_SEMAPHORE_SUPPORT_STYLE_SINGLE_QUEUE = 0,
        MVK_CONFIG_VK_SEMAPHORE_SUPPORT_STYLE_METAL_EVENTS_WHERE_SAFE = 1,
        MVK_CONFIG_VK_SEMAPHORE_SUPPORT_STYLE_METAL_EVENTS = 2,
        MVK_CONFIG_VK_SEMAPHORE_SUPPORT_STYLE_CALLBACK = 3,
        MVK_CONFIG_VK_SEMAPHORE_SUPPORT_STYLE_MAX_ENUM = 0x7FFFFFFF,
    }

    readonly struct Bool32
    {
        uint Value { get; }

        public Bool32(uint value)
        {
            Value = value;
        }

        public Bool32(bool value)
        {
            Value = value ? 1u : 0u;
        }

        public static implicit operator bool(Bool32 val) => val.Value == 1;
        public static implicit operator Bool32(bool val) => new(val);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MVKConfiguration
    {
        public Bool32 DebugMode;
        public Bool32 ShaderConversionFlipVertexY;
        public Bool32 SynchronousQueueSubmits;
        public MVKPrefillMetalCommandBuffersStyle PrefillMetalCommandBuffers;
        public uint MaxActiveMetalCommandBuffersPerQueue;
        public Bool32 SupportLargeQueryPools;
        public Bool32 PresentWithCommandBuffer;
        public Bool32 SwapchainMinMagFilterUseNearest;
        public ulong MetalCompileTimeout;
        public Bool32 PerformanceTracking;
        public uint PerformanceLoggingFrameCount;
        public Bool32 DisplayWatermark;
        public Bool32 SpecializedQueueFamilies;
        public Bool32 SwitchSystemGPU;
        public Bool32 FullImageViewSwizzle;
        public uint DefaultGPUCaptureScopeQueueFamilyIndex;
        public uint DefaultGPUCaptureScopeQueueIndex;
        public MVKConfigFastMath FastMathEnabled;
        public MVKConfigLogLevel LogLevel;
        public MVKConfigTraceVulkanCalls TraceVulkanCalls;
        public Bool32 ForceLowPowerGPU;
        public Bool32 SemaphoreUseMTLFence;
        public MVKVkSemaphoreSupportStyle SemaphoreSupportStyle;
        public MVKConfigAutoGPUCaptureScope AutoGPUCaptureScope;
        public nint AutoGPUCaptureOutputFilepath;
        public Bool32 Texture1DAs2D;
        public Bool32 PreallocateDescriptors;
        public Bool32 UseCommandPooling;
        public MVKConfigUseMTLHeap UseMTLHeap;
        public MVKConfigActivityPerformanceLoggingStyle ActivityPerformanceLoggingStyle;
        public uint ApiVersionToAdvertise;
        public MVKConfigAdvertiseExtensions AdvertiseExtensions;
        public Bool32 ResumeLostDevice;
        public Bool32 UseMetalArgumentBuffers;
        public MVKConfigCompressionAlgorithm ShaderSourceCompressionAlgorithm;
    }
}
