using NUnit.Framework;
using Ryujinx.Graphics.Vulkan;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace Ryujinx.Tests.Graphics
{
    class VulkanMemoryAllocatorTests
    {
        private const ulong Capacity = 64 * 1024;
        private const ulong GuardSize = 64;

        // Seed an existing allocation block so the real pool can be exercised without a Vulkan
        // device. A permanent guard prevents the pool from releasing the block during the test.
        private static MemoryAllocatorBlockList CreateSeededPool(out MemoryAllocatorBlockList.Block block)
        {
            MemoryAllocatorBlockList allocator = new(null, default, 0, 4096, forBuffer: true);
            block = new(default, nint.Zero, Capacity);
            Assert.AreEqual(0, block.Allocate(GuardSize, 16));
            GetBlocks(allocator).Add(block);
            return allocator;
        }

        private static List<MemoryAllocatorBlockList.Block> GetBlocks(MemoryAllocatorBlockList allocator)
        {
            // This private setup seam avoids adding an injection API to production allocation code.
            return (List<MemoryAllocatorBlockList.Block>)typeof(MemoryAllocatorBlockList)
                .GetField("_blocks", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(allocator);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ConcurrentAllocationsDoNotOverlapOrLoseCapacity(bool synchronizeRounds)
        {
            const int WorkerCount = 8;
            const int Iterations = 512;

            using MemoryAllocatorBlockList allocator = CreateSeededPool(out _);
            using Barrier phase = new(WorkerCount);
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
            ConcurrentQueue<string> errors = new();
            Dictionary<ulong, ulong> liveRanges = new();
            Task[] workers = new Task[WorkerCount];

            void RunWorker(int workerIndex)
            {
                try
                {
                    phase.SignalAndWait(timeout.Token);

                    for (int iteration = 0; iteration < Iterations; iteration++)
                    {
                        MemoryAllocation? allocation = null;
                        bool registered = false;

                        try
                        {
                            if (errors.IsEmpty)
                            {
                                ulong size = (ulong)(16 * (1 + (iteration + workerIndex) % 16));
                                ulong alignment = 1UL << (4 + workerIndex % 4);
                                MemoryAllocation rented = allocator.Allocate(size, alignment, map: false);
                                allocation = rented;

                                lock (liveRanges)
                                {
                                    if (rented.Offset < GuardSize || rented.Offset + rented.Size > Capacity ||
                                        rented.Offset % alignment != 0)
                                    {
                                        throw new InvalidOperationException("Allocation is outside the guarded pool or misaligned.");
                                    }

                                    foreach ((ulong offset, ulong length) in liveRanges)
                                    {
                                        if (rented.Offset < offset + length && offset < rented.Offset + rented.Size)
                                        {
                                            throw new InvalidOperationException("Two live allocations overlap.");
                                        }
                                    }

                                    liveRanges.Add(rented.Offset, rented.Size);
                                    registered = true;
                                }
                            }
                        }
                        catch (Exception exception)
                        {
                            errors.Enqueue(exception.ToString());
                        }

                        if (synchronizeRounds)
                        {
                            phase.SignalAndWait(timeout.Token);
                        }

                        Thread.Yield();

                        if (allocation is MemoryAllocation toFree)
                        {
                            if (registered)
                            {
                                lock (liveRanges)
                                {
                                    liveRanges.Remove(toFree.Offset);
                                }
                            }

                            try
                            {
                                // Do not serialize Free with the test's live-range tracking lock.
                                toFree.Dispose();
                            }
                            catch (Exception exception)
                            {
                                errors.Enqueue(exception.ToString());
                            }
                        }

                        if (synchronizeRounds)
                        {
                            phase.SignalAndWait(timeout.Token);
                        }
                    }
                }
                catch (Exception exception)
                {
                    errors.Enqueue(exception.ToString());
                    timeout.Cancel();
                }
            }

            for (int index = 0; index < WorkerCount; index++)
            {
                int workerIndex = index;
                workers[index] = Task.Factory.StartNew(() => RunWorker(workerIndex), CancellationToken.None,
                    TaskCreationOptions.LongRunning, TaskScheduler.Default);
            }

            Assert.IsTrue(Task.WaitAll(workers, TimeSpan.FromSeconds(15)), "Concurrent pool operations did not finish.");
            Assert.IsEmpty(errors, string.Join(Environment.NewLine, errors));
            Assert.IsEmpty(liveRanges);

            // All temporary ranges must coalesce again, including their alignment gaps.
            using MemoryAllocation wholeRemainder = allocator.Allocate(Capacity - GuardSize, 1, map: false);
            Assert.AreEqual(GuardSize, wholeRemainder.Offset);
        }

        [Test]
        public void BlockIsDetachedOnlyAfterItsLastAllocationIsFreed()
        {
            using MemoryAllocatorBlockList allocator = CreateSeededPool(out MemoryAllocatorBlockList.Block block);
            MemoryAllocation remainder = allocator.Allocate(Capacity - GuardSize, 1, map: false);

            allocator.Free(block, 0, GuardSize);
            Assert.AreEqual(1, GetBlocks(allocator).Count);
            Assert.IsFalse(block.IsTotallyFree());

            // The synthetic block has no device handle, so destroying it also makes no Vulkan call.
            remainder.Dispose();
            Assert.IsEmpty(GetBlocks(allocator));
        }
    }
}
