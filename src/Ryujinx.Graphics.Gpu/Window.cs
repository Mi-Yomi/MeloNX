using Ryujinx.Graphics.GAL;
using Ryujinx.Common.Logging;
using Ryujinx.Graphics.Gpu.Image;
using Ryujinx.Graphics.Gpu.Memory;
using Ryujinx.Graphics.Texture;
using Ryujinx.Memory.Range;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;

namespace Ryujinx.Graphics.Gpu
{
    /// <summary>
    /// GPU image presentation window.
    /// </summary>
    public class Window
    {
        private readonly GpuContext _context;
        private const int DefaultUnboundedPresentTargetFps = 60;

        /// <summary>
        /// Texture presented on the window.
        /// </summary>
        private readonly struct PresentationTexture
        {
            /// <summary>
            /// Texture cache where the texture might be located.
            /// </summary>
            public TextureCache Cache { get; }

            /// <summary>
            /// Texture information.
            /// </summary>
            public TextureInfo Info { get; }

            /// <summary>
            /// Physical memory locations where the texture data is located.
            /// </summary>
            public MultiRange Range { get; }

            /// <summary>
            /// Texture crop region.
            /// </summary>
            public ImageCrop Crop { get; }

            /// <summary>
            /// Texture acquire callback.
            /// </summary>
            public Action<GpuContext, object> AcquireCallback { get; }

            /// <summary>
            /// Texture release callback.
            /// </summary>
            public Action<object> ReleaseCallback { get; }

            /// <summary>
            /// User defined object, passed to the various callbacks.
            /// </summary>
            public object UserObj { get; }

            /// <summary>
            /// Creates a new instance of the presentation texture.
            /// </summary>
            /// <param name="cache">Texture cache used to look for the texture to be presented</param>
            /// <param name="info">Information of the texture to be presented</param>
            /// <param name="range">Physical memory locations where the texture data is located</param>
            /// <param name="crop">Texture crop region</param>
            /// <param name="acquireCallback">Texture acquire callback</param>
            /// <param name="releaseCallback">Texture release callback</param>
            /// <param name="userObj">User defined object passed to the release callback, can be used to identify the texture</param>
            public PresentationTexture(
                TextureCache cache,
                TextureInfo info,
                MultiRange range,
                ImageCrop crop,
                Action<GpuContext, object> acquireCallback,
                Action<object> releaseCallback,
                object userObj)
            {
                Cache = cache;
                Info = info;
                Range = range;
                Crop = crop;
                AcquireCallback = acquireCallback;
                ReleaseCallback = releaseCallback;
                UserObj = userObj;
            }
        }

        private readonly ConcurrentQueue<PresentationTexture> _frameQueue;
        private readonly Stopwatch _unboundedPresentTimer;
        private long _unboundedPresentTicksPerFrame;

        private int _framesAvailable;
        private long _lastUnboundedPresentTicks;
        private volatile bool _unboundedPresentMode;
        private int _unboundedPresentTargetFps;
        private long _framesEnqueued;
        private long _framesPresented;
        private long _framesDropped;
        private int _maxQueuedFrames;
        private long _lastPresentStatsTicks;
        private long _lastStatsEnqueued;
        private long _lastStatsPresented;
        private long _lastStatsDropped;
        private long _frameStallCount;

        public long FrameStallCount => Interlocked.Read(ref _frameStallCount);

        public bool IsFrameAvailable => _framesAvailable != 0;

        /// <summary>
        /// Creates a new instance of the GPU presentation window.
        /// </summary>
        /// <param name="context">GPU emulation context</param>
        public Window(GpuContext context)
        {
            _context = context;

            _frameQueue = new ConcurrentQueue<PresentationTexture>();
            _unboundedPresentTimer = Stopwatch.StartNew();
            SetUnboundedPresentTargetFps(DefaultUnboundedPresentTargetFps);
        }

        /// <summary>
        /// Enqueues a frame for presentation.
        /// This method is thread safe and can be called from any thread.
        /// When the texture is presented and not needed anymore, the release callback is called.
        /// It's an error to modify the texture after calling this method, before the release callback is called.
        /// </summary>
        /// <param name="pid">Process ID of the process that owns the texture pointed to by <paramref name="address"/></param>
        /// <param name="address">CPU virtual address of the texture data</param>
        /// <param name="width">Texture width</param>
        /// <param name="height">Texture height</param>
        /// <param name="stride">Texture stride for linear texture, should be zero otherwise</param>
        /// <param name="isLinear">Indicates if the texture is linear, normally false</param>
        /// <param name="gobBlocksInY">GOB blocks in the Y direction, for block linear textures</param>
        /// <param name="format">Texture format</param>
        /// <param name="bytesPerPixel">Texture format bytes per pixel (must match the format)</param>
        /// <param name="crop">Texture crop region</param>
        /// <param name="acquireCallback">Texture acquire callback</param>
        /// <param name="releaseCallback">Texture release callback</param>
        /// <param name="userObj">User defined object passed to the release callback</param>
        /// <exception cref="ArgumentException">Thrown when <paramref name="pid"/> is invalid</exception>
        /// <returns>True if the frame was added to the queue, false otherwise</returns>
        public bool EnqueueFrameThreadSafe(
            ulong pid,
            ulong address,
            int width,
            int height,
            int stride,
            bool isLinear,
            int gobBlocksInY,
            Format format,
            byte bytesPerPixel,
            ImageCrop crop,
            Action<GpuContext, object> acquireCallback,
            Action<object> releaseCallback,
            object userObj)
        {
            if (!_context.PhysicalMemoryRegistry.TryGetValue(pid, out PhysicalMemory physicalMemory))
            {
                return false;
            }

            FormatInfo formatInfo = new(format, 1, 1, bytesPerPixel, 4);

            TextureInfo info = new(
                0UL,
                width,
                height,
                1,
                1,
                1,
                1,
                stride,
                isLinear,
                gobBlocksInY,
                1,
                1,
                Target.Texture2D,
                formatInfo);

            int size = SizeCalculator.GetBlockLinearTextureSize(
                width,
                height,
                1,
                1,
                1,
                1,
                1,
                bytesPerPixel,
                gobBlocksInY,
                1,
                1).TotalSize;

            MultiRange range = new(address, (ulong)size);

            _frameQueue.Enqueue(new PresentationTexture(
                physicalMemory.TextureCache,
                info,
                range,
                crop,
                acquireCallback,
                releaseCallback,
                userObj));

            long enqueued = Interlocked.Increment(ref _framesEnqueued);
            UpdateMaxQueuedFrames(enqueued - Interlocked.Read(ref _framesPresented) - Interlocked.Read(ref _framesDropped));

            return true;
        }

        /// <summary>
        /// Presents a texture on the queue.
        /// If the queue is empty, then no texture is presented.
        /// </summary>
        /// <param name="swapBuffersCallback">Callback method to call when a new texture should be presented on the screen</param>
        public void Present(Action swapBuffersCallback)
        {
            _context.AdvanceSequence();

            if (_frameQueue.TryDequeue(out PresentationTexture pt))
            {
                pt.AcquireCallback(_context, pt.UserObj);

                Image.Texture texture = pt.Cache.FindOrCreateTexture(null, TextureSearchFlags.WithUpscale, pt.Info, 0, range: pt.Range);

                pt.Cache.Tick();

                texture.SynchronizeMemory();

                ImageCrop crop = new(
                    (int)(pt.Crop.Left * texture.ScaleFactor),
                    (int)MathF.Ceiling(pt.Crop.Right * texture.ScaleFactor),
                    (int)(pt.Crop.Top * texture.ScaleFactor),
                    (int)MathF.Ceiling(pt.Crop.Bottom * texture.ScaleFactor),
                    pt.Crop.FlipX,
                    pt.Crop.FlipY,
                    pt.Crop.IsStretched,
                    pt.Crop.AspectRatioX,
                    pt.Crop.AspectRatioY);

                if (texture.Info.Width > pt.Info.Width || texture.Info.Height > pt.Info.Height)
                {
                    int top = crop.Top;
                    int bottom = crop.Bottom;
                    int left = crop.Left;
                    int right = crop.Right;

                    if (top == 0 && bottom == 0)
                    {
                        bottom = Math.Min(texture.Info.Height, pt.Info.Height);
                    }

                    if (left == 0 && right == 0)
                    {
                        right = Math.Min(texture.Info.Width, pt.Info.Width);
                    }

                    crop = new ImageCrop(left, right, top, bottom, crop.FlipX, crop.FlipY, crop.IsStretched, crop.AspectRatioX, crop.AspectRatioY);
                }

                _context.Renderer.Window.Present(texture.HostTexture, crop, swapBuffersCallback);
                Interlocked.Increment(ref _framesPresented);

                pt.ReleaseCallback(pt.UserObj);
            }
        }

        /// <summary>
        /// Presents all available ready frames, optionally capping unbounded present mode.
        /// </summary>
        /// <remarks>
        /// This method always consumes frames through <see cref="ConsumeFrameAvailable"/> before dequeuing,
        /// preserving the guest GPU fence readiness gate.
        /// </remarks>
        /// <param name="swapBuffersCallback">Callback method to call when a new texture should be presented on the screen</param>
        /// <returns>True if a frame was presented, false otherwise</returns>
        public bool PresentLoop(Action swapBuffersCallback)
        {
            bool presented = false;
            long statsTicks = _unboundedPresentTimer.ElapsedTicks;

            if (!_unboundedPresentMode)
            {
                while (ConsumeFrameAvailable())
                {
                    Present(swapBuffersCallback);
                    presented = true;
                }

                LogPresentStatsIfNeeded(statsTicks);
                return presented;
            }

            long ticks = statsTicks;
            long lastPresentTicks = Interlocked.Read(ref _lastUnboundedPresentTicks);
            bool shouldPresent = lastPresentTicks == 0 || ticks - lastPresentTicks >= Interlocked.Read(ref _unboundedPresentTicksPerFrame);

            while (ConsumeFrameAvailable())
            {
                if (shouldPresent)
                {
                    Present(swapBuffersCallback);
                    _lastUnboundedPresentTicks = ticks;
                    shouldPresent = false;
                    presented = true;
                }
                else
                {
                    DropFrame();
                }
            }

            LogPresentStatsIfNeeded(statsTicks);
            return presented;
        }

        /// <summary>
        /// Sets whether the host present loop should cap and drop surplus ready frames.
        /// </summary>
        /// <param name="enabled">True when the guest compositor is running with swap interval 0</param>
        public void SetUnboundedPresentMode(bool enabled)
        {
            if (_unboundedPresentMode != enabled)
            {
                _unboundedPresentMode = enabled;
                Interlocked.Exchange(ref _lastUnboundedPresentTicks, 0);
                Logger.Info?.Print(
                    LogClass.Gpu,
                    $"Guest presentation pacing changed: unbounded={enabled}, target_fps={_unboundedPresentTargetFps}.");
            }
        }

        /// <summary>
        /// Sets the FPS target used when unbounded present mode is capped by the host.
        /// </summary>
        /// <param name="targetFps">Host display refresh rate in frames per second</param>
        public void SetUnboundedPresentTargetFps(int targetFps)
        {
            targetFps = Math.Max(1, targetFps);

            if (Interlocked.Exchange(ref _unboundedPresentTargetFps, targetFps) == targetFps)
            {
                // UIKit can repeat layout/touch updates every frame. An unchanged
                // display rate must not reset the pacing clock or flood the log.
                return;
            }
            Interlocked.Exchange(ref _unboundedPresentTicksPerFrame, Math.Max(1, Stopwatch.Frequency / targetFps));
            Interlocked.Exchange(ref _lastUnboundedPresentTicks, 0);
            Logger.Info?.Print(LogClass.Gpu, $"Unbounded host presentation target set: fps={targetFps}.");
        }

        private void DropFrame()
        {
            if (_frameQueue.TryDequeue(out PresentationTexture pt))
            {
                pt.ReleaseCallback(pt.UserObj);
                Interlocked.Increment(ref _framesDropped);
            }
        }

        private void UpdateMaxQueuedFrames(long queued)
        {
            int candidate = (int)Math.Clamp(queued, 0, int.MaxValue);
            int current;

            while (candidate > (current = Volatile.Read(ref _maxQueuedFrames)) &&
                   Interlocked.CompareExchange(ref _maxQueuedFrames, candidate, current) != current)
            {
            }
        }

        private void LogPresentStatsIfNeeded(long ticks)
        {
            long lastTicks = Interlocked.Read(ref _lastPresentStatsTicks);
            if (ticks - lastTicks < Stopwatch.Frequency * 10 ||
                Interlocked.CompareExchange(ref _lastPresentStatsTicks, ticks, lastTicks) != lastTicks)
            {
                return;
            }

            long enqueued = Interlocked.Read(ref _framesEnqueued);
            long presented = Interlocked.Read(ref _framesPresented);
            long dropped = Interlocked.Read(ref _framesDropped);
            int maxQueued = Interlocked.Exchange(ref _maxQueuedFrames, (int)Math.Clamp(enqueued - presented - dropped, 0, int.MaxValue));

            Logger.Info?.Print(
                LogClass.Gpu,
                $"Presentation telemetry: unbounded={_unboundedPresentMode}, target_fps={_unboundedPresentTargetFps}, " +
                $"interval_enqueued={enqueued - _lastStatsEnqueued}, interval_presented={presented - _lastStatsPresented}, " +
                $"interval_dropped={dropped - _lastStatsDropped}, max_queued={maxQueued}, " +
                $"total_enqueued={enqueued}, total_presented={presented}, total_dropped={dropped}, " +
                $"frames_available={Volatile.Read(ref _framesAvailable)}.");

            if (presented > 0 && presented == _lastStatsPresented)
            {
                Interlocked.Increment(ref _frameStallCount);
                // This loop still runs when guest frame production stops. The ordinary
                // PreFrame/AdvanceSequence snapshots do not, which hid the old stall.
                Logger.Warning?.Print(LogClass.Gpu,
                    $"GPU frame stall: sequence={_context.SequenceNumber}, sync={_context.SyncNumber}, " +
                    $"deferred_actions={_context.DeferredActions.Count}, {_context.Renderer.GetDiagnosticSnapshot()}");
            }

            _lastStatsEnqueued = enqueued;
            _lastStatsPresented = presented;
            _lastStatsDropped = dropped;
        }

        public string GetDiagnosticSnapshot()
        {
            long enqueued = Interlocked.Read(ref _framesEnqueued);
            long presented = Interlocked.Read(ref _framesPresented);
            long dropped = Interlocked.Read(ref _framesDropped);

            return $"unbounded={_unboundedPresentMode}, target_fps={_unboundedPresentTargetFps}, " +
                   $"enqueued={enqueued}, presented={presented}, dropped={dropped}, " +
                   $"queued={Math.Max(0, enqueued - presented - dropped)}, frames_available={Volatile.Read(ref _framesAvailable)}";
        }

        /// <summary>
        /// Indicate that a frame on the queue is ready to be acquired.
        /// </summary>
        public void SignalFrameReady()
        {
            Interlocked.Increment(ref _framesAvailable);
        }

        /// <summary>
        /// Determine if any frames are available, and decrement the available count if there are.
        /// </summary>
        /// <returns>True if a frame is available, false otherwise</returns>
        public bool ConsumeFrameAvailable()
        {
            if (Interlocked.CompareExchange(ref _framesAvailable, 0, 0) != 0)
            {
                Interlocked.Decrement(ref _framesAvailable);

                return true;
            }

            return false;
        }
    }
}
