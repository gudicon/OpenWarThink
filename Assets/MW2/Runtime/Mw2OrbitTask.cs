using MW2.Formats;
using UnityEngine;

namespace MW2
{
    /// <summary>
    /// TSK class 2, FUN_1000e3d0 0x1000e3d0: moves a node along a circle and turns it with the
    /// same angle. SOL/WOL SCRI use this for four invisible crowd/rain/jungle sound emitters.
    /// FUN_1005a7ca adds (sin(angle+90), 0, -sin(angle)) * speed * elapsedTicks/181
    /// in MW2 coordinates; FUN_1005ab08 then adds the yaw.
    /// </summary>
    public sealed class Mw2OrbitTask : MonoBehaviour
    {
        public int DegreesPerSecond;
        public int UnitsPerSecond;
        float _angle;

        void Update()
        {
            float seconds = Time.deltaTime;
            float distance = UnitsPerSecond * seconds * PolyModel.UnitsToMeters;
            float radians = _angle * Mathf.Deg2Rad;
            transform.localPosition += new Vector3(Mathf.Cos(radians) * distance, 0f,
                                                    -Mathf.Sin(radians) * distance);
            float turn = DegreesPerSecond * seconds;
            transform.localRotation *= Quaternion.AngleAxis(turn, Vector3.up);
            _angle = Mathf.Repeat(_angle + turn, 360f);
        }
    }
}
