namespace MW2.Formats
{
    /// <summary>
    /// Pilot look: FUN_10023c23 / FUN_10023d89, mercsw.dll 0x10023c23 / 0x10023d89.
    /// The input layer integrates pilot_tilt / pilot_pan; glances temporarily replace their targets.
    /// FirstEyepoint 0x10022f20 gives both filters a 0.2 second time constant.
    /// </summary>
    public sealed class CockpitLook
    {
        readonly ApproachBlock _yaw = new ApproachBlock(), _pitch = new ApproachBlock();
        bool _glanceReleased;

        public float Yaw => _yaw.Current;
        public float Pitch => _pitch.Current;

        public void Enter(int now)
        {
            _yaw.Init(0f, 0f, 0.2, now);
            _pitch.Init(0f, 0f, 0.2, now);
        }

        public void Step(InputSystem input, int now)
        {
            bool left = input.Block[0x76] != 0, right = input.Block[0x77] != 0;
            bool up = input.Block[0x78] != 0, down = input.Block[0x79] != 0;
            // Original priority: left, right, up, down. Releasing a glance centres both channels once;
            // subsequent frames retain the angles integrated by the pilot controls.
            if (left) input.SetFloat(0x54, -70f);
            else if (right) input.SetFloat(0x54, 70f);
            else if (up) input.SetFloat(0x50, -50f);
            else if (down) input.SetFloat(0x50, 40f);
            else if (!_glanceReleased)
            {
                input.SetFloat(0x50, 0f);
                input.SetFloat(0x54, 0f);
            }
            _glanceReleased = !left && !right && !up && !down;
            _yaw.Target = input.Float(0x54);
            _pitch.Target = input.Float(0x50);
            _yaw.Update(now);
            _pitch.Update(now);
        }
    }
}
