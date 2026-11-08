using System.Collections.Generic;
using System.Text;

namespace MW2.Shell
{
    /// <summary>
    /// Text page of the shell (textpage.cpp, FUN_10075e50 0x10075e50, 0x34 bytes): +0 its text objects, +4 remap, +8 font,
    /// +0xc x, +0x10 y of the last line (starts one line above the box), +0x14 width, +0x18 bottom (y + height, or none when
    /// the height is negative), +0x1c line height (font height + 2), +0x2c hot links, +0x30 the typing sample (record 0x32).
    /// <see cref="Layout"/> is FUN_100765d9 0x100765d9 with the tokens of FUN_1007602a 0x1007602a; it keeps the original's
    /// quirks: widths are raw glyph sums (escapes counted as characters, FUN_1002977b), a \b or \g word subtracts its value
    /// from the line width, a word that does not fit an empty line is dropped, the last word of a text without a separator
    /// after it is dropped, an unknown escape keeps only its backslash. The pending \b / \g flags (DAT_100fad94 / 98) are
    /// globals and outlive a page.
    /// </summary>
    public sealed partial class ShellTextPage
    {
        public sealed class Line
        {
            public string Text;
            public int X, Y;
            /// <summary>\l: the line is drawn in font 7 (DAT_100cd0e4) with remap 0x100cceb0 (FUN_10076d16 0x10076d16).</summary>
            public bool Large;
            public ShellText Object;
        }

        /// <summary>A hot link of \aNN (0x14 bytes: rectangle and id).</summary>
        public struct Link
        {
            public int X0, Y0, X1, Y1, Id;
        }

        public static readonly int[] TabStops = { 0, 36, 72, 108, 144, 180, 216, 252, 288, 324, 360, 396, 432, 468, 504, 540, 576, 612, 640 };   // DAT_100bb808

        static bool _pendingBack, _pendingGo;      // DAT_100fad94, DAT_100fad98

        public readonly List<Line> Lines = new List<Line>();
        public readonly List<Link> Links = new List<Link>();
        readonly ShellFont _font, _large;
        public readonly int X, Width, Bottom, LineHeight;
        int _y;

        public ShellTextPage(ShellFont font, ShellFont large, int x, int y, int width, int height)
        {
            _font = font;
            _large = large;
            X = x;
            Width = width;
            Bottom = height >= 0 ? height + y : 0;
            LineHeight = font.Height + 2;
            _y = y - LineHeight;
        }

        /// <summary>
        /// FUN_100765d9: lay <paramref name="text"/> from <paramref name="start"/> into the page; returns where the next page
        /// starts, or -1 when the text is used up. \n new line, \c centre the line (by spaces), \s next page, \t tab stop,
        /// \l large line, \aNN link, \bNNN / \gNNN passed on to the text objects as words.
        /// </summary>
        public int Layout(string text, int start = 0)
        {
            int len = text.Length;
            int pos = start;
            int lineWidth = 0;
            var line = new StringBuilder();
            var word = new StringBuilder();
            bool center = false, large = false;
            int tabs = 0, link = -1, back = -1, tok = 4;
            while (true)
            {
                if (tok == 0)
                {
                    int w = _font.Width(word.ToString());
                    if (word.Length != 0) { line.Append(word); lineWidth += w; }
                    Flush(line, null, large);
                    if (link != -1) AddLink(false, w, lineWidth, link);
                    return -1;
                }
                int tokenStart = pos;
                tok = Token(text, word, ref pos, len, ref link, ref back);
                // Not in the original, which loops forever on a token that starts with a space (none in the game's texts).
                if (tok == 4 && word.Length == 0 && pos == tokenStart && pos < len && text[pos] == ' ') pos++;
                bool wrapped = false;
                switch (tok)
                {
                    case 1:
                    {
                        int w = _font.Width(word.ToString());
                        if (center)
                        {
                            center = false;
                            int pad = large ? (Width - _large.Width(line.ToString())) / 2 / _large.Width(" ")
                                            : (Width - lineWidth) / 2 / _font.Width(" ");
                            line.Insert(0, new string(' ', pad < 0 ? 0 : pad));
                        }
                        if (Bottom > 0 && Bottom < _y + LineHeight) return pos;
                        lineWidth = Flush(line, null, large);
                        large = false;
                        if (link != -1) { AddLink(wrapped, w, lineWidth, link); link = -1; }
                        break;
                    }
                    case 2:
                        center = true;
                        break;
                    case 3:
                        return pos;
                    case 4:
                    {
                        int w = _font.Width(word.ToString());
                        var prefixed = new StringBuilder();
                        for (int k = 0; k < tabs; k++) prefixed.Append("\\t");
                        tabs = 0;
                        if (link != -1) prefixed.Append("\\a");
                        prefixed.Append(word);
                        word.Clear().Append(prefixed);
                        if (back != -1) { lineWidth -= back; back = -1; }
                        int full = _font.Width(word.ToString());
                        if (Width < lineWidth + full)
                        {
                            lineWidth = Flush(line, word.ToString(), large);
                            large = false;
                            wrapped = true;
                        }
                        else
                        {
                            line.Append(word);
                            lineWidth += w;
                        }
                        if (link != -1)
                        {
                            int lw = _font.Width(word.ToString());
                            Links.Add(wrapped
                                ? new Link { X0 = X, Y0 = _y, X1 = X + lw, Y1 = _y + LineHeight, Id = link }
                                : new Link { X0 = X + lineWidth - lw, Y0 = _y + _font.Height, X1 = X + lineWidth, Y1 = _y + _font.Height + LineHeight, Id = link });
                            link = -1;
                        }
                        if (Bottom > 0 && Bottom < _y + LineHeight) return tokenStart;
                        break;
                    }
                    case 5:
                        tabs++;
                        for (int k = 0; k < 0x13; k++)
                        {
                            if (k == 0x12) { lineWidth = TabStops[18]; break; }
                            if (lineWidth < TabStops[k]) { lineWidth = TabStops[k]; break; }
                        }
                        break;
                    case 6: case 7: case 8:
                        break;
                    case 9:
                        large = true;
                        break;
                    default:
                        word.Clear();
                        tabs = 0;
                        break;
                }
            }
        }

        /// <summary>The link record of the end of a line (FUN_100765d9, cases 0 and 1).</summary>
        void AddLink(bool wrapped, int w, int lineWidth, int id)
        {
            Links.Add(wrapped
                ? new Link { X0 = X, Y0 = _y, X1 = X + w, Y1 = _y + LineHeight, Id = id }
                : new Link { X0 = X + lineWidth - w, Y0 = _y + _font.Height, X1 = X + lineWidth, Y1 = _y + _font.Height + LineHeight, Id = id });
        }

        /// <summary>FUN_10076517 0x10076517: next line; a non-empty line becomes a text object and the buffer takes
        /// <paramref name="next"/>, whose width is returned.</summary>
        int Flush(StringBuilder line, string next, bool large)
        {
            _y += LineHeight;
            if (line.Length == 0) return 0;
            Lines.Add(new Line { Text = line.ToString(), X = X, Y = _y, Large = large });
            line.Clear();
            if (next == null) return 0;
            line.Append(next);
            return _font.Width(next);
        }

        /// <summary>
        /// FUN_1007602a: the next token. 0 end (the word read up to the end is left in the buffer), 1 \n, 2 \c, 3 \s, 4 word
        /// (with one space per space after it), 5 \t, 6 \aNN, 7 \bNNN, 8 \gNNN, 9 \l; after 7 / 8 the next call returns the
        /// word "\b%03d" / "\g%03d". Spaces after an escape are skipped. Word characters are masked with 0x7f.
        /// </summary>
        static int Token(string t, StringBuilder word, ref int pos, int len, ref int link, ref int back)
        {
            if (pos >= len) return 0;
            if (_pendingBack) { _pendingBack = false; word.Clear().Append("\\b").Append(back.ToString("000")); return 4; }
            if (_pendingGo) { _pendingGo = false; word.Clear().Append("\\g").Append(back.ToString("000")); return 4; }
            word.Clear();
            int i = pos;
            while (true)
            {
                for (; i < len && t[i] != ' ' && t[i] != '\\'; i++) word.Append((char)(t[i] & 0x7f));
                if (i >= len) { pos = i; return 0; }
                if (word.Length > 0)
                {
                    pos = i;
                    int spaces = SkipSpaces(t, ref pos, len);
                    word.Append(' ', spaces);
                    return 4;
                }
                if (t[i] != '\\') return 4;
                char c = i + 1 < len ? t[i + 1] : '\0';
                switch (c)
                {
                    case 'A': case 'a':
                        link = Atoi(t, i + 2, 2);
                        pos = i + 4; SkipSpaces(t, ref pos, len);
                        return 6;
                    case 'B': case 'b':
                        back = Atoi(t, i + 2, 3);
                        _pendingBack = true;
                        pos = i + 5; SkipSpaces(t, ref pos, len);
                        return 7;
                    case 'C': case 'c':
                        pos = i + 2; SkipSpaces(t, ref pos, len);
                        return 2;
                    case 'G': case 'g':
                        back = Atoi(t, i + 2, 3);
                        _pendingGo = true;
                        pos = i + 5; SkipSpaces(t, ref pos, len);
                        return 8;
                    case 'L': case 'l':
                        pos = i + 2; SkipSpaces(t, ref pos, len);
                        return 9;
                    case 'N': case 'n':
                        pos = i + 2; SkipSpaces(t, ref pos, len);
                        return 1;
                    case 'S': case 's':
                        pos = i + 2; SkipSpaces(t, ref pos, len);
                        return 3;
                    case 'T': case 't':
                        pos = i + 2; SkipSpaces(t, ref pos, len);
                        return 5;
                    default:
                        i += 2;
                        word.Append('\\');
                        break;
                }
            }
        }

        /// <summary>FUN_10075fde 0x10075fde: skip spaces, returning how many.</summary>
        static int SkipSpaces(string t, ref int pos, int len)
        {
            int n = 0;
            while (pos < len && t[pos] == ' ') { pos++; n++; }
            return n;
        }

        /// <summary>atoi of the next <paramref name="count"/> characters (copied into a small buffer first).</summary>
        static int Atoi(string t, int at, int count)
        {
            var s = new StringBuilder();
            for (int k = 0; k < count && at + k < t.Length; k++) s.Append(t[at + k]);
            string v = s.ToString();
            int i = 0, sign = 1, n = 0;
            while (i < v.Length && (v[i] == ' ' || v[i] == '\t')) i++;
            if (i < v.Length && (v[i] == '-' || v[i] == '+')) { if (v[i] == '-') sign = -1; i++; }
            for (; i < v.Length && v[i] >= '0' && v[i] <= '9'; i++) n = n * 10 + (v[i] - '0');
            return n * sign;
        }
    }
}
