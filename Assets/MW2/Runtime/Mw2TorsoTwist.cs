using MW2.Formats;
using UnityEngine;

namespace MW2
{
    /// <summary>
    /// Torso twist (mercsw.dll): the target [2] comes from the control block (+4) and is clamped to ±limit
    /// ([0x385] = MGEO[5] / 65536 degrees, 0x1003a449); the current angle [3] follows it through a 0.2 s smoothing
    /// block (0x100380d0) and is applied as the absolute yaw of the first THNG node (+0x6c, 0x1003880d).
    /// Positive angles twist to the right, like the heading (MW2 [3]; the torso heading is heading + twist).
    /// </summary>
    public class Mw2TorsoTwist : MonoBehaviour
    {
        public Transform Pivot;
        [Tooltip("Twist limit in degrees (MGEO[5]); 360 or more means a full turret.")]
        public float Limit = 90f;
        [Tooltip("Requested twist in degrees (control block +4).")]
        public float Target;

        [Header("Weapon pitch")]
        [Tooltip("Node raised with the aim (second THNG, the arm mount).")]
        public Transform PitchNode;
        [Tooltip("Aim pivot above the second THNG; keeps the node's own ANIM rotation intact.")]
        public Transform PitchPivot;
        [Tooltip("Requested pitch in degrees, up positive (control block +0).")]
        public float PitchTarget;

        [Header("Read-only")]
        public float Current;
        public float Pitch;

        readonly ApproachBlock _block = new ApproachBlock();
        readonly ApproachBlock _pitchBlock = new ApproachBlock();      // [5..8], 0.2 s (0x100380d0)
        double _clock;
        bool _started;

        /// <summary>
        /// Torso yaw from the unit's heading and the twist alone (the engine's heading +0x60 plus [3]), not from the
        /// twist node: that node hangs under the animated hips, whose step sway would shake the view and the reticle.
        /// </summary>
        public Quaternion TorsoYaw => Body * Quaternion.Euler(0, Current, 0);

        /// <summary>The unit's frame for the aim; a mech that fell over aims upright (Mw2MechController.AimFrame, 0x1006de54).</summary>
        Quaternion Body
        {
            get
            {
                if (!_controllerChecked) { _controllerChecked = true; TryGetComponent(out _controller); }
                return _controller != null ? _controller.AimFrame : transform.rotation;
            }
        }
        Mw2MechController _controller;
        bool _controllerChecked;

        /// <summary>Direction the torso faces, level; mechs face local +Z.</summary>
        public Vector3 Forward => TorsoYaw * Vector3.forward;

        /// <summary>Line of fire: the torso facing raised by the weapon pitch (mech +0x1c, not the animated pose).</summary>
        public Vector3 Aim => Quaternion.AngleAxis(-Pitch, TorsoYaw * Vector3.right) * Forward;

        /// <summary>Sets target [2] and current [3] at once (0x1003a449, FEET_TO_TORSO: [2] = [3] - delta, [3] = [2]).</summary>
        public void SetTwistImmediate(float value)
        {
            Target = value;
            _block.Target = value;
            _block.Current = value;
            Current = value;
        }

        /// <summary>Places the aim rotation above the animated arm mount, like the torso twist pivot.</summary>
        public void SetPitchNode(Transform node)
        {
            PitchNode = node;
            if (node == null || PitchPivot != null || node.parent == null) return;
            var pivot = new GameObject("weapon pitch").transform;
            pivot.SetParent(node.parent, false);
            pivot.localPosition = node.localPosition;
            pivot.localRotation = Quaternion.identity;
            node.SetParent(pivot, true);
            PitchPivot = pivot;
        }

        void Update()
        {
            _clock += Time.deltaTime * MechMotion.TicksPerSecond;
            int now = (int)_clock;
            if (!_started)
            {
                _block.Init(0, 0, 0.2, now);
                _pitchBlock.Init(0, 0, 0.2, now);
                _started = true;
            }
            if (Limit < 360f) Target = Mathf.Clamp(Target, -Limit, Limit);
            _block.Target = Target;
            Current = _block.Update(now);
            // Mech_UpdateControl 0x1003a449 copies [6] from control +0 without a limit: the player's torso_tilt channel is
            // bounded to +-60 by the channel table of the DLL, the AI's AI_TiltControl to +-64.
            _pitchBlock.Target = PitchTarget;
            Pitch = _pitchBlock.Update(now);
            // 0x1003880d: with flag 0x2000 the torso receives half the weapon elevation (+0x68).
            // The arm mount receives the remaining elevation relative to the torso, so the weapons still aim at
            // the full angle. The torso pitch is about the body's cross axis, ahead of the torso yaw.
            if (Pivot != null)
                Pivot.localRotation = Quaternion.Euler(-Pitch * 0.5f, 0, 0) * Quaternion.Euler(0, Current, 0);
            if (PitchNode != null && PitchPivot == null) SetPitchNode(PitchNode); // existing serialized mechs
            if (PitchPivot != null)
                PitchPivot.localRotation = Quaternion.Euler(-Pitch * (Pivot != null && PitchPivot.IsChildOf(Pivot) ? 0.5f : 1f), 0, 0);
        }
    }
}
