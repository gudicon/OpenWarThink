namespace MW2.Formats
{
    /// <summary>Material_Shade 1001afb0 mode 1 followed by raster dispatch 1005581d.</summary>
    public static class ImageEnhancement
    {
        public static int Shade(int flags, int damage, int material)
        {
            int type = material & 0xf000;
            if (type == 0x8000 || type == 0x9000) return material;
            if ((flags & 0x200) != 0) return 7;
            if ((flags & 0x400) != 0) return type != 0 && type != 0x1000 ? material : 11;
            if ((flags & 0x100) == 0) return (flags & 0x70) == 0 && material != 0 ? 8 : 0;
            return damage == 0 ? 7 : damage < 12 ? 3 : 11;
        }
        // -1 original raster, 0 skipped, positive black fill + boundary in this palette colour.
        public static int Boundary(int shaded)
        {
            if (shaded == 0 || (shaded & 0xf000) == 0x9000) return 0;
            int type = shaded & 0xf000;
            return type == 0x3000 || type == 0x5000 || type == 0x6000 || type == 0x7000 || type == 0x8000 ? -1 : shaded & 255;
        }
    }
}
