using Ryujinx.Common;
using Ryujinx.Common.Configuration;
using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL.Multithreading.Commands;
using Ryujinx.Graphics.GAL.Multithreading.Commands.Buffer;
using Ryujinx.Graphics.GAL.Multithreading.Commands.Renderer;
using Ryujinx.Graphics.GAL.Multithreading.Model;
using Ryujinx.Graphics.GAL.Multithreading.Resources;
using Ryujinx.Graphics.GAL.Multithreading.Resources.Programs;
using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Ryujinx.Graphics.GAL.Multithreading
{
    /// <summary>
    /// The ThreadedRenderer is a layer that can be put in front of any Renderer backend to make
    /// its processing happen on a separate thread, rather than intertwined with the GPU emulation.
    /// A new thread is created to handle the GPU command processing, separate from the renderer thread.
    /// Calls to the renderer, pipeline and resources are queued to happen on the renderer thread.
    /// </summary>
    public class ThreadedRenderer : IRenderer
    {
        private const int SpanPoolBytes = 8 * 1024 * 1024;
        private const int MaxRefsPerCommand = 2;
        private const int QueueCount = 10000;
        private const int RecentCommandCount = 32;

        private readonly int _elementSize;
        private readonly IRenderer _baseRenderer;
        private Thread _gpuThread;
        private Thread _backendThread;
        private volatile bool _running;
        private int _disposeRequested;

        private readonly AutoResetEvent _frameComplete = new(true);

        private readonly ManualResetEventSlim _galWorkAvailable;
        private readonly CircularSpanPool _spanPool;

        private readonly ManualResetEventSlim _invokeRun;
        private readonly AutoResetEvent _interruptRun;

        private bool _lastSampleCounterClear = true;

        private readonly byte[] _commandQueue;
        private readonly object[] _refQueue;

        private int _consumerPtr;
        private int _commandCount;

        private int _producerPtr;
        private int _lastProducedPtr;
        private int _invokePtr = -1;
        private ExceptionDispatchInfo _interruptFailure;
        private long _backgroundBufferCopies;

        private int _refProducerPtr;
        private int _refConsumerPtr;
        private readonly CommandType[] _recentCommands = new CommandType[RecentCommandCount];
        private int _recentCommandIndex;
        private int _recentCommandCount;

        public uint ProgramCount { get; set; } = 0;

        private Action _interruptAction;
        private readonly Lock _interruptLock = new();

        public event EventHandler<ScreenCaptureImageInfo> ScreenCaptured;

        internal BufferMap Buffers { get; }
        internal SyncMap Sync { get; }
        internal CircularSpanPool SpanPool { get; }
        internal ProgramQueue Programs { get; }

        public IPipeline Pipeline { get; }
        public IWindow Window { get; }

        public IRenderer BaseRenderer => _baseRenderer;

        public bool PreferThreading => _baseRenderer.PreferThreading;

        public ThreadedRenderer(IRenderer renderer)
        {
            _baseRenderer = renderer;

            renderer.ScreenCaptured += (sender, info) => ScreenCaptured?.Invoke(this, info);
            renderer.SetInterruptAction(Interrupt);

            Pipeline = new ThreadedPipeline(this);
            Window = new ThreadedWindow(this, renderer);
            Buffers = new BufferMap();
            Sync = new SyncMap();
            Programs = new ProgramQueue(renderer);

            _galWorkAvailable = new ManualResetEventSlim(false);
            _invokeRun = new ManualResetEventSlim();
            _interruptRun = new AutoResetEvent(false);
            _spanPool = new CircularSpanPool(this, SpanPoolBytes);
            SpanPool = _spanPool;

            _elementSize = BitUtils.AlignUp(CommandHelper.GetMaxCommandSize(), 4);

            _commandQueue = new byte[_elementSize * QueueCount];
            _refQueue = new object[MaxRefsPerCommand * QueueCount];
        }

        public void RunLoop(ThreadStart gpuLoop)
        {
            _running = true;

            _backendThread = Thread.CurrentThread;

            _gpuThread = new Thread(gpuLoop)
            {
                Name = "GPU.MainThread",
            };

            _gpuThread.Start();

            RenderLoop();
        }

        public void RenderLoop()
        {
            // Power through the render queue until the Gpu thread work is done.

            while (_running)
            {
                _galWorkAvailable.Wait();
                _galWorkAvailable.Reset();

                if (Volatile.Read(ref _interruptAction) != null)
                {
                    // The caller may hold guest range/lifetime locks. This interrupt must never
                    // drain the emulation producer or call back into guest memory tracking.
                    try
                    {
                        _interruptAction();
                    }
                    catch (Exception exception)
                    {
                        _interruptFailure = ExceptionDispatchInfo.Capture(exception);
                    }
                    finally
                    {
                        Interlocked.Exchange(ref _interruptAction, null);
                        _interruptRun.Set();
                    }
                }

                // The other thread can only increase the command count.
                // We can assume that if it is above 0, it will stay there or get higher.

                while (Volatile.Read(ref _commandCount) > 0 && Volatile.Read(ref _interruptAction) == null)
                {
                    int commandPtr = _consumerPtr;

                    Span<byte> command = new(_commandQueue, commandPtr * _elementSize, _elementSize);

                    // Run the command. Keep a small allocation-free history so a fatal backend
                    // exception identifies both the failing operation and the commands leading to it.

                    CommandType commandType = (CommandType)command[^1];
                    RecordCommand(commandType);

                    try
                    {
                        CommandHelper.RunCommand(command, this, _baseRenderer);
                    }
                    catch (Exception exception)
                    {
                        try
                        {
                            Logger.Log log = Logger.Error ?? Logger.Notice;
                            log.Print(
                                LogClass.Gpu,
                                $"Threaded renderer command failed: command={commandType}, {GetDiagnosticSnapshot()}, " +
                                $"recent_commands=[{GetRecentCommands()}]. Exception: {exception}");
                            Logger.Flush();
                        }
                        catch
                        {
                            // Diagnostics must never replace the original backend exception,
                            // especially when the failure itself is allocation related.
                        }

                        throw;
                    }

                    if (Interlocked.CompareExchange(ref _invokePtr, -1, commandPtr) == commandPtr)
                    {
                        _invokeRun.Set();
                    }

                    _consumerPtr = (_consumerPtr + 1) % QueueCount;

                    Interlocked.Decrement(ref _commandCount);
                }
            }
        }

        private void RecordCommand(CommandType commandType)
        {
            _recentCommands[_recentCommandIndex] = commandType;
            _recentCommandIndex = (_recentCommandIndex + 1) % _recentCommands.Length;
            _recentCommandCount = Math.Min(_recentCommandCount + 1, _recentCommands.Length);
        }

        private string GetRecentCommands()
        {
            int count = _recentCommandCount;
            string[] commands = new string[count];
            int start = (_recentCommandIndex - count + _recentCommands.Length) % _recentCommands.Length;

            for (int i = 0; i < count; i++)
            {
                commands[i] = _recentCommands[(start + i) % _recentCommands.Length].ToString();
            }

            return string.Join(",", commands);
        }

        /// <summary>
        /// Returns compact queue and buffer-map state for crash and memory-pressure diagnostics.
        /// </summary>
        public string GetDiagnosticSnapshot()
        {
            var buffers = Buffers.GetDiagnostics();

            return $"queue_pending={Volatile.Read(ref _commandCount)}, consumer={Volatile.Read(ref _consumerPtr)}, " +
                   $"background_buffer_copies={Interlocked.Read(ref _backgroundBufferCopies)}, " +
                   $"producer={Volatile.Read(ref _producerPtr)}, ref_consumer={Volatile.Read(ref _refConsumerPtr)}, " +
                   $"ref_producer={Volatile.Read(ref _refProducerPtr)}, buffers_issued={buffers.Issued}, " +
                   $"buffers_mapped={buffers.Mapped}, buffers_in_flight={buffers.InFlight}, buffer_map_misses={buffers.Misses}";
        }

        internal SpanRef<T> CopySpan<T>(ReadOnlySpan<T> data) where T : unmanaged
        {
            return _spanPool.Insert(data);
        }

        private TableRef<T> Ref<T>(T reference)
        {
            return new TableRef<T>(this, reference);
        }

        internal unsafe T* New<T>() where T : unmanaged, IGALCommand
        {
            if (_producerPtr == (Volatile.Read(ref _consumerPtr) + QueueCount - 1) % QueueCount)
            {
                using var timing = ExecutionTimings.Measure(ExecutionStage.GalQueueBackpressure);
                while (_producerPtr == (Volatile.Read(ref _consumerPtr) + QueueCount - 1) % QueueCount)
                {
                    // The consumer can only move forward. Measure existing backpressure;
                    // do not take timestamps on the ordinary per-draw path.
                    Thread.Sleep(1);
                }
            }

            int taken = _producerPtr;
            _lastProducedPtr = taken;

            _producerPtr = (_producerPtr + 1) % QueueCount;

            Span<byte> memory = new(_commandQueue, taken * _elementSize, _elementSize);
            T* result = (T*)Unsafe.AsPointer(ref memory.GetPinnableReference());
            // ref T result = ref Unsafe.As<byte, T>(ref MemoryMarshal.GetReference(memory));

            memory[^1] = (byte)(result)->CommandType;

            return result;
        }

        internal int AddTableRef(object obj)
        {
            // The reference table is sized so that it will never overflow, so long as the references are taken after the command is allocated.

            int index = _refProducerPtr;

            _refQueue[index] = obj;

            _refProducerPtr = (_refProducerPtr + 1) % _refQueue.Length;

            return index;
        }

        internal object RemoveTableRef(int index)
        {
            Debug.Assert(index == _refConsumerPtr);

            object result = _refQueue[_refConsumerPtr];
            _refQueue[_refConsumerPtr] = null;

            _refConsumerPtr = (_refConsumerPtr + 1) % _refQueue.Length;

            return result;
        }

        internal void QueueCommand()
        {
            int result = Interlocked.Increment(ref _commandCount);

            if (result == 1)
            {
                _galWorkAvailable.Set();
            }
        }

        internal void InvokeCommand()
        {
            _invokeRun.Reset();
            _invokePtr = _lastProducedPtr;

            QueueCommand();

            // Wait for the command to complete.
            using var timing = ExecutionTimings.Measure(ExecutionStage.GalInvokeWait);
            _invokeRun.Wait();
        }

        internal void WaitForFrame()
        {
            using var timing = ExecutionTimings.Measure(ExecutionStage.GalFrameWait);
            _frameComplete.WaitOne();
        }

        internal void SignalFrame()
        {
            _frameComplete.Set();
        }

        internal bool IsGpuThread()
        {
            return Thread.CurrentThread == _gpuThread;
        }

        public unsafe void BackgroundContextAction(Action action, bool alwaysBackground = false)
        {
            if (IsGpuThread() && !alwaysBackground)
            {
                // The action must be performed on the render thread.
                New<ActionCommand>()->Set(Ref(action));
                InvokeCommand();
            }
            else
            {
                _baseRenderer.BackgroundContextAction(action, true);
            }
        }

        public void TrimMemory(bool aggressive, ulong availableMemoryBytes)
        {
            // Pressure is reported by the GPU emulation thread. Queueing an invoked action here
            // first drains every earlier GAL command, then runs the trim on the backend owner thread.
            BackgroundContextAction(() => _baseRenderer.TrimMemory(aggressive, availableMemoryBytes));
        }

        public unsafe BufferHandle CreateBuffer(int size, BufferAccess access)
        {
            BufferHandle handle = Buffers.CreateBufferHandle();
            New<CreateBufferAccessCommand>()->Set(handle, size, access);
            QueueCommand();

            return handle;
        }

        public unsafe BufferHandle CreateBuffer(nint pointer, int size)
        {
            BufferHandle handle = Buffers.CreateBufferHandle();
            New<CreateHostBufferCommand>()->Set(handle, pointer, size);
            QueueCommand();

            return handle;
        }

        public unsafe BufferHandle CreateBufferSparse(ReadOnlySpan<BufferRange> storageBuffers)
        {
            BufferHandle handle = Buffers.CreateBufferHandle();
            New<CreateBufferSparseCommand>()->Set(handle, CopySpan(storageBuffers));
            QueueCommand();

            return handle;
        }

        public unsafe IImageArray CreateImageArray(int size, bool isBuffer)
        {
            ThreadedImageArray imageArray = new(this);
            New<CreateImageArrayCommand>()->Set(Ref(imageArray), size, isBuffer);
            QueueCommand();

            return imageArray;
        }

        public unsafe IProgram CreateProgram(ShaderSource[] shaders, ShaderInfo info)
        {
            ThreadedProgram program = new(this);

            SourceProgramRequest request = new(program, shaders, info);

            Programs.Add(request);

            ProgramCount++;

            New<CreateProgramCommand>()->Set(Ref((IProgramRequest)request));
            QueueCommand();

            return program;
        }

        public unsafe ISampler CreateSampler(SamplerCreateInfo info)
        {
            ThreadedSampler sampler = new(this);
            New<CreateSamplerCommand>()->Set(Ref(sampler), info);
            QueueCommand();

            return sampler;
        }

        public unsafe void CreateSync(ulong id, bool strict)
        {
            Sync.CreateSyncHandle(id);
            New<CreateSyncCommand>()->Set(id, strict);
            QueueCommand();
        }

        public unsafe ITexture CreateTexture(TextureCreateInfo info)
        {
            if (IsGpuThread())
            {
                ThreadedTexture texture = new(this, info);
                New<CreateTextureCommand>()->Set(Ref(texture), info);
                QueueCommand();

                return texture;
            }
            else
            {
                ThreadedTexture texture = new(this, info)
                {
                    Base = _baseRenderer.CreateTexture(info),
                };

                return texture;
            }
        }
        public unsafe ITextureArray CreateTextureArray(int size, bool isBuffer)
        {
            ThreadedTextureArray textureArray = new(this);
            New<CreateTextureArrayCommand>()->Set(Ref(textureArray), size, isBuffer);
            QueueCommand();

            return textureArray;
        }

        public unsafe void DeleteBuffer(BufferHandle buffer)
        {
            New<BufferDisposeCommand>()->Set(buffer);
            QueueCommand();
        }

        public unsafe PinnedSpan<byte> GetBufferData(BufferHandle buffer, int offset, int size)
        {
            if (IsGpuThread())
            {
                ResultBox<PinnedSpan<byte>> box = new();
                New<BufferGetDataCommand>()->Set(buffer, offset, size, Ref(box));
                InvokeCommand();

                return box.Result;
            }
            else
            {
                return _baseRenderer.GetBufferData(Buffers.MapBufferBlocking(buffer), offset, size);
            }
        }

        /// <summary>
        /// Reconciles a completed virtual-buffer write during external CPU readback. The caller
        /// has waited for the guest sync and holds both buffers alive. Unlike the normal pipeline
        /// API, this path does not publish from a second producer into the SPSC GAL ring.
        /// </summary>
        public void CopyBufferForReadback(BufferHandle source, BufferHandle destination, int srcOffset, int dstOffset, int size)
        {
            if (IsGpuThread())
            {
                Pipeline.CopyBuffer(source, destination, srcOffset, dstOffset, size);
                return;
            }

            // Wait for queued creations on the calling thread, NEVER inside an interrupt:
            // the consumer cannot execute a CreateBuffer while it is running the interrupt.
            Buffers.MapBufferBlocking(source);
            Buffers.MapBufferBlocking(destination);

            Interrupt(() =>
            {
                if (!Buffers.TryMapBuffer(source, out BufferHandle nativeSource) ||
                    !Buffers.TryMapBuffer(destination, out BufferHandle nativeDestination))
                {
                    throw new InvalidOperationException("Readback reconciliation outlived its buffer mapping. " + GetDiagnosticSnapshot());
                }

                // Runs between backend commands, so pipeline, mirror invalidation, and native
                // fence ownership all stay on their existing owner. No guest locks are acquired.
                _baseRenderer.Pipeline.CopyBuffer(nativeSource, nativeDestination, srcOffset, dstOffset, size);
                Interlocked.Increment(ref _backgroundBufferCopies);
            });
        }

        internal void CopyTextureForReadback(ThreadedTexture texture, BufferRange range, int layer, int level, int stride)
        {
            // ThreadedTexture holds its copy/release gate. Complete native work before allowing
            // Release to be published, without injecting a second producer into the GAL ring.
            ThreadedHelpers.SpinUntilNonNull(ref texture.Base);
            Buffers.MapBufferBlocking(range.Handle);
            Interrupt(() =>
            {
                if (!Buffers.TryMapBufferRange(range, out BufferRange mapped))
                    throw new InvalidOperationException("Missing texture readback target. " + GetDiagnosticSnapshot());
                texture.Base.CopyTo(mapped, layer, level, stride);
            });
        }

        public unsafe Capabilities GetCapabilities()
        {
            ResultBox<Capabilities> box = new();
            New<GetCapabilitiesCommand>()->Set(Ref(box));
            InvokeCommand();

            return box.Result;
        }

        public ulong GetCurrentSync()
        {
            return _baseRenderer.GetCurrentSync();
        }

        public HardwareInfo GetHardwareInfo()
        {
            return _baseRenderer.GetHardwareInfo();
        }

        /// <summary>
        /// Initialize the base renderer. Must be called on the render thread.
        /// </summary>
        /// <param name="logLevel">Log level to use</param>
        public void Initialize(GraphicsDebugLevel logLevel)
        {
            _baseRenderer.Initialize(logLevel);
        }

        public unsafe IProgram LoadProgramBinary(byte[] programBinary, bool hasFragmentShader, ShaderInfo info)
        {
            ThreadedProgram program = new(this);

            BinaryProgramRequest request = new(program, programBinary, hasFragmentShader, info);
            Programs.Add(request);

            New<CreateProgramCommand>()->Set(Ref((IProgramRequest)request));
            QueueCommand();

            return program;
        }

        public unsafe void PreFrame()
        {
            New<PreFrameCommand>();
            QueueCommand();
        }

        public unsafe ICounterEvent ReportCounter(CounterType type, EventHandler<ulong> resultHandler, float divisor, bool hostReserved)
        {
            ThreadedCounterEvent evt = new(this, type, _lastSampleCounterClear);
            New<ReportCounterCommand>()->Set(Ref(evt), type, Ref(resultHandler), divisor, hostReserved);
            QueueCommand();

            if (type == CounterType.SamplesPassed)
            {
                _lastSampleCounterClear = false;
            }

            return evt;
        }

        public unsafe void ResetCounter(CounterType type)
        {
            New<ResetCounterCommand>()->Set(type);
            QueueCommand();
            _lastSampleCounterClear = true;
        }

        public void Screenshot()
        {
            _baseRenderer.Screenshot();
        }

        public unsafe void SetBufferData(BufferHandle buffer, int offset, ReadOnlySpan<byte> data)
        {
            New<BufferSetDataCommand>()->Set(buffer, offset, CopySpan(data));
            QueueCommand();
        }

        public unsafe void UpdateCounters()
        {
            New<UpdateCountersCommand>();
            QueueCommand();
        }

        public void WaitSync(ulong id)
        {
            Sync.WaitSyncAvailability(id);

            _baseRenderer.WaitSync(id);
        }

        private void Interrupt(Action action)
        {
            // Interrupt the backend thread from any external thread and invoke the given action.

            if (Thread.CurrentThread == _backendThread)
            {
                // If this is called from the backend thread, the action can run immediately.
                action();
            }
            else
            {
                lock (_interruptLock)
                {
                    _interruptFailure = null;
                    while (Interlocked.CompareExchange(ref _interruptAction, action, null) != null)
                    {
                    }

                    _galWorkAvailable.Set();

                    using var timing = ExecutionTimings.Measure(ExecutionStage.GalExternalInterruptWait);
                    _interruptRun.WaitOne();
                    _interruptFailure?.Throw();
                }
            }
        }

        public void SetInterruptAction(Action<Action> interruptAction)
        {
            // Threaded renderer ignores given interrupt action, as it provides its own to the child renderer.
        }

        public bool PrepareHostMapping(nint address, ulong size)
        {
            return _baseRenderer.PrepareHostMapping(address, size);
        }

        public void FlushThreadedCommands()
        {
            SpinWait wait = new();

            while (Volatile.Read(ref _commandCount) > 0)
            {
                wait.SpinOnce();
            }
        }

        public void Dispose()
        {
            GC.SuppressFinalize(this);

            if (Thread.CurrentThread == _gpuThread)
            {
                throw new InvalidOperationException("The GPU producer cannot dispose its own threaded renderer.");
            }

            if (Interlocked.Exchange(ref _disposeRequested, 1) != 0)
            {
                return;
            }

            // The caller has stopped the producer. Keep the consumer alive while joining it
            // and while draining resource deletions queued by GpuContext.Dispose afterwards.
            if (_gpuThread != null && _gpuThread.IsAlive)
            {
                _gpuThread.Join();
            }

            if (_backendThread != null && _backendThread.IsAlive && Thread.CurrentThread != _backendThread)
            {
                FlushThreadedCommands();
                Interrupt(() =>
                {
                    _baseRenderer.Dispose();
                    _running = false;
                });
                _backendThread.Join();
            }
            else
            {
                _running = false;
                _baseRenderer.Dispose();
            }

            _frameComplete.Dispose();
            _galWorkAvailable.Dispose();
            _invokeRun.Dispose();
            _interruptRun.Dispose();
            Sync.Dispose();
        }
    }
}
