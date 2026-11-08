using MW2.Formats;
using UnityEngine;

namespace MW2.Autopilot
{
    /// <summary>Player-side weapon choice and observed-motion lead. Calls no damage/fire shortcuts.</summary>
    public sealed class AutopilotCombat
    {
        int _lastFireFrame = -100;
        float _lastHeading, _lastBearing, _aimTime = -1, _panRate;
        public float PreferredRange = 180f;
        public float LastAimError = float.NaN; // Read-only diagnostic of the last actual reticle/desired ray comparison.
        public float AimTolerance = 3f; // Tester waits for a steadier reticle; never changes weapon dispersion or shot rays.
        internal static bool SteadyAim(float error, float tolerance) => Mathf.Abs(error) < Mathf.Clamp(tolerance, 0.05f, 3f);
        public string WeaponName = "none";
        public bool AvoidAutocannons; // Explicit tester preference; never edits mounted weapons or ammunition.

        internal static bool IsAutocannon(WeaponDef def) => def != null &&
            ((def.Name ?? "").IndexOf("AC", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
             (def.Name ?? "").IndexOf("LBX", System.StringComparison.OrdinalIgnoreCase) >= 0);

        public void Aim(Mw2MechCombat self, Mw2TorsoTwist twist, AutopilotSensors sensor, PilotTactic tactic, ref PilotControls c, bool groupFire = false)
        {
            c.weapon = -1;
            float best = float.NegativeInfinity;
            float bestRange = 0;
            for (int i = 0; i < self.Weapons.Count; i++)
            {
                var w = self.Weapons[i];
                if (w.Def == null || w.Def.IsAms || !self.PilotWeaponWorks(i) || w.Ammo == 0) continue;
                if (AvoidAutocannons && (IsAutocannon(w.Def) || groupFire &&
                    self.Weapons.Exists(member => member.Group == w.Group && IsAutocannon(member.Def)))) continue;
                if (sensor.ManualVisual && w.Def.LockOn) continue;
                bestRange = Mathf.Max(bestRange, w.Def.RangeMeters);
                bool inRange = sensor.Distance >= w.Def.MinRangeMeters && sensor.Distance <= ShotRange(w.Def, tactic == PilotTactic.Guard && sensor.Structure != null);
                float heat = w.Def.Heat * Mathf.Max(1, w.Def.Burst);
                float dps = w.Def.Damage * Mathf.Max(1, w.Def.Burst) * 181f / Mathf.Max(1, w.Def.RecycleTicks);
                float score = (inRange ? 1000 : 0) + (w.State == 1 ? 80 : 0) + dps - heat * 2;
                if (i == self.SelectedWeapon && w.Def.LockOn && inRange &&
                    (tactic != PilotTactic.Guard || w.State != 3)) score += 150; // Keep acquisition alive, not a reloading Guard slot.
                if (self.Heat + heat >= 65f) score -= 2000;
                if (score > best) { best = score; c.weapon = i; }
            }
            PreferredRange = Mathf.Clamp(bestRange * (tactic == PilotTactic.KeepRange ? 0.8f : 0.6f), 60, 650);
            if (tactic == PilotTactic.Guard) PreferredRange = Mathf.Min(PreferredRange, 200f); // Close defense uses the short-range groups too.
            if (c.weapon < 0 || twist == null || !sensor.HasTarget) return;
            var weapon = self.Weapons[c.weapon]; var def = weapon.Def;
            WeaponName = def.Name;
            // Tester feedback follows the ordinary Weapon_AimRay1006ddd8:
            // the eye can be above the GP origin and its animated yaw can differ
            // from heading+twist. Only request corrected player pan/pitch inputs.
            self.WeaponAimRay(twist, out var origin, out var actualAim);
            var point = sensor.Point;
            if (sensor.Clear && !def.LockOn)
            {
                var r = point - origin; var v = sensor.ObservedVelocity;
                // Mobile tester uses the actual projectile lifetime, rather than
                // the conservative1.5s default (PPC needs over3s at500m).
                float leadLimit = AutopilotPolicy.MobileTactic(tactic) ? def.LifetimeTicks / 181f : 1.5f;
                float t = AutopilotPolicy.InterceptSeconds(r.x, r.y, r.z, v.x, v.y, v.z, def.Speed * 181f * PolyModel.UnitsToMeters, leadLimit);
                point += v * t;
            }
            var d = point - origin;
            float heading = self.transform.eulerAngles.y, bearing = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
            float dt = Time.time - _aimTime;
            if (_aimTime >= 0 && dt > 0 && dt < 0.2f)
            {
                float rate = (Mathf.DeltaAngle(_lastBearing, bearing) - Mathf.DeltaAngle(_lastHeading, heading)) / dt;
                _panRate = Mathf.Lerp(_panRate, Mathf.Clamp(rate, -180, 180), 0.15f);
            }
            else _panRate = 0;
            _lastHeading = heading; _lastBearing = bearing; _aimTime = Time.time;
            float rayYaw = Mathf.Atan2(actualAim.x, actualAim.z) * Mathf.Rad2Deg;
            float yawOffset = Mathf.DeltaAngle(heading + twist.Current, rayYaw);
            c.pan = Mathf.Clamp(Mathf.DeltaAngle(heading, bearing) - yawOffset + _panRate * 0.2f, -twist.Limit, twist.Limit);
            float rayPitch = Mathf.Atan2(actualAim.y, new Vector2(actualAim.x, actualAim.z).magnitude) * Mathf.Rad2Deg;
            c.pitch = Mathf.Clamp(Mathf.Atan2(d.y, new Vector2(d.x, d.z).magnitude) * Mathf.Rad2Deg - (rayPitch - twist.Pitch), -60f, 60f);
            // Fire against the actual current reticle, not the requested angle.
            float error = Vector3.Angle(actualAim, d); LastAimError = error;
            c.fire = sensor.Attackable && SteadyAim(error, AimTolerance) && Time.frameCount - _lastFireFrame >= 3 &&
                AutopilotPolicy.FireAllowed(self.Heat, def.Heat, def.Burst, weapon.State == 1,
                    sensor.Distance >= def.MinRangeMeters && sensor.Distance <= ShotRange(def, tactic == PilotTactic.Guard && sensor.Structure != null),
                    sensor.Clear, error, def.LockOn, self.Locked);
            if (c.fire && groupFire) c.fire = GroupReady(self, c.weapon, sensor.Distance, tactic == PilotTactic.Guard && sensor.Structure != null);
            if (c.fire) _lastFireFrame = Time.frameCount;
        }
        // Manual PPC fire has no nominal-range veto in FireGroup1006d4f2 /
        // FireProjectile1006c554. Shot_Move10010f40 retires it by lifetime.
        // Guard uses that physical reach only for a visible static GT, with an
        // eight-tick margin. No weapon table or projectile lifetime is changed.
        internal static float ShotRange(WeaponDef def, bool structure)
        {
            if (structure && def.ProjectileType == 6 && Mathf.Abs(def.Gravity) < 1e-7f && def.Speed > 0f)
                return Mathf.Max(def.RangeMeters, def.Speed * PolyModel.UnitsToMeters * Mathf.Max(0, def.LifetimeTicks - 8));
            return def.RangeMeters;
        }

        // Test-only salvo policy. FireGroup1006d4f2 fires every ready selectable
        // weapon in a group, so budget all working members before pressing fire.
        internal static bool GroupReady(Mw2MechCombat self, int selected, float distance, bool structure = false)
        {
            if (selected < 0 || selected >= self.Weapons.Count) return false;
            int group = self.Weapons[selected].Group;
            float heat = self.Heat;
            bool any = false;
            for (int i = 0; i < self.Weapons.Count; i++)
            {
                var w = self.Weapons[i]; var d = w.Def;
                if (w.Group != group || d == null || d.IsAms || !self.PilotWeaponWorks(i) || w.Ammo == 0) continue;
                any = true;
                if (w.State != 1 || distance < d.MinRangeMeters || distance > ShotRange(d, structure) || d.LockOn && !self.Locked) return false;
                heat += Mathf.Max(0, d.Heat) * Mathf.Max(1, d.Burst);
            }
            return any && heat < 65;
        }
    }
}
