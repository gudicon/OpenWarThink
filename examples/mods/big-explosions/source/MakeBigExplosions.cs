// Builds the asset bundle of the example mod big-explosions. Not part of the game: copy this file into Assets/MW2/Editor of
// a copy of the project (the Player build script build_player.ps1 leaves one in <destination>/project) and run
//   Unity.exe -batchmode -quit -projectPath <copy> -executeMethod MW2.EditorTools.MakeBigExplosions.Make
//             --booms-output <mod>/effects/mechs [--booms-platforms windows,mac,linux] -logFile <log>
// It makes pictures, materials and prefabs in Assets/BigExplosions, builds them with Mw2ModBundleBuilder into
// booms.windows (and booms.mac / booms.linux when asked, with that platform's build support installed) and removes the
// folder again.
//
// The destruction of units gets new pictures (what happens to the unit, its timing, sounds and debris stay):
//   MechDeath   - a dead mech: a white flash, a rolling fireball that climbs into a mushroom, a ring of dust racing over
//                 the ground, burning chunks and sparks thrown high, then the wreck burns with a column of black smoke
//                 for about ten seconds (the game's own wreck explosions go off in that time);
//   WreckBurst  - each smaller explosion on the wreck: a fireball, sparks and a puff of smoke;
//   SectionBlow - an arm, a leg or the torso blown off: a fountain of sparks, a burst of flame and trailing smoke;
//   BlowApart   - a wreck that lands hard and breaks up: a blast, chunks and dust;
//   VehicleDeath - a tank, car, aircraft or turret: a sharp blast with chunks flung up, then fire and smoke for a while;
//   DebrisTrail - rides with each small piece the wreck throws: a flame and a soft trail of dark smoke behind it.
// A thrown-off part that is shot apart gets a small WreckBurst ("partBlast" in the mod files).
// Prefabs stand upright (+Y up, +Z the unit's forward); the mod files give the size per weight class.
using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace MW2.EditorTools
{
    public static class MakeBigExplosions
    {
        const string Folder = "Assets/BigExplosions";
        static Material s_add, s_beam, s_smoke, s_fire, s_ring, s_grit, s_flat;

        public static void Make()
        {
            var args = Environment.GetCommandLineArgs();
            int at = Array.IndexOf(args, "--booms-output");
            string output = at >= 0 && at + 1 < args.Length ? args[at + 1] : throw new ArgumentException("--booms-output is missing");
            if (AssetDatabase.IsValidFolder(Folder)) AssetDatabase.DeleteAsset(Folder);
            AssetDatabase.CreateFolder("Assets", "BigExplosions");
            try
            {
                s_add = Mat("Additive", "Legacy Shaders/Particles/Additive", Picture("dot", 64, 64, Dot));
                s_beam = Mat("Beam", "Legacy Shaders/Particles/Additive", Picture("beam", 8, 64, Across));
                s_smoke = Mat("Smoke", "Legacy Shaders/Particles/Alpha Blended", Picture("smoke", 64, 64, Puff));
                s_fire = Mat("Fire", "Legacy Shaders/Particles/Additive", Picture("fire", 64, 64, Flame));
                s_ring = Mat("Ring", "Legacy Shaders/Particles/Additive", Picture("ring", 64, 64, Ring));
                s_grit = Mat("Grit", "Legacy Shaders/Particles/Alpha Blended", AssetDatabase.LoadAssetAtPath<Texture2D>(Folder + "/dot.png"));
                s_flat = Mat("Dust", "Legacy Shaders/Particles/Alpha Blended", Picture("dustring", 64, 64, Ring));

                Save(MechDeath());
                Save(WreckBurst());
                Save(SectionBlow());
                Save(BlowApart());
                Save(VehicleDeath());
                Save(DebrisTrail());

                int on = Array.IndexOf(args, "--booms-platforms");
                string[] endings = on >= 0 && on + 1 < args.Length
                    ? Array.ConvertAll(args[on + 1].Split(','), p => "." + p.Trim().ToLowerInvariant())
                    : new[] { ".windows" };
                var written = Mw2ModBundleBuilder.Build(Folder, Path.GetFullPath(output), "booms", endings);
                Debug.Log("BIG_EXPLOSIONS_DONE " + string.Join(" ", written));
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
        static Gradient Fire(float a = 1f) =>
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

        // ---- the explosions (upright: +Y up) ----

        static readonly Color Dark = new Color(0.08f, 0.075f, 0.07f);

        /// <summary>Particles that lie flat on the ground (a ring of dust or a scorch).</summary>
        static void Flat(ParticleSystem ps)
        {
            var r = ps.GetComponent<ParticleSystemRenderer>();
            r.renderMode = ParticleSystemRenderMode.HorizontalBillboard;
        }

        /// <summary>Upwards: a cone opening round +Y (the shape's own axis is +Z).</summary>
        static GameObject Up(GameObject parent, string name) => Child(parent, name, new Vector3(-90f, 0f, 0f));

        /// <summary>The fireball: hot puffs that swell, climb and darken into smoke.</summary>
        static void Fireball(GameObject go, int count, Vector2 size, float rise, float radius)
        {
            var fire = Particles(Up(go, "Fireball"), s_fire, false, 0f, count, new Vector2(1f, 1.8f), new Vector2(1f, 4f), size,
                                 -rise, 60f, Ramp((0f, Hot, 1f), (0.2f, Molten, 1f), (0.55f, Ember, 0.7f), (1f, Soot, 0f)));
            Sphere(fire, radius);
            Grow(fire, 0.5f, 1.9f);
            // The body of the ball: opaque billows going from orange to black smoke, so it holds against a bright sky.
            var body = Particles(Up(go, "Billows"), s_smoke, false, 0f, count * 2 / 3, new Vector2(1.6f, 2.6f), new Vector2(1f, 3.5f), size * 0.9f,
                                 -rise * 0.6f, 60f, Ramp((0f, Molten, 0.95f), (0.2f, new Color(0.85f, 0.35f, 0.08f), 0.9f), (0.45f, new Color(0.25f, 0.12f, 0.06f), 0.8f), (1f, Dark, 0f)));
            Sphere(body, radius * 0.8f);
            Grow(body, 0.5f, 2f);
            var order = fire.GetComponent<ParticleSystemRenderer>();
            order.sortingOrder = 1;     // the glowing fire over the billows
        }

        static void DustRing(GameObject go, float to, float life, float alpha)
        {
            var ps = Particles(Child(go, "DustRing"), s_flat, false, 0f, 1, new Vector2(life, life), Vector2.zero, Vector2.one, 0f, 0f,
                               Ramp((0f, Dust, alpha), (1f, Dust, 0f)));
            Flat(ps);
            Grow(ps, 1f, to);
            var low = Particles(Child(go, "DustCloud"), s_smoke, false, 0f, 14, new Vector2(1.5f, 2.5f), new Vector2(6f, 12f), new Vector2(2f, 3f),
                                0f, 0f, Ramp((0f, Dust, alpha), (1f, Ash, 0f)));
            var shape = low.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Circle;
            shape.radius = 0.5f;
            shape.rotation = new Vector3(90f, 0f, 0f);
            Grow(low, 0.6f, 2.5f);
            var limit = low.limitVelocityOverLifetime;
            limit.enabled = true;
            limit.limit = 1f;
            limit.dampen = 0.15f;
        }

        /// <summary>The wreck burning for <paramref name="seconds"/>: flames and a column of black smoke.</summary>
        static void Burning(GameObject go, float seconds, float flameRate, float smokeRate, float width)
        {
            var flames = Particles(Up(go, "Flames"), s_fire, false, 0f, 0, new Vector2(0.5f, 1f), new Vector2(1f, 2.5f), new Vector2(1.2f, 2.2f) * width,
                                   -0.2f, 15f, Ramp((0f, Hot, 0f), (0.1f, Molten, 0.9f), (0.5f, Ember, 0.6f), (1f, Soot, 0f)));
            Sphere(flames, 1f * width);
            For(flames, seconds, flameRate);
            Delay(flames, 0.3f);
            Grow(flames, 1f, 0.3f);
            var column = Particles(Up(go, "Column"), s_smoke, false, 0f, 0, new Vector2(4f, 6f), new Vector2(2f, 4f), new Vector2(2f, 3f) * width,
                                   -0.05f, 8f, Ramp((0f, Dark, 0f), (0.1f, Dark, 0.7f), (0.6f, new Color(0.2f, 0.19f, 0.18f), 0.45f), (1f, Ash, 0f)));
            Sphere(column, 0.8f * width);
            For(column, seconds, smokeRate);
            Delay(column, 0.5f);
            Grow(column, 0.6f, 3.5f);
            var main = column.main;
            main.maxParticles = 600;
            var embers = Particles(Up(go, "Embers"), s_add, false, 0f, 0, new Vector2(1f, 2.5f), new Vector2(2f, 5f), new Vector2(0.05f, 0.12f),
                                   -0.1f, 25f, Ramp((0f, Hot, 1f), (0.4f, Molten, 1f), (1f, Ember, 0f)));
            For(embers, seconds, 10f);
            Waver(embers, 1f, 0.6f);
        }

        static GameObject MechDeath()
        {
            var go = new GameObject("MechDeath");
            Flash(go, "Flash", Hot, 26f, 0.3f, 3);
            Shock(go, Hot, 25f, 0.35f, 0.5f);
            Fireball(go, 40, new Vector2(6f, 10f), 0.25f, 2.5f);
            // The mushroom: a later, slower puff of fire climbing above the ball.
            var cap = Particles(Up(go, "Mushroom"), s_fire, false, 0f, 12, new Vector2(1.4f, 2.2f), new Vector2(8f, 13f), new Vector2(5f, 8f),
                                0f, 12f, Ramp((0f, Molten, 0.9f), (0.4f, Ember, 0.7f), (1f, Soot, 0f)));
            Delay(cap, 0.15f);
            Grow(cap, 0.6f, 2f);
            var capLimit = cap.limitVelocityOverLifetime;
            capLimit.enabled = true;
            capLimit.limit = 2f;
            capLimit.dampen = 0.08f;
            var capSmoke = Particles(Up(go, "MushroomSmoke"), s_smoke, false, 0f, 14, new Vector2(4f, 6f), new Vector2(6f, 10f), new Vector2(4f, 6f),
                                     -0.02f, 15f, Ramp((0f, Dark, 0f), (0.15f, Dark, 0.75f), (1f, Ash, 0f)));
            Delay(capSmoke, 0.5f);
            Grow(capSmoke, 0.7f, 2.5f);
            var capSmokeLimit = capSmoke.limitVelocityOverLifetime;
            capSmokeLimit.enabled = true;
            capSmokeLimit.limit = 1.5f;
            capSmokeLimit.dampen = 0.06f;
            DustRing(go, 30f, 1.2f, 0.55f);
            Sparks(Up(go, "Sparks"), "Sparks", Molten, 80, 70f, new Vector2(12f, 35f), new Vector2(0.8f, 1.8f), new Vector2(0.12f, 0.3f), 1.2f);
            var burning = Particles(Up(go, "BurningChunks"), s_fire, false, 0f, 10, new Vector2(1.5f, 2.5f), new Vector2(10f, 22f), new Vector2(0.6f, 1f),
                                    1.5f, 55f, Ramp((0f, Hot, 1f), (0.5f, Molten, 0.9f), (1f, Ember, 0f)));
            var trails = burning.trails;
            trails.enabled = true;
            trails.lifetime = 0.35f;
            trails.widthOverTrail = new ParticleSystem.MinMaxCurve(0.5f);
            trails.colorOverTrail = Ramp((0f, Molten, 0.8f), (1f, Soot, 0f));
            trails.dieWithParticles = true;
            burning.GetComponent<ParticleSystemRenderer>().trailMaterial = s_smoke;
            Particles(Up(go, "Chunks"), s_grit, false, 0f, 30, new Vector2(1.5f, 2.5f), new Vector2(8f, 20f), new Vector2(0.15f, 0.4f), 2f, 65f,
                      Ramp((0f, Dark, 1f), (1f, Dark, 0f)));
            Burning(go, 10f, 14f, 5f, 1.2f);
            Glow(go, Molten, 30f, 2.5f);
            return go;
        }

        /// <summary>
        /// Rides with each small piece the wreck throws (the "debris" part): a flame at the piece and a soft trail of dark
        /// smoke left in the air behind it, in place of the original pixel smoke. Lets go where the piece stops.
        /// </summary>
        static GameObject DebrisTrail()
        {
            var go = new GameObject("DebrisTrail");
            var flame = Particles(Child(go, "Flame"), s_fire, true, 30f, 0, new Vector2(0.15f, 0.3f), new Vector2(0f, 0.3f), new Vector2(0.5f, 0.9f),
                                  0f, 0f, Ramp((0f, Hot, 0.9f), (0.4f, Molten, 0.7f), (1f, Ember, 0f)));
            Grow(flame, 1f, 0.4f);
            var smoke = Particles(Child(go, "Smoke"), s_smoke, true, 0f, 0, new Vector2(1f, 1.8f), new Vector2(0f, 0.2f), new Vector2(0.5f, 0.8f),
                                  -0.03f, 0f, Ramp((0f, Soot, 0f), (0.08f, Soot, 0.55f), (0.5f, new Color(0.25f, 0.24f, 0.23f), 0.3f), (1f, Ash, 0f)));
            PerMetre(smoke, 3f, 300);
            Grow(smoke, 0.6f, 3f);
            Waver(smoke, 0.3f, 0.5f);
            return go;
        }

        static GameObject WreckBurst()
        {
            var go = new GameObject("WreckBurst");
            Flash(go, "Flash", Hot, 6f, 0.15f);
            Fireball(go, 10, new Vector2(1.8f, 3f), 0.2f, 0.6f);
            Sparks(Up(go, "Sparks"), "Sparks", Molten, 25, 60f, new Vector2(6f, 16f), new Vector2(0.5f, 1.1f), new Vector2(0.08f, 0.18f), 1.2f);
            Smoke(Up(go, "Smoke"), "Smoke", Dark, 0.6f, 6, 20f, new Vector2(1f, 3f), new Vector2(2.5f, 3.5f), new Vector2(1.6f, 2.4f), 0.06f, 3f);
            Glow(go, Molten, 10f, 1.2f);
            return go;
        }

        static GameObject SectionBlow()
        {
            var go = new GameObject("SectionBlow");
            Flash(go, "Flash", Hot, 5f, 0.12f);
            Fireball(go, 8, new Vector2(1.2f, 2.2f), 0.1f, 0.4f);
            Sparks(go, "Sparks", Molten, 50, 180f, new Vector2(6f, 18f), new Vector2(0.5f, 1.2f), new Vector2(0.07f, 0.16f), 1.4f);
            var spurt = Particles(Child(go, "Spurt"), s_add, false, 0f, 0, new Vector2(0.3f, 0.7f), new Vector2(3f, 9f), new Vector2(0.06f, 0.12f),
                                  1f, 40f, Ramp((0f, Hot, 1f), (0.4f, Molten, 1f), (1f, Ember, 0f)), true);
            For(spurt, 1.5f, 40f);
            Smoke(Up(go, "Smoke"), "Smoke", Dark, 0.55f, 5, 25f, new Vector2(1f, 2.5f), new Vector2(2f, 3f), new Vector2(1.2f, 1.8f), 0.05f, 3f);
            Glow(go, Molten, 8f, 1.2f);
            return go;
        }

        static GameObject BlowApart()
        {
            var go = new GameObject("BlowApart");
            Flash(go, "Flash", Hot, 10f, 0.18f);
            Shock(go, Hot, 14f, 0.3f, 0.45f);
            Fireball(go, 16, new Vector2(2.5f, 4f), 0.2f, 1f);
            DustRing(go, 18f, 1f, 0.5f);
            Particles(Up(go, "Chunks"), s_grit, false, 0f, 24, new Vector2(1.2f, 2f), new Vector2(6f, 16f), new Vector2(0.15f, 0.35f), 2f, 70f,
                      Ramp((0f, Dark, 1f), (1f, Dark, 0f)));
            Sparks(Up(go, "Sparks"), "Sparks", Molten, 40, 70f, new Vector2(8f, 24f), new Vector2(0.6f, 1.4f), new Vector2(0.1f, 0.22f), 1.2f);
            Glow(go, Molten, 18f, 2f);
            return go;
        }

        static GameObject VehicleDeath()
        {
            var go = new GameObject("VehicleDeath");
            Flash(go, "Flash", Hot, 10f, 0.18f, 3);
            Shock(go, Hot, 14f, 0.3f, 0.5f);
            Fireball(go, 24, new Vector2(4f, 6.5f), 0.3f, 1.4f);
            DustRing(go, 16f, 1f, 0.45f);
            Sparks(Up(go, "Sparks"), "Sparks", Molten, 45, 60f, new Vector2(10f, 26f), new Vector2(0.7f, 1.5f), new Vector2(0.1f, 0.24f), 1.2f);
            Particles(Up(go, "Chunks"), s_grit, false, 0f, 20, new Vector2(1.3f, 2.2f), new Vector2(8f, 18f), new Vector2(0.15f, 0.35f), 2f, 50f,
                      Ramp((0f, Dark, 1f), (1f, Dark, 0f)));
            Burning(go, 8f, 10f, 3.5f, 0.8f);
            Glow(go, Molten, 20f, 2f);
            return go;
        }
    }
}
