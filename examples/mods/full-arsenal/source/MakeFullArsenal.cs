// Builds the asset bundle of the example mod full-arsenal. Not part of the game: copy this file into Assets/MW2/Editor of
// a copy of the project (the Player build script build_player.ps1 leaves one in <destination>/project) and run
//   Unity.exe -batchmode -quit -projectPath <copy> -executeMethod MW2.EditorTools.MakeFullArsenal.Make
//             --arsenal-output <mod>/effects/weapons [--arsenal-platforms windows,mac,linux] -logFile <log>
// It makes pictures, materials and prefabs in Assets/FullArsenal, builds them with Mw2ModBundleBuilder into
// arsenal.windows (and arsenal.mac / arsenal.linux when asked, with that platform's build support installed) and
// removes the folder again.
//
// Every weapon gets its own picture; only pictures change (the game's shots, damage, sounds and timing stay):
//   lasers      - the beams of burn-lasers: light at once from the emitter to what it hits, burning while the shot flies;
//   missiles    - a hot exhaust flare with a flame tongue and a long smoke trail; the hit is a fireball with debris,
//                 sparks, a shock ring and a rising column of dark smoke (inferno: burning napalm; NARC: a blinking beacon;
//                 torpedoes: a trail of bubbles);
//   autocannons - a muzzle blast with a jet, side vents and smoke; a glowing tracer; the hit throws sparks, dust and chunks;
//   machine guns and AMS - thin tracers, small flashes and ricochet sparks;
//   gauss       - a white-hot slug with an ion trail; a blue ring at the muzzle; a heavy hit with a shock ring;
//   PPC         - a ball of blue plasma wrapped in crackling arcs; arcs and a blue flash at the muzzle and at the hit;
//   flamer      - each burst rolls out as churning fire and smoke and sets the target alight for a moment.
using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace MW2.EditorTools
{
    public static class MakeFullArsenal
    {
        const string Folder = "Assets/FullArsenal";
        static Material s_add, s_beam, s_smoke, s_fire, s_ring, s_grit, s_storm, s_veins, s_bolt;

        public static void Make()
        {
            var args = Environment.GetCommandLineArgs();
            int at = Array.IndexOf(args, "--arsenal-output");
            string output = at >= 0 && at + 1 < args.Length ? args[at + 1] : throw new ArgumentException("--arsenal-output is missing");
            if (AssetDatabase.IsValidFolder(Folder)) AssetDatabase.DeleteAsset(Folder);
            AssetDatabase.CreateFolder("Assets", "FullArsenal");
            try
            {
                s_add = Mat("Additive", "Legacy Shaders/Particles/Additive", Picture("dot", 64, 64, Dot));
                s_beam = Mat("Beam", "Legacy Shaders/Particles/Additive", Picture("beam", 8, 64, Across));
                s_smoke = Mat("Smoke", "Legacy Shaders/Particles/Alpha Blended", Picture("smoke", 64, 64, Puff));
                s_fire = Mat("Fire", "Legacy Shaders/Particles/Additive", Picture("fire", 64, 64, Flame));
                s_ring = Mat("Ring", "Legacy Shaders/Particles/Additive", Picture("ring", 64, 64, Ring));
                s_grit = Mat("Grit", "Legacy Shaders/Particles/Alpha Blended", AssetDatabase.LoadAssetAtPath<Texture2D>(Folder + "/dot.png"));
                s_storm = Mat("Storm", "Legacy Shaders/Particles/Alpha Blended", Picture("storm", 64, 64, StormBody));
                s_veins = Mat("Veins", "Legacy Shaders/Particles/Additive", Picture("veins", 256, 256, Veins));
                s_bolt = Mat("Bolt", "Legacy Shaders/Particles/Additive", Picture("bolt", 128, 32, Zigzag, TextureWrapMode.Repeat));

                // Lasers (as burn-lasers 2.2).
                Save(Beam("BeamRed", new Color(1f, 0.16f, 0.1f), 0.07f, 0.45f, 0.8f));
                Save(Beam("BeamGreen", new Color(0.2f, 1f, 0.3f), 0.1f, 0.6f, 1f));
                Save(Beam("BeamBlue", new Color(0.3f, 0.55f, 1f), 0.15f, 0.9f, 1.35f));
                Save(Burn());

                // Missiles.
                Save(Missile("Missile", 1f, 2.5f, new Vector2(1.6f, 2.4f)));
                Save(Missile("MissileLong", 0.8f, 1.2f, new Vector2(1.2f, 1.8f)));
                Save(Missile("Rocket", 1.8f, 3f, new Vector2(3f, 4.5f)));
                Save(Torpedo());
                Save(Launch());
                Save(Blast());
                Save(Napalm());
                Save(Beacon());

                // Autocannons, machine guns, AMS.
                Save(Tracer("Tracer", new Color(1f, 0.75f, 0.3f), 0.22f, 0.07f, 0.7f, true));
                Save(Tracer("TracerThin", new Color(1f, 0.5f, 0.2f), 0.09f, 0.045f, 0.35f, false));
                Save(MuzzleBlast());
                Save(ShellHit());
                Save(Ricochet());

                // Gauss, PPC, flamer.
                Save(GaussSlug());
                Save(GaussMuzzle());
                Save(GaussHit());
                Save(PpcBolt());
                Save(PpcMuzzle());
                Save(PpcHit());
                Save(FlameShot());
                Save(FlameMuzzle());
                Save(FireHit());

                int on = Array.IndexOf(args, "--arsenal-platforms");
                string[] endings = on >= 0 && on + 1 < args.Length
                    ? Array.ConvertAll(args[on + 1].Split(','), p => "." + p.Trim().ToLowerInvariant())
                    : new[] { ".windows" };
                var written = Mw2ModBundleBuilder.Build(Folder, Path.GetFullPath(output), "arsenal", endings);
                Debug.Log("FULL_ARSENAL_DONE " + string.Join(" ", written));
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

        /// <summary>A soft blob broken up by noise: tongues of flame.</summary>
        static float Flame(float u, float v)
        {
            float dx = u * 2f - 1f, dy = v * 2f - 1f, r = Mathf.Sqrt(dx * dx + dy * dy);
            float n = Mathf.PerlinNoise(u * 4f + 7f, v * 4f) * 0.55f + Mathf.PerlinNoise(u * 9f, v * 9f + 5f) * 0.45f;
            return Mathf.Clamp01(Mathf.Pow(Mathf.Clamp01(1f - r), 1.5f) * (0.35f + n * 1.1f));
        }

        /// <summary>A thin bright ring: shock waves.</summary>
        static float Ring(float u, float v)
        {
            float dx = u * 2f - 1f, dy = v * 2f - 1f, r = Mathf.Sqrt(dx * dx + dy * dy);
            float d = Mathf.Abs(r - 0.8f) / 0.15f;
            return Mathf.Clamp01(1f - d * d) * Mathf.Clamp01((1f - r) * 8f);
        }

        /// <summary>The PPC ball: a body with a lumpy rim (white; the particle colour tints it deep blue).</summary>
        static float StormBody(float u, float v)
        {
            float dx = u * 2f - 1f, dy = v * 2f - 1f, r = Mathf.Sqrt(dx * dx + dy * dy);
            float a = Mathf.Atan2(dy, dx);
            float rim = 0.82f + 0.07f * Mathf.Sin(a * 7f) + 0.05f * Mathf.Sin(a * 13f + 1.3f);
            return Mathf.Clamp01((rim - r) * 12f) * Mathf.Lerp(0.95f, 0.75f, r);
        }

        /// <summary>
        /// Lightning veins inside the ball, 4 x 4 different frames (texture sheet): thin ridges where the noise crosses
        /// its middle value, masked to a circle.
        /// </summary>
        static float Veins(float u, float v)
        {
            int cell = (int)(u * 4f) + 4 * (int)(v * 4f);
            float x = u * 4f % 1f, y = v * 4f % 1f;
            float dx = x * 2f - 1f, dy = y * 2f - 1f, r = Mathf.Sqrt(dx * dx + dy * dy);
            float o = cell * 17.3f;
            float n = Mathf.PerlinNoise(x * 3.2f + o, y * 3.2f + o * 0.7f) * 0.7f + Mathf.PerlinNoise(x * 9f + o, y * 9f - o) * 0.3f;
            float ridge = 1f - Mathf.Abs(n - 0.5f) * 2f;
            return Mathf.Clamp01(Mathf.Pow(ridge, 9f) * 1.4f) * Mathf.Clamp01((0.8f - r) * 6f);
        }

        /// <summary>A jagged lightning line along the trail (u along, v across), repeating along u.</summary>
        static float Zigzag(float u, float v)
        {
            float k = u * 6f, f = k - Mathf.Floor(k);
            int i = (int)Mathf.Floor(k);
            float y = Mathf.Lerp(Jag(i), Jag(i + 1), f) + 0.04f * Mathf.Sin(u * 6.2832f * 9f);
            float d = Mathf.Abs(v - y);
            return Mathf.Clamp01(Mathf.Clamp01(1f - d / 0.05f) + 0.35f * Mathf.Clamp01(1f - d / 0.18f));
        }

        /// <summary>Fixed offsets of the zigzag corners (six per repeat, the same at both ends so the tiles join).</summary>
        static float Jag(int i) => new[] { 0.5f, 0.78f, 0.3f, 0.62f, 0.2f, 0.7f }[((i % 6) + 6) % 6];

        static Texture2D Picture(string name, int w, int h, Func<float, float, float> alpha, TextureWrapMode wrap = TextureWrapMode.Clamp)
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
            importer.wrapMode = wrap;
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

        static void Save(GameObject go)
        {
            PrefabUtility.SaveAsPrefabAsset(go, Folder + "/" + go.name + ".prefab");
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
            main.scalingMode = ParticleSystemScalingMode.Hierarchy;     // the file's "scale" sizes the whole picture
            main.emitterVelocityMode = ParticleSystemEmitterVelocityMode.Transform;
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
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            if (stretch) { r.renderMode = ParticleSystemRenderMode.Stretch; r.velocityScale = 0.035f; r.lengthScale = 1.2f; }
            return ps;
        }

        /// <summary>Particles that stay where they were given off: a trail behind a moving shot, <paramref name="perMetre"/>
        /// of them for each metre it flies.</summary>
        static void PerMetre(ParticleSystem ps, float perMetre, int max)
        {
            var emission = ps.emission;
            emission.rateOverTime = 0f;
            emission.rateOverDistance = perMetre;
            var main = ps.main;
            main.maxParticles = max;
        }

        /// <summary>Particles that move with the copy (the glow round a shot's head).</summary>
        static void Local(ParticleSystem ps)
        {
            var main = ps.main;
            main.simulationSpace = ParticleSystemSimulationSpace.Local;
        }

        static void Sphere(ParticleSystem ps, float radius)
        {
            var shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = radius;
        }

        /// <summary>A system that keeps giving off <paramref name="rate"/> a second for <paramref name="seconds"/>, once.</summary>
        static void For(ParticleSystem ps, float seconds, float rate)
        {
            var main = ps.main;
            main.loop = false;
            main.duration = seconds;
            var emission = ps.emission;
            emission.rateOverTime = rate;
        }

        static void Delay(ParticleSystem ps, float seconds)
        {
            var main = ps.main;
            main.startDelay = seconds;
        }

        static void Grow(ParticleSystem ps, float from, float to)
        {
            var s = ps.sizeOverLifetime;
            s.enabled = true;
            s.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.EaseInOut(0f, from, 1f, to));
        }

        static void Waver(ParticleSystem ps, float strength, float frequency)
        {
            var n = ps.noise;
            n.enabled = true;
            n.strength = strength;
            n.frequency = frequency;
            n.scrollSpeed = 1.5f;
            n.quality = ParticleSystemNoiseQuality.Medium;
        }

        /// <summary>Electric arcs: the particles jerk about (noise) and draw their way as thin bright lines (trails).</summary>
        static void Arcs(ParticleSystem ps, Gradient colour, float width)
        {
            var n = ps.noise;
            n.enabled = true;
            n.strength = 5f;
            n.frequency = 3f;
            n.scrollSpeed = 4f;
            n.quality = ParticleSystemNoiseQuality.High;
            var t = ps.trails;
            t.enabled = true;
            t.lifetime = 0.6f;
            t.minVertexDistance = 0.05f;
            t.widthOverTrail = new ParticleSystem.MinMaxCurve(width);
            t.colorOverTrail = colour;
            t.dieWithParticles = true;
            ps.GetComponent<ParticleSystemRenderer>().trailMaterial = s_beam;
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

        static TrailRenderer Trail(GameObject go, float width, float time, Gradient colour, float tail)
        {
            var t = go.AddComponent<TrailRenderer>();
            t.sharedMaterial = s_beam;
            t.time = time;
            t.widthMultiplier = width;
            t.widthCurve = AnimationCurve.Linear(0f, 1f, 1f, tail);
            t.colorGradient = colour;
            t.minVertexDistance = 0.3f;
            t.alignment = LineAlignment.View;
            t.textureMode = LineTextureMode.Stretch;
            t.numCapVertices = 2;
            t.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            t.receiveShadows = false;
            return t;
        }

        static readonly Color Hot = new Color(1f, 0.95f, 0.8f), Molten = new Color(1f, 0.55f, 0.15f), Ember = new Color(0.65f, 0.1f, 0.03f);
        static readonly Color Soot = new Color(0.16f, 0.15f, 0.14f), Ash = new Color(0.42f, 0.41f, 0.4f), Dust = new Color(0.47f, 0.42f, 0.35f);
        static readonly Color Plasma = new Color(0.45f, 0.7f, 1f), Ion = new Color(0.55f, 0.85f, 1f);

        /// <summary>Fire that burns out: white-yellow, orange, red, gone.</summary>
        static Gradient Burning(float a = 1f) =>
            Ramp((0f, Hot, a), (0.25f, Molten, a * 0.9f), (0.6f, Ember, a * 0.5f), (1f, Ember, 0f));

        // ---- shared pieces ----

        static void Flash(GameObject parent, string name, Color c, float size, float life, int count = 2)
        {
            var ps = Particles(Child(parent, name), s_add, false, 0f, count, new Vector2(life * 0.7f, life), Vector2.zero,
                               new Vector2(size * 0.8f, size), 0f, 0f, Ramp((0f, Color.Lerp(c, Color.white, 0.6f), 1f), (1f, c, 0f)));
            Grow(ps, 0.7f, 1.2f);
        }

        static void Sparks(GameObject parent, string name, Color hot, int count, float cone, Vector2 speed, Vector2 life, Vector2 size, float gravity)
        {
            Particles(Child(parent, name), s_add, false, 0f, count, life, speed, size, gravity, cone,
                      Ramp((0f, Color.Lerp(hot, Color.white, 0.5f), 1f), (0.35f, hot, 1f), (1f, Ember, 0f)), true);
        }

        static void Shock(GameObject parent, Color c, float to, float life, float alpha)
        {
            var ps = Particles(Child(parent, "Ring"), s_ring, false, 0f, 1, new Vector2(life, life), Vector2.zero, Vector2.one,
                               0f, 0f, Ramp((0f, c, alpha), (1f, c, 0f)));
            Grow(ps, 1f, to);
        }

        static void Smoke(GameObject parent, string name, Color c, float alpha, int count, float cone, Vector2 speed, Vector2 life,
                          Vector2 size, float rise, float grow)
        {
            var ps = Particles(Child(parent, name), s_smoke, false, 0f, count, life, speed, size, -rise, cone,
                               Ramp((0f, c, alpha), (0.6f, Color.Lerp(c, Ash, 0.4f), alpha * 0.6f), (1f, Ash, 0f)));
            Grow(ps, 0.7f, grow);
        }

        // ---- lasers (as burn-lasers 2.2) ----

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

        // ---- missiles ----

        /// <summary>
        /// A missile in flight (shot prefab riding the game's shot, +Z the flight): a white-hot exhaust flare, a short flame
        /// tongue and a smoke trail left in the air, <paramref name="smoke"/> puffs a metre, each living <paramref name="smokeLife"/>.
        /// </summary>
        static GameObject Missile(string name, float k, float smoke, Vector2 smokeLife)
        {
            var go = new GameObject(name);
            var flare = Particles(Child(go, "Flare"), s_add, true, 40f, 0, new Vector2(0.04f, 0.07f), Vector2.zero, new Vector2(0.9f, 1.3f) * k,
                                  0f, 0f, Ramp((0f, Hot, 1f), (1f, Molten, 0f)));
            Local(flare);
            var flame = Particles(Child(go, "Flame", new Vector3(0f, 180f, 0f)), s_fire, true, 0f, 0, new Vector2(0.08f, 0.15f),
                                  new Vector2(2f, 5f), new Vector2(0.5f, 0.8f) * k, 0f, 6f, Burning());
            PerMetre(flame, 3f, 300);
            Grow(flame, 1f, 0.3f);
            var trail = Particles(Child(go, "Smoke", new Vector3(0f, 180f, 0f)), s_smoke, true, 0f, 0, smokeLife, new Vector2(0.2f, 0.8f),
                                  new Vector2(0.7f, 1f) * k, -0.02f, 8f,
                                  Ramp((0f, new Color(0.72f, 0.7f, 0.67f), 0f), (0.08f, new Color(0.72f, 0.7f, 0.67f), 0.55f),
                                       (0.5f, new Color(0.55f, 0.55f, 0.55f), 0.35f), (1f, Ash, 0f)));
            PerMetre(trail, smoke, 1000);
            Grow(trail, 1f, 4f);
            Waver(trail, 0.3f, 0.5f);
            Glow(go, Molten, 8f * k, 1.2f);
            return go;
        }

        /// <summary>A torpedo under water: no flame, a trail of bubbles that rise.</summary>
        static GameObject Torpedo()
        {
            var go = new GameObject("Torpedo");
            var bubbles = Particles(Child(go, "Bubbles", new Vector3(0f, 180f, 0f)), s_add, true, 0f, 0, new Vector2(1.5f, 2.5f),
                                    new Vector2(0.3f, 1f), new Vector2(0.15f, 0.35f), -0.15f, 20f,
                                    Ramp((0f, new Color(0.85f, 0.95f, 1f), 0.7f), (1f, new Color(0.6f, 0.8f, 1f), 0f)));
            PerMetre(bubbles, 3f, 600);
            Waver(bubbles, 0.4f, 1f);
            var wake = Particles(Child(go, "Wake", new Vector3(0f, 180f, 0f)), s_smoke, true, 0f, 0, new Vector2(2f, 3f), new Vector2(0.1f, 0.3f),
                                 new Vector2(0.6f, 0.9f), 0f, 10f, Ramp((0f, new Color(0.75f, 0.85f, 0.9f), 0.25f), (1f, new Color(0.6f, 0.7f, 0.8f), 0f)));
            PerMetre(wake, 1f, 300);
            Grow(wake, 1f, 3f);
            return go;
        }

        /// <summary>A missile leaves its tube (muzzle prefab, +Z the flight): a flash and a back-blast of smoke.</summary>
        static GameObject Launch()
        {
            var go = new GameObject("Launch");
            Flash(go, "Flash", Molten, 1.5f, 0.07f);
            Smoke(Child(go, "Back", new Vector3(0f, 180f, 0f)), "Blast", new Color(0.7f, 0.68f, 0.65f), 0.5f, 6, 30f,
                  new Vector2(3f, 8f), new Vector2(0.8f, 1.4f), new Vector2(0.6f, 0.9f), 0.03f, 3f);
            Smoke(go, "Puff", new Color(0.75f, 0.73f, 0.7f), 0.35f, 3, 15f, new Vector2(1f, 3f), new Vector2(0.6f, 1f), new Vector2(0.5f, 0.7f), 0.03f, 2.5f);
            Glow(go, Molten, 6f, 1.5f);
            return go;
        }

        /// <summary>
        /// An explosion (impact prefab, +Z back towards the shooter): a white core, a rolling fireball, a shock ring, glowing
        /// debris, dark chunks and a column of smoke that rises and spreads.
        /// </summary>
        static GameObject Blast()
        {
            var go = new GameObject("Blast");
            Flash(go, "Core", Hot, 8f, 0.15f);
            var fire = Particles(Child(go, "Fire"), s_fire, false, 0f, 16, new Vector2(0.4f, 0.8f), new Vector2(2f, 6f), new Vector2(2.5f, 4.5f),
                                 -0.1f, 0f, Ramp((0f, Hot, 1f), (0.25f, Molten, 1f), (0.6f, Ember, 0.6f), (1f, Soot, 0f)));
            Sphere(fire, 0.5f);
            Grow(fire, 0.6f, 1.8f);
            Shock(go, Hot, 7f, 0.25f, 0.6f);
            Sparks(go, "Debris", Molten, 30, 70f, new Vector2(8f, 22f), new Vector2(0.5f, 1.2f), new Vector2(0.1f, 0.22f), 1.4f);
            Particles(Child(go, "Chunks"), s_grit, false, 0f, 10, new Vector2(1f, 1.6f), new Vector2(6f, 14f), new Vector2(0.1f, 0.2f), 2f, 60f,
                      Ramp((0f, Soot, 1f), (1f, Soot, 0f)));
            var smoke = Particles(Child(go, "Smoke"), s_smoke, false, 0f, 10, new Vector2(2.5f, 4f), new Vector2(0.8f, 2.2f), new Vector2(2f, 3f),
                                  -0.05f, 0f, Ramp((0f, Soot, 0.65f), (0.5f, new Color(0.25f, 0.24f, 0.23f), 0.45f), (1f, Ash, 0f)));
            Sphere(smoke, 0.8f);
            Delay(smoke, 0.05f);
            Grow(smoke, 0.8f, 3f);
            Glow(go, Molten, 22f, 3f);
            return go;
        }

        /// <summary>An inferno hit: a burst of burning jelly that keeps burning for about three seconds, with black smoke.</summary>
        static GameObject Napalm()
        {
            var go = new GameObject("Napalm");
            Flash(go, "Core", Molten, 3.5f, 0.12f);
            var splash = Particles(Child(go, "Splash"), s_fire, false, 0f, 18, new Vector2(0.4f, 0.8f), new Vector2(3f, 9f), new Vector2(0.8f, 1.5f),
                                   0.8f, 70f, Burning());
            Grow(splash, 1f, 0.5f);
            var flames = Particles(Child(go, "Flames"), s_fire, false, 0f, 0, new Vector2(0.4f, 0.9f), new Vector2(0.5f, 1.5f), new Vector2(1f, 2f),
                                   -0.3f, 0f, Burning(0.9f));
            For(flames, 3f, 30f);
            Sphere(flames, 1.2f);
            Grow(flames, 1f, 0.2f);
            var smoke = Particles(Child(go, "Smoke"), s_smoke, false, 0f, 0, new Vector2(2f, 3f), new Vector2(0.5f, 1.2f), new Vector2(1.2f, 1.8f),
                                  -0.12f, 0f, Ramp((0f, Soot, 0.6f), (1f, Soot, 0f)));
            For(smoke, 3f, 6f);
            Sphere(smoke, 1f);
            Grow(smoke, 0.8f, 3f);
            Glow(go, Molten, 14f, 2f);
            return go;
        }

        /// <summary>A NARC pod sticks to the target: a red beacon that blinks for six seconds.</summary>
        static GameObject Beacon()
        {
            var go = new GameObject("Beacon");
            Sparks(go, "Hit", Molten, 10, 50f, new Vector2(5f, 12f), new Vector2(0.2f, 0.4f), new Vector2(0.05f, 0.1f), 1f);
            var red = new Color(1f, 0.12f, 0.08f);
            var blink = Particles(Child(go, "Blink"), s_add, false, 0f, 0, new Vector2(0.15f, 0.15f), Vector2.zero, new Vector2(1f, 1f), 0f, 0f,
                                  Ramp((0f, Color.Lerp(red, Color.white, 0.4f), 1f), (1f, red, 0f)));
            var emission = blink.emission;
            emission.SetBursts(new[] { new ParticleSystem.Burst(0f, new ParticleSystem.MinMaxCurve(1f), 12, 0.5f) });
            For(blink, 6f, 0f);
            Local(blink);
            var pulse = Particles(Child(go, "Pulse"), s_ring, false, 0f, 0, new Vector2(0.4f, 0.4f), Vector2.zero, new Vector2(0.5f, 0.5f), 0f, 0f,
                                  Ramp((0f, red, 0.6f), (1f, red, 0f)));
            var pe = pulse.emission;
            pe.SetBursts(new[] { new ParticleSystem.Burst(0f, new ParticleSystem.MinMaxCurve(1f), 12, 0.5f) });
            For(pulse, 6f, 0f);
            Local(pulse);
            Grow(pulse, 1f, 5f);
            Glow(go, red, 5f, 1f);
            return go;
        }

        // ---- autocannons, machine guns, AMS ----

        /// <summary>A shell in flight (shot prefab, +Z the flight): a glowing head and a streak of light behind it.</summary>
        static GameObject Tracer(string name, Color c, float width, float time, float head, bool light)
        {
            var go = new GameObject(name);
            Trail(go, width, time, Ramp((0f, Color.Lerp(c, Color.white, 0.6f), 1f), (1f, c, 0f)), 0.3f);
            var h = Particles(Child(go, "Head"), s_add, true, 40f, 0, new Vector2(0.03f, 0.05f), Vector2.zero, new Vector2(head * 0.8f, head),
                              0f, 0f, Ramp((0f, Color.Lerp(c, Color.white, 0.7f), 1f), (1f, c, 0f)));
            Local(h);
            if (light) Glow(go, c, 5f, 0.8f);
            return go;
        }

        /// <summary>A gun fires (muzzle prefab, +Z the flight): a star flash, a jet of fire, side vents, a puff of smoke.</summary>
        static GameObject MuzzleBlast()
        {
            var go = new GameObject("MuzzleBlast");
            Flash(go, "Flash", new Color(1f, 0.8f, 0.4f), 2.6f, 0.08f, 3);
            Particles(Child(go, "Jet"), s_fire, false, 0f, 12, new Vector2(0.05f, 0.1f), new Vector2(15f, 35f), new Vector2(0.3f, 0.6f), 0f, 12f,
                      Burning(), true);
            Particles(Child(go, "Vents"), s_fire, false, 0f, 8, new Vector2(0.06f, 0.1f), new Vector2(4f, 10f), new Vector2(0.25f, 0.45f), 0f, 80f,
                      Burning(0.8f), true);
            Smoke(go, "Smoke", new Color(0.62f, 0.6f, 0.57f), 0.45f, 6, 25f, new Vector2(1.5f, 5f), new Vector2(0.8f, 1.4f), new Vector2(0.6f, 1f), 0.03f, 3f);
            Glow(go, new Color(1f, 0.75f, 0.35f), 10f, 2f);
            return go;
        }

        /// <summary>A shell hits (impact prefab, +Z back towards the shooter): a flash, sparks, dust and chunks.</summary>
        static GameObject ShellHit()
        {
            var go = new GameObject("ShellHit");
            Flash(go, "Flash", Molten, 2.2f, 0.08f);
            Sparks(go, "Sparks", Molten, 25, 65f, new Vector2(10f, 28f), new Vector2(0.25f, 0.6f), new Vector2(0.06f, 0.12f), 1.2f);
            Smoke(go, "Dust", Dust, 0.55f, 7, 50f, new Vector2(1.5f, 4f), new Vector2(1.2f, 2.2f), new Vector2(1f, 1.6f), 0.02f, 3f);
            Particles(Child(go, "Chunks"), s_grit, false, 0f, 8, new Vector2(0.8f, 1.3f), new Vector2(5f, 12f), new Vector2(0.07f, 0.14f), 2f, 55f,
                      Ramp((0f, Soot, 1f), (1f, Soot, 0f)));
            Glow(go, Molten, 8f, 1.5f);
            return go;
        }

        /// <summary>A bullet hits: a small flash and a few ricochet sparks.</summary>
        static GameObject Ricochet()
        {
            var go = new GameObject("Ricochet");
            Flash(go, "Flash", Molten, 0.8f, 0.05f, 1);
            Sparks(go, "Sparks", Molten, 10, 50f, new Vector2(8f, 20f), new Vector2(0.2f, 0.4f), new Vector2(0.05f, 0.08f), 1f);
            Smoke(go, "Dust", Dust, 0.4f, 3, 40f, new Vector2(0.8f, 2f), new Vector2(0.6f, 1f), new Vector2(0.4f, 0.6f), 0.02f, 2f);
            return go;
        }

        // ---- gauss ----

        static GameObject GaussSlug()
        {
            var go = new GameObject("GaussSlug");
            Trail(go, 0.35f, 0.4f, Ramp((0f, Color.Lerp(Ion, Color.white, 0.7f), 1f), (0.4f, Ion, 0.6f), (1f, Plasma, 0f)), 0.1f);
            var head = Particles(Child(go, "Head"), s_add, true, 50f, 0, new Vector2(0.03f, 0.05f), Vector2.zero, new Vector2(0.6f, 0.8f), 0f, 0f,
                                 Ramp((0f, Color.white, 1f), (1f, Ion, 0f)));
            Local(head);
            var ions = Particles(Child(go, "Ions"), s_add, true, 0f, 0, new Vector2(0.4f, 0.9f), new Vector2(0.2f, 0.8f), new Vector2(0.15f, 0.3f),
                                 0f, 0f, Ramp((0f, Ion, 0.9f), (1f, Plasma, 0f)));
            Sphere(ions, 0.2f);
            PerMetre(ions, 1.5f, 600);
            Waver(ions, 0.6f, 1.5f);
            Glow(go, Ion, 9f, 1.5f);
            return go;
        }

        static GameObject GaussMuzzle()
        {
            var go = new GameObject("GaussMuzzle");
            Flash(go, "Flash", Ion, 2.2f, 0.08f);
            Shock(go, Ion, 5f, 0.2f, 0.8f);
            Sparks(go, "Sparks", Ion, 16, 35f, new Vector2(10f, 25f), new Vector2(0.15f, 0.3f), new Vector2(0.05f, 0.1f), 0.3f);
            Smoke(go, "Smoke", new Color(0.75f, 0.78f, 0.8f), 0.3f, 4, 20f, new Vector2(1f, 3f), new Vector2(0.6f, 1f), new Vector2(0.5f, 0.8f), 0.02f, 2.5f);
            Glow(go, Ion, 12f, 2.5f);
            return go;
        }

        static GameObject GaussHit()
        {
            var go = new GameObject("GaussHit");
            Flash(go, "Flash", Color.white, 3f, 0.08f);
            Shock(go, Color.white, 7f, 0.22f, 0.5f);
            Sparks(go, "Sparks", Molten, 45, 75f, new Vector2(14f, 35f), new Vector2(0.35f, 0.8f), new Vector2(0.07f, 0.14f), 1.1f);
            Smoke(go, "Dust", Dust, 0.6f, 12, 60f, new Vector2(2f, 6f), new Vector2(1.5f, 2.8f), new Vector2(1.4f, 2.2f), 0.02f, 3f);
            Particles(Child(go, "Chunks"), s_grit, false, 0f, 14, new Vector2(0.9f, 1.5f), new Vector2(6f, 16f), new Vector2(0.08f, 0.18f), 2f, 60f,
                      Ramp((0f, Soot, 1f), (1f, Soot, 0f)));
            Glow(go, Color.Lerp(Ion, Color.white, 0.5f), 14f, 2.5f);
            return go;
        }

        // ---- PPC ----

        static Gradient Electric(float a = 1f) => Ramp((0f, Color.white, a), (0.4f, Ion, a), (1f, Plasma, 0f));
        static readonly Color Deep = new Color(0.08f, 0.22f, 1f), Storm = new Color(0.2f, 0.45f, 1f);

        /// <summary>Draw after the ball body (additive veins over the alpha-blended blue).</summary>
        static void On(ParticleSystem ps, int order) => ps.GetComponent<ParticleSystemRenderer>().sortingOrder = order;

        static void Frames(ParticleSystem ps)
        {
            var sheet = ps.textureSheetAnimation;
            sheet.enabled = true;
            sheet.numTilesX = 4;
            sheet.numTilesY = 4;
            sheet.frameOverTime = new ParticleSystem.MinMaxCurve(0f);
            sheet.startFrame = new ParticleSystem.MinMaxCurve(0f, 15f);
        }

        /// <summary>The ball itself: a deep blue lumpy body and white veins that change every few hundredths of a second
        /// (as the animated ball of the original), all riding the copy.</summary>
        static void Ball(GameObject parent, float size, float rate)
        {
            var body = Particles(Child(parent, "Body"), s_storm, true, rate, 0, new Vector2(0.06f, 0.1f), Vector2.zero,
                                 new Vector2(size * 0.9f, size), 0f, 0f, Ramp((0f, Storm, 0.9f), (0.5f, Deep, 0.85f), (1f, Deep, 0f)));
            Local(body);
            var veins = Particles(Child(parent, "Veins"), s_veins, true, rate * 1.5f, 0, new Vector2(0.04f, 0.07f), Vector2.zero,
                                  new Vector2(size * 0.8f, size * 0.95f), 0f, 0f, Ramp((0f, Color.white, 1f), (0.5f, Ion, 1f), (1f, Ion, 0f)));
            Local(veins);
            Frames(veins);
            On(veins, 1);
            var heart = Particles(Child(parent, "Heart"), s_add, true, 30f, 0, new Vector2(0.05f, 0.08f), Vector2.zero,
                                  new Vector2(size * 0.35f, size * 0.45f), 0f, 0f, Ramp((0f, Color.white, 0.9f), (1f, Ion, 0f)));
            Local(heart);
            On(heart, 2);
            var halo = Particles(Child(parent, "Halo"), s_add, true, 15f, 0, new Vector2(0.1f, 0.15f), Vector2.zero,
                                 new Vector2(size * 1.8f, size * 2.2f), 0f, 0f, Ramp((0f, Plasma, 0.3f), (1f, Plasma, 0f)));
            Local(halo);
        }

        static TrailRenderer Jagged(GameObject go, float width, float time, Gradient colour)
        {
            var t = Trail(go, width, time, colour, 0.25f);
            t.sharedMaterial = s_bolt;
            t.textureMode = LineTextureMode.Tile;
            t.textureScale = new Vector2(0.25f, 1f);   // one zigzag repeat each 4 m
            t.minVertexDistance = 0.5f;
            return t;
        }

        /// <summary>
        /// The bolt (shot prefab riding the game's shot): a big ball of blue plasma laced with lightning (as the DOS
        /// version) dragging a long white-blue jagged discharge and crackling arcs (as the newer games).
        /// </summary>
        static GameObject PpcBolt()
        {
            var go = new GameObject("PpcBolt");
            Ball(go, 3.2f, 40f);
            Jagged(Child(go, "Bolt"), 0.9f, 0.22f, Ramp((0f, Color.white, 1f), (0.3f, Ion, 0.9f), (1f, Plasma, 0f)));
            Jagged(Child(go, "Bolt2", new Vector3(0f, 0f, 90f)), 1.3f, 0.15f, Ramp((0f, Ion, 0.8f), (1f, Plasma, 0f)));
            Trail(Child(go, "Glow"), 1.6f, 0.18f, Ramp((0f, Plasma, 0.45f), (1f, Deep, 0f)), 0.1f);
            var arcs = Particles(Child(go, "Arcs"), s_add, true, 45f, 0, new Vector2(0.1f, 0.2f), new Vector2(4f, 9f), new Vector2(0.05f, 0.05f),
                                 0f, 0f, Electric());
            Sphere(arcs, 1.2f);
            Arcs(arcs, Electric(), 0.12f);
            var sparks = Particles(Child(go, "Sparks"), s_add, true, 0f, 0, new Vector2(0.2f, 0.45f), new Vector2(1f, 4f), new Vector2(0.08f, 0.16f),
                                   0f, 0f, Ramp((0f, Color.white, 1f), (0.5f, Ion, 0.8f), (1f, Plasma, 0f)));
            Sphere(sparks, 1f);
            PerMetre(sparks, 1.5f, 600);
            Glow(go, Storm, 18f, 2.6f);
            return go;
        }

        static GameObject PpcMuzzle()
        {
            var go = new GameObject("PpcMuzzle");
            Flash(go, "Flash", Ion, 3f, 0.1f);
            var ball = Particles(Child(go, "Ball"), s_veins, false, 0f, 2, new Vector2(0.12f, 0.16f), Vector2.zero, new Vector2(2f, 2.4f), 0f, 0f,
                                 Ramp((0f, Color.white, 1f), (1f, Ion, 0f)));
            Frames(ball);
            var arcs = Particles(Child(go, "Arcs"), s_add, false, 0f, 14, new Vector2(0.15f, 0.25f), new Vector2(4f, 10f), new Vector2(0.05f, 0.05f),
                                 0f, 45f, Electric());
            Arcs(arcs, Electric(), 0.09f);
            Glow(go, Storm, 14f, 3f);
            return go;
        }

        /// <summary>The hit: the ball bursts into a flash of blue lightning that crawls over the target, sparks and vapour.</summary>
        static GameObject PpcHit()
        {
            var go = new GameObject("PpcHit");
            Flash(go, "Flash", Ion, 6f, 0.14f, 3);
            var burst = Particles(Child(go, "Burst"), s_storm, false, 0f, 2, new Vector2(0.25f, 0.35f), Vector2.zero, new Vector2(4f, 5f), 0f, 0f,
                                  Ramp((0f, Storm, 0.8f), (1f, Deep, 0f)));
            Grow(burst, 0.6f, 1.5f);
            var veins = Particles(Child(go, "Veins"), s_veins, false, 0f, 6, new Vector2(0.1f, 0.3f), Vector2.zero, new Vector2(4f, 6f), 0f, 0f,
                                  Ramp((0f, Color.white, 1f), (0.5f, Ion, 1f), (1f, Plasma, 0f)));
            Frames(veins);
            On(veins, 1);
            Grow(veins, 0.8f, 1.3f);
            Shock(go, Ion, 8f, 0.25f, 0.7f);
            var arcs = Particles(Child(go, "Arcs"), s_add, false, 0f, 30, new Vector2(0.25f, 0.5f), new Vector2(5f, 12f), new Vector2(0.05f, 0.05f),
                                 0f, 85f, Electric());
            Arcs(arcs, Electric(), 0.13f);
            var crawl = Particles(Child(go, "Crawl"), s_add, false, 0f, 0, new Vector2(0.1f, 0.2f), new Vector2(1f, 3f), new Vector2(0.04f, 0.04f),
                                  0f, 0f, Electric(0.9f));
            Sphere(crawl, 1.5f);
            For(crawl, 0.6f, 40f);
            Arcs(crawl, Electric(0.9f), 0.08f);
            Sparks(go, "Sparks", Ion, 40, 75f, new Vector2(8f, 22f), new Vector2(0.3f, 0.7f), new Vector2(0.06f, 0.12f), 0.8f);
            Smoke(go, "Vapour", new Color(0.7f, 0.75f, 0.85f), 0.35f, 6, 50f, new Vector2(0.8f, 2f), new Vector2(1.2f, 2f), new Vector2(1f, 1.5f), 0.05f, 2.5f);
            Glow(go, Storm, 22f, 3.5f);
            return go;
        }

        // ---- flamer ----

        /// <summary>One blob of a flamer burst (shot prefab riding the game's shot): churning fire that leaves smoke.</summary>
        static GameObject FlameShot()
        {
            var go = new GameObject("FlameShot");
            var fire = Particles(Child(go, "Fire"), s_fire, true, 70f, 0, new Vector2(0.25f, 0.45f), new Vector2(0.5f, 2f), new Vector2(0.6f, 1f),
                                 -0.25f, 0f, Burning(0.9f));
            Sphere(fire, 0.3f);
            Grow(fire, 0.6f, 2.4f);
            var smoke = Particles(Child(go, "Smoke"), s_smoke, true, 10f, 0, new Vector2(0.8f, 1.4f), new Vector2(0.3f, 1f), new Vector2(1f, 1.5f),
                                  -0.1f, 0f, Ramp((0f, Soot, 0f), (0.2f, Soot, 0.3f), (1f, Ash, 0f)));
            Grow(smoke, 0.8f, 3f);
            Glow(go, Molten, 9f, 1.2f);
            return go;
        }

        static GameObject FlameMuzzle()
        {
            var go = new GameObject("FlameMuzzle");
            var fire = Particles(Child(go, "Fire"), s_fire, false, 0f, 6, new Vector2(0.1f, 0.15f), new Vector2(4f, 8f), new Vector2(0.4f, 0.7f),
                                 0f, 15f, Burning());
            Grow(fire, 0.6f, 1.6f);
            Glow(go, Molten, 6f, 1.2f);
            return go;
        }

        static GameObject FireHit()
        {
            var go = new GameObject("FireHit");
            var burst = Particles(Child(go, "Burst"), s_fire, false, 0f, 10, new Vector2(0.4f, 0.8f), new Vector2(1f, 3f), new Vector2(1f, 2f),
                                  -0.2f, 0f, Burning());
            Sphere(burst, 0.6f);
            Grow(burst, 0.8f, 1.6f);
            var flames = Particles(Child(go, "Flames"), s_fire, false, 0f, 0, new Vector2(0.3f, 0.6f), new Vector2(0.5f, 1.2f), new Vector2(0.6f, 1.2f),
                                   -0.35f, 0f, Burning(0.85f));
            For(flames, 1.2f, 20f);
            Sphere(flames, 0.8f);
            Grow(flames, 1f, 0.2f);
            Smoke(go, "Smoke", Soot, 0.4f, 4, 40f, new Vector2(0.5f, 1.5f), new Vector2(1.5f, 2.5f), new Vector2(1f, 1.5f), 0.12f, 3f);
            Glow(go, Molten, 10f, 1.5f);
            return go;
        }
    }
}
