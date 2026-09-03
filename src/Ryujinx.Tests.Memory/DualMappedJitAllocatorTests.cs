using NUnit.Framework;
using Ryujinx.Memory;
using System;
using System.Collections.Generic;

// These tests inject managed operations and never invoke the Darwin mapping APIs.
#pragma warning disable CA1416

namespace Ryujinx.Tests.Memory
{
    public class DualMappedJitAllocatorTests
    {
        private const ulong MappingSize = 16384;

        private sealed class MappingOperations
        {
            public nint? RxPointer = (nint)0x10000;
            public nint RwPointer = (nint)0x20000;
            public Exception RemapError;
            public Exception ProtectError;
            public Exception UnmapError;
            public int RemapCalls;
            public int ProtectCalls;
            public readonly List<(nint Pointer, ulong Size)> Unmapped = [];

            public DualMappedJitAllocator Create()
            {
                return new DualMappedJitAllocator(
                    MappingSize,
                    _ => RxPointer,
                    (_, _) =>
                    {
                        RemapCalls++;
                        if (RemapError != null)
                        {
                            throw RemapError;
                        }

                        return RwPointer;
                    },
                    (_, _) =>
                    {
                        ProtectCalls++;
                        if (ProtectError != null)
                        {
                            throw ProtectError;
                        }
                    },
                    (pointer, size) =>
                    {
                        Unmapped.Add((pointer, size));
                        if (pointer == RwPointer && UnmapError != null)
                        {
                            throw UnmapError;
                        }
                    });
            }
        }

        [TestCase(null)]
        [TestCase(-1)]
        public void FailedInitialMappingDoesNotReleaseAnInvalidAddress(int? result)
        {
            MappingOperations operations = new() { RxPointer = result.HasValue ? (nint)result.Value : null };

            Assert.Throws<Exception>(() => operations.Create());

            Assert.AreEqual(0, operations.RemapCalls);
            Assert.AreEqual(0, operations.ProtectCalls);
            Assert.IsEmpty(operations.Unmapped);
        }

        [Test]
        public void RemapFailureReleasesTheOriginalMapping()
        {
            MappingOperations operations = new() { RemapError = new InvalidOperationException("remap failed") };

            Assert.AreSame(operations.RemapError, Assert.Throws<InvalidOperationException>(() => operations.Create()));

            Assert.AreEqual(0, operations.ProtectCalls);
            CollectionAssert.AreEqual(new[] { (operations.RxPointer.Value, MappingSize) }, operations.Unmapped);
        }

        [Test]
        public void ProtectionFailureReleasesBothMappings()
        {
            MappingOperations operations = new() { ProtectError = new InvalidOperationException("protect failed") };

            Assert.AreSame(operations.ProtectError, Assert.Throws<InvalidOperationException>(() => operations.Create()));

            CollectionAssert.AreEquivalent(
                new[] { (operations.RwPointer, MappingSize), (operations.RxPointer.Value, MappingSize) },
                operations.Unmapped);
        }

        [TestCase(0x10000, 0x20000)]
        [TestCase(0, 0x20000)]
        [TestCase(0x10000, 0)]
        public void SuccessfulMappingsAreReleasedExactlyOnce(int rxPointer, int rwPointer)
        {
            MappingOperations operations = new() { RxPointer = (nint)rxPointer, RwPointer = (nint)rwPointer };
            DualMappedJitAllocator allocator = operations.Create();

            Assert.AreEqual((nint)rxPointer, allocator.RxPtr);
            Assert.AreEqual((nint)rwPointer, allocator.RwPtr);
            Assert.IsEmpty(operations.Unmapped);

            allocator.Dispose();
            allocator.Dispose();

            Assert.AreEqual(nint.Zero, allocator.RxPtr);
            Assert.AreEqual(nint.Zero, allocator.RwPtr);
            CollectionAssert.AreEquivalent(
                new[] { ((nint)rwPointer, MappingSize), ((nint)rxPointer, MappingSize) },
                operations.Unmapped);
        }

        [Test]
        public void DisposalAttemptsBothMappingsEvenIfTheFirstOperationThrows()
        {
            MappingOperations operations = new() { UnmapError = new InvalidOperationException("unmap failed") };
            DualMappedJitAllocator allocator = operations.Create();

            Assert.AreSame(operations.UnmapError, Assert.Throws<InvalidOperationException>(() => allocator.Dispose()));
            allocator.Dispose();

            Assert.AreEqual(nint.Zero, allocator.RxPtr);
            Assert.AreEqual(nint.Zero, allocator.RwPtr);
            CollectionAssert.AreEquivalent(
                new[] { (operations.RwPointer, MappingSize), (operations.RxPointer.Value, MappingSize) },
                operations.Unmapped);
        }
    }
}
