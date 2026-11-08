using MW2.Formats;
using UnityEngine;

namespace MW2
{
    /// <summary>
    /// TSK class 5, FUN_1000e89e 0x1000e89e: moves an area node along a PTBL path on the 181 Hz game clock, e.g. the
    /// governor's cars of BET2 (bet2are2/bet2are3 "1;oneshot,rotate,car1/car2"). The node is placed like a BWD node:
    /// position in model units on the original axes, rotation Euler(pitch, yaw, roll) as in the mission builder.
    /// The task starts when the mission starts and keeps running after the GT on the node is destroyed (no check in the
    /// original update).
    /// </summary>
    public sealed class Mw2PathTask : MonoBehaviour
    {
        public string PathName;
        public int Mode = 2;            // 0 loop, 1 repeat, 2 one pass
        public bool Rotate;
        public Vector3[] Points;        // model units, MW2 axes
        public Vector3[] Angles;        // pitch, yaw, roll degrees
        public int[] Ticks;

        PathMotion _motion;
        double _clock;
        Mw2Surface[] _surfaces;

        void Start()
        {
            if (Points == null || Points.Length < 2) { enabled = false; return; }
            var task = new PathTask
            {
                Node = -1, Mode = Mode, Rotate = Rotate, PathName = PathName,
                Path = new PathTable { Name = PathName, Points = Points, Angles = Angles, Ticks = Ticks }
            };
            _motion = new PathMotion(task, 0);
            Apply();
        }

        void Update()
        {
            _clock += Time.deltaTime * MechMotion.TicksPerSecond;
            if (!_motion.Update((int)_clock))
            {
                if (_motion.Finished) enabled = false;
                return;
            }
            Apply();
        }

        void Apply()
        {
            transform.localPosition = PolyMeshBuilder.ToUnity(_motion.Position * PolyModel.UnitsToMeters);
            var a = _motion.Angles;
            transform.localRotation = Quaternion.Euler(a.x, a.y, a.z);
            if (_surfaces == null) _surfaces = GetComponentsInChildren<Mw2Surface>(true);
            bool boxesChanged = false;
            foreach (var surface in _surfaces)
                if (surface != null && surface.RefreshWorldBox()) boxesChanged = true;
            if (boxesChanged) Physics.SyncTransforms();
        }
    }
}
