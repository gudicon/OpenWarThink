using System;
using System.Collections.Generic;

namespace MW2.Shell
{
    /// <summary>
    /// An item list of the shell (peck collect.c: FUN_100093c0 0x100093c0 makes it, here always with the comparator
    /// FUN_10048d27 0x10048d27, which compares the first int: the table index). FUN_100095df 0x100095df appends and marks
    /// the list unsorted (+0xd); FUN_100098b9 0x100098b9 sorts an unsorted list with the C runtime's qsort; FUN_10009724
    /// 0x10009724 removes the entry FUN_100097dc finds: with the comparator the first one with the same index, without it
    /// (+0x19 cleared for the call) the record itself; FUN_10049345 0x10049345 finds the first record of an index.
    /// </summary>
    public sealed class ShellCollection
    {
        public readonly List<ItemRecord> Items;
        public bool Unsorted;       // +0xd

        public ShellCollection() { Items = new List<ItemRecord>(); }
        /// <summary>A list the career keeps (the unit's or the dealer's ammunition), taken as unsorted.</summary>
        public ShellCollection(List<ItemRecord> items) { Items = items; Unsorted = true; }

        public int Count => Items.Count;
        public ItemRecord this[int i] => Items[i];

        /// <summary>FUN_100095df.</summary>
        public void Add(ItemRecord it)
        {
            Items.Add(it);
            Unsorted = true;
        }

        /// <summary>FUN_10009724 with the comparator: the first record of the same index.</summary>
        public void RemoveSameIndex(ItemRecord it) => Career.RemoveItem(Items, it);

        /// <summary>FUN_10009724 with +0x19 cleared: the record itself.</summary>
        public void RemoveRecord(ItemRecord it) => Items.Remove(it);

        /// <summary>FUN_10049345.</summary>
        public ItemRecord FirstOfIndex(int index) => Items.Find(x => x.Index == index);

        /// <summary>FUN_100098b9.</summary>
        public void SortIfUnsorted()
        {
            if (!Unsorted) return;
            CrtQsort(Items, (a, b) => a.Index.CompareTo(b.Index));
            Unsorted = false;
        }

        /// <summary>
        /// qsort of MSVCRT.DLL, which MW2SHELL.DLL imports: partitions above 8 elements around the median of the first,
        /// middle and last, smaller ones by "shortsort" (the largest to the end). Equal elements are reordered the way the
        /// system library does it; checked against msvcrt.dll's qsort on random arrays with repeated keys.
        /// </summary>
        public static void CrtQsort<T>(IList<T> v, Comparison<T> c)
        {
            int n = v.Count;
            if (n < 2) return;
            void Swap(int a, int b) { if (a != b) { T t = v[a]; v[a] = v[b]; v[b] = t; } }
            var stack = new Stack<(int, int)>();
            int lo = 0, hi = n - 1;
            while (true)
            {
                int size = hi - lo + 1;
                bool next = false;
                if (size <= 8)
                {
                    for (int h = hi; h > lo; h--)
                    {
                        int max = lo;
                        for (int p = lo + 1; p <= h; p++) if (c(v[p], v[max]) > 0) max = p;
                        Swap(max, h);
                    }
                }
                else
                {
                    int mid = lo + size / 2;
                    if (c(v[lo], v[mid]) > 0) Swap(lo, mid);
                    if (c(v[lo], v[hi]) > 0) Swap(lo, hi);
                    if (c(v[mid], v[hi]) > 0) Swap(mid, hi);
                    int loguy = lo, higuy = hi;
                    while (true)
                    {
                        if (mid > loguy)
                        {
                            do loguy++; while (loguy < mid && c(v[loguy], v[mid]) <= 0);
                        }
                        if (mid <= loguy)
                        {
                            do loguy++; while (loguy <= hi && c(v[loguy], v[mid]) <= 0);
                        }
                        do higuy--; while (higuy > mid && c(v[higuy], v[mid]) > 0);
                        if (higuy < loguy) break;
                        Swap(loguy, higuy);
                        if (mid == higuy) mid = loguy;
                    }
                    higuy++;
                    if (mid < higuy)
                    {
                        do higuy--; while (higuy > mid && c(v[higuy], v[mid]) == 0);
                    }
                    if (mid >= higuy)
                    {
                        do higuy--; while (higuy > lo && c(v[higuy], v[mid]) == 0);
                    }
                    if (higuy - lo >= hi - loguy)
                    {
                        if (lo < higuy) stack.Push((lo, higuy));
                        if (loguy < hi) { lo = loguy; next = true; }
                    }
                    else
                    {
                        if (loguy < hi) stack.Push((loguy, hi));
                        if (lo < higuy) { hi = higuy; next = true; }
                    }
                }
                if (next) continue;
                if (stack.Count == 0) return;
                (lo, hi) = stack.Pop();
            }
        }
    }
}
