using NUnit.Framework;
using Ryujinx.Graphics.Vulkan;
using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Ryujinx.Tests.Graphics
{
    [NonParallelizable]
    public class AutoLifetimeTests
    {
        private sealed class Counter
        {
            public int Disposals;
        }

        private sealed class Resource : IDisposable
        {
            private readonly Counter _counter;
            private readonly Exception _failure;

            public Resource(Counter counter, Exception failure = null)
            {
                _counter = counter;
                _failure = failure;
            }

            public void Dispose()
            {
                Interlocked.Increment(ref _counter.Disposals);
                if (_failure != null) throw _failure;
            }
        }

        private sealed class MirrorOwner : IMirrorable<Resource>
        {
            // Represents an upload backing retained by a BufferHolder. No Vulkan device
            // is needed to test the production Auto lifetime that owns this object.
            public readonly byte[] Payload = new byte[1024 * 1024];

            public Auto<Resource> GetMirrorable(CommandBufferScoped cbs, ref int offset, int size, out bool mirrored)
                => throw new InvalidOperationException("A destroyed mirror owner must not be called.");

            public void ClearMirrors(CommandBufferScoped cbs, int offset, int size)
                => throw new InvalidOperationException("A destroyed mirror owner must not be called.");
        }

        private sealed record Fixture(
            Auto<Resource> Owner, Counter ParentCount, Counter ChildCount,
            WeakReference Mirror, WeakReference Payload, WeakReference Waitable, WeakReference Child);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Fixture CreateOwnedGraph()
        {
            Counter parentCount = new(), childCount = new();
            MirrorOwner mirror = new();
            mirror.Payload[17] = 0x7b;
            MultiFenceHolder waitable = new(mirror.Payload.Length);
            Auto<Resource> child = new(new Resource(childCount));
            Auto<Resource> owner = new(new Resource(parentCount), mirror, waitable, child);

            // The parent now owns the child's remaining reference. Hold two extra
            // references exactly as CommandBufferPool.AddDependant does.
            child.Dispose();
            owner.IncrementReferenceCount();
            owner.IncrementReferenceCount();

            return new(owner, parentCount, childCount, new(mirror), new(mirror.Payload), new(waitable), new(child));
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Collect()
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void AssertPayloadStillValid(WeakReference payload)
        {
            Assert.That(((byte[])payload.Target)[17], Is.EqualTo(0x7b));
        }

        [Test]
        public void OwnerDisposalRetainsUploadDataUntilLastCommandBufferReferenceRetires()
        {
            Fixture fixture = CreateOwnedGraph();
            fixture.Owner.Dispose();
            fixture.Owner.DecrementReferenceCount(1);
            Collect();

            Assert.Multiple(() =>
            {
                Assert.That(fixture.ParentCount.Disposals, Is.Zero);
                Assert.That(fixture.ChildCount.Disposals, Is.Zero);
                Assert.That(fixture.Mirror.IsAlive, Is.True);
                Assert.That(fixture.Waitable.IsAlive, Is.True);
                Assert.That(fixture.Child.IsAlive, Is.True);
            });
            AssertPayloadStillValid(fixture.Payload);

            // A binding cache intentionally keeps the Auto wrapper alive after this
            // final retirement. Its dead owner graph must nevertheless be collectible.
            fixture.Owner.DecrementReferenceCount(2);
            Collect();
            Assert.Multiple(() =>
            {
                Assert.That(fixture.ParentCount.Disposals, Is.EqualTo(1));
                Assert.That(fixture.ChildCount.Disposals, Is.EqualTo(1));
                Assert.That(fixture.Mirror.IsAlive, Is.False);
                Assert.That(fixture.Payload.IsAlive, Is.False);
                Assert.That(fixture.Waitable.IsAlive, Is.False);
                Assert.That(fixture.Child.IsAlive, Is.False);
            });
            GC.KeepAlive(fixture.Owner);
        }

        [Test]
        public void StaleBindingReadsReturnEmptyWithoutCallingMirrorOrResurrectingResource()
        {
            Counter count = new();
            Auto<Resource> owner = new(new Resource(count), new MirrorOwner(), null);
            owner.Dispose();

            int offset = 7;
            Assert.That(owner.GetMirrorable(default, ref offset, 4, out bool mirrored), Is.Null);
            Assert.That(mirrored, Is.False);
            Assert.That(offset, Is.EqualTo(7));
            Assert.That(owner.Get(default, 0, 4, true), Is.Null);
            Assert.That(owner.Get(default), Is.Null);
            Assert.That(owner.GetUnsafe(), Is.Null);
            Assert.That(owner.TryIncrementReferenceCount(), Is.False);
            Assert.Throws<InvalidOperationException>(owner.IncrementReferenceCount);
            owner.Dispose();
            Assert.That(count.Disposals, Is.EqualTo(1));
        }

        [Test]
        public void ThrowingNativeDisposalStillReleasesEveryChildAndPreservesFirstError()
        {
            Counter parentCount = new(), firstChildCount = new(), secondChildCount = new();
            InvalidOperationException expected = new("native destructor failed");
            Auto<Resource> first = new(new Resource(firstChildCount, new ArgumentException("child destructor failed")));
            Auto<Resource> second = new(new Resource(secondChildCount));
            Auto<Resource> owner = new(new Resource(parentCount, expected), null, first, second);
            first.Dispose();
            second.Dispose();

            Assert.That(Assert.Throws<InvalidOperationException>(owner.Dispose), Is.SameAs(expected));
            Assert.Multiple(() =>
            {
                Assert.That(parentCount.Disposals, Is.EqualTo(1));
                Assert.That(firstChildCount.Disposals, Is.EqualTo(1));
                Assert.That(secondChildCount.Disposals, Is.EqualTo(1));
                Assert.That(owner.TryIncrementReferenceCount(), Is.False);
                Assert.That(first.TryIncrementReferenceCount(), Is.False);
                Assert.That(second.TryIncrementReferenceCount(), Is.False);
            });
            Assert.DoesNotThrow(owner.Dispose);
        }

        [Test]
        public void ThrowingFirstDependencyDoesNotPreventRemainingDependencyRelease()
        {
            Counter parentCount = new(), firstChildCount = new(), secondChildCount = new();
            InvalidOperationException expected = new("child destructor failed");
            Auto<Resource> first = new(new Resource(firstChildCount, expected));
            Auto<Resource> second = new(new Resource(secondChildCount));
            Auto<Resource> owner = new(new Resource(parentCount), null, first, second);
            first.Dispose();
            second.Dispose();
            Assert.That(Assert.Throws<InvalidOperationException>(owner.Dispose), Is.SameAs(expected));
            Assert.That(parentCount.Disposals, Is.EqualTo(1));
            Assert.That(firstChildCount.Disposals, Is.EqualTo(1));
            Assert.That(secondChildCount.Disposals, Is.EqualTo(1));
            Assert.DoesNotThrow(owner.Dispose);
        }

        [Test]
        public void ConcurrentBorrowedReferenceRetirementDestroysResourcesExactlyOnce()
        {
            Counter parentCount = new(), childCount = new();
            Auto<Resource> child = new(new Resource(childCount));
            Auto<Resource> owner = new(new Resource(parentCount), null, child);
            child.Dispose();
            for (int i = 0; i < 1000; i++) owner.IncrementReferenceCount();
            owner.Dispose();
            Parallel.For(0, 1000, _ => owner.DecrementReferenceCount());
            Assert.That(parentCount.Disposals, Is.EqualTo(1));
            Assert.That(childCount.Disposals, Is.EqualTo(1));
            Assert.That(owner.TryIncrementReferenceCount(), Is.False);
        }
    }
}
