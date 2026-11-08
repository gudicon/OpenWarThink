using MW2.Formats;
using UnityEngine;

namespace MW2
{
    /// <summary>
    /// The player's outside cameras.
    /// External (mode 1, Camera_External 0x100237c0): entering it (the previous mode was another) resets the smoothing blocks of
    /// FirstEyepoint 0x10022f20 to the camera as it is (x / z offsets 0.5 s, height 0.7 s, yaw and pitch 0.2 s with period 360)
    /// and, the very first time, sets the distance to 5 x the unit radius (+0xe1c), its limits to distance / 1.75 .. distance x 4,
    /// the height to distance / 8 and the height limits to -MGEO[0] .. distance x 4; the zoom becomes 1. Every update the distance
    /// changes by track_distance_delta (+0x64), the height by track_height_delta (+0x68) and the orbit (DAT_100902f4, 180 at
    /// start) by eyepoint_pan_delta (+0x5c), each times dt / 181 (Camera_Setup 0x100230d9); the camera looks at the eye point
    /// (bearing, and pitch = -tilt - eyepoint_tilt (+0x58) / 2) and moves to eye + distance (sin r, cos r) with r = eye yaw -
    /// orbit, its height kept 500 units over the ground under it (200 when that ground is at 0; FUN_10023334).
    /// The eye point and yaw are those of Camera_CockpitView 0x100235e1: the eye node, heading + twist while alive.
    /// Death (Mech_PlayerCommands 0x1003b7dd, state 4): once, DAT_100ad360 = 1 and the external camera, whose orbit then turns
    /// 0.25 degrees a tick by itself (Camera_Setup), the eye angles being the node's own. Ejected (state 5): for 0x389 ticks
    /// camera mode 4 (FUN_10023e24): over the eye point, rising from rest with 0.2368 units per tick squared, pitch 90 (straight
    /// down), yaw growing by 0.007 x ticks since the start every update; afterwards DAT_100ad360 = 1 and mode 4 stays.
    /// Not ported: the palette fade to mode 16 at death (FUN_1003285c) - palettes are baked at import.
    /// </summary>
    public partial class Mw2Cockpit
    {
        const float U = PolyModel.UnitsToMeters;

        readonly CockpitLook _cockpitLook = new CockpitLook();
        bool _cockpitLookActive;

        bool _external;                 // DAT_10090300 == 1 last update
        bool _deathCamera;              // DAT_100ad360
        bool _deathHandled;             // DAT_100ad378 / DAT_100ad37c
        int _ejectUntil;
        bool _ejectCamera;              // mode 4 stays active after the 0x389-tick deadline
        float _extDistance, _extMin, _extMax, _extHeight, _extHeightMin, _extHeightMax;
        float _orbit = 180f;            // DAT_100902f4
        readonly ApproachBlock _bx = new ApproachBlock(), _bz = new ApproachBlock(), _by = new ApproachBlock();
        readonly ApproachBlock _bYaw = new ApproachBlock(), _bPitch = new ApproachBlock();
        Vector3 _camUnits;              // camera position, units
        float _camYaw, _camPitch;
        double _camClock;
        int _camNow, _camDt;
        float _ejectVy;
        int _ejectStart;

        int CameraTicks()
        {
            _camClock += Time.deltaTime * MechMotion.TicksPerSecond;
            int now = (int)_camClock;
            _camDt = now - _camNow;
            _camNow = now;
            return now;
        }

        /// <summary>Camera_CockpitView 0x100235e1: the eye point (units) and yaw; the node's own yaw after DAT_100ad360.</summary>
        void EyePoint(out Vector3 eyeUnits, out float eyeYaw)
        {
            var eye = Eye != null ? Eye.position : transform.position;
            eyeUnits = eye / U;
            eyeYaw = _deathCamera && Eye != null ? Eye.eulerAngles.y
                   : _twist != null ? _twist.TorsoYaw.eulerAngles.y : transform.eulerAngles.y;
        }

        static float Wrap360(float a)
        {
            a %= 360f;
            if (a > 180f) a -= 360f;
            else if (a < -180f) a += 360f;
            return a;
        }

        /// <summary>FUN_1002563a + ApproachAngle_Update 0x10025512: the target wrapped into +-180, the current kept mod 360.</summary>
        static float ApproachAngle(ApproachBlock b, float target, int now)
        {
            b.Target = Wrap360(target);
            float diff = Wrap360(b.Target - b.Current);
            float step = (now - b.LastTime) * diff / b.TimeConstant;
            if ((diff < 0f && step <= diff) || (diff > 1e-7f && diff <= step)) b.Current = b.Target;
            else b.Current += step;
            b.Current %= 360f;
            b.LastTime = now;
            return b.Current;
        }

        /// <summary>Camera_External 0x100237c0 for the player's camera.</summary>
        public bool PlaceExternalCamera(Camera cam)
        {
            if (cam == null) return false;
            cam.orthographic = false;
            int now = CameraTicks();
            UpdateDeathCamera(now);
            if (_ejectCamera) { PlaceEjectCamera(cam, now); return true; }
            EyePoint(out var eye, out float eyeYaw);
            float dt = _camDt;
            float tilt = Mw2Input.Loaded ? Mw2Input.Float(0x58) : 0f;
            float pan = _deathCamera ? dt * 0.25f : (Mw2Input.Loaded ? dt * Mw2Input.Float(0x5c) / 181f : 0f);
            float dDist = Mw2Input.Loaded ? dt * Mw2Input.Float(0x64) / 181f : 0f;
            float dHeight = Mw2Input.Loaded ? dt * Mw2Input.Float(0x68) / 181f : 0f;
            if (!_external)
            {
                if (_extDistance == 0f)
                {
                    float radius = TryGetComponent<Mw2MechController>(out var mc) ? mc.CollisionRadius / U : 500f;
                    _extDistance = radius * 5f;
                    _extMin = _extDistance / 1.75f;
                    _extMax = _extDistance * 4f;
                    _extHeight = _extDistance / 8f;
                    _extHeightMin = -(_combat != null ? _combat.OriginHeight / U : 0f);
                    _extHeightMax = _extMax;
                }
                _camUnits = cam.transform.position / U;
                _camYaw = cam.transform.eulerAngles.y;
                _camPitch = cam.transform.eulerAngles.x;
                _bx.Init(_camUnits.x - eye.x, _camUnits.x - eye.x, 0.5, now);
                _by.Init(_camUnits.y - eye.y, _camUnits.y - eye.y, 0.7, now);
                _bz.Init(_camUnits.z - eye.z, _camUnits.z - eye.z, 0.5, now);
                _bYaw.Init(_camYaw, _camYaw, 0.2, now);
                _bPitch.Init(_camPitch, _camPitch, 0.2, now);
                if (Mathf.Abs(_camUnits.x - eye.x) < 1e-7f) _camUnits.x += 10f;
                _external = true;
            }
            _extDistance = Mathf.Clamp(_extDistance + dDist, _extMin, _extMax);
            float height = _extHeight + dHeight;
            _orbit = (_orbit + pan) % 360f;
            // Vector_ToSpherical 0x10067cff from the camera to the eye: bearing atan2(dx, dz), tilt atan2(dy, flat).
            var d = eye - _camUnits;
            float bearing = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
            float tiltAngle = Mathf.Atan2(d.y, new Vector2(d.x, d.z).magnitude) * Mathf.Rad2Deg;
            _camPitch = ApproachAngle(_bPitch, -tiltAngle - tilt / 2f, now);
            _camYaw = ApproachAngle(_bYaw, bearing, now);
            float r = Wrap360(eyeYaw - _orbit);
            _bx.Target = _extDistance * Mathf.Sin(r * Mathf.Deg2Rad);
            _camUnits.x = _bx.Update(now) + eye.x;
            _bz.Target = _extDistance * Mathf.Cos(r * Mathf.Deg2Rad);
            _camUnits.z = _bz.Update(now) + eye.z;
            // FUN_10023334: the highest walkable ground under the camera plus 500 units (200 over ground 0).
            float ground = Mw2Ground.Height(_camUnits * U) / U;
            float floor = ground + (ground > 1e-7f ? 500f : 200f);
            float y = height;
            if (eye.y + y < floor) y = floor - eye.y;
            _extHeight = Mathf.Clamp(height, _extHeightMin, _extHeightMax);
            _by.Target = y;
            _camUnits.y = _by.Update(now) + eye.y;
            cam.transform.SetPositionAndRotation(_camUnits * U, Quaternion.Euler(_camPitch, _camYaw, 0f));
            cam.fieldOfView = Camera.HorizontalToVerticalFieldOfView(90f, cam.aspect);      // zoom 1
            cam.nearClipPlane = 0.3f;
            return true;
        }

        /// <summary>
        /// Mech_PlayerCommands 0x1003b7dd outside state 2: state 4 once switches to the external camera with its own orbit;
        /// state 5 runs camera mode 4 for 0x389 ticks.
        /// </summary>
        void UpdateDeathCamera(int now)
        {
            if (!IsPlayer || _combat == null || !_combat.Destroyed) return;
            if (_combat.PowerState == 4 && !_deathHandled)
            {
                _deathHandled = true;
                _deathCamera = true;
                CockpitView = false;
                MissileCamera = false;
            }
            else if (_combat.PowerState == 5)
            {
                // Mech_PlayerCommands 0x1003b7dd only schedules the deadline on the first state-5 update.
                // Camera_SetMode(4) starts on the next update and remains selected after the deadline.
                if (!_deathHandled)
                {
                    _deathHandled = true;
                    _ejectUntil = now + 0x389;
                    CockpitView = false;
                    MissileCamera = false;
                    return;
                }
                if (now < _ejectUntil) _ejectCamera = true;
                else _deathCamera = true;
                CockpitView = false;
                MissileCamera = false;
            }
        }

        /// <summary>FUN_10023e24 0x10023e24 (camera mode 4, ejection).</summary>
        void PlaceEjectCamera(Camera cam, int now)
        {
            EyePoint(out var eye, out _);
            if (!_external || _ejectStart == 0)
            {
                _ejectVy = 0f;
                _camPitch = 90f;
                _ejectStart = now;
                _camUnits = cam.transform.position / U;
                _camYaw = cam.transform.eulerAngles.y;
                _external = true;
            }
            float dt = _camDt;
            _camUnits.y += (0.2368f * dt * 0.5f + _ejectVy) * dt;          // Motion_Integrate
            _ejectVy += 0.2368f * dt;
            _camUnits.x = eye.x;
            _camUnits.z = eye.z;
            _camYaw += (now - _ejectStart) * 0.007f;
            cam.transform.SetPositionAndRotation(_camUnits * U, Quaternion.Euler(_camPitch, _camYaw, 0f));
            cam.fieldOfView = Camera.HorizontalToVerticalFieldOfView(90f, cam.aspect);
            cam.nearClipPlane = 0.3f;
        }

        /// <summary>The camera of a destroyed player (the controller no longer runs then).</summary>
        void UpdateDestroyedCamera()
        {
            if (!IsPlayer || _combat == null || !_combat.Destroyed) return;
            var cam = Camera.main;
            if (cam != null) PlaceExternalCamera(cam);
        }

    }
}
