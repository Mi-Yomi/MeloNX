using Silk.NET.Vulkan;

namespace Ryujinx.Graphics.Vulkan
{
    class PipelineHelperShader : PipelineBase
    {
        public PipelineHelperShader(VulkanRenderer gd, Device device) : base(gd, device)
        {
        }

        public void SetRenderTarget(TextureView view, uint width, uint height)
        {
            CreateFramebuffer(view, width, height);
            CreateRenderPass();
            SignalStateChange();
        }

        private void CreateFramebuffer(TextureView view, uint width, uint height)
        {
            FramebufferParams = new FramebufferParams(Device, view, width, height);
            UpdatePipelineAttachmentFormats();
        }

        public void SetCommandBuffer(CommandBufferScoped cbs)
        {
            CommandBuffer = (Cbs = cbs).CommandBuffer;

            // Restore per-command buffer state.

            Auto<DisposablePipeline> pipeline = Pipeline;

            if (pipeline != null)
            {
                // A pressure trim can retire the cache owner between helper invocations. Keep
                // the pipeline alive while attaching it to this command buffer, or forget the
                // stale raw reference and let the next draw/dispatch recreate it.
                if (pipeline.TryIncrementReferenceCount())
                {
                    try
                    {
                        Gd.Api.CmdBindPipeline(CommandBuffer, Pbp, pipeline.Get(CurrentCommandBuffer).Value);
                    }
                    finally
                    {
                        pipeline.DecrementReferenceCount();
                    }
                }
                else
                {
                    ForgetCurrentPipelineIfCurrent(pipeline);
                }
            }

            SignalCommandBufferChange();
        }

        public void Finish()
        {
            EndRenderPass();
        }

        public void Finish(VulkanRenderer gd, CommandBufferScoped cbs)
        {
            Finish();

            if (gd.PipelineInternal.IsCommandBufferActive(cbs.CommandBuffer))
            {
                gd.PipelineInternal.Restore();
            }
        }
    }
}
