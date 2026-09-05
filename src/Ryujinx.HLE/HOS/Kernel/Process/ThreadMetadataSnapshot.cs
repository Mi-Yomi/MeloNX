using Ryujinx.HLE.HOS.Kernel.Threading;
using System;

namespace Ryujinx.HLE.HOS.Kernel.Process
{
    // Diagnostic observations only. The scheduler may change these scalar fields
    // between reads; this is not a coherent kernel state or a guest stack trace.
    internal readonly record struct ThreadMetadata(
        ulong ThreadUid,
        string HostName,
        ThreadSchedState SchedFlags,
        bool WaitingSync,
        bool WaitingInArbitration,
        ulong? MutexOwnerUid,
        ulong MutexAddress,
        bool TerminationRequested,
        int CurrentCore,
        int DynamicPriority)
    {
        internal const int MaxHostNameLength = 80;

        internal static ThreadMetadata Capture(KThread thread)
        {
            // Do not call GetThreadName: it follows pointers in guest TLS. HostThread
            // already holds the name discovered during ordinary guest scheduling.
            var hostThread = thread.HostThread;
            var mutexOwner = thread.MutexOwner;
            return new(
                thread.ThreadUid,
                SanitizeHostName(hostThread?.Name),
                thread.SchedFlags,
                thread.WaitingSync,
                thread.WaitingInArbitration,
                mutexOwner?.ThreadUid,
                thread.MutexAddress,
                thread.TerminationRequested,
                thread.CurrentCore,
                thread.DynamicPriority);
        }

        private static string SanitizeHostName(string name)
        {
            if (name == null) return "unknown";

            int length = Math.Min(name.Length, MaxHostNameLength);
            Span<char> result = stackalloc char[length];
            for (int i = 0; i < length; i++)
            {
                char character = name[i];
                result[i] = char.IsControl(character) || character is '"' or '\\' or '[' or ']' or ';' or '='
                    ? '_' : character;
            }
            return new string(result);
        }
    }

    internal readonly record struct ThreadMetadataSnapshot(
        bool ThreadListBusy,
        int? TotalThreads,
        ThreadMetadata[] Threads)
    {
        internal int? TruncatedThreads => TotalThreads - Threads.Length;
    }
}
