using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Vulkan.Queries;
using Silk.NET.Vulkan;
using System;
using System.Collections.Generic;
using System.Threading;

namespace Ryujinx.Graphics.Vulkan
{
    class PipelineFull : PipelineBase, IPipeline
    {
        private const ulong MinByteWeightForFlush = 256 * 1024 * 1024; // MiB

        private readonly List<(QueryPool, bool)> _activeQueries;

        private readonly List<BufferedQuery> _pendingQueryCopies;
        private readonly List<BufferHolder> _activeBufferMirrors;

        private ulong _byteWeight;
        private int _disposalFlushDeferralDepth;
        private long _queryCopiesRecorded;
        private long _idleSubmissions;

        internal readonly struct DisposalFlushScope : IDisposable
        {
            private readonly PipelineFull _owner;

            internal DisposalFlushScope(PipelineFull owner)
            {
                _owner = owner;
                owner._disposalFlushDeferralDepth++;
            }

            public void Dispose()
            {
                // The caller of the enclosing upload can still hold a captured
                // command buffer. Flush only on a future ordinary disposal or the
                // pipeline's natural submission, never while unwinding this scope.
                _owner._disposalFlushDeferralDepth--;
            }
        }

        internal DisposalFlushScope DeferDisposalFlushes() => new(this);

        private readonly List<BufferHolder> _backingSwaps;

        public PipelineFull(VulkanRenderer gd, Device device) : base(gd, device)
        {
            _activeQueries = [];
            _pendingQueryCopies = [];
            _backingSwaps = [];
            _activeBufferMirrors = [];

            CommandBuffer = (Cbs = gd.CommandBufferPool.Rent()).CommandBuffer;

            IsMainPipeline = true;
        }

        private void CopyPendingQuery()
        {
            foreach (BufferedQuery query in _pendingQueryCopies)
            {
                query.PoolCopy(Cbs);
                Interlocked.Increment(ref _queryCopiesRecorded);
            }

            _pendingQueryCopies.Clear();
        }

        public void ClearRenderTargetColor(int index, int layer, int layerCount, uint componentMask, ColorF color)
        {
            if (FramebufferParams == null)
            {
                return;
            }

            if (componentMask != 0xf || Gd.IsQualcommProprietary)
            {
                // We can't use CmdClearAttachments if not writing all components,
                // because on Vulkan, the pipeline state does not affect clears.
                // On proprietary Adreno drivers, CmdClearAttachments appears to execute out of order, so it's better to not use it at all.
                TextureView dstTexture = FramebufferParams.GetColorView(index);
                if (dstTexture == null)
                {
                    return;
                }

                Span<float> clearColor = stackalloc float[4];
                clearColor[0] = color.Red;
                clearColor[1] = color.Green;
                clearColor[2] = color.Blue;
                clearColor[3] = color.Alpha;

                // TODO: Clear only the specified layer.
                Gd.HelperShader.Clear(
                    Gd,
                    dstTexture,
                    clearColor,
                    componentMask,
                    (int)FramebufferParams.Width,
                    (int)FramebufferParams.Height,
                    FramebufferParams.GetAttachmentComponentType(index),
                    ClearScissor);
            }
            else
            {
                ClearRenderTargetColor(index, layer, layerCount, color);
            }
        }

        public void ClearRenderTargetDepthStencil(int layer, int layerCount, float depthValue, bool depthMask, int stencilValue, int stencilMask)
        {
            if (FramebufferParams == null)
            {
                return;
            }

            if ((stencilMask != 0 && stencilMask != 0xff) || Gd.IsQualcommProprietary)
            {
                // We can't use CmdClearAttachments if not clearing all (mask is all ones, 0xFF) or none (mask is 0) of the stencil bits,
                // because on Vulkan, the pipeline state does not affect clears.
                // On proprietary Adreno drivers, CmdClearAttachments appears to execute out of order, so it's better to not use it at all.
                TextureView dstTexture = FramebufferParams.GetDepthStencilView();
                if (dstTexture == null)
                {
                    return;
                }

                // TODO: Clear only the specified layer.
                Gd.HelperShader.Clear(
                    Gd,
                    dstTexture,
                    depthValue,
                    depthMask,
                    stencilValue,
                    stencilMask,
                    (int)FramebufferParams.Width,
                    (int)FramebufferParams.Height,
                    FramebufferParams.AttachmentFormats[FramebufferParams.AttachmentsCount - 1],
                    ClearScissor);
            }
            else
            {
                ClearRenderTargetDepthStencil(layer, layerCount, depthValue, depthMask, stencilValue, stencilMask != 0);
            }
        }

        public void EndHostConditionalRendering()
        {
            // This backend evaluates conditions on the CPU until native Vulkan
            // conditional begin/end commands are implemented together.
        }

        public bool TryHostConditionalRendering(ICounterEvent value, ulong compare, bool isEqual)
        {
            // Advertising an extension does not implement native conditional
            // rendering. Returning true without recording its begin/end commands
            // makes rejected guest draws execute unconditionally. Publish query
            // work before the caller waits and evaluates the result on the CPU;
            // this fallback never reserves a query for unused host access.
            FlushPendingQuery();
            return false;
        }

        public bool TryHostConditionalRendering(ICounterEvent value, ICounterEvent compare, bool isEqual)
        {
            FlushPendingQuery(); // The thread will be stalled manually flushing the counter, so flush commands now.
            return false;
        }

        private void FlushPendingQuery()
        {
            if (AutoFlush.ShouldFlushQuery())
            {
                FlushCommandsImpl();
            }
        }

        /// <summary>
        /// Submits work which can otherwise wait forever for the next guest draw.
        /// Call only on the backend thread between complete GAL commands.
        /// </summary>
        public bool FlushPendingWorkAtIdle()
        {
            if (_disposalFlushDeferralDepth != 0 ||
                (!AutoFlush.ShouldFlushQuery() && _byteWeight < MinByteWeightForFlush))
            {
                return false;
            }

            FlushCommandsImpl();
            Interlocked.Increment(ref _idleSubmissions);
            return true;
        }

        public string GetPendingWorkDiagnosticSnapshot() =>
            $"pending_query_submission={AutoFlush.ShouldFlushQuery()}, disposal_weight={Volatile.Read(ref _byteWeight)}, " +
            $"disposal_flush_depth={Volatile.Read(ref _disposalFlushDeferralDepth)}, " +
            $"query_copies_recorded={Interlocked.Read(ref _queryCopiesRecorded)}, idle_submissions={Interlocked.Read(ref _idleSubmissions)}";

        public CommandBufferScoped GetPreloadCommandBuffer()
        {
            PreloadCbs ??= Gd.CommandBufferPool.Rent();

            return PreloadCbs.Value;
        }

        public void FlushCommandsIfWeightExceeding(IAuto disposedResource, ulong byteWeight)
        {
            if (RegisterDisposalWeight(disposedResource, byteWeight))
            {
                FlushCommandsImpl();
            }
        }

        internal bool RegisterDisposalWeight(IAuto disposedResource, ulong byteWeight)
        {
            bool usedByCurrentCb = disposedResource.HasCommandBufferDependency(Cbs);

            if (PreloadCbs != null && !usedByCurrentCb)
            {
                usedByCurrentCb = disposedResource.HasCommandBufferDependency(PreloadCbs.Value);
            }

            if (usedByCurrentCb)
            {
                // Since we can only free memory after the command buffer that uses a given resource was executed,
                // keeping the command buffer might cause a high amount of memory to be in use.
                // To prevent that, we force submit command buffers if the memory usage by resources
                // in use by the current command buffer is above a given limit, and those resources were disposed.
                _byteWeight += byteWeight;

                return _byteWeight >= MinByteWeightForFlush && _disposalFlushDeferralDepth == 0;
            }

            return false;
        }

        public void Restore()
        {
            if (Pipeline != null)
            {
                Gd.Api.CmdBindPipeline(CommandBuffer, Pbp, Pipeline.Get(Cbs).Value);
            }

            SignalCommandBufferChange();

            if (Pipeline != null && Pbp == PipelineBindPoint.Graphics)
            {
                DynamicState.ReplayIfDirty(Gd, CommandBuffer);
            }
        }

        public void FlushCommandsImpl()
        {
            AutoFlush.RegisterFlush(DrawCount);
            EndRenderPass();
            // A report can arrive after an upload/dispatch already ended the render pass.
            // Such copies must precede submission and every following query reset.
            CopyPendingQuery();

            foreach ((QueryPool queryPool, _) in _activeQueries)
            {
                Gd.Api.CmdEndQuery(CommandBuffer, queryPool, 0);
            }

            _byteWeight = 0;

            if (PreloadCbs != null)
            {
                PreloadCbs.Value.Dispose();
                PreloadCbs = null;
            }

            Gd.Barriers.Flush(Cbs, false, null, null);
            CommandBuffer = (Cbs = Gd.CommandBufferPool.ReturnAndRent(Cbs)).CommandBuffer;
            Gd.RegisterFlush();

            // Restore per-command buffer state.
            foreach (BufferHolder buffer in _activeBufferMirrors)
            {
                buffer.ClearMirrors();
            }

            _activeBufferMirrors.Clear();

            foreach ((QueryPool queryPool, bool isOcclusion) in _activeQueries)
            {
                bool isPrecise = Gd.Capabilities.SupportsPreciseOcclusionQueries && isOcclusion;

                Gd.Api.CmdResetQueryPool(CommandBuffer, queryPool, 0, 1);
                Gd.Api.CmdBeginQuery(CommandBuffer, queryPool, 0, isPrecise ? QueryControlFlags.PreciseBit : 0);
            }

            Gd.ResetCounterPool();

            Restore();
        }

        public void RegisterActiveMirror(BufferHolder buffer)
        {
            _activeBufferMirrors.Add(buffer);
        }

        public void BeginQuery(BufferedQuery query, QueryPool pool, bool needsReset, bool isOcclusion, bool fromSamplePool)
        {
            if (needsReset)
            {
                EndRenderPass();

                CopyPendingQuery();

                Gd.Api.CmdResetQueryPool(CommandBuffer, pool, 0, 1);

                if (fromSamplePool)
                {
                    // Try reset some additional queries in advance.

                    Gd.ResetFutureCounters(CommandBuffer, AutoFlush.GetRemainingQueries());
                }
            }

            bool isPrecise = Gd.Capabilities.SupportsPreciseOcclusionQueries && isOcclusion;
            Gd.Api.CmdBeginQuery(CommandBuffer, pool, 0, isPrecise ? QueryControlFlags.PreciseBit : 0);

            _activeQueries.Add((pool, isOcclusion));
        }

        public void EndQuery(QueryPool pool)
        {
            Gd.Api.CmdEndQuery(CommandBuffer, pool, 0);

            for (int i = 0; i < _activeQueries.Count; i++)
            {
                if (_activeQueries[i].Item1.Handle == pool.Handle)
                {
                    _activeQueries.RemoveAt(i);
                    break;
                }
            }
        }

        public void CopyQueryResults(BufferedQuery query)
        {
            if (RenderPassActive)
            {
                _pendingQueryCopies.Add(query);
            }
            else
            {
                // There may be no later render pass. Record the copy now, before a
                // pooled query can be reset, and let the idle service submit it.
                query.PoolCopy(Cbs);
                Interlocked.Increment(ref _queryCopiesRecorded);
            }

            if (AutoFlush.RegisterPendingQuery())
            {
                FlushCommandsImpl();
            }
        }

        protected override void SignalAttachmentChange()
        {
            if (AutoFlush.ShouldFlushAttachmentChange(DrawCount))
            {
                FlushCommandsImpl();
            }
        }

        protected override void SignalRenderPassEnd()
        {
            CopyPendingQuery();
        }
    }
}
