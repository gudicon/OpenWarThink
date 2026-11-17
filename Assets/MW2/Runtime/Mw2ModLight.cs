using System.Collections.Generic;
using UnityEngine;

namespace MW2
{
    /// <summary>
    /// A soft round light of a mod weapon effect (WeaponLook "light"), passed to the MW2 lit shaders as their extra lights
    /// (Mw2ExtraLight.cginc).
    /// Port addition: the original renderer has only its cube point lights (Mw2PointLight); these exist only while a mod
    /// effect asks for one. With <see cref="Life"/> above 0 the light fades out over that many seconds and removes itself.
    /// At most 16 reach the shaders, the ones nearest the camera.
    /// </summary>
    public class Mw2ModLight : MonoBehaviour
    {
        public Color Color = Color.white;
        public float Intensity = 1f;
        public float Range = 10f;
        public float Life;

        const int Max = 16;
        static readonly List<Mw2ModLight> s_lights = new List<Mw2ModLight>();
        static readonly Vector4[] s_positions = new Vector4[Max];
        static readonly Vector4[] s_colors = new Vector4[Max];
        static int s_frame = -1;
        static bool s_pushedAny = true;
        float _age;

        void OnEnable() { if (!s_lights.Contains(this)) s_lights.Add(this); }

        void OnDisable()
        {
            s_lights.Remove(this);
            if (s_lights.Count == 0) Push();
        }

        void Update()
        {
            if (Life <= 0f) return;
            _age += Time.deltaTime;
            if (_age >= Life) Destroy(gameObject);
        }

        void LateUpdate()
        {
            if (s_frame == Time.frameCount) return;
            s_frame = Time.frameCount;
            Push();
        }

        float Strength => Life > 0f ? Intensity * Mathf.Clamp01(1f - _age / Life) : Intensity;

        static void Push()
        {
            if (s_lights.Count == 0 && !s_pushedAny) return;
            var cam = Camera.main;
            var eye = cam != null ? cam.transform.position : Vector3.zero;
            if (s_lights.Count > Max)
                s_lights.Sort((a, b) => (a.transform.position - eye).sqrMagnitude.CompareTo((b.transform.position - eye).sqrMagnitude));
            int n = 0;
            foreach (var l in s_lights)
            {
                if (l == null || n >= Max) continue;
                var p = l.transform.position;
                s_positions[n] = new Vector4(p.x, p.y, p.z, Mathf.Max(l.Range, 0.01f));
                float k = l.Strength;
                s_colors[n] = new Vector4(l.Color.r * k, l.Color.g * k, l.Color.b * k, 1f);
                n++;
            }
            Shader.SetGlobalVectorArray("_Mw2ExtraLightPos", s_positions);
            Shader.SetGlobalVectorArray("_Mw2ExtraLightColor", s_colors);
            Shader.SetGlobalFloat("_Mw2ExtraLightCount", n);
            s_pushedAny = n > 0;
        }
    }
}
