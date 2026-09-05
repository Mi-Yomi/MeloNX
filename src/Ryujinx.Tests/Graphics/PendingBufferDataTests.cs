using NUnit.Framework;
using Ryujinx.Common.Memory;
using Ryujinx.Graphics.Vulkan;
using Silk.NET.Core.Contexts;
using Silk.NET.Vulkan;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace Ryujinx.Tests.Graphics
{
    [NonParallelizable]
    public class PendingBufferDataTests
    {
        private delegate void UploadCallback(int offset, ReadOnlySpan<byte> data);

        private readonly struct Sink(UploadCallback callback) : IPendingBufferUpload
        {
            public void Upload(int offset, ReadOnlySpan<byte> data) => callback(offset, data);
        }

        [Test]
        public void TinyUpdateNearTwoGiBLimitRentsOnlyTouchedPages()
        {
            List<int> rentals = [];
            using PendingBufferData pending = new(int.MaxValue, size =>
            {
                rentals.Add(size);
                return MemoryOwner<byte>.Rent(size, MemoryOwnerPurpose.Mirror);
            });
            int offset = int.MaxValue - PendingBufferData.PageSize - 2;
            byte[] update = [1, 2, 3, 4, 5, 6];
            pending.Write(offset, update);
            Assert.That(pending.PageCount, Is.EqualTo(2));
            Assert.That(pending.LogicalPageBytes, Is.EqualTo(8192));
            Assert.That(rentals, Is.EqualTo(new[] { 4096, 4096 }));
            Assert.That(Read(pending, offset - 2, 10), Is.EqualTo(new byte[] { 0xcc, 0xcc, 1, 2, 3, 4, 5, 6, 0xcc, 0xcc }));
        }

        [Test]
        public void OverwriteAndPartialRemovalKeepOtherBytesAndReturnCleanPages()
        {
            using PendingBufferData pending = new(3 * PendingBufferData.PageSize);
            byte[] expected = Enumerable.Repeat((byte)0xcc, 3 * PendingBufferData.PageSize).ToArray();
            byte[] original = Enumerable.Repeat((byte)7, 5000).ToArray();
            pending.Write(3000, original);
            original.CopyTo(expected, 3000);
            byte[] replacement = [11, 12, 13, 14, 15, 16];
            pending.Write(4094, replacement);
            replacement.CopyTo(expected, 4094);
            Assert.That(pending.Remove(4000, 97), Is.True);
            Array.Fill(expected, (byte)0xcc, 4000, 97);
            Assert.That(Read(pending, 0, expected.Length), Is.EqualTo(expected));
            Assert.That(pending.PageCount, Is.EqualTo(2));
            pending.Remove(0, PendingBufferData.PageSize);
            Assert.That(pending.PageCount, Is.EqualTo(1));
            pending.Remove(PendingBufferData.PageSize, 2 * PendingBufferData.PageSize);
            Assert.That(pending.HasData, Is.False);
            Assert.That(pending.PageCount, Is.Zero);
        }

        [Test]
        public void DenseUploadUsesAtMostSixtyFourKiBPerSubmission()
        {
            const int offset = 37;
            byte[] data = Pattern(3 * PendingBufferData.MaxUploadBatchSize + 123);
            using PendingBufferData pending = new(offset + data.Length);
            pending.Write(offset, data);
            List<int> lengths = [];
            byte[] received = new byte[data.Length];
            Sink sink = new((start, bytes) =>
            {
                lengths.Add(bytes.Length);
                bytes.CopyTo(received.AsSpan(start - offset));
            });
            pending.Upload(offset, data.Length, ref sink);
            Assert.That(lengths, Is.EqualTo(new[] { 65536, 65536, 65536, 123 }));
            Assert.That(received, Is.EqualTo(data));
            Assert.That(pending.HasData, Is.False);
            Assert.That(pending.PageCount, Is.Zero);
        }

        [Test]
        public void FailedPageRentalLeavesPreviousUpdateUnchangedAndReturnsNewPages()
        {
            int calls = 0;
            List<MemoryOwner<byte>> owners = [];
            using PendingBufferData pending = new(3 * PendingBufferData.PageSize, size =>
            {
                if (++calls == 3) throw new OutOfMemoryException("injected page rental failure");
                MemoryOwner<byte> owner = MemoryOwner<byte>.Rent(size, MemoryOwnerPurpose.Mirror);
                owners.Add(owner);
                return owner;
            });
            pending.Write(0, new byte[] { 41, 42 });
            Assert.Throws<OutOfMemoryException>(() => pending.Write(0, new byte[3 * PendingBufferData.PageSize]));
            Assert.That(pending.PageCount, Is.EqualTo(1));
            Assert.That(Read(pending, 0, 4), Is.EqualTo(new byte[] { 41, 42, 0xcc, 0xcc }));
            Assert.Throws<ObjectDisposedException>(() => _ = owners[1].Memory);
            Assert.That(owners[0].Memory.Length, Is.EqualTo(4096));
        }

        [Test]
        public void InvalidAndFailedBatchRentalsLeaveDirtyDataRetryable()
        {
            int calls = 0;
            MemoryOwner<byte> invalid = null;
            using PendingBufferData pending = new(4096, size =>
            {
                calls++;
                if (calls == 2) return invalid = MemoryOwner<byte>.Rent(size - 1);
                if (calls == 3) throw new OutOfMemoryException("injected batch rental failure");
                return MemoryOwner<byte>.Rent(size, MemoryOwnerPurpose.Mirror);
            });
            pending.Write(100, new byte[] { 1, 2, 3 });
            int uploads = 0;
            Sink sink = new((_, bytes) =>
            {
                uploads++;
                Assert.That(bytes.ToArray(), Is.EqualTo(new byte[] { 1, 2, 3 }));
            });
            Assert.Throws<InvalidOperationException>(() => pending.Upload(0, 4096, ref sink));
            Assert.Throws<ObjectDisposedException>(() => _ = invalid.Memory);
            Assert.Throws<OutOfMemoryException>(() => pending.Upload(0, 4096, ref sink));
            Assert.That(Read(pending, 100, 3), Is.EqualTo(new byte[] { 1, 2, 3 }));
            Assert.That(uploads, Is.Zero);
            pending.Upload(0, 4096, ref sink);
            Assert.That(uploads, Is.EqualTo(1));
            Assert.That(pending.PageCount, Is.Zero);
        }

        [Test]
        public void FailedSubmissionRestoresOnlyFailedAndUnsubmittedBatches()
        {
            int batch = PendingBufferData.MaxUploadBatchSize;
            byte[] data = Pattern(2 * batch + 17);
            using PendingBufferData pending = new(data.Length);
            pending.Write(0, data);
            int calls = 0;
            Sink fail = new((_, _) =>
            {
                if (++calls == 2) throw new InvalidOperationException("injected upload failure");
            });
            Assert.Throws<InvalidOperationException>(() => pending.Upload(0, data.Length, ref fail));
            Assert.That(pending.Overlaps(0, batch), Is.False);
            Assert.That(pending.Overlaps(batch, data.Length - batch), Is.True);
            Assert.That(Read(pending, batch, data.Length - batch), Is.EqualTo(data[batch..]));
            List<int> retryOffsets = [];
            Sink retry = new((start, bytes) =>
            {
                retryOffsets.Add(start);
                Assert.That(bytes.ToArray(), Is.EqualTo(data.AsSpan(start, bytes.Length).ToArray()));
            });
            pending.Upload(0, data.Length, ref retry);
            Assert.That(retryOffsets, Is.EqualTo(new[] { batch, 2 * batch }));
            Assert.That(pending.PageCount, Is.Zero);
        }

        [Test]
        public void NestedRemovalDoesNotReplayFutureRangeOrReturnActivePages()
        {
            List<MemoryOwner<byte>> owners = [];
            using PendingBufferData pending = new(8192, size =>
            {
                MemoryOwner<byte> owner = MemoryOwner<byte>.Rent(size, MemoryOwnerPurpose.Mirror);
                owners.Add(owner);
                return owner;
            });
            pending.Write(0, new byte[] { 1, 2, 3 });
            pending.Write(4096, new byte[] { 4, 5, 6 });
            int calls = 0;
            Sink sink = new((start, bytes) =>
            {
                calls++;
                Assert.That(start, Is.Zero);
                pending.Remove(4096, 3);
                Assert.That(owners.All(owner => owner.Memory.Length > 0), Is.True);
                using MemoryOwner<byte> competing = MemoryOwner<byte>.Rent(8192);
                competing.Span.Fill(0xff);
                Assert.That(bytes.ToArray(), Is.EqualTo(new byte[] { 1, 2, 3 }));
            });
            pending.Upload(0, 8192, ref sink);
            Assert.That(calls, Is.EqualTo(1));
            Assert.That(pending.PageCount, Is.Zero);
            foreach (MemoryOwner<byte> owner in owners)
                Assert.Throws<ObjectDisposedException>(() => _ = owner.Memory);
        }

        [Test]
        public void NestedUploadConsumesFutureRangeExactlyOnce()
        {
            using PendingBufferData pending = new(8192);
            pending.Write(0, new byte[] { 1, 2, 3 });
            pending.Write(4096, new byte[] { 4, 5, 6 });
            List<int> offsets = [];
            Sink inner = new((start, bytes) =>
            {
                offsets.Add(start);
                Assert.That(bytes.ToArray(), Is.EqualTo(new byte[] { 4, 5, 6 }));
            });
            Sink outer = new((start, bytes) =>
            {
                offsets.Add(start);
                pending.Upload(0, 8192, ref inner);
                Assert.That(bytes.ToArray(), Is.EqualTo(new byte[] { 1, 2, 3 }));
            });
            pending.Upload(0, 8192, ref outer);
            Assert.That(offsets, Is.EqualTo(new[] { 0, 4096 }));
            Assert.That(pending.PageCount, Is.Zero);
        }

        [Test]
        public void NestedWritePreservesCallbackSnapshotAndNewDirtyBytes()
        {
            using PendingBufferData pending = new(4096);
            pending.Write(100, new byte[] { 1, 2, 3 });
            Sink sink = new((_, bytes) =>
            {
                pending.Write(100, new byte[] { 7, 8, 9 });
                Assert.That(bytes.ToArray(), Is.EqualTo(new byte[] { 1, 2, 3 }));
                Assert.Throws<InvalidOperationException>(() => pending.Dispose());
            });
            pending.Upload(0, 4096, ref sink);
            Assert.That(pending.HasData, Is.True);
            Assert.That(Read(pending, 100, 3), Is.EqualTo(new byte[] { 7, 8, 9 }));
            Sink retry = new((_, bytes) => Assert.That(bytes.ToArray(), Is.EqualTo(new byte[] { 7, 8, 9 })));
            pending.Upload(0, 4096, ref retry);
            Assert.That(pending.PageCount, Is.Zero);
        }

        [Test]
        public void EmptyOperationsDoNotSplitDirtyRangesOrUpload()
        {
            using PendingBufferData pending = new(4096);
            pending.Write(100, new byte[] { 1, 2, 3 });
            pending.Write(101, ReadOnlySpan<byte>.Empty);
            Assert.That(pending.Remove(101, 0), Is.False);
            Assert.That(pending.Overlaps(101, 0), Is.False);
            Sink sink = new((_, _) => Assert.Fail("Empty upload must not call backend."));
            pending.Upload(101, 0, ref sink);
            Assert.Throws<ArgumentOutOfRangeException>(() => pending.Remove(int.MaxValue, 2));
            Assert.That(Read(pending, 100, 3), Is.EqualTo(new byte[] { 1, 2, 3 }));
        }

        [Test]
        public void RepeatedMirrorReadsDoNotAllocateRangeSnapshots()
        {
            using PendingBufferData pending = new(8192);
            pending.Write(10, new byte[] { 1, 2, 3 });
            pending.Write(5000, new byte[] { 4, 5, 6 });
            byte[] source = new byte[8192];
            byte[] destination = new byte[8192];
            for (int i = 0; i < 100; i++) pending.FillData(source, 0, destination);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 20000; i++) pending.FillData(source, 0, destination);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.That(allocated, Is.Zero);
            Assert.That(destination[10], Is.EqualTo(1));
            Assert.That(destination[5002], Is.EqualTo(6));
        }

        [Test]
        public unsafe void NativeDestructionReturnsPendingPagesOnlyAfterFinalSubmissionReference()
        {
            MemoryOwner<byte> page = null;
            using PendingBufferData pending = new(256, size => page = MemoryOwner<byte>.Rent(size, MemoryOwnerPurpose.Mirror));
            pending.Write(8, new byte[] { 1, 2, 3 });
            int destroyed = 0;
            bool pageAliveDuringDestruction = false;
            DestroyBufferDelegate destroy = (_, _, _) =>
            {
                destroyed++;
                // Never let an assertion/exception cross the unmanaged callback.
                try { pageAliveDuringDestruction = page.Memory.Length != 0; }
                catch (ObjectDisposedException) { pageAliveDuringDestruction = false; }
            };
            using Vk api = new(new LamdaNativeContext(name => name switch
            {
                "vkDestroyBuffer" => Marshal.GetFunctionPointerForDelegate(destroy),
                _ => throw new InvalidOperationException($"Unexpected native operation: {name}"),
            }));
            VulkanRenderer renderer = (VulkanRenderer)RuntimeHelpers.GetUninitializedObject(typeof(VulkanRenderer));
            typeof(VulkanRenderer).GetField("<Api>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(renderer, api);
            BufferHolder holder = new(renderer, default, new VkBuffer(42), 256, []);
            typeof(BufferHolder).GetField("_pendingData", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(holder, pending);
            Auto<DisposableBuffer> native = holder.GetBuffer();
            native.IncrementReferenceCount();
            native.IncrementReferenceCount();
            holder.Dispose();
            native.DecrementReferenceCount(0);
            Assert.That(destroyed, Is.Zero);
            Assert.That(Read(pending, 8, 3), Is.EqualTo(new byte[] { 1, 2, 3 }));
            Assert.That(page.Memory.Length, Is.EqualTo(4096));
            native.DecrementReferenceCount(1);
            Assert.That(destroyed, Is.EqualTo(1));
            Assert.That(pageAliveDuringDestruction, Is.True);
            Assert.That(pending.PageCount, Is.Zero);
            Assert.Throws<ObjectDisposedException>(() => _ = page.Memory);
            Assert.Throws<ObjectDisposedException>(() => pending.Write(0, new byte[] { 1 }));
            GC.KeepAlive(native);
            GC.KeepAlive(holder);
            GC.KeepAlive(destroy);
        }

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private unsafe delegate void DestroyBufferDelegate(Device device, VkBuffer buffer, AllocationCallbacks* allocator);

        private static byte[] Read(PendingBufferData pending, int offset, int length)
        {
            byte[] source = Enumerable.Repeat((byte)0xcc, length).ToArray();
            byte[] destination = new byte[length];
            pending.FillData(source, offset, destination);
            return destination;
        }

        private static byte[] Pattern(int length)
        {
            byte[] data = new byte[length];
            for (int i = 0; i < data.Length; i++) data[i] = (byte)(i % 251);
            return data;
        }
    }
}
