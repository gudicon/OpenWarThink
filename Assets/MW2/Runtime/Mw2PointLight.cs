using System.Collections.Generic;
using UnityEngine;

namespace MW2
{
    /// <summary>
    /// A point light of the original light pool (0x102492e0, 256 records of 0x54; active list DAT_100ad2b8), here from a
    /// MLIT chunk. Shading (point lights on, DAT_10097cb8 = 1 in the DLL): a polygon is lit when one of its vertices lies in
    /// the cube light position +- radius (+0x30, FUN_1002ea96 0x1002ea96).
    /// - Palette-row faces (Material_LightShade 0x1002f543), while the level is below 0x80: level += Light_SunDot 0x10056cd3
    ///   in position mode = trunc(128 * n . (light - vertex) / |light - vertex|) clamped to +-127 (127 at the light itself),
    ///   when positive; stops above 0x7f.
    /// - Textured faces (Light_VertexColors 0x1002ec0a): colour (+0x44..+0x4c) * that cosine * 255 added per vertex.
    /// Approximations: the cube test and the cosine are per pixel (the original tests the polygon's vertices and takes the
    /// cosine at its first vertex); Light_VertexColors carries its sum from vertex to vertex, here each pixel is independent.
    /// At most 16 lights reach the shaders (the pool holds 256).
    /// </summary>
    public class Mw2PointLight : MonoBehaviour
    {
        public float Radius;                // metres
        public Color Color = new Color(0.6f, 0.6f, 0.6f);

        const int Max = 16;
        static readonly List<Mw2PointLight> s_lights = new List<Mw2PointLight>();
        static readonly Vector4[] s_positions = new Vector4[Max];
        static readonly Vector4[] s_colors = new Vector4[Max];
        static int s_frame = -1;

        void OnEnable() { if (!s_lights.Contains(this)) s_lights.Add(this); }
        void OnDisable() => s_lights.Remove(this);

        void LateUpdate()
        {
            if (s_frame == Time.frameCount) return;
            s_frame = Time.frameCount;
            Push();
        }

        void OnDestroy()
        {
            s_lights.Remove(this);
            Push();
        }

        static void Push()
        {
            int n = 0;
            // Light_VertexColors 0x1002ec0a / Material_LightShade 0x1002f543 add point lights only with Multiple Lights on
            // (DAT_10097cb8, main menu).
            foreach (var l in s_lights)
            {
                if (l == null || n >= Max || !Mw2Options.MultipleLights) continue;
                var p = l.transform.position;
                s_positions[n] = new Vector4(p.x, p.y, p.z, l.Radius);
                s_colors[n] = l.Color;
                n++;
            }
            Shader.SetGlobalVectorArray("_Mw2PointPos", s_positions);
            Shader.SetGlobalVectorArray("_Mw2PointColor", s_colors);
            Shader.SetGlobalFloat("_Mw2PointCount", n);
        }
    }
}
