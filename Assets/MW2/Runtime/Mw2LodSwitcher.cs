using System.Collections.Generic;
using MW2.Formats;
using UnityEngine;

namespace MW2
{
    /// <summary>
    /// Per-frame LOD choice, ported from mercsw.dll 0x1001f099. Distances are compared in model units with thresholds
    /// camera[+0xb8] * 1500 / 13000 / 21200 (camera[+0xb8] = zoom, 1 at normal view):
    /// the player in cockpit view (camera mode 0) uses representation index 4 (LOD5); otherwise the nearest unit closer
    /// than 1500 gets index 0, the next ones (at most three in total) index 1 within 13000, then index 2 within 21200,
    /// else index 3. Distances come from 0x10067cff: the level uses the 3D distance, the "nearest" bookkeeping the
    /// horizontal one. camera[+0xb8] = half viewport width * zoom / (160 * resolution factor), i.e. 1 at 320x200.
    /// Destroyed units take index 1 (the player 0) and are left out of the nearest-unit bookkeeping.
    /// </summary>
    public class Mw2LodSwitcher : MonoBehaviour
    {
        readonly List<(Mw2LodGroup group, int index, float distance)> _units = new List<(Mw2LodGroup, int, float)>();
        Mw2LodGroup[] _groups;
        float _groupsTime = -1f;

        void LateUpdate()
        {
            var cam = Camera.main;
            if (cam == null) return;
            // 0x1001f099 uses the camera's current focal scale; viewport setup makes it
            // half the window width divided by tan(horizontal FOV / 2).
            float zoom = 1f / (Mathf.Tan(cam.fieldOfView * Mathf.Deg2Rad * 0.5f) * cam.aspect);
            float near0 = zoom * 1500f, near1 = zoom * 13000f, near2 = zoom * 21200f;

            _units.Clear();
            int nearest = -1, second = -1, third = -1;
            float threshold = near0;
            // The scene's groups, looked up again every 2 s rather than every frame.
            if (_groups == null || Time.unscaledTime - _groupsTime > 2f)
            {
                _groups = FindObjectsByType<Mw2LodGroup>(FindObjectsSortMode.None);
                _groupsTime = Time.unscaledTime;
            }
            foreach (var group in _groups)
            {
                if (group == null) continue;
                int i = _units.Count;
                if (group.TryGetComponent<Mw2Cockpit>(out var cockpit) && cockpit.InCockpit)
                {
                    _units.Add((group, 4, 0));
                    continue;
                }
                // 0x1001f099: a destroyed unit (gp +0x14 & 2) keeps index 1, the player's own 0.
                if (group.TryGetComponent<Mw2MechCombat>(out var dead) && dead.Destroyed)
                {
                    _units.Add((group, dead.IsPlayer ? 0 : 1, 0));
                    continue;
                }
                var delta = group.transform.position - cam.transform.position;
                float d = delta.magnitude / PolyModel.UnitsToMeters;
                float flat = new Vector2(delta.x, delta.z).magnitude / PolyModel.UnitsToMeters;
                _units.Add((group, -2, d));
                if (flat < threshold)
                {
                    // Same bookkeeping as the original: each closer unit pushes the previous ones down the list.
                    threshold = flat;
                    third = second;
                    second = nearest;
                    nearest = i;
                }
            }

            int detailed = 0;
            for (int i = 0; i < _units.Count; i++)
            {
                var (group, index, d) = _units[i];
                if (index == -2)
                {
                    if (i == nearest && d < near0) index = 0;
                    else if (i == second || (i == third && d < near1)) { detailed++; index = 1; }
                    else if ((second == -1 || third == -1) && d < near1 && detailed < 3) { detailed++; index = 1; }
                    else if (d < near2) index = 2;
                    else index = 3;
                }
                group.Show(index + 1);
            }
        }
    }
}
