using MW2.Formats;
using UnityEngine;

namespace MW2
{
    /// <summary>
    /// Autopilot (mech [0x37c] = +0xdf0) and the obstacle steering it shares with the AI (FUN_100643e1 0x100643e1).
    ///
    /// Mech_UpdateControl 0x1003a449: TOGGLE_AUTOPILOT (control +0x43) flips [0x37c] with "Autopilot engaged /
    /// disengaged" (Radio_Computer 0x1d, variants 1 / 2); a legs_pan input (+0x10 != 0) or FEET_TO_TORSO turns it off the
    /// same way. While it is on, the turn control +0xc is no longer copied from +0x10, and FUN_100355e0 0x100355e0 flies
    /// the nav route: without a nav target (+0x104) it takes the first nav point that is not visited (Target_CycleNav,
    /// which also makes it the current target); inside the nav radius (flat distance +0xe8) the nav is marked visited
    /// (MECNAVPT) and the next one becomes the target; when that index is lower, the route is over: the autopilot turns
    /// off, the nav target clears and the throttle +8 goes to 0 with throttle_set. Steering: FUN_100643e1 when an
    /// obstacle is ahead, else FUN_1000a3b9 0x1000a3b9: +0xc = 819.2 * bearing / 45, saturated at +-45 degrees, the bearing
    /// being +0xfc (the bearing to the nav target) minus the heading (FUN_1000ac7d 0x1000ac7d). After an avoidance
    /// (+0x1d8) the throttle is restored from +0x1d4, which holds ftol(+8) and so is 0 for every throttle below 1: the
    /// original stops the mech for that update, until the input layer writes its throttle back (Input_Frame runs after
    /// the simulation step). The port reproduces that one-update value.
    ///
    /// FUN_100643e1 (state +0x1dc obstacle, +0x1e0 next test tick, +0x1e4 side, +0x1e6 look-ahead): no test without
    /// speed ([0x36e]), with no side and zero throttle, or in reverse; before +0x1e0 it answers "obstacle" while one is
    /// remembered. Look-ahead = horizontal speed / 7.7 (halved for non-mechs), at least 0.3; up to four segments from the
    /// feet, length look-ahead * 5000 units, along the directions of table 0x1009fe70 (0, 26.6, 45, 63.4 degrees to the
    /// side, mirrored for side -1) (FUN_1006484c 0x1006484c); each segment that hits a non-walkable object adds
    /// side * 204 (ftol(side * 819.2 / 4)); the first hit picks the side (FUN_10064a18 0x10064a18: obstacle to the left
    /// -> +1, right -> -1) and remembers the object; the first clear segment ends the scan, and a clear first segment
    /// with a side set forgets the obstacle when the target bearing is within 1 degree, then tests the far shoulder
    /// (from radius - 1 to the side) on the opposite side. Walkable hits (FUN_10064806 0x10064806: a surface with
    /// normal y >= 0.766) end the scan. A non-zero sum gives +0xc = -clamp(sum, 819.2) and the throttle: first segment
    /// clear -> the player's ftol(saved) / the AI's 1/64, else 1/256 (type 6: no movement). Next test: player +10
    /// ticks, AI +90 with an obstacle, else +181.
    /// Approximation: the segment test is the port's two-way ray against colliders (FirstHit), at the feet height.
    /// </summary>
    public partial class Mw2MechController
    {
        /// <summary>[0x37c]: the autopilot is on.</summary>
        [System.NonSerialized] public bool Autopilot;

        int _avoidSide;             // +0x1e4
        Collider _avoidObstacle;     // +0x1dc

        /// <summary>+0x1e4: the side of the current obstacle steering, 0 for none.</summary>
        public int AvoidSide => _avoidSide;
        /// <summary>+0x1dc: the obstacle being steered round.</summary>
        public Collider AvoidObstacle => _avoidObstacle;
        /// <summary>[0x376] (+0xdd8): this update's movement ran into something; WallHitUnit when it was a unit (+0x7c set).</summary>
        public bool WallHit => _wallHit;
        public bool WallHitUnit => _wallHitUnit;
        /// <summary>[0x36e] (+0xdb8): the top speed, 0 for a unit that cannot move.</summary>
        public float MaxSpeedUnits => _motion != null ? _motion.MaxSpeed : 0f;
        float _lookAhead;           // +0x1e6
        int _avoidNextTest;         // +0x1e0
        int _savedThrottle;         // +0x1d4
        bool _avoided;              // +0x1d8
        static readonly Vector2[] AvoidDirections =
        {
            new Vector2(0, 1), new Vector2(2, 1), new Vector2(1, 1), new Vector2(1, 2), new Vector2(1, 0), new Vector2(1, -2),
            new Vector2(1, -1), new Vector2(2, -1), new Vector2(0, -1), new Vector2(-2, -1), new Vector2(-1, -1),
            new Vector2(-1, -2), new Vector2(-1, 0), new Vector2(-1, 2), new Vector2(-1, 1), new Vector2(-2, 1)
        };

        static float Wrap180(float a)
        {
            if (a > 180f) a -= 360f;
            else if (a < -180f) a += 360f;
            return a;
        }

        // MW2 units -> Unity world (PolyMeshBuilder.ToUnity).
        static Vector3 ToWorld(float x, float y, float z) => new Vector3(x, y, z) * PolyModel.UnitsToMeters;

        /// <summary>FUN_1006484c: segment i on side s; local (x right, z forward) rotated by the heading and moved to the unit.</summary>
        void AvoidSegment(int side, int index, float length, bool shoulder, out Vector3 from, out Vector3 to)
        {
            var dir = AvoidDirections[side < 0 ? (16 - index) & 0xf : index];
            float lx = dir.x != 0 ? length / dir.x : 0f, lz = dir.y != 0 ? length / dir.y : 0f;
            double h = _motion.Heading * System.Math.PI / 180.0;
            float c = (float)System.Math.Cos(h), s = (float)System.Math.Sin(h);
            float ex = _motion.X + lx * c + lz * s, ez = _motion.Z - lx * s + lz * c;
            float sx = _motion.X, sz = _motion.Z;
            if (shoulder)
            {
                float r = CollisionRadius / PolyModel.UnitsToMeters;
                float ox = side < 0 ? -r + 1f : r - 1f;
                sx = _motion.X + ox * c;
                sz = _motion.Z - ox * s;
            }
            from = ToWorld(sx, _motion.Y, sz);
            to = ToWorld(ex, _motion.Y, ez);
        }

        bool AvoidHit(Vector3 from, Vector3 to, out Mw2ShotHit hit)
        {
            hit = default;
            var d = to - from;
            float len = d.magnitude;
            return len > 1e-4f && TryGetComponent<Mw2MechCombat>(out var combat) && combat.FirstHit(from, d / len, len, out hit);
        }

        // FUN_10064806: walkable surface with a shallow normal.
        static bool Ignorable(Mw2ShotHit hit)
        {
            var surface = hit.collider.GetComponentInParent<Mw2Surface>();
            return surface != null && surface.Walkable && hit.normal.y >= 0.766f;
        }

        /// <summary>FUN_100643e1 for an AI pilot (state handlers 0x10009cd7).</summary>
        public bool AiAvoid(float bearing, ref float turnControl, ref float throttleControl) =>
            AvoidObstacles((int)_clock, false, bearing, ref turnControl, ref throttleControl);

        /// <summary>FUN_1000ac7d for the player on autopilot: +0xfc - heading, wrapped.</summary>
        float AutopilotBearing(Vector3 navPosition)
        {
            float dx = navPosition.x / PolyModel.UnitsToMeters - _motion.X, dz = navPosition.z / PolyModel.UnitsToMeters - _motion.Z;
            float bearing = Mathf.Atan2(dx, dz) * Mathf.Rad2Deg;
            return Wrap180(bearing - _motion.Heading);
        }

        /// <summary>FUN_100643e1 0x100643e1: writes turnControl (+0xc) and throttleControl (+8, x1/64 units) when avoiding.</summary>
        bool AvoidObstacles(int now, bool player, float bearing, ref float turnControl, ref float throttleControl)
        {
            bool reverse = player ? Mw2Input.Held(0x30) : Reverse;
            if (_motion.MaxSpeed == 0f || (_avoidSide == 0 && throttleControl == 0f) || reverse) return false;
            if (now < _avoidNextTest) return _avoidObstacle != null;
            if (_avoidSide == 0)
            {
                _lookAhead = Mathf.Sqrt(_motion.VelX * _motion.VelX + _motion.VelZ * _motion.VelZ) / 7.7f;
                if (!TryGetComponent<Mw2MechCombat>(out var c) || c.GamepieceType != 1) _lookAhead /= 2f;
                if (_lookAhead < 0.3f) _lookAhead = 0.3f;
            }
            float sum = 0f;
            int i;
            for (i = 0; i < 4; i++)
            {
                AvoidSegment(_avoidSide, i, _lookAhead * 5000f, false, out var from, out var to);
                if (AvoidHit(from, to, out var hit))
                {
                    if (Ignorable(hit)) break;
                    if (_avoidSide == 0)
                    {
                        var centre = hit.collider.bounds.center;
                        float b = Wrap180(Mathf.Atan2(centre.x / PolyModel.UnitsToMeters - _motion.X,
                                                      centre.z / PolyModel.UnitsToMeters - _motion.Z) * Mathf.Rad2Deg - _motion.Heading);
                        _avoidSide = b < 0f ? 1 : -1;
                        _avoidObstacle = hit.collider;
                    }
                    sum += (int)(_avoidSide * 819.2f / 4f);
                    continue;
                }
                if (i == 0 && Mathf.Abs(bearing) <= 1f) { _avoidObstacle = null; _avoidSide = 0; }
                if (i == 0 && _avoidSide != 0)
                {
                    AvoidSegment(-_avoidSide, 1, _lookAhead * 5000f, true, out var from2, out var to2);
                    if (AvoidHit(from2, to2, out var hit2))
                    {
                        if (Ignorable(hit2)) break;
                        sum += _avoidSide;
                    }
                }
                break;
            }
            if (sum != 0f)
            {
                float v = Mathf.Clamp(sum, -819.2f, 819.2f);
                turnControl = -v;
                float first = player ? _savedThrottle : 0.015625f;
                throttleControl = i == 0 ? first : 0.00390625f;
                if (TryGetComponent<Mw2MechCombat>(out var c6) && c6.GamepieceType == 6) { throttleControl = 0f; turnControl = 0f; }
            }
            _avoidNextTest = now + (player ? 10 : _avoidObstacle != null ? 0x5a : 0xb5);
            return sum != 0f;
        }

        /// <summary>
        /// The autopilot part of Mech_UpdateControl for the player, once per update in state 2. Returns true when it
        /// drives the turn; turnControl is +0xc (+-1024), throttleControl the throttle control +8.
        /// </summary>
        bool UpdateAutopilot(int now, ref float turnControl, ref float throttleControl)
        {
            if (!TryGetComponent<Mw2MechCombat>(out var combat)) return false;
            if ((Mw2Input.Float(0x10) != 0f || _feetToTorso) && Autopilot) Mw2Input.Set(0x43);
            if (Mw2Input.Held(0x43))
            {
                Autopilot = !Autopilot;
                if (Mw2Audio.Instance != null) Mw2Audio.Instance.Computer(0x1d, Autopilot ? 1 : 2);
                Mw2Input.Clear(0x43);
            }
            if (!Autopilot) { _savedThrottle = 0; return false; }
            var director = combat.NavDirector;
            if (director == null) return false;
            if (combat.NavTarget < 0)
            {
                combat.CycleTarget(0, 1);
                int start = combat.NavTarget;
                while (start >= 0 && director.NavVisited(combat.NavTarget))
                {
                    combat.CycleTarget(1, 1);
                    if (combat.NavTarget == start) { combat.NavTarget = -1; return true; }
                }
                if (combat.NavTarget < 0) return true;
            }
            var nav = director.Navs[combat.NavTarget];
            var p = transform.position;
            if (new Vector2(p.x - nav.Position.x, p.z - nav.Position.z).magnitude < nav.Radius)
            {
                int index = combat.NavTarget;
                if (director.MarkNavVisited(index, combat.Team) && Mw2Audio.Instance != null)
                    Mw2Audio.Instance.Play(0xfc, transform.position, true);
                combat.CycleTarget(1, 1);
                if (combat.NavTarget < index)
                {
                    Autopilot = false;
                    combat.NavTarget = -1;
                    Mw2Input.SetFloat(0x08, 0f);
                    Mw2Input.Set(0x24);
                    throttleControl = 0f;
                    return true;
                }
                nav = director.Navs[combat.NavTarget];
            }
            float bearing = AutopilotBearing(nav.Position);
            if (!AvoidObstacles(now, true, bearing, ref turnControl, ref throttleControl))
            {
                // FUN_1000a3b9: +0xc from the bearing, +-819.2 beyond +-45 degrees.
                turnControl = bearing > 45f ? 819.2f : bearing < -45f ? -819.2f : 819.2f * (bearing / 45f);
                if (_avoided) { throttleControl = _savedThrottle; _avoided = false; }
                _savedThrottle = (int)throttleControl;
            }
            else _avoided = true;
            return true;
        }
    }
}
