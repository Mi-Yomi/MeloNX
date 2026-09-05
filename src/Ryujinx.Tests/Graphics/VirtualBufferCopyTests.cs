using NUnit.Framework;
using Ryujinx.Graphics.Gpu;
using Ryujinx.Graphics.Gpu.Memory;
using Ryujinx.Memory.Range;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using GpuBuffer = Ryujinx.Graphics.Gpu.Memory.Buffer;

namespace Ryujinx.Tests.Graphics
{
    public class VirtualBufferCopyTests
    {
        private static void Set(object obj, string field, object value) =>
            obj.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(obj, value);

        [TestCase(0, 4096, 0, 65536)]
        [TestCase(32768, 4096, 0, 65536)]
        [TestCase(61440, 4096, 0, 65536)]
        [TestCase(61440, 4096, 63488, 2048)]
        public void CopiesOnlyTheModifiedIntersection(int modifiedOffset, int modifiedLength, int readOffset, int readLength)
        {
            const ulong address = 0x10000;
            AuditTestRenderer renderer = new();
            GpuContext context = (GpuContext)RuntimeHelpers.GetUninitializedObject(typeof(GpuContext));
            Set(context, "<Renderer>k__BackingField", renderer);
            GpuBuffer physical = (GpuBuffer)RuntimeHelpers.GetUninitializedObject(typeof(GpuBuffer));
            Set(physical, "_context", context);
            Set(physical, "<Address>k__BackingField", address);
            Set(physical, "<Size>k__BackingField", 65536UL);
            Set(physical, "<Handle>k__BackingField", renderer.CreateBuffer(65536));
            using ReaderWriterLockSlim dependencyLock = new();
            Set(physical, "_virtualDependenciesLock", dependencyLock);
            MultiRangeBuffer virtualBuffer = new(context, new MultiRange(address, 65536UL));
            Set(physical, "_virtualDependencies", new HashSet<MultiRangeBuffer> { virtualBuffer });
            Array.Fill(renderer.Buffers[physical.Handle], (byte)0x22);
            Array.Fill(renderer.Buffers[virtualBuffer.Handle], (byte)0x7b);
            virtualBuffer.AddModifiedRegion(new MultiRange(address + (ulong)modifiedOffset, (ulong)modifiedLength), 1);
            byte[] baseline = renderer.Buffers[physical.Handle].AsSpan(readOffset, readLength).ToArray();
            byte[] result = physical.CopyFromDependantVirtualBuffers(baseline, address + (ulong)readOffset, (ulong)readLength).ToArray();
            int start = Math.Max(modifiedOffset, readOffset);
            int length = Math.Min(modifiedOffset + modifiedLength, readOffset + readLength) - start;
            Assert.That(renderer.Copies.ToArray(), Is.EqualTo(new[] { (start, start, length) }));
            Assert.That(result.Take(start - readOffset), Is.All.EqualTo((byte)0x22));
            Assert.That(result.Skip(start - readOffset).Take(length), Is.All.EqualTo((byte)0x7b));
            Assert.That(result.Skip(start - readOffset + length), Is.All.EqualTo((byte)0x22));
            Assert.That(renderer.Buffers[physical.Handle].Take(start), Is.All.EqualTo((byte)0x22));
            Assert.That(renderer.Buffers[physical.Handle].Skip(start + length), Is.All.EqualTo((byte)0x22));
        }
    }
}
