using System;
using System.Collections.Generic;
using MW2.Formats;

namespace MW2.Shell
{
    /// <summary>
    /// The shell's 640x480 8-bit screen object (FUN_1007738f 0x1007738f, DAT_100cd0c0): the front buffer (+0x2e, view +0x56) shown
    /// on the display, the background buffer (+0x42, view +0x6a) that erased areas are restored from, the 6-bit palette (+0x92),
    /// the dirty rectangle (+0x82..+0x8e, FUN_1007765d / FUN_100776d2, emptied after each present by FUN_10077849) and two draw
    /// lists of text objects redrawn around the animations (+0x1a before, +0x16 after; FUN_10078896, FUN_100788f4).
    /// The DirectDraw driver shows palette entries as the 6-bit value shifted left by 2 (FUN_1004dc5d 0x1004dc5d).
    /// </summary>
    public sealed class ShellSurface
    {
        public const int Width = 640, Height = 480;
        public readonly byte[] Front = new byte[Width * Height];
        public readonly byte[] Back = new byte[Width * Height];
        public readonly byte[] Palette6 = new byte[768];
        /// <summary>+0x1e: the palette changed and the whole screen must be shown again.</summary>
        public bool PaletteDirty;
        /// <summary>+0x3a6: transparent key of background restores (-1 = none).</summary>
        public int RestoreKey = -1;

        int _dx0 = Width - 1, _dy0 = Height - 1, _dx1, _dy1;
        /// <summary>DAT_100fb1bc: set while the draw lists are redrawn; text then draws only where the dirty rectangle reaches.</summary>
        public bool InDrawListPass;

        public readonly List<ShellText> DrawAfter = new List<ShellText>();    // +0x16
        public readonly List<ShellText> DrawBefore = new List<ShellText>();   // +0x1a

        /// <summary>FUN_1007765d: grow the dirty rectangle by an inclusive rectangle.</summary>
        public void MarkInclusive(int x0, int y0, int x1, int y1)
        {
            if (x0 < _dx0) _dx0 = x0;
            if (y0 < _dy0) _dy0 = y0;
            if (_dx1 < x1) _dx1 = x1;
            if (_dy1 < y1) _dy1 = y1;
        }

        /// <summary>FUN_100776d2: grow the dirty rectangle by x, y, w, h.</summary>
        public void Mark(int x, int y, int w, int h) => MarkInclusive(x, y, x - 1 + w, y - 1 + h);

        public void MarkAll() => MarkInclusive(0, 0, Width - 1, Height - 1);

        /// <summary>FUN_100777b1: does x, y, w, h touch the dirty rectangle?</summary>
        public bool Touches(int x, int y, int w, int h) =>
            !(_dx1 < x || x - 1 + w < _dx0 || _dy1 < y || y - 1 + h < _dy0);

        public bool HasDirty => _dx0 <= _dx1 && _dy0 <= _dy1;

        /// <summary>FUN_10077849: clamp the dirty rectangle, report it to the display and empty it.</summary>
        public bool Present(out int x0, out int y0, out int x1, out int y1)
        {
            if (_dx0 < 0) _dx0 = 0;
            if (_dy0 < 0) _dy0 = 0;
            if (Width - 1 < _dx1) _dx1 = Width - 1;
            if (Height - 1 < _dy1) _dy1 = Height - 1;
            bool any = PaletteDirty || (_dx0 <= _dx1 && _dy0 <= _dy1);
            x0 = PaletteDirty ? 0 : _dx0; y0 = PaletteDirty ? 0 : _dy0;
            x1 = PaletteDirty ? Width - 1 : _dx1; y1 = PaletteDirty ? Height - 1 : _dy1;
            PaletteDirty = false;
            _dx0 = Width - 1; _dy0 = Height - 1; _dx1 = 0; _dy1 = 0;
            return any;
        }

        /// <summary>
        /// FUN_10077f39 0x10077f39: a PCX record becomes the screen. A new palette is taken whole; the picture goes to the background
        /// and front buffers and the whole screen is marked.
        /// </summary>
        public void LoadBackground(ShellPcx pcx)
        {
            bool same = true;
            for (int i = 0; i < 768; i++) if (pcx.Palette6[i] != Palette6[i]) { same = false; break; }
            if (!same)
            {
                Buffer.BlockCopy(pcx.Palette6, 0, Palette6, 0, 768);
                PaletteDirty = true;
            }
            CopyPcx(pcx, Back);
            Buffer.BlockCopy(Back, 0, Front, 0, Front.Length);
            MarkAll();
        }

        /// <summary>FUN_10078507 0x10078507: a PCX straight to the front buffer, palette included (a full-screen picture).</summary>
        public void ShowPicture(ShellPcx pcx)
        {
            Buffer.BlockCopy(pcx.Palette6, 0, Palette6, 0, 768);
            CopyPcx(pcx, Front);
            MarkAll();
            PaletteDirty = true;
        }

        static void CopyPcx(ShellPcx pcx, byte[] target)
        {
            int w = Math.Min(Width, pcx.Width), h = Math.Min(Height, pcx.Height);
            for (int y = 0; y < h; y++) Buffer.BlockCopy(pcx.Pixels, y * pcx.Width, target, y * Width, w);
        }

        /// <summary>FUN_100784b1 / FUN_100784dc: whole-buffer copies front to background and back.</summary>
        public void FrontToBack() => Buffer.BlockCopy(Front, 0, Back, 0, Front.Length);
        public void BackToFront() { Buffer.BlockCopy(Back, 0, Front, 0, Front.Length); MarkAll(); }

        /// <summary>FUN_1007840f 0x1007840f: restore x, y, w, h of the front buffer from the background.</summary>
        public void Restore(int x, int y, int w, int h)
        {
            Blit(Back, Width, x, y, w, h, Front, x, y, RestoreKey);
            Mark(x, y, w, h);
        }

        /// <summary>FUN_10078179 / FUN_1007821c (onlyDirty): an opaque w x h buffer at x, y (Smacker frames).</summary>
        public void DrawBuffer(byte[] pixels, int x, int y, int w, int h, bool onlyDirty = false)
        {
            if (onlyDirty && !Touches(x, y, w, h)) return;
            Blit(pixels, w, 0, 0, w, h, Front, x, y, -1);
            Mark(x, y, w, h);
        }

        /// <summary>FUN_1007e92f: copy a rectangle with clipping; key >= 0 skips that colour.</summary>
        static void Blit(byte[] src, int srcPitch, int sx, int sy, int w, int h, byte[] dst, int dx, int dy, int key)
        {
            if (dx < 0) { sx -= dx; w += dx; dx = 0; }
            if (dy < 0) { sy -= dy; h += dy; dy = 0; }
            if (sx < 0) { dx -= sx; w += sx; sx = 0; }
            if (sy < 0) { dy -= sy; h += sy; sy = 0; }
            if (dx + w > Width) w = Width - dx;
            if (dy + h > Height) h = Height - dy;
            int srcRows = src.Length / srcPitch;
            if (sy + h > srcRows) h = srcRows - sy;
            if (sx + w > srcPitch) w = srcPitch - sx;
            if (w <= 0 || h <= 0) return;
            for (int r = 0; r < h; r++)
            {
                int s = (sy + r) * srcPitch + sx, d = (dy + r) * Width + dx;
                if (key < 0) Buffer.BlockCopy(src, s, dst, d, w);
                else
                    for (int c = 0; c < w; c++)
                    {
                        byte v = src[s + c];
                        if (v != key) dst[d + c] = v;
                    }
            }
        }

        /// <summary>
        /// FUN_100785da / FUN_10078643 (onlyDirty) via FUN_1007c9a0 0x1007c9a0: frame of a shell SHP at x + bounds x0, y + bounds y0
        /// (the RLE leaves skipped pixels untouched). The dirty mark covers x, y, w, h of the animation.
        /// </summary>
        public void DrawShape(Shape frame, int x, int y, int w, int h, bool onlyDirty = false)
        {
            if (onlyDirty && !Touches(x, y, w, h)) return;
            int ox = x + frame.BoundsX0, oy = y + frame.BoundsY0;
            for (int r = 0; r < frame.Height; r++)
            {
                int yy = oy + r;
                if (yy < 0 || yy >= Height) continue;
                for (int c = 0; c < frame.Width; c++)
                {
                    int v = frame.Pixels[r * frame.Width + c];
                    int xx = ox + c;
                    if (v >= 0 && xx >= 0 && xx < Width) Front[yy * Width + xx] = (byte)v;
                }
            }
            Mark(x, y, w, h);
        }

        /// <summary>FUN_100787d3 0x100787d3 via FUN_100804f3: one character; remap[pixel] = 0xff leaves the pixel. Returns its width.</summary>
        public int DrawChar(ShellFont font, int x, int y, char ch, byte[] remap)
        {
            int w = font.Width(ch);
            if (InDrawListPass && !Touches(x, y, w, font.Height)) return w;
            remap ??= DefaultRemap;
            for (int r = 0; r < font.Height; r++)
            {
                int yy = y + r;
                if (yy < 0 || yy >= Height) continue;
                for (int c = 0; c < w; c++)
                {
                    int xx = x + c;
                    if (xx < 0 || xx >= Width) continue;
                    byte v = remap[font.Pixel(ch, c, r)];
                    if (v != 0xff) Front[yy * Width + xx] = v;
                }
            }
            Mark(x, y, w, font.Height);
            return w;
        }

        /// <summary>FUN_100786cb 0x100786cb: a string from x, y; returns its width.</summary>
        public int DrawString(ShellFont font, int x, int y, string s, byte[] remap)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            int total = font.Width(s);
            if (total == 0 || (InDrawListPass && !Touches(x, y, total, font.Height))) return total;
            bool saved = InDrawListPass;
            InDrawListPass = false;
            int px = x;
            foreach (char c in s) px += DrawChar(font, px, y, c, remap);
            InDrawListPass = saved;
            return total;
        }

        /// <summary>FUN_1007810a 0x1007810a via FUN_1007be65: fill the inclusive rectangle with a colour.</summary>
        public void Fill(int x0, int y0, int x1, int y1, byte color)
        {
            for (int y = Math.Max(0, y0); y <= Math.Min(Height - 1, y1); y++)
                for (int x = Math.Max(0, x0); x <= Math.Min(Width - 1, x1); x++)
                    Front[y * Width + x] = color;
            MarkInclusive(x0, y0, x1, y1);
        }

        /// <summary>DAT_100fb0b8 (FUN_1007738f): identity except colour 0, which is transparent.</summary>
        public static readonly byte[] DefaultRemap = MakeRemap(-1);

        /// <summary>A remap table like the shell builds them: identity, 0 transparent, and colour 1 replaced (when color >= 0).</summary>
        public static byte[] MakeRemap(int color1)
        {
            var t = new byte[256];
            t[0] = 0xff;
            for (int i = 1; i < 256; i++) t[i] = (byte)i;
            if (color1 >= 0) t[1] = (byte)color1;
            return t;
        }

        /// <summary>FUN_100788f4 0x100788f4: redraw one draw list (0 = before the animations, 1 = after).</summary>
        public void DrawList(int which)
        {
            InDrawListPass = true;
            foreach (var t in (which == 0 ? DrawBefore : DrawAfter).ToArray()) t.Draw();
            InDrawListPass = false;
        }
    }
}
