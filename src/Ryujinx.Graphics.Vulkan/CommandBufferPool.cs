using Ryujinx.Common;
using Silk.NET.Vulkan;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace Ryujinx.Graphics.Vulkan
{
    readonly record struct CommandBufferPoolTrimResult(
        int RetiredSubmissions,
        int TotalCommandBuffers,
        int QueuedBefore,
        int QueuedAfter,
        int InUse,
        int DependenciesBefore,
        int WaitablesBefore,
        int PeakQueued,
        int PeakDependenciesPerCommandBuffer,
        int PeakWaitablesPerCommandBuffer);

    class CommandBufferPool : IDisposable
    {
        public const int MaxCommandBuffers = 16;
        internal const int IosCommandBuffers = 8;

        private readonly int _totalCommandBuffers;
        private readonly int _totalCommandBuffersMask;

        private readonly Vk _api;
        private readonly Device _device;
        private readonly Queue _queue;
        private readonly Lock _queueLock;
        private readonly bool _concurrentFenceWaitUnsupported;
        private readonly CommandPool _pool;
        private readonly Thread _owner;

        public bool OwnedByCurrentThread => _owner == Thread.CurrentThread;

        private struct ReservedCommandBuffer
        {
            public bool InUse;
            public bool InConsumption;
            public int SubmissionCount;
            public CommandBuffer CommandBuffer;
            public FenceHolder Fence;

            public List<IAuto> Dependants;
            public List<MultiFenceHolder> Waitables;

            public void Initialize(Vk api, Device device, CommandPool pool)
            {
                CommandBufferAllocateInfo allocateInfo = new()
                {
                    SType = StructureType.CommandBufferAllocateInfo,
                    CommandBufferCount = 1,
                    CommandPool = pool,
                    Level = CommandBufferLevel.Primary,
                };

                api.AllocateCommandBuffers(device, in allocateInfo, out CommandBuffer);

                Dependants = [];
                Waitables = [];
            }
        }

        private readonly ReservedCommandBuffer[] _commandBuffers;

        private readonly int[] _queuedIndexes;
        private int _queuedIndexesPtr;
        private int _queuedCount;
        private int _inUseCount;
        private int _peakQueuedCount;
        private int _peakDependenciesPerCommandBuffer;
        private int _peakWaitablesPerCommandBuffer;

        public unsafe CommandBufferPool(
            Vk api,
            Device device,
            Queue queue,
            Lock queueLock,
            uint queueFamilyIndex,
            bool concurrentFenceWaitUnsupported,
            bool isLight = false)
        {
            _api = api;
            _device = device;
            _queue = queue;
            _queueLock = queueLock;
            _concurrentFenceWaitUnsupported = concurrentFenceWaitUnsupported;
            _owner = Thread.CurrentThread;

            CommandPoolCreateInfo commandPoolCreateInfo = new()
            {
                SType = StructureType.CommandPoolCreateInfo,
                QueueFamilyIndex = queueFamilyIndex,
                Flags = CommandPoolCreateFlags.TransientBit |
                        CommandPoolCreateFlags.ResetCommandBufferBit,
            };

            api.CreateCommandPool(device, in commandPoolCreateInfo, null, out _pool).ThrowOnError();

            // We need at least 2 command buffers to get texture data in some cases. iOS has a
            // strict process-memory ceiling, so keep fewer completed submissions and their
            // dependencies resident than the desktop backends do.
            _totalCommandBuffers = GetTotalCommandBuffers(isLight, OperatingSystem.IsIOS());
            _totalCommandBuffersMask = _totalCommandBuffers - 1;

            _commandBuffers = new ReservedCommandBuffer[_totalCommandBuffers];

            _queuedIndexes = new int[_totalCommandBuffers];
            _queuedIndexesPtr = 0;
            _queuedCount = 0;

            for (int i = 0; i < _totalCommandBuffers; i++)
            {
                _commandBuffers[i].Initialize(api, device, _pool);
                WaitAndDecrementRef(i);
            }
        }

        internal static int GetTotalCommandBuffers(bool isLight, bool isIos)
        {
            return isLight ? 2 : isIos ? IosCommandBuffers : MaxCommandBuffers;
        }

        public void AddDependant(int cbIndex, IAuto dependant)
        {
            dependant.IncrementReferenceCount();
            _commandBuffers[cbIndex].Dependants.Add(dependant);
            _peakDependenciesPerCommandBuffer = Math.Max(
                _peakDependenciesPerCommandBuffer,
                _commandBuffers[cbIndex].Dependants.Count);
        }

        public void AddWaitable(MultiFenceHolder waitable)
        {
            lock (_commandBuffers)
            {
                for (int i = 0; i < _totalCommandBuffers; i++)
                {
                    ref ReservedCommandBuffer entry = ref _commandBuffers[i];

                    if (entry.InConsumption)
                    {
                        AddWaitable(i, waitable);
                    }
                }
            }
        }

        public void AddInUseWaitable(MultiFenceHolder waitable)
        {
            lock (_commandBuffers)
            {
                for (int i = 0; i < _totalCommandBuffers; i++)
                {
                    ref ReservedCommandBuffer entry = ref _commandBuffers[i];

                    if (entry.InUse)
                    {
                        AddWaitable(i, waitable);
                    }
                }
            }
        }

        public void AddWaitable(int cbIndex, MultiFenceHolder waitable)
        {
            ref ReservedCommandBuffer entry = ref _commandBuffers[cbIndex];
            if (waitable.AddFence(cbIndex, entry.Fence))
            {
                entry.Waitables.Add(waitable);
                _peakWaitablesPerCommandBuffer = Math.Max(
                    _peakWaitablesPerCommandBuffer,
                    entry.Waitables.Count);
            }
        }

        public bool HasWaitableOnRentedCommandBuffer(MultiFenceHolder waitable, int offset, int size)
        {
            lock (_commandBuffers)
            {
                for (int i = 0; i < _totalCommandBuffers; i++)
                {
                    ref ReservedCommandBuffer entry = ref _commandBuffers[i];

                    if (entry.InUse &&
                        waitable.HasFence(i) &&
                        waitable.IsBufferRangeInUse(i, offset, size))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        public bool IsFenceOnRentedCommandBuffer(FenceHolder fence)
        {
            lock (_commandBuffers)
            {
                for (int i = 0; i < _totalCommandBuffers; i++)
                {
                    ref ReservedCommandBuffer entry = ref _commandBuffers[i];

                    if (entry.InUse && entry.Fence == fence)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        public FenceHolder GetFence(int cbIndex)
        {
            return _commandBuffers[cbIndex].Fence;
        }

        public int GetSubmissionCount(int cbIndex)
        {
            return _commandBuffers[cbIndex].SubmissionCount;
        }

        private int FreeConsumed(bool wait)
        {
            int freeEntry = 0;

            while (_queuedCount > 0)
            {
                int index = _queuedIndexes[_queuedIndexesPtr];

                ref ReservedCommandBuffer entry = ref _commandBuffers[index];

                if (wait || !entry.InConsumption || entry.Fence.IsSignaled())
                {
                    WaitAndDecrementRef(index);

                    wait = false;
                    freeEntry = index;

                    _queuedCount--;
                    _queuedIndexesPtr = (_queuedIndexesPtr + 1) % _totalCommandBuffers;
                }
                else
                {
                    break;
                }
            }

            return freeEntry;
        }

        public CommandBufferScoped ReturnAndRent(CommandBufferScoped cbs)
        {
            Return(cbs);
            return Rent();
        }

        public CommandBufferScoped Rent()
        {
            lock (_commandBuffers)
            {
                int cursor = FreeConsumed(_inUseCount + _queuedCount == _totalCommandBuffers);

                for (int i = 0; i < _totalCommandBuffers; i++)
                {
                    ref ReservedCommandBuffer entry = ref _commandBuffers[cursor];

                    if (!entry.InUse && !entry.InConsumption)
                    {
                        entry.InUse = true;

                        _inUseCount++;

                        CommandBufferBeginInfo commandBufferBeginInfo = new()
                        {
                            SType = StructureType.CommandBufferBeginInfo,
                            // Every command buffer rented here is submitted exactly once before it is
                            // begun again. MoltenVK uses this bit to discard the Vulkan command list
                            // after immediate Metal encoding instead of retaining both representations.
                            Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
                        };

                        _api.BeginCommandBuffer(entry.CommandBuffer, in commandBufferBeginInfo).ThrowOnError();

                        return new CommandBufferScoped(this, entry.CommandBuffer, cursor);
                    }

                    cursor = (cursor + 1) & _totalCommandBuffersMask;
                }
            }

            throw new InvalidOperationException($"Out of command buffers (In use: {_inUseCount}, queued: {_queuedCount}, total: {_totalCommandBuffers})");
        }

        public void Return(CommandBufferScoped cbs)
        {
            Return(cbs, null, null, null);
        }

        public unsafe void Return(
            CommandBufferScoped cbs,
            ReadOnlySpan<Semaphore> waitSemaphores,
            ReadOnlySpan<PipelineStageFlags> waitDstStageMask,
            ReadOnlySpan<Semaphore> signalSemaphores)
        {
            lock (_commandBuffers)
            {
                int cbIndex = cbs.CommandBufferIndex;

                ref ReservedCommandBuffer entry = ref _commandBuffers[cbIndex];

                Debug.Assert(entry.InUse);
                Debug.Assert(entry.CommandBuffer.Handle == cbs.CommandBuffer.Handle);
                entry.InUse = false;
                entry.InConsumption = true;
                entry.SubmissionCount++;
                _inUseCount--;

                CommandBuffer commandBuffer = entry.CommandBuffer;

                _api.EndCommandBuffer(commandBuffer).ThrowOnError();

                fixed (Semaphore* pWaitSemaphores = waitSemaphores, pSignalSemaphores = signalSemaphores)
                {
                    fixed (PipelineStageFlags* pWaitDstStageMask = waitDstStageMask)
                    {
                        SubmitInfo sInfo = new()
                        {
                            SType = StructureType.SubmitInfo,
                            WaitSemaphoreCount = !waitSemaphores.IsEmpty ? (uint)waitSemaphores.Length : 0,
                            PWaitSemaphores = pWaitSemaphores,
                            PWaitDstStageMask = pWaitDstStageMask,
                            CommandBufferCount = 1,
                            PCommandBuffers = &commandBuffer,
                            SignalSemaphoreCount = !signalSemaphores.IsEmpty ? (uint)signalSemaphores.Length : 0,
                            PSignalSemaphores = pSignalSemaphores,
                        };

                        lock (_queueLock)
                        {
                            using var timing = ExecutionTimings.Measure(ExecutionStage.CommandBufferSubmit);
                            _api.QueueSubmit(_queue, 1, in sInfo, entry.Fence.GetUnsafe()).ThrowOnError();
                        }
                    }
                }

                int ptr = (_queuedIndexesPtr + _queuedCount) % _totalCommandBuffers;
                _queuedIndexes[ptr] = cbIndex;
                _queuedCount++;
                _peakQueuedCount = Math.Max(_peakQueuedCount, _queuedCount);
            }
        }

        private void WaitAndDecrementRef(int cbIndex, bool refreshFence = true)
        {
            ref ReservedCommandBuffer entry = ref _commandBuffers[cbIndex];

            if (entry.InConsumption)
            {
                entry.Fence.Wait();
                entry.InConsumption = false;
            }

            foreach (IAuto dependant in entry.Dependants)
            {
                dependant.DecrementReferenceCount(cbIndex);
            }

            foreach (MultiFenceHolder waitable in entry.Waitables)
            {
                waitable.RemoveFence(cbIndex);
                waitable.RemoveBufferUses(cbIndex);
            }

            entry.Dependants.Clear();
            entry.Waitables.Clear();
            entry.Fence?.Dispose();

            if (refreshFence)
            {
                entry.Fence = new FenceHolder(_api, _device, _concurrentFenceWaitUnsupported);
            }
            else
            {
                entry.Fence = null;
            }
        }

        /// <summary>
        /// Releases completed command-buffer dependencies and asks the backend to return unused
        /// command-pool storage to the system. This must run on the pool owner thread.
        /// </summary>
        /// <returns>Submission and dependency accounting captured before and after the trim</returns>
        public CommandBufferPoolTrimResult Trim()
        {
            Debug.Assert(OwnedByCurrentThread);

            lock (_commandBuffers)
            {
                int queuedBefore = _queuedCount;
                int dependenciesBefore = 0;
                int waitablesBefore = 0;

                for (int index = 0; index < _totalCommandBuffers; index++)
                {
                    dependenciesBefore += _commandBuffers[index].Dependants.Count;
                    waitablesBefore += _commandBuffers[index].Waitables.Count;
                }

                // Completed submissions release their dependencies; pending command buffers stay valid.
                FreeConsumed(wait: false);
                _api.TrimCommandPool(_device, _pool, 0);

                CommandBufferPoolTrimResult result = new(
                    queuedBefore - _queuedCount,
                    _totalCommandBuffers,
                    queuedBefore,
                    _queuedCount,
                    _inUseCount,
                    dependenciesBefore,
                    waitablesBefore,
                    _peakQueuedCount,
                    _peakDependenciesPerCommandBuffer,
                    _peakWaitablesPerCommandBuffer);

                _peakQueuedCount = _queuedCount;
                _peakDependenciesPerCommandBuffer = 0;
                _peakWaitablesPerCommandBuffer = 0;

                // Pending submissions survive a non-blocking trim. Seed the next reporting
                // window from their current ownership rather than briefly reporting zero.
                for (int index = 0; index < _totalCommandBuffers; index++)
                {
                    _peakDependenciesPerCommandBuffer = Math.Max(
                        _peakDependenciesPerCommandBuffer,
                        _commandBuffers[index].Dependants.Count);
                    _peakWaitablesPerCommandBuffer = Math.Max(
                        _peakWaitablesPerCommandBuffer,
                        _commandBuffers[index].Waitables.Count);
                }

                return result;
            }
        }

        public unsafe void Dispose()
        {
            for (int i = 0; i < _totalCommandBuffers; i++)
            {
                WaitAndDecrementRef(i, refreshFence: false);
            }

            _api.DestroyCommandPool(_device, _pool, null);
        }
    }
}
