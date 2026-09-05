using Ryujinx.Cpu.Signal;
using Ryujinx.Memory;
using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;

namespace Ryujinx.Cpu.Jit.HostTracked
{
    sealed class NativePageTable : IDisposable
    {
        private delegate ulong TrackingEventDelegate(ulong address, ulong size, bool write, ulong faultPc);

        private const int PageBits = 12;
        private const int PageSize = 1 << PageBits;
        private const int PageMask = PageSize - 1;

        private const int PteSize = 8;

        private readonly int _bitsPerPtPage;
        private readonly int _entriesPerPtPage;
        private readonly int _pageCommitmentBits;

        private readonly PageTable<ulong> _pageTable;
        private readonly MemoryBlock _nativePageTable;
        private readonly ulong[] _pageCommitmentBitmap;
        private readonly ulong _hostPageSize;
        private readonly ulong _addressSpaceSize;

        private readonly TrackingEventDelegate _trackingEvent;

        private bool _disposed;
        private long _committedBytes;
        private long _lazyReadFaults;
        private long _lazyWriteFaults;
        private long _guardFaults;
        // These are observations, not a mapping lookup. Never acquire a guest mapping
        // lock from the diagnostics reader, nor retain exception/context objects here.
        private long _lastFaultOffset = -1;
        private int _lastFaultWrite;
        private long _lastFaultPc;
        public ulong ReservedBytes => _nativePageTable.Size;
        public long CommittedBytes => Interlocked.Read(ref _committedBytes);
        public int ManagedLeafCount => _pageTable.AllocatedLeafCount;

        public (long LazyReads, long LazyWrites, long GuardFaults, long LastOffset, bool LastWrite, ulong LastFaultPc) GetFaultStatistics() =>
            (Interlocked.Read(ref _lazyReadFaults), Interlocked.Read(ref _lazyWriteFaults),
             Interlocked.Read(ref _guardFaults), Interlocked.Read(ref _lastFaultOffset), Volatile.Read(ref _lastFaultWrite) != 0,
             (ulong)Interlocked.Read(ref _lastFaultPc));

        public nint PageTablePointer => _nativePageTable.Pointer;

        public NativePageTable(ulong asSize)
        {
            ulong hostPageSize = MemoryBlock.GetPageSize();

            _entriesPerPtPage = (int)(hostPageSize / sizeof(ulong));
            _bitsPerPtPage = BitOperations.Log2((uint)_entriesPerPtPage);
            _pageCommitmentBits = PageBits + _bitsPerPtPage;

            _hostPageSize = hostPageSize;
            _addressSpaceSize = asSize;
            _pageTable = new PageTable<ulong>();
            _nativePageTable = new MemoryBlock((asSize / PageSize) * PteSize + _hostPageSize, MemoryAllocationFlags.Reserve);
            _pageCommitmentBitmap = new ulong[(asSize >> _pageCommitmentBits) / (sizeof(ulong) * 8)];

            ulong ptStart = (ulong)_nativePageTable.Pointer;
            ulong ptEnd = ptStart + _nativePageTable.Size;

            _trackingEvent = VirtualMemoryEvent;

            bool added = NativeSignalHandler.AddTrackedRegion((nuint)ptStart, (nuint)ptEnd, Marshal.GetFunctionPointerForDelegate(_trackingEvent), actionWithFaultAddress: true);

            if (!added)
            {
                throw new InvalidOperationException("Number of allowed tracked regions exceeded.");
            }
        }

        public void Map(ulong va, ulong pa, ulong size, AddressSpacePartitioned addressSpace, MemoryBlock backingMemory, bool privateMap)
        {
            ValidateRange(va, size);
            while (size != 0)
            {
                _pageTable.Map(va, pa);

                EnsureCommitment(va);

                if (privateMap)
                {
                    _nativePageTable.Write((va / PageSize) * PteSize, GetPte(va, addressSpace.GetPointer(va, PageSize)));
                }
                else
                {
                    _nativePageTable.Write((va / PageSize) * PteSize, GetPte(va, backingMemory.GetPointer(pa, PageSize)));
                }

                va += PageSize;
                pa += PageSize;
                size -= PageSize;
            }
        }

        public void Unmap(ulong va, ulong size)
        {
            ValidateRange(va, size);
            nint guardPagePtr = GetGuardPagePointer();
            ulong guestBytesPerPtPage = 1UL << _pageCommitmentBits;

            while (size != 0)
            {
                ulong bit = va >> _pageCommitmentBits;
                int index = (int)(bit / 64);
                ulong mask = 1UL << (int)(bit % 64);
                if ((Volatile.Read(ref _pageCommitmentBitmap[index]) & mask) == 0)
                {
                    // Every mapped PTE commits its containing page first. There is
                    // nothing to clear in a never-committed chunk: writing guard PTEs
                    // here would fault and materialize otherwise-unused table pages.
                    // Map/Unmap are serialized by the owning process page table.
                    ulong skipped = Math.Min(size, guestBytesPerPtPage - (va & (guestBytesPerPtPage - 1)));
                    va += skipped;
                    size -= skipped;
                    continue;
                }

                _pageTable.Unmap(va);
                _nativePageTable.Write((va / PageSize) * PteSize, GetPte(va, guardPagePtr));

                va += PageSize;
                size -= PageSize;
            }
        }

        public ulong Read(ulong va)
        {
            ulong pte = _nativePageTable.Read<ulong>((va / PageSize) * PteSize);

            pte += va & ~(ulong)PageMask;

            return pte + (va & PageMask);
        }

        public void Update(ulong va, nint ptr, ulong size)
        {
            ValidateRange(va, size);
            ulong remainingSize = size;

            while (remainingSize != 0)
            {
                EnsureCommitment(va);

                _nativePageTable.Write((va / PageSize) * PteSize, GetPte(va, ptr));

                va += PageSize;
                ptr += PageSize;
                remainingSize -= PageSize;
            }
        }

        private void EnsureCommitment(ulong va)
        {
            ulong bit = va >> _pageCommitmentBits;

            int index = (int)(bit / (sizeof(ulong) * 8));
            int shift = (int)(bit % (sizeof(ulong) * 8));

            ulong mask = 1UL << shift;

            ulong oldMask = Volatile.Read(ref _pageCommitmentBitmap[index]);

            if ((oldMask & mask) == 0)
            {
                lock (_pageCommitmentBitmap)
                {
                    oldMask = _pageCommitmentBitmap[index];

                    if ((oldMask & mask) != 0)
                    {
                        return;
                    }

                    _nativePageTable.Commit(bit * _hostPageSize, _hostPageSize);
                    Interlocked.Add(ref _committedBytes, (long)_hostPageSize);

                    Span<ulong> pageSpan = MemoryMarshal.Cast<byte, ulong>(_nativePageTable.GetSpan(bit * _hostPageSize, (int)_hostPageSize));

                    Debug.Assert(pageSpan.Length == _entriesPerPtPage);

                    nint guardPagePtr = GetGuardPagePointer();

                    for (int i = 0; i < pageSpan.Length; i++)
                    {
                        pageSpan[i] = GetPte((bit << _pageCommitmentBits) | ((ulong)i * PageSize), guardPagePtr);
                    }

                    // Publish the fully initialized guard entries before a lock-free
                    // reader decides that this native page can be accessed directly.
                    Volatile.Write(ref _pageCommitmentBitmap[index], oldMask | mask);
                }
            }
        }

        private nint GetGuardPagePointer()
        {
            return _nativePageTable.GetPointer(_nativePageTable.Size - _hostPageSize, _hostPageSize);
        }

        private static ulong GetPte(ulong va, nint ptr)
        {
            Debug.Assert((va & PageMask) == 0);

            return (ulong)ptr - va;
        }

        public ulong GetPhysicalAddress(ulong va)
        {
            return _pageTable.Read(va) + (va & PageMask);
        }

        private void ValidateRange(ulong va, ulong size)
        {
            if (((va | size) & PageMask) != 0 || va > _addressSpaceSize || size > _addressSpaceSize - va)
            {
                throw new InvalidMemoryRegionException(
                    $"Invalid native page table range: guest_va=0x{va:X}, size=0x{size:X}, address_space_bytes={_addressSpaceSize}.");
            }
        }

        private ulong VirtualMemoryEvent(ulong address, ulong size, bool write, ulong faultPc)
        {
            Interlocked.Exchange(ref _lastFaultOffset, (long)address);
            Volatile.Write(ref _lastFaultWrite, write ? 1 : 0);
            Interlocked.Exchange(ref _lastFaultPc, (long)faultPc);
            if (address < _nativePageTable.Size - _hostPageSize)
            {
                if (write) Interlocked.Increment(ref _lazyWriteFaults);
                else Interlocked.Increment(ref _lazyReadFaults);
                // Some prefetch instructions do not cause faults with invalid addresses.
                // Retry if we are hitting a case where the page table is unmapped, the next
                // run will execute the actual instruction.
                // The address loaded from the page table will be invalid, and it should hit the else case
                // if the instruction faults on unmapped or protected memory.

                ulong va = address * (PageSize / sizeof(ulong));

                EnsureCommitment(va);

                return (ulong)_nativePageTable.Pointer + address;
            }
            else
            {
                Interlocked.Increment(ref _guardFaults);
                var faults = GetFaultStatistics();
                // All invalid guest PTEs alias this guard, so its offset cannot be
                // inverted into a guest VA. Keep it protected and preserve the fault.
                throw new InvalidMemoryRegionException(
                    $"Native page table guard access: table_relative_offset=0x{address:X}, access_size={size}, write={write}, fault_pc=0x{faultPc:X}, " +
                    $"address_space_bytes={_addressSpaceSize}, table_reserved_bytes={ReservedBytes}, table_committed_bytes={CommittedBytes}, " +
                    $"lazy_read_faults={faults.LazyReads}, lazy_write_faults={faults.LazyWrites}, guard_faults={faults.GuardFaults}. Guest VA is not recoverable from the shared guard offset.");
            }
        }

        private void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    NativeSignalHandler.RemoveTrackedRegion((nuint)_nativePageTable.Pointer);

                    _nativePageTable.Dispose();
                }

                _disposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}
