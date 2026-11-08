using System;
using System.Collections.Generic;
using System.Linq;
using MW2.Formats;
using UnityEngine;

namespace MW2
{
    /// <summary>
    /// The player's target selection of mech +0xdc (kind 0x100 nav point, 0x200 unit, 0x400 GT, 0x1000 none) and of the
    /// nav target +0x104, which a nav selection also writes. Keys from GAMEKEY.MAP and
    /// the command table at 0x10093548 (5-byte records: name pointer, id): NEXT_TARGET t 0x64, PREV_TARGET r 0x65,
    /// TARGET_NEAREST_ENEMY e 0x68, TARGET_FRIENDLY f 0x69, TARGET_AT_RETICLE q 0x6a, INSPECT_TARGET i 0x29 set the
    /// player control block DAT_100a7ae8 bytes +0x34, +0x35, +0x39, +0x38, +0x37, +0x3b (FUN_100267c0 0x100267c0);
    /// RESET_TARGETTING Ctrl+t 0x3e clears the target at once (FUN_10066ed5 0x10066ed5); NEXT_NAVPOINT n 0x3f and
    /// PREV_NAVPOINT Shift+n 0x40 set +0x31 / +0x32 (Target_CycleNav 0x10067e4c). Mech_UpdateControl 0x1003a449
    /// handles the bytes only in power state 2, so a key pressed while shut down waits for the start-up, and then
    /// revalidates the target every update (FUN_10067668 0x10067668).
    /// Not ported: nav points attached to objects (NAVO, record +4), private nav points (flag 1 with an
    /// owner +8, set by the AI at run time), and the autopilot toggle (+0x43) these functions request on a new target.
    /// </summary>
    public partial class Mw2MechCombat
    {
        [Tooltip("Index of the unit's GP (GPS load order, World_LoadChunks 0x10070e92): order of the target cycle.")]
        public int GamepieceIndex;
        [Tooltip("Team 0..15 of the unit (GPS +4, gamepiece +8); its bit marks the targets it has identified.")]
        public int Team;
        [Tooltip("GP +0x14 flags from GPS +0x1A: 0x400 targetable / on radar, 0x800 hidden from the player.")]
        public int UnitFlags = 0x400;
        [Tooltip("GP +0x126 (GPS +0x44): the text Hud_DrawTargetText 0x10065990 shows when the unit is inspected.")]
        public string InspectText;

        /// <summary>
        /// GP +0x14 as the engine sees it: the GPS flags, 0x6 once destroyed (Mech_Destroy 0x10035ef6), 0x10 while shut
        /// down (state 3 of 0x1003a449, AI orders 10/11 in FUN_1000a5e5 0x1000a5e5), 0x20 once identified.
        /// </summary>
        public int GamepieceFlags => UnitFlags | (Destroyed ? 6 : 0) | (ShutDown || PoweredDown ? 0x10 : 0) |
                                     (IdentifiedTeams != 0 ? 0x20 : 0);

        /// <summary>
        /// Range limit of the target checks: distance +0xc4 at most 175000 units (_DAT_10088390) unless flag 0x1000.
        /// Vector_ToSpherical 0x10067cff returns the 3D distance in its fifth output (+0xc4) and the flat one in the sixth (+0xc0).
        /// </summary>
        public const float TargetRangeUnits = 175000f;
        /// <summary>Inspection reach: 20000 units (_DAT_10088394) plus the target's radius (GP +0xe1c, GT +0x10).</summary>
        public const float InspectRangeUnits = 20000f;

        /// <summary>
        /// DAT_100b18c8, the result of the last inspection for Hud_DrawTargetText: 1 identified, 2 out of range, 3 already
        /// identified, 0 nothing. 0x10067668 clears it on every pass; the cockpit consumes it (ConsumeInspectStatus).
        /// </summary>
        public int InspectStatus { get; private set; }

        /// <summary>DAT_100b18cc: set after TARGET_AT_RETICLE, cleared at the start of every target cycle of the player.</summary>
        static bool _reticlePick;
        bool _keyNextTarget, _keyPrevTarget, _keyNearestEnemy, _keyFriendly, _keyReticle, _keyInspect, _keyNextNav, _keyPrevNav;

        /// <summary>Mech +0xdf0 = 1: the autopilot flies the nav route (FUN_100355e0 0x100355e0, Mw2MechControllerAutopilot).</summary>
        public bool AutopilotNavMode => TryGetComponent<Mw2MechController>(out var c) && c.Autopilot;

        /// <summary>The mission director whose nav points the target ring uses.</summary>
        public Mw2MissionDirector NavDirector => TargetDirector;

        /// <summary>Mech +0xdc of kind 0x100: index of the targeted nav point in the director's Navs, -1 otherwise.</summary>
        [NonSerialized] public int TargetNav = -1;
        /// <summary>Mech +0x104: the nav target (index), -1 for flag 0x1000. Written whenever a nav becomes the target.</summary>
        [NonSerialized] public int NavTarget = -1;

        Mw2MissionDirector _targetDirector;
        Mw2MissionDirector TargetDirector
        {
            get
            {
                if (_targetDirector == null) _targetDirector = FindAnyObjectByType<Mw2MissionDirector>();
                return _targetDirector;
            }
        }

        public bool HasTarget => Target != null || TargetStructure != null || TargetNav >= 0;

        /// <summary>The cockpit reads and clears the inspection result (Hud_DrawTargetText 0x10065990).</summary>
        public int ConsumeInspectStatus()
        {
            int s = InspectStatus;
            InspectStatus = 0;
            return s;
        }

        /// <summary>FUN_10066ed5 0x10066ed5 (and failed searches): target flag 0x1000, no target.</summary>
        public void ClearTarget()
        {
            Target = null;
            TargetStructure = null;
            TargetNav = -1;
        }

        void SetTarget(Mw2MechCombat unit, Mw2Destructible structure, int nav = -1)
        {
            Target = unit;
            TargetStructure = unit != null ? null : structure;
            TargetNav = unit != null || structure != null ? -1 : nav;
            if (TargetNav >= 0) NavTarget = TargetNav;      // 0x10066c17: kind 0x100 also writes +0x104
        }

        /// <summary>
        /// Target_CheckNav 0x10066f01: a nav point under filter <paramref name="filter"/> (0xe bits exclude nav points).
        /// Rejected: a start point (+0 = 0), without flag 0x40 under filter 0x10000, a private one (flag 1 whose owner +8
        /// is not this unit; no data sets it), a public one under filter 0x100, and another team's (+0xc). No range limit.
        /// </summary>
        bool CheckNav(int index, int filter)
        {
            var director = TargetDirector;
            if ((filter & 0xe) != 0 || director == null || index < 0 || index >= director.Navs.Count) return false;
            var nav = director.Navs[index];
            if (nav.Type == 0) return false;
            if ((filter & 0x10000) != 0 && (nav.Flags & 0x40) == 0) return false;
            if ((nav.Flags & 1) != 0) return false;
            if ((filter & 0x100) != 0) return false;
            return nav.Team == Team;
        }

        static float FlatDistance(Vector3 a, Vector3 b) => new Vector2(a.x - b.x, a.z - b.z).magnitude;

        // Target_CheckGp10067128/Target_CheckGt10067406 use GP+50..58,
        // not the Unity foot transform. SPA1 aircraft have MGEO[0]=2130m.
        // Keep Vector_ToSpherical10067cff's table-based distance for its gates.
        float TargetDistance(Vector3 point)
        {
            var delta = (point - Origin) / PolyModel.UnitsToMeters;
            AiLeadPoint.Spherical(new AiLeadPoint.Vector(delta.x, delta.y, delta.z),
                out _, out float distance, out _, out _);
            return distance * PolyModel.UnitsToMeters;
        }

        /// <summary>
        /// FUN_10067128 0x10067128 for the player: whether unit <paramref name="c"/> may be the target under filter
        /// <paramref name="filter"/> (0x15 bits exclude units, 0x20000 side 0 only, 0x40000 side 1 only, 0x10000 objective
        /// units only). Rejected: flag 0x10 (shut down) unless picked at the reticle, 0x6 (destroyed), the player itself,
        /// no flag 0x1400 unless picked at the reticle, flag 0x800, and a distance (+0xc4, 3D) over 175000 units without 0x1000.
        /// </summary>
        bool CheckUnit(Mw2MechCombat c, int filter)
        {
            if ((filter & 0x15) != 0 || c == null || c == this) return false;
            int f = c.GamepieceFlags;
            if (!_reticlePick && (f & 0x10) != 0) return false;
            if ((f & 6) != 0) return false;
            if ((filter & 0x10000) != 0 && (f & 0x40) == 0) return false;
            if ((filter & 0x20000) != 0 && c.Side != 0) return false;
            if ((filter & 0x40000) != 0 && c.Side != 1) return false;
            if (!_reticlePick && (f & 0x1400) == 0) return false;
            if ((f & 0x800) != 0) return false;
            return (f & 0x1000) != 0 ||
                   TargetDistance(c.Origin) <= TargetRangeUnits * PolyModel.UnitsToMeters;
        }

        /// <summary>
        /// FUN_10067406 0x10067406 for the player: a GT under filter <paramref name="filter"/> (0x23 bits exclude GTs).
        /// Rejected: flag 0x4 (destroyed), the side filters by Gt_Side 0x1002a7dc, no flag 0x1400 unless picked at the
        /// reticle, flag 0x800, and a 3D distance over 175000 units without 0x1000.
        /// </summary>
        bool CheckStructure(Mw2Destructible g, int filter)
        {
            if ((filter & 0x23) != 0 || g == null) return false;
            int f = g.GtFlags;
            if ((f & 4) != 0) return false;
            if ((filter & 0x20000) != 0 && g.Side != 0) return false;
            if ((filter & 0x40000) != 0 && g.Side != 1) return false;
            if (!_reticlePick && (f & 0x1400) == 0) return false;
            if ((f & 0x800) != 0) return false;
            return (f & 0x1000) != 0 ||
                   TargetDistance(g.transform.position) <= TargetRangeUnits * PolyModel.UnitsToMeters;
        }

        /// <summary>One entry of the target ring: a unit, a GT or a nav point index.</summary>
        struct RingEntry
        {
            public Mw2MechCombat Unit;
            public Mw2Destructible Structure;
            public int Nav;
            public bool Same(RingEntry o) => Unit == o.Unit && Structure == o.Structure && Nav == o.Nav;
            public Vector3 Position(Mw2MissionDirector d) => Unit != null ? Unit.Origin
                : Structure != null ? Structure.transform.position : d.Navs[Nav].Position;
        }

        /// <summary>Units, GTs, then nav points in index order: the ring 0x200 -> 0x400 -> 0x100 -> 0x200 of 0x10066c17.</summary>
        List<RingEntry> TargetRing()
        {
            var ring = new List<RingEntry>();
            foreach (var c in All.OrderBy(c => c.GamepieceIndex)) ring.Add(new RingEntry { Unit = c, Nav = -1 });
            foreach (var g in Mw2Destructible.All.OrderBy(g => g.Index)) ring.Add(new RingEntry { Structure = g, Nav = -1 });
            var director = TargetDirector;
            if (director != null) for (int i = 0; i < director.Navs.Count; i++) ring.Add(new RingEntry { Nav = i });
            return ring;
        }

        bool CheckRingEntry(RingEntry e, int filter) =>
            e.Unit != null ? CheckUnit(e.Unit, filter) : e.Structure != null ? CheckStructure(e.Structure, filter) : CheckNav(e.Nav, filter);

        RingEntry CurrentEntry => new RingEntry
        {
            Unit = Target,
            Structure = Target != null ? null : TargetStructure,
            Nav = Target != null || TargetStructure != null ? -1 : TargetNav
        };

        /// <summary>
        /// FUN_10066c17 0x10066c17: from the current target step <paramref name="direction"/> through the ring until an entry
        /// passes the filter; with direction 0 or no target the search starts at the first unit and goes forward. The
        /// whole ring is tried once, the current target last. Nothing found leaves no target.
        /// Filters: 8 units and GTs (t / r), 0x20008 side 0 (f), 0x40008 side 1 (e), 2 units only, 4 GTs only.
        /// </summary>
        public void CycleTarget(int direction, int filter)
        {
            _reticlePick = false;
            var ring = TargetRing();
            int n = ring.Count;
            var cur = CurrentEntry;
            int current = HasTarget ? ring.FindIndex(e => e.Same(cur)) : -1;
            int start, count;
            if (direction == 0 || current < 0) { direction = 1; start = 0; count = n; }
            else { start = current + direction; count = n; }
            for (int k = 0; k < count && n > 0; k++)
            {
                var e = ring[((start + k * direction) % n + n) % n];
                if (!CheckRingEntry(e, filter)) continue;
                SetTarget(e.Unit, e.Structure, e.Nav);
                // 0x10066c17: without a nav target (+0x104 & 0x100) a running autopilot is toggled off (+0x43 = 1).
                if (IsPlayer && NavTarget < 0 && AutopilotNavMode) Mw2Input.Set(0x43);
                return;
            }
            ClearTarget();
        }

        /// <summary>
        /// FUN_10067f28 0x10067f28 (e): over the side-1 ring (0x40008) the entry with the smallest distance (+0xc4, 3D),
        /// first found on ties; none, or beyond 175000 units, keeps the previous target; else it becomes the target and
        /// is revalidated (FUN_10067668), restoring the previous one if that fails.
        /// </summary>
        public void TargetNearestEnemy()
        {
            var saved = CurrentEntry;
            _reticlePick = false;
            RingEntry? best = null;
            float bestDistance = 3.4e38f;
            foreach (var e in TargetRing())
            {
                if (!CheckRingEntry(e, 0x40008)) continue;
                float d = TargetDistance(e.Position(TargetDirector));
                if (d < bestDistance) { best = e; bestDistance = d; }
            }
            if (best == null || bestDistance > TargetRangeUnits * PolyModel.UnitsToMeters)
            {
                Restore(saved);
                return;
            }
            SetTarget(best.Value.Unit, best.Value.Structure);
            if (!ValidateTarget()) Restore(saved);
        }

        /// <summary>
        /// FUN_10067b80 0x10067b80 (q): the object under the reticle (FUN_1006dc4a 0x1006dc4a: Weapon_AimRay and the
        /// segment test 0x10018ad5; object flag 0x100 a unit part, 0x200 a GT) becomes the target if it passes the check
        /// with the current reticle state; it is then revalidated, restoring the previous target on failure. Afterwards
        /// DAT_100b18cc = 1, so later checks accept shut-down units and GTs without flag 0x1400.
        /// </summary>
        public void TargetAtReticle()
        {
            Mw2MechCombat unit = null;
            Mw2Destructible structure = null;
            TryGetComponent<Mw2TorsoTwist>(out var twist);
            WeaponAimRay(twist, out var eye, out var aim);
            // 10067bb3 calls Target_PickReticle, including both integer approach updates.
            // Target commands run before this component advances its per-object Unity clock.
            int tick = (int)(_clock + Time.deltaTime * MechMotion.TicksPerSecond);
            if (RefreshAimConvergence(tick, eye, aim, out var hit))
            {
                if (hit.collider.TryGetComponent<Mw2HitPart>(out _) || hit.collider is BoxCollider)
                    unit = hit.collider.GetComponentInParent<Mw2MechCombat>();
                if (unit == null) structure = hit.collider.GetComponentInParent<Mw2Destructible>();
            }
            bool accepted = unit != null ? CheckUnit(unit, 0) : structure != null && CheckStructure(structure, 0);
            if (accepted && (unit != Target || structure != TargetStructure || TargetNav >= 0))
            {
                var saved = CurrentEntry;
                SetTarget(unit, structure);
                if (!ValidateTarget()) Restore(saved);
            }
            _reticlePick = true;
        }

        /// <summary>Puts back a saved +0xdc (the original stores the word; +0x104 stays as the search wrote it).</summary>
        void Restore(RingEntry saved)
        {
            Target = saved.Unit;
            TargetStructure = saved.Structure;
            TargetNav = saved.Nav;
        }

        /// <summary>
        /// FUN_10067668 0x10067668: the current target must still pass its check (filter 0), else it is dropped. A nav
        /// target the player is inside (flat distance +0xc0 below its radius +0x10, autopilot not in nav mode) gets the
        /// visited flag 0x20 with MECNAVPT the first time, and the target moves on to the next nav point
        /// (Target_CycleNav 0x10067e4c, 1). With the
        /// inspect byte (+0x3b) set, the byte is cleared and a target the player's team has not identified is identified
        /// when its flat distance (+0xc0) is below 20000 units plus its radius: flag 0x20 and the team bit in +0x16 (GT +2);
        /// result 1, 2 too far, 3 already identified (DAT_100b18c8). A failed target skips the clear at 0x10067864,
        /// but Input_Frame 0x1002c516 clears +0x3b at the start of the next frame.
        /// </summary>
        public bool ValidateTarget()
        {
            bool ok = Target != null ? CheckUnit(Target, 0) : TargetStructure != null ? CheckStructure(TargetStructure, 0)
                : TargetNav >= 0 && CheckNav(TargetNav, 0);
            if (!ok)
            {
                ClearTarget();
                return false;
            }
            if (TargetNav >= 0)
            {
                var director = TargetDirector;
                var nav = director.Navs[TargetNav];
                if (FlatDistance(transform.position, nav.Position) < nav.Radius && !AutopilotNavMode)
                {
                    if (director.MarkNavVisited(TargetNav, Team) && Mw2Audio.Instance != null)
                        Mw2Audio.Instance.Play(0xfc, transform.position, true);
                    CycleTarget(1, 1);
                }
            }
            InspectStatus = 0;
            if (!_keyInspect) return true;
            _keyInspect = false;
            Mw2Input.Clear(0x3b); // Target_Validate 0x10067864 consumes INSPECT_TARGET only after a valid target.
            if (Target == null && TargetStructure == null) return true;   // the nav branch of 0x10067668 does not identify
            int bit = 1 << (Team & 0x1f);
            float distance = TargetDistance(Target != null ? Target.Origin : TargetStructure.transform.position);
            if (Target != null)
            {
                if ((Target.IdentifiedTeams & bit) != 0) InspectStatus = 3;
                else
                {
                    float radius = Target.SphereRadius;
                    if (InspectRangeUnits * PolyModel.UnitsToMeters + radius <= distance) InspectStatus = 2;
                    else { InspectStatus = 1; Target.IdentifiedTeams |= bit; }
                }
            }
            else if ((TargetStructure.IdentifiedTeams & bit) != 0) InspectStatus = 3;
            else if (InspectRangeUnits * PolyModel.UnitsToMeters + TargetStructure.Radius <= distance) InspectStatus = 2;
            else { InspectStatus = 1; TargetStructure.IdentifiedTeams |= bit; }
            return true;
        }

        /// <summary>
        /// Reads the target controls of the control block (Mw2Input; Input_Command 0x100267c0 sets them from gamekey.map,
        /// inputw.map can bind them too): +0x31 / +0x32 nav, +0x34 / +0x35 next / previous, +0x37 reticle, +0x38 friendly,
        /// +0x39 nearest enemy, +0x3b inspect, +0x33/+0x36 reset cycles, +0x3c next objective,
        /// +0x3d..0x3f GT and +0x40..0x42 GP. RESET_TARGETTING (0x3e) clears the target at once (FUN_10066ed5).
        /// Input_Frame 0x1002c516 clears all type-1/2 control bytes at the start of each frame. Commands from
        /// GAMEKEY.MAP are then set for that frame; Mech_UpdateControl clears the consumed target bytes immediately.
        /// </summary>
        void ReadTargetKeys()
        {
            if (Mw2Input.Command(0x3e)) ClearTarget();
            _keyNextNav = Mw2Input.Held(0x31);
            _keyPrevNav = Mw2Input.Held(0x32);
            _keyNextTarget = Mw2Input.Held(0x34);
            _keyPrevTarget = Mw2Input.Held(0x35);
            _keyReticle = Mw2Input.Held(0x37);
            _keyFriendly = Mw2Input.Held(0x38);
            _keyNearestEnemy = Mw2Input.Held(0x39);
            _keyInspect = Mw2Input.Held(0x3b);
        }

        /// <summary>
        /// The target part of Mech_UpdateControl 0x1003a449 in power state 2, in its order: +0x31 / +0x32 next / previous nav
        /// point (Target_CycleNav 0x10067e4c: 0x10066c17 with filter 1), +0x34 next (0x10066c17, 1, 8),
        /// +0x35 previous (-1, 8), +0x39 nearest enemy (0x10067f28), +0x38 friendly (0x10067ed0: 1, 0x20008),
        /// +0x3c objective (1, 0x10008), +0x3d..0x3f GT (filter 4), +0x40..0x42 GP (filter 2), +0x37 at
        /// reticle (0x10067b80), then FUN_10067668 while there is a target.
        /// </summary>
        void UpdatePlayerTargeting()
        {
            if (_keyNextNav) { _keyNextNav = false; CycleTarget(1, 1); Mw2Input.Clear(0x31); }
            if (_keyPrevNav) { _keyPrevNav = false; CycleTarget(-1, 1); Mw2Input.Clear(0x32); }
            if (Mw2Input.Held(0x33)) { CycleTarget(0, 1); Mw2Input.Clear(0x33); }
            if (_keyNextTarget) { _keyNextTarget = false; CycleTarget(1, 8); Mw2Input.Clear(0x34); }
            if (_keyPrevTarget) { _keyPrevTarget = false; CycleTarget(-1, 8); Mw2Input.Clear(0x35); }
            if (Mw2Input.Held(0x36)) { CycleTarget(0, 8); Mw2Input.Clear(0x35); }
            if (_keyNearestEnemy) { _keyNearestEnemy = false; TargetNearestEnemy(); Mw2Input.Clear(0x39); }
            if (_keyFriendly) { _keyFriendly = false; CycleTarget(1, 0x20008); Mw2Input.Clear(0x38); }
            if (Mw2Input.Held(0x3a)) Mw2Input.Clear(0x3a); // 0x1003a449: TARGET_LAST_SHOT has no selection callback.
            if (Mw2Input.Held(0x3c)) { CycleTarget(1, 0x10008); Mw2Input.Clear(0x3c); }
            if (Mw2Input.Held(0x3d)) { CycleTarget(1, 4); Mw2Input.Clear(0x3d); }
            if (Mw2Input.Held(0x3e)) { CycleTarget(-1, 4); Mw2Input.Clear(0x3e); }
            if (Mw2Input.Held(0x3f)) { CycleTarget(0, 4); Mw2Input.Clear(0x3e); }
            if (Mw2Input.Held(0x40)) { CycleTarget(1, 2); Mw2Input.Clear(0x40); }
            if (Mw2Input.Held(0x41)) { CycleTarget(-1, 2); Mw2Input.Clear(0x41); }
            if (Mw2Input.Held(0x42)) { CycleTarget(0, 2); Mw2Input.Clear(0x41); }
            if (_keyReticle) { _keyReticle = false; TargetAtReticle(); Mw2Input.Clear(0x37); }
            if (HasTarget) ValidateTarget();
        }
    }
}
