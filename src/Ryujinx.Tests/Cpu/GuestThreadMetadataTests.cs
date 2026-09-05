using NUnit.Framework;
using Ryujinx.HLE.HOS.Kernel.Process;
using Ryujinx.HLE.HOS.Kernel.Threading;
using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace Ryujinx.Tests.Cpu
{
    public class GuestThreadMetadataTests
    {
        private static void SetProperty(object target, string name, object value)
        {
            target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .SetValue(target, value);
        }

        [Test]
        public void BusyProcessThreadListIsSkippedWithoutWaitingForItsOwner()
        {
            KProcess process = new(null);
            Lock threadLock = (Lock)typeof(KProcess).GetField("_threadingLock", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(process);
            using ManualResetEventSlim acquired = new(false);
            using ManualResetEventSlim release = new(false);
            Thread holder = new(() =>
            {
                lock (threadLock)
                {
                    acquired.Set();
                    release.Wait();
                }
            });
            holder.Start();
            Task<ThreadMetadataSnapshot> capture = null;
            try
            {
                Assert.That(acquired.Wait(TimeSpan.FromSeconds(5)), Is.True);
                capture = Task.Run(process.CaptureThreadMetadata);
                // The holder cannot release before this assertion. Blocking Enter,
                // or using Monitor on the Lock object, fails the actual contract.
                Assert.That(capture.Wait(TimeSpan.FromSeconds(5)), Is.True);
                Assert.That(capture.Result.ThreadListBusy, Is.True);
                Assert.That(capture.Result.TotalThreads, Is.Null);
                Assert.That(capture.Result.TruncatedThreads, Is.Null);
                Assert.That(capture.Result.Threads, Is.Empty);
            }
            finally
            {
                release.Set();
                holder.Join();
                capture?.Wait(TimeSpan.FromSeconds(5));
            }

            Assert.That(process.CaptureThreadMetadata().ThreadListBusy, Is.False,
                "A skipped attempt must not change ownership of the real process lock.");
        }

        [Test]
        public void SnapshotIsBoundedAndDoesNotNeedKernelOrGuestMemory()
        {
            // These are actual KProcess/KThread lists, without a KernelContext,
            // native execution context, guest address space or debug services.
            KProcess process = new(null);
            for (ulong id = 1; id <= 80; id++)
            {
                KThread thread = new(null);
                SetProperty(thread, nameof(KThread.ThreadUid), id);
                process.AddThread(thread);
            }

            ThreadMetadataSnapshot snapshot = process.CaptureThreadMetadata();
            Assert.That(snapshot.ThreadListBusy, Is.False);
            Assert.That(snapshot.TotalThreads, Is.EqualTo(80));
            Assert.That(snapshot.Threads.Length, Is.EqualTo(64));
            Assert.That(snapshot.TruncatedThreads, Is.EqualTo(16));
            Assert.That(snapshot.Threads[0].ThreadUid, Is.EqualTo(1));
            Assert.That(snapshot.Threads[^1].ThreadUid, Is.EqualTo(64));
            Assert.That(snapshot.Threads[0].HostName, Is.EqualTo("unknown"));
        }

        [Test]
        public void SnapshotCapturesManagedWaitMetadataAndSanitizesExistingHostName()
        {
            KProcess process = new(null);
            KThread mutexOwner = new(null);
            SetProperty(mutexOwner, nameof(KThread.ThreadUid), 42UL);
            KThread waiting = new(null)
            {
                WaitingSync = true,
                WaitingInArbitration = true,
                MutexAddress = 0x1234000,
                CurrentCore = 2,
            };
            SetProperty(waiting, nameof(KThread.ThreadUid), 7UL);
            SetProperty(waiting, nameof(KThread.MutexOwner), mutexOwner);
            SetProperty(waiting, nameof(KThread.SchedFlags), ThreadSchedState.Paused | ThreadSchedState.ThreadPauseFlag);
            SetProperty(waiting, nameof(KThread.HostThread), new Thread(() => { })
            {
                Name = "<MainThread>\r\n;=[\\\"" + new string('x', 120),
            });
            process.AddThread(waiting);

            ThreadMetadata thread = process.CaptureThreadMetadata().Threads[0];
            Assert.That(thread.ThreadUid, Is.EqualTo(7));
            Assert.That(thread.WaitingSync, Is.True);
            Assert.That(thread.WaitingInArbitration, Is.True);
            Assert.That(thread.MutexOwnerUid, Is.EqualTo(42));
            Assert.That(thread.MutexAddress, Is.EqualTo(0x1234000));
            Assert.That(thread.SchedFlags, Is.EqualTo(ThreadSchedState.Paused | ThreadSchedState.ThreadPauseFlag));
            Assert.That(thread.CurrentCore, Is.EqualTo(2));
            Assert.That(thread.HostName, Does.StartWith("<MainThread>_______"));
            Assert.That(thread.HostName.Length, Is.EqualTo(80));
            Assert.That(thread.HostName, Does.Not.Contain("\n").And.Not.Contain("\r").And.Not.Contain("\""));
        }
    }
}
