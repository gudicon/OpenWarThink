using System;
using MW2.Formats;

namespace MW2.Shell
{
    /// <summary>
    /// Shell bitmap font, a DATABASE.MW2 record ("1." header): +4 u32 glyph count, +8 u32 height (FUN_100804c0 0x100804c0),
    /// from +0x10 one u32 offset per character (FUN_100804d3 0x100804d3); a glyph is u32 width then height rows of width bytes.
    /// ShellMain 0x100428d3 loads records 5, 6, 7, 9, 10 and 11 (FUN_100296f0 0x100296f0); FUN_1002977b 0x1002977b sums widths.
    /// A language mod (port addition) may translate the strings measured and drawn (<see cref="Translate"/>) and add letters
    /// from U+00A0 (<see cref="ExtraGlyphs"/>); a font with capitals only draws a missing small letter as its capital, as
    /// the records do for Latin. A translated Latin-1 letter the mod does not draw uses the record's own code page 437
    /// glyph, else its plain letter (<see cref="Local"/>). Without a language mod both are null and nothing changes.
    /// </summary>
    public sealed class ShellFont
    {
        /// <summary>Port addition: the translation of a shown string, or null for the original's text.</summary>
        public static Func<string, string> Translate;
        /// <summary>Port addition: extra letters of a font by name (shell5 ... shell11), or null.</summary>
        public static Func<string, LangPack.GlyphSet> ExtraGlyphs;
        /// <summary>Port addition: bumped when the language changes, so fonts look their extra letters up again.</summary>
        public static int ExtraVersion;

        readonly byte[] _d;
        public readonly int Height;
        readonly int _count;
        /// <summary>Port addition: "shell" + the record number, the name of its extra letters.</summary>
        public string Name;

        LangPack.GlyphSet _extra;
        int _extraVersion = -1;
        byte _ink = 1;

        public ShellFont(byte[] data, string name = null)
        {
            _d = data;
            _count = BitConverter.ToInt32(data, 4);
            Height = BitConverter.ToInt32(data, 8);
            Name = name;
        }

        /// <summary>The translated text when a language mod has it, else <paramref name="s"/>.</summary>
        public static string Tr(string s)
        {
            if (Translate == null || string.IsNullOrEmpty(s)) return s;
            return Translate(s) ?? s;
        }

        int GlyphOffset(int ch)
        {
            ch &= 0xff;
            if (ch >= _count) return -1;
            return BitConverter.ToInt32(_d, 0x10 + ch * 4);
        }

        LangPack.Glyph Extra(int ch)
        {
            if (ch < 0xa0 || ExtraGlyphs == null || Name == null) return null;
            if (_extraVersion != ExtraVersion)
            {
                _extraVersion = ExtraVersion;
                _extra = ExtraGlyphs(Name);
                if (_extra != null && _extra.Height != Height) _extra = null;
                // The ink of this record: the commonest colour of 'A' other than empty (0xff) and shadow (0).
                var counts = new int[256];
                int o = GlyphOffset('A');
                if (o >= 0 && o + 4 <= _d.Length)
                {
                    int w = BitConverter.ToInt32(_d, o);
                    for (int i = 0; i < w * Height && o + 4 + i < _d.Length; i++) counts[_d[o + 4 + i]]++;
                }
                int best = 1;
                for (int v = 1; v < 255; v++) if (counts[v] > counts[best]) best = v;
                _ink = (byte)best;
            }
            if (_extra == null) return null;
            if (_extra.Glyphs.TryGetValue((char)ch, out var g)) return g;
            char up = char.ToUpperInvariant((char)ch);
            return up != ch && _extra.Glyphs.TryGetValue(up, out g) ? g : null;
        }

        /// <summary>
        /// Port addition: with a language mod, a letter from U+00A0 (translations are Unicode, the records code page 437)
        /// becomes the code the record draws it at: its code page 437 code when the record has ink there, else its plain
        /// letter (LangPack.BaseLetter); -1 when there is none. Without a language mod every code stays as it is.
        /// </summary>
        int Local(int ch)
        {
            if (ch < 0xa0 || Translate == null) return ch;
            int cp = LangPack.Cp437((char)ch);
            if (cp >= 0 && Inked(cp)) return cp;
            char b = LangPack.BaseLetter((char)ch);
            return b != 0 && b < 0x80 ? b : -1;
        }

        bool[] _inked;

        /// <summary>The record has a glyph with ink (not only empty 0xff and shadow 0) at <paramref name="code"/>.</summary>
        bool Inked(int code)
        {
            if (_inked == null)
            {
                _inked = new bool[Math.Min(_count, 256)];
                for (int c = 0; c < _inked.Length; c++)
                {
                    int o = GlyphOffset(c);
                    if (o < 0 || o + 4 > _d.Length) continue;
                    int w = BitConverter.ToInt32(_d, o);
                    for (int i = 0; i < w * Height && o + 4 + i < _d.Length && !_inked[c]; i++)
                        _inked[c] = _d[o + 4 + i] != 0xff && _d[o + 4 + i] != 0;
                }
            }
            return code >= 0 && code < _inked.Length && _inked[code];
        }

        /// <summary>Port addition: the font draws <paramref name="ch"/> as itself (a mod's letter, or the record's own glyph
        /// with ink), not as a plain letter or nothing.</summary>
        public bool Has(int ch)
        {
            if (Extra(ch) != null) return true;
            int code = ch < 0xa0 ? ch : ch <= 0xff ? LangPack.Cp437((char)ch) : -1;
            return code >= 0 && code < 0x100 && Inked(code);
        }

        /// <summary>FUN_100804d3: width of the character (the shell indexes with a signed char; codes above 127 read the table as bytes 0..255 here).</summary>
        public int Width(int ch)
        {
            var e = Extra(ch);
            if (e != null) return e.Width;
            ch = Local(ch);
            if (ch < 0) return 0;
            int o = GlyphOffset(ch);
            return o < 0 || o + 4 > _d.Length ? 0 : BitConverter.ToInt32(_d, o);
        }

        /// <summary>FUN_1002977b: width of a string as shown (translated when a language mod has it).</summary>
        public int Width(string s) => RawWidth(Tr(s));

        /// <summary>FUN_1002977b: width of a string as it is.</summary>
        public int RawWidth(string s)
        {
            int w = 0;
            if (s == null) return 0;
            foreach (char c in s) w += Width(c);
            return w;
        }

        /// <summary>Pixel (x, y) of the glyph.</summary>
        public byte Pixel(int ch, int x, int y)
        {
            var e = Extra(ch);
            if (e != null)
            {
                byte v = e.Pixels[y * e.Width + x];
                return v == 1 ? _ink : v == 2 ? (byte)0 : (byte)0xff;
            }
            ch = Local(ch);
            if (ch < 0) return 0xff;
            int o = GlyphOffset(ch);
            int w = BitConverter.ToInt32(_d, o);
            return _d[o + 4 + y * w + x];
        }
    }
}
