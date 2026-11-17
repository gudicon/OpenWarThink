using MW2.Formats;
using UnityEngine;

namespace MW2
{
    /// <summary>
    /// The mods' weapon pictures (Mw2WeaponView.Current while the game runs): for a weapon with a look in the active mods
    /// (Mw2WeaponLooks.Get) it draws the muzzle, shot and impact parts of that look — glowing beam (Mw2ModShot), laser
    /// (Mw2ModLaser), soft lights, flash sprites, sparks and prefabs from the mod's asset bundles (Mw2ModPrefabs) — and
    /// tells the weapon code which original pictures to leave out. Weapons without a look are drawn as in the original.
    /// Port addition with no counterpart in the original program.
    /// </summary>
    public sealed class Mw2ModWeaponView : IWeaponView
    {
        /// <summary>
        /// Where a shot left, kept on the part that fired it: the weapon mount whose node is at the muzzle point (else the
        /// unit itself), so a beam drawn from the muzzle stays on the arm as it turns.
        /// </summary>
        public sealed class Muzzle
        {
            readonly Transform _node;
            readonly Vector3 _local, _world;

            public Muzzle(Mw2MechCombat owner, Vector3 point)
            {
                _world = point;
                if (owner == null) return;
                Transform best = null;
                float nearest = 0.05f * 0.05f;
                if (owner.TryGetComponent<Mw2WeaponMounts>(out var mounts))
                    foreach (var s in mounts.Sections)
                    {
                        if (s == null) continue;
                        float d = (s.position - point).sqrMagnitude;
                        if (d <= nearest) { nearest = d; best = s; }
                    }
                _node = best != null ? best : owner.transform;
                _local = _node.InverseTransformPoint(point);
            }

            public Vector3 Position => _node != null ? _node.TransformPoint(_local) : _world;
        }

        public bool Fired(WeaponDef def, Mw2MechCombat owner, Vector3 muzzle, Vector3 direction)
        {
            var look = Mw2WeaponLooks.Get(def);
            if (look == null) return true;
            Mw2WeaponLooks.Muzzle(look, muzzle, direction);
            return look.Muzzle?.HideOriginal != true;
        }

        public IShotView Shot(WeaponDef def, Mw2MechCombat owner, Vector3 muzzle, Vector3 direction, float reach)
        {
            var look = Mw2WeaponLooks.Get(def);
            if (look == null || look.Shot == null && look.Laser == null && look.Impact == null) return null;
            return new ShotView(look, owner, muzzle, direction, reach);
        }

        sealed class ShotView : IShotView
        {
            readonly WeaponLook _look;
            readonly Mw2MechCombat _owner;
            readonly Vector3 _muzzle, _direction;
            readonly float _reach;
            Mw2ModLaser _laser;
            Mw2ModPrefab _prefab;
            bool _gone;

            public ShotView(WeaponLook look, Mw2MechCombat owner, Vector3 muzzle, Vector3 direction, float reach)
            {
                _look = look; _owner = owner; _muzzle = muzzle; _direction = direction; _reach = reach;
            }

            public bool DrawsOriginal => _look.Shot?.HideOriginal != true && _look.Laser?.HideShot != true;

            public void Started(Transform shot)
            {
                var s = _look.Shot;
                // A glowing beam from the muzzle; a laser from the muzzle along the shot, whose far end follows the shot
                // (or with reach "instant" a ray of its own up to the shot's full reach); a soft light flying with it.
                if (s?.Beam != null) Mw2ModShot.Attach(shot.gameObject, s.Beam, _muzzle);
                var muzzle = _look.Laser != null || s?.Prefab != null ? new Muzzle(_owner, _muzzle) : null;
                if (_look.Laser != null) _laser = Mw2ModLaser.Fire(_look.Laser, muzzle, _direction, _reach, shot, _owner);
                if (s?.Light != null) Mw2WeaponLooks.Light(s.Light, shot.position, shot);
                if (s?.Prefab != null)
                {
                    // Reach "instant": at once at the far end of the shot's line (the laser's ray), not on the shot.
                    bool instant = s.Prefab.Instant;
                    var end = instant ? Mw2ModLaser.Aim(_owner, _muzzle, _direction.normalized, _reach) : shot.position;
                    _prefab = Mw2ModPrefabs.Place(s.Prefab, end, shot.rotation, instant ? null : shot);
                    if (_prefab != null) _prefab.Ride(muzzle, s.Prefab.Stretch, !instant);
                }
            }

            public bool Hit(Vector3 point, Vector3 back)
            {
                // A shot prefab with "hold" may still be burning: the impact pictures come when it lets go.
                End(point, () => Mw2WeaponLooks.Impact(_look, point, back));
                return _look.Impact?.HideOriginal != true;
            }

            public void Gone(Vector3 point) => End(point, null);

            void End(Vector3 point, System.Action then)
            {
                if (_gone) return;
                _gone = true;
                if (_laser != null) _laser.End(point);
                if (_prefab != null) _prefab.Release(point, then);
                else then?.Invoke();
            }
        }
    }
}
