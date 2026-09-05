using Ryujinx.Common.Configuration;
using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL.Multithreading;
using System;
using System.Threading;

namespace Ryujinx.Graphics.GAL
{
    public interface IRenderer : IDisposable
    {
        event EventHandler<ScreenCaptureImageInfo> ScreenCaptured;

        bool PreferThreading { get; }

        public IRenderer TryMakeThreaded(BackendThreading backendThreading = BackendThreading.Auto)
        {
            if (backendThreading is BackendThreading.On ||
                (backendThreading is BackendThreading.Auto && PreferThreading))
            {
                Logger.Info?.PrintMsg(LogClass.Gpu, $"Backend Threading ({backendThreading}): True");
                return new ThreadedRenderer(this);
            }

            Logger.Info?.PrintMsg(LogClass.Gpu, $"Backend Threading ({backendThreading}): False");

            return this;
        }

        IPipeline Pipeline { get; }

        IWindow Window { get; }

        uint ProgramCount { get; }

        void BackgroundContextAction(Action action, bool alwaysBackground = false);

        /// <summary>
        /// Releases backend allocations that are safe to discard while the host is under memory pressure.
        /// </summary>
        /// <param name="aggressive">Whether expensive, rebuildable caches should also be discarded</param>
        /// <param name="availableMemoryBytes">Current host bytes remaining before the process limit</param>
        void TrimMemory(bool aggressive, ulong availableMemoryBytes);

        BufferHandle CreateBuffer(int size, BufferAccess access = BufferAccess.Default);
        BufferHandle CreateBuffer(nint pointer, int size);
        BufferHandle CreateBufferSparse(ReadOnlySpan<BufferRange> storageBuffers);

        IImageArray CreateImageArray(int size, bool isBuffer);

        IProgram CreateProgram(ShaderSource[] shaders, ShaderInfo info);

        ISampler CreateSampler(SamplerCreateInfo info);
        ITexture CreateTexture(TextureCreateInfo info);
        ITextureArray CreateTextureArray(int size, bool isBuffer);

        bool PrepareHostMapping(nint address, ulong size);

        void CreateSync(ulong id, bool strict);

        void DeleteBuffer(BufferHandle buffer);

        PinnedSpan<byte> GetBufferData(BufferHandle buffer, int offset, int size);

        Capabilities GetCapabilities();
        ulong GetCurrentSync();
        HardwareInfo GetHardwareInfo();

        IProgram LoadProgramBinary(byte[] programBinary, bool hasFragmentShader, ShaderInfo info);

        void SetBufferData(BufferHandle buffer, int offset, ReadOnlySpan<byte> data);

        void UpdateCounters();

        void PreFrame();

        /// <summary>
        /// Submits deferred work that can unblock the guest when no new commands arrive.
        /// Called only on the backend owner thread, between complete GAL commands.
        /// Backends without deferred submissions need no idle service.
        /// </summary>
        void FlushPendingCommands() { }

        /// <summary>
        /// Returns observational progress counters without waiting for GPU completion.
        /// </summary>
        string GetDiagnosticSnapshot() => "backend_progress=unavailable";

        /// <summary>Sampler-safe: cached JSON and atomic scalars only. Never wait for GPU work.</summary>
        void WriteMemoryForensicState(System.Text.Json.Utf8JsonWriter writer, long now) => writer.WriteNullValue();

        ICounterEvent ReportCounter(CounterType type, EventHandler<ulong> resultHandler, float divisor, bool hostReserved);

        void ResetCounter(CounterType type);

        void RunLoop(ThreadStart gpuLoop)
        {
            gpuLoop();
        }

        void WaitSync(ulong id);

        void Initialize(GraphicsDebugLevel logLevel);

        void SetInterruptAction(Action<Action> interruptAction);

        void Screenshot();
    }
}
