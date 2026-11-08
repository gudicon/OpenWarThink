using System;
using UnityEngine;

namespace MW2.Formats
{
    /// <summary>
    /// CEL resource, two variants:
    ///   raw  : u16 width, u16 height, width*height indices into the world palette;
    ///   1.22 : "1.22", u16 kind, u16 width, u16 height, u16 colorCount, colorCount * (B,G,R,transparency), width*height indices
    ///          (layout from mercsw.dll 0x1000275a).
    /// Rows are stored top-down.
    /// </summary>
    public sealed class Cel
    {
        public int Width;
        public int Height;
        public bool HasOwnColors;
        public int Kind = -1;
        public byte[] Indices;
        public Color32[] Colors;    // only for 1.22; decoded to Unity RGBA

        /// <summary>Whether any pixel uses a partially transparent colour from the CEL palette.</summary>
        public bool HasTransparency
        {
            get
            {
                if (!HasOwnColors) return false;
                foreach (byte index in Indices)
                    if (index >= Colors.Length || Colors[index].a < 255) return true;
                return false;
            }
        }

        public static Cel Decode(byte[] data)
        {
            var cel = new Cel();
            int pixels;
            if (data.Length >= 12 && data[0] == '1' && data[1] == '.' && data[2] == '2' && data[3] == '2')
            {
                cel.HasOwnColors = true;
                cel.Kind = BitConverter.ToUInt16(data, 4);
                cel.Width = BitConverter.ToUInt16(data, 6);
                cel.Height = BitConverter.ToUInt16(data, 8);
                int n = BitConverter.ToUInt16(data, 10);
                cel.Colors = new Color32[n];
                for (int i = 0; i < n; i++)
                {
                    int o = 12 + i * 4;
                    // zglide.dll hands the entry to Glide as a 0xAARRGGBB palette dword, so the bytes are B, G, R
                    // (A9RLAS "red laser" = 26,26,233). For kinds > 0 it builds AP_88 texels whose alpha is
                    // ~A = 255 - A (FUN_10001090), so the fourth byte is transparency. Kind 0 is P_8 and opaque.
                    byte opacity = cel.Kind == 0 ? (byte)255 : (byte)(255 - data[o + 3]);
                    cel.Colors[i] = new Color32(data[o + 2], data[o + 1], data[o], opacity);
                }
                pixels = 12 + n * 4;
            }
            else
            {
                cel.Width = BitConverter.ToUInt16(data, 0);
                cel.Height = BitConverter.ToUInt16(data, 2);
                pixels = 4;
            }
            cel.Indices = new byte[cel.Width * cel.Height];
            Buffer.BlockCopy(data, pixels, cel.Indices, 0, cel.Indices.Length);
            return cel;
        }

        /// <summary>
        /// The indices of a raw CEL (the world palette) as an R8 texture for the palette shaders (Mw2Palette.cginc): the colour
        /// comes from the live palette of the engine, so the palette fades of Palette_Fade 0x1006b46f reach the textures.
        /// Point filtered, the shader mixes the four looked-up colours itself (Glide filters after the palette).
        /// </summary>
        public Texture2D ToIndexTexture(string name)
        {
            var tex = new Texture2D(Width, Height, TextureFormat.R8, false, true)
            {
                name = name,
                filterMode = FilterMode.Point
            };
            var rows = new byte[Width * Height];
            for (int y = 0; y < Height; y++)
                Buffer.BlockCopy(Indices, y * Width, rows, (Height - 1 - y) * Width, Width);     // Unity textures are bottom-up
            tex.SetPixelData(rows, 0);
            tex.Apply();
            return tex;
        }

        /// <param name="palette">World palette, used by raw CELs.</param>
        /// <param name="transparentIndex">Palette index treated as transparent for raw CELs, or -1.</param>
        public Texture2D ToTexture(Color32[] palette, string name, int transparentIndex = 0)
        {
            var tex = new Texture2D(Width, Height, TextureFormat.RGBA32, false)
            {
                name = name,
                // zglide.dll sets grTexFilterMode(tmu, BILINEAR, BILINEAR) for every TMU at start-up.
                filterMode = FilterMode.Bilinear
            };
            var px = new Color32[Width * Height];
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    byte i = Indices[y * Width + x];
                    Color32 c;
                    if (HasOwnColors)
                    {
                        c = i < Colors.Length ? Colors[i] : new Color32(0, 0, 0, 0);
                        // Kind > 0 uses AP_88: zglide disables chromakey for format 0xE.
                        // Index zero keeps its palette alpha, just like every other index.
                    }
                    else
                        c = i == transparentIndex ? new Color32(0, 0, 0, 0) : palette[i];
                    px[(Height - 1 - y) * Width + x] = c;     // Unity textures are bottom-up
                }
            }
            tex.SetPixels32(px);
            tex.Apply();
            return tex;
        }
    }
}
