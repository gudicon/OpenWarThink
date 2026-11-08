using System.Collections.Generic;
using MW2.Formats;
using UnityEngine;

namespace MW2
{
    /// <summary>
    /// The attack state's maneuvers. State 3 (FUN_10009f0e) clears the fire bytes and runs AI_UpdateMechManeuver 0x10062270
    /// with the state target (+0x178): without a maneuver (+0x1c4 = -1) AI_SelectMechManeuver 0x10062aa8 picks one and
    /// AI_StartMechManeuver 0x10062dd7 starts it; otherwise its handler runs, then the end rules: an armed-out unit (weapons,
    /// none usable, FUN_10064c70) that may move, holds no controls (+0x1ea) and is neither in maneuver 6 nor fleeing forces
    /// flee (-2) or maneuver 6 (RandomInt(100) above +0x1ac; only mech pilots of skill 1 may pick 6) at 25 %, after maneuver 5
    /// or without jets (FUN_10064e99 40), else a jump (4, +0x1d8 = 1); a mech bumping into something (FUN_10065453), the end
    /// tick +0x1cc or a forced maneuver (+0x1c8) end it (FUN_10063171).
    /// Tables (+0x202 = 0x100a0470 + GP type * 8): mechs 12 maneuvers drawn first from 0..4, aircraft (types 2 / 8) 2, 3, 7, 13
    /// from 2 / 3, every other type only 0. Pilot skill +0x187 (GPS +0x10, 0 -> 1, clamped 1..6, FUN_1006280a) sets the flags
    /// +0x1f2: bit 0 skill below 4 (hop to turn, FUN_10064d7b), bit 1 below 4 (jump maneuvers), bit 2 below 3, bit 3 mech below
    /// 6 (else the twist limit becomes 0), bit 4 below 5, bit 5 mech below 2 (may ram), bit 6 below 6.
    /// Private nav points (Nav_CreatePrivate 0x10066940, radius 3000 units) are kept on the pilot: only their owner steers
    /// to them (Target_CheckNav filter 0x101) and FUN_1000b046 drops them all, as an AI pilot has no nav target (+0x1a2).
    /// Approximations: the target point of a unit is its origin (Mw2MechCombat.Origin); the nav created around a unit takes
    /// its height from the unit's heading as the original does (+0x60 of the game piece) - only flat distances matter.
    /// </summary>
    public partial class Mw2MechAI
    {
        [Tooltip("Pilot skill +0x187 (GPS body +0x10, 0 -> 1, 1..6; 1 = best).")]
        [Range(1, 6)] public int PilotSkill = 1;
        [Tooltip("+0x1ac: percent of armed-out pilots that flee rather than ram (GPS body +0x12: 0 / 1 -> 33, 2 -> 100).")]
        public int FleePercent = 33;

        int _prevManeuver = -1;         // +0x1c6
        int _forcedManeuver;            // +0x1c8: 0 none, -2 flee, else the next maneuver
        int _maneuverEnd;               // +0x1cc
        int _phaseTick;                 // +0x1d0
        int _phase;                     // +0x1d4
        int _phaseFlag;                 // +0x1d8
        bool _holdControls;             // +0x1ea
        float _lastRange;               // +0x1ee
        /// <summary>Game piece +0x1f6 of every unit (the player's too): how many attackers took each of its eight slots (maneuver 1).</summary>
        static readonly Dictionary<Mw2MechCombat, int[]> s_attackSlots = new Dictionary<Mw2MechCombat, int[]>();

        static int[] SlotsOf(Mw2MechCombat unit)
        {
            if (unit == null) return new int[8];
            if (!s_attackSlots.TryGetValue(unit, out var slots)) s_attackSlots[unit] = slots = new int[8];
            return slots;
        }
        AiTarget _steer;                // +0x178 while a maneuver runs
        readonly List<AiTarget> _privateNavs = new List<AiTarget>();
        bool _maneuverInit;

        // Target_Validate results for the target last looked at (FUN_1000a168).
        float _flat, _dist3, _targetBearing, _targetY;
        AiTarget _sceneTarget;          // GP+0xdc, independent of pilot target +0x17a
        bool _sceneTargetValid;         // GP+dc != -1 and no Target_Validate failure bit 0x1000
        Vector3 _sceneTargetPoint;      // GP+0xc8..0xd0, retained on failed validation
        float _targetPitch;             // GP+0xd8, original positive-up elevation

        int Ticks => FireNow;
        int SkillFlags
        {
            get
            {
                int s = Mathf.Clamp(PilotSkill, 1, 6);
                bool mech = _combat != null && _combat.GamepieceType == 1;
                return (s < 4 ? 1 : 0) | (s < 4 ? 2 : 0) | (s < 3 ? 4 : 0) | (mech && s < 6 ? 8 : 0) | (s < 5 ? 16 : 0) |
                       (mech && s < 2 ? 32 : 0) | (s < 6 ? 64 : 0);
            }
        }

        /// <summary>+0x1fe: the style's attack range, 15000 units when the style gives none (FUN_1006280a).</summary>
        float AttackRangeUnits
        {
            get
            {
                int units = AiTables.Styles[Mathf.Clamp(PilotStyleId, 0, AiTables.Styles.Length - 1)].AttackRange;
                return units == 0 ? 15000f : units;
            }
        }

        float AttackRangeMeters => AttackRangeUnits * PolyModel.UnitsToMeters;

        static (int id, int[] next)[] TableFor(int gpType, out int first)
        {
            if (gpType == 1) { first = AiTables.FirstManeuvers; return AiTables.MechManeuvers; }
            if (gpType == 2 || gpType == 8) { first = AiTables.FirstAircraftManeuvers; return AiTables.AircraftManeuvers; }
            first = 1;
            return AiTables.GroundManeuvers;
        }

        /// <summary>
        /// Target_CheckGp 0x1006730b: pilot style +0x1bc == 1 and target GP type 1 use the target's foot point
        /// (GP origin +0x54 minus MGEO[0] at mech +0xe00); every other style uses its origin.
        /// </summary>
        public Vector3 TargetPoint(Mw2MechCombat target) =>
            target != null && target.GamepieceType == 1 &&
            AiTables.Styles[Mathf.Clamp(PilotStyleId, 0, AiTables.Styles.Length - 1)].AimLow == 1
                ? target.transform.position : target != null ? target.Origin : transform.position;

        /// <summary>
        /// FUN_1000a168 0x1000a168 / Target_Validate for an AI pilot: +0xdc = the target, +0xc0 flat and +0xc4 3D distance
        /// (units), +0xd4 bearing, +0xcc the target height.
        /// </summary>
        bool Look(AiTarget t)
        {
            _sceneTarget = t;
            _sceneTargetValid = false;
            // Target_CheckGp 0x10067128 rejects the observer itself before writing geometry.
            if (t == null || t.Destroyed || (t.Kind == AiTarget.TargetKind.Unit && t.Unit == _combat)) return false;
            Vector3 point = t.Kind == AiTarget.TargetKind.Unit && t.Unit != null ? TargetPoint(t.Unit) : t.Position;
            var from = _combat != null ? _combat.Origin : transform.position;
            var d = (point - from) / PolyModel.UnitsToMeters;
            AiLeadPoint.Spherical(new AiLeadPoint.Vector(d.x, d.y, d.z), out _targetBearing, out _dist3, out _flat, out _targetPitch);
            _targetY = point.y / PolyModel.UnitsToMeters;
            _sceneTargetPoint = point;
            _sceneTargetValid = true;
            return true;
        }

        /// <summary>AI_RelativeBearing 0x1000ac7d: +0xd4 - heading, wrapped to -180..180.</summary>
        float RelativeBearing() => Mathf.Repeat(_targetBearing - transform.eulerAngles.y + 180f, 360f) - 180f;

        float ThrottleControl { get => _controller.Throttle / 64f; set => _controller.Throttle = value * 64f; }

        /// <summary>AI_SteerBearing 0x1000a3b9: +0xc from the relative bearing; returns the bearing.</summary>
        float Steer()
        {
            float b = RelativeBearing();
            _turnControl = BearingTurn(b);
            return b;
        }

        /// <summary>FUN_100643e1 0x100643e1 with the current controls; true while it steers round an obstacle.</summary>
        bool Avoiding()
        {
            float turn = _turnControl, throttle = ThrottleControl;
            if (!_controller.AiAvoid(RelativeBearing(), ref turn, ref throttle)) return false;
            _turnControl = turn;
            ThrottleControl = throttle;
            return true;
        }

        /// <summary>Gamepiece +0x78: Mech_UpdatePhysics 0x1003917e/0x100395aa/0x10039645 writes 1 on ground, 0 in air.</summary>
        bool OnGround => !_controller.Airborne;

        void Jets(bool on) => _controller.Jump = on;           // FUN_100643d0: control +0x1d

        /// <summary>FUN_10064d0f: +0x20 forward = <paramref name="forward"/>, +0x21 backward the opposite.</summary>
        void JetDirection(bool forward)
        {
            _controller.JumpForward = forward;
            _controller.JumpBackward = !forward;
        }

        /// <summary>FUN_10064e99 0x10064e99: fuel above 5, nonzero thrust and the heat's whole part below the limit.</summary>
        bool CanJump(int heatLimit) =>
            _controller.JumpMp > 0 && _controller.JumpFuel > 5 && !_controller.JumpDisabled &&
            (_combat == null || Mathf.FloorToInt(_combat.Heat) < heatLimit);

        /// <summary>FUN_10064d32 0x10064d32: (last flat distance - flat distance) / dt, then remembers the distance.</summary>
        float ClosingRate(int dt)
        {
            if (dt == 0) return 0f;
            float rate = (_lastRange - _flat) / dt;
            _lastRange = _flat;
            return rate;
        }

        /// <summary>FUN_10065453 0x10065453: the mech ran into something, is not reversing, holds no controls, not maneuver 10.</summary>
        bool Bumped() =>
            _controller.WallHit && !_controller.Reverse && !_holdControls && ManeuverId != 10;

        /// <summary>FUN_100635de 0x100635de: outside +-5 degrees, a stopped unit creeps at 0.0015625.</summary>
        bool TurnInPlace(float bearing)
        {
            if (bearing <= 5f && bearing >= -5f) return false;
            if (ThrottleControl == 0f) ThrottleControl = 0.0015625f;
            return true;
        }

        /// <summary>
        /// FUN_10064d7b 0x10064d7b: a pilot below skill 4 that is not steering round something and not in maneuver 10 hops
        /// (jets) when the steering target lies at or beyond the twist limit, on the ground, with jets allowed (heat 20); else a
        /// grounded unit that holds no controls stops its jets.
        /// </summary>
        void HopToTurn()
        {
            if ((SkillFlags & 1) == 0 || _controller.AvoidSide != 0 || ManeuverId == 10) return;
            Look(_steer);
            float b = Mathf.Abs(RelativeBearing());
            float limit = TryGetComponent<Mw2TorsoTwist>(out var twist) ? twist.Limit : 0f;
            if (limit <= b)
            {
                if (CanJump(20) && OnGround && !_controller.Jump) Jets(true);
            }
            else if (!_holdControls && _controller.Jump && OnGround) Jets(false);
        }

        /// <summary>
        /// FUN_100642d5 0x100642d5: with jets, below 20000 units the jets fire while falling faster than 13.730763 units per
        /// tick and stop above -12.115379; returns whether the unit stands (+0x78).
        /// </summary>
        bool DescentJets()
        {
            if (_controller.JumpFuel < 0 || _controller.JumpMp <= 0) return OnGround;
            bool jets = _controller.Jump;
            float y = (_combat != null ? _combat.Origin.y : transform.position.y) / PolyModel.UnitsToMeters;
            if (y < 20000f)
            {
                if (_controller.VerticalSpeedUnits < -13.730763f) jets = true;
                else if (_controller.VerticalSpeedUnits > -12.115379f) jets = false;
            }
            Jets(jets);
            return OnGround;
        }

        /// <summary>Nav_CreatePrivate 0x10066940: a nav point of this pilot, radius 3000 units.</summary>
        void CreatePrivateNav(Vector3 position) =>
            _privateNavs.Add(AiTarget.ForNav(new Mw2MissionDirector.NavInfo { Position = position, Radius = 3000f * PolyModel.UnitsToMeters }));

        /// <summary>FUN_1000b046 0x1000b046: drops the pilot's private nav points.</summary>
        void DropPrivateNavs()
        {
            if (_privateNavs.Contains(_steer)) _steer = _pilotTarget;
            _privateNavs.Clear();
        }

        /// <summary>
        /// FUN_1000b29e(.., 0x100) 0x1000b29e: Target_CycleNav from nav 0 with filter 0x101 takes the next private nav point
        /// of this pilot after the current steering one (Target_Validate keeps no nav target for an AI pilot).
        /// </summary>
        void SteerToNextPrivateNav()
        {
            if (_privateNavs.Count == 0) return;
            int i = _privateNavs.IndexOf(_steer);
            _steer = _privateNavs[(i + 1) % _privateNavs.Count];
        }

        /// <summary>
        /// FUN_10063421 / FUN_10063390 0x10063390: a private nav point at slot <paramref name="slot"/> (0..15, table
        /// 0x1009fe70: x = d / a, z = d / b, 0 for a zero entry) of <paramref name="around"/>'s frame, d model units; the
        /// pilot then steers to the next private nav point.
        /// </summary>
        void NavAtSlot(Transform around, int slot, float d)
        {
            var e = SlotDirections[slot & 15];
            float x = e.x == 0f ? 0f : d / e.x, z = e.y == 0f ? 0f : d / e.y;
            var world = around.TransformPoint(new Vector3(x, 0f, z) * PolyModel.UnitsToMeters);
            float heading = around.eulerAngles.y;       // the original's nav height is the unit's heading (+0x60)
            CreatePrivateNav(new Vector3(world.x, heading * PolyModel.UnitsToMeters, world.z));
            SteerToNextPrivateNav();
        }

        static readonly Vector2[] SlotDirections =
        {
            new Vector2(0, 1), new Vector2(2, 1), new Vector2(1, 1), new Vector2(1, 2), new Vector2(1, 0), new Vector2(1, -2),
            new Vector2(1, -1), new Vector2(2, -1), new Vector2(0, -1), new Vector2(-2, -1), new Vector2(-1, -1),
            new Vector2(-1, -2), new Vector2(-1, 0), new Vector2(-1, 2), new Vector2(-1, 1), new Vector2(-2, 1),
        };

        Mw2MechCombat TargetUnit => _pilotTarget != null && _pilotTarget.Kind == AiTarget.TargetKind.Unit ? _pilotTarget.Unit : null;

        /// <summary>
        /// FUN_100652e6 0x100652e6: the sector of this unit as seen from the target (FUN_10064abf: bearing to it minus the
        /// target's heading, +360 mod 360, / 45 + 0.5 truncated, 8 -> 0). Sector 0 -> slot 2 or 6 at random, the other one when
        /// taken (the original looks at this pilot's own slot table there); 1 -> 3, 7 -> 5; 2..4 -> the first free of the target's
        /// slots 2..4 (else 4), 5..6 -> the first free of 6 down to 4 (else 4). Returns the slot * 2 (a 16-direction index).
        /// </summary>
        int PickAttackSlot()
        {
            var target = _pilotTarget != null && _pilotTarget.Kind == AiTarget.TargetKind.Unit ? _pilotTarget.Unit : null;
            int sector = 0;
            if (target != null)
            {
                var d = transform.position - target.transform.position;
                float b = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg - target.transform.eulerAngles.y;
                b = (b + 360f) % 360f;
                sector = (int)(b / 45f + 0.5f);
                if (sector > 7) sector = 0;
            }
            var slots = SlotsOf(target);
            int s;
            if (sector == 0 || sector == 1 || sector == 7)
            {
                s = sector;
                if (s == 0)
                {
                    s = Random.Range(0, 2) != 0 ? 2 : 6;
                    if (SlotsOf(_combat)[s] != 0) s = s != 2 ? 2 : 6;
                }
                if (s == 1) s = 3;
                if (s == 7) s = 5;
            }
            else if (sector < 5)
            {
                s = 2;
                while (s < 5 && slots[s] != 0) s++;
                if (s == 5) s = 4;
            }
            else
            {
                s = 6;
                while (s > 3 && slots[s] != 0) s--;
                if (s == 3) s = 4;
            }
            return s << 1;
        }

        /// <summary>
        /// FUN_1000b101 0x1000b101: four private nav points W, S, E, N of the steering target at its wanted distance (a nav's
        /// radius, 10000 units for a unit, 25000 for a structure), then steer to the next of them.
        /// </summary>
        void NavsAround() => NavsAround(_steer);

        void NavsAround(AiTarget around)
        {
            if (around == null || around.Destroyed) return;
            float d = WantedDistance(around);
            if (around.Kind == AiTarget.TargetKind.Structure) d = 25000f;
            var p = around.Kind == AiTarget.TargetKind.Unit && around.Unit != null ? around.Unit.Origin : around.Position;
            float m = d * PolyModel.UnitsToMeters;
            CreatePrivateNav(p + new Vector3(-m, 0, 0));
            CreatePrivateNav(p + new Vector3(0, 0, -m));
            CreatePrivateNav(p + new Vector3(m, 0, 0));
            CreatePrivateNav(p + new Vector3(0, 0, m));
            SteerToNextPrivateNav();
        }

        /// <summary>AI_UpdateMechManeuver 0x10062270, once per update in the attack state.</summary>
        void UpdateAttack()
        {
            if (_combat != null)
            {
                _combat.Trigger = false;        // FUN_10009f0e: fire bytes +0x25 / +0x27
            }
            if (!_maneuverInit) { _maneuverInit = true; _steer = _pilotTarget; }
            int now = Ticks, dt = now - _lastManeuverTick;
            _lastManeuverTick = now;
            if (_steer == null) _steer = _pilotTarget;
            if (ManeuverId == -1)
            {
                ManeuverId = SelectManeuver();
                if (ManeuverId != -1) StartManeuver();
                ApplyControls();
                return;
            }
            bool finished = false;
            switch (ManeuverId)
            {
                case 0: DescentJets(); CloseIn(); break;
                case 1:
                    if (_phaseTick <= now)
                    {
                        DropPrivateNavs();
                        var slots = SlotsOf(TargetUnit);
                        if (_phase != 0) slots[_phase / 2 & 7]--;
                        _phaseTick = now + 0x21f;
                        if (_phaseFlag == -1) { _phase = PickAttackSlot(); _phaseFlag = 0; }
                        if (TargetUnit != null) NavAtSlot(TargetUnit.transform, _phase, 15000f);
                        slots[_phase / 2 & 7]++;
                    }
                    DescentJets();
                    OffsetApproach();
                    break;
                case 2: DescentJets(); finished = Pass(dt); break;
                case 3:
                    DescentJets();
                    finished = Pass(dt);
                    if (!Avoiding()) _turnControl += _phase * 450 << 16;
                    if (_phaseTick <= now) { _phase = -_phase; _phaseTick = now + 0x21f; }
                    break;
                case 4: finished = Climb(); break;
                case 5: finished = JetClose(dt) || _controller.JumpFuel < 1; break;
                case 6: finished = Ram(); break;
                case 7: DescentJets(); finished = Flank(); break;
                case 8: DescentJets(); finished = Retreat(); break;
                case 9: finished = HopObstacle(); break;
                case 10: Escape(); break;
                case 11: finished = TurnOnly(); break;
                case 12: DescentJets(); finished = Circle(); break;
                case 13: Strafe(); break;
            }
            // Armed out: flee, ram or jump away.
            if (_controller.MaxSpeedUnits != 0f && Unarmed && !_holdControls &&
                ManeuverId != 6 && PilotState != AiState.Flee)
            {
                if (Random.Range(0, 4) == 0 || _prevManeuver == 5 || !CanJump(40))
                {
                    _forcedManeuver = Random.Range(0, 100) <= FleePercent ? -2 : 6;
                    if ((SkillFlags & 32) == 0) _forcedManeuver = -2;
                }
                else { _forcedManeuver = 4; _phaseFlag = 1; }
            }
            if (_combat != null && _combat.GamepieceType == 1 && Bumped()) finished = true;
            if (_maneuverEnd != 0 && _maneuverEnd <= now) finished = true;
            if (_forcedManeuver != 0) finished = true;
            if (finished) EndManeuver();
            ApplyControls();
        }

        int _lastManeuverTick;

        void ApplyControls() => _controller.Turn = _turnControl / 1024f;

        /// <summary>AI_SelectMechManeuver 0x10062aa8.</summary>
        int SelectManeuver()
        {
            var table = TableFor(_combat != null ? _combat.GamepieceType : 1, out int first);
            for (int guard = 0; guard < 64; guard++)
            {
                Look(_steer);
                if (_forcedManeuver != 0) break;
                int c;
                if (_combat != null && _combat.GamepieceType == 1)
                {
                    if (Bumped()) return 10;
                    if (_pilotTarget == null || _pilotTarget.Kind != AiTarget.TargetKind.Unit) return 0;
                    if (CanJump(20) && (SkillFlags & 2) != 0 && ObstacleJump() && SceneClear(_targetY))
                    {
                        c = _prevManeuver != 0 ? 0 : 9;
                        if (c == 0 && Random.Range(0, 2) != 0) c = 8;
                        return c;
                    }
                }
                if (_prevManeuver == -1)
                    c = table[Random.Range(0, Mathf.Max(1, table.Length - first))].id;
                else
                {
                    int row = System.Array.FindIndex(table, r => r.id == _prevManeuver);
                    if (row < 0) row = 0;
                    var next = table[row].next;
                    c = next[Random.Range(0, next.Length)];
                }
                if (_prevManeuver == 4 && _phaseFlag == 1) return 5;
                if (c == 4 && (!CanJump(20) || (SkillFlags & 2) == 0)) c = -1;
                if (c == 4)
                {
                    Look(_steer);
                    float limit = TryGetComponent<Mw2TorsoTwist>(out var tw) ? tw.Limit : 0f;
                    if (limit < RelativeBearing()) c = 0;
                }
                if (c == 3 && (TryGetComponent<Mw2TorsoTwist>(out var t3) ? t3.Limit : 0f) < 655360f) c = 2;
                if (c == 5 && OnGround) c = 0;
                if (c != -1) return c;
            }
            int forced = _forcedManeuver;
            _forcedManeuver = 0;
            if (forced == -2)
            {
                EnterState(AiState.Flee, _pilotTarget);
                return -1;
            }
            return forced;
        }

        /// <summary>AI_StartMechManeuver 0x10062dd7.</summary>
        void StartManeuver()
        {
            int now = Ticks;
            _pilotTarget = _steer;
            _phaseTick = 0;
            _lastRange = 0f;
            _phase = 0;
            _phaseFlag = 0;
            _holdControls = false;
            _maneuverEnd = now + 0x235a;
            switch (ManeuverId)
            {
                case 0: _maneuverEnd = now + (Random.Range(0, 5) + 8) * 0xb5; break;
                case 1: _maneuverEnd = now + 0xe24; _phaseFlag = -1; break;
                case 3: _phase = Random.Range(0, 2) != 0 ? 1 : -1; break;
                case 4:
                    ThrottleControl = 0f;
                    _turnControl = 0f;
                    _holdControls = true;
                    Jets(true);
                    _maneuverEnd = now + 0x389;
                    break;
                case 5:
                    Look(_steer);
                    ClosingRate(1);
                    _holdControls = true;
                    break;
                case 7:
                    Look(_steer);
                    NavAtSlot(transform, _flat >= 4000f ? (Random.Range(0, 2) != 0 ? 3 : 15) : (Random.Range(0, 2) != 0 ? 4 : 12), 10000f);
                    break;
                case 8:
                    _maneuverEnd = now + (Random.Range(0, 11) + 10) * 0xb5;
                    _controller.Reverse = true;
                    break;
                case 9:
                    _phase = 0;
                    _turnControl = 0f;
                    _holdControls = true;
                    break;
                case 10:
                    if (!_controller.WallHit || _prevManeuver != 10 || _controller.WallHitUnit)
                    {
                        _controller.Reverse = true;
                        _maneuverEnd = now + 0x5a8;
                    }
                    else _maneuverEnd = now + 0x2d4;
                    break;
                case 11:
                    _turnControl = 0f;
                    _holdControls = true;
                    _controller.JumpLeft = true;            // +0x1d4 was just cleared, so always the +0x1e jet
                    Jets(true);
                    _maneuverEnd = now + 0x16a;
                    break;
                case 12:
                    _maneuverEnd = now + 0x46b4;
                    NavsAround();
                    break;
                case 13:
                    _maneuverEnd = now + 0x46b4;
                    _phase = Random.Range(0, 16);
                    break;
            }
        }

        /// <summary>FUN_10063171 0x10063171.</summary>
        void EndManeuver()
        {
            switch (ManeuverId)
            {
                case 1:
                    DropPrivateNavs();
                    if (TargetUnit != null) SlotsOf(TargetUnit)[_phase / 2 & 7]--;
                    break;
                case 5:
                    _controller.JumpForward = _controller.JumpBackward = false;
                    break;
                case 7: case 12: case 13:
                    DropPrivateNavs();
                    break;
                case 8: case 10:
                    _controller.Reverse = false;
                    break;
                case 11:
                    _controller.JumpForward = _controller.JumpBackward = _controller.JumpLeft = _controller.JumpRight = false;
                    break;
            }
            Jets(false);
            _prevManeuver = ManeuverId;
            ManeuverId = -1;
            _phaseTick = 0;
            _maneuverEnd = 0;
            _steer = _pilotTarget;
            _phaseFlag = 0;
            _phase = 0;
            Look(_pilotTarget);
        }

        /// <summary>FUN_1006423a 0x1006423a: a unit target behind something (from this unit's height) and over 800 units higher.</summary>
        bool ObstacleJump()
        {
            if (_pilotTarget == null || _pilotTarget.Kind != AiTarget.TargetKind.Unit) return false;
            Look(_pilotTarget);
            float y = (_combat != null ? _combat.Origin.y : transform.position.y) / PolyModel.UnitsToMeters;
            return !SceneClear(y) && _targetY - 800f > y && _controller.JumpFuel >= 0;
        }

        /// <summary>
        /// FUN_10064b58 0x10064b58: the segment from (x, <paramref name="heightUnits"/>, z) of this unit to the target point
        /// hits nothing, or hits the target itself.
        /// </summary>
        bool SceneClear(float heightUnits)
        {
            if (_combat == null) return true;
            var origin = _combat.Origin;
            var from = new Vector3(origin.x, heightUnits * PolyModel.UnitsToMeters, origin.z);
            // 10064b58 reads cached GP+c8..d0, even when the target has moved since Look.
            var to = _sceneTargetPoint - from;
            float length = to.magnitude;
            if (length < 1e-4f || !_combat.FirstHit(from, to / length, length, out var hit)) return true;
            if (_sceneTarget == null) return false;
            if (_sceneTarget.Kind == AiTarget.TargetKind.Unit) return hit.collider.GetComponentInParent<Mw2MechCombat>() == _sceneTarget.Unit;
            if (_sceneTarget.Kind == AiTarget.TargetKind.Structure) return hit.collider.GetComponentInParent<Mw2Destructible>() == _sceneTarget.Structure;
            return false;
        }

        float FireAt(AiTarget combatTarget)
        {
            Look(combatTarget);
            float b = RelativeBearing();
            FireDecision(b);
            return b;
        }

        /// <summary>FUN_1006363a 0x1006363a (maneuver 0): close in to the attack range; within min(range, 4500) -> maneuver 10.</summary>
        void CloseIn()
        {
            Look(_steer);
            float b;
            if (!Avoiding())
            {
                ThrottleControl = StateThrottle(_flat, AttackRangeUnits);
                b = Steer();
                TurnInPlace(b);
            }
            else b = RelativeBearing();
            if (_flat < Mathf.Min(AttackRangeUnits, 4500f)) _forcedManeuver = 10;
            FireDecision(b);
            HopToTurn();
        }

        /// <summary>FUN_1006371a 0x1006371a (maneuver 1): to the slot nav, then face the target until it is 4500 units off.</summary>
        void OffsetApproach()
        {
            float b = FireAt(_pilotTarget);
            if (_phaseFlag == 0)
            {
                Look(_steer);
                if (!Avoiding())
                {
                    Steer();
                    ThrottleControl = StateThrottle(_flat, 4000f);
                }
                if (Mathf.Abs(ThrottleControl) < 1e-7f && Mathf.Abs(b) > 0f) _phaseFlag = 1;
                HopToTurn();
            }
            if (_phaseFlag == 1)
            {
                // The bearing is of whatever FUN_1000a168 last validated: the target, or the nav when phase 0 ended just now.
                float face = Avoiding() ? RelativeBearing() : Steer();
                if (!TurnInPlace(face)) ThrottleControl = 0f;
                else HopToTurn();
                Look(_steer);
                if (_flat > 4500f) _phaseFlag = 0;
            }
        }

        /// <summary>FUN_100638d4 0x100638d4 (maneuvers 2 and 3): pass the target; done within max(1, closing / 7.7) * 8000 units.</summary>
        bool Pass(int dt)
        {
            Look(_steer);
            float b;
            if (!Avoiding())
            {
                b = Steer();
                ThrottleControl = StateThrottle(_flat, 5500f);
            }
            else b = RelativeBearing();
            FireDecision(b);
            float k = ClosingRate(dt) / 7.7f;
            if (k < 1f) k = 1f;
            bool done = _flat <= k * 8000f;
            HopToTurn();
            return done;
        }

        /// <summary>FUN_10063cd3 0x10063cd3 (maneuver 4): jets until 2000 units above the target.</summary>
        bool Climb()
        {
            float b = FireAt(_steer);
            return _targetY + 2000f <= (_combat != null ? _combat.Origin.y : transform.position.y) / PolyModel.UnitsToMeters;
        }

        /// <summary>
        /// FUN_10063d6a 0x10063d6a (maneuver 5): done once standing or bumping; within 1000 units the phase counts up while
        /// jetting and the jets stop; farther, forward / backward jets by the closing rate (9 within 6000 units, else 36) and
        /// whether the target lies between the twist limit and three times it; stop without jets (heat 40).
        /// </summary>
        bool JetClose(int dt)
        {
            float b = FireAt(_steer);
            bool done = OnGround || _controller.WallHit;
            if (_phase != 2)
            {
                float limit = TryGetComponent<Mw2TorsoTwist>(out var tw) ? tw.Limit : 0f;
                float a = Mathf.Abs(b);
                bool side = !(a <= limit || 3f * limit <= a);
                if (_flat <= 1000f)
                {
                    if (_controller.Jump) _phase++;
                    Jets(false);
                    _controller.JumpForward = _controller.JumpBackward = false;
                }
                else
                {
                    float rate = ClosingRate(dt);
                    float threshold = _flat <= 6000f ? 9f : 36f;
                    JetDirection(rate <= threshold ? !side : side);
                    Jets(true);
                    if (!CanJump(40)) done = true;
                }
            }
            return done;
        }

        /// <summary>FUN_100639a8 0x100639a8 (maneuver 6): drive at the target; within 2000 units (3D) self-destruct.</summary>
        bool Ram()
        {
            Look(_steer);
            if (!Avoiding() || ObstacleIsTarget())
            {
                Steer();
                ThrottleControl = StateThrottle(_flat, 0f);
            }
            bool done = _dist3 <= 2000f;
            if (done && _combat != null) _combat.StartSelfDestruct();
            return done;
        }

        /// <summary>FUN_10064f05 0x10064f05: the obstacle steering is about the target's own model (+0x1dc).</summary>
        bool ObstacleIsTarget()
        {
            var o = _controller.AvoidObstacle;
            if (o == null || _steer == null) return false;
            return _steer.Kind == AiTarget.TargetKind.Unit && _steer.Unit != null ? o.transform.IsChildOf(_steer.Unit.transform)
                 : _steer.Kind == AiTarget.TargetKind.Structure && _steer.Structure != null && o.transform.IsChildOf(_steer.Structure.transform);
        }

        /// <summary>FUN_10063aa4 0x10063aa4 (maneuver 7): fire at the target, drive to the flank nav (3000); done within 3000 units.</summary>
        bool Flank()
        {
            FireAt(_pilotTarget);
            Look(_steer);
            if (!Avoiding())
            {
                ThrottleControl = StateThrottle(_flat, 3000f);
                Steer();
            }
            HopToTurn();
            return _flat <= 3000f;
        }

        /// <summary>FUN_10063a3f 0x10063a3f (maneuver 8): reverse at full throttle facing the target; done on a bump.</summary>
        bool Retreat()
        {
            Look(_steer);
            float b = Steer();
            FireDecision(b);
            bool done = _controller.WallHit;
            ThrottleControl = 0.015625f;
            HopToTurn();
            return done;
        }

        /// <summary>
        /// FUN_10063b70 0x10063b70 (maneuver 9): phase 0 jets while jumping is allowed (heat 40) and the target still needs the
        /// obstacle jump, else jets off, phase 1 for 0xb5 ticks (with 25 % and jets allowed at heat 20 maneuver 5 follows);
        /// phase 1 keeps the jets on and steers; phase 2 ends once standing (FUN_100642d5).
        /// </summary>
        bool HopObstacle()
        {
            FireAt(_steer);
            if (_phase == 0)
            {
                if (!CanJump(40) || !ObstacleJump())
                {
                    Jets(false);
                    _phase = 1;
                    if (CanJump(20) && Random.Range(0, 4) == 0) _forcedManeuver = 5;
                    _phaseTick = Ticks + 0xb5;
                }
                else Jets(true);
            }
            else if (_phase == 1)
            {
                if (_phaseTick < Ticks) { _phase = 2; Jets(false); }
                else Jets(true);
                Look(_steer);
                Steer();
            }
            else if (_phase == 2 && DescentJets()) return true;
            return false;
        }

        /// <summary>FUN_10063f54 0x10063f54 (maneuver 10): full throttle (reversing when the start chose so), firing.</summary>
        void Escape()
        {
            Look(_steer);
            float b = !_controller.Reverse ? (Avoiding() ? RelativeBearing() : Steer()) : Steer();
            FireDecision(b);
            ThrottleControl = 0.015625f;
            HopToTurn();
        }

        /// <summary>FUN_10063d30 0x10063d30 (maneuver 11): fire only; the jets set at the start push it sideways.</summary>
        bool TurnOnly()
        {
            FireAt(_steer);
            return false;
        }

        /// <summary>FUN_10063fe5 0x10063fe5 (maneuver 12): every 0x5a8 ticks new navs round the target; go from nav to nav.</summary>
        bool Circle()
        {
            Look(_pilotTarget);
            if (_phaseTick < Ticks)
            {
                if (_flat > 0f) { var around = _steer; DropPrivateNavs(); NavsAround(around); }
                _phaseTick = Ticks + 0x5a8;
            }
            FireAt(_pilotTarget);
            Look(_steer);
            if (!Avoiding())
            {
                ThrottleControl = StateThrottle(_flat, 3000f);
                Steer();
            }
            HopToTurn();
            if (_flat <= 3000f) SteerToNextPrivateNav();
            return false;
        }

        /// <summary>
        /// FUN_10064111 0x10064111 (maneuver 13, aircraft): fire at the target, fly to the steering point (3000); there, drop the
        /// navs and, while still steering at the target itself, make a nav at slot 1 of this unit 22500 units out.
        /// </summary>
        void Strafe()
        {
            FireAt(_pilotTarget);
            Look(_steer);
            if (!Avoiding())
            {
                ThrottleControl = StateThrottle(_flat, 3000f);
                Steer();
            }
            if (_flat <= 3000f)
            {
                bool atTarget = _steer == _pilotTarget;
                DropPrivateNavs();
                if (atTarget) NavAtSlot(transform, 1, 22500f);
                else { Look(_pilotTarget); _steer = _pilotTarget; }
            }
        }
    }
}
