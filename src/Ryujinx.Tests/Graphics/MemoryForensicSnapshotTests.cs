using NUnit.Framework;
using Ryujinx.Common.Diagnostics;
using Ryujinx.Graphics.GAL.Multithreading;
using Ryujinx.Graphics.Gpu;
using System;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Ryujinx.Tests.Graphics
{
    public class MemoryForensicSnapshotTests
    {
        private static void SmallPacket(Utf8JsonWriter writer)
        {
            writer.WriteStartObject();
            writer.WriteString("text", "Память");
            writer.WriteEndObject();
        }

        [Test]
        public void CapacityFailureDoesNotTouchCallerMemoryAndNextPacketRecovers()
        {
            var builder = new BoundedDiagnosticJson();
            byte[] output = [0xaa, 0xaa];
            Assert.That(builder.TryCopy(output, SmallPacket), Is.EqualTo(BoundedDiagnosticJson.TooSmall));
            Assert.That(output, Is.All.EqualTo(0xaa));
            output = new byte[1024];
            int length = builder.TryCopy(output, SmallPacket);
            Assert.That(length, Is.GreaterThan(0));
            using var json = JsonDocument.Parse(output.AsMemory(0, length));
            Assert.That(json.RootElement.GetProperty("text").GetString(), Is.EqualTo("Память"));
        }

        [Test]
        public void OversizedWriterIsCappedAndDoesNotPoisonNextSample()
        {
            var builder = new BoundedDiagnosticJson();
            byte[] output = new byte[65536];
            Array.Fill(output, (byte)0xaa);
            int size = builder.TryCopy(output, writer =>
            {
                writer.WriteStartArray();
                for (int i = 0; i < 100000; i++) writer.WriteNumberValue(i);
                writer.WriteEndArray();
            });
            Assert.That(size, Is.EqualTo(BoundedDiagnosticJson.TooSmall));
            Assert.That(output, Is.All.EqualTo(0xaa));
            Assert.That(builder.TryCopy(output, SmallPacket), Is.GreaterThan(0));
        }

        [Test]
        public void WriterExceptionCannotCrossBoundaryOrExposePartialJson()
        {
            var builder = new BoundedDiagnosticJson();
            byte[] output = [0xaa];
            Assert.That(builder.TryCopy(output, writer =>
            {
                writer.WriteStartObject();
                throw new InvalidOperationException();
            }), Is.EqualTo(BoundedDiagnosticJson.Failed));
            Assert.That(output[0], Is.EqualTo(0xaa));
        }

        [Test]
        public void IncompleteWriterCannotExposeUnterminatedJson()
        {
            var builder = new BoundedDiagnosticJson();
            byte[] output = [0xaa];
            Assert.That(builder.TryCopy(output, writer => writer.WriteStartObject()), Is.EqualTo(BoundedDiagnosticJson.Failed));
            Assert.That(output[0], Is.EqualTo(0xaa));
        }

        [Test]
        public void RecursiveCaptureIsRejectedWithoutCorruptingOuterWriter()
        {
            var builder = new BoundedDiagnosticJson();
            byte[] output = new byte[1024];
            int nested = 0;
            int size = builder.TryCopy(output, writer =>
            {
                nested = builder.TryCopy(new byte[1024], SmallPacket);
                SmallPacket(writer);
            });
            Assert.That(nested, Is.EqualTo(BoundedDiagnosticJson.Busy));
            Assert.That(size, Is.GreaterThan(0));
            using var json = JsonDocument.Parse(output.AsMemory(0, size));
            Assert.That(json.RootElement.GetProperty("text").GetString(), Is.EqualTo("Память"));
        }

        [Test]
        public void CompetingCaptureReturnsBusyWhileFirstWriterIsPaused()
        {
            var builder = new BoundedDiagnosticJson();
            using ManualResetEventSlim entered = new();
            using ManualResetEventSlim release = new();
            Task first = Task.Run(() => builder.TryCopy(new byte[1024], writer =>
            {
                entered.Set();
                if (!release.Wait(5000)) throw new TimeoutException();
                SmallPacket(writer);
            }));
            try
            {
                Assert.That(entered.Wait(5000), Is.True);
                Task<int> second = Task.Run(() => builder.TryCopy(new byte[1024], SmallPacket));
                Assert.That(second.Wait(1000), Is.True, "Sampler blocked on diagnostic writer");
                Assert.That(second.Result, Is.EqualTo(BoundedDiagnosticJson.Busy));
            }
            finally { release.Set(); first.Wait(5000); }
        }

        private static JsonDocument ReadCache(ForensicSnapshotCache cache, long now)
        {
            byte[] bytes = new byte[65536];
            int count = new BoundedDiagnosticJson().TryCopy(bytes, writer => cache.Write(writer, now));
            Assert.That(count, Is.GreaterThan(0));
            return JsonDocument.Parse(bytes.AsMemory(0, count));
        }

        [Test]
        public void FailedPublisherKeepsOldTimestampAndIncrementsFailureCount()
        {
            var cache = new ForensicSnapshotCache();
            cache.Publish(1000, SmallPacket);
            cache.Publish(2000, _ => throw new InvalidOperationException());
            using var packet = ReadCache(cache, 9000);
            Assert.That(packet.RootElement.GetProperty("captured_at_monotonic_ms").GetInt64(), Is.EqualTo(1000));
            Assert.That(packet.RootElement.GetProperty("age_ms").GetInt64(), Is.EqualTo(8000));
            Assert.That(packet.RootElement.GetProperty("publish_failures").GetInt64(), Is.EqualTo(1));
        }

        [Test]
        public void PausedProducerDoesNotBlockCachedReadOrMakeItLookFresh()
        {
            var cache = new ForensicSnapshotCache();
            cache.Publish(1000, SmallPacket);
            using ManualResetEventSlim entered = new();
            using ManualResetEventSlim release = new();
            Task producer = Task.Run(() => cache.Publish(2000, writer =>
            {
                entered.Set();
                if (!release.Wait(5000)) throw new TimeoutException();
                SmallPacket(writer);
            }));
            try
            {
                Assert.That(entered.Wait(5000), Is.True);
                using var packet = ReadCache(cache, 12000);
                Assert.That(packet.RootElement.GetProperty("age_ms").GetInt64(), Is.EqualTo(11000));
            }
            finally { release.Set(); producer.Wait(5000); }
        }

        [Test]
        public void TwoLargeUtf8OwnerPacketsFitCombinedAbiCapacity()
        {
            var first = new ForensicSnapshotCache();
            var second = new ForensicSnapshotCache();
            byte[] raw = Encoding.UTF8.GetBytes("{\"value\":\"" + new string('a', 21000) + "\"}");
            first.Publish(1000, writer => writer.WriteRawValue(raw, true));
            second.Publish(1000, writer => writer.WriteRawValue(raw, true));
            var builder = new BoundedDiagnosticJson();
            byte[] output = new byte[65536];
            int count = builder.TryCopy(output, writer =>
            {
                writer.WriteStartArray();
                first.Write(writer, 1000);
                second.Write(writer, 1000);
                writer.WriteEndArray();
            });
            Assert.That(count, Is.GreaterThan(42000));
            using var json = JsonDocument.Parse(output.AsMemory(0, count));
            Assert.That(json.RootElement[1].GetProperty("data").GetProperty("value").GetString().Length, Is.EqualTo(21000));
        }

        [Test]
        public void CoreSnapshotDistinguishesUnobservedOwnersAndSurvivesOwnerTeardown()
        {
            var context = new GpuContext(new AuditTestRenderer(), new(Array.Empty<ulong>()));
            byte[] output = new byte[65536];
            try
            {
                int count = context.CopyMemoryForensicSnapshot(output);
                Assert.That(count, Is.GreaterThan(0));
                using var initial = JsonDocument.Parse(output.AsMemory(0, count));
                Assert.That(initial.RootElement.GetProperty("schema_version").GetInt32(), Is.EqualTo(1));
                Assert.That(initial.RootElement.GetProperty("producer").GetProperty("observed").GetBoolean(), Is.False);
                Assert.That(initial.RootElement.GetProperty("managed").GetProperty("allocated_bytes_total").GetInt64(), Is.GreaterThan(0));
                typeof(GpuContext).GetMethod("PublishMemoryForensics", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(context, null);
            }
            finally { context.Dispose(); }
            // A racing ABI caller can hold the old context after it is unpublished.
            int finalCount = context.CopyMemoryForensicSnapshot(output);
            Assert.That(finalCount, Is.GreaterThan(0));
            using var final = JsonDocument.Parse(output.AsMemory(0, finalCount));
            Assert.That(final.RootElement.GetProperty("producer").GetProperty("observed").GetBoolean(), Is.True);
        }

        [Test]
        public void RendererForensicsDoesNotAcquireBufferMapLock()
        {
            using var renderer = new ThreadedRenderer(new AuditTestRenderer());
            object mapGate = typeof(BufferMap).GetField("_bufferMap", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(renderer.Buffers);
            Task<int> capture;
            lock (mapGate)
            {
                capture = Task.Run(() => new BoundedDiagnosticJson().TryCopy(new byte[65536],
                    writer => renderer.WriteMemoryForensicState(writer, Environment.TickCount64)));
                Assert.That(capture.Wait(1000), Is.True, "Snapshot waited for BufferMap owner");
            }
            Assert.That(capture.Result, Is.GreaterThan(0));
        }
    }
}
