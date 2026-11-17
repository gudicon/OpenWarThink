using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace OpenWarThink.ModKit
{
    /// <summary>
    /// Makes three example prefabs in a project folder, ready to change in the Editor:
    /// MyBeam (a shot prefab for lasers: line renderers from the muzzle to what it hits, a burning spot, a glowing lens),
    /// MyBolt (a shot prefab that flies with the game's shot: a glowing ball with a trail) and
    /// MyHit (an impact prefab: sparks, smoke, a flash of light). Pictures and materials go next to them.
    /// Only built-in shaders are used (Legacy Shaders/Particles), so the bundle looks the same in the game.
    /// </summary>
    public static class ExamplePrefabs
    {
        public const string Beam = "MyBeam", Bolt = "MyBolt", Hit = "MyHit";

        static string s_folder;
        static Material s_glow, s_line, s_smoke;

        /// <summary>Creates the folder (if needed) and the three prefabs; existing ones with these names are replaced.</summary>
        public static void Create(string folder)
        {
            if (!folder.StartsWith("Assets/", StringComparison.Ordinal)) throw new ArgumentException("The folder must be inside Assets: " + folder);
            if (!AssetDatabase.IsValidFolder(folder))
            {
                string parent = Path.GetDirectoryName(folder).Replace('\\', '/');
                if (!AssetDatabase.IsValidFolder(parent)) throw new ArgumentException("No folder " + parent);
                AssetDatabase.CreateFolder(parent, Path.GetFileName(folder));
            }
            s_folder = folder;
            s_glow = Mat("Glow", "Legacy Shaders/Particles/Additive", Picture("glow", 64, 64, Dot));
            s_line = Mat("BeamLine", "Legacy Shaders/Particles/Additive", Picture("beam", 8, 64, Across));
            s_smoke = Mat("Smoke", "Legacy Shaders/Particles/Alpha Blended", Picture("smoke", 64, 64, Puff));
            Save(MakeBeam(new Color(0.2f, 1f, 0.3f)), Beam);
            Save(MakeBolt(new Color(0.3f, 0.7f, 1f)), Bolt);
            Save(MakeHit(), Hit);
            AssetDatabase.SaveAssets();
        }

        // ---- the prefabs ----

        /// <summary>
        /// Shot prefab for "reach": "instant" and "stretch": true. Its root is the far end (where the beam touches), +Z along
        /// the shot. The game sets the points of every LineRenderer from the muzzle to the far end; "Spot" burns there;
        /// the child named "Muzzle" is kept at the muzzle by the game.
        /// </summary>
        static GameObject MakeBeam(Color c)
        {
            var go = new GameObject(Beam);
            var white = Color.Lerp(c, Color.white, 0.75f);
            Line(go, 0.1f, Ramp((0f, white, 0.9f), (1f, white, 1f)));
            Line(Child(go, "Sheath"), 0.6f, Ramp((0f, c, 0.25f), (1f, c, 0.5f)));
            var spot = Child(go, "Spot", new Vector3(0f, 180f, 0f));
            Particles(spot, s_glow, true, 45f, 0, new Vector2(0.05f, 0.1f), Vector2.zero, new Vector2(1.8f, 3f), 0f, 0f,
                      Ramp((0f, Color.white, 1f), (1f, c, 0f)));
            Particles(Child(spot, "Sparks"), s_glow, true, 120f, 0, new Vector2(0.2f, 0.5f), new Vector2(4f, 10f), new Vector2(0.1f, 0.2f), 1f, 60f,
                      Ramp((0f, Color.white, 1f), (1f, c, 0f)), true);
            Light(spot, c, 8f, 1.5f);
            var muzzle = Child(go, "Muzzle");
            Particles(muzzle, s_glow, true, 50f, 0, new Vector2(0.04f, 0.08f), Vector2.zero, new Vector2(0.6f, 1f), 0f, 0f,
                      Ramp((0f, white, 1f), (1f, c, 0f)));
            return go;
        }

        /// <summary>Shot prefab for "reach": "shot": it rides on the game's shot, +Z along the flight.</summary>
        static GameObject MakeBolt(Color c)
        {
            var go = new GameObject(Bolt);
            Particles(go, s_glow, true, 60f, 0, new Vector2(0.05f, 0.1f), Vector2.zero, new Vector2(1.2f, 1.6f), 0f, 0f,
                      Ramp((0f, Color.white, 1f), (1f, c, 0f)));
            var trail = go.AddComponent<TrailRenderer>();
            trail.sharedMaterial = s_line;
            trail.time = 0.25f;
            trail.widthMultiplier = 0.5f;
            trail.colorGradient = Ramp((0f, Color.Lerp(c, Color.white, 0.5f), 1f), (1f, c, 0f));
            trail.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            Light(go, c, 10f, 1.2f);
            return go;
        }

        /// <summary>Impact prefab: at the hit, +Z back towards the shooter. One burst; it goes when its particles are gone.</summary>
        static GameObject MakeHit()
        {
            var go = new GameObject(Hit);
            var hot = new Color(1f, 0.9f, 0.6f);
            var ember = new Color(1f, 0.35f, 0.05f);
            Particles(go, s_glow, false, 0f, 40, new Vector2(0.4f, 1f), new Vector2(4f, 12f), new Vector2(0.1f, 0.25f), 1.2f, 70f,
                      Ramp((0f, hot, 1f), (0.4f, ember, 1f), (1f, ember, 0f)), true);
            var smoke = Particles(Child(go, "Smoke"), s_smoke, false, 0f, 8, new Vector2(1.5f, 3f), new Vector2(0.6f, 1.8f), new Vector2(1.5f, 2.3f),
                                  -0.06f, 50f, Ramp((0f, new Color(0.2f, 0.2f, 0.2f), 0.5f), (1f, new Color(0.3f, 0.3f, 0.3f), 0f)));
            var size = smoke.sizeOverLifetime;
            size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.EaseInOut(0f, 0.7f, 1f, 2.5f));
            Particles(Child(go, "Flash"), s_glow, false, 0f, 1, new Vector2(0.25f, 0.25f), Vector2.zero, new Vector2(4f, 4f), 0f, 0f,
                      Ramp((0f, hot, 1f), (1f, ember, 0f)));
            Light(go, ember, 12f, 2.4f);
            return go;
        }

        // ---- building blocks ----

        static float Dot(float u, float v)
        {
            float dx = u * 2f - 1f, dy = v * 2f - 1f, r = Mathf.Clamp01(1f - Mathf.Sqrt(dx * dx + dy * dy));
            return r * r;
        }

        static float Across(float u, float v)
        {
            float d = Mathf.Abs(v * 2f - 1f);
            return Mathf.Clamp01(Mathf.Pow(1f - d, 2.2f) + 0.6f * Mathf.Clamp01(1f - d * 4f));
        }

        static float Puff(float u, float v)
        {
            float dx = u * 2f - 1f, dy = v * 2f - 1f, r = Mathf.Sqrt(dx * dx + dy * dy);
            float n = Mathf.PerlinNoise(u * 5f, v * 5f) * 0.6f + Mathf.PerlinNoise(u * 11f + 3f, v * 11f) * 0.4f;
            return Mathf.Clamp01((1f - r) * 1.6f) * Mathf.Clamp01(n * 1.4f - 0.15f);
        }

        static Texture2D Picture(string name, int w, int h, Func<float, float, float> alpha)
        {
            var t = new Texture2D(w, h, TextureFormat.RGBA32, false);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    t.SetPixel(x, y, new Color(1f, 1f, 1f, alpha((x + 0.5f) / w, (y + 0.5f) / h)));
            string path = s_folder + "/" + name + ".png";
            File.WriteAllBytes(path, t.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(t);
            AssetDatabase.ImportAsset(path);
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.alphaIsTransparency = true;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.mipmapEnabled = false;
            importer.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        static Material Mat(string name, string shader, Texture2D tex)
        {
            string path = s_folder + "/" + name + ".mat";
            var m = new Material(Shader.Find(shader)) { mainTexture = tex };
            AssetDatabase.DeleteAsset(path);
            AssetDatabase.CreateAsset(m, path);
            return m;
        }

        static void Save(GameObject go, string name)
        {
            PrefabUtility.SaveAsPrefabAsset(go, s_folder + "/" + name + ".prefab");
            UnityEngine.Object.DestroyImmediate(go);
        }

        static Gradient Ramp(params (float t, Color c, float a)[] keys)
        {
            var g = new Gradient();
            var cs = new GradientColorKey[keys.Length];
            var al = new GradientAlphaKey[keys.Length];
            for (int i = 0; i < keys.Length; i++) { cs[i] = new GradientColorKey(keys[i].c, keys[i].t); al[i] = new GradientAlphaKey(keys[i].a, keys[i].t); }
            g.SetKeys(cs, al);
            return g;
        }

        static GameObject Child(GameObject parent, string name, Vector3 euler = default)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent.transform, false);
            go.transform.localEulerAngles = euler;
            return go;
        }

        static ParticleSystem Particles(GameObject go, Material m, bool loop, float rate, int burst, Vector2 life, Vector2 speed,
                                        Vector2 size, float gravity, float cone, Gradient colour, bool stretch = false)
        {
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main;
            main.playOnAwake = true;
            main.loop = loop;
            main.duration = loop ? 1f : 0.05f;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.startLifetime = new ParticleSystem.MinMaxCurve(life.x, life.y);
            main.startSpeed = new ParticleSystem.MinMaxCurve(speed.x, speed.y);
            main.startSize = new ParticleSystem.MinMaxCurve(size.x, size.y);
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
            main.gravityModifier = gravity;
            main.maxParticles = 400;
            var emission = ps.emission;
            emission.rateOverTime = rate;
            if (burst > 0) emission.SetBursts(new[] { new ParticleSystem.Burst(0f, (short)burst) });
            var shape = ps.shape;
            shape.enabled = cone > 0f;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = cone;
            shape.radius = 0.04f;
            var col = ps.colorOverLifetime;
            col.enabled = true;
            col.color = colour;
            var r = go.GetComponent<ParticleSystemRenderer>();
            r.sharedMaterial = m;
            if (stretch) { r.renderMode = ParticleSystemRenderMode.Stretch; r.velocityScale = 0.035f; r.lengthScale = 1.2f; }
            return ps;
        }

        static void Light(GameObject go, Color c, float range, float intensity)
        {
            var l = go.AddComponent<UnityEngine.Light>();
            l.type = LightType.Point;
            l.color = c;
            l.range = range;
            l.intensity = intensity;
        }

        static void Line(GameObject go, float width, Gradient colour)
        {
            var line = go.AddComponent<LineRenderer>();
            line.sharedMaterial = s_line;
            line.positionCount = 2;
            line.SetPosition(0, new Vector3(0f, 0f, -10f));
            line.SetPosition(1, Vector3.zero);
            line.useWorldSpace = false;
            line.widthMultiplier = width;
            line.colorGradient = colour;
            line.textureMode = LineTextureMode.Stretch;
            line.alignment = LineAlignment.View;
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            line.receiveShadows = false;
        }
    }
}
