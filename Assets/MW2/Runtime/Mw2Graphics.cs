using System;
using System.Collections.Generic;
using System.IO;
using MW2.Formats;
using UnityEngine;

namespace MW2
{
    /// <summary>
    /// Applies the picture settings of the active mods (GraphicsQuality, file graphics/quality.json): at start, after the
    /// mods screen saved a choice and when the file changes while the game runs (Mw2WeaponLooks polls). Without the file
    /// the picture is the original's: the quality level's anti-aliasing, one bilinear texture sample, the fog of SKYGND.PAR
    /// and no colour pass.
    /// Port addition with no counterpart in the original program.
    /// </summary>
    public static class Mw2Graphics
    {
        /// <summary>The settings in force (the original picture when no active mod has the file).</summary>
        public static GraphicsQuality Current { get; private set; } = new GraphicsQuality();

        /// <summary>True while a graphics mod's lighting wants the point lights of shots and explosions even with Multiple
        /// Lights off ("lights": "always"); they are then made for that lighting only (Mw2PointLight.ModernOnly).</summary>
        public static bool DynamicLights => Current.Lighting && Current.LightsAlways && Current.LightsIntensity > 0f;

        /// <summary>Factor on the fog distance (Mw2Atmosphere).</summary>
        public static float FogScale => Current.FogDistance;

        static readonly int TexSamplesId = Shader.PropertyToID("_Mw2TexSamples");
        static readonly int ModernId = Shader.PropertyToID("_Mw2Modern");
        static readonly int ModernParamsId = Shader.PropertyToID("_Mw2ModernParams");
        static readonly int ModernFadeId = Shader.PropertyToID("_Mw2ModernFade");
        static readonly int ModernUnitsId = Shader.PropertyToID("_Mw2ModernUnits");
        static readonly int ModernStructuresId = Shader.PropertyToID("_Mw2ModernStructures");
        static readonly int ModernGroundId = Shader.PropertyToID("_Mw2ModernGround");
        static readonly int ModernCockpitId = Shader.PropertyToID("_Mw2ModernCockpit");
        static readonly int ModernLightsId = Shader.PropertyToID("_Mw2ModernLights");
        static readonly int EmissiveId = Shader.PropertyToID("_Mw2Emissive");
        static Camera s_hdrCamera;
        static bool s_hdrSaved;
        static readonly int HideUnitShadowsId = Shader.PropertyToID("_Mw2ModernHideUnitShadows");
        static int s_levelAntiAliasing = -1;
        static readonly int ClassMapId = Shader.PropertyToID("_Mw2ModernClassMap");
        static readonly int ClassesId = Shader.PropertyToID("_Mw2ModernClasses");
        static readonly int RowClassId = Shader.PropertyToID("_Mw2ModernRowClass");
        static Dictionary<int, string> s_missionTextures;
        static Texture2D s_classMap;
        static readonly Vector4[] s_classes = new Vector4[8];
        static readonly Color32[] s_classPixels = new Color32[256];
        static readonly int GlassPart = System.Array.IndexOf(GraphicsQuality.PartNames, "glass");

        /// <summary>The mission's texture keys and the CEL names they show (Mw2TextureBank.KeyNames), for the surface
        /// classes of unit parts ("unitParts").</summary>
        public static void SetMissionTextures(Dictionary<int, string> names)
        {
            s_missionTextures = names;
            SetParts(Current);
        }

        /// <summary>
        /// "unitParts": the class of every texture key 0x100 + x of the mission (a 256 x 1 map the shaders read by the low
        /// byte of a face's material word, Mw2Surface.cginc; G = 255 for the windshield key 0x18c) and the surface of each class. The material word is the
        /// one after Material_ApplyPaint 0x100745eb, so the team's plates (PLATEA..H) are found by their names too.
        /// </summary>
        static void SetParts(GraphicsQuality q)
        {
            if (s_classMap == null)
                s_classMap = new Texture2D(256, 1, TextureFormat.RGBA32, false, true)
                {
                    name = "MW2 unit part classes", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp,
                    hideFlags = HideFlags.HideAndDontSave
                };
            for (int i = 0; i < 256; i++)
            {
                string name = null;
                s_missionTextures?.TryGetValue(0x100 + i, out name);
                // The windshield key 0x18c is glass whatever CEL the mission shows on it (*CLOU in most missions,
                // SPA1OBJ5 in SPA1/SPA2, none in MIS*: then Mw2TextureBank.GetUnit gives MCC1CLOU). G marks it for
                // graphics/detail.json "opaqueGlass", with or without "unitParts".
                bool glass = 0x100 + i == Mw2TextureBank.GlassKey;
                int part = q.PartOf(name);
                if (glass && q.Parts[GlassPart].Enabled) part = GlassPart + 1;
                s_classPixels[i] = new Color32((byte)part, glass ? (byte)255 : (byte)0, 0, 255);
            }
            s_classMap.SetPixels32(s_classPixels);
            s_classMap.Apply(false);
            Shader.SetGlobalTexture(ClassMapId, s_classMap);
            s_classes[0] = Vector4.zero;
            for (int k = 0; k < q.Parts.Length && k + 1 < s_classes.Length; k++)
            {
                var part = q.Parts[k];
                s_classes[k + 1] = part.Enabled
                    ? new Vector4(part.Surface.Metallic, part.Surface.Roughness, part.Surface.Albedo, 1f) : Vector4.zero;
            }
            Shader.SetGlobalVectorArray(ClassesId, s_classes);
            Shader.SetGlobalFloat(RowClassId, q.RowsPart);
        }

        public static void Apply()
        {
            string path = null;
            try { path = Mw2Mods.Resolve(GraphicsQuality.File); }
            catch (Exception e) { Debug.LogWarning("MW2 mods: " + e.Message); }
            var q = new GraphicsQuality();
            if (path != null)
            {
                string shown = Mw2WeaponLooks.Shown(path);
                try
                {
                    q = GraphicsQuality.Parse(File.ReadAllText(path), path);
                    foreach (var w in q.Warnings) Mw2WeaponLooks.Say(shown + ": " + w, true);
                }
                catch (Exception e) when (e is MiniJson.Error || e is IOException || e is UnauthorizedAccessException)
                {
                    Mw2WeaponLooks.Say(shown + ": " + e.Message + " (the original picture is used)", true);
                    q = new GraphicsQuality();
                }
            }
            bool was = !Current.IsOriginal;
            Current = q;

            if (s_levelAntiAliasing < 0) s_levelAntiAliasing = QualitySettings.antiAliasing;
            QualitySettings.antiAliasing = q.AntiAliasing >= 0 ? q.AntiAliasing : s_levelAntiAliasing;
            Shader.SetGlobalFloat(TexSamplesId, q.TextureSamples);
            SetSurface(ModernUnitsId, q.Units);
            SetSurface(ModernStructuresId, q.Structures);
            SetSurface(ModernGroundId, q.Ground);
            // "cockpit": w = 1 lights the shell (MW2/TexturedLit, TexturedAlphaLit, VertexColorLit); 0 keeps the original.
            Shader.SetGlobalVector(ModernCockpitId, q.Cockpit is GraphicsQuality.Surface c
                ? new Vector4(c.Metallic, c.Roughness, c.Albedo, 1 + q.CockpitLight) : Vector4.zero);   // w - 1 = "light"
            SetParts(q);
            Shader.SetGlobalVector(ModernParamsId, new Vector4(q.Sun, q.Sky, q.Bounce, q.Reflections));
            Shader.SetGlobalVector(ModernLightsId, new Vector4(q.LightsIntensity, q.LightsRange, q.Fill, 0));
            Shader.SetGlobalFloat(EmissiveId, q.Emissive);

            if (!q.IsOriginal)
            {
                Debug.Log("MW2 mods: picture from " + Mw2WeaponLooks.Shown(path));
                Mw2WeaponLooks.Say("Picture: " + Mw2WeaponLooks.Shown(path), false);
            }
            else if (was) Mw2WeaponLooks.Say("Picture: as in the original", false);
            Tick();
        }

        /// <summary>Every frame: keeps the colour pass on the camera of a mission, and only there (the shell draws with the
        /// same main camera).</summary>
        public static void Tick()
        {
            var cam = Camera.main;
            var atmosphere = Mw2Atmosphere.Instance;
            bool mission = atmosphere != null && atmosphere.isActiveAndEnabled;
            Lighting(mission ? atmosphere : null, cam);
            Hdr(mission && Current.Hdr ? cam : null);
            if (cam == null) return;
            bool want = Current.NeedsPost && mission;
            if (cam.TryGetComponent<Mw2GraphicsPost>(out var post))
            {
                if (post.enabled != want) post.enabled = want;
            }
            else if (want) cam.gameObject.AddComponent<Mw2GraphicsPost>();
        }

        /// <summary>"hdr": the mission camera draws in high dynamic range; another camera, or none, gets its own setting back.</summary>
        static void Hdr(Camera cam)
        {
            if (s_hdrCamera != null && s_hdrCamera != cam) s_hdrCamera.allowHDR = s_hdrSaved;
            if (cam == null) { s_hdrCamera = null; return; }
            if (s_hdrCamera != cam)
            {
                s_hdrCamera = cam;
                s_hdrSaved = cam.allowHDR;
            }
            cam.allowHDR = true;
        }

        static void SetSurface(int id, GraphicsQuality.Surface s) => Shader.SetGlobalVector(id, new Vector4(s.Metallic, s.Roughness, s.Albedo, 0));

        /// <summary>
        /// The lighting of GraphicsQuality (Mw2Modern.cginc), every frame: the shaders' switch and the fade level of the live
        /// palette. The sun's shadow map and the depth for the ambient occlusion are drawn by Mw2GraphicsDepth; the
        /// scene's own Unity light is not touched.
        /// </summary>
        static void Lighting(Mw2Atmosphere atmosphere, Camera cam)
        {
            var q = Current;
            bool on = q.Lighting && atmosphere != null;
            Shader.SetGlobalFloat(ModernId, on ? 1f : 0f);
            var fade = Mw2PaletteFade.Instance;
            Shader.SetGlobalVector(ModernFadeId, new Vector4(fade != null ? fade.FadeLevel : 1f, 0, 0, 0));
            bool shadows = on && q.Shadows > 0f && Mw2GraphicsDepth.SunVector(atmosphere, out _);
            Shader.SetGlobalFloat(HideUnitShadowsId, shadows ? 1f : 0f);
            if (on) Mw2GraphicsDepth.Ensure();
        }
    }

    /// <summary>The colour pass of GraphicsQuality on the finished 3D frame (shader MW2/ModPost): bloom, sharpen, brightness,
    /// contrast, saturation, gamma and vignette. The HUD is drawn by OnGUI after it and is not touched.
    /// Port addition with no counterpart in the original program.</summary>
    [RequireComponent(typeof(Camera))]
    public sealed class Mw2GraphicsPost : MonoBehaviour
    {
        const int PassPrefilter = 0, PassDown = 1, PassUp = 2, PassFinal = 3;
        static readonly int BloomTexId = Shader.PropertyToID("_BloomTex");
        static readonly int BloomId = Shader.PropertyToID("_Bloom");
        static readonly int ColorId = Shader.PropertyToID("_Grade");
        static readonly int ExtraId = Shader.PropertyToID("_Extra");
        static readonly int SsrId = Shader.PropertyToID("_Ssr");
        static readonly int HdrId = Shader.PropertyToID("_Hdr");
        static readonly int RaysId = Shader.PropertyToID("_Rays");
        static readonly int RaysColorId = Shader.PropertyToID("_RaysColor");
        static readonly int ContactId = Shader.PropertyToID("_Contact");
        static readonly int ContactLengthId = Shader.PropertyToID("_ContactLength");
        Material _material;
        readonly RenderTexture[] _chain = new RenderTexture[9];

        void OnRenderImage(RenderTexture src, RenderTexture dst)
        {
            var q = Mw2Graphics.Current;
            if (_material == null)
            {
                var shader = Shader.Find("MW2/ModPost");
                if (shader == null || !shader.isSupported) { Graphics.Blit(src, dst); return; }
                _material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            }
            float knee = Mathf.Max(q.BloomThreshold * q.BloomSoft, 1e-4f);
            _material.SetVector(BloomId, new Vector4(q.BloomThreshold, knee, q.BloomIntensity, 0));
            _material.SetVector(ColorId, new Vector4(q.Brightness, q.Contrast, q.Saturation, 1f / q.Gamma));
            bool view = Shader.GetGlobalVector("_Mw2ViewParams").w > 0.5f;
            bool occlusion = q.NeedsOcclusion && view;
            _material.SetVector(SsrId, new Vector4(q.NeedsSsr && view ? q.SsrIntensity : 0f, q.SsrDistance, q.SsrThickness, 0));
            _material.SetVector(HdrId, new Vector4(q.Hdr ? q.Exposure : 1f, q.Hdr ? q.Tonemapping : 0, 0, 0));
            _material.SetVector(RaysId, Vector4.zero);
            _material.SetVector(ContactId, Vector4.zero);
            if (q.NeedsContact && view && Mw2Atmosphere.Instance != null && Mw2GraphicsDepth.SunVector(Mw2Atmosphere.Instance, out var sun))
            {
                // Towards the sun in the view space of the post pass (x right, y up, z ahead).
                Vector3 l = GetComponent<Camera>().worldToCameraMatrix.MultiplyVector(-sun).normalized;
                _material.SetVector(ContactId, new Vector4(l.x, l.y, -l.z, q.ContactShadows));
                _material.SetVector(ContactLengthId, new Vector4(q.ContactLength, 0, 0, 0));
            }
            if (q.NeedsRays && view && Mw2Atmosphere.Instance != null && Mw2GraphicsDepth.SunVector(Mw2Atmosphere.Instance, out var dir))
            {
                // The sun on the screen: far along the way back to it. Behind the camera there are no rays; they fade
                // out as the sun leaves the view.
                var cam = GetComponent<Camera>();
                Vector3 sunAt = cam.transform.position - dir * (cam.farClipPlane * 0.5f);
                Vector3 vp = cam.WorldToViewportPoint(sunAt);
                float facing = Vector3.Dot(cam.transform.forward, -dir);
                if (vp.z > 0f && facing > 0f)
                {
                    float off = Mathf.Max(Mathf.Abs(vp.x - 0.5f), Mathf.Abs(vp.y - 0.5f));
                    float shown = Mathf.Clamp01((1.2f - off) / 0.7f) * Mathf.Clamp01(facing * 3f);
                    var a = Mw2Atmosphere.Instance.LightA.Color;
                    _material.SetVector(RaysId, new Vector4(vp.x, vp.y, q.RaysIntensity * shown, q.RaysDecay));
                    _material.SetVector(RaysColorId, new Vector4(a.r / 255f * q.Sun, a.g / 255f * q.Sun, a.b / 255f * q.Sun, q.RaysLength));
                }
            }
            _material.SetVector(ExtraId, new Vector4(q.Sharpen, q.Vignette, occlusion ? q.Occlusion : 0f, q.OcclusionRadius));

            int steps = 0;
            if (q.BloomIntensity > 0f)
            {
                int w = src.width / 2, h = src.height / 2;
                var format = src.format;
                for (; steps < q.BloomRadius && w >= 2 && h >= 2; steps++, w /= 2, h /= 2)
                {
                    _chain[steps] = RenderTexture.GetTemporary(w, h, 0, format);
                    _chain[steps].filterMode = FilterMode.Bilinear;
                    Graphics.Blit(steps == 0 ? src : _chain[steps - 1], _chain[steps], _material, steps == 0 ? PassPrefilter : PassDown);
                }
                for (int i = steps - 1; i > 0; i--) Graphics.Blit(_chain[i], _chain[i - 1], _material, PassUp);
            }
            _material.SetTexture(BloomTexId, steps > 0 ? _chain[0] : (Texture)Texture2D.blackTexture);
            Graphics.Blit(src, dst, _material, PassFinal);
            for (int i = 0; i < steps; i++) { RenderTexture.ReleaseTemporary(_chain[i]); _chain[i] = null; }
        }

        void OnDestroy()
        {
            if (_material != null) Destroy(_material);
        }
    }
}
