using MW2.Formats;
using UnityEngine;

namespace MW2
{
    /// <summary>
    /// A flying shot: one slot of the projectile pool (0x1023c900, 255 x 0x6c bytes), started by
    /// Weapon_FireProjectile 0x1006c554 and moved by 0x10010eb7 / 0x10010f40 once per frame with dt in ticks:
    /// velocity = speed (+0x3C, units/tick) * direction; acceleration (0, -planet gravity * weapon gravity (+0x38), 0);
    /// position += (v + a * dt / 2) * dt (Motion_Integrate 0x100060a0); lifetime (+0x5C minus the slot's leftover
    /// ticks) runs down by dt and the shot vanishes below 1. Missile types (3, 4, 9, 10, 12) start RandomInt(200) - 100
    /// units off along the right and up axes of the direction (0x1006df73); unless weapon flag 0x40, every shot starts
    /// 2 x the model radius ahead of the muzzle. A shot tests the segment retained from its previous move (0x10018ad5), or the
    /// ground when it sinks 25 units below it; a unit hit takes the damage (+0x40) on the side the shot travels towards
    /// (heading difference within 90 degrees = rear, 0x10010f40) and heat +0x48 (target +0xdc4).
    /// Homing (0x10011a5e, lock-on missiles fired with a lock): acceleration = unit vector to the target's position
    /// (scaled by 0.95 inside 3000 units) minus the unit velocity; within 100 units it bursts (Detonate: no damage).
    /// Missiles leave smoke (Mw2Effects.Smoke) and every hit starts the weapon's impact effect (Mw2Effects.Spawn).
    /// With Multiple Lights each shot carries a point light and a ground glow (see Spawn).
    /// Approximations: model geometry/radius and per-object Unity update clocks remain adapters;
    /// the smoothed homing of weapon flag 0x10 is
    /// not ported.
    /// </summary>
    public class Mw2Projectile : MonoBehaviour
    {
        const float Units = PolyModel.UnitsToMeters;

        Mw2MechCombat _owner, _homing;
        WeaponDef _def;
        Vector3 _velocity;      // units per tick, Unity axes
        Vector3 _accel;         // units per tick^2
        int _life;
        double _clock;
        int _now;
        bool _proximity;
        ShotSlotPool _pool;
        int _slot = -1, _poolGeneration;

        /// <summary>Shots in flight (the pool 0x1023c900), for the AMS scan.</summary>
        public static readonly System.Collections.Generic.List<Mw2Projectile> Active = new System.Collections.Generic.List<Mw2Projectile>();
        /// <summary>Slot +0x60: distance (units) at which an AMS destroys the missile; -1 undecided (Shot_Clear), 0 never.</summary>
        [System.NonSerialized] public float KillDistance = -1f;
        public WeaponDef Def => _def;
        public Mw2MechCombat Owner => _owner;
        public Vector3 Velocity => _velocity;

        void OnEnable() => Active.Add(this);
        void OnDisable() { Active.Remove(this); ReleaseSlot(); }

        // Shot_Remove 0x10011d40 -> Shot_Clear: make the slot reusable immediately,
        // before Unity performs its deferred GameObject destruction.
        void ReleaseSlot()
        {
            if (_slot < 0) return;
            if (_pool.Generation == _poolGeneration) _pool.Release(_slot);
            _slot = -1;
        }
        void Retire()
        {
            ReleaseSlot();
            Active.Remove(this);
            if (s_followed == this) s_followed = null;
            Destroy(gameObject);
        }

        /// <summary>
        /// Slot flag +0x48 | 0x8000 (Shot_Home 0x10011a5e within 100 units of its target, or an AMS kill in FUN_1000d08b):
        /// the next Shot_Move takes no collision test and "hits" the homing target's root object, whose model (the node 0
        /// mount) has no section, so Mech_ApplyDamage 0x10037b59 deals nothing; the target still gets the heat (+0xdc4)
        /// and a NARC mark, and the impact effect starts at the shot with the unit flags. Without a homing target the shot
        /// just bursts there with its plain impact effect.
        /// </summary>
        public void Detonate() => _proximity = true;

        /// <summary>
        /// DAT_1008eb70: the player's last shot when it is a missile type (3, 4, 9, 10, 12), else none
        /// (Weapon_FireProjectile 0x1006c554).
        /// </summary>
        public static Mw2Projectile LastPlayerMissile;
        /// <summary>DAT_1008eb74: the shot the missile camera follows (slot flag +0x5c), none once it is gone.</summary>
        static Mw2Projectile s_followed;

        /// <summary>FUN_10012e37 0x10012e37: follow the player's last missile; false when there is none.</summary>
        public static bool FollowLastMissile()
        {
            s_followed = LastPlayerMissile != null ? LastPlayerMissile : null;
            return s_followed != null;
        }

        /// <summary>FUN_10012e05 0x10012e05: the followed shot while it flies, else null.</summary>
        public static Mw2Projectile Followed => s_followed != null ? s_followed : null;

        /// <summary>
        /// The missile camera of Shot_Move 0x10010f40 for the followed slot (DAT_100a65c8): the shot's position, heading
        /// Math_HeadingDeg of its velocity, pitch and roll 0.
        /// </summary>
        public Quaternion CameraRotation =>
            Quaternion.Euler(0f, _velocity.sqrMagnitude > 1e-9f ? Mathf.Atan2(_velocity.x, _velocity.z) * Mathf.Rad2Deg : transform.eulerAngles.y, 0f);

        public static void Spawn(Mw2MechCombat owner, WeaponDef def, Vector3 muzzle, Vector3 direction, int lifeTicks, Mw2MechCombat homing, Mw2ShotModels models, int slot)
        {
            var go = new GameObject(def.Name);
            go.transform.SetParent(models.transform, false);
            var shot = go.AddComponent<Mw2Projectile>();
            shot._pool = models.Pool; shot._slot = slot; shot._poolGeneration = models.Pool.Generation;
            if (owner != null && owner.IsPlayer) LastPlayerMissile = def.IsMissileType ? shot : null;
            shot._owner = owner;
            shot._def = def;
            shot._homing = homing;
            shot._life = lifeTicks;
            shot._velocity = direction * def.Speed;
            float planetGravity = owner != null && owner.TryGetComponent<Mw2MechController>(out var controller)
                ? controller.PlanetGravity : 0.0296f;
            shot._accel = new Vector3(0, -planetGravity * def.Gravity, 0);

            float radius = 0f;
            if (models != null)
                foreach (var part in models.Parts)
                {
                    if (part.Type != def.ProjectileType || part.Mesh == null) continue;
                    var child = new GameObject(part.Mesh.name);
                    child.transform.SetParent(go.transform, false);
                    child.transform.localScale = part.Scale;
                    child.AddComponent<MeshFilter>().sharedMesh = part.Mesh;
                    var r = child.AddComponent<MeshRenderer>();
                    var mats = (Material[])part.Materials.Clone();
                    if (Mw2TextureAnimator.Instance != null)
                        for (int i = 0; i < mats.Length; i++) mats[i] = Mw2TextureAnimator.Instance.Live(mats[i]);
                    r.sharedMaterials = mats;
                    r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    radius = Mathf.Max(radius, part.Mesh.bounds.extents.magnitude * Mathf.Max(part.Scale.x, Mathf.Max(part.Scale.y, part.Scale.z)));
                }

            var start = muzzle;
            if (def.IsMissileType)
            {
                // 0x1006df73: r * (right + up) with r = RandomInt(200) - 100 units.
                float r = (Random.Range(0, 200) - 100) * Units;
                var right = new Vector3(direction.z, 0, -direction.x);
                if (right.sqrMagnitude > 1e-6f) right.Normalize();
                var up = Vector3.Cross(direction, right);
                start += r * right + r * up;
            }
            if ((def.Flags & 0x40) == 0) start += direction * (2f * radius);
            go.transform.position = start;
            shot.Face();

            // 0x1006c554 with Multiple Lights on (DAT_100b1958): a point light (Light_Set flags 0x107: a cube of
            // +-10000 units, colour 0.2, 0.2, 0.2 or the model's palette colour / 128), moved to the shot by every
            // Shot_Move 0x10010f40 (Light_Set flag 1) and freed by Shot_Remove 0x10011d40; and a glow of type 2.
            Color32? glow = null;
            if (models != null)
                foreach (var g in models.Glows)
                    if (g.Type == def.ProjectileType) { glow = g.Rgb; break; }
            if (Mw2Options.MultipleLights)
            {
                var light = go.AddComponent<Mw2PointLight>();
                light.Radius = 10000f * Units;
                light.Color = glow.HasValue ? new Color(glow.Value.r / 128f, glow.Value.g / 128f, glow.Value.b / 128f) : new Color(0.2f, 0.2f, 0.2f);
            }
            if (Mw2Effects.Instance != null) Mw2Effects.Instance.StartShotGlow(go.transform, glow);
        }

        void Face()
        {
            // Models point along MW2 +Z, which is Unity +Z.
            if (_velocity.sqrMagnitude > 1e-6f) transform.rotation = Quaternion.LookRotation(_velocity.normalized, Vector3.up);
        }

        void Update()
        {
            _clock += Time.deltaTime * MechMotion.TicksPerSecond;
            int now = (int)_clock, dt = now - _now;
            _now = now;
            if (dt <= 0) return;

            Move(dt);
        }

        // Shot_Move 0x10010f40; dt is the frame's integer tick count.
        void Move(int dt)
        {
            _life -= dt;
            var pos = transform.position;

            // 10010f40: scene collision uses slot+24/+28/+2c -> current node,
            // including the retained previous coordinates on the first move after reuse.
            if (_slot < 0 || _pool.Generation != _poolGeneration) { Retire(); return; }
            var previous = _pool.Previous(_slot);
            var from = new Vector3(previous.X, previous.Y, previous.Z) * Units;
            _pool.Advance(_slot, new AiLeadPoint.Vector(pos.x / Units, pos.y / Units, pos.z / Units));
            if (_proximity) { Burst(pos); return; }
            var step = pos - from;
            float length = step.magnitude;
            if (length > 1e-5f && _owner != null && _owner.FirstHit(from, step / length, length, out var hit))
            {
                Hit(hit.point, hit.collider.GetComponentInParent<Mw2MechCombat>(), hit.collider.GetComponentInParent<Mw2Destructible>(), hit.collider);
                return;
            }
            var next = pos + (_velocity + _accel * (dt * 0.5f)) * (dt * Units);
            _velocity += _accel * dt;

            // Shot_Move 0x10010f40: below height 0 a shot more than 25 units under World_GroundHeight hits the ground
            // (FUN_10051c77 cuts the segment at that height).
            float ground = next.y < 0f ? Mw2Ground.Height(next) : 0f;
            if (next.y < 0f && next.y < ground - 25f * Units)
            {
                // 10051c77 cuts the retained ray at the ground plane without clamping
                // its length. A nearly horizontal ray keeps its current endpoint.
                var point = pos;
                if (Mathf.Abs(step.y) >= 1e-7f * Units)
                    point = Vector3.LerpUnclamped(from, pos, (ground - from.y) / step.y);
                Hit(point, null, null, null);
                return;
            }
            if (_life < 1) { Retire(); return; }

            transform.position = next;
            Face();
            if (_def.IsMissileType && Mw2Effects.Instance != null) Mw2Effects.Instance.Smoke(pos, next - pos);     // 0x1004dcc4
            if (_homing != null) Steer(next);
        }

        void Steer(Vector3 pos)
        {
            if (_homing.Destroyed) { _homing = null; _accel = Vector3.zero; return; }
            var d = (_homing.ModelCentre - pos) / Units;
            float distance = d.magnitude;
            if (distance <= 100f) _proximity = true;
            if (distance < 1e-3f) return;
            d /= distance;
            if (distance < 3000f) d *= 0.95f;
            _accel = d - _velocity.normalized;
        }

        void Burst(Vector3 point)
        {
            var unit = _homing != null && !_homing.Destroyed ? _homing : null;
            int flags = 0;
            if (unit != null)
            {
                if ((_def.Flags & 0x80) != 0) MarkNarc(unit);
                if (_def.HeatToTarget != 0f) unit.AddHeat(_def.HeatToTarget);
                flags = 0x100 | (unit.IsPlayer ? 0x4000 : 0);
            }
            var effects = Mw2Effects.Instance;
            if (effects != null) effects.Spawn(_def.ImpactEffect | flags, point, unit != null ? unit.transform : null, null, _owner);
            Retire();
        }

        /// <summary>
        /// Shot_Move 0x10010f40: a weapon with flag 0x80 (NARC, types 28 / 61) marks the unit (+0xe42 | 1); the computer
        /// says 0x24 when the player fired it, else 0x25 when the player was marked.
        /// </summary>
        void MarkNarc(Mw2MechCombat unit)
        {
            unit.Narced = true;
            var voice = Mw2Audio.Instance;
            if (voice != null && _owner != null && _owner.IsPlayer) voice.Computer(0x24);
            else if (voice != null && unit.IsPlayer) voice.Computer(0x25);
        }

        void Hit(Vector3 point, Mw2MechCombat unit, Mw2Destructible structure, Collider part)
        {
            var effects = Mw2Effects.Instance;
            // Shot_Move 0x10010f40: a detached model (flags 0x50) sends the weapon's +0x54 damage
            // straight to Part_Damage 0x10015082, rather than to a GP or GT.
            bool debrisHit = unit == null && structure == null && effects != null && effects.HitPart(part, _def.Damage);
            if (unit != null && !unit.Destroyed)
            {
                // Side: the point the shot comes from, so a shot travelling the way the target faces hits the rear.
                if ((_def.Flags & 0x80) != 0) MarkNarc(unit);
                unit.ApplyDamage(_def.Damage, point, point - _velocity.normalized * 10f, _owner, part);
                if (_def.HeatToTarget != 0f) unit.AddHeat(_def.HeatToTarget);
            }
            else if (structure != null) structure.ApplyDamage(_def.Damage, _owner);
            // 0x10011d40 -> 0x10011ea8: impact effect +0x14 with the hit flags (0x100 unit, 0x4000 the player, 0x200
            // building, 0x400 ground or other scenery).
            int flags = unit != null ? 0x100 | (unit.IsPlayer ? 0x4000 : 0) : structure != null ? 0x200 : debrisHit ? 0 : 0x400;
            if (effects != null) effects.Spawn(_def.ImpactEffect | flags, point, unit != null ? unit.transform : null, null, _owner);
            Retire();
        }
    }
}
