using Silk.NET.Vulkan;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace Ryujinx.Graphics.Vulkan
{
    class BackgroundResource : IDisposable
    {
        private readonly VulkanRenderer _gd;
        private Device _device;

        private CommandBufferPool _pool;
        private PersistentFlushBuffer _flushBuffer;
        private readonly Thread _owner;
        private long _appliedPoolTrimGeneration;
        private long _pendingFlushTrimGeneration;
        private long _appliedFlushTrimGeneration;

        public BackgroundResource(VulkanRenderer gd, Device device)
        {
            _gd = gd;
            _device = device;
            _owner = Thread.CurrentThread;
        }

        public CommandBufferPool GetPool()
        {
            if (_pool == null)
            {
                bool useBackground = _gd.BackgroundQueue.Handle != 0 && _gd.Vendor != Vendor.Amd;
                Queue queue = useBackground ? _gd.BackgroundQueue : _gd.Queue;
                Lock queueLock = useBackground ? _gd.BackgroundQueueLock : _gd.QueueLock;

                lock (queueLock)
                {
                    _pool = new CommandBufferPool(
                        _gd.Api,
                        _device,
                        queue,
                        queueLock,
                        _gd.QueueFamilyIndex,
                        _gd.IsQualcommProprietary,
                        isLight: true);
                }
            }

            return _pool;
        }

        public PersistentFlushBuffer GetFlushBuffer()
        {
            // A returned readback span is valid until the next readback on this thread. Delay
            // a cross-thread pressure request until this boundary so it cannot free live CPU data.
            TrimPendingFlushBuffer();
            _flushBuffer ??= new PersistentFlushBuffer(_gd);

            return _flushBuffer;
        }

        private long TrimPendingFlushBuffer()
        {
            if (_pendingFlushTrimGeneration <= _appliedFlushTrimGeneration)
            {
                return 0;
            }

            _appliedFlushTrimGeneration = _pendingFlushTrimGeneration;
            return _flushBuffer?.Trim() ?? 0;
        }

        public (int RetiredSubmissions, long FlushBufferBytes) ApplyTrim(long generation, bool trimFlushBufferNow)
        {
            Debug.Assert(Thread.CurrentThread == _owner);

            int retiredSubmissions = 0;

            if (generation > _appliedPoolTrimGeneration)
            {
                _appliedPoolTrimGeneration = generation;
                _pendingFlushTrimGeneration = generation;
                CommandBufferPoolTrimResult? poolTrim = _pool?.Trim();
                retiredSubmissions = poolTrim?.RetiredSubmissions ?? 0;
            }

            long flushBufferBytes = trimFlushBufferNow ? TrimPendingFlushBuffer() : 0;

            return (retiredSubmissions, flushBufferBytes);
        }

        public void Dispose()
        {
            _pool?.Dispose();
            _flushBuffer?.Dispose();
        }
    }

    class BackgroundResources : IDisposable
    {
        private readonly VulkanRenderer _gd;
        private Device _device;

        private readonly Dictionary<Thread, BackgroundResource> _resources;
        private long _trimGeneration;

        public BackgroundResources(VulkanRenderer gd, Device device)
        {
            _gd = gd;
            _device = device;

            _resources = new Dictionary<Thread, BackgroundResource>();
        }

        private void Cleanup()
        {
            List<Thread> stoppedThreads = null;

            foreach (KeyValuePair<Thread, BackgroundResource> tuple in _resources)
            {
                if (!tuple.Key.IsAlive)
                {
                    tuple.Value.Dispose();
                    (stoppedThreads ??= []).Add(tuple.Key);
                }
            }

            if (stoppedThreads != null)
            {
                foreach (Thread thread in stoppedThreads)
                {
                    _resources.Remove(thread);
                }
            }
        }

        public BackgroundResource Get()
        {
            Thread thread = Thread.CurrentThread;
            BackgroundResource resource;
            long trimGeneration;

            lock (_resources)
            {
                if (!_resources.TryGetValue(thread, out resource))
                {
                    Cleanup();

                    resource = new BackgroundResource(_gd, _device);

                    _resources[thread] = resource;
                }

                trimGeneration = _trimGeneration;
            }

            // Each resource owns a Vulkan command pool created on this thread. Apply a pending
            // trim here rather than touching another thread's command pool from the renderer.
            resource.ApplyTrim(trimGeneration, trimFlushBufferNow: false);

            return resource;
        }

        /// <summary>
        /// Trims the calling thread's resource immediately and marks all other resources to trim
        /// themselves when their owner next accesses them.
        /// </summary>
        public (int Resources, int RetiredSubmissions, long FlushBufferBytes) Trim()
        {
            Thread thread = Thread.CurrentThread;
            BackgroundResource currentResource;
            long trimGeneration;
            int resourceCount;

            lock (_resources)
            {
                trimGeneration = ++_trimGeneration;
                resourceCount = _resources.Count;
                _resources.TryGetValue(thread, out currentResource);
            }

            (int RetiredSubmissions, long FlushBufferBytes) result =
                currentResource?.ApplyTrim(trimGeneration, trimFlushBufferNow: true) ?? (0, 0L);
            return (resourceCount, result.RetiredSubmissions, result.FlushBufferBytes);
        }

        public void Dispose()
        {
            lock (_resources)
            {
                foreach (BackgroundResource resource in _resources.Values)
                {
                    resource.Dispose();
                }
            }
        }
    }
}
