using System;
using UnityEngine;

namespace MW2.Formats
{
    /// <summary>
    /// SHP "1.10" HUD sprite: at +0x10 u16 height, u16 width, u16 hot y, u16 hot x, i32 bounds x0, y0, x1, y1;
    /// rows from +0x28, one command list per row: 00 end of row, 01 n skip n pixels, even k: k/2 pixels of the next colour,
    /// odd k >= 3: (k-1)/2 literal colours follow. Variants: plain (320x200), "6" (640x480), "K" (1024x768).
    /// </summary>
    public sealed class Shape
    {
        public int Width, Height, HotX, HotY;
        /// <summary>Size fields of the header (+0x12 width, +0x10 height): one less than the drawn size for most sprites.</summary>
        public int HeaderWidth, HeaderHeight;
        public int[] Pixels;        // palette index, -1 = transparent; rows top-down
        /// <summary>Bounds x0, y0 (+0x18, +0x1c): where Pixels[0] lies relative to the drawing point (shell blitter FUN_1007c9a0).</summary>
        public int BoundsX0, BoundsY0;

        /// <summary>
        /// Shell SHP files (game SMK folder, animate.cpp FUN_10001b4b) hold several frames: u32 count at +4 (FUN_100810a3), then per
        /// frame a pair of u32 (offset, 0) from +8 (FUN_10080f23); each frame has the layout of the PRJ sprite from its +0x10.
        /// </summary>
        public static Shape[] DecodeFrames(byte[] d)
        {
            int count = BitConverter.ToInt32(d, 4);
            var frames = new Shape[count];
            for (int i = 0; i < count; i++) frames[i] = Decode(d, BitConverter.ToInt32(d, 8 + i * 8) - 0x10);
            return frames;
        }

        public static Shape Decode(byte[] d) => Decode(d, 0);

        static Shape Decode(byte[] d, int b)
        {
            var s = new Shape
            {
                HeaderHeight = BitConverter.ToUInt16(d, b + 0x10),
                HeaderWidth = BitConverter.ToUInt16(d, b + 0x12),
                HotY = BitConverter.ToUInt16(d, b + 0x14),
                HotX = BitConverter.ToUInt16(d, b + 0x16)
            };
            // The rows cover the bounds x0..x1, y0..y1 (+0x18..+0x24, inclusive), usually one more than the header size
            // (TGTGTE6: header 44, bounds -22..22): the canvas takes the larger of the two.
            s.BoundsX0 = BitConverter.ToInt32(d, b + 0x18);
            s.BoundsY0 = BitConverter.ToInt32(d, b + 0x1C);
            int bw = BitConverter.ToInt32(d, b + 0x20) - BitConverter.ToInt32(d, b + 0x18) + 1;
            int bh = BitConverter.ToInt32(d, b + 0x24) - BitConverter.ToInt32(d, b + 0x1C) + 1;
            s.Width = bw > 0 && bw <= 4096 ? Math.Max(s.HeaderWidth, bw) : s.HeaderWidth;
            s.Height = bh > 0 && bh <= 4096 ? Math.Max(s.HeaderHeight, bh) : s.HeaderHeight;
            s.Pixels = new int[s.Width * s.Height];
            for (int i = 0; i < s.Pixels.Length; i++) s.Pixels[i] = -1;
            // The blitter (FUN_1007c9a0 0x1007c9a0) reads rows y0..y1 of the bounds only; a header taller than the bounds
            // (SMK\RARM.SHP frame 0: header 99, bounds 2..97) has no rows past y1, the bytes there are the next frame.
            int rows = bh > 0 && bh <= 4096 ? bh : s.HeaderHeight;
            int p = b + 0x28;
            for (int y = 0; y < rows && p < d.Length; y++)
            {
                int x = 0;
                while (p < d.Length)
                {
                    int op = d[p++];
                    if (op == 0) break;
                    if (op == 1)
                    {
                        x += d[p++];
                    }
                    else if (op % 2 == 0)
                    {
                        int c = d[p++];
                        for (int k = 0; k < op / 2; k++, x++) s.Set(x, y, c);
                    }
                    else
                    {
                        for (int k = 0; k < (op - 1) / 2; k++, x++) s.Set(x, y, d[p++]);
                    }
                }
            }
            return s;
        }

        void Set(int x, int y, int c)
        {
            if (x >= 0 && x < Width) Pixels[y * Width + x] = c;
        }

        public Texture2D ToTexture(Color32[] palette, string name)
        {
            var tex = new Texture2D(Width, Height, TextureFormat.RGBA32, false)
            {
                name = name,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };
            var px = new Color32[Width * Height];
            for (int y = 0; y < Height; y++)
                for (int x = 0; x < Width; x++)
                {
                    int c = Pixels[y * Width + x];
                    px[(Height - 1 - y) * Width + x] = c < 0 ? new Color32(0, 0, 0, 0) : palette[c];
                }
            tex.SetPixels32(px);
            tex.Apply();
            return tex;
        }
    }
}
