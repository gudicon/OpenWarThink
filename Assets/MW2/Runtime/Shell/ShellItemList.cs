using System;
using System.Collections.Generic;

namespace MW2.Shell
{
    /// <summary>
    /// A list of clickable text items of the shell (0x38-byte entries ended by x = -1): +0 x, +4 y (negative: below the
    /// previous entry), +8 width (-1: the
    /// text's), +0xc height (-1: the text's, else the previous entry's), +0x14 remap, +0x18 the text object made by the
    /// draw function +0x1c, +0x20 the click function, +0x24 / +0x28 data, +0x2c state (0, 1 selected, 2 just clicked).
    /// FUN_1004b400 0x1004b400 lays it out, FUN_1004b50e 0x1004b50e redraws it (a state 2 becomes the only selected
    /// one), FUN_1004b647 0x1004b647 frees the texts, FUN_1004b6c3 0x1004b6c3 clears the states, FUN_1004b78e
    /// 0x1004b78e finds the clickable entry under the mouse.
    /// </summary>
    public sealed class ShellItemList
    {
        public sealed class Entry
        {
            public int X, Y, W = -1, H = -1;
            public byte[] Remap;
            public ShellText Text;
            public Func<Entry, ShellText> Draw;
            public Action<Entry> Click;
            public int Data, Data2, State;
        }

        public readonly List<Entry> Entries = new List<Entry>();

        /// <summary>FUN_1004b400.</summary>
        public void Init()
        {
            int previousY = 0, previousH = 0;
            foreach (var e in Entries)
            {
                // A negative y follows the previous entry: its y, (y & 0xff0) >> 4 of its heights and y & 0xf pixels.
                if (e.Y < 0) e.Y = previousY + previousH * ((e.Y & 0xff0) >> 4) + (e.Y & 0xf);
                e.Text = e.Draw?.Invoke(e);
                if (e.Text != null)
                {
                    if (e.W == -1) e.W = e.Text.Width;
                    if (e.H == -1) e.H = e.Text.Height;
                }
                if (e.W == -1) e.W = 0;
                if (e.H == -1) e.H = previousH;
                previousY = e.Y;
                previousH = e.H;
            }
        }

        /// <summary>FUN_1004b50e.</summary>
        public void Refresh()
        {
            if (Entries.Exists(e => e.State == 2))
                foreach (var e in Entries) e.State = e.State == 2 ? 1 : 0;
            Free();
            foreach (var e in Entries) e.Text = e.Draw?.Invoke(e);
        }

        /// <summary>FUN_1004b647.</summary>
        public void Free()
        {
            foreach (var e in Entries)
            {
                e.Text?.Hide();
                e.Text = null;
            }
        }

        /// <summary>FUN_1004b6c3.</summary>
        public void ClearStates()
        {
            foreach (var e in Entries) e.State = 0;
        }

        /// <summary>FUN_1004b78e.</summary>
        public Entry Hit(int x, int y)
        {
            foreach (var e in Entries)
                if (e.Click != null && e.X <= x && x < e.X + e.W && e.Y <= y && y < e.Y + e.H) return e;
            return null;
        }

        /// <summary>FUN_1002e4b4 0x1002e4b4: the data of the first selected entry, or -1.</summary>
        public int SelectedData()
        {
            foreach (var e in Entries) if (e.State == 1) return e.Data;
            return -1;
        }
    }
}
