using System;
using System.Collections.Generic;
using System.Linq;
using MW2.Formats;
using UnityEngine;

namespace MW2
{
    /// <summary>
    /// Weapons, heat and armour of a unit. The weapon cycle is the one in mercsw.dll 0x1006bc57: ready (1) -> firing (2,
    /// "Burst" shots spaced by "IntervalTicks") -> waiting (3) for "RecycleTicks" -> ready, or straight back to firing
    /// while the trigger is held. Shots use Mw2Projectile and section damage from the hit point;
        /// Emission follows native EYEO/POFO convergence (0x1006dc4a/0x1006dd6d).
    /// </summary>
    public partial class Mw2MechCombat : MonoBehaviour
    {
        [Serializable]
        public class MountedWeapon
        {
            public WeaponDef Def;
            public int Section;
            public int Ammo;            // rounds of the weapon's pool (mech +0x1a6); -1 = energy weapon
            public int Pool = AmmoStore.NoPool;     // slot +0x20: the ammunition pool of its type (Mech_LoadMek 0x1002f9e0)
            public int HudGauge = -1;   // cockpit gauge of the weapon row (HudLayout.AssignWeaponRows)
            public int Group;           // fire group, slot +0xc (MEK, Shift+1..5)
            [NonSerialized] public int State = 1;
            [NonSerialized] public int Stamp;
            [NonSerialized] public int Timer;
            [NonSerialized] public int ShotsLeft;
            public int Id;              // MEK id (type * 100 + n), the item id in the section slots
            public bool Broken;         // slot state -1 after a critical hit (0x10036aa9)
            [NonSerialized] public bool Repeat;             // recycling to fire again (slot state 3 of 0x1006bc57)
            [NonSerialized] public Mw2MechCombat LockTarget;    // slot +0x18/+0x1C, taken when the burst starts
        }

        public static readonly List<Mw2MechCombat> All = new List<Mw2MechCombat>();

        public int Side;                // team side from STAR (FUN_1002a7c1 0x1002a7c1): 0 player's side, 1 enemy, 2 neutral
        public bool IsPlayer;
        public string DisplayName;
        public int GamepieceType = -1; // model BWD GP body +2 -> gamepiece +0 (World_LoadChunks 0x10070e92)
        [Tooltip("Mech +0xe00 = MGEO[0] in metres: the engine object's origin above the feet (death explosions start there).")]
        public float OriginHeight;
        /// <summary>MGEO[6], mech +0xe1c, in metres. Static GP units without ANIM
        /// also have this radius; a movement controller is not required by 0x10034973.</summary>
        public float ModelRadius;
        public float SphereRadius => TryGetComponent<Mw2MechController>(out var controller) ? controller.CollisionRadius : ModelRadius;
        [Tooltip("Mission file the unit was spawned from (ENS*/USR*), used by objectives that name that file.")]
        public string SourceFile;
        public int ObjectiveMask;   // GPS +0x18, expanded by FUN_10070e60 for Objective_AddTarget 0x1004045b
        [NonSerialized] public int IdentifiedTeams; // GP +0x16, teams that identified it (0x10067668); with flag 0x20 for FUN_100408e6 0x100408e6
        public int HeatSinks = 10;
        [Tooltip("PLNT temperature in degrees, loaded with the mission.")]
        public int PlanetTemperature;
        public List<MountedWeapon> Weapons = new List<MountedWeapon>();
        public int[] Front = new int[8], Rear = new int[8], Internal = new int[8], MaxInternal = new int[8];
        /// <summary>Bins and pools of the ammunition (mech +0x5c / +0x1a4, Mech_LoadMek 0x1002f9e0).</summary>
        public AmmoStore Ammunition = new AmmoStore();
        /// <summary>Section +0x26 flags from the MEK (0x100: an ammunition explosion stays in the section).</summary>
        public int[] SectionFlags = new int[8];
        /// <summary>The MEK the unit was loaded from and its name (GPS +0x25, DAT_100afa45: also the wreck's name).</summary>
        public byte[] MekBytes;
        public string MekName;
        /// <summary>Gamepiece flag +0xe42 &amp; 2: loaded from a MEK the shell wrote; Units_WriteMeks writes it back.</summary>
        public bool CareerUnit;

        /// <summary>Mek_WriteUnit 0x10058f90: the MEK this career unit leaves after the mission; null for other units.</summary>
        public byte[] WriteMek()
        {
            if (!CareerUnit || MekBytes == null) return null;
            var working = new Dictionary<int, int>();
            foreach (var w in Weapons) if (!w.Broken) working[w.Id] = w.Group;
            TryGetComponent<Mw2MechController>(out var controller);
            return MekWriter.Write(new MekWriter.Unit
            {
                Mek = MekLoadout.Decode(MekBytes), MekBytes = MekBytes, ArmorFactor = ArmorFactor,
                Front = Front, Rear = Rear, Internal = Internal, Items = SectionItems, WorkingWeapons = working,
                Ammo = Ammunition, SectionFlags = SectionFlags, JumpMp = controller != null ? controller.JumpMp : 0,
                GravityFactor = controller != null ? controller.PlanetGravity / 0.0296f : 1f
            });
        }

        /// <summary>Difficulty (game option +5, Mech_LoadMek 0x1002f9e0): armour x4 for the player, allies x3/x3/x4, enemies x1/x3/x4.</summary>
        public static int Difficulty = 1;
        [Tooltip("Armour multiplier applied at load (Mech_LoadMek): the HUD divides the armour by it.")]
        public int ArmorFactor = 1;
        /// <summary>Items left in each section's slots (MEK), hit by critical hits (0x10036aa9).</summary>
        [NonSerialized] public List<int>[] SectionItems;
        [NonSerialized] public bool[] SectionGone = new bool[8];
        // Hud_DamageGauges 0x10017bd1: +6 of each of the 34 player gauges.
        [NonSerialized] public int[] GaugeDamage = new int[HudSensorDamage.GaugeCount];
        readonly bool[] _armorBreachAnnounced = new bool[8];
        /// <summary>Speed factor [0x379] that scales the throttle (0x1003a449): 0.2 limping, 0 without a leg; crits take 0.1.</summary>
        [NonSerialized] public float SpeedFactor = 1f;
        [NonSerialized] public bool LegLost;    // [0x390] & 0x20
        [Tooltip("Per-section damage scale for the cockpit wireframe (HudLayout.DamageScale, Mech_LoadMek 0x1002f9e0).")]
        public int[] DamageScale = new int[8];
        public float Heat;
        /// <summary>
        /// Heat change of the last update for the dH/dT bar (0x100298fb): heat generated during the frame ([0x371], +0xdc4)
        /// minus ticks * sink rate per tick ([0x374], +0xdd0).
        /// </summary>
        [NonSerialized] public float HeatRate;
        float _heatThisFrame;
        public bool ShutDown;
        public bool Destroyed;

        [Header("Control")]
        public bool Trigger;
        public Mw2MechCombat Target;
        /// <summary>
        /// +0xe42 bit 1: hit by a NARC shot (weapon flag 0x80, Shot_Move 0x10010f40). Lock-on weapons lock it at once
        /// (Weapon_UpdateLock 0x1006d794); Mech_UpdateControl state 3 clears it once now - [0x36f] exceeds 0x712.
        /// </summary>
        [NonSerialized] public bool Narced;
        int _narcStamp;                 // [0x36f] while shut down
        [Tooltip("Shots hit when the target is within this angle of the torso's facing.")]
        public float FireCone = 12f;
        [Tooltip("Weapon the pilot has selected (mech +0xde0); the AI fires it alone, or all weapons in range for group fire.")]
        public int SelectedWeapon;
        [Tooltip("Powered down on orders (AI state 'shutdown', mech +0xdd4): no movement, no fire.")]
        public bool PoweredDown;
        public int Tons;
        /// <summary>Mech_LoadMek 0x1002f9e0 / FUN_1003107e: pilot +0x17c, preserved after damage.</summary>
        public int PilotCombatValue;

        /// <summary>
        /// Unit this one is attacking: the player's target while the player fires on it (0x1000b392 records "mech X has
        /// attacked mech Y"), an AI unit's target while it is in the attack state. Units attacked this way fight back (0x1000a058).
        /// </summary>
        [NonSerialized] public Mw2MechCombat Attacking;

        readonly List<int> _fireRequests = new List<int>();

        /// <summary>Chain fire (DAT_100a1128 = 1): the fire key shoots the selected weapon only; 0 (default) = group fire.</summary>
        public static bool ChainFire;

        // Player fire controls of 0x1006bc57 (control block +0x25 fire, +0x27 group fire, +0x28..+0x2a groups 0..2,
        // +0x2d alpha strike, +0x2c chain/group toggle) and the latch bit 1 of [0x390].
        bool _keyFire, _keyGroupFire, _keyGroups0, _keyGroups1, _keyGroups2, _keyCycleGroup, _keyAlpha, _keyToggle;
        bool _fireLatch, _groupFireHeld;

        double _clock;
        int _now;
        // Mech_InitMovement 0x100380d0 initializes +0xdbc to zero. Mech_UpdateControl 0x1003a449 and
        // Mech_UpdateHeat 0x10035a9b share this field for startup and thermal deadlines.
        int _heatStamp;                           // [0x36f], +0xdbc
        bool _overheated;                        // flag 4 of [0x390]: shutdown sequence running
        bool _override;                          // flag 8 of [0x390]: automatic shutdown overridden (O)

        /// <summary>Flag 4 of +0xe40: the shutdown sequence runs (heat went above 80).</summary>
        public bool ShutdownSequence => _overheated;
        /// <summary>Flag 8 of +0xe40: the automatic shutdown is overridden.</summary>
        public bool ShutdownOverridden => _override;
        bool _overheatAnnounced;                 // flag 0x1000 of [0x390]
        static bool _heatWarned;                 // DAT_100ad33c, "Heat level critical" said
        static int _heatWarnedAt;                // DAT_100ad330

        /// <summary>
        /// Heat model of mercsw.dll 0x10035a9b: heat += generated - multiplier * sinks * 0.000763 * dt per tick, with the
        /// sink rate [0x374] = MEK heat sinks * 0.00076293945 * HeatSinkMultiplier (0x1002f9e0); shut down (state 3)
        /// it cools twice as fast.
        /// Above 80 the shutdown sequence starts (flag 4; the player hears "Thermal threshold has been exceeded..."
        /// and MECOVHW1 once until the heat is back at 65); 0x43e ticks later, unless overridden (O), the mech shuts
        /// down ("Shutting down"). Back at 65 or below and 0x2d4 ticks after the shutdown it starts again (state 0).
        /// Between 65 and 80 with the heat rising the player hears "Heat level critical" once (again after 0x2d4 ticks
        /// below 40). Above 100: without override destroyed 0xe24 ticks after the sequence began; with override each
        /// update has a 3 in 4000/dt chance of 0x10035915 (an ammunition critical, else destruction).
        /// Jet heat is supplied by MechMotion; other movement heat is still omitted.
        /// </summary>
        void UpdateHeat(int now, int dt)
        {
            // Mech_UpdateHeat 0x10035a9b: without mw2dif.cfg +4 the player's heat stays 0.
            if (IsPlayer && !Mw2Options.GameOptions.PlayerHeat) { Heat = 0f; _heatThisFrame = 0f; HeatRate = 0f; return; }
            float sinkRate = HeatSinks * 0.00076293945f * HeatSinkMultiplier(PlanetTemperature, Difficulty, IsPlayer);
            HeatRate = _heatThisFrame - dt * sinkRate;
            float delta = _heatThisFrame - (ShutDown ? 2 : 1) * sinkRate * dt;
            _heatThisFrame = 0;
            Heat = Mathf.Max(0, Heat - (ShutDown ? 2 : 1) * sinkRate * dt);
            var audio = Mw2Audio.Instance;
            if (_overheated && !_override && !ShutDown && now - _heatStamp > 0x43e)
            {
                ShutDown = true;
                _heatStamp = now;
                _narcStamp = now;
                SetMobile(false);
                if (IsPlayer && audio != null) audio.Computer(0xd);
            }
            if (Heat > 100f)
            {
                if (!_override) { if (now - _heatStamp > 0xe24) { Die(this); return; } }
                else if (dt > 0 && UnityEngine.Random.Range(0, Mathf.Max(1, 4000 / dt)) < 3)
                {
                    HeatExplosion();
                    return;
                }
            }
            else if (Heat > 80f)
            {
                if (!_overheated)
                {
                    _heatStamp = now;
                    _overheated = true;
                    if (!_overheatAnnounced && IsPlayer)
                    {
                        _overheatAnnounced = true;
                        if (audio != null) { audio.Computer(3); audio.Play(0xfe, transform.position, true); }
                    }
                }
            }
            else if (Heat > 65f)
            {
                if (IsPlayer && !_heatWarned && delta > 0)
                {
                    _heatWarnedAt = now;
                    _heatWarned = true;
                    if (audio != null) audio.Computer(0);
                }
            }
            else if (!_overheated)
            {
                if (_heatWarned && now - _heatWarnedAt > 0x2d4 && IsPlayer && Heat < 40f) _heatWarned = false;
                _overheatAnnounced = false;
            }
            else
            {
                if (ShutDown && now - _heatStamp > 0x2d4)
                {
                    _overheated = false;
                    ShutDown = false;
                    SetMobile(true);
                    StartUp();
                }
                if (_override) _override = _overheated = false;
            }
        }

        /// <summary>Mech_HeatExplode 0x10035915: the first live ammo bin takes a critical; with none, destroy the unit.</summary>
        void HeatExplosion()
        {
            var hit = MechHeatExplosion.FirstLiveAmmo(SectionItems, SectionGone, Ammunition.Live);
            if (hit.section != 0)
            {
                Critical(hit.section, hit.slot, false, null);
                return;
            }
            if (IsPlayer && Mw2Audio.Instance != null) Mw2Audio.Instance.Computer(0);
            Die(this);
        }

        /// <summary>
        /// O, override automatic thermal shutdown (command 0x57 -> 0x100176cb): while the shutdown sequence runs and the
        /// mech is not shut down yet, MECBSOXX and "Shutdown sequence overridden".
        /// </summary>
        public void OverrideShutdown()
        {
            if (ShutDown || !_overheated || _override) return;
            _override = true;
            if (Mw2Audio.Instance != null)
            {
                Mw2Audio.Instance.Play(0xde, transform.position, true);
                Mw2Audio.Instance.Computer(2);
            }
        }

        /// <summary>
        /// S, manual shutdown / restart (commands 0x54 / 0x55 -> 0x1003b7dd): running -> "Shutting down", MECSHTD1 at the
        /// mech and shutdown; shut down without a running shutdown sequence (or with override) -> "Powering up..." and
        /// start-up.
        /// </summary>
        public void ToggleManualShutdown()
        {
            var audio = Mw2Audio.Instance;
            if (!ShutDown)
            {
                if (audio != null)
                {
                    audio.Computer(0xd);
                    audio.Play(0x107, transform.position, false, 100, Mw2Cockpit.PlayerInCockpit);
                }
                ShutDown = true;
                // 0x1003b7dd enters state 3 without setting [0x36f], which still holds the lock timer of
                // Weapon_UpdateLock: the NARC mark therefore goes at the next update once the mission is 10 s old.
                _narcStamp = _lockTimer;
                SetMobile(false);
            }
            else if (!_overheated || _override)
            {
                if (TryGetComponent<Mw2Cockpit>(out var cockpit)) cockpit.ShowMessage("Powering up...");
                ShutDown = false;
                SetMobile(true);
                StartUp();
            }
        }

        /// <summary>Mech_LoadMek 0x1002f9e0: difficulty 0/1/2 applies only to the player;
        /// the other units use the neutral column. Planet temperature comes from PLNT, not CLIM.</summary>
        public static float HeatSinkMultiplier(int temperature, int difficulty, bool player)
        {
            if (!player || difficulty == 1)
                return temperature < -99 ? 5f : temperature < -30 ? 2f : temperature > 50 ? 0.9f : 1f;
            if (difficulty <= 0)
                return 1.4f * (temperature < -99 ? 5f : temperature < -30 ? 2f : 1f);
            return 0.9f * (temperature < -99 ? 5f : temperature < -30 ? 1.5f : temperature > 50 ? 0.8f : 1f);
        }

        void SetMobile(bool on)
        {
            if (TryGetComponent<Mw2MechController>(out var controller))
            {
                // Keep the physics clock running during shutdown so an airborne mech can fall and land.
                controller.PowerEnabled = on;
                if (TryGetComponent<Mw2MechAnimator>(out var animator)) animator.TargetState = -1;
            }
        }

        /// <summary>0x1003a449: jump jets add [0x37e] * 0.005859375 heat per tick.</summary>
        public void AddJumpHeat(float amount) => AddHeat(amount);

        /// <summary>Heat from outside the weapons: jump jets, or a hit by a weapon with +0x48 (target +0xdc4).</summary>
        public void AddHeat(float amount)
        {
            if (Destroyed) return;
            Heat += amount;
            _heatThisFrame += amount;
        }

        /// <summary>0x1003880d: hard landing applies the same damage to sections 8 and 7 (left and right legs).</summary>
        public void ApplyLandingDamage(float damage)
        {
            if (Destroyed || damage <= 0) return;
            Damage(8, damage, false, null);
            if (!Destroyed) Damage(7, damage, false, null);
        }

        public static void Setup(GameObject go, MekLoadout mek, IList<WeaponDef> table, int side, bool isPlayer, string name)
        {
            // Unity objects must not be null-coalesced: a missing component is a fake-null object, not C# null.
            if (!go.TryGetComponent<Mw2MechCombat>(out var c)) c = go.AddComponent<Mw2MechCombat>();
            c.Side = side;
            c.IsPlayer = isPlayer;
            c.DisplayName = name;
            c.HeatSinks = mek.HeatSinks;
            if (go.TryGetComponent<Mw2MechController>(out var controller))
            {
                controller.HasMasc = mek.HasMasc;
                controller.MascActive = false;
            }
            c.Tons = mek.Tons;
            c.PilotCombatValue = mek.PilotCombatValue(table);
            // Mech_LoadMek: front and rear armour times the difficulty factor of the unit's side.
            int ally = Difficulty >= 2 ? 4 : 3, enemy = Difficulty switch { 0 => 1, 1 => 3, _ => 4 };
            c.ArmorFactor = isPlayer ? 4 : side == 0 ? ally : enemy;
            c.Front = mek.FrontArmor.Select(a => a * c.ArmorFactor).ToArray();
            c.Rear = mek.RearArmor.Select(a => a * c.ArmorFactor).ToArray();
            c.SectionItems = mek.SectionItems.Select(l => l != null ? new List<int>(l) : new List<int>()).ToArray();
            c.SectionFlags = (int[])mek.SectionFlags.Clone();
            c.Ammunition = AmmoStore.Load(mek, table);
            c.SectionGone = new bool[8];
            c.SpeedFactor = 1f;
            c.Internal = (int[])mek.Internal.Clone();
            c.MaxInternal = (int[])mek.Internal.Clone();
            // Mech_LoadMek 0x1002f9e0: a section word without 0x800 gets the full-damage scale nibbles of the MEK values and
            // 0x800 (the high byte kept); with 0x800 the MEK's nibbles stay (a damaged career mech keeps its full scale).
            // Mek_WriteUnit writes this word back, and the shell's section pictures (FUN_1004ae6f) compare with it.
            c.DamageScale = new int[8];
            for (int i = 0; i < 8; i++)
            {
                if ((c.SectionFlags[i] & 0x800) == 0)
                    c.SectionFlags[i] = (c.SectionFlags[i] & 0xff00) | 0x800 |
                        HudLayout.DamageScale(mek.FrontArmor[i], mek.RearArmor[i], mek.Internal[i]);
                c.DamageScale[i] = c.SectionFlags[i] & 0xff;
            }
            c.Weapons.Clear();
            for (int i = 0; i < mek.WeaponIds.Count; i++)
            {
                int type = mek.WeaponIds[i] / 100;
                if (type < 0 || type >= table.Count) continue;
                var def = table[type];
                var w = new MountedWeapon { Def = def, Section = mek.WeaponSections[i], Group = mek.WeaponGroups[i], Id = mek.WeaponIds[i],
                                            Pool = c.Ammunition.PoolOf(mek.WeaponIds[i]) };
                // Mech_LoadMek 0x1002f9e0: a weapon that needs ammunition but has no pool starts in state -1.
                if (w.Pool == AmmoStore.NoPool && def.UsesAmmo) w.State = -1;
                c.Weapons.Add(w);
            }
            c.SyncAmmo();
            // Cockpit rows and weapon slot order (0x10016803): left column even slots, right column odd slots.
            var gauges = HudLayout.AssignWeaponRows(c.Weapons.Select(w => w.Section).ToList(), out var slots);
            for (int i = 0; i < c.Weapons.Count; i++) c.Weapons[i].HudGauge = gauges[i];
            c.Weapons = c.Weapons.Select((w, i) => (w, slot: slots[i])).OrderBy(x => x.slot).Select(x => x.w).ToList();

            // Hit box around the assembled model for targeting; movement uses source sphere tests.
            // From the visible model only (starting at the origin stretched a flying unit's box down to the ground).
            Bounds bounds = default;
            bool anyRenderer = false;
            foreach (var r in go.GetComponentsInChildren<Renderer>())
            {
                if (!r.enabled) continue;   // disabled renderers report empty bounds at the origin
                if (anyRenderer) bounds.Encapsulate(r.bounds); else { bounds = r.bounds; anyRenderer = true; }
            }
            if (!anyRenderer) bounds = new Bounds(go.transform.position, Vector3.one);
            if (!go.TryGetComponent<BoxCollider>(out var box)) box = go.AddComponent<BoxCollider>();
            box.center = go.transform.InverseTransformPoint(bounds.center);
            box.size = bounds.size;
            go.layer = Mw2HitPart.UnitRootLayer;
        }

        void OnEnable() => All.Add(this);

        /// <summary>Each weapon shows the rounds of its pool (mech +0x1a6); without a pool 0, energy weapons -1.</summary>
        public void SyncAmmo()
        {
            foreach (var w in Weapons)
            {
                var p = Ammunition?.Get(w.Pool);
                w.Ammo = p != null ? p.Total : w.Def != null && w.Def.UsesAmmo ? 0 : -1;
            }
        }

        /// <summary>Every weapon of the pool's type goes to state -1 (Weapon_FireProjectile 0x1006c554 / Weapon_JettisonAmmo
        /// 0x1006d162: slot +0x3c / 100 equals the pool type).</summary>
        void PoolEmpty(int pool)
        {
            var p = Ammunition?.Get(pool);
            if (p == null) return;
            foreach (var w in Weapons) if (w.Id / 100 == p.Type) w.State = -1;
        }
        void OnDisable() => All.Remove(this);

        public float MaxRange => Weapons.Count == 0 ? 0 : Weapons.Max(w => w.Def.RangeMeters);
        /// <summary>Line of fire: the torso's facing when the mech can twist, otherwise the body's (mechs face local +Z).</summary>
        public Vector3 Forward => TryGetComponent<Mw2TorsoTwist>(out var twist) ? twist.Forward : transform.forward;
        public Vector3 Chest => TryGetComponent<BoxCollider>(out var b) ? transform.TransformPoint(b.center) : transform.position + Vector3.up * 5;

        public bool Works(int i) => i >= 0 && i < Weapons.Count && !Weapons[i].Broken;
        /// <summary>Weapon ready to fire (slot state 1, 0x1006d11b) with ammunition left (0x10006032).</summary>
        public bool IsReady(int i) => Works(i) && Weapons[i].State == 1 && Weapons[i].Ammo != 0;
        /// <summary>The AI pulls the trigger of one weapon (control +0x25) or of a group (+0x27).</summary>
        public void FireWeapon(int i) { if (!_fireRequests.Contains(i)) _fireRequests.Add(i); }

        /// <summary>
        /// A GT target of the AI (pilot target +0xdc may name a GT, 0x400 kind; FUN_10005309 0x10005309 checks only the
        /// distance +0xc0, so the AI fires at buildings and path-driven vehicles like at units). Shots aim at its visible
        /// centre; lock-on weapons fire unguided at it (no lock on a GT is ported).
        /// </summary>
        [NonSerialized] public Mw2Destructible TargetStructure;

        /// <summary>Centre of the enabled renderers of a structure (falls back to its transform).</summary>
        public static Vector3 StructureCentre(Mw2Destructible s)
        {
            var b = new Bounds();
            bool any = false;
            foreach (var r in s.GetComponentsInChildren<Renderer>())
            {
                if (!r.enabled) continue;
                if (any) b.Encapsulate(r.bounds); else { b = r.bounds; any = true; }
            }
            return any ? b.center : s.transform.position;
        }

        /// <summary>Whether a shot from the chest towards the structure's centre reaches it rather than other scenery.</summary>
        public bool HasLineOfFire(Mw2Destructible target)
        {
            var to = StructureCentre(target) - Chest;
            return FirstHit(Chest, to.normalized, to.magnitude + 1f, out var hit) &&
                   hit.collider.GetComponentInParent<Mw2Destructible>() == target;
        }
        public bool HasWeapons
        {
            get
            {
                for (int i = 0; i < Weapons.Count; i++) if (Works(i) && Weapons[i].Ammo != 0) return true;
                return false;
            }
        }

        /// <summary>A slot that can be selected and fired: not destroyed (state -1), ammunition left, weapon flag 4.</summary>
        bool Selectable(int i) => Works(i) && Weapons[i].Ammo != 0 && (Weapons[i].Def.Flags & 4) != 0;

        /// <summary>
        /// 0x1006cc81: the next selectable weapon after the selected one in the same group; with <paramref name="otherGroups"/>
        /// and none left in the group, the first weapon of the next group (0x1006cea4). Enter calls it with true.
        /// </summary>
        public void SelectNextWeapon(bool otherGroups = true)
        {
            if (Weapons.Count == 0) return;
            int sel = Mathf.Clamp(SelectedWeapon, 0, Weapons.Count - 1), group = Weapons[sel].Group;
            for (int k = 1; k < Weapons.Count; k++)
            {
                int i = (sel + k) % Weapons.Count;
                if (Selectable(i) && Weapons[i].Group == group) { SelectedWeapon = i; return; }
            }
            if (!otherGroups) return;
            // 0x1006cea4: groups (g + 1) mod 14 until one has a selectable weapon or the search is back at g.
            for (int g = (group + 1) % 14; g != group; g = (g + 1) % 14)
                for (int i = 0; i < Weapons.Count; i++)
                    if (Selectable(i) && Weapons[i].Group == g) { SelectedWeapon = i; return; }
        }

        /// <summary>0x1006cff2: select group g (0..2): keep the selection if it is in g, else the first weapon of g.</summary>
        internal bool SelectGroup(int g)
        {
            if (g < 0 || g > 2 || Weapons.Count == 0) return false;
            if (Weapons[Mathf.Clamp(SelectedWeapon, 0, Weapons.Count - 1)].Group == g) return true;
            for (int i = 0; i < Weapons.Count; i++)
                if (Selectable(i) && Weapons[i].Group == g) { SelectedWeapon = i; return true; }
            return false;
        }

        /// <summary>
        /// Slot fire start (0x1006d4f2 / 0x1006d615 / chain fire): state 2 with +0x2C shots; lock-on (+0x28) weapons take
        /// the target when the reticle is locked (flag 0x80 of [0x390]).
        /// </summary>
        void StartBurst(int i)
        {
            if (IsPlayer && NetGame && Mw2NetGame.Instance != null) Mw2NetGame.Instance.MarkFired(i);
            var w = Weapons[i];
            if (w.Ammo == 0) return;
            w.State = 2;
            w.ShotsLeft = Math.Max(1, w.Def.Burst);
            w.Timer = 0;
            w.Repeat = false;
            w.LockTarget = w.Def.LockOn && Locked ? Target : null;
            if (IsPlayer && Target != null && Target.Side != Side) Attacking = Target;
        }

        /// <summary>
        /// Target angles of 0x100176cb / 0x1006d794: bearing relative to the torso heading (+0xd4 - heading - twist, wrapped
        /// to +-180), pitch difference (torso pitch + target elevation +0xd8) and distance (metres).
        /// Approximation: measured between the visible model centres; the original uses the object positions, which sit
        /// at the feet, and for a helicopter (body node 100 m above its origin) on the ground below it.
        /// </summary>
        public bool TargetAngles(out float bearing, out float pitch, out float distance)
        {
            bearing = pitch = distance = 0f;
            if (Target == null || Target.Destroyed) return false;
            var to = Target.ModelCentre - ModelCentre;
            distance = to.magnitude;
            bearing = Vector3.SignedAngle(Vector3.ProjectOnPlane(Forward, Vector3.up), Vector3.ProjectOnPlane(to, Vector3.up), Vector3.up);
            var aim = TryGetComponent<Mw2TorsoTwist>(out var twist) ? twist.Aim : Forward;
            float Elevation(Vector3 v) => Mathf.Atan2(v.y, new Vector2(v.x, v.z).magnitude) * Mathf.Rad2Deg;
            pitch = Elevation(to) - Elevation(aim);
            return true;
        }

        /// <summary>Lock state of [0x390]: 0x8000 target in the lock cone, 0x40 locking, 0x80 locked; timer [0x36f].</summary>
        [System.NonSerialized] public bool TargetInCone, LockAcquiring, Locked;
        int _lockTimer;

        /// <summary>
        /// 0x1006d794, every update: the lock clears, then with a selected lock-on weapon (+0x28) and a unit target the
        /// target must be inside the weapon's range and within 16 degrees of the torso heading and pitch: entering starts
        /// locking with a timer of 0x16a ticks, staying counts it down and at 0 the target is locked (0x80); outside, the
        /// timer counts up and above 0x169 the locking attempt ends. A NARC-marked target (+0xe42 bit 1) locks at once.
        /// Approximations: the range test FUN_10015760 lost its arguments in the decompile and is taken as the selected
        /// weapon's minimum and maximum range.
        /// </summary>
        void UpdateLock(int dt)
        {
            // 1006d79f..1006d7c9: an AI pilot with nonzero +1b4 preserves an existing
            // lock bit, bypassing the player-style cone/timer/selected-weapon checks.
            if (!IsPlayer && Locked && TryGetComponent<Mw2MechAI>(out var ai) &&
                AiTables.Styles[Mathf.Clamp(ai.PilotStyleId, 0, AiTables.Styles.Length - 1)].TauntPercent != 0) return;
            Locked = false;
            int sel = SelectedWeapon;
            if (sel < 0 || sel >= Weapons.Count || Target == null || Target.Destroyed || !Weapons[sel].Def.LockOn)
            {
                TargetInCone = false;
                LockAcquiring = false;
                return;
            }
            if (Target.Narced)
            {
                // 0x1006d794: a NARC-marked unit is locked with no range or angle test.
                _lockTimer = 0;
                TargetInCone = LockAcquiring = Locked = true;
                return;
            }
            var def = Weapons[sel].Def;
            TargetAngles(out float bearing, out float pitch, out float distance);
            bool inRange = distance > def.MinRangeMeters && distance < def.RangeMeters;
            if (!inRange || Mathf.Abs(bearing) >= 16f || Mathf.Abs(pitch) >= 16f)
            {
                TargetInCone = false;
                if (LockAcquiring && (_lockTimer += dt) > 0x169) LockAcquiring = false;
                return;
            }
            TargetInCone = true;
            if (!LockAcquiring) { LockAcquiring = true; _lockTimer = 0x16a; }
            else if (_lockTimer < 1) { _lockTimer = 0; Locked = true; }
            else _lockTimer -= dt;
        }

        /// <summary>Ai_LockOnPlayer 0x10005c42 grants mech+e40 bit0x80 after the
        /// style probability check for every AI target; only its sound is player-specific.</summary>
        internal bool TryAiLock(int styleId, int roll)
        {
            if (!AiTables.EnemyFireWarns(styleId, roll)) return false;
            Locked = true;
            return true;
        }

        /// <summary>0x1006d4f2: every ready weapon of the selected weapon's group fires; the selection is kept.</summary>
        void FireGroup()
        {
            if (Weapons.Count == 0) return;
            int saved = SelectedWeapon;
            SelectNextWeapon(false);
            int start = SelectedWeapon;
            int group = Weapons[start].Group;
            // StartBurst can spend the last round, making the starting slot unselectable.
            // Walk each slot once instead of waiting for selection to return to that slot.
            for (int k = 0; k < Weapons.Count; k++)
            {
                int i = (start + k) % Weapons.Count;
                if (Weapons[i].Group == group && Selectable(i) && Weapons[i].State == 1) StartBurst(i);
            }
            SelectedWeapon = saved;
        }

        /// <summary>0x1006d615 (alpha strike): every ready weapon fires.</summary>
        void AlphaStrike()
        {
            for (int i = 0; i < Weapons.Count; i++)
                if (Weapons[i].State == 1 && Works(i)) StartBurst(i);
        }

        /// <summary>
        /// Player branch of Weapons_Update (0x1006bc57). \ toggles chain/group fire. In group fire the fire key acts as
        /// group fire (+0x27 = +0x25). Each control fires once per press (latch bit 1 of [0x390]): group fire, alpha
        /// strike (P), group 0..2 keys (select the group, then fire it), or in chain fire the selected weapon alone. When
        /// no fire control is held any more the latch clears and the selection moves on in its group (0x1006cc81).
        /// </summary>
        void PlayerFireControl()
        {
            // Weapons_Update 0x1006bc57 returns before reading +0x2c (chain/group toggle), +0x2d (alpha)
            // or the firing/group bytes unless +0xdd4 is 2. Mech_PlayerCommands 0x1003b7dd separately handles
            // +0x2b without +0x27, so a standalone next-group command still works during start-up.
            if (!MechPowerTiming.WeaponControlsEnabled(PowerState) || ShutDown || PoweredDown)
            {
                _keyToggle = _keyAlpha = false;
                return;
            }
            if (_keyToggle)
            {
                // Weapons_Update 0x1006bc57: "Chain fire engaged" / "Group fire engaged".
                ChainFire = !ChainFire;
                if (Mw2Audio.Instance != null) Mw2Audio.Instance.Computer(ChainFire ? 6 : 7, 1);
            }
            _keyToggle = false;
            bool fire = _keyFire, group = _keyGroupFire, alpha = _keyAlpha;
            _keyAlpha = false;
            // Weapons_Update 0x1006bc57, group fire mode: without +0x2b the fire byte becomes the group byte (+0x27 = +0x25).
            if (!ChainFire && !_keyCycleGroup) { group = fire; fire = false; }
            // Mech_UpdateControl 0x1003a449 after Weapons_Update: +0x2b with +0x27 -> the next group (0x1006d3c6).
            if (_keyCycleGroup && group) SelectNextGroup();
            _groupFireHeld = group || _keyGroups0 || _keyGroups1 || _keyGroups2;
            if (ShutDown || PoweredDown || PoweringUp || Weapons.Count == 0) return;
            SelectedWeapon = Mathf.Clamp(SelectedWeapon, 0, Weapons.Count - 1);

            if (group) { if (!_fireLatch) { _fireLatch = true; FireGroup(); } return; }
            if (alpha) { if (!_fireLatch) { _fireLatch = true; AlphaStrike(); } return; }
            int g = _keyGroups0 ? 0 : _keyGroups1 ? 1 : _keyGroups2 ? 2 : -1;
            if (g >= 0) { if (!_fireLatch) { _fireLatch = true; if (SelectGroup(g)) FireGroup(); } return; }
            if (fire)
            {
                if (!_fireLatch)
                {
                    _fireLatch = true;
                    if (Weapons[SelectedWeapon].State == 1 && Works(SelectedWeapon)) StartBurst(SelectedWeapon);
                    else if (Mw2Audio.Instance != null)
                        Mw2Audio.Instance.Play(0x15d, transform.position, true); // Weapons_Update 0x1006bc57: WEPNBUSY
                }
                return;
            }
            if (_fireLatch) { _fireLatch = false; SelectNextWeapon(false); }
        }

        void Update()
        {
            if (Destroyed) { DeathExplosions(); return; }
            if (IsPlayer) { PlayerInput(); PlayerFireControl(); }
            // The target controls of Mech_UpdateControl 0x1003a449 (state 2), in the frame their bytes are set.
            if (IsPlayer && PowerState == 2 && !ShutDown && !PoweredDown)
            {
                if (Mw2Input.Held(0x44)) StartSelfDestruct();
                else UpdatePlayerTargeting();
            }

            _clock += Time.deltaTime * MechMotion.TicksPerSecond;
            int now = (int)_clock, dt = now - _now;
            _now = now;
            if (dt <= 0) return;

            if (!NetRemote) UpdateHeat(now, dt);
            if (Narced && ShutDown && now - _narcStamp > 0x712) Narced = false;      // 0x1003a449 state 3
            // Weapon_UpdateLock 0x1006d794 is reached from Mech_UpdateControl
            // 0x1003a449 and the target updater 0x10058b17 only in state 2.
            // During the startup frame that changes 1 -> 2 it still waits for
            // the next tick, just as the original pre-switch guard does.
            if (PowerState == 2 && !ShutDown && !PoweredDown) UpdateLock(dt);
            UpdateSoundCues(now);
            // Mech_UpdateControl 0x1003a449 and target update 0x10058b17 call
            // Weapons_Update 0x1006bc57 only in power state 2. Burst and recycle
            // timers therefore pause throughout startup and shutdown.
            if (PowerState != 2 || ShutDown || PoweredDown)
            {
                Trigger = false;
                _fireRequests.Clear();
                return;
            }

            for (int i = 0; i < Weapons.Count; i++)
            {
                var w = Weapons[i];
                if (Internal[Mathf.Max(0, w.Section)] <= 0) continue;
                if (w.Def.IsAms) AmsReady(w);
                switch (w.State)
                {
                    case 1 when !IsPlayer && (Trigger || _fireRequests.Contains(i)) && w.Ammo != 0:
                        StartBurst(i);
                        break;
                    case 2:
                        w.Timer += dt;
                        while (w.ShotsLeft > 0 && w.Timer >= w.Def.IntervalTicks)
                        {
                            w.Timer -= Math.Max(1, w.Def.IntervalTicks);
                            if (w.Def.IsAms)
                            {
                                // Weapons_Update takes the fire function's count off +0x24; 0 ends the burst.
                                int used = AmsFire(w);
                                if (used <= 0) { w.ShotsLeft = 0; break; }
                                w.ShotsLeft -= used;
                                AmsShotEffects(w);
                                continue;
                            }
                            w.ShotsLeft--;
                            if (!FireShot(w)) { w.ShotsLeft = 0; break; }
                        }
                        if (w.ShotsLeft == 0)
                        {
                            // Auto-repeat weapons (+0x20) with a fire control still held wait in state 3 to fire again.
                            w.Repeat = w.Def.Auto && FireHeld;
                            w.State = 3;
                            w.Stamp = now;
                        }
                        break;
                    case 3 when now - w.Stamp >= w.Def.RecycleTicks:
                        // State 3 fires again when it is the selected weapon or a group control is held, else ready.
                        if (w.Repeat && (i == SelectedWeapon || _groupFireHeld || (!IsPlayer && Trigger)) && w.Ammo != 0) StartBurst(i);
                        else { w.State = 1; w.Repeat = false; }
                        break;
                }
            }
            _fireRequests.Clear();
        }

        /// <summary>
        /// One shot (Weapons_Update 0x1006bc57 -> Weapon_FireProjectile 0x1006c554). The origin is the muzzle node of the
        /// weapon's section (POFO, mech +0xd98[slot +0x28]); the direction runs from the muzzle to the aim point, a point
        /// on the aim ray of the pilot's view (0x1006dd6d: node +0x44 orientation, distance +0xa0), so all weapons
        /// converge under the reticle; the projectile itself flies (Mw2Projectile).
        /// Lock-on (+0x28) missiles home on the target the burst locked (StartBurst).
        /// The player retains convergence from Mech_UpdatePhysics 0x1003a1c8; non-player fire updates it here.
        /// Missing POFO/EYEO nodes still use Chest/Origin/body orientation as adapter fallbacks.
        /// </summary>
        bool FireShot(MountedWeapon w)
        {
            // Weapon_FireProjectile 0x1006c554: no shot from an empty pool (unless mw2dif.cfg +0 is set); one round
            // leaves the pool and its bin, except for the player with that option; an emptied pool ends the burst.
            bool unlimited = Mw2Options.GameOptions.UnlimitedAmmo;
            // Weapon_FireProjectile 0x1006c554: only the owner's machine counts rounds in a network game.
            if (!NetRemote && !Ammunition.CanFire(w.Pool, unlimited)) return true;
            bool more = NetRemote || Ammunition.Consume(w.Pool, unlimited && IsPlayer);
            if (!more) PoolEmpty(w.Pool);
            SyncAmmo();
            Heat += w.Def.Heat;
            Autopilot.MissionTelemetry.Shot(this, w);
            _heatThisFrame += w.Def.Heat;
            var from = TryGetComponent<Mw2WeaponMounts>(out var mounts) ? mounts.Muzzle(w.Section, Chest) : Chest;
            // 1006c554 consumes ammo/stats before attempting its first-free allocation.
            // Exhaustion leaves audio/muzzle effects and the hook return value intact.
            var shotModels = Mw2ShotModels.Instance;
            int shotSlot = shotModels != null ? shotModels.Take(w.Def.ProjectileType) : -1;
            if (shotSlot >= 0)
            {
                TryGetComponent<Mw2TorsoTwist>(out var twist);
                WeaponAimRay(twist, out var eye, out var aim);
                // 1006c82c skips Target_PickReticle for the player: fire consumes the cache
                // updated in physics, even if scene geometry changes before this fire hook.
                if (!IsPlayer) RefreshAimConvergence(_now, eye, aim, out _);
                var e = eye / PolyModel.UnitsToMeters; var m = from / PolyModel.UnitsToMeters;
                var converged = AiFireGeometry.ConvergedDirection(new AiLeadPoint.Vector(e.x, e.y, e.z),
                    new AiLeadPoint.Vector(aim.x, aim.y, aim.z), new AiLeadPoint.Vector(m.x, m.y, m.z), _aimConvergence.Distance);
                var direction = new Vector3(converged.X, converged.Y, converged.Z);

                var homing = w.LockTarget != null && !w.LockTarget.Destroyed ? w.LockTarget : null;
                Mw2Projectile.Spawn(this, w.Def, from, direction, w.Def.LifetimeTicks - w.Timer, homing, shotModels, shotSlot);
            }
            // Weapons_Update 0x1006bc57 -> FUN_10007081 with the cockpit flag DAT_100a6e48: half volume in the cockpit.
            if (Mw2Audio.Instance != null) Mw2Audio.Instance.Play(w.Def.Sound, from, false, 100, Mw2Cockpit.PlayerInCockpit);
            // 0x10011e79: the weapon's two shot effects (+0x18, +0x1C) on the muzzle (flash of the autocannons).
            var effects = Mw2Effects.Instance;
            if (effects != null)
            {
                var muzzle = mounts != null ? mounts.MuzzleNode(w.Section) : null;
                effects.Spawn(w.Def.FireEffect1, from, null, muzzle);
                effects.Spawn(w.Def.FireEffect2, from, null, muzzle);
            }
            return more;
        }

        /// <summary>The engine object's origin (+0x50..+0x58): OriginHeight above the feet, turned with the unit.</summary>
        public Vector3 Origin => transform.position + transform.rotation * Vector3.up * OriginHeight;

        readonly AimConvergence _aimConvergence = new AimConvergence();

        /// <summary>Mech_UpdatePhysics 0x1003a1bc..1003a1cd updates only the player's
        /// convergence each physics pass. Unity object update order remains a frame adapter.</summary>
        internal void UpdatePlayerConvergence(int now)
        {
            if (!IsPlayer) return;
            TryGetComponent<Mw2TorsoTwist>(out var twist);
            WeaponAimRay(twist, out var eye, out var aim);
            RefreshAimConvergence(now, eye, aim, out _);
        }

        /// <summary>Target_PickReticle 0x1006dc4a: only GP/GT model hits replace the slow current.</summary>
        bool RefreshAimConvergence(int now, Vector3 eye, Vector3 aim, out Mw2ShotHit aimHit)
        {
            bool hit = FirstHit(eye, aim, 150000f * PolyModel.UnitsToMeters, out aimHit);
            bool targetHit = hit && (aimHit.collider.GetComponentInParent<Mw2MechCombat>() != null ||
                aimHit.collider.GetComponentInParent<Mw2Destructible>() != null);
            _aimConvergence.Update(now, targetHit, hit ? aimHit.distance / PolyModel.UnitsToMeters : 0);
            return hit;
        }

        /// <summary>Weapon_AimRay 0x1006ddd8 / Weapon_AimNodeDirection 0x1006de54.
        /// Shared by the AI fire gate and player/AI muzzle-to-convergence directions.</summary>
        internal void WeaponAimRay(Mw2TorsoTwist twist, out Vector3 origin, out Vector3 direction)
        {
            Transform eye = null;
            if (TryGetComponent<Mw2Cockpit>(out var cockpit)) eye = cockpit.Eye;
            origin = eye != null ? eye.position : Origin;
            var player = Mw2Cockpit.Player;
            if (player != null) origin.y += player.EyeVerticalOffset;
            var forward = eye != null ? eye.forward : transform.forward;
            AiFireGeometry.EyeAngles(new AiLeadPoint.Vector(forward.x, forward.y, forward.z), out float pitch, out float yaw);
            bool falling = TryGetComponent<Mw2MechController>(out var controller) && controller.FallingOver;
            var aim = AiFireGeometry.Direction(pitch, yaw, twist != null ? -twist.Pitch : 0f, falling);
            direction = new Vector3(aim.X, aim.Y, aim.Z);
        }

        /// <summary>
        /// FUN_1000cc95 0x1000cc95, the AMS slot's ready hook, every Weapons_Update: a ready slot (state 1) starts firing
        /// (state 2, +0x24 = +0x2C rounds, timers and lock cleared) as soon as a missile shot (types 3, 4, 9, 10, 12) of
        /// another side is within 50000 units (_DAT_100870fc) of the unit's origin and flying towards it (velocity . (origin -
        /// shot) > 0). FUN_10064f91, which every burst start also calls, is not ported here either.
        /// </summary>
        void AmsReady(MountedWeapon w)
        {
            if (w.State != 1) return;
            var origin = Origin;
            foreach (var shot in Mw2Projectile.Active)
            {
                if (!IncomingMissile(shot, origin, out _, out bool approaching) || !approaching) continue;
                w.State = 2;
                w.ShotsLeft = Math.Max(1, w.Def.Burst);
                w.Timer = 0;
                w.Repeat = false;
                w.LockTarget = null;
                return;
            }
        }

        /// <summary>A live missile of another side within 50000 units of the origin; distance in units.</summary>
        bool IncomingMissile(Mw2Projectile shot, Vector3 origin, out float distance, out bool approaching)
        {
            distance = 0f;
            approaching = false;
            if (shot == null || shot.Def == null || !shot.Def.IsMissileType || shot.Owner == null || shot.Owner.Side == Side) return false;
            var delta = (origin - shot.transform.position) / PolyModel.UnitsToMeters;
            distance = delta.magnitude;
            if (distance > 50000f) return false;
            approaching = Vector3.Dot(shot.Velocity, delta) > 0f;
            return true;
        }

        /// <summary>
        /// FUN_1000ce3f 0x1000ce3f, the AMS fire function: no rounds in the bin -> 0 (the burst ends); otherwise
        /// RandomInt(5) + 1 rounds (at most the burst's remaining +0x24) leave the bin, and an empty bin puts the slot in
        /// state -1 with Radio_Computer(10, 0) (variant 0 says nothing); then FUN_1000d08b decides the missiles' fate.
        /// mw2dif.cfg +0 (unlimited ammunition) leaves the player's bin untouched.
        /// </summary>
        int AmsFire(MountedWeapon w)
        {
            int used = Ammunition.AmsFire(w.Pool, w.ShotsLeft, IsPlayer && Mw2Options.GameOptions.UnlimitedAmmo,
                                          n => UnityEngine.Random.Range(0, n), out bool empty);
            if (Ammunition.Get(w.Pool) == null || used == 0 && !empty) { SyncAmmo(); return 0; }
            if (empty) w.State = -1;    // Radio_Computer(10, 0): variant 0 says nothing
            SyncAmmo();
            AmsKill();
            return used;
        }

        /// <summary>
        /// FUN_1000d08b 0x1000d08b: for every missile of another side within 50000 units: an undecided kill distance
        /// (slot +0x60 below 0) becomes 0 within 2500 units (_DAT_10087104); farther out, for a missile flying towards the
        /// unit, RandomInt(100) at most 75 sets it to 2500 + RandomInt(trunc((d - 2500) / 100)) * 100 (2500 when d - 2500 is
        /// below 100), else 0 (it gets through). A missile inside its positive kill distance bursts harmlessly (Detonate).
        /// </summary>
        void AmsKill()
        {
            var origin = Origin;
            foreach (var shot in Mw2Projectile.Active.ToArray())
            {
                if (!IncomingMissile(shot, origin, out float d, out bool approaching)) continue;
                if (shot.KillDistance < 0f)
                {
                    float beyond = d - 2500f;
                    if (beyond <= 0f) shot.KillDistance = 0f;
                    else if (approaching)
                        shot.KillDistance = UnityEngine.Random.Range(0, 100) <= 0x4b
                            ? beyond < 100f ? 2500f : UnityEngine.Random.Range(0, (int)(beyond / 100f)) * 100f + 2500f
                            : 0f;
                }
                if (shot.KillDistance > 0f && d <= shot.KillDistance) shot.Detonate();
            }
        }

        /// <summary>Weapons_Update after a successful AMS shot: heat (+0x44), sound and the muzzle effects (+0x18, +0x1C).</summary>
        void AmsShotEffects(MountedWeapon w)
        {
            Heat += w.Def.Heat;
            _heatThisFrame += w.Def.Heat;
            var mounts = TryGetComponent<Mw2WeaponMounts>(out var m) ? m : null;
            var from = mounts != null ? mounts.Muzzle(w.Section, Chest) : Chest;
            if (Mw2Audio.Instance != null) Mw2Audio.Instance.Play(w.Def.Sound, from, false, 100, Mw2Cockpit.PlayerInCockpit);
            var effects = Mw2Effects.Instance;
            if (effects == null) return;
            var muzzle = mounts != null ? mounts.MuzzleNode(w.Section) : null;
            effects.Spawn(w.Def.FireEffect1, from, null, muzzle);
            effects.Spawn(w.Def.FireEffect2, from, null, muzzle);
        }

        /// <summary>
        /// FUN_10018ad5: first scene object or model-sphere part that is not this unit.
        /// Types 0/1 use native box clipping, part type 6 native sphere clipping;
        /// scene POLY faces remain Unity casts in both directions.
        /// </summary>
        public bool FirstHit(Vector3 from, Vector3 direction, float range, out Mw2ShotHit first)
        {
            // Mech bodies and their hit-part colliders move through Transform in Update. With Auto Sync
            // Transforms disabled, raycasts otherwise test their positions from the previous physics step.
            Physics.SyncTransforms();
            first = default;
            float best = float.MaxValue;
            long bestOrder = 0;
            var end = from + direction * range;
            foreach (var h in Physics.RaycastAll(from, direction, range, ~0, QueryTriggerInteraction.Ignore))
            {
                if (!Shootable(h.collider)) continue;
                long order = CollisionOrder(h.collider);
                if (!SceneCollisionOrder.Precedes(h.distance, order, best, bestOrder)) continue;
                best = h.distance; bestOrder = order; first = new Mw2ShotHit(h);
            }
            foreach (var h in Physics.RaycastAll(end, -direction, range, ~0, QueryTriggerInteraction.Ignore))
            {
                float d = range - h.distance;
                if (!Shootable(h.collider)) continue;
                long order = CollisionOrder(h.collider);
                if (!SceneCollisionOrder.Precedes(d, order, best, bestOrder)) continue;
                best = d; bestOrder = order; first = new Mw2ShotHit(h); first.distance = d;
            }
            // Lod_SwapModel 1001eb15 marks body parts type 6. FUN_10018ad5 first calls 100047ff,
            // then Object_SegmentTest 10018ee2: conservative model sphere, not mesh triangles.
            // Attached parts use the current representation; detached parts keep their enabled model.
            float m = PolyModel.UnitsToMeters;
            var start = from / m;
            // Object_SegmentTest types 0/1 -> 10017f60, including inside-start and endpoint rules.
            // Bounds still come from the Unity world-axis adapter; native scenery broad-phase is separate.
            foreach (var surface in Mw2Surface.All)
            {
                if (surface == null || !surface.UsesNativeBox || !surface.AxisBox.enabled ||
                    !surface.gameObject.activeInHierarchy || surface.transform.IsChildOf(transform)) continue;
                var bounds = surface.AxisBox.bounds;
                var min = bounds.min / m; var max = bounds.max / m;
                if (!ModelBoxCast.Hit(new AiLeadPoint.Vector(min.x, min.y, min.z), new AiLeadPoint.Vector(max.x, max.y, max.z),
                    new AiLeadPoint.Vector(start.x, start.y, start.z), new AiLeadPoint.Vector(direction.x, direction.y, direction.z),
                    range / m, out float clipped, out var point, out var normal)) continue;
                float distance = clipped * m;
                if (!SceneCollisionOrder.Precedes(distance, surface.CollisionOrder, best, bestOrder)) continue;
                best = distance; bestOrder = surface.CollisionOrder;
                first = new Mw2ShotHit { collider = surface.AxisBox, point = new Vector3(point.X, point.Y, point.Z) * m,
                    distance = distance, normal = new Vector3(normal.X, normal.Y, normal.Z) };
            }
            foreach (var part in Mw2HitPart.All)
            {
                if (part == null || !part.SphereReady || !part.CurrentModel || part.HitCollider == null || !part.HitCollider.enabled ||
                    !part.gameObject.activeInHierarchy || part.transform.IsChildOf(transform)) continue;
                var center = part.transform.TransformPoint(part.LocalCentre) / m;
                var scale = part.transform.lossyScale;
                float radius = part.ModelRadius * Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z)) / m;
                if (!ModelSphereCast.Hit(new AiLeadPoint.Vector(center.x, center.y, center.z), radius,
                    new AiLeadPoint.Vector(start.x, start.y, start.z), new AiLeadPoint.Vector(direction.x, direction.y, direction.z),
                    range / m, out float candidate, out float clipped, out var point)) continue;
                float distance = clipped * m;
                if (!SceneCollisionOrder.Precedes(distance, part.CollisionOrder, best, bestOrder)) continue;
                best = distance; bestOrder = part.CollisionOrder;
                var hitPoint = new Vector3(point.X, point.Y, point.Z) * m;
                var normal = (candidate == 10 ? from : hitPoint) - center * m;
                first = new Mw2ShotHit { collider = part.HitCollider, point = hitPoint, distance = distance, normal = normal.normalized };
            }
            return best < float.MaxValue;
        }

        /// <summary>0x10049159: shared GP/GT model activation, independent of Physics hit-array order.
        /// Unmarked legacy colliders retain fallback priority zero until their scene is rebuilt.</summary>
        static long CollisionOrder(Collider collider)
        {
            if (collider.TryGetComponent<Mw2HitPart>(out var part)) return part.CollisionOrder;
            var surface = collider.GetComponentInParent<Mw2Surface>();
            return surface != null ? surface.CollisionOrder : 0;
        }

        /// <summary>
        /// A collider a shot can hit: scenery, or another unit's hit part (Mw2HitPart); a unit's own colliders and the
        /// movement box (BoxCollider on the root) of units that have hit parts are skipped.
        /// </summary>
        bool Shootable(Collider c)
        {
            if (c.transform.IsChildOf(transform)) return false;
            var surface = c.GetComponentInParent<Mw2Surface>();
            if (surface != null && surface.UsesNativeBox && surface.AxisBox == c) return false;
            if (c.TryGetComponent<Mw2HitPart>(out var part)) return !part.SphereReady;
            if (c is BoxCollider && c.TryGetComponent<Mw2MechCombat>(out var unit) && unit.HasHitParts) return false;
            return true;
        }

        /// <summary>Units built with per-part colliders are hit through those, not through their movement box.</summary>
        public bool HasHitParts
        {
            get
            {
                if (_hasHitParts < 0) _hasHitParts = GetComponentInChildren<Mw2HitPart>(true) != null ? 1 : 0;
                return _hasHitParts == 1;
            }
        }
        int _hasHitParts = -1;

        /// <summary>
        /// Centre of the visible model (bracket and missile aim point; the unit origin sits at its feet), computed once per
        /// frame from the renderers found at the first call.
        /// </summary>
        public Vector3 ModelCentre
        {
            get
            {
                if (_centreFrame == Time.frameCount) return _centre;
                _renderers ??= GetComponentsInChildren<Renderer>(true);
                Bounds b = default;
                bool any = false;
                foreach (var r in _renderers)
                {
                    if (r == null || !r.enabled) continue;
                    if (any) b.Encapsulate(r.bounds); else { b = r.bounds; any = true; }
                }
                _centre = any ? b.center : transform.position;
                _centreFrame = Time.frameCount;
                return _centre;
            }
        }
        Renderer[] _renderers;
        Vector3 _centre;
        int _centreFrame = -1;

        /// <summary>Whether a shot from this unit's chest would reach <paramref name="target"/> rather than scenery.</summary>
        public bool HasLineOfFire(Mw2MechCombat target)
        {
            var to = target.Chest - Chest;
            return FirstHit(Chest, to.normalized, to.magnitude + 1f, out var hit) &&
                   hit.collider.GetComponentInParent<Mw2MechCombat>() == target;
        }

        /// <summary>Section from the impact point in the target's own frame (its right side is local +X).</summary>
        int SectionAt(Vector3 point)
        {
            if (!TryGetComponent<BoxCollider>(out var box)) return 2;
            var local = transform.InverseTransformPoint(point) - box.center;
            float up = local.y / Mathf.Max(0.01f, box.size.y) + 0.5f;
            float side = local.x / Mathf.Max(0.01f, box.size.x * 0.5f);    // +1 = target's right
            if (up > 0.88f) return 0;
            if (up < 0.42f) return side >= 0 ? 6 : 7;
            if (side > 0.6f) return 4;
            if (side < -0.6f) return 5;
            if (side > 0.2f) return 1;
            if (side < -0.2f) return 3;
            return 2;
        }

        static int Inner(int section) => section switch { 4 => 1, 5 => 3, 6 => 1, 7 => 3, 1 => 2, 3 => 2, _ => -1 };

        public void ApplyDamage(float damage, Vector3 point, Vector3 attacker, Mw2MechCombat source, Collider part = null)
        {
            if (Destroyed) return;
            // The part's section (OBJL, object +0x16), else by the point.
            int section = part != null && TryGetComponent<Mw2HitMap>(out var map) ? map.SectionOf(part.transform) : -1;
            if (section < 0) section = SectionAt(point);
            bool rear = Vector3.Dot(Forward, attacker - transform.position) < 0;
            Damage(section + 1, damage, rear, source);
        }

        /// <summary>
        /// 0x10037b59 with a 1-based hit location (1 head, 2 right torso, 3 centre torso, 4 left torso, 5/6 right/left arm,
        /// 7/8 right/left leg). A centre torso hit lands on 2, 3 or 4 at random; the rear flag counts for the torsos only.
        /// The armour takes the damage; when it is used up the rest comes off the internal structure of the same section
        /// (no transfer inwards). Structure gone -> the section is destroyed (0x100365d3); otherwise RandomInt(5) times a
        /// critical roll: below 20 of 100 with items left, 12 destroys the section, else a random slot item is hit
        /// (0x10036aa9).
        /// </summary>
        void Damage(int location, float damage, bool rear, Mw2MechCombat source)
        {
            // Shot_Move 0x10010f40, Effect_AreaDamageUnits 0x10012b59: the owner's machine alone applies damage.
            if (NetGame && !IsPlayer) return;
            if (NetGame && Mw2NetGame.Instance != null) Mw2NetGame.Instance.LocalHit(source);
            if (Destroyed || damage <= 0f || location < 1 || location > 8) return;
            // Mech_ApplyDamage 0x10037b59: units of type 2 on the player's side and, with mw2dif.cfg +1, the player take none.
            if ((Side == 0 && GamepieceType == 2) || (IsPlayer && Mw2Options.GameOptions.Invulnerable)) return;
            if (location == 3) location = UnityEngine.Random.Range(0, 3) switch { 0 => 2, 1 => 3, _ => 4 };
            Autopilot.MissionTelemetry.Damage(this, location, damage, rear, source);
            int s = location - 1;
            bool back = rear && location >= 2 && location <= 4;
            // Armour and structure are floats in the engine; ours are whole points, so fractions (area damage of
            // 0.004 per tick) are carried until they make a point.
            _carry[s] += damage;
            int whole = (int)_carry[s];
            if (whole <= 0) return;
            _carry[s] -= whole;
            ref int armor = ref (back ? ref Rear[s] : ref Front[s]);
            armor -= whole;
            if (armor > 0) { RaiseNodeDamage(s, back, armor); return; }
            // Mech_ApplyDamage 0x10037b59: first loss of armour on a player section plays MECRDWA1.
            if (IsPlayer && !_armorBreachAnnounced[s] && Mw2Audio.Instance != null)
            {
                _armorBreachAnnounced[s] = true;
                Mw2Audio.Instance.Play(0x101, transform.position, true);
            }
            // Mech_ApplyDamage 0x10037b59: armour used up on the player's head or centre torso (hit location 1 or 3 after the torso
            // roll) by more than 2 points, the unit not destroyed: the screen flash of Hud_CockpitTones (DAT_100a66cc).
            if (IsPlayer && (location == 1 || location == 3) && PowerState != 4 && damage > 2f && Mw2PaletteFade.Instance != null)
                Mw2PaletteFade.Instance.RequestHitFlash();
            Internal[s] += armor;
            armor = 0;
            if (Internal[s] <= 0) { DestroySection(location, source); return; }
            int rolls = UnityEngine.Random.Range(0, 5);
            for (int k = 0; k < rolls && !Destroyed; k++)
            {
                int r = UnityEngine.Random.Range(0, 100);
                if (r >= 20 || SectionItems == null || SectionItems[s].Count == 0) continue;
                if (r == 12) { DestroySection(location, source); return; }
                Critical(location, UnityEngine.Random.Range(0, SectionItems[s].Count), false, source);
            }
            if (!Destroyed && !SectionGone[s]) RaiseNodeDamage(s, back, armor);
        }

        /// <summary>
        /// Mech_ApplyDamage 0x10037b59 (0x10037f1f..0x10037fca) after a hit that did not destroy the section: level =
        /// 15 - trunc((armour of the hit side / k + structure) * 3 / scale), scale = the side's nibble of the section word
        /// +0x26 (DamageScale), k = DAT_10099fbc (4) for the player and DAT_10099fb8 (difficulty 1 / 3 / 4) for every other
        /// unit, allies included although their armour was multiplied by 3 / 3 / 4; then FUN_1005b367 marks the nodes.
        /// A zero scale skips it (the original then reuses an unset local).
        /// </summary>
        void RaiseNodeDamage(int s, bool back, int armor)
        {
            if (DamageScale == null || !TryGetComponent<Mw2HitMap>(out var map)) return;
            int scale = back ? (DamageScale[s] >> 4) & 0xf : DamageScale[s] & 0xf;
            if (scale == 0) return;
            float k = IsPlayer ? 4f : Difficulty switch { 0 => 1f, 1 => 3f, _ => 4f };
            map.RaiseDamage(15 - (int)((armor / k + Internal[s]) * 3f / scale), s);
        }

        readonly float[] _carry = new float[8];

        /// <summary>
        /// Area damage of an explosion (0x10012b59): half to the head, the full amount to locations 2..8 (location 3
        /// spreads over the torsos), doubled while shut down; the heat goes to the unit (+0xdc8).
        /// </summary>
        public void AreaHit(float damage, float heat, Mw2MechCombat source)
        {
            if (Destroyed) return;
            if (ShutDown || PoweredDown) damage *= 2f;
            Damage(1, damage / 2f, false, source);
            for (int loc = 2; loc <= 8 && !Destroyed; loc++) Damage(loc, damage, false, source);
            if (heat != 0f) AddHeat(heat);
        }

        /// <summary>
        /// 0x100365d3: section destroyed (flag 0x2000), all its slot items are hit silently (0x1003655d), then: right / left
        /// torso -> the arm on that side (the torso itself stays undestroyed); centre torso -> arms, side torsos and head,
        /// the unit dies; arms fall off; head -> 20 % the unit dies, else it keeps 1 structure; a leg -> 95 % it keeps 1
        /// structure and the unit limps (speed 0.2) only for GP type 1, else it falls off (speed 0, the unit falls) - with a leg already lost
        /// the centre torso goes too.
        /// </summary>
        void DestroySection(int location, Mw2MechCombat source)
        {
            int s = location - 1;
            if (Destroyed || SectionGone[s]) return;
            Internal[s] = 0;
            SectionGone[s] = true;
            if (SectionItems != null)
                for (int n = SectionItems[s].Count; n > 0 && SectionItems[s].Count > 0; n--) Critical(location, 0, true, source);
            switch (location)
            {
                case 2: DestroySection(5, source); SectionGone[s] = false; return;
                case 4: DestroySection(6, source); SectionGone[s] = false; return;
                case 3:
                    DestroySection(5, source); DestroySection(2, source); DestroySection(4, source); DestroySection(6, source);
                    DestroySection(1, source);
                    BlowOffSection(location);
                    Die(source);
                    return;
                case 5:
                case 6:
                    BlowOffSection(location);
                    return;
                case 7:
                case 8:
                    if (!LegLost)
                    {
                        // Mech_DestroySection 0x100365d3: the 95 % limp path also requires gamepiece +0 == 1.
                        int legRoll = UnityEngine.Random.Range(0, 100);
                        if (legRoll < 95 && GamepieceType == 1)
                        {
                            Internal[s] = 1;
                            SectionGone[s] = false;
                            SpeedFactor = 0.2f;
                            return;
                        }
                        BlowOffSection(location);
                        SpeedFactor = 0f;
                        LegLost = true;
                        // 0x100365d3: only GP type 1 sets +0xe42 bit 4 with the fall rate RandomNormal() / 1024.
                        if (GamepieceType == 1 && TryGetComponent<Mw2MechController>(out var legless))
                            legless.StartFall(Mathf.RoundToInt(Mw2Effects.Gauss()));
                        return;
                    }
                    DestroySection(3, source);
                    return;
                default:
                    if (UnityEngine.Random.Range(0, 100) > 19) { Internal[s] = 1; SectionGone[s] = false; return; }
                    Die(source);
                    BlowOffSection(location);
                    return;
            }
        }

        /// <summary>
        /// FUN_1005b528 0x1005b528 (Mech_DestroySection with param_4 = 0): from the root, the topmost nodes whose model carries
        /// this section (model +0x16, set from OBJL by Lod_SwapModel) come off with everything under them (FUN_10014e39):
        /// a node whose model is a 0x70 mount or has no section is hidden (FUN_10014cd0 -> FUN_10014eb1), every other one
        /// flies off as its own debris piece (Mw2Effects.ThrowPart), hidden instead when no debris record is free. A leg
        /// therefore breaks into its upper leg, lower leg and toes; the centre torso takes the whole remaining body.
        /// The parts can no longer be hit (their hit colliders are switched off).
        /// </summary>
        void BlowOffSection(int location)
        {
            if (!TryGetComponent<Mw2HitMap>(out var map)) return;
            TryGetComponent<Mw2LodGroup>(out var lods);
            int section = location - 1;
            var tops = new List<Transform>();
            for (int i = 0; i < map.Nodes.Count; i++)
            {
                var node = map.Nodes[i];
                if (map.Sections[i] != section || node == null || !node.IsChildOf(transform)) continue;
                bool top = true;
                for (var a = node.parent; a != null && a != transform && top; a = a.parent)
                {
                    int k = map.Nodes.IndexOf(a);
                    if (k >= 0 && map.Sections[k] == section) top = false;
                }
                if (top) tops.Add(node);
            }
            var fx = Mw2Effects.Instance;
            var nodes = new List<Transform>();
            foreach (var top in tops) CollectNodes(top, nodes);
            foreach (var node in nodes)
            {
                var own = new List<Renderer>();
                if (node.TryGetComponent<Renderer>(out var r0)) own.Add(r0);
                foreach (Transform child in node)
                    if (Mw2LodGroup.IsLodChild(child) && child.TryGetComponent<Renderer>(out var rc)) own.Add(rc);
                foreach (var r in own)
                    if (r.TryGetComponent<MeshCollider>(out var part)) part.enabled = false;
                var mesh = Mw2LodGroup.NodeMesh(node, lods);
                if (mesh != null && map.Nodes.Contains(node) && fx != null &&
                    fx.ThrowPart(node, lods, Mw2LodGroup.Radius(mesh.sharedMesh), mesh.sharedMesh.bounds.center)) continue;
                foreach (var r in own)
                    if (lods != null) lods.Hide(r); else r.enabled = false;
            }
            _renderers = null;
        }

        /// <summary>The node and every skeleton node under it (the "LOD{n}" children are its own models, not nodes).</summary>
        static void CollectNodes(Transform node, List<Transform> list)
        {
            list.Add(node);
            foreach (Transform child in node)
                if (!Mw2LodGroup.IsLodChild(child)) CollectNodes(child, list);
        }

        /// <summary>
        /// 0x10036aa9: the item in slot <paramref name="slot"/> of the section is hit. Weapon (id / 100 < 0x42): it is broken
        /// (state -1) and its id leaves every section. Ammunition (id >= 10000): the bin with that critical id empties into
        /// its pool (AmmoStore.Explode); unless silent, its rounds explode (rounds x damage per round) into the section's
        /// structure, which can destroy it and, without section flag 0x100, carry the rest to 3 (from 2, 4), 2 (from 5, 7)
        /// or 4 (from 6, 8) with another critical there; then the item leaves the section. Equipment (removed): 7300..7450
        /// and 7700 sensors / actuators (0x10017bd1), 7500..7650, 7800, 7850 speed -0.1 (7800 also loses the
        /// jump jets), 7750 destroys the unit, 8000 a heat sink, 8500 a jump jet, 9000 / 9500 roll again. A speed left
        /// between 0 and 0.4 becomes 0.4.
        /// Automatic ejection follows DAT_10099fb4 after live ammo detonates; 7900 (DAT_100a6bc4) does nothing.
        /// </summary>
        void Critical(int location, int slot, bool silent, Mw2MechCombat source)
        {
            int s = location - 1;
            var items = SectionItems[s];
            if (slot < 0 || slot >= items.Count) return;
            int id = items[slot];
            Autopilot.MissionTelemetry.Critical(this, location, id, silent, source);
            int kind = id / 100;
            // Mech_CriticalHit 0x10036aa9: a non-silent critical on the active player mech plays MECCHIN1.
            if (!silent && IsPlayer && Mw2Audio.Instance != null)
                Mw2Audio.Instance.Play(0xe1, transform.position, true);
            if (!silent && IsPlayer) CriticalVoices(id);
            if (kind < 0x42)
            {
                foreach (var w in Weapons) if (w.Id == id) w.Broken = true;
                foreach (var list in SectionItems) list.RemoveAll(x => x == id);
                return;
            }
            if (kind >= 100)
            {
                // The bin with this critical id empties into its pool (an emptied pool silences the pool's weapons) and,
                // unless silent, explodes: rounds x damage per round off the internal structure; then the item leaves the
                // section like any other (LAB_100379c3).
                var (rounds, perRound) = Ammunition.Explode(id, out bool emptied, out int pool);
                if (emptied)
                    foreach (var w in Weapons)
                        if (w.Pool == pool) w.State = -1;
                SyncAmmo();
                if (!silent && rounds != 0)
                {
                    // 0x10036aa9: ammunition detonation has a second, positioned sound (MECBACXX).
                    if (IsPlayer && Mw2PaletteFade.Instance != null) Mw2PaletteFade.Instance.RequestHitFlash();   // DAT_100a66cc = 1
                    if (IsPlayer && Mw2Audio.Instance != null)
                    {
                        Mw2Audio.Instance.Play(0xcd, transform.position);
                        Mw2Audio.Instance.CriticalVoice(1);      // "Critical hit : internal ammo explosion detected."
                    }
                    var fx = Mw2Effects.Instance;
                    if (fx != null) fx.Spawn(7, ModelCentre);      // 0x10011e0b(.., 7, ..)
                    Internal[s] -= Mathf.RoundToInt(rounds * perRound);
                    if (Internal[s] < 0)
                    {
                        int over = -Internal[s];
                        Front[s] = Rear[s] = Internal[s] = 0;
                        DestroySection(location, source);
                        // Section flag 0x100 keeps the explosion in the section.
                        int next = (SectionFlags[s] & 0x100) != 0 ? 0
                                 : location switch { 2 => 3, 4 => 3, 5 => 2, 6 => 4, 7 => 2, 8 => 4, _ => 0 };
                        if (next != 0 && !Destroyed)
                        {
                            Internal[next - 1] -= over;
                            if (SectionItems[next - 1].Count > 0)
                                Critical(next, UnityEngine.Random.Range(0, SectionItems[next - 1].Count), false, source);
                            if (Internal[next - 1] <= 0) DestroySection(next, source);
                        }
                    }
                    if (AutomaticEjection && !Destroyed) AutoEject(source);
                }
                if (slot < items.Count && items[slot] == id) items.RemoveAt(slot);
                return;
            }
            int group = id / 10 * 10;
            switch (group)
            {
                case 7300: case 7350: case 7400: case 7450:
                    if (IsPlayer) HudSensorDamage.Apply(GaugeDamage, false, n => UnityEngine.Random.Range(0, n));
                    break;
                case 7700:
                    if (IsPlayer) HudSensorDamage.Apply(GaugeDamage, true, n => UnityEngine.Random.Range(0, n));
                    break;
                case 7500: case 7550: case 7600: case 7650: case 7850:
                    SpeedFactor = Mathf.Max(0f, SpeedFactor - 0.1f);
                    break;
                case 7800:
                    SpeedFactor = Mathf.Max(0f, SpeedFactor - 0.1f);
                    if (TryGetComponent<Mw2MechController>(out var mc)) mc.JumpDisabled = true;
                    break;
                case 7750:
                    Die(source);
                    break;
                case 8000:
                    HeatSinks = Math.Max(0, HeatSinks - 1);
                    break;
                case 8500:
                    // 0x1003735f: a jump jet: the thrust +0xe20 loses 1 / [0x37e] of itself and the jump MP [0x37e] one;
                    // with none left the fuel state +0xdf4 becomes -2 (MechMotion derives the thrust from the jump MP).
                    if (TryGetComponent<Mw2MechController>(out var jets)) jets.JumpJetHit();
                    break;
                case 9000:
                case 9500:
                    // 0x100372f7 / 0x1003732e push 1 before RandomInt(count); it is
                    // the recursive call's fifth (silent) argument, not an RNG lower bound.
                    // Ghidra loses this argument. The silent reroll may hit the same slot
                    // and removes it without rerolling structural equipment again.
                    if (!silent) { Critical(location, UnityEngine.Random.Range(0, items.Count), true, source); return; }
                    break;
            }
            if (SpeedFactor != 0f && SpeedFactor < 0.4f) SpeedFactor = 0.4f;
            if (group == 8000)
                items.RemoveAll(x => x / 10 * 10 == 8000 && x == id);
            if (slot < items.Count) items.RemoveAt(slot);
        }

        /// <summary>Mech_Destroy 0x10035ef6. The unit's own deaths (Mech_Eject 0x10037fd6, Mech_UpdateHeat 0x10035a9b,
        /// Mech_HeatExplode 0x10035915) name the unit itself as the killer (gp +0x13c).</summary>
        void Die(Mw2MechCombat source)
        {
            if (Destroyed) return;
            // Mech_DestroySection / Mech_CriticalHit: another player's mech dies only by its own state (FUN_10021376).
            if (NetRemote && !_netDeath) return;
            // Mech_Destroy 0x10035ef6: mw2dif.cfg +1 spares the player, except after self-destruct (state 7).
            if (IsPlayer && Mw2Options.GameOptions.Invulnerable && PowerState != 7) return;
            Autopilot.MissionTelemetry.Death(this, source);
            CountDestroyed(source);
            Destroyed = true;
            if (IsPlayer && NetGame && Mw2NetGame.Instance != null) Mw2NetGame.Instance.LocalDied(source);
            Trigger = false;
            foreach (var b in GetComponents<Behaviour>())
                if (b is Mw2MechAI) b.enabled = false;
            // Mech_UpdatePhysics 0x1003880d keeps dead units falling and animating until they land.
            if (TryGetComponent<Mw2MechController>(out var controller)) controller.PowerEnabled = false;
            _killer = source;
            _deathEnd = 0;
            HideUnsectionedParts();
            DestroyedVoice();
            Debug.Log($"MW2: {DisplayName} destroyed{(source != null ? $" by {source.DisplayName}" : "")}");
        }

        /// <summary>
        /// Mech_Destroy 0x10035ef6, statistics DAT_101fd6e0: a unit of the player's team counts as ejected (state 5) or
        /// killed; the player's outcome +0x1d becomes 2 after an ejection in a breathable atmosphere (DAT_100a6bc4 = 0),
        /// otherwise 4; a kill by the player counts by the victim's side, mechs (GP +0 == 1) apart from other
        /// gamepieces; every unit with GP flags 0x1400 counts in the "star" kills whoever destroyed it.
        /// </summary>
        void CountDestroyed(Mw2MechCombat source)
        {
            var stats = Mw2MissionDirector.Stats;
            var player = PlayerUnit;
            if (player != null && Team == player.Team)
            {
                stats.Add(_stateFive ? CombatCareer.StarMembersEjected : CombatCareer.StarMembersKilled);
                AddWreck();
            }
            if (IsPlayer) stats.Data[CombatCareer.Outcome] = (byte)(_stateFive && !HostileAtmosphere ? 2 : 4);
            bool mech = GamepieceType == 1;
            if (source != null && source.IsPlayer)
            {
                if (mech && Side == 1) AddWreck();
                if (mech) stats.AddBySide(Side, CombatCareer.FriendlyMechKills, CombatCareer.EnemyMechKills, CombatCareer.NeutralMechKills);
                else stats.AddBySide(Side, CombatCareer.FriendlyOtherKills, CombatCareer.EnemyOtherKills, CombatCareer.NeutralOtherKills);
            }
            if ((UnitFlags & 0x1400) != 0)
            {
                if (mech) stats.AddBySide(Side, CombatCareer.StarFriendlyMechKills, CombatCareer.StarEnemyMechKills, CombatCareer.StarNeutralMechKills);
                else stats.AddBySide(Side, CombatCareer.StarFriendlyOtherKills, CombatCareer.StarEnemyOtherKills, CombatCareer.StarNeutralOtherKills);
            }
        }

        /// <summary>FUN_10059a10 0x10059a10: the wreck for salvage.mw2, as the unit is at its destruction. The section words
        /// are the MEK's +0x26 with 0x2000 for a lost section (the engine's other run-time bits are not kept here).</summary>
        void AddWreck()
        {
            if (MekBytes == null) return;
            var flags = new int[8];
            for (int i = 0; i < 8; i++) flags[i] = SectionFlags[i] | (SectionGone != null && SectionGone[i] ? 0x2000 : 0);
            Mw2MissionDirector.Salvage.Add(MekName, MekBytes, Front, Rear, Internal, SectionItems, flags, Difficulty);
        }

        /// <summary>State 5 at Mech_Destroy (set by Mech_Eject in a breathable atmosphere and by the automatic ejection).</summary>
        bool _stateFive;

        Mw2MechCombat _killer;      // gp +0x13c
        int _deathEnd;              // [0x36f] in state 4
        bool _blownApart;           // mech +0xe40 bit 0x100 after Mech_BlowApart 0x1003551a
        public bool BlownApart => _blownApart;
        /// <summary>Mech_UpdateControl 0x1003a449 sets mech+0xe40 bit 0x200 after the death effect deadline.</summary>
        public bool DeathControlFinished => Destroyed && _deathEnd != 0 && _now >= _deathEnd;

        /// <summary>Mech_BlowApart 0x1003551a: a dead unit that collides or lands hard throws its remaining parts and plays 0xa9.</summary>
        public void BlowApartOnImpact()
        {
            if (!Destroyed || _blownApart) return;
            _blownApart = true;
            for (int section = 1; section <= 8; section++) BlowOffSection(section);
            if (Mw2Audio.Instance != null) Mw2Audio.Instance.Play(0xa9, transform.position);
        }

        /// <summary>
        /// Mech_UpdateControl 0x1003a449 state 4 with FUN_1003292e 0x1003292e: the first update after Mech_Destroy (which
        /// clears [0x36f]) starts effect 0xd at the unit's origin (+0x50..+0x58, MGEO[0] above the feet) and sets [0x36f] =
        /// now + 0x712; every update before that tick (the first one included) has a 21 % chance (RandomInt(100) < 0x15) of an
        /// explosion at the origin moved by RandomNormal() / 2 units in x and z: effect 3 at 60 % (RandomInt(100) < 0x3c),
        /// else 0x10b (debris). The killer (+0x13c) owns them. Afterwards bit 0x200 stops the control update. An ejected
        /// player stays in state 5 (Mech_Destroy 0x10035ef6) and gets none. Like the original, the chance is per update
        /// (per frame), not per tick.
        /// </summary>
        void DeathExplosions()
        {
            _clock += Time.deltaTime * MechMotion.TicksPerSecond;
            int now = (int)_clock, dt = now - _now;
            _now = now;
            if (dt <= 0 || (_deathEnd != 0 && now >= _deathEnd)) return;
            if (IsPlayer && PowerState == 5) return;
            var fx = Mw2Effects.Instance;
            var origin = Origin;
            if (_deathEnd == 0)
            {
                if (fx != null) fx.Spawn(0xd, origin, null, null, _killer);
                _deathEnd = now + 0x712;
            }
            if (UnityEngine.Random.Range(0, 100) >= 0x15) return;
            const float units = MW2.Formats.PolyModel.UnitsToMeters;
            origin.x += (int)Mw2Effects.Gauss() / 2 * units;
            origin.z += (int)Mw2Effects.Gauss() / 2 * units;
            if (fx != null) fx.Spawn(UnityEngine.Random.Range(0, 100) < 0x3c ? 3 : 0x10b, origin, null, null, _killer);
        }

        /// <summary>
        /// Mech_Destroy 0x10035ef6 -> Lod_SetForGamepiece(gp, 1) 0x1001e9df: with the destroyed flag a part without its own
        /// OBJL section (record +0x20, Objl_SetSection 0x1001f074: the DECL decals, mounts) is hidden (FUN_10049335), the
        /// others are shown at representation index 1 (Mw2LodSwitcher keeps dead units there, the player at 0).
        /// </summary>
        void HideUnsectionedParts()
        {
            if (!TryGetComponent<Mw2HitMap>(out var map)) return;
            TryGetComponent<Mw2LodGroup>(out var lods);
            foreach (var r in GetComponentsInChildren<Renderer>(true))
            {
                if (map.HasOwnSection(r.transform)) continue;
                if (lods != null) lods.Hide(r); else r.enabled = false;
            }
            _renderers = null;
        }

        /// <summary>
        /// Nearest hostile unit: another side that is not neutral, the rule of the AI scene selector 0x4201
        /// (FUN_1000aa2f 0x1000aa2f skips FUN_1002a83a same side and FUN_1002a7c1 = 2).
        /// </summary>
        public Mw2MechCombat NearestEnemy(float maxDistance)
        {
            Mw2MechCombat best = null;
            float bestDistance = maxDistance;
            foreach (var c in All)
            {
                if (c == this || c.Destroyed || c.Side == Side || c.Side == 2) continue;
                float d = Vector3.Distance(c.transform.position, transform.position);
                if (d < bestDistance) { best = c; bestDistance = d; }
            }
            return best;
        }

        /// <summary>A fire control is held: the player's fire / group keys, or the AI trigger.</summary>
        bool FireHeld => IsPlayer ? _keyFire || _keyGroups0 || _keyGroups1 || _keyGroups2 : Trigger;

        /// <summary>
        /// The player's weapon and pilot controls from the control block (Mw2Input): +0x25 weapon_fire, +0x26
        /// weapon_cycle, +0x27 fire group (FIRE_WEAPON_GROUP), +0x28..+0x2a groups 1..3, +0x2b next group, +0x2c chain /
        /// group toggle, +0x2d fire all (alpha strike); gamekey commands of Input_Command 0x100267c0 / Mech_PlayerCommands
        /// 0x1003b7dd: ADD_WEAPON_TO_GROUP_1..5 (0xb0, 0xb1, 0xb2, 0xc2, 0xc4: Weapon_SetSelectedGroup 0x1006d38a),
        /// EJECT 0x52, TOGGLE_AUTOEJECT 0x53, STARTUP_MECH 0x54 / SHUTDOWN_MECH 0x55 (DAT_100ad364: start when shut
        /// down, else shut down), OVERRIDE_SHUTDOWN 0x57, JETTISON_AMMO 0x2b (DAT_100ad348).
        /// </summary>
        void PlayerInput()
        {
            if (!Mw2Input.Loaded) return;
            _keyFire = Mw2Input.Held(0x25);
            _keyGroupFire = Mw2Input.Held(0x27);
            _keyGroups0 = Mw2Input.Held(0x28);
            _keyGroups1 = Mw2Input.Held(0x29);
            _keyGroups2 = Mw2Input.Held(0x2a);
            _keyCycleGroup = Mw2Input.Held(0x2b);
            if (Mw2Input.Held(0x2d)) _keyAlpha = true;
            if (Mw2Input.Held(0x2c)) _keyToggle = true;
            int[] groupCommands = { 0xb0, 0xb1, 0xb2, 0xc2, 0xc4 };
            for (int k = 0; k < groupCommands.Length; k++)
                if (Mw2Input.Command(groupCommands[k]) && SelectedWeapon >= 0 && SelectedWeapon < Weapons.Count)
                    Weapons[SelectedWeapon].Group = k;
            if (Mw2Input.Held(0x26))
            {
                // 0x1003b7dd: MECWPTG1 at half volume, then 0x1006cd92 with DAT_100ad358 = 1: the next slot that holds a weapon.
                if (Mw2Audio.Instance != null) Mw2Audio.Instance.Play(0x112, transform.position, true, 0x32);
                if (Weapons.Count > 0) SelectedWeapon = (Mathf.Clamp(SelectedWeapon, 0, Weapons.Count - 1) + 1) % Weapons.Count;
            }
            if (_keyCycleGroup && !_keyGroupFire) SelectNextGroup();     // 0x1003b7dd: +0x2b without +0x27 -> 0x1006d3c6
            if (Mw2Input.Command(0x53))
            {
                // Command 0x53: toggle automatic ejection, default ON.
                AutomaticEjection = !AutomaticEjection;
                var cockpit = Mw2Cockpit.Player;
                if (cockpit != null) cockpit.ShowMessage(AutomaticEjection ? "Automatic ejection ON" : "Automatic ejection OFF");
            }
            if (Mw2Input.Command(0x57)) OverrideShutdown();
            // SHUTDOWN_MECH is ignored in state 7; STARTUP_MECH would request a shutdown there, which state 7 never takes.
            if ((Mw2Input.Command(0x54) || Mw2Input.Command(0x55)) && PowerState != 7) ToggleManualShutdown();
            if (Mw2Input.Command(0x2b)) JettisonAmmo();
            // Command 0x52 -> Mech_Eject 0x10037fd6.
            if (Mw2Input.Command(0x52)) { Eject(); return; }
            ReadTargetKeys();
        }

        /// <summary>Weapon_SelectNextGroup 0x1006cea4: the first weapon of the next group (mod 14) that has one.</summary>
        void SelectNextGroup()
        {
            if (Weapons.Count == 0) return;
            int group = Weapons[Mathf.Clamp(SelectedWeapon, 0, Weapons.Count - 1)].Group;
            for (int g = (group + 1) % 14; g != group; g = (g + 1) % 14)
                for (int i = 0; i < Weapons.Count; i++)
                    if (Selectable(i) && Weapons[i].Group == g) { SelectedWeapon = i; return; }
        }

        /// <summary>Debug panels (this text and the objectives list), port only: Ctrl+F12 toggles them (no gamekey.map command
        /// uses code 0x1bc; ` is REVERSE_DIRECTION and F12 DISPLAY_OBJECTIVES).</summary>
        public static bool ShowDebug;

        void OnGUI()
        {
            if (!IsPlayer) return;
            if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.F12 && Event.current.control)
            {
                ShowDebug = !ShowDebug;
                Event.current.Use();
            }
            if (!ShowDebug) return;
            var lines = new List<string> { $"{DisplayName}   heat {Heat:0.0}{(Heat > 65 ? " WARNING" : "")}{(ShutDown ? "  SHUTDOWN" : "")}{(Destroyed ? "   DESTROYED" : "")}" };
            for (int s = 0; s < 8; s++) lines.Add($"{MekLoadout.SectionNames[s],-13} {Front[s],3} / {Rear[s],3}  int {Internal[s],3}");
            lines.Add("");
            foreach (var w in Weapons)
            {
                string state = Internal[Mathf.Max(0, w.Section)] <= 0 ? "destroyed" : w.State == 1 ? "ready" : w.State == 2 ? "firing" : "recycling";
                lines.Add($"{w.Def.Name,-8} {state,-9} {(w.Ammo >= 0 ? $"ammo {w.Ammo}" : "")}");
            }
            if (Target != null)
            {
                lines.Add("");
                lines.Add($"Target: {Target.DisplayName}  {Vector3.Distance(Target.transform.position, transform.position):0} m{(Target.Destroyed ? "  destroyed" : "")}");
                lines.Add($"  CT {Target.Front[2]}/{Target.Internal[2]}  head {Target.Front[0]}/{Target.Internal[0]}");
            }
            lines.Add("");
            if (TryGetComponent<Mw2TorsoTwist>(out var tw)) lines.Add($"Torso twist {tw.Current:0}° (limit {tw.Limit:0}°)  pitch {tw.Pitch:0}°");
            lines.Add("Keys: game folder inputw.map / gamekey.map");
            var text = new GUIContent(string.Join("\n", lines));
            float height = GUI.skin.label.CalcHeight(text, 300);
            GUI.Box(new Rect(10, 10, 316, height + 12), GUIContent.none);
            GUI.Label(new Rect(18, 16, 300, height), text);
        }
    }
}
