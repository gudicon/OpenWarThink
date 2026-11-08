using System.Collections.Generic;

namespace MW2.Shell
{
    /// <summary>
    /// Mouse hot spots of a screen (FUN_1002ec10 0x1002ec10): from a table of 0x1c-byte entries (x0, y0, x1, y1, label x, label y,
    /// label). A label starting with '<' is shown all the time (the rest of it); any other label appears while the mouse is over
    /// the spot (FUN_1002f5d6 0x1002f5d6) unless the shared "keep" flag (+0x2a, e.g. DAT_100a434c) is 0. Labels use the given font
    /// with colour 1 -> 0x16. With the outline flag the spots are framed in colour 0x11 (FUN_1002f72e). Spots are numbered by
    /// table order; FUN_1002f33f disables and FUN_1002f2b4 enables one; FUN_1002f01c returns the last enabled spot under the
    /// mouse, or -1.
    /// </summary>
    public sealed class ShellRegions
    {
        public struct Entry
        {
            public int X0, Y0, X1, Y1, LabelX, LabelY;
            public string Label;
            public Entry(int x0, int y0, int x1, int y1, int lx, int ly, string label)
            {
                X0 = x0; Y0 = y0; X1 = x1; Y1 = y1; LabelX = lx; LabelY = ly; Label = label;
            }
        }

        sealed class Spot
        {
            public Entry E;
            public int Index;
            public bool Enabled = true;     // +0x29
            public ShellText Hover;         // +0x1c
            public ShellText Fixed;         // +0x20
            public string Label;            // +0
        }

        readonly ShellSurface _surface;
        readonly ShellFont _font;
        readonly List<Spot> _spots = new List<Spot>();
        readonly byte[] _remap = ShellSurface.MakeRemap(0x16);
        readonly System.Func<bool> _keep;

        public ShellRegions(ShellSurface surface, ShellFont font, bool outline, IList<Entry> table, System.Func<bool> keepLabels)
        {
            _surface = surface;
            _font = font;
            _keep = keepLabels;
            for (int i = 0; i < table.Count; i++)
            {
                var s = new Spot { E = table[i], Index = i, Label = table[i].Label };
                if (s.Label != null && s.Label.StartsWith("<"))
                {
                    s.Label = s.Label.Substring(1);
                    s.Fixed = new ShellText(surface, font, s.Label, s.E.LabelX, s.E.LabelY, _remap);
                    s.Fixed.Show();
                }
                if (outline)
                {
                    surface.Fill(s.E.X0, s.E.Y1, s.E.X1, s.E.Y1, 0x11);
                    surface.Fill(s.E.X0, s.E.Y0, s.E.X1, s.E.Y0, 0x11);
                    surface.Fill(s.E.X1, s.E.Y0, s.E.X1, s.E.Y1, 0x11);
                    surface.Fill(s.E.X0, s.E.Y1, s.E.X0, s.E.Y0, 0x11);
                }
                _spots.Add(s);
            }
        }

        /// <summary>FUN_1002f01c 0x1002f01c with FUN_1002f5d6: update the labels and return the spot under the mouse.</summary>
        public int Hit(int x, int y)
        {
            int found = -1;
            foreach (var s in _spots)
                if (Over(s, x, y) && s.Enabled) found = s.Index;
            return found;
        }

        bool Over(Spot s, int x, int y)
        {
            if (s.Enabled && !(s.E.Y1 < y || y < s.E.Y0 || s.E.X1 < x || x < s.E.X0))
            {
                if (s.Hover == null && !string.IsNullOrEmpty(s.Label))
                {
                    s.Hover = new ShellText(_surface, _font, s.Label, s.E.LabelX, s.E.LabelY, _remap);
                    s.Hover.Show();
                }
                if (s.Hover != null && !_keep()) s.Hover.Hide();
                return true;
            }
            if (s.Hover != null)
            {
                s.Hover.Hide();
                s.Hover = null;
                s.Fixed?.Draw();
            }
            return false;
        }

        /// <summary>FUN_1002f33f 0x1002f33f: disable spot n (its labels go away).</summary>
        public void Disable(int n)
        {
            foreach (var s in _spots)
                if (s.Index == n && s.Enabled)
                {
                    s.Enabled = false;
                    if (s.Hover != null) { s.Hover.Hide(); s.Hover = null; }
                    s.Fixed?.Hide();
                }
        }

        /// <summary>FUN_1002f2b4 0x1002f2b4: enable spot n (its fixed label comes back).</summary>
        public void Enable(int n)
        {
            foreach (var s in _spots)
                if (s.Index == n && !s.Enabled)
                {
                    s.Enabled = true;
                    s.Fixed?.Show();
                }
        }

        /// <summary>FUN_1002ee25 0x1002ee25: delete every spot and its labels.</summary>
        public void Dispose()
        {
            foreach (var s in _spots)
            {
                s.Hover?.Hide();
                s.Fixed?.Hide();
            }
            _spots.Clear();
        }
    }
}
