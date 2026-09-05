using System.Collections.Generic;
using System.Threading;

namespace Ryujinx.Memory
{
    public class PageTable<T> where T : unmanaged
    {
        public const int PageBits = 12;
        public const int PageSize = 1 << PageBits;
        public const int PageMask = PageSize - 1;

        private const int PtLevelBits = 9; // 9 * 4 + 12 = 48 (max address space size)
        private const int PtLevelSize = 1 << PtLevelBits;
        private const int PtLevelMask = PtLevelSize - 1;

        private readonly T[][][][] _pageTable;
        private int _allocatedLeafCount;

        public int AllocatedLeafCount => Volatile.Read(ref _allocatedLeafCount);

        public PageTable()
        {
            _pageTable = new T[PtLevelSize][][][];
        }

        public T Read(ulong va)
        {
            int l3 = (int)(va >> PageBits) & PtLevelMask;
            int l2 = (int)(va >> (PageBits + PtLevelBits)) & PtLevelMask;
            int l1 = (int)(va >> (PageBits + PtLevelBits * 2)) & PtLevelMask;
            int l0 = (int)(va >> (PageBits + PtLevelBits * 3)) & PtLevelMask;

            if (_pageTable[l0] == null)
            {
                return default;
            }

            if (_pageTable[l0][l1] == null)
            {
                return default;
            }

            if (_pageTable[l0][l1][l2] == null)
            {
                return default;
            }

            return _pageTable[l0][l1][l2][l3];
        }

        public void Map(ulong va, T value)
        {
            int l3 = (int)(va >> PageBits) & PtLevelMask;
            int l2 = (int)(va >> (PageBits + PtLevelBits)) & PtLevelMask;
            int l1 = (int)(va >> (PageBits + PtLevelBits * 2)) & PtLevelMask;
            int l0 = (int)(va >> (PageBits + PtLevelBits * 3)) & PtLevelMask;

            if (_pageTable[l0] == null)
            {
                _pageTable[l0] = new T[PtLevelSize][][];
            }

            if (_pageTable[l0][l1] == null)
            {
                _pageTable[l0][l1] = new T[PtLevelSize][];
            }

            if (_pageTable[l0][l1][l2] == null)
            {
                _pageTable[l0][l1][l2] = new T[PtLevelSize];
                Interlocked.Increment(ref _allocatedLeafCount);
            }

            _pageTable[l0][l1][l2][l3] = value;
        }

        public void Unmap(ulong va)
        {
            int l3 = (int)(va >> PageBits) & PtLevelMask;
            int l2 = (int)(va >> (PageBits + PtLevelBits)) & PtLevelMask;
            int l1 = (int)(va >> (PageBits + PtLevelBits * 2)) & PtLevelMask;
            int l0 = (int)(va >> (PageBits + PtLevelBits * 3)) & PtLevelMask;

            if (_pageTable[l0] == null)
            {
                return;
            }

            if (_pageTable[l0][l1] == null)
            {
                return;
            }

            if (_pageTable[l0][l1][l2] == null)
            {
                return;
            }

            _pageTable[l0][l1][l2][l3] = default;

            bool empty = true;

            for (int i = 0; i < _pageTable[l0][l1][l2].Length; i++)
            {
                // Equals(object) with an untyped default compares a boxed value with null.
                // Compare with default(T), and stop scanning as soon as a live entry is found.
                if (!EqualityComparer<T>.Default.Equals(_pageTable[l0][l1][l2][i], default))
                {
                    empty = false;
                    break;
                }
            }

            if (empty)
            {
                _pageTable[l0][l1][l2] = null;
                Interlocked.Decrement(ref _allocatedLeafCount);

                RemoveIfAllNull(l0, l1);
            }
        }

        private void RemoveIfAllNull(int l0, int l1)
        {
            bool empty = true;

            for (int i = 0; i < _pageTable[l0][l1].Length; i++)
            {
                empty &= (_pageTable[l0][l1][i] == null);
            }

            if (empty)
            {
                _pageTable[l0][l1] = null;

                RemoveIfAllNull(l0);
            }
        }

        private void RemoveIfAllNull(int l0)
        {
            bool empty = true;

            for (int i = 0; i < _pageTable[l0].Length; i++)
            {
                empty &= (_pageTable[l0][i] == null);
            }

            if (empty)
            {
                _pageTable[l0] = null;
            }
        }
    }
}
