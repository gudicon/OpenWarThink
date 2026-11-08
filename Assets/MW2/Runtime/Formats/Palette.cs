using UnityEngine;

namespace MW2.Formats
{
    /// <summary>PAL resource: 256 RGB triplets with 6-bit VGA components.</summary>
    public static class Palette
    {
        public static Color32[] Decode(byte[] data)
        {
            var colors = new Color32[256];
            for (int i = 0; i < 256; i++)
                colors[i] = new Color32(Expand(data[i * 3]), Expand(data[i * 3 + 1]), Expand(data[i * 3 + 2]), 255);
            return colors;
        }

        public static byte Expand(byte v) => (byte)Mathf.Min(255, (v << 2) | (v >> 4));

        /// <summary>256x1 point-filtered lookup texture, for palette shaders.</summary>
        public static Texture2D ToTexture(Color32[] colors, string name)
        {
            var tex = new Texture2D(256, 1, TextureFormat.RGBA32, false)
            {
                name = name,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };
            tex.SetPixels32(colors);
            tex.Apply();
            return tex;
        }
    }
}
