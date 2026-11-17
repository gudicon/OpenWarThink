using System.Collections.Generic;
using MW2.Formats;
using UnityEngine;

namespace MW2
{
    /// <summary>
    /// A mod laser beam (WeaponLook "laser") for one shot: one or more flashes of a wavering beam (shader MW2/ModBeam) from
    /// the muzzle to the far end, each with lights along it. Only a picture: it never tests for a hit on its own except, with
    /// reach "instant", one ray for where to draw to (Mw2MechCombat.FirstHit, a query) and the ground under the line; damage,
    /// heat, rate, range, speed and the hit are those of the game's shot (Mw2Projectile), whatever the beam shows.
    ///
    /// Timeline. Reach "shot": the far end is the flying shot, so the first flash grows with it at the shot's speed, at full
    /// strength, until the shot hits or runs out (End); from then the first flash goes out over its time and further pulses
    /// follow Interval apart. Reach "instant": the far end is found at once and every flash starts from the moment of firing.
    /// A flash going out: progress 1 -> 0 over its time (thinning, breaking up, dimming as the look says), brightness up over
    /// Appear. Limits: at most <see cref="MaxFlashes"/> flashes drawn at once (the oldest goes first), at most 32 pieces along
    /// one strip, at most 8 lights a flash and <see cref="MaxLights"/> laser lights in all (the shaders take the 16 nearest).
    /// Port addition with no counterpart in the original program.
    /// </summary>
    public class Mw2ModLaser : MonoBehaviour
    {
        public const int MaxFlashes = 48, MaxLights = 32;
        const float SafetySeconds = 15f;

        sealed class Flash
        {
            public Mw2ModLaser Owner;
            public float Start = -1f;       // realtime-independent game time; below 0: not started
            public bool Growing;            // the first flash of reach "shot" while the shot flies
            public GameObject Go;
            public Mesh Mesh;
            public MeshRenderer Renderer;
            public float Seed;
            public readonly List<Mw2ModLight> Lights = new List<Mw2ModLight>();
            public bool Done;
        }

        static readonly List<Flash> s_flashes = new List<Flash>();
        static int s_lights;
        static Material s_material;
        static MaterialPropertyBlock s_block;

        WeaponLook.LaserLook _look;
        Vector3 _from, _direction, _head;
        Mw2ModWeaponView.Muzzle _muzzle;
        Transform _shot;
        bool _ended, _instant;
        float _fired, _origin = -1f;        // _origin: when the flashes' timeline starts (end of growth, or firing)
        readonly List<Flash> _mine = new List<Flash>();

        /// <summary>Starts the beam of a shot. <paramref name="reach"/>: the farthest the shot can fly (metres).</summary>
        /// <paramref name="from"/>: the muzzle on the firing part; the beam's start follows it every frame.
        public static Mw2ModLaser Fire(WeaponLook.LaserLook look, Mw2ModWeaponView.Muzzle from, Vector3 direction, float reach, Transform shot, Mw2MechCombat owner)
        {
            if (direction.sqrMagnitude < 1e-8f || from == null) return null;
            direction.Normalize();
            var muzzle = from.Position;
            var go = new GameObject("mod laser");
            var laser = go.AddComponent<Mw2ModLaser>();
            laser._look = look;
            laser._muzzle = from;
            laser._from = muzzle;
            laser._direction = direction;
            laser._shot = shot;
            laser._fired = Time.time;
            laser._instant = look.Reach == WeaponLook.LaserLook.ReachInstant;
            if (laser._instant)
            {
                laser._head = Aim(owner, muzzle, direction, reach);
                laser._ended = true;
                laser._origin = laser._fired;
            }
            else
            {
                laser._head = shot != null ? shot.position : muzzle;
                var first = laser.Add();
                first.Start = laser._fired;
                first.Growing = true;
            }
            for (int k = laser._mine.Count; k < look.Pulses; k++) laser.Add();
            laser.LateUpdate();
            return laser;
        }

        /// <summary>The game's shot hit or ran out at <paramref name="point"/>: the far end stops there (reach "shot").</summary>
        public void End(Vector3 point)
        {
            if (_ended) return;
            _ended = true;
            _head = point;
            _origin = Time.time;
        }

        /// <summary>
        /// Reach "instant": the first thing along the line the shot could hit (the same query the shot makes each move), or
        /// the ground where the line goes 25 units under it below height 0 (as Shot_Move 0x10010f40 tests), else the reach.
        /// </summary>
        internal static Vector3 Aim(Mw2MechCombat owner, Vector3 from, Vector3 direction, float reach)
        {
            float best = reach;
            if (owner != null && owner.FirstHit(from, direction, reach, out var hit)) best = Mathf.Min(best, hit.distance);
            const float step = 4f, under = 25f * PolyModel.UnitsToMeters;
            bool Below(float d)
            {
                var p = from + direction * d;
                return p.y < 0f && p.y < Mw2Ground.Height(p) - under;
            }
            for (float d = step; d < best; d += step)
            {
                if (!Below(d)) continue;
                float lo = d - step, hi = d;
                for (int i = 0; i < 12; i++) { float mid = (lo + hi) * 0.5f; if (Below(mid)) hi = mid; else lo = mid; }
                best = hi;
                break;
            }
            return from + direction * best;
        }

        Flash Add()
        {
            var f = new Flash { Owner = this, Seed = Mw2WeaponLooks.RandomValue() * 50f };
            f.Go = new GameObject("mod laser flash");
            f.Go.transform.SetParent(transform, false);
            f.Mesh = new Mesh { name = "mod laser" };
            f.Mesh.MarkDynamic();
            f.Go.AddComponent<MeshFilter>().sharedMesh = f.Mesh;
            f.Renderer = f.Go.AddComponent<MeshRenderer>();
            f.Renderer.sharedMaterial = Material();
            f.Renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            f.Renderer.receiveShadows = false;
            f.Renderer.enabled = false;
            _mine.Add(f);
            s_flashes.Add(f);
            while (s_flashes.Count > MaxFlashes) Finish(s_flashes[0]);
            return f;
        }

        static Material Material()
        {
            if (s_material != null) return s_material;
            var shader = Shader.Find("MW2/ModBeam");
            if (shader == null) { Debug.LogWarning("MW2 mods: the laser shader MW2/ModBeam is missing"); shader = Shader.Find("Sprites/Default"); }
            return s_material = new Material(shader) { name = "mod laser", hideFlags = HideFlags.DontSave };
        }

        static void Finish(Flash f)
        {
            if (f.Done) return;
            f.Done = true;
            s_flashes.Remove(f);
            foreach (var l in f.Lights) if (l != null) { Destroy(l.gameObject); s_lights--; }
            f.Lights.Clear();
            if (f.Mesh != null) Destroy(f.Mesh);
            if (f.Go != null) Destroy(f.Go);
        }

        void OnDestroy()
        {
            foreach (var f in _mine) Finish(f);
        }

        void LateUpdate()
        {
            float now = Time.time;
            if (_muzzle != null) _from = _muzzle.Position;
            if (!_ended)
            {
                if (_shot != null) _head = _shot.position;
                else End(_head);            // the shot went without telling (mission ended)
            }
            if (!_ended && now - _fired > SafetySeconds) End(_head);

            float flashTime = _look.FlashTime;
            bool alive = false;
            for (int k = 0; k < _mine.Count; k++)
            {
                var f = _mine[k];
                if (f.Done) continue;
                if (f.Growing && _ended) { f.Growing = false; f.Start = _origin; }
                if (!f.Growing && f.Start < 0f && _origin >= 0f) f.Start = _origin + k * _look.Interval;
                alive = true;
                if (f.Start < 0f || now < f.Start) { f.Renderer.enabled = false; continue; }
                float age = now - f.Start;
                float progress = f.Growing ? 1f : 1f - age / flashTime;
                if (progress <= 0f) { Finish(f); continue; }
                float appear = _look.Appear > 0f ? Mathf.Clamp01((now - (k == 0 ? _fired : f.Start)) / _look.Appear) : 1f;
                Draw(f, progress, appear);
            }
            if (!alive) Destroy(gameObject);
        }

        void Draw(Flash f, float progress, float appear)
        {
            var to = _head;
            float length = Vector3.Distance(_from, to);
            if (length < 0.01f) { f.Renderer.enabled = false; return; }
            BuildStrip(f.Mesh, _from, to, length, _look.Width * _look.Room * 0.5f);
            if (s_block == null) s_block = new MaterialPropertyBlock();
            var b = s_block;
            b.Clear();
            var l = _look;
            var core = l.Core;
            b.SetColor("_Color", new Color(core.R, core.G, core.B, 1f));
            b.SetColor("_OutlineColor", new Color(l.Color.R, l.Color.G, l.Color.B, 1f));
            b.SetFloat("_HalfHeight", l.Width * l.Room * 0.5f);
            b.SetFloat("_Thickness", 1f / l.Room);
            b.SetFloat("_OutlineThickness", l.Halo / l.Room);
            b.SetFloat("_Glow", l.Glow);
            b.SetFloat("_OutlineGlow", l.HaloGlow);
            b.SetFloat("_Energy", l.Waver);
            b.SetFloat("_Roughness", l.Roughness);
            b.SetFloat("_Frequency", l.Frequency);
            b.SetFloat("_Speed", l.Speed);
            b.SetFloat("_Strands", l.Strands);
            b.SetFloat("_StrandDifference", l.StrandDifference);
            b.SetFloat("_Progress", progress);
            b.SetFloat("_Thin", l.Thin);
            b.SetFloat("_BreakUp", l.BreakUp);
            b.SetFloat("_Dim", l.Dim);
            b.SetFloat("_EdgeSize", Mathf.Max(l.Edge, 1e-4f));
            b.SetFloat("_NoiseScaleX", length / l.Ripple);
            b.SetFloat("_Seed", f.Seed);
            b.SetFloat("_Intensity", appear);
            b.SetVector("_NearFade", new Vector4(l.NearFrom, l.NearTo, 0f, 0f));
            f.Renderer.SetPropertyBlock(b);
            f.Renderer.enabled = true;
            PlaceLights(f, to, length, progress * appear);
        }

        /// <summary>The strip: pairs of vertices on the line (POSITION), the line's direction (NORMAL), uv x along, y across.</summary>
        static void BuildStrip(Mesh mesh, Vector3 from, Vector3 to, float length, float halfHeight)
        {
            int segments = Mathf.Clamp(Mathf.CeilToInt(length / 15f), 1, 32);
            int n = (segments + 1) * 2;
            var vertices = new Vector3[n];
            var normals = new Vector3[n];
            var uvs = new Vector2[n];
            var along = (to - from) / length;
            for (int i = 0; i <= segments; i++)
            {
                float x = i / (float)segments;
                var p = Vector3.LerpUnclamped(from, to, x);
                vertices[i * 2] = vertices[i * 2 + 1] = p;
                normals[i * 2] = normals[i * 2 + 1] = along;
                uvs[i * 2] = new Vector2(x, 0f);
                uvs[i * 2 + 1] = new Vector2(x, 1f);
            }
            var triangles = new int[segments * 6];
            for (int i = 0; i < segments; i++)
            {
                int v = i * 2, t = i * 6;
                triangles[t] = v; triangles[t + 1] = v + 1; triangles[t + 2] = v + 2;
                triangles[t + 3] = v + 1; triangles[t + 4] = v + 3; triangles[t + 5] = v + 2;
            }
            mesh.Clear();
            mesh.vertices = vertices;
            mesh.normals = normals;
            mesh.uv = uvs;
            mesh.triangles = triangles;
            var bounds = new Bounds((from + to) * 0.5f, Vector3.zero);
            bounds.Encapsulate(from); bounds.Encapsulate(to);
            bounds.Expand(halfHeight * 2f);
            mesh.bounds = bounds;
        }

        /// <summary>Lights from the muzzle to the far end, kept a little inside both, one per Spacing metres (2 .. Max).</summary>
        void PlaceLights(Flash f, Vector3 to, float length, float strength)
        {
            var lights = _look.Lights;
            if (lights == null || lights.Max <= 0 || lights.Range <= 0f || lights.Intensity <= 0f) return;
            float spacing = lights.Spacing > 0f ? lights.Spacing : 2f * lights.Range;
            int want = lights.Max == 1 ? 1 : Mathf.Clamp(Mathf.CeilToInt(length / spacing) + 1, 2, lights.Max);
            while (f.Lights.Count < want && s_lights < MaxLights)
            {
                var go = new GameObject("mod laser light");
                go.transform.SetParent(f.Go.transform, false);
                var light = go.AddComponent<Mw2ModLight>();
                var c = lights.Color ?? _look.Color;
                light.Color = new Color(c.R, c.G, c.B);
                light.Range = lights.Range;
                f.Lights.Add(light);
                s_lights++;
            }
            int count = f.Lights.Count;
            float inset = Mathf.Min(length * 0.1f, 2f);
            for (int i = 0; i < count; i++)
            {
                float x = count == 1 ? 1f : i / (float)(count - 1);
                float d = length > inset * 2f ? inset + (length - inset * 2f) * x : length * x;
                f.Lights[i].transform.position = _from + (to - _from) * (d / length);
                f.Lights[i].Intensity = lights.Intensity * strength;
            }
        }
    }
}
