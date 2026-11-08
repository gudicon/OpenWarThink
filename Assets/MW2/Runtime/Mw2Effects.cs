using System.Collections.Generic;
using System.Linq;
using MW2.Formats;
using UnityEngine;

namespace MW2
{
    /// <summary>
    /// Explosions, debris and smoke. Lives on the mission root.
    ///
    /// Explosions: the pool of 255 slots (0x102434a0, 0x2C bytes) filled from the shot file's XPLO chunks, each slot
    /// holding a model (with its BWD node scale) and its own animated texture key for one effect type of the table at
    /// 0x1009bb08 (EffectTable). 0x10011ea8 turns an effect code (weapon +0x14 ORed with the hit flags 0x100 unit, 0x200
    /// building, 0x400 ground, 0x4000 the player was hit) into an effect type, takes a free slot of that type, places its
    /// model at the point, restarts the key at frame 0 with the record's mode (+0x1C: 2 = once) and keeps it for +0x00
    /// ticks (0x1001280e counts it down by dt, then hides the model and stops the key). Types 23..33 started right after a
    /// shot (0x10011e79, weapon +0x18 / +0x1C) sit on the firing muzzle (0x1006df1d). With record flag 1 and a unit hit
    /// the model rides on the unit at its origin. Missile types 3, 4, 0x22, 0x23 also start code 0x10b (debris).
    ///
    /// Debris (type 0xb, 0x1001314a): up to 16 pieces for flag 0x1000, 4 for a unit or building hit, else 8, from the free
    /// CHUNKER slots, each for type 0xb's duration. Physics (0x10014b00 / 0x10014bdf / 0x100150c7, 128 slots at 0x10263360):
    /// velocity (g, g + 1024, g) / 1024 * 4 units per tick and spin g * 0.5 / 1024 per tick with g from the normal table of
    /// InitRandom 0x100515d0 (sum of 30 rand(), sigma 1024); gravity from PLNT; per frame the bottom (centre - radius / 2)
    /// integrates with Motion_Integrate and on reaching the ground (rising: stop) bounces with vy = -vy / 2, half the time
    /// also vy / 2 and the horizontal speed reversed and halved, spin reversed and halved; once it lands slower than 0.55 it
    /// stops. A moving piece leaves debris smoke (0x1004e460).
    ///
    /// Smoke groups of 20 particles (0x1004dcc4 missiles, 0x1004e460 debris) along the segment moved this frame:
    /// particle k at start + (k + 2) * delta / s + jitter; every frame the group drifts by delta / t plus RandomInt(10)
    /// units up; colour palette[table[trunc(age / a * b)]]; 0x1004d57a draws each particle as a screen square of 3 pixels,
    /// or 5 - depth * 0.00025 (at least 2) up to 20000 units deep and 2 beyond (640x480 pixels).
    /// Missiles: s 16, t 8, jitter (RandomInt(10) - 5, RandomInt(50) - 25, RandomInt(10) - 5), 91 ticks,
    /// table 0x10112440 {0x15, 0x15, 63, 63, ..., 56, 56}[age / 90 * 17]; debris: s 20, t 2, jitter (RandomInt(4) - 2,
    /// RandomInt(50) - 25, RandomInt(4) - 2), 46 ticks, table 0x10112458 {0xde, 0xdd, ..., 0xd7}[age / 45 * 7].
    ///
    /// Light (0x10011ea8, codes 3, 0x22, 0x23 and 6 once a slot is taken): a point light of the pool (Light_Set flags 0x10f:
    /// the point, cube radius 20000 units, colour 0.6, 0.6, 0.2, life = the record's duration plus the frame's ticks,
    /// FUN_1002e761 frees it after that) and a ground glow (FUN_1005c2eb type 6 / FUN_1005c864: the point on the ground,
    /// radius 5000 units, the same life; FUN_1005c0e6 scales it as R - |R (life - 2 t) / life|, growing to R at half life;
    /// FUN_1005cb49 draws four quads to (+-r, +-r) and (+-1.5 r, 0) / (0, +-1.5 r), alpha 0x80 at the centre and 0 at the
    /// rim, colour 0x8096 = RGB (150, 128, 0); 0x6496 in view modes 12..15).
    /// Sun flash (record +0x10, PLNT +0x38 = 0): the sun becomes a position at the explosion and the ambient level drops by
    /// 10 (Mw2Atmosphere.Flash); a nearer explosion takes it over (horizontal distance to the camera); it comes back when
    /// that slot ends and the palette fade (FUN_1006b46f to palette mode + record +4 over duration / 2, then back) is over.
    /// Every PALG in MW2.PRJ repeats one palette over modes 0..3, 4..7, 8..11 and 12..15, so the fade changes no colour
    /// and only its length is kept.
    /// Combat variables (main menu): without Particles no smoke group starts (FUN_1004cd61) and switching it off removes
    /// the live ones (FUN_1004d25a(0)); without Multiple Lights no point light is taken (Light_Take 0x1002e843) and the
    /// ground glows are not drawn (0x1005d52c).
    /// Approximations: sound uses Unity attenuation; no scorch marks; the glow is drawn without fog and the IR colour is
    /// not used; the close-up variants for hits on the player's
    /// cockpit side (flag 0x2000) and area damage (+0x18, 0x10012b59 / 0x10012d1a) are not ported; nearby explosions of
    /// one type are limited as with DAT_100a1100 = 1 (at most two others within 500 units); debris lands on the
    /// walkable surfaces (World_GroundHeight 0x100186b1, Mw2Ground) and passes through walls; smoke is drawn opaque (the vertex
    /// alpha byte 0x32 of 0x1004d57a is not decoded); the smoke drift is per frame as in the original, so it depends on
    /// the frame rate.
    /// </summary>
    public class Mw2Effects : MonoBehaviour
    {
        [System.Serializable]
        public class Slot
        {
            public int Effect;
            public int Key;
            public GameObject Model;
            public float Radius;    // model radius in metres (debris bottom)
            [System.NonSerialized] public bool Active;
            [System.NonSerialized] public int Left;
            [System.NonSerialized] public Transform Follow;
            [System.NonSerialized] public Mw2MechCombat Owner;    // slot +0x28: who started it (area damage source)
            [System.NonSerialized] public bool Moving;
            [System.NonSerialized] public Vector3 Velocity;     // units per tick
            [System.NonSerialized] public Vector3 Spin;         // degrees per tick
        }

        public List<EffectDef> Defs = new List<EffectDef>();
        [Tooltip("PLNT +0x38 = 0: explosions move the sun to themselves (DAT_1008eb78).")]
        public bool SunFlashes = true;

        sealed class Glow
        {
            public Vector3 Point;
            public int Life, Time;
            public float Scale;
            public Color Color = new Color(150 / 255f, 128 / 255f, 0f);     // record +0x34, 0x8096 for explosions
            public Transform Shot;      // type 2: follows the projectile at height 0 (Glow_Update 0x1005c0e6)
        }

        sealed class FlashLight
        {
            public Mw2PointLight Light;
            public int Life, Time;
        }

        readonly List<Glow> _glows = new List<Glow>();
        readonly List<FlashLight> _lights = new List<FlashLight>();
        Slot _flashSlot;
        int _flashFadeLeft;
        Mesh _glowMesh;
        Material _glowMaterial;
        int _lastDt = 1;
        public List<Slot> Slots = new List<Slot>();
        public Color32[] Palette;
        public float PlanetGravity = 0.0296f;
        public static Mw2Effects Instance;

        const float Units = PolyModel.UnitsToMeters;
        double _clock;
        int _now;

        void OnEnable()
        {
            Instance = this;
            Camera.onPostRender += DrawSmoke;
        }

        void OnDisable()
        {
            if (Instance == this) Instance = null;
            Camera.onPostRender -= DrawSmoke;
        }

        /// <summary>0x10011ea8: start the effect of <paramref name="code"/> at <paramref name="point"/>.</summary>
        public void Spawn(int code, Vector3 point, Transform hitUnit = null, Transform muzzle = null, Mw2MechCombat owner = null)
        {
            if (code < 0) return;
            int id = code & 0xff;
            if (id >= 0x24) return;
            bool debrisToo = false;
            switch (id)
            {
                case 0: case 1: case 2:
                    if ((code & 0x2000) != 0) id = 0xe + id;
                    else if ((code & 0x400) != 0) id = 9;
                    break;
                case 3: case 0x22: case 0x23:
                    if ((code & 0x400) != 0) id = 0xc;
                    else if ((code & 0x2000) != 0) id = 0x11;
                    else if ((code & 0x200) != 0) id = 0x13;
                    debrisToo = true;
                    break;
                case 4:
                    if ((code & 0x400) != 0) id = 0xc;
                    else if ((code & 0x2000) != 0) id = 0x11;
                    else if ((code & 0x200) != 0) id = 0x14;
                    debrisToo = true;
                    break;
                case 5:
                    if ((code & 0x100) != 0) id = 8;
                    else if ((code & 0x400) != 0) id = 9;
                    else if ((code & 0x2000) != 0) id = 0x12;
                    break;
                case 6:
                    if ((code & 0x400) != 0) id = 0x15;
                    break;
                case 0xb:
                    Debris(point, (code & 0x1000) != 0 ? 0x10 : (code & 0x300) != 0 ? 4 : 8);
                    return;
            }
            int baseId = code & 0xff;
            bool light = baseId == 3 || baseId == 0x22 || baseId == 0x23 || baseId == 6;
            StartSlot(id, code, point, hitUnit, muzzle, owner, light);
            if (debrisToo) Spawn(0x10b, point, null, null, owner);     // 0x10011e0b
        }

        void StartSlot(int id, int code, Vector3 point, Transform hitUnit, Transform muzzle, Mw2MechCombat owner, bool light = false)
        {
            if (id >= Defs.Count) return;
            var def = Defs[id];
            if (!def.Flash && !def.UsesModel) return;

            Slot free = null;
            int nearby = 0;
            foreach (var s in Slots)
            {
                if (s.Effect != id) continue;
                if (!s.Active)
                {
                    if (free == null && (!def.UsesModel || s.Model != null)) free = s;
                }
                else if ((id < 0x17 || id > 0x21) && (def.Flags & 2) == 0 && s.Model != null &&
                         Vector3.Distance(s.Model.transform.position, point) < 500f * Units)
                {
                    // DAT_100a1100 = 1: up to two other explosions of the type nearby.
                    if (++nearby > 2) return;
                }
            }
            if (free == null) return;

            free.Active = true;
            free.Left = def.Duration;
            if (light) StartLight(point, def.Duration + _lastDt);
            if (def.Flash && SunFlashes) StartFlash(free, point, def.Duration);
            free.Follow = null;
            free.Moving = false;
            free.Owner = owner;
            // Effect_Start 0x10011ea8: record +0x08 is the SNDS id; +0x0C is a percent chance (-1 = always).
            if (def.Sound > 0 && (def.SoundChance == -1 || Random.Range(0, 100) < def.SoundChance) && Mw2Audio.Instance != null)
                Mw2Audio.Instance.Play(def.Sound, point);
            if (free.Model == null) return;
            var t = free.Model.transform;
            if ((def.Flags & 1) != 0)
            {
                if (hitUnit != null) { free.Follow = hitUnit; point = hitUnit.position; }
                else free.Left = 0xb5;
            }
            if (muzzle != null && id >= 0x17 && id <= 0x21) t.SetPositionAndRotation(muzzle.position, muzzle.rotation);
            else t.SetPositionAndRotation(point, Quaternion.identity);
            free.Model.SetActive(true);
            var anim = Mw2TextureAnimator.Instance;
            if (anim != null && free.Key >= 0)
            {
                anim.Restart(free.Key, 0);
                anim.SetSpeed(free.Key, def.TextureMode);
            }
        }

        /// <summary>The point light and ground glow of 0x10011ea8.</summary>
        void StartLight(Vector3 point, int life)
        {
            var go = new GameObject("MW2 explosion light");
            go.transform.SetParent(transform, false);
            go.transform.position = point;
            if (Mw2Options.MultipleLights)
            {
                var pl = go.AddComponent<Mw2PointLight>();
                pl.Radius = 20000f * Units;
                pl.Color = new Color(0.6f, 0.6f, 0.2f);
                _lights.Add(new FlashLight { Light = pl, Life = life });
            }
            else Destroy(go);
            if (_glows.Count < 0x40) _glows.Add(new Glow { Point = new Vector3(point.x, Mw2Ground.Height(point), point.z), Life = life });
        }

        /// <summary>
        /// Glow_Take 0x1005c2eb type 2 (Weapon_FireProjectile 0x1006c554, Multiple Lights on): a glow of radius 2000 units
        /// at the shot's x, z and height 0, moved with it every frame by Glow_Update 0x1005c0e6 and freed with the shot
        /// (Shot_Remove 0x10011d40). Colour (record +0x34): 0x404000 (red 0, green 0x40, blue 0x40), or the shot model's
        /// palette colour (Mw2ShotModels.Glow).
        /// </summary>
        public void StartShotGlow(Transform shot, Color32? rgb)
        {
            if (!Mw2Options.MultipleLights || _glows.Count >= 0x40) return;
            var c = rgb ?? new Color32(0, 0x40, 0x40, 255);
            _glows.Add(new Glow { Shot = shot, Life = -1, Scale = 2000f, Color = new Color(c.r / 255f, c.g / 255f, c.b / 255f) });
        }

        /// <summary>The sun flash of 0x10011ea8 (record +0x10).</summary>
        void StartFlash(Slot slot, Vector3 point, int duration)
        {
            var atmosphere = Mw2Atmosphere.Instance;
            var cam = Camera.main;
            if (atmosphere == null) return;
            if (atmosphere.Flashing && cam != null)
            {
                var c = cam.transform.position;
                float toNew = new Vector2(c.x - point.x, c.z - point.z).sqrMagnitude;
                var sun = atmosphere.FlashPosition;
                float toSun = new Vector2(sun.x - c.x, sun.z - c.z).sqrMagnitude;
                if (toSun <= toNew) return;
            }
            atmosphere.Flash(point);
            _flashSlot = slot;
            // FUN_1006b46f with mode 1: duration / 2 there, the same back.
            _flashFadeLeft = (duration >> 1) * 2;
        }

        void UpdateLights(int dt)
        {
            for (int i = _lights.Count - 1; i >= 0; i--)
            {
                var l = _lights[i];
                l.Time += dt;
                if (l.Life < l.Time)
                {
                    if (l.Light != null) Destroy(l.Light.gameObject);
                    _lights.RemoveAt(i);
                }
            }
            for (int i = _glows.Count - 1; i >= 0; i--)
            {
                var g = _glows[i];
                if (g.Life < 0)
                {
                    if (g.Shot == null) { _glows.RemoveAt(i); continue; }
                    var p = g.Shot.position;
                    g.Point = new Vector3(p.x, 0f, p.z);
                    continue;
                }
                g.Time += dt;
                if (g.Time >= g.Life) { _glows.RemoveAt(i); continue; }
                const float r = 5000f;
                g.Scale = r - Mathf.Abs((g.Life - g.Time * 2) * r / g.Life);
            }
        }

        void LateUpdate()
        {
            if (_glows.Count == 0 || !Mw2Options.MultipleLights) return;
            if (_glowMesh == null)
            {
                _glowMesh = new Mesh { name = "MW2 explosion glow" };
                _glowMesh.MarkDynamic();
                _glowMaterial = new Material(Shader.Find("Hidden/Internal-Colored")) { hideFlags = HideFlags.HideAndDontSave };
                _glowMaterial.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                _glowMaterial.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                _glowMaterial.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
                _glowMaterial.SetInt("_ZWrite", 0);
                _glowMaterial.renderQueue = 3000;
            }
            var vertices = new List<Vector3>();
            var colors = new List<Color>();
            var indices = new List<int>();
            foreach (var g in _glows)
            {
                if (g.Life < 0 && g.Shot == null) continue;
                var centre = new Color(g.Color.r, g.Color.g, g.Color.b, 0x80 / 255f);
                var rim = new Color(g.Color.r, g.Color.g, g.Color.b, 0f);
                float r = g.Scale * Units, far = (g.Scale / 2f + g.Scale) * Units;
                var c = g.Shot != null ? new Vector3(g.Shot.position.x, 0f, g.Shot.position.z) : g.Point;
                // FUN_1005cb49: four quads (centre, corner, tip, corner) to the +x, -z, -x and +z sides.
                Vector3[][] quads =
                {
                    new[] { c, c + new Vector3(r, 0, -r), c + new Vector3(far, 0, 0), c + new Vector3(r, 0, r) },
                    new[] { c, c + new Vector3(-r, 0, -r), c + new Vector3(0, 0, -far), c + new Vector3(r, 0, -r) },
                    new[] { c, c + new Vector3(-r, 0, r), c + new Vector3(-far, 0, 0), c + new Vector3(-r, 0, -r) },
                    new[] { c, c + new Vector3(r, 0, r), c + new Vector3(0, 0, far), c + new Vector3(-r, 0, r) },
                };
                foreach (var q in quads)
                {
                    int b = vertices.Count;
                    for (int k = 0; k < 4; k++) { vertices.Add(q[k]); colors.Add(k == 0 ? centre : rim); }
                    indices.Add(b); indices.Add(b + 1); indices.Add(b + 2);
                    indices.Add(b); indices.Add(b + 2); indices.Add(b + 3);
                }
            }
            _glowMesh.Clear();
            _glowMesh.SetVertices(vertices);
            _glowMesh.SetColors(colors);
            _glowMesh.SetTriangles(indices, 0);
            _glowMesh.bounds = new Bounds(Vector3.zero, Vector3.one * 100000f);
            Graphics.DrawMesh(_glowMesh, Matrix4x4.identity, _glowMaterial, 0);
        }

        /// <summary>Normal random value of the InitRandom table (sigma 1024).</summary>
        internal static float Gauss()
        {
            float u1 = 1f - Random.value, u2 = Random.value;
            return Mathf.Sqrt(-2f * Mathf.Log(u1)) * Mathf.Cos(2f * Mathf.PI * u2) * 1024f;
        }

        /// <summary>
        /// Debris_Throw 0x10014bdf (MW2 and Unity axes agree): velocity (g, g + 0x400, g) / (1024 * DAT_1009014c) * 4 units
        /// per tick, DAT_1009014c = mission gravity / 0.0296 (0x1001f411 area, _DAT_10087228); spin g * 0.5 / 1024 degrees
        /// per tick.
        /// </summary>
        void Throw(out Vector3 velocity, out Vector3 spin)
        {
            float scale = 1024f * (PlanetGravity / 0.0296f);
            velocity = new Vector3(Gauss() / scale * 4f, (Gauss() + 0x400) / scale * 4f, Gauss() / scale * 4f);
            spin = new Vector3(Gauss() * 0.5f / 1024f, Gauss() * 0.5f / 1024f, Gauss() * 0.5f / 1024f);
        }

        /// <summary>A part of a unit thrown off as debris (FUN_10014cd0 0x10014cd0: Debris_Start kind 2 + record 0x10264560).</summary>
        sealed class PartPiece
        {
            public Transform Node;
            public Mw2LodGroup Lods;
            public Vector3 Velocity, Spin, LocalCentre;
            public float Radius;
            public bool Moving;
            public int Born;
            public float Health; // record +0xc: 16 at Part_Throw 0x10014de1
        }

        readonly List<PartPiece> _parts = new List<PartPiece>();

        /// <summary>
        /// FUN_10014cd0 0x10014cd0: the node leaves its parent (FUN_1005aff2) and flies as debris kind 2 (Debris_Start
        /// 0x10014b00, Debris_Throw), which leaves no smoke (Debris_Move smokes kind 1 only); its record (0x80 at
        /// 0x10264560) removes it 0x712 ticks later (FUN_10014efc: FUN_10014eb1 and the model callback FUN_1001f411).
        /// Returns false when no record or debris slot is free: then the caller hides the node. Record +0xc starts with
        /// 16 hit points (0x10014de1); Part_Damage 0x10015082 destroys it strictly below zero via 0x10014fba.
        /// </summary>
        public bool ThrowPart(Transform node, Mw2LodGroup lods, float radius, Vector3 localCentre)
        {
            int moving = 0;
            foreach (var s in Slots) if (s.Active && s.Moving) moving++;
            foreach (var p in _parts) if (p.Moving) moving++;
            if (_parts.Count >= 0x80 || moving >= 0x80) return false;
            var piece = new PartPiece { Node = node, Lods = lods, Radius = radius, LocalCentre = localCentre, Moving = true, Born = _now, Health = 16f };
            Throw(out piece.Velocity, out piece.Spin);
            node.SetParent(null, true);
            // Shot_Move 0x10010f40 reaches Part_Damage only when the thrown model (flags 0x50)
            // remains in the segment test. The unit's hit collider was disabled while the section detached.
            var mesh = Mw2LodGroup.NodeMesh(node, lods);
            if (mesh != null && mesh.TryGetComponent<MeshCollider>(out var collider)) collider.enabled = true;
            _parts.Add(piece);
            return true;
        }

        /// <summary>FUN_10052148 rebuilds the living node tree. Reused Unity nodes must first leave the detached-part update list.</summary>
        internal void ReclaimUnitParts(HashSet<Transform> nodes) => _parts.RemoveAll(p => nodes.Contains(p.Node));

        /// <summary>Shot_Move 0x10010f40: a model of flags 0x50 carries its debris record index at +0x14.</summary>
        public bool HitPart(Collider collider, float damage)
        {
            if (collider == null) return false;
            for (int i = _parts.Count - 1; i >= 0; i--)
            {
                var p = _parts[i];
                if (p.Node == null) { _parts.RemoveAt(i); continue; }
                if (!collider.transform.IsChildOf(p.Node)) continue;
                DamagePart(i, damage);
                return true;
            }
            return false;
        }

        void UpdateParts(int dt)
        {
            for (int i = _parts.Count - 1; i >= 0; i--)
            {
                var p = _parts[i];
                if (p.Node == null) { _parts.RemoveAt(i); continue; }
                if (_now - p.Born > 0x712)
                {
                    HidePart(p);
                    _parts.RemoveAt(i);
                    continue;
                }
                if (!p.Moving) continue;
                var centre = p.Node.TransformPoint(p.LocalCentre);
                if (StepDebris(p.Node, centre, ref p.Velocity, ref p.Spin, p.Radius, dt)) p.Moving = false;
            }
        }

        /// <summary>FUN_10014eb1 0x10014eb1 / Part_ShotApart 0x10014fba: remove a detached part's visible model.</summary>
        void HidePart(PartPiece p)
        {
            foreach (var r in p.Node.GetComponentsInChildren<Renderer>(true))
                if (r.transform == p.Node || r.transform.parent == p.Node)
                {
                    if (p.Lods != null) p.Lods.Hide(r); else r.enabled = false;
                }
            foreach (var c in p.Node.GetComponentsInChildren<Collider>(true)) c.enabled = false;
        }

        /// <summary>Part_Damage 0x10015082: subtract damage and break only when remaining health is less than zero.</summary>
        void DamagePart(int index, float damage)
        {
            var p = _parts[index];
            p.Health -= damage;
            if (p.Health >= 0f) return;
            // Part_ShotApart 0x10014fba starts effect 7 at the model centre, then releases the debris record.
            var centre = p.Node.TransformPoint(p.LocalCentre);
            Spawn(7, centre);
            HidePart(p);
            _parts.RemoveAt(index);
        }

        /// <summary>0x1001314a: throw up to <paramref name="count"/> free CHUNKER pieces from the point.</summary>
        void Debris(Vector3 point, int count)
        {
            if (Defs.Count <= 0xb) return;
            int thrown = 0;
            foreach (var s in Slots)
            {
                if (thrown >= count) break;
                if (s.Effect != 0xb || s.Active || s.Model == null) continue;
                s.Active = true;
                s.Left = Defs[0xb].Duration;
                s.Follow = null;
                s.Moving = true;
                Throw(out s.Velocity, out s.Spin);
                s.Model.transform.SetPositionAndRotation(point, Quaternion.identity);
                s.Model.SetActive(true);
                thrown++;
            }
        }

        void Update()
        {
            _clock += Time.deltaTime * MechMotion.TicksPerSecond;
            int now = (int)_clock, dt = now - _now;
            _now = now;
            if (!Mw2Options.Particles) _smoke.Clear();

            if (dt > 0)
            {
                _lastDt = dt;
                if (_flashFadeLeft > 0) _flashFadeLeft -= dt;
                UpdateLights(dt);
            }
            if (dt > 0) UpdateParts(dt);
            if (dt > 0)
                foreach (var s in Slots)
                {
                    if (!s.Active) continue;
                    if (s.Moving && s.Model != null) MoveDebris(s, dt);
                    // 0x1001280e
                    if (s.Follow != null && s.Model != null) s.Model.transform.position = s.Follow.position;
                    if (s.Model != null && s.Effect < Defs.Count && Defs[s.Effect].AreaDamage) AreaDamage(s, dt);
                    s.Left -= dt;
                    if (s.Left >= 1) continue;
                    // The flashing slot waits for the palette fade before the sun comes back.
                    if (s == _flashSlot)
                    {
                        if (_flashFadeLeft > 0) continue;
                        _flashSlot = null;
                        if (Mw2Atmosphere.Instance != null) Mw2Atmosphere.Instance.EndFlash();
                    }
                    s.Active = false;
                    s.Moving = false;
                    s.Follow = null;
                    if (s.Model != null) s.Model.SetActive(false);
                    var anim = Mw2TextureAnimator.Instance;
                    if (anim != null && s.Key > 0) { anim.SetSpeed(s.Key, 0); anim.Restart(s.Key, 0); }
                }

            // 0x1004d4ac / 0x1004df4a: smoke groups drift every frame and die after their life.
            for (int i = _smoke.Count - 1; i >= 0; i--)
            {
                var g = _smoke[i];
                int age = _now - g.Born;
                if (age >= g.Life) { _smoke.RemoveAt(i); continue; }
                g.Origin += g.Drift + new Vector3(0, Random.Range(0, 10) * Units, 0);
                int index = (int)(age / g.AgeScale * g.Table.Length * g.TableScale);
                g.Color = g.Table[Mathf.Clamp(index, 0, g.Table.Length - 1)];
            }
        }

        /// <summary>Effects_Update 0x10012953 reads the live DAT_100aea44 +2 byte, including NetRoom changes.</summary>
        public static bool AreaDamageToUnits => Mw2Options.GameOptions.AreaDamageToUnits;

        Mw2Destructible[] _buildings;
        float[] _buildingRadius;

        /// <summary>
        /// 0x1001280e for an effect with +0x18 set, every update while it lasts. Reach r = the model radius (+0x40),
        /// growing as (duration - left) * 28 units for type 0x16 and * 24 for 0x23. Units (0x10012b59; option +2 or those
        /// two types): within their radius (+0xe1c) + r of the point, dt * (+0x20) damage to every section through
        /// 0x10037b59 (head half, x2 when shut down) and dt * (+0x24) heat. Buildings (0x10012d1a, always): within their
        /// radius + r, dt * (+0x20) damage (0x10013010). Distances from GP+50..58 (the unit origin), not the foot-space controller.
        /// Approximation: the building radius is that of its renderers. Detached parts use their current-LOD radius.
        /// </summary>
        void AreaDamage(Slot s, int dt)
        {
            var def = Defs[s.Effect];
            var at = s.Model.transform.position;
            float reach = s.Effect == 0x16 ? (def.Duration - s.Left) * 28f * Units
                        : s.Effect == 0x23 ? (def.Duration - s.Left) * 24f * Units
                        : s.Radius;
            float damage = dt * def.DamagePerTick, heat = dt * def.HeatPerTick;
            if (AreaDamageToUnits || s.Effect == 0x16 || s.Effect == 0x23)
                foreach (var unit in Mw2MechCombat.All.ToArray())
                {
                    if (unit == null || unit.Destroyed) continue;
                    float radius = unit.TryGetComponent<Mw2MechController>(out var mc) ? mc.CollisionRadius : 5f;
                    if (Vector3.Distance(unit.Origin, at) < radius + reach) unit.AreaHit(damage, heat, s.Owner);
                }
            if (_buildings == null)
            {
                _buildings = FindObjectsByType<Mw2Destructible>(FindObjectsSortMode.None);
                _buildingRadius = new float[_buildings.Length];
                for (int i = 0; i < _buildings.Length; i++)
                {
                    Bounds b = default;
                    bool any = false;
                    foreach (var r in _buildings[i].GetComponentsInChildren<Renderer>())
                        if (any) b.Encapsulate(r.bounds); else { b = r.bounds; any = true; }
                    _buildingRadius[i] = any ? b.extents.magnitude : 0f;
                }
            }
            for (int i = 0; i < _buildings.Length; i++)
            {
                var bld = _buildings[i];
                if (bld == null || bld.Destroyed) continue;
                if (Vector3.Distance(bld.transform.position, at) < _buildingRadius[i] + reach) bld.ApplyDamage(damage, s.Owner);
            }
            // Effect_AreaDamageDebris 0x100155f6: every active detached part whose model centre lies inside
            // its bounds radius plus the effect radius loses dt * the effect's damage-per-tick.
            for (int i = _parts.Count - 1; i >= 0; i--)
            {
                var p = _parts[i];
                if (p.Node == null) { _parts.RemoveAt(i); continue; }
                var centre = p.Node.TransformPoint(p.LocalCentre);
                if ((centre - at).sqrMagnitude <= (p.Radius + reach) * (p.Radius + reach)) DamagePart(i, damage);
            }
        }

        /// <summary>0x100150c7: one frame of a debris piece (positions in units; ground World_GroundHeight, Mw2Ground).</summary>
        void MoveDebris(Slot s, int dt)
        {
            var t = s.Model.transform;
            var start = t.position;
            bool stop = StepDebris(t, start, ref s.Velocity, ref s.Spin, s.Radius, dt);
            if (stop) s.Moving = false;
            else SmokeGroup.Start(this, start, t.position - start, debris: true);
        }

        /// <summary>
        /// Debris_Move 0x100150c7 without the smoke: the bottom (model centre - radius / 2) integrates with Motion_Integrate;
        /// at the ground while falling it bounces (vy = -vy / 2, half the time also vy / 2 and the horizontal speed reversed
        /// and halved, spin reversed and halved); a piece that was rising or lands slower than 0.55 stops. Returns true when
        /// it stops.
        /// </summary>
        bool StepDebris(Transform t, Vector3 centre, ref Vector3 velocity, ref Vector3 spin, float radius, int dt)
        {
            float bottom = centre.y / Units - radius / Units / 2f;
            float vy = velocity.y;
            bool rising = vy > 0f;
            float y = bottom + (vy - PlanetGravity * dt * 0.5f) * dt;     // Motion_Integrate
            vy -= PlanetGravity * dt;
            bool stop = false;
            float ground = vy <= 0f ? Mw2Ground.Height(centre) / Units : float.NegativeInfinity;
            if (vy <= 0f && y <= ground)
            {
                if (rising) stop = true;
                if (vy > -0.5524862f) stop = true;
                y = ground;
                vy = -(vy / 2f);
                if (Random.Range(0, 2) != 0)
                {
                    vy /= 2f;
                    velocity.x = -(velocity.x / 2f);
                    velocity.z = -(velocity.z / 2f);
                }
                spin = -(spin / 2f);
            }
            velocity.y = vy;
            t.position += new Vector3(velocity.x * dt, y - bottom, velocity.z * dt) * Units;
            t.Rotate(spin * dt, Space.Self);
            return stop;
        }

        // --- smoke ---

        class SmokeGroup
        {
            public Vector3 Origin, Drift;
            public readonly Vector3[] Offsets = new Vector3[20];
            public int Born, Life, Color;
            public int[] Table;
            public float AgeScale, TableScale;

            /// <summary>0x1004dcc4 (missile) / 0x1004e460 (debris).</summary>
            public static void Start(Mw2Effects fx, Vector3 start, Vector3 delta, bool debris)
            {
                var cam = Camera.main;
                if (cam == null || Vector3.Distance(start, cam.transform.position) > cam.farClipPlane) return;
                if (!Mw2Options.Particles) return;     // FUN_1004cd61: no particle group while DAT_1009db88 is 0
                var g = new SmokeGroup { Origin = start, Born = fx._now };
                var step = delta / (debris ? 20f : 16f);
                g.Drift = delta / (debris ? 2f : 8f);
                for (int k = 0; k < 20; k++)
                {
                    var jitter = debris
                        ? new Vector3(Random.Range(0, 4) - 2, Random.Range(0, 50) - 25, Random.Range(0, 4) - 2)
                        : new Vector3(Random.Range(0, 10) - 5, Random.Range(0, 50) - 25, Random.Range(0, 10) - 5);
                    g.Offsets[k] = step * (k + 2) + jitter * Units;
                }
                if (debris) { g.Life = 0x2e; g.Table = DebrisTable; g.AgeScale = 45f; g.TableScale = 7f / DebrisTable.Length; }
                else { g.Life = 0x5b; g.Table = MissileTable; g.AgeScale = 90f; g.TableScale = 17f / MissileTable.Length; }
                g.Color = g.Table[0];
                fx._smoke.Add(g);
            }
        }

        static readonly int[] MissileTable = { 0x15, 0x15, 63, 63, 62, 62, 61, 61, 60, 60, 59, 59, 58, 58, 57, 57, 56, 56 };
        static readonly int[] DebrisTable = { 0xde, 0xdd, 0xdc, 0xdb, 0xda, 0xd9, 0xd8, 0xd7 };
        readonly List<SmokeGroup> _smoke = new List<SmokeGroup>();
        static Material _smokeMaterial;

        /// <summary>0x1004dcc4: a missile smoke group along the segment <paramref name="start"/> + <paramref name="delta"/>.</summary>
        public void Smoke(Vector3 start, Vector3 delta) => SmokeGroup.Start(this, start, delta, debris: false);

        void DrawSmoke(Camera cam)
        {
            if (_smoke.Count == 0 || cam != Camera.main || Palette == null || Palette.Length < 256) return;
            if (_smokeMaterial == null)
            {
                _smokeMaterial = new Material(Shader.Find("Hidden/Internal-Colored")) { hideFlags = HideFlags.HideAndDontSave };
                _smokeMaterial.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
            }
            _smokeMaterial.SetPass(0);
            GL.PushMatrix();
            GL.LoadProjectionMatrix(cam.projectionMatrix);
            GL.modelview = cam.worldToCameraMatrix;
            GL.Begin(GL.QUADS);
            // Metres per 640x480 pixel at depth 1.
            float perPixel = 2f * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) / 480f;
            var right = cam.transform.right;
            var up = cam.transform.up;
            foreach (var g in _smoke)
            {
                float depth = Vector3.Dot(g.Origin - cam.transform.position, cam.transform.forward);
                if (depth <= 0f) continue;
                float units = depth / Units;
                float size = units <= 20000f ? Mathf.Max(2f, 5f - units * 0.00025f) : 2f;
                int index = g.Color == 0xd0 ? 0xdf : g.Color;
                GL.Color(Palette[index]);
                foreach (var o in g.Offsets)
                {
                    var p = g.Origin + o;
                    float d = Vector3.Dot(p - cam.transform.position, cam.transform.forward);
                    if (d <= 0f) continue;
                    float w = size * perPixel * d;
                    var r = right * w;
                    var u = up * w;
                    GL.Vertex(p);
                    GL.Vertex(p + r);
                    GL.Vertex(p + r - u);
                    GL.Vertex(p - u);
                }
            }
            GL.End();
            GL.PopMatrix();
        }
    }
}
