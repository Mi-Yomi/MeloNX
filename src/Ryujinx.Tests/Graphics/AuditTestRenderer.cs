using Ryujinx.Common.Configuration;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Shader;
using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Ryujinx.Tests.Graphics
{
    internal sealed class AuditTestRenderer : IRenderer
    {
        internal readonly ConcurrentDictionary<BufferHandle, byte[]> Buffers = new();
        internal readonly ConcurrentQueue<(string Operation, int Thread)> Events = new();
        internal readonly ConcurrentQueue<(int Source, int Destination, int Size)> Copies = new();
        internal Func<nint, ulong, bool> PrepareHostMappingHandler { get; set; }
        internal Action IdleHandler { get; set; }
        internal Func<CounterType, EventHandler<ulong>, float, bool, ICounterEvent> ReportCounterHandler { get; set; }
        internal Func<ICounterEvent, ulong, bool, bool> ConditionalRenderingHandler { get; set; }
        internal Action EndConditionalRenderingHandler { get; set; }
        internal BufferAccess? LastBufferAccess { get; private set; }
        private long _nextHandle;
        public event EventHandler<ScreenCaptureImageInfo> ScreenCaptured { add { } remove { } }
        public bool PreferThreading => true;
        public IPipeline Pipeline { get; }
        public IWindow Window => null;
        public uint ProgramCount => 0;
        public AuditTestRenderer() => Pipeline = new AuditTestPipeline(this);
        public BufferHandle CreateBuffer(int size, BufferAccess access = BufferAccess.Default)
        {
            LastBufferAccess = access;
            ulong value = (ulong)Interlocked.Increment(ref _nextHandle);
            BufferHandle handle = Unsafe.As<ulong, BufferHandle>(ref value);
            Buffers[handle] = GC.AllocateArray<byte>(size, pinned: true);
            Events.Enqueue(("create", Environment.CurrentManagedThreadId));
            return handle;
        }
        public void DeleteBuffer(BufferHandle buffer)
        {
            if (!Buffers.TryRemove(buffer, out _)) throw new InvalidOperationException("Missing backing");
            Events.Enqueue(("delete", Environment.CurrentManagedThreadId));
        }
        public PinnedSpan<byte> GetBufferData(BufferHandle buffer, int offset, int size) =>
            PinnedSpan<byte>.UnsafeFromSpan(Buffers[buffer].AsSpan(offset, size));
        public void SetBufferData(BufferHandle buffer, int offset, ReadOnlySpan<byte> data) => data.CopyTo(Buffers[buffer].AsSpan(offset));
        public void SetInterruptAction(Action<Action> action) { }
        public void BackgroundContextAction(Action action, bool alwaysBackground = false) => action();
        public void Dispose() => Events.Enqueue(("dispose", Environment.CurrentManagedThreadId));
        public void TrimMemory(bool aggressive, ulong availableMemoryBytes) => throw new NotSupportedException();
        public BufferHandle CreateBuffer(nint pointer, int size) => throw new NotSupportedException();
        public BufferHandle CreateBufferSparse(ReadOnlySpan<BufferRange> storageBuffers) => throw new NotSupportedException();
        public IImageArray CreateImageArray(int size, bool isBuffer) => throw new NotSupportedException();
        public IProgram CreateProgram(ShaderSource[] shaders, ShaderInfo info) => throw new NotSupportedException();
        public ISampler CreateSampler(SamplerCreateInfo info) => throw new NotSupportedException();
        public ITexture CreateTexture(TextureCreateInfo info) => new AuditTestTexture(this);
        public ITextureArray CreateTextureArray(int size, bool isBuffer) => throw new NotSupportedException();
        public bool PrepareHostMapping(nint address, ulong size) =>
            PrepareHostMappingHandler?.Invoke(address, size) ?? throw new NotSupportedException();
        public void CreateSync(ulong id, bool strict) => throw new NotSupportedException();
        public Capabilities GetCapabilities() => throw new NotSupportedException();
        public ulong GetCurrentSync() => throw new NotSupportedException();
        public HardwareInfo GetHardwareInfo() => throw new NotSupportedException();
        public IProgram LoadProgramBinary(byte[] programBinary, bool hasFragmentShader, ShaderInfo info) => throw new NotSupportedException();
        public void UpdateCounters() => throw new NotSupportedException();
        public void PreFrame() => throw new NotSupportedException();
        public void FlushPendingCommands() => IdleHandler?.Invoke();
        public ICounterEvent ReportCounter(CounterType type, EventHandler<ulong> resultHandler, float divisor, bool hostReserved) =>
            ReportCounterHandler?.Invoke(type, resultHandler, divisor, hostReserved) ?? throw new NotSupportedException();
        public void ResetCounter(CounterType type) => throw new NotSupportedException();
        public void WaitSync(ulong id) => throw new NotSupportedException();
        public void Initialize(GraphicsDebugLevel logLevel) => throw new NotSupportedException();
        public void Screenshot() => throw new NotSupportedException();
    }
    internal sealed class AuditTestPipeline : IPipeline
    {
        private readonly AuditTestRenderer _renderer;
        internal AuditTestPipeline(AuditTestRenderer renderer) => _renderer = renderer;
        public void CopyBuffer(BufferHandle source, BufferHandle destination, int srcOffset, int dstOffset, int size)
        {
            _renderer.Buffers[source].AsSpan(srcOffset, size).CopyTo(_renderer.Buffers[destination].AsSpan(dstOffset, size));
            _renderer.Copies.Enqueue((srcOffset, dstOffset, size));
            _renderer.Events.Enqueue(("copy", Environment.CurrentManagedThreadId));
        }
        public void Barrier() => throw new NotSupportedException();
        public void BeginTransformFeedback(PrimitiveTopology topology) => throw new NotSupportedException();
        public void ClearBuffer(BufferHandle destination, int offset, int size, uint value) => throw new NotSupportedException();
        public void ClearRenderTargetColor(int index, int layer, int layerCount, uint componentMask, ColorF color) => throw new NotSupportedException();
        public void ClearRenderTargetDepthStencil(int layer, int layerCount, float depthValue, bool depthMask, int stencilValue, int stencilMask) => throw new NotSupportedException();
        public void CommandBufferBarrier() => throw new NotSupportedException();
        public void DispatchCompute(int groupsX, int groupsY, int groupsZ) => throw new NotSupportedException();
        public void Draw(int vertexCount, int instanceCount, int firstVertex, int firstInstance) => throw new NotSupportedException();
        public void DrawIndexed(int indexCount, int instanceCount, int firstIndex, int firstVertex, int firstInstance) => throw new NotSupportedException();
        public void DrawIndexedIndirect(BufferRange indirectBuffer) => throw new NotSupportedException();
        public void DrawIndexedIndirectCount(BufferRange indirectBuffer, BufferRange parameterBuffer, int maxDrawCount, int stride) => throw new NotSupportedException();
        public void DrawIndirect(BufferRange indirectBuffer) => throw new NotSupportedException();
        public void DrawIndirectCount(BufferRange indirectBuffer, BufferRange parameterBuffer, int maxDrawCount, int stride) => throw new NotSupportedException();
        public void DrawTexture(ITexture texture, ISampler sampler, Extents2DF srcRegion, Extents2DF dstRegion) => throw new NotSupportedException();
        public void EndTransformFeedback() => throw new NotSupportedException();
        public void SetAlphaTest(bool enable, float reference, CompareOp op) => throw new NotSupportedException();
        public void SetBlendState(AdvancedBlendDescriptor blend) => throw new NotSupportedException();
        public void SetBlendState(int index, BlendDescriptor blend) => throw new NotSupportedException();
        public void SetDepthBias(PolygonModeMask enables, float factor, float units, float clamp) => throw new NotSupportedException();
        public void SetDepthClamp(bool clamp) => throw new NotSupportedException();
        public void SetDepthMode(DepthMode mode) => throw new NotSupportedException();
        public void SetDepthTest(DepthTestDescriptor depthTest) => throw new NotSupportedException();
        public void SetFaceCulling(bool enable, Face face) => throw new NotSupportedException();
        public void SetFrontFace(FrontFace frontFace) => throw new NotSupportedException();
        public void SetIndexBuffer(BufferRange buffer, IndexType type) => throw new NotSupportedException();
        public void SetImage(ShaderStage stage, int binding, ITexture texture) => throw new NotSupportedException();
        public void SetImageArray(ShaderStage stage, int binding, IImageArray array) => throw new NotSupportedException();
        public void SetImageArraySeparate(ShaderStage stage, int setIndex, IImageArray array) => throw new NotSupportedException();
        public void SetLineParameters(float width, bool smooth) => throw new NotSupportedException();
        public void SetLogicOpState(bool enable, LogicalOp op) => throw new NotSupportedException();
        public void SetMultisampleState(MultisampleDescriptor multisample) => throw new NotSupportedException();
        public void SetPatchParameters(int vertices, ReadOnlySpan<float> defaultOuterLevel, ReadOnlySpan<float> defaultInnerLevel) => throw new NotSupportedException();
        public void SetPointParameters(float size, bool isProgramPointSize, bool enablePointSprite, Origin origin) => throw new NotSupportedException();
        public void SetPolygonMode(PolygonMode frontMode, PolygonMode backMode) => throw new NotSupportedException();
        public void SetPrimitiveRestart(bool enable, int index) => throw new NotSupportedException();
        public void SetPrimitiveTopology(PrimitiveTopology topology) => throw new NotSupportedException();
        public void SetProgram(IProgram program) => throw new NotSupportedException();
        public void SetRasterizerDiscard(bool discard) => throw new NotSupportedException();
        public void SetRenderTargetColorMasks(ReadOnlySpan<uint> componentMask) => throw new NotSupportedException();
        public void SetRenderTargets(Span<ITexture> colors, ITexture depthStencil) => throw new NotSupportedException();
        public void SetScissors(ReadOnlySpan<Rectangle<int>> regions) => throw new NotSupportedException();
        public void SetStencilTest(StencilTestDescriptor stencilTest) => throw new NotSupportedException();
        public void SetStorageBuffers(ReadOnlySpan<BufferAssignment> buffers) => throw new NotSupportedException();
        public void SetTextureAndSampler(ShaderStage stage, int binding, ITexture texture, ISampler sampler) => throw new NotSupportedException();
        public void SetTextureArray(ShaderStage stage, int binding, ITextureArray array) => throw new NotSupportedException();
        public void SetTextureArraySeparate(ShaderStage stage, int setIndex, ITextureArray array) => throw new NotSupportedException();
        public void SetTransformFeedbackBuffers(ReadOnlySpan<BufferRange> buffers) => throw new NotSupportedException();
        public void SetUniformBuffers(ReadOnlySpan<BufferAssignment> buffers) => throw new NotSupportedException();
        public void SetUserClipDistance(int index, bool enableClip) => throw new NotSupportedException();
        public void SetVertexAttribs(ReadOnlySpan<VertexAttribDescriptor> vertexAttribs) => throw new NotSupportedException();
        public void SetVertexBuffers(ReadOnlySpan<VertexBufferDescriptor> vertexBuffers) => throw new NotSupportedException();
        public void SetViewports(ReadOnlySpan<Viewport> viewports) => throw new NotSupportedException();
        public void TextureBarrier() => throw new NotSupportedException();
        public void TextureBarrierTiled() => throw new NotSupportedException();
        public bool TryHostConditionalRendering(ICounterEvent value, ulong compare, bool isEqual) =>
            _renderer.ConditionalRenderingHandler?.Invoke(value, compare, isEqual) ?? throw new NotSupportedException();
        public bool TryHostConditionalRendering(ICounterEvent value, ICounterEvent compare, bool isEqual) => throw new NotSupportedException();
        public void EndHostConditionalRendering() =>
            (_renderer.EndConditionalRenderingHandler ?? throw new NotSupportedException()).Invoke();
    }
}
