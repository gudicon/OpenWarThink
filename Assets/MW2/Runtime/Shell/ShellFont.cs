using System;

namespace MW2.Shell
{
    /// <summary>
    /// Shell bitmap font, a DATABASE.MW2 record ("1." header): +4 u32 glyph count, +8 u32 height (FUN_100804c0 0x100804c0),
    /// from +0x10 one u32 offset per character (FUN_100804d3 0x100804d3); a glyph is u32 width then height rows of width bytes.
    /// ShellMain 0x100428d3 loads records 5, 6, 7, 9, 10 and 11 (FUN_100296f0 0x100296f0); FUN_1002977b 0x1002977b sums widths.
    /// </summary>
    public sealed class ShellFont
    {
        readonly byte[] _d;
        public readonly int Height;
        readonly int _count;

        public ShellFont(byte[] data)
        {
            _d = data;
            _count = BitConverter.ToInt32(data, 4);
            Height = BitConverter.ToInt32(data, 8);
        }

        int GlyphOffset(int ch)
        {
            ch &= 0xff;
            if (ch >= _count) return -1;
            return BitConverter.ToInt32(_d, 0x10 + ch * 4);
        }

        /// <summary>FUN_100804d3: width of the character (the shell indexes with a signed char; codes above 127 read the table as bytes 0..255 here).</summary>
        public int Width(int ch)
        {
            int o = GlyphOffset(ch);
            return o < 0 || o + 4 > _d.Length ? 0 : BitConverter.ToInt32(_d, o);
        }

        /// <summary>FUN_1002977b: width of a string.</summary>
        public int Width(string s)
        {
            int w = 0;
            if (s == null) return 0;
            foreach (char c in s) w += Width(c);
            return w;
        }

        /// <summary>Pixel (x, y) of the glyph.</summary>
        public byte Pixel(int ch, int x, int y)
        {
            int o = GlyphOffset(ch);
            int w = BitConverter.ToInt32(_d, o);
            return _d[o + 4 + y * w + x];
        }
    }
}
