using Silk.NET.Vulkan;
using System;
using System.Diagnostics;

namespace Ryujinx.Graphics.Vulkan
{
    class DescriptorSetManager : IDisposable
    {
        public const uint MaxSets = 8;

        public class DescriptorPoolHolder : IDisposable
        {
            public Vk Api { get; }
            public Device Device { get; }

            private readonly DescriptorPool _pool;
            private int _freeDescriptors;
            private int _totalSets;
            private int _setsInUse;
            private bool _done;

            public unsafe DescriptorPoolHolder(Vk api, Device device, ReadOnlySpan<DescriptorPoolSize> poolSizes, bool updateAfterBind)
            {
                Api = api;
                Device = device;

                foreach (DescriptorPoolSize poolSize in poolSizes)
                {
                    _freeDescriptors += (int)poolSize.DescriptorCount;
                }

                fixed (DescriptorPoolSize* pPoolsSize = poolSizes)
                {
                    DescriptorPoolCreateInfo descriptorPoolCreateInfo = new()
                    {
                        SType = StructureType.DescriptorPoolCreateInfo,
                        Flags = updateAfterBind ? DescriptorPoolCreateFlags.UpdateAfterBindBit : DescriptorPoolCreateFlags.None,
                        MaxSets = MaxSets,
                        PoolSizeCount = (uint)poolSizes.Length,
                        PPoolSizes = pPoolsSize,
                    };

                    Api.CreateDescriptorPool(device, in descriptorPoolCreateInfo, null, out _pool).ThrowOnError();
                }
            }

            public unsafe DescriptorSetCollection AllocateDescriptorSets(ReadOnlySpan<DescriptorSetLayout> layouts, int consumedDescriptors)
            {
                TryAllocateDescriptorSets(layouts, consumedDescriptors, isTry: false, out DescriptorSetCollection dsc);
                return dsc;
            }

            public bool TryAllocateDescriptorSets(ReadOnlySpan<DescriptorSetLayout> layouts, int consumedDescriptors, out DescriptorSetCollection dsc)
            {
                return TryAllocateDescriptorSets(layouts, consumedDescriptors, isTry: true, out dsc);
            }

            private unsafe bool TryAllocateDescriptorSets(
                ReadOnlySpan<DescriptorSetLayout> layouts,
                int consumedDescriptors,
                bool isTry,
                out DescriptorSetCollection dsc)
            {
                Debug.Assert(!_done);

                DescriptorSet[] descriptorSets = new DescriptorSet[layouts.Length];

                fixed (DescriptorSet* pDescriptorSets = descriptorSets)
                {
                    fixed (DescriptorSetLayout* pLayouts = layouts)
                    {
                        DescriptorSetAllocateInfo descriptorSetAllocateInfo = new()
                        {
                            SType = StructureType.DescriptorSetAllocateInfo,
                            DescriptorPool = _pool,
                            DescriptorSetCount = (uint)layouts.Length,
                            PSetLayouts = pLayouts,
                        };

                        Result result = Api.AllocateDescriptorSets(Device, &descriptorSetAllocateInfo, pDescriptorSets);
                        if (isTry && result == Result.ErrorOutOfPoolMemory)
                        {
                            _totalSets = (int)MaxSets;
                            _done = true;
                            DestroyIfDone();
                            dsc = default;
                            return false;
                        }

                        result.ThrowOnError();
                    }
                }

                _freeDescriptors -= consumedDescriptors;
                _totalSets += layouts.Length;
                _setsInUse += layouts.Length;

                dsc = new DescriptorSetCollection(this, descriptorSets);
                return true;
            }

            public void FreeDescriptorSets(DescriptorSetCollection dsc)
            {
                _setsInUse -= dsc.SetsCount;
                Debug.Assert(_setsInUse >= 0);
                DestroyIfDone();
            }

            public bool CanFit(int setsCount, int descriptorsCount)
            {
                // Try to determine if an allocation with the given parameters will succeed.
                // An allocation may fail if the sets count or descriptors count exceeds the available counts
                // of the pool.
                // Not getting that right is not fatal, it will just create a new pool and try again,
                // but it is less efficient.

                if (_totalSets + setsCount <= MaxSets && _freeDescriptors >= descriptorsCount)
                {
                    return true;
                }

                _done = true;
                DestroyIfDone();
                return false;
            }

            private unsafe void DestroyIfDone()
            {
                if (_done && _setsInUse == 0)
                {
                    Api.DestroyDescriptorPool(Device, _pool, null);
                }
            }

            public void Retire()
            {
                _done = true;
                DestroyIfDone();
            }

            protected virtual void Dispose(bool disposing)
            {
                if (disposing)
                {
                    unsafe
                    {
                        Api.DestroyDescriptorPool(Device, _pool, null);
                    }
                }
            }

            public void Dispose()
            {
                GC.SuppressFinalize(this);
                Dispose(true);
            }
        }

        private readonly Device _device;
        private readonly DescriptorPoolHolder[] _currentPools;
        private int _poolCreationsSinceLastReport;
        private int _allocationRetriesSinceLastReport;

        public DescriptorSetManager(Device device, int poolCount)
        {
            _device = device;
            _currentPools = new DescriptorPoolHolder[poolCount];
        }

        public Auto<DescriptorSetCollection> AllocateDescriptorSet(
            Vk api,
            DescriptorSetLayout layout,
            ReadOnlySpan<DescriptorPoolSize> poolSizes,
            int poolIndex,
            int consumedDescriptors,
            bool updateAfterBind)
        {
            Span<DescriptorSetLayout> layouts = stackalloc DescriptorSetLayout[1];
            layouts[0] = layout;
            return AllocateDescriptorSets(api, layouts, poolSizes, poolIndex, consumedDescriptors, updateAfterBind);
        }

        public Auto<DescriptorSetCollection> AllocateDescriptorSets(
            Vk api,
            ReadOnlySpan<DescriptorSetLayout> layouts,
            ReadOnlySpan<DescriptorPoolSize> poolSizes,
            int poolIndex,
            int consumedDescriptors,
            bool updateAfterBind)
        {
            // If we fail the first time, just create a new pool and try again.

            DescriptorPoolHolder pool = GetPool(api, poolSizes, poolIndex, layouts.Length, consumedDescriptors, updateAfterBind);
            if (!pool.TryAllocateDescriptorSets(layouts, consumedDescriptors, out DescriptorSetCollection dsc))
            {
                _allocationRetriesSinceLastReport++;
                pool = GetPool(api, poolSizes, poolIndex, layouts.Length, consumedDescriptors, updateAfterBind);
                dsc = pool.AllocateDescriptorSets(layouts, consumedDescriptors);
            }

            return new Auto<DescriptorSetCollection>(dsc);
        }

        private DescriptorPoolHolder GetPool(
            Vk api,
            ReadOnlySpan<DescriptorPoolSize> poolSizes,
            int poolIndex,
            int setsCount,
            int descriptorsCount,
            bool updateAfterBind)
        {
            ref DescriptorPoolHolder currentPool = ref _currentPools[poolIndex];

            if (currentPool == null || !currentPool.CanFit(setsCount, descriptorsCount))
            {
                currentPool = new DescriptorPoolHolder(api, _device, poolSizes, updateAfterBind);
                _poolCreationsSinceLastReport++;
            }

            return currentPool;
        }

        /// <summary>
        /// Stops reusing the current pools. Pools with live descriptor sets are destroyed when
        /// their final <see cref="DescriptorSetCollection"/> is released.
        /// </summary>
        public (int Retired, int Created, int AllocationRetries) RetireCurrentPools()
        {
            int retired = 0;

            for (int index = 0; index < _currentPools.Length; index++)
            {
                DescriptorPoolHolder pool = _currentPools[index];
                if (pool != null)
                {
                    pool.Retire();
                    _currentPools[index] = null;
                    retired++;
                }
            }

            var activity = ConsumeActivityCounters();
            return (retired, activity.Created, activity.AllocationRetries);
        }

        /// <summary>
        /// Returns and resets pool activity accumulated since the previous report without
        /// retiring live pools. This lets pressure diagnostics include manual descriptor pools,
        /// whose stable cache indexes prevent them from being retired with automatic sets.
        /// </summary>
        public (int Created, int AllocationRetries) ConsumeActivityCounters()
        {
            int created = _poolCreationsSinceLastReport;
            int allocationRetries = _allocationRetriesSinceLastReport;
            _poolCreationsSinceLastReport = 0;
            _allocationRetriesSinceLastReport = 0;

            return (created, allocationRetries);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                for (int index = 0; index < _currentPools.Length; index++)
                {
                    _currentPools[index]?.Dispose();
                    _currentPools[index] = null;
                }
            }
        }

        public void Dispose()
        {
            GC.SuppressFinalize(this);
            Dispose(true);
        }
    }
}
