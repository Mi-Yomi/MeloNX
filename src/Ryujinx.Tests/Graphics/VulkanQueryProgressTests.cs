using NUnit.Framework;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Vulkan;
using Ryujinx.Graphics.Vulkan.Queries;
using Silk.NET.Core;
using Silk.NET.Core.Contexts;
using Silk.NET.Vulkan;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using VkBuffer = Silk.NET.Vulkan.Buffer;
using VkQueue = Silk.NET.Vulkan.Queue;

namespace Ryujinx.Tests.Graphics
{
    public class VulkanQueryProgressTests
    {
        private const long Sentinel = unchecked((long)0xFFFFFFFEFFFFFFFE);

        [Test]
        public void ReportOutsideRenderPassCopiesBeforeQueryResetAndIdleSubmission()
        {
            using Fixture fixture = new();
            BufferedQuery query = fixture.CreateQuery();
            query.End(true);
            query.PoolReset(fixture.Pipeline.CurrentCommandBuffer.CommandBuffer, 1);
            Assert.That(fixture.Events, Is.EqualTo(new[] { "end-query", "copy-query", "reset-query" }));
            Assert.That(fixture.Pipeline.FlushPendingWorkAtIdle(), Is.True);
            Assert.That(fixture.Events, Is.EqualTo(new[] { "end-query", "copy-query", "reset-query", "submit" }));
            Assert.That(fixture.Pipeline.FlushPendingWorkAtIdle(), Is.False, "Idle must not submit empty buffers repeatedly.");
        }

        [Test]
        public void QueuedCopyWithoutActivePassIsRecordedBeforeFlushSubmit()
        {
            using Fixture fixture = new();
            BufferedQuery query = fixture.CreateQuery();
            fixture.PendingCopies.Add(query);
            fixture.Pipeline.AutoFlush.RegisterPendingQuery();
            fixture.Pipeline.FlushPendingWorkAtIdle();
            Assert.That(fixture.Events, Is.EqualTo(new[] { "copy-query", "submit" }));
            Assert.That(fixture.PendingCopies, Is.Empty);
        }

        [Test]
        public void QueuedCopyIsRecordedBeforeBeginQueryResetsPoolOutsidePass()
        {
            using Fixture fixture = new();
            BufferedQuery query = fixture.CreateQuery();
            fixture.PendingCopies.Add(query);
            fixture.Pipeline.BeginQuery(query, new QueryPool(77), true, true, false);
            Assert.That(fixture.Events, Is.EqualTo(new[] { "copy-query", "reset-query", "begin-query" }));
        }

        [Test]
        public void IdleCannotSubmitCapturedCommandBufferInsideDisposalScope()
        {
            using Fixture fixture = new();
            fixture.CreateQuery().End(true);
            CommandBufferScoped captured = fixture.Pipeline.CurrentCommandBuffer;
            using (fixture.Pipeline.DeferDisposalFlushes())
            {
                Assert.That(fixture.Pipeline.FlushPendingWorkAtIdle(), Is.False);
                Assert.That(fixture.Pipeline.CurrentCommandBuffer.CommandBuffer, Is.EqualTo(captured.CommandBuffer));
                Assert.That(fixture.Events, Does.Not.Contain("submit"));
            }

            Assert.That(fixture.Pipeline.FlushPendingWorkAtIdle(), Is.True);
            Assert.That(fixture.Events, Does.Contain("submit"));
        }

        [Test]
        public void IdleSubmitsDeferredDisposalWeightOnceWithoutQuery()
        {
            using Fixture fixture = new();
            Set(fixture.Pipeline, "_byteWeight", 256 * 1024 * 1024UL);
            Assert.That(fixture.Pipeline.FlushPendingWorkAtIdle(), Is.True);
            Assert.That(fixture.Pipeline.FlushPendingWorkAtIdle(), Is.False);
            Assert.That(fixture.Events, Is.EqualTo(new[] { "submit" }));
        }

        [Test]
        public void UnsupportedNativeConditionalRenderingFlushesQueryAndUsesCpuResult()
        {
            using Fixture fixture = new();
            BufferedQuery query = fixture.CreateQuery();
            CounterQueueEvent evt = fixture.CreateEvent(fixture.CreateQueue(), query);
            Set(evt, "<ClearCounter>k__BackingField", true);
            query.End(true);
            Assert.That(fixture.Pipeline.TryHostConditionalRendering(evt, 0, false), Is.False);
            Assert.That(fixture.Events, Is.EqualTo(new[] { "end-query", "copy-query", "submit" }));
            Assert.That(Get<bool>(evt, "_hostAccessReserved"), Is.False);
            Assert.That(Get<int>(evt, "_refCount"), Is.EqualTo(1));
            fixture.Pipeline.EndHostConditionalRendering();
            evt.Dispose();
        }

        [TestCase(Sentinel, false)]
        [TestCase(unchecked((long)0xFFFFFFFE00000003), false)]
        [TestCase(0L, true)]
        [TestCase(4294967299L, true)]
        public void NonBlockingResultRejectsIncomplete64BitWrite(long value, bool ready)
        {
            using Fixture fixture = new();
            BufferedQuery query = fixture.CreateQuery();
            fixture.Write(query, value);
            Assert.That(query.TryGetResult(out long actual), Is.EqualTo(ready));
            Assert.That(actual, Is.EqualTo(value));
            Assert.That(query.TryAwaitResult(out _, timeoutMilliseconds: 0), Is.EqualTo(ready));
        }

        [Test]
        public void TimeoutDoesNotPublishOrRetireAndLaterResultCompletesExactlyOnce()
        {
            using Fixture fixture = new();
            CounterQueue queue = fixture.CreateQueue();
            BufferedQuery query = fixture.CreateQuery();
            CounterQueueEvent evt = fixture.CreateEvent(queue, query);
            Set(evt, "<ClearCounter>k__BackingField", true);
            int calls = 0;
            evt.OnResult += (_, _) => calls++;
            ulong accumulated = 91;

            Assert.That(evt.TryConsume(ref accumulated, true, timeoutMilliseconds: 0), Is.False);
            Assert.That(accumulated, Is.EqualTo(91));
            Assert.That(calls, Is.Zero);
            Assert.That(evt.Disposed, Is.False);
            Assert.That(fixture.Pool(queue), Is.Empty, "An uncompleted query must not be reset or reused.");

            fixture.Write(query, 7);
            Assert.That(evt.TryConsume(ref accumulated, true, timeoutMilliseconds: 0), Is.True);
            Assert.That(accumulated, Is.EqualTo(7));
            Assert.That(calls, Is.EqualTo(1));
            Assert.That(evt.Disposed, Is.True);
            Assert.That(fixture.Pool(queue).Count, Is.EqualTo(1));
            evt.Dispose();
            Assert.That(evt.TryConsume(ref accumulated, false), Is.True);
            Assert.That(calls, Is.EqualTo(1));
            Assert.That(fixture.Pool(queue).Count, Is.EqualTo(1));
        }

        [Test]
        public void ConsumerRetainsTimedOutHeadBeforeProcessingLaterReadyResult()
        {
            using Fixture fixture = new();
            CounterQueue queue = fixture.CreateQueue();
            BufferedQuery first = fixture.CreateQuery();
            BufferedQuery second = fixture.CreateQuery();
            fixture.Write(second, 5);
            CounterQueueEvent firstEvent = fixture.CreateEvent(queue, first);
            CounterQueueEvent secondEvent = fixture.CreateEvent(queue, second);
            List<ulong> results = [];
            firstEvent.OnResult += (_, value) => { lock (results) results.Add(value); };
            secondEvent.OnResult += (_, value) => { lock (results) results.Add(value); };
            fixture.StartConsumer(queue, firstEvent, secondEvent);

            Assert.That(SpinWait.SpinUntil(() => first.WaitTimeouts > 0, 3000), Is.True);
            Assert.That(firstEvent.Disposed, Is.False);
            Assert.That(secondEvent.Disposed, Is.False);
            lock (results) Assert.That(results, Is.Empty);
            Task flush = Task.Run(() => queue.Flush(true));
            Assert.That(SpinWait.SpinUntil(() => Get<int>(queue, "_waiterCount") > 0 || flush.IsCompleted, 3000), Is.True);
            Assert.That(flush.IsCompleted, Is.False,
                "A blocking flush must not consume the ready second event ahead of the pending first event.");
            fixture.Write(first, 3);
            Assert.That(SpinWait.SpinUntil(() => secondEvent.Disposed, 3000), Is.True);
            Assert.That(flush.Wait(3000), Is.True);
            lock (results) Assert.That(results, Is.EqualTo(new ulong[] { 3, 8 }));
            fixture.StopConsumer(queue);
        }

        [Test]
        public void DisposalCancelsUnavailableResultWithoutCallbackOrShutdownWait()
        {
            using Fixture fixture = new();
            CounterQueue queue = fixture.CreateQueue();
            CounterQueueEvent evt = fixture.CreateEvent(queue, fixture.CreateQuery());
            int callbacks = 0;
            evt.OnResult += (_, _) => Interlocked.Increment(ref callbacks);
            fixture.StartConsumer(queue, evt);
            Assert.That(SpinWait.SpinUntil(() => Get<CounterQueueEvent>(queue, "_activeEvent") != null, 3000), Is.True);
            Task stop = Task.Run(() => fixture.StopConsumer(queue));
            Assert.That(stop.Wait(3000), Is.True);
            Assert.That(callbacks, Is.Zero);
            Assert.That(evt.Disposed, Is.True);
        }

        [Test]
        public void ExplicitDisposeDoesNotBlockBackendOrRecycleQueryDuringConsumerRead()
        {
            using Fixture fixture = new();
            CounterQueue queue = fixture.CreateQueue();
            BufferedQuery query = fixture.CreateQuery();
            CounterQueueEvent evt = fixture.CreateEvent(queue, query);
            int callbacks = 0;
            evt.OnResult += (_, _) => Interlocked.Increment(ref callbacks);
            ulong accumulated = 19;
            Task<bool> consume = Task.Run(() => evt.TryConsume(ref accumulated, true, timeoutMilliseconds: 3000));
            Assert.That(SpinWait.SpinUntil(() => Get<int>(evt, "_refCount") == 2, 3000), Is.True);
            evt.Dispose();
            Assert.That(evt.Disposed, Is.True);
            Assert.That(consume.IsCompleted, Is.False);
            Assert.That(fixture.Pool(queue), Is.Empty);
            fixture.Write(query, 7);
            Assert.That(consume.Wait(3000), Is.True);
            Assert.That(consume.Result, Is.True);
            Assert.That(accumulated, Is.EqualTo(19));
            Assert.That(callbacks, Is.Zero);
            Assert.That(fixture.Pool(queue).Count, Is.EqualTo(1));
        }

        [Test]
        public void PoolResetSerializesAgainstConsumerReturningQuery()
        {
            using Fixture fixture = new();
            CounterQueue queue = fixture.CreateQueue();
            BufferedQuery first = fixture.CreateQuery();
            BufferedQuery returned = fixture.CreateQuery();
            fixture.Pool(queue).Enqueue(first);
            using ManualResetEventSlim returnStarted = new();
            using ManualResetEventSlim returnCompleted = new();
            Task returnTask = null;
            bool changedDuringReset = false;
            fixture.OnReset = () =>
            {
                returnTask = Task.Run(() =>
                {
                    returnStarted.Set();
                    queue.ReturnQueryObject(returned);
                    returnCompleted.Set();
                });
                Assert.That(returnStarted.Wait(3000), Is.True);
                changedDuringReset = returnCompleted.Wait(100);
            };

            Assert.DoesNotThrow(() => queue.ResetFutureCounters(default, 1));
            Assert.That(returnTask.Wait(3000), Is.True);
            Assert.That(changedDuringReset, Is.False, "Pool reset and return must use the same lock.");
            Assert.That(fixture.Pool(queue).Count, Is.EqualTo(2));
        }

        private static T Get<T>(object target, string name) => (T)FindField(target, name).GetValue(target);
        private static void Set(object target, string name, object value) => FindField(target, name).SetValue(target, value);
        private static FieldInfo FindField(object target, string name)
        {
            for (Type type = target.GetType(); type != null; type = type.BaseType)
            {
                FieldInfo field = type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (field != null) return field;
            }

            throw new MissingFieldException(target.GetType().FullName, name);
        }

        // Only Vulkan dispatch and device/bootstrap are fake. Query recording, native
        // command-buffer submission, Auto ownership and CounterQueue's thread are real.
        private sealed unsafe class Fixture : IDisposable
        {
            [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate Result CreatePool(Device d, CommandPoolCreateInfo* i, AllocationCallbacks* a, CommandPool* p);
            [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void DestroyPool(Device d, CommandPool p, AllocationCallbacks* a);
            [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate Result AllocateCommands(Device d, CommandBufferAllocateInfo* i, CommandBuffer* c);
            [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate Result BeginCommands(CommandBuffer c, CommandBufferBeginInfo* i);
            [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate Result EndCommands(CommandBuffer c);
            [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate Result Submit(VkQueue q, uint n, SubmitInfo* i, Fence f);
            [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate Result CreateFence(Device d, FenceCreateInfo* i, AllocationCallbacks* a, Fence* f);
            [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void DestroyFence(Device d, Fence f, AllocationCallbacks* a);
            [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate Result WaitFences(Device d, uint n, Fence* f, Bool32 all, ulong timeout);
            [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void EndQuery(CommandBuffer c, QueryPool q, uint first);
            [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void BeginQuery(CommandBuffer c, QueryPool q, uint first, QueryControlFlags flags);
            [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void ResetQuery(CommandBuffer c, QueryPool q, uint first, uint count);
            [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void CopyQuery(CommandBuffer c, QueryPool q, uint first, uint count, VkBuffer b, ulong offset, ulong stride, QueryResultFlags flags);
            [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void DestroyQuery(Device d, QueryPool q, AllocationCallbacks* a);
            [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void DestroyBuffer(Device d, VkBuffer b, AllocationCallbacks* a);
            [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void Barrier(CommandBuffer c, PipelineStageFlags src,
                PipelineStageFlags dst, DependencyFlags flags, uint memoryCount, MemoryBarrier* memory, uint bufferCount,
                BufferMemoryBarrier* buffers, uint imageCount, ImageMemoryBarrier* images);

            private readonly Dictionary<string, Delegate> _native = [];
            private readonly Dictionary<BufferedQuery, nint> _queries = [];
            private readonly List<CounterQueue> _queues = [];
            private readonly HashSet<CounterQueue> _runningQueues = [];
            private readonly HashSet<CounterQueue> _stoppedQueues = [];
            private readonly Vk _api;
            private readonly CommandBufferPool _pool;
            private readonly BarrierBatch _barriers;
            public VulkanRenderer Renderer { get; }
            public PipelineFull Pipeline { get; }
            public List<BufferedQuery> PendingCopies { get; } = [];
            public List<string> Events { get; } = [];
            public Action OnReset { get; set; }

            public Fixture()
            {
                int nextHandle = 1;
                _native["vkCreateCommandPool"] = new CreatePool((_, _, _, p) => { *p = new CommandPool(1); return Result.Success; });
                _native["vkDestroyCommandPool"] = new DestroyPool((_, _, _) => { });
                _native["vkAllocateCommandBuffers"] = new AllocateCommands((_, _, c) => { *c = new CommandBuffer((nint)(++nextHandle)); return Result.Success; });
                _native["vkBeginCommandBuffer"] = new BeginCommands((_, _) => Result.Success);
                _native["vkEndCommandBuffer"] = new EndCommands(_ => Result.Success);
                _native["vkQueueSubmit"] = new Submit((_, _, _, _) => { Events.Add("submit"); return Result.Success; });
                _native["vkCreateFence"] = new CreateFence((_, _, _, f) => { *f = new Fence((ulong)(++nextHandle)); return Result.Success; });
                _native["vkDestroyFence"] = new DestroyFence((_, _, _) => { });
                _native["vkWaitForFences"] = new WaitFences((_, _, _, _, _) => Result.Success);
                _native["vkCmdEndQuery"] = new EndQuery((_, _, _) => Events.Add("end-query"));
                _native["vkCmdBeginQuery"] = new BeginQuery((_, _, _, _) => Events.Add("begin-query"));
                _native["vkCmdResetQueryPool"] = new ResetQuery((_, _, _, _) => { Events.Add("reset-query"); OnReset?.Invoke(); });
                _native["vkCmdCopyQueryPoolResults"] = new CopyQuery((_, _, _, _, _, _, _, _) => Events.Add("copy-query"));
                _native["vkDestroyQueryPool"] = new DestroyQuery((_, _, _) => { });
                _native["vkDestroyBuffer"] = new DestroyBuffer((_, _, _) => { });
                _native["vkCmdPipelineBarrier"] = new Barrier((_, _, _, _, _, _, _, _, _, _) => { });
                _api = new Vk(new LamdaNativeContext(name => _native.TryGetValue(name, out Delegate value)
                    ? Marshal.GetFunctionPointerForDelegate(value) : throw new InvalidOperationException($"Unexpected native operation: {name}")));

                Renderer = (VulkanRenderer)RuntimeHelpers.GetUninitializedObject(typeof(VulkanRenderer));
                Pipeline = (PipelineFull)RuntimeHelpers.GetUninitializedObject(typeof(PipelineFull));
                Set(Renderer, "<Api>k__BackingField", _api);
                Set(Renderer, "_pipeline", Pipeline);
                Set(Renderer, "<SyncManager>k__BackingField", new SyncManager(Renderer, default));
                BufferManager buffers = (BufferManager)RuntimeHelpers.GetUninitializedObject(typeof(BufferManager));
                StagingBuffer staging = (StagingBuffer)RuntimeHelpers.GetUninitializedObject(typeof(StagingBuffer));
                FieldInfo pendingCopies = FindField(staging, "_pendingCopies");
                pendingCopies.SetValue(staging, Activator.CreateInstance(pendingCopies.FieldType));
                Set(buffers, "<StagingBuffer>k__BackingField", staging);
                Set(Renderer, "<BufferManager>k__BackingField", buffers);
                _barriers = new BarrierBatch(Renderer);
                Set(Renderer, "<Barriers>k__BackingField", _barriers);
                Counters counters = (Counters)RuntimeHelpers.GetUninitializedObject(typeof(Counters));
                Set(counters, "_counterQueues", Array.Empty<CounterQueue>());
                Set(Renderer, "_counters", counters);
                _pool = new CommandBufferPool(_api, default, default, new Lock(), 0, false, isLight: true);
                Set(Renderer, "<CommandBufferPool>k__BackingField", _pool);
                CommandBufferScoped commands = _pool.Rent();
                Set(Pipeline, "Gd", Renderer);
                Set(Pipeline, "AutoFlush", new AutoFlushCounter(Renderer));
                Set(Pipeline, "Cbs", commands);
                Set(Pipeline, "CommandBuffer", commands.CommandBuffer);
                Set(Pipeline, "_pendingQueryCopies", PendingCopies);
                Set(Pipeline, "_activeQueries", new List<(QueryPool, bool)>());
                Set(Pipeline, "_activeBufferMirrors", new List<BufferHolder>());
                Set(Pipeline, "_vertexBuffers", new VertexBufferState[1]);
                Set(Pipeline, "_descriptorSetUpdater", RuntimeHelpers.GetUninitializedObject(typeof(DescriptorSetUpdater)));
            }

            public BufferedQuery CreateQuery()
            {
                BufferedQuery query = (BufferedQuery)RuntimeHelpers.GetUninitializedObject(typeof(BufferedQuery));
                nint map = (nint)NativeMemory.Alloc(8);
                Marshal.WriteInt64(map, Sentinel);
                Set(query, "_api", _api);
                Set(query, "_pipeline", Pipeline);
                Set(query, "_queryPool", new QueryPool((ulong)_queries.Count + 100));
                Set(query, "_buffer", new BufferHolder(Renderer, default, new VkBuffer((ulong)_queries.Count + 200), 8, []));
                Set(query, "_bufferMap", map);
                Set(query, "_defaultValue", Sentinel);
                Set(query, "_isSupported", true);
                _queries.Add(query, map);
                return query;
            }

            public void Write(BufferedQuery query, long value) => Marshal.WriteInt64(_queries[query], value);

            public CounterQueue CreateQueue()
            {
                CounterQueue queue = (CounterQueue)RuntimeHelpers.GetUninitializedObject(typeof(CounterQueue));
                Set(queue, "_lock", new Lock());
                Set(queue, "_queryPool", new Queue<BufferedQuery>());
                Set(queue, "_events", new Queue<CounterQueueEvent>());
                Set(queue, "_disposeCancellation", new CancellationTokenSource());
                Set(queue, "_queuedEvent", new AutoResetEvent(false));
                Set(queue, "_wakeSignal", new AutoResetEvent(false));
                Set(queue, "_eventConsumed", new AutoResetEvent(false));
                _queues.Add(queue);
                return queue;
            }

            public Queue<BufferedQuery> Pool(CounterQueue queue) => Get<Queue<BufferedQuery>>(queue, "_queryPool");

            public CounterQueueEvent CreateEvent(CounterQueue queue, BufferedQuery query)
            {
                CounterQueueEvent evt = (CounterQueueEvent)RuntimeHelpers.GetUninitializedObject(typeof(CounterQueueEvent));
                Set(evt, "_queue", queue);
                Set(evt, "_counter", query);
                Set(evt, "_lock", new Lock());
                Set(evt, "_refCount", 1);
                Set(evt, "_divisor", 1d);
                Set(evt, "_result", ulong.MaxValue);
                return evt;
            }

            public void StartConsumer(CounterQueue queue, params CounterQueueEvent[] events)
            {
                Queue<CounterQueueEvent> pending = Get<Queue<CounterQueueEvent>>(queue, "_events");
                foreach (CounterQueueEvent evt in events) pending.Enqueue(evt);
                Set(queue, "_reportsQueued", (long)events.Length);
                MethodInfo consume = typeof(CounterQueue).GetMethod("EventConsumer", BindingFlags.Instance | BindingFlags.NonPublic);
                Thread thread = new(() => consume.Invoke(queue, null)) { IsBackground = true };
                Set(queue, "_consumerThread", thread);
                _runningQueues.Add(queue);
                thread.Start();
            }

            public void StopConsumer(CounterQueue queue)
            {
                queue.Dispose();
                _stoppedQueues.Add(queue);
            }

            public void Dispose()
            {
                foreach (CounterQueue queue in _runningQueues)
                {
                    if (!_stoppedQueues.Contains(queue)) StopConsumer(queue);
                }

                _pool.Dispose();
                foreach ((BufferedQuery query, nint pointer) in _queries)
                {
                    // Running queues own and dispose their pooled native queries.
                    bool alreadyDisposed = false;
                    foreach (CounterQueue queue in _stoppedQueues) alreadyDisposed |= Pool(queue).Contains(query);
                    if (!alreadyDisposed) query.Dispose();
                    NativeMemory.Free((void*)pointer);
                }

                foreach (CounterQueue queue in _queues)
                {
                    if (_stoppedQueues.Contains(queue)) continue;
                    Get<CancellationTokenSource>(queue, "_disposeCancellation").Dispose();
                    Get<AutoResetEvent>(queue, "_queuedEvent").Dispose();
                    Get<AutoResetEvent>(queue, "_wakeSignal").Dispose();
                    Get<AutoResetEvent>(queue, "_eventConsumed").Dispose();
                }

                _barriers.Dispose();
                _api.Dispose();
                GC.KeepAlive(_native);
            }
        }
    }
}
