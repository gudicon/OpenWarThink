namespace MW2.Shell
{
    /// <summary>
    /// Text object of the shell (FUN_10075c52 0x10075c52, 0x3e bytes): a string at x, y in a font with a colour remap, kept in a
    /// surface draw list and redrawn there (FUN_100756a5 0x100756a5). A leading '~' centres it on x. Its erase rectangle is the
    /// raw string width (escape codes included, FUN_1002977b) by the font height. Mode 0 draws the raw string at once; mode 1
    /// (+0x10) types it one character per step (FUN_10075741 0x10075741) and understands escapes: \a highlight remap
    /// (DAT_100bb858, colour 1 -> 0x11), \bNNN move back NNN pixels, \gNNN go to start + NNN, \t next tab stop (DAT_100bb808,
    /// every 36 pixels up to 640); a space returns to the normal remap.
    /// </summary>
    public sealed class ShellText
    {
        readonly ShellSurface _surface;
        public readonly ShellFont Font;
        public readonly string Text;
        public readonly int X, Y, Width, Height;
        public byte[] Remap;
        byte[] _current;
        /// <summary>+0x10: 0 whole string, 1 typewriter.</summary>
        public int Mode;
        /// <summary>+0x14: in the surface list.</summary>
        bool _listed;
        /// <summary>+0x35: everything shown.</summary>
        public bool Done;
        int _penX = -1, _index = -1;
        /// <summary>+0x21: erase width, grown as the pen passes the measured width.</summary>
        int _extent;

        public static readonly byte[] HighlightRemap = ShellSurface.MakeRemap(0x11);
        static readonly int[] TabStops = { 0, 36, 72, 108, 144, 180, 216, 252, 288, 324, 360, 396, 432, 468, 504, 540, 576, 612, 640 };

        public ShellText(ShellSurface surface, ShellFont font, string text, int x, int y, byte[] remap)
        {
            _surface = surface;
            Font = font;
            Text = text != null && text.StartsWith("~") ? text.Substring(1) : text ?? "";
            Width = font.Width(Text);
            Height = font.Height;
            X = text != null && text.StartsWith("~") ? x - Width / 2 : x;
            Y = y;
            Remap = remap;
            _current = remap;
            _extent = Width;
        }

        /// <summary>FUN_100756a5: draw (mode 0) or redraw the typed part (mode 1).</summary>
        public void Draw()
        {
            if (Text.Length == 0) return;
            if (Mode == 1)
            {
                int upTo = _index;
                _index = -1;
                while (_index < upTo) Step();
            }
            else
            {
                _surface.DrawString(Font, X, Y, Text, Remap);
                Done = true;
            }
        }

        /// <summary>FUN_10078896 + FUN_100756a5: put into a draw list and draw; list 1 "after" (FUN_100298a0 0x100298a0),
        /// 0 "before" (FUN_100297f1 0x100297f1).</summary>
        public void Show(int list = 1)
        {
            var l = list == 0 ? _surface.DrawBefore : _surface.DrawAfter;
            if (!l.Contains(this)) l.Add(this);
            Draw();
        }

        /// <summary>FUN_10075c17 0x10075c17: one typewriter step; true when the string is complete.</summary>
        public bool Advance() => Done || Text.Length == 0 || Step();

        /// <summary>FUN_10075741: type the next character or escape.</summary>
        bool Step()
        {
            if (!_listed)
            {
                if (!_surface.DrawAfter.Contains(this)) _surface.DrawAfter.Add(this);
                _listed = true;
            }
            if (_index < 0)
            {
                _current = Remap;
                _index = 0;
                _penX = X;
            }
            if (_index >= Text.Length)
            {
                _current = Remap;
                Done = true;
                return true;
            }
            char c = Text[_index];
            if (c == '\\')
            {
                _current = Remap;
                _index++;
                if (_index < Text.Length)
                    switch (Text[_index])
                    {
                        case 'A': case 'a':
                            _index++;
                            _current = HighlightRemap;
                            break;
                        case 'B': case 'b':
                            _index++;
                            _penX -= Number3();
                            break;
                        case 'G': case 'g':
                            _index++;
                            _penX = X + Number3();
                            break;
                        case 'T': case 't':
                            _index++;
                            for (int k = 0; k < TabStops.Length; k++)
                                if (_penX - X < TabStops[k]) { _penX = X + TabStops[k]; break; }
                            break;
                    }
            }
            else
            {
                if (c == ' ') _current = Remap;
                _penX += _surface.DrawChar(Font, _penX, Y, c, _current);
                _index++;
            }
            if (_extent < _penX - X + 1) _extent = _penX - X + 1;
            return false;
        }

        /// <summary>Three characters read as atoi (the shell copies them into a 4-byte buffer).</summary>
        int Number3()
        {
            int v = 0, sign = 1, k = 0;
            string s = "";
            for (; k < 3 && _index < Text.Length; k++) s += Text[_index++];
            int i = 0;
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            if (i < s.Length && (s[i] == '-' || s[i] == '+')) { if (s[i] == '-') sign = -1; i++; }
            for (; i < s.Length && char.IsDigit(s[i]); i++) v = v * 10 + (s[i] - '0');
            return v * sign;
        }

        /// <summary>FUN_10076e86 0x10076e86 on one text: +0x35 = 0, +0x36 = +0x3a = -1, typewriter mode; nothing is erased.</summary>
        public void ResetTyping()
        {
            Done = false;
            _penX = -1;
            _index = -1;
            Mode = 1;
        }

        /// <summary>FUN_10075d84 0x10075d84: take out of the list and erase its rectangle.</summary>
        public void Hide()
        {
            Done = false;
            _penX = -1;
            _index = -1;
            _surface.DrawAfter.Remove(this);
            _surface.DrawBefore.Remove(this);
            _listed = false;
            _surface.Restore(X, Y, _extent, Height);
        }
    }
}
