using MW2.Formats;
using UnityEngine;

namespace MW2
{
    /// <summary>
    /// The AI fire decision and torso aim, FUN_10005370 0x10005370, called by state 2 (AI_StateMove 0x10009cd7, after
    /// it clears the fire bytes +0x25 / +0x27) and by every attack maneuver (0x1006363a, 0x1006371a, ...) with the
    /// bearing of the pilot target relative to the heading (AI_SteerBearing 0x1000a3b9 / FUN_1000ac7d 0x1000ac7d).
    /// Style +0x1c0 = 0: FUN_10005633 0x10005633, else FUN_100053af 0x100053af with the bearing of the lead point
    /// (FUN_10005caa 0x10005caa). Every call:
    /// - when the clock passes +0x18e: r = RandomInt(+0x186) (GPS AI parameter 0, at least 1, plus 1), next check in
    ///   r * 0x16 ticks; it fires only with r = 0, |bearing - twist| below 10 (lead: 1) degrees, |body pitch + weapon pitch
    ///   + target elevation +0xd8| below 10, the target the player or RandomInt(3) = 0, a weapon choice (FUN_10005864
    ///   0x10005864 one weapon, or with +0x192 FUN_10005a23 0x10005a23 a group) and a clear aim ray (FUN_10005050
    ///   0x10005050: the target within 90 degrees of the torso; the ray along the torso for the flat distance hits
    ///   nothing or the target). Then the fire byte +0x25 (selected weapon) or +0x27 (group) is set, and with a lock-on
    ///   weapon Ai_LockOnPlayer 0x10005c42.
    /// - torso pan control +4 = trunc(4096 * clamp(x / 11520, +-1/64)) with x = bearing - twist (lead: the lead bearing),
    ///   tilt control +0 = -trunc(4096 * clamp(+0xd8 / 3840, +-1/64)) (FUN_1000a35d / FUN_1000a38b -> FUN_1000b97f; the
    ///   added term is the RandomInt result, 0 whenever it fires).
    /// FUN_10005864: from the selected weapon, up to the weapon count: in range (FUN_10005309: minimum <= flat distance
    /// <= maximum), heat + weapon heat below +0x1b0, slot ready (FUN_1006d11b), ammunition or an energy weapon
    /// (FUN_10006032) and RandomInt(recycle / 90 + 1) = 0, else Weapon_SelectNextInGroup; its pitch offsets for weapon
    /// types 0 / 4 / 0x15 are overwritten by the tilt above. FUN_10005a23: every weapon in range (minimum <= distance <
    /// maximum) with ammunition and not destroyed goes into group 1; a non-player stops (fires nothing) once heat + their
    /// heat reaches +0x1b0; the first ready lock-on weapon becomes the selection.
    /// AI_TorsoSweep 0x1000caca (states 5..8, game piece type 1): every 32 ticks while the throttle control is not 0,
    /// else 64, the pan control moves 5 degrees and turns back beyond 45.
    /// Lead point uses GP origins, current physics velocity and the original angle tables (AiLeadPoint).
    /// Approximation: the body pitch (+0x5c) of a walking mech is taken as 0.
    /// </summary>
    public partial class Mw2MechAI
    {
        [Tooltip("Fire check divisor +0x186: GPS AI parameter 0 (at least 1) + 1 (0x10007f35).")]
        public int FireChance = 2;

        double _fireClock;
        int _nextFireCheck;             // +0x18e
        int _nextSweep;                 // +0x1cc
        bool _sweepRight;               // +0x1d4
        int _fireFrameTicks;            // DAT_100a6644: integer timer delta, including zero

        int FireNow => (int)_fireClock;

        void TickFireClock()
        {
            int previous = FireNow;
            _fireClock += Time.deltaTime * MechMotion.TicksPerSecond;
            _fireFrameTicks = FireNow - previous;
        }

        /// <summary>FUN_10005309: 3 below the minimum, 2 in range, 1 exactly at the maximum, 0 beyond.</summary>
        static int RangeCheck(WeaponDef def, float flatUnits)
        {
            float min = def.MinRangeMeters / PolyModel.UnitsToMeters, max = def.RangeMeters / PolyModel.UnitsToMeters;
            if (flatUnits < min) return 3;
            if (flatUnits < max) return 2;
            return flatUnits <= max ? 1 : 0;
        }

        /// <summary>FUN_10005370 0x10005370: returns whether a fire byte was set.</summary>
        bool FireDecision(float bearing)
        {
            if (_combat == null || _combat.Destroyed) return false;
            TryGetComponent<Mw2TorsoTwist>(out var twist);
            float twistNow = twist != null ? twist.Current : 0f;
            float weaponPitch = twist != null ? twist.Pitch : 0f;
            bool hasTarget = _pilotTarget != null && !_pilotTarget.Destroyed && _pilotTarget.Kind != AiTarget.TargetKind.Nav;
            float flatUnits = _flat, elevation = -_targetPitch;
            var style = AiTables.Styles[Mathf.Clamp(PilotStyleId, 0, AiTables.Styles.Length - 1)];
            bool lead = style.LeadTarget != 0;
            float panInput = bearing - twistNow;
            float window = 10f;
            float fireElevation = elevation;
            if (lead && hasTarget)
            {
                bearing = LeadBearing(bearing, ref fireElevation);
                panInput = bearing - twistNow;
                window = 1f;
            }
            float relative = bearing - twistNow;

            bool fired = false, lockShot = false;
            if (FireNow >= _nextFireCheck)
            {
                int r = Random.Range(0, Mathf.Max(1, FireChance));
                _nextFireCheck = FireNow + r * 0x16;
                bool pitchAllowed = AiFireGeometry.FirePitchAllowed(_controller != null ? _controller.BodyPitch : 0f,
                    -weaponPitch, -fireElevation);
                var targetUnit = hasTarget && _pilotTarget.Kind == AiTarget.TargetKind.Unit ? _pilotTarget.Unit : null;
                if (hasTarget && relative < window && relative > -window && pitchAllowed &&
                    (targetUnit != null && targetUnit.IsPlayer || Random.Range(0, 3) == 0))
                {
                    // 10005752: no-lead refresh occurs after angle/player gates, even when r != 0.
                    // 100053af has no corresponding refresh; its caller's cache remains authoritative.
                    if (!lead) Look(_pilotTarget);
                    flatUnits = _flat;
                    if (r == 0)
                    {
                        var choice = style.GroupFire == 0 ? SingleFire(flatUnits, style, out lockShot) : GroupFire(flatUnits, style, out lockShot);
                        if (choice != null && AimRayClear(twist, flatUnits))
                        {
                            foreach (int i in choice) _combat.FireWeapon(i);
                            fired = true;
                            if (targetUnit != null) _combat.Attacking = targetUnit;
                        }
                    }
                }
            }
            // Ai_LockOnPlayer 0x10005c42 grants the lock bit for every target;
            // only ENEMYFIR 0x5c depends on the target being the player.
            if (lockShot && fired && _combat.TryAiLock(PilotStyleId, Random.Range(0, 100)) &&
                _pilotTarget.Kind == AiTarget.TargetKind.Unit && _pilotTarget.Unit != null && _pilotTarget.Unit.IsPlayer && Mw2Audio.Instance != null)
                Mw2Audio.Instance.Play(0x5c, _pilotTarget.Unit.transform.position, true);
            if (twist != null)
            {
                twist.Target = (int)(4096f * Mathf.Clamp(panInput * (1f / 11520f), -0.015625f, 0.015625f));
                // AI_FireLead 100055fe still passes unmodified GP +0xd8 to AI_TiltControl.
                twist.PitchTarget = -(int)(4096f * Mathf.Clamp(-_targetPitch * (1f / 3840f), -0.015625f, 0.015625f));
            }
            return fired;
        }

        readonly System.Collections.Generic.List<int> _fireChoice = new System.Collections.Generic.List<int>();

        /// <summary>FUN_10005864 0x10005864: the weapon to fire, or null.</summary>
        System.Collections.Generic.List<int> SingleFire(float flatUnits, PilotStyle style, out bool lockOn)
        {
            lockOn = false;
            _fireChoice.Clear();
            bool ok = false;
            int count = _combat.Weapons.Count;
            for (int k = 0; k < count && !ok; k++)
            {
                int sel = _combat.SelectedWeapon;
                if (sel >= 0 && sel < count)
                {
                    var w = _combat.Weapons[sel];
                    int range = RangeCheck(w.Def, flatUnits);
                    ok = range > 0 && range < 3 && _combat.Heat + w.Def.Heat < style.HeatLimit && _combat.IsReady(sel) &&
                         Random.Range(0, w.Def.RecycleTicks / 0x5a + 1) == 0;
                }
                // 1000599f -> Weapon_SelectNextInGroup(mech,1) also crosses fire groups.
                if (!ok) _combat.SelectNextWeapon(true);
            }
            if (!ok) return null;
            int s = _combat.SelectedWeapon;
            lockOn = _combat.Weapons[s].Def.LockOn;
            _fireChoice.Add(s);
            return _fireChoice;
        }

        /// <summary>FUN_10005a23 0x10005a23: the weapons of group 1, or null.</summary>
        System.Collections.Generic.List<int> GroupFire(float flatUnits, PilotStyle style, out bool lockOn)
        {
            lockOn = false;
            _fireChoice.Clear();
            float heat = 0f;
            int lockSlot = -1;
            for (int i = 0; i < _combat.Weapons.Count; i++)
            {
                var w = _combat.Weapons[i];
                // 10005aa4 resets each visited slot, then marks eligible slots as group 1.
                w.Group = 0;
                if (!_sceneTargetValid || RangeCheck(w.Def, flatUnits) != 2 || w.Ammo == 0 || !_combat.Works(i)) continue;
                if (!_combat.IsPlayer && style.HeatLimit <= _combat.Heat + heat + w.Def.Heat)
                {
                    _fireChoice.Clear(); _combat.SelectNextWeapon(true); return null;
                }
                w.Group = 1;
                _fireChoice.Add(i);
                heat += w.Def.Heat;
                if (lockSlot < 0 && w.Def.LockOn && w.State == 1) lockSlot = i;
            }
            if (_fireChoice.Count == 0) { _combat.SelectNextWeapon(true); return null; }
            if (lockSlot >= 0) { _combat.SelectedWeapon = lockSlot; lockOn = true; }
            else _combat.SelectGroup(1);
            // Group fire (+0x27) starts the ready weapons of the group.
            _fireChoice.RemoveAll(i => _combat.Weapons[i].State != 1);
            return _fireChoice;
        }

        /// <summary>
        /// FUN_10005050 0x10005050 (with the angle test): the target must lie within 90 degrees of the torso; the aim ray
        /// along the torso, as long as the cached flat distance, hits nothing or the scene target GP+dc.
        /// </summary>
        bool AimRayClear(Mw2TorsoTwist twist, float flatUnits)
        {
            if (!AiFireGeometry.AimInFront(transform.eulerAngles.y, twist != null ? twist.Current : 0f, _targetBearing)) return false;
            AimRay(twist, out var origin, out var aim);
            float length = flatUnits * PolyModel.UnitsToMeters;
            if (!_combat.FirstHit(origin, aim, length, out var hit)) return true;
            if (_sceneTarget == null) return false;
            if (_sceneTarget.Kind == AiTarget.TargetKind.Unit) return hit.collider.GetComponentInParent<Mw2MechCombat>() == _sceneTarget.Unit;
            return _sceneTarget.Kind == AiTarget.TargetKind.Structure && hit.collider.GetComponentInParent<Mw2Destructible>() == _sceneTarget.Structure;
        }

        /// <summary>Weapon_AimRay 0x1006ddd8 / Weapon_AimNodeDirection 0x1006de54:
        /// world EYEO (GP root when absent), global player's MGEO[1] and native matrix-angle extraction.</summary>
        void AimRay(Mw2TorsoTwist twist, out Vector3 origin, out Vector3 direction)
        {
            _combat.WeaponAimRay(twist, out origin, out direction);
        }

        /// <summary>
        /// AI_LeadPoint 0x10005caa: current target velocity, GP origins, cached target distance +0xc4,
        /// integer frame ticks, and table yaw/pitch. Both angles change only when the native gates pass.
        /// </summary>
        float LeadBearing(float bearing, ref float elevation)
        {
            if (_pilotTarget.Kind != AiTarget.TargetKind.Unit || _pilotTarget.Unit == null) return bearing;
            var unit = _pilotTarget.Unit;
            Vector3 velocity = unit.TryGetComponent<Mw2MechController>(out var controller) ? controller.PhysicsVelocityUnits : Vector3.zero;
            int sel = _combat.SelectedWeapon;
            if (sel < 0 || sel >= _combat.Weapons.Count) return bearing;
            var def = _combat.Weapons[sel].Def;
            var from = _combat.Origin / PolyModel.UnitsToMeters;
            var target = unit.Origin / PolyModel.UnitsToMeters;
            var predicted = new AiLeadPoint.Vector();
            float pitch = -elevation;
            if (AiLeadPoint.Predict(new AiLeadPoint.Vector(from.x, from.y, from.z),
                new AiLeadPoint.Vector(target.x, target.y, target.z), new AiLeadPoint.Vector(velocity.x, velocity.y, velocity.z),
                _dist3, def.Speed, def.LifetimeTicks, _fireFrameTicks, transform.eulerAngles.y, ref predicted, ref bearing, ref pitch))
                elevation = -pitch;
            return bearing;
        }

        /// <summary>AI_TorsoSweep 0x1000caca for states 5..8.</summary>
        void TorsoSweep()
        {
            if (_combat == null || _combat.GamepieceType != 1 || !TryGetComponent<Mw2TorsoTwist>(out var twist)) return;
            int period = Mathf.Abs(_controller.Throttle) >= 1e-7f ? 0x20 : 0x40;
            int now = FireNow;
            if (now + period < _nextSweep) _nextSweep = now + period;
            if (_nextSweep > now) return;
            twist.Target += _sweepRight ? 5f : -5f;
            if (Mathf.Abs(twist.Target) > 45f) _sweepRight = !_sweepRight;
            _nextSweep = now + period;
        }
    }
}
