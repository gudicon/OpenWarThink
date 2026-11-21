// Builds the asset bundle of the example mod burn-lasers. Not part of the game: copy this file into Assets/MW2/Editor of
// a copy of the project (the Player build script build_player.ps1 leaves one in <destination>/project) and run
//   Unity.exe -batchmode -quit -projectPath <copy> -executeMethod MW2.EditorTools.MakeBurnLasers.Make
//             --burn-output <mod>/effects/weapons [--burn-platforms windows,mac,linux] -logFile <log>
// It makes pictures, materials and prefabs in Assets/BurnLasers, builds them with Mw2ModBundleBuilder into
// lasers.windows (and lasers.mac / lasers.linux when asked, with that platform's build support installed) and
// removes the folder again.
//
// The idea: a laser is light, nothing flies. The beam is there at once from the emitter to what it hits (reach "instant")
// and stays while the game's shot is under way, i.e. while it burns: a thin core near white inside a faint coloured
// sheath, wavering a little (hot air bends it, "flicker"). Where it touches, a white-hot spot throws molten spatter back
// and sideways and boils off vapour. The emitter lens glows. When the game's shot arrives (the hit) a spray of molten
// drops falls, a puff of dark smoke rises and the armour glows orange there and cools through dark red. The beam keeps
// its class colour (small red, medium green, large blue); what burns is metal, so the spatter is always orange.
using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace MW2.EditorTools
{
    public static class MakeBurnLasers
    {
        const string Folder = "Assets/BurnLasers";
        static Material s_add, s_beam, s_smoke;

        public static void Make()
        {
            var args = Environment.GetCommandLineArgs();
            int at = Array.IndexOf(args, "--burn-output");
            string output = at >= 0 && at + 1 < args.Length ? args[at + 1] : throw new ArgumentException("--burn-output is missing");
            if (AssetDatabase.IsValidFolder(Folder)) AssetDatabase.DeleteAsset(Folder);
            AssetDatabase.CreateFolder("Assets", "BurnLasers");
            try
            {
                s_add = Mat("Additive", "Legacy Shaders/Particles/Additive", Picture("dot", 64, 64, Dot));
                s_beam = Mat("Beam", "Legacy Shaders/Particles/Additive", Picture("beam", 8, 64, Across));
                s_smoke = Mat("Smoke", "Legacy Shaders/Particles/Alpha Blended", Picture("smoke", 64, 64, Puff));
                Save(Beam("BeamRed", new Color(1f, 0.16f, 0.1f), 0.07f, 0.45f, 0.8f), "BeamRed");
                Save(Beam("BeamGreen", new Color(0.2f, 1f, 0.3f), 0.1f, 0.6f, 1f), "BeamGreen");
                Save(Beam("BeamBlue", new Color(0.3f, 0.55f, 1f), 0.15f, 0.9f, 1.35f), "BeamBlue");
                Save(Burn(), "Burn");
                int on = Array.IndexOf(args, "--burn-platforms");
                string[] endings = on >= 0 && on + 1 < args.Length
                    ? Array.ConvertAll(args[on + 1].Split(','), p => "." + p.Trim().ToLowerInvariant())
                    : new[] { ".windows" };
                var written = Mw2ModBundleBuilder.Build(Folder, Path.GetFullPath(output), "lasers", endings);
                Debug.Log("BURN_LASERS_DONE " + string.Join(" ", written));
            }
            finally { AssetDatabase.DeleteAsset(Folder); }
        }

        // ---- pictures and materials ----

        static float Dot(float u, float v)
        {
            float dx = u * 2f - 1f, dy = v * 2f - 1f, r = Mathf.Clamp01(1f - Mathf.Sqrt(dx * dx + dy * dy));
            return r * r;
        }

        /// <summary>Across the beam: a hard bright middle with a soft falloff to the sides.</summary>
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
            string path = Folder + "/" + name + ".png";
            File.WriteAllBytes(path, t.EncodeToPNG());
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
            var m = new Material(Shader.Find(shader)) { mainTexture = tex };
            AssetDatabase.CreateAsset(m, Folder + "/" + name + ".mat");
            return m;
        }

        static void Save(GameObject go, string name)
        {
            PrefabUtility.SaveAsPrefabAsset(go, Folder + "/" + name + ".prefab");
            UnityEngine.Object.DestroyImmediate(go);
        }

        // ---- building blocks ----

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

        static void Grow(ParticleSystem ps, float from, float to)
        {
            var s = ps.sizeOverLifetime;
            s.enabled = true;
            s.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.EaseInOut(0f, from, 1f, to));
        }

        static void Glow(GameObject go, Color c, float range, float intensity)
        {
            var l = go.AddComponent<Light>();
            l.type = LightType.Point;
            l.color = c;
            l.range = range;
            l.intensity = intensity;
        }

        static LineRenderer Line(GameObject go, float width, Gradient colour)
        {
            var line = go.AddComponent<LineRenderer>();
            line.sharedMaterial = s_beam;
            line.positionCount = 2;
            line.widthMultiplier = width;
            line.colorGradient = colour;
            line.textureMode = LineTextureMode.Stretch;
            line.alignment = LineAlignment.View;
            line.numCapVertices = 0;
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            line.receiveShadows = false;
            return line;
        }

        static readonly Color Hot = new Color(1f, 0.95f, 0.8f), Molten = new Color(1f, 0.55f, 0.15f), Ember = new Color(0.65f, 0.1f, 0.03f);

        /// <summary>
        /// The beam (shot prefab with reach "instant" and "stretch"): the root is the far end. Core and sheath are the lines;
        /// "Spot" burns where it touches (flare, spatter thrown back towards the shooter, vapour); "Muzzle" is the glowing lens.
        /// </summary>
        static GameObject Beam(string name, Color c, float core, float sheath, float k)
        {
            var go = new GameObject(name);
            var white = Color.Lerp(c, Color.white, 0.75f);
            Line(go, core, Ramp((0f, white, 0.9f), (1f, white, 1f)));
            Line(Child(go, "Sheath"), sheath, Ramp((0f, c, 0.25f), (0.7f, c, 0.4f), (1f, c, 0.5f)));

            var spot = Child(go, "Spot", new Vector3(0f, 180f, 0f));    // +Z of the root is the flight; the spot looks back
            var flare = Particles(spot, s_add, true, 45f, 0, new Vector2(0.05f, 0.1f), Vector2.zero, new Vector2(1.8f, 3f) * k, 0f, 0f,
                                  Ramp((0f, Hot, 1f), (1f, Color.Lerp(Hot, c, 0.4f), 0f)));
            var spatterGo = Child(spot, "Spatter");
            Particles(spatterGo, s_add, true, 160f * k, 0, new Vector2(0.25f, 0.7f), new Vector2(4f, 12f), new Vector2(0.12f, 0.25f) * k, 1f, 60f,
                      Ramp((0f, Hot, 1f), (0.35f, Molten, 1f), (1f, Ember, 0f)), true);
            var vapourGo = Child(spot, "Vapour");
            var vapour = Particles(vapourGo, s_smoke, true, 18f * k, 0, new Vector2(0.9f, 1.6f), new Vector2(0.5f, 1.2f), new Vector2(0.8f, 1.3f) * k,
                                   -0.08f, 35f, Ramp((0f, new Color(0.75f, 0.75f, 0.72f), 0.35f), (1f, new Color(0.5f, 0.5f, 0.5f), 0f)));
            Grow(vapour, 0.6f, 2.4f);
            Grow(flare, 0.8f, 1.1f);
            Glow(spot, Color.Lerp(Molten, Hot, 0.5f), 9f * k, 1.6f);

            var muzzle = Child(go, "Muzzle");
            Particles(muzzle, s_add, true, 55f, 0, new Vector2(0.04f, 0.08f), Vector2.zero, new Vector2(0.6f, 1f) * k, 0f, 0f,
                      Ramp((0f, Color.Lerp(c, Color.white, 0.6f), 1f), (1f, c, 0f)));
            Glow(muzzle, c, 5f, 0.8f);
            return go;
        }

        /// <summary>The hit (impact prefab, +Z back towards the shooter): molten drops, a puff of smoke, cooling armour, a light.</summary>
        static GameObject Burn()
        {
            var go = new GameObject("Burn");
            Particles(go, s_add, false, 0f, 50, new Vector2(0.5f, 1.2f), new Vector2(4f, 13f), new Vector2(0.12f, 0.3f), 1.2f, 70f,
                      Ramp((0f, Hot, 1f), (0.3f, Molten, 1f), (1f, Ember, 0f)), true);
            var smoke = Particles(Child(go, "Smoke"), s_smoke, false, 0f, 8, new Vector2(1.8f, 3.2f), new Vector2(0.6f, 1.8f), new Vector2(1.6f, 2.4f),
                                  -0.06f, 50f, Ramp((0f, new Color(0.2f, 0.19f, 0.18f), 0.55f), (1f, new Color(0.3f, 0.3f, 0.3f), 0f)));
            Grow(smoke, 0.7f, 2.6f);
            var scorch = Particles(Child(go, "Scorch"), s_add, false, 0f, 1, new Vector2(2.2f, 2.2f), Vector2.zero, new Vector2(2f, 2f), 0f, 0f,
                                   Ramp((0f, Hot, 1f), (0.1f, Molten, 0.9f), (0.5f, Ember, 0.6f), (1f, Ember, 0f)));
            Grow(scorch, 1.3f, 0.8f);
            Glow(go, Molten, 12f, 2.4f);
            return go;
        }
    }
}
