namespace MW2.Formats
{
    /// <summary>Jet-fuel gauge: layout FUN_10029037, draw FUN_10029c9c,
    /// filter FUN_100256b5 at DAT_10090688 (shift 1). Coordinates are local
    /// 640x480 surface pixels with inclusive frame edges.</summary>
    public static class HudJetBar
    {
        public struct Box
        {
            public int Left, Top, Right, Bottom, FillX, FillBottom, Columns, MaxHeight;
        }
        static int Scale(int span, int fraction)
        {
            long product = (long)span * fraction;
            return (int)(product >> 16) + ((product & 0x8000) != 0 ? 1 : 0);
        }
        /// <summary>FUN_10029037 uses the rectangle spans (width-1,height-1), then
        /// the gauge's inclusive pixel dimensions (+0xb8/+0xba) for frame placement.</summary>
        public static Box Layout(int width, int height)
        {
            int w = Scale(width - 1, 0x1e7a), h = Scale(height - 1, 0x8dc9);
            int top = height - (h + h / 2) - 3, bottom = height - 1;
            return new Box { Left = 0, Top = top, Right = w, Bottom = bottom,
                FillX = 1, FillBottom = height - 2, Columns = w - 1, MaxHeight = bottom - top - 2 };
        }
        /// <summary>FUN_10029c9c: a negative fuel field suppresses the label/bar.
        /// Smoothing advances once per displayed altitude window, even at empty fuel.</summary>
        public static int FillHeight(Box box, int fuel, ref int filtered)
        {
            if (fuel < 0) return -1;
            filtered += (fuel - filtered) >> 1;
            int fixedFuel = unchecked(filtered << 16);
            int fixedHeight = (int)((double)(box.MaxHeight + 1) * fixedFuel / 1810f);
            int pixels = fixedHeight / 65536;
            return pixels > box.MaxHeight ? box.MaxHeight : pixels;
        }
    }
}
