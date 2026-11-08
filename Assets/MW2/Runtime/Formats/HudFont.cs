using System;
using UnityEngine;

namespace MW2.Formats
{
    /// <summary>
    /// FONT resource "1.": u32 version, u32 glyph count (128), u32 height, u32 0xff, glyph count * u32 offsets from the
    /// resource start; a glyph is u32 width followed by width * height bytes, rows top-down. Byte 0x0e is ink and 0xff is
    /// empty; the text routine (mercsw.dll 0x1007fa67, string loop 0x1007fbe4) maps each byte through a colour table whose
    /// entry 0x0e is set to the text colour (0x0e green, 6 or 0x0b for warnings), entries 0xff are skipped.
    /// Variants: BASE6X7 (320x200, height 7), BASE6X76 (640x480, height 14), BASE6X7K (1024x768).
    /// </summary>
    public sealed class HudFont
    {
        public int Height;
        public int[] Widths;
        public byte[][] Pixels;

        public static HudFont Decode(byte[] d)
        {
            if (d.Length < 16 || d[0] != '1' || d[1] != '.') throw new ArgumentException("not a FONT resource");
            int count = BitConverter.ToInt32(d, 4);
            var font = new HudFont { Height = BitConverter.ToInt32(d, 8), Widths = new int[count], Pixels = new byte[count][] };
            for (int c = 0; c < count; c++)
            {
                int off = BitConverter.ToInt32(d, 0x10 + c * 4);
                if (off <= 0 || off + 4 > d.Length) continue;
                int w = BitConverter.ToInt32(d, off);
                if (w <= 0 || off + 4 + w * font.Height > d.Length) continue;
                font.Widths[c] = w;
                font.Pixels[c] = new byte[w * font.Height];
                Buffer.BlockCopy(d, off + 4, font.Pixels[c], 0, w * font.Height);
            }
            return font;
        }

        /// <summary>Pixel width of a string: the sum of glyph widths (0x1007fbe4 advances by each glyph's width).</summary>
        public int Measure(string text)
        {
            int w = 0;
            foreach (char ch in text) if (ch < Widths.Length) w += Widths[ch];
            return w;
        }

        /// <summary>
        /// Packs all glyphs in one row: white ink with alpha, transparent elsewhere. <paramref name="glyphX"/> receives each
        /// glyph's left edge in the atlas.
        /// </summary>
        public Texture2D ToAtlas(string name, out int[] glyphX)
        {
            glyphX = new int[Widths.Length];
            int total = 0;
            for (int c = 0; c < Widths.Length; c++) { glyphX[c] = total; total += Widths[c]; }
            var tex = new Texture2D(Math.Max(1, total), Height, TextureFormat.RGBA32, false)
            {
                name = name,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };
            var px = new Color32[tex.width * Height];
            for (int c = 0; c < Widths.Length; c++)
            {
                if (Pixels[c] == null) continue;
                for (int y = 0; y < Height; y++)
                    for (int x = 0; x < Widths[c]; x++)
                        if (Pixels[c][y * Widths[c] + x] == 0x0e)
                            px[(Height - 1 - y) * tex.width + glyphX[c] + x] = new Color32(255, 255, 255, 255);
            }
            tex.SetPixels32(px);
            tex.Apply();
            return tex;
        }
    }
}
