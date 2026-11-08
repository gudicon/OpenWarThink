namespace MW2.Formats
{
    /// <summary>
    /// A mech falling over without a leg (gamepiece +0xe42 bits 4 and 8). Mech_DestroySection 0x100365d3: a leg that comes
    /// off a GP type 1 unit sets bit 4 and the fall rate [0x394] (+0xe50) = RandomNormal() / 1024 (_DAT_10087f18) degrees
    /// per tick. Mech_UpdatePhysics 0x1003880d (0x10039966..0x10039f91): while bit 4 is set and the unit stands
    /// (object +0x78) or its height above the ground minus the leg offset is at most 100 (_DAT_10087f94), the fall angle
    /// [0x391] (+0xe44) and rate run through Motion_Integrate 0x100060a0 with 0.0001 (push 0x38d1b717); the step is kept
    /// only while the rate keeps its sign or the old angle is inside (-5, 5) (_DAT_10087f98 / _DAT_10087f9c). The change
    /// is added to the pitch +0x5c and the body turns about the foot point (the leg offset [0x39a] = MGEO[0] below the
    /// object, 0x10039cf5..0x10039e8b); a pitch inside (90, 180) becomes 90, inside (180, 270) 270. When a THNG node
    /// ([0x364] / [0x365]) with a model reaches the ground (node height minus half the model radius, FUN_10003c5e, at or
    /// below World_GroundHeight), bit 4 clears and bit 8 sets: the unit lies. While either bit is set Mech_Collide is
    /// skipped and the position is not moved. Sound 0xee after the touch (0x10039f12) is unreachable: the touch already
    /// sets bit 8, which that branch requires clear. The straightening branch (0x10039f96: bit 4, not standing, angle not
    /// 0) cannot run either: +0x78 changes only inside the skipped collision block.
    /// </summary>
    public sealed class MechFall
    {
        public const float NormalScale = 1024f;      // _DAT_10087f18
        public const float Acceleration = 0.0001f;   // 0x38d1b717
        public const float NearGround = 100f;        // _DAT_10087f94
        public const float FreeAngle = 5f;           // _DAT_10087f9c (and -5, _DAT_10087f98)

        public bool Falling;    // +0xe42 & 4
        public bool Down;       // +0xe42 & 8
        public float Angle;     // [0x391]
        public float Rate;      // [0x394]
        public float Pitch;     // object +0x5c

        /// <summary>Position and collision are frozen (0x1003880d skips Mech_Collide for bits 4 and 8).</summary>
        public bool Frozen => Falling || Down;

        /// <summary>0x100365d3: the leg came off a mech; <paramref name="normal"/> is a RandomNormal value (sigma 1024).</summary>
        public void Start(int normal)
        {
            Falling = true;
            Rate = normal / NormalScale;
        }

        /// <summary>0x10039966: the fall runs only while the unit stands or is within 100 units of the ground.</summary>
        public static bool Grounded(bool standing, float heightAboveGround) =>
            standing || System.Math.Abs(heightAboveGround) <= NearGround;

        /// <summary>
        /// One update of 0x1003880d with <paramref name="dt"/> ticks. <paramref name="nodeTouches"/>: a THNG node is on the
        /// ground. Returns the change of pitch to apply to the body (about the feet).
        /// </summary>
        public float Step(int dt, bool grounded, bool nodeTouches)
        {
            if (!Falling || !grounded) return 0f;
            if (nodeTouches)
            {
                Falling = false;
                Down = true;
                return 0f;
            }
            float angle = Angle, rate = Rate;
            angle += (dt * Acceleration * 0.5f + rate) * dt;
            rate += dt * Acceleration;
            angle %= 360f;                                  // fmod
            bool keep = (rate > 0f && Rate > 0f) || (rate < 0f && Rate < 0f) || (Angle > -FreeAngle && Angle < FreeAngle);
            if (!keep) return 0f;
            float delta = angle - Pitch;
            Angle = angle;
            Rate = rate;
            float before = Pitch;
            Pitch += delta;
            if (Pitch < 180f && Pitch > 90f) Pitch = 90f;
            if (Pitch > 180f && Pitch < 270f) Pitch = 270f;
            return Pitch - before;
        }
    }
}
