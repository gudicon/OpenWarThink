using System;

namespace MW2.Formats
{
    /// <summary>
    /// Time-based smoothing block {last time, target, current, time constant in ticks}.
    /// mercsw.dll 0x100253ed (update) and 0x10025396 (init, seconds * 181).
    /// </summary>
    public sealed class ApproachBlock
    {
        public int LastTime;
        public float Target;
        public float Current;
        public float TimeConstant;

        public void Init(float current, float target, double seconds, int now)
        {
            Current = current;
            Target = target;
            TimeConstant = (float)(seconds * MechMotion.TicksPerSecond);
            LastTime = now;
        }

        public float Update(int now)
        {
            float diff = Target - Current;
            if (Math.Abs(diff) < 1e-7f)
            {
                Current = Target;
            }
            else
            {
                float step = (now - LastTime) * diff / TimeConstant;
                if ((diff < 0 && step <= diff) || (diff > 1e-7f && diff <= step)) Current = Target;
                else Current += step;
            }
            LastTime = now;
            return Current;
        }
    }

    /// <summary>
    /// Ground and jump movement of a mech, ported from mercsw.dll:
    /// control 0x1003a449 (throttle, speed and turn targets), gait selection 0x10028abb,
    /// physics 0x1003880d (velocity, heading, jump acceleration, integration 0x100060a0).
    /// Terrain contact is supplied by the controller; torso twist and heat live in other components.
    /// Units: model units (~1 cm), clock ticks (181 per second), degrees.
    /// </summary>
    public sealed class MechMotion
    {
        /// <summary>Game clock: Miles timer, PIT divisor 0x199C (~182 Hz); the code converts with 181.0 (_DAT_100872c0).</summary>
        public const double TicksPerSecond = 181.0;
        const float ThrottleBias = 1f / 64f;                // _DAT_10087f48: throttle blocks hold value + 1/64
        const float FullThrottle = 1f / 64f;                // 0x3c800000 in the control code
        const float MpToSpeed = 105.49f;                    // _DAT_10087e10: walking MP -> max speed scale
        const float SpeedFootContactTicks = 90.5f;          // 0x42b50000, set while a foot is planted
        public float Gravity = 0.0296f;                     // _DAT_10090148, set from PLNT at mission load
        const float JumpThrustPerMp = 0.1184f;              // _DAT_10087e30
        const int FullJumpFuel = 0x712;                     // mech +0xdf4, in ticks
        const float JumpHeatPerTick = 3f * 0.005859375f;     // 0x1003a449: [0x37e] * _DAT_10087fbc
        const float HardLandingSpeed = -16.153839f;         // _DAT_10087f84
        const float LandingDamageScale = -32.307678f;      // _DAT_10087f88

        // Movement record blocks (indices of the mercsw.dll record in brackets).
        public readonly ApproachBlock Turn = new ApproachBlock();      // [0xd..0x10]
        public readonly ApproachBlock Throttle = new ApproachBlock();  // [0x11..0x14]
        public readonly ApproachBlock Speed = new ApproachBlock();     // [0x9..0xc]

        public float MaxSpeed;          // [0x36e] = walking MP * 105.49
        public float ThrottleScale = 1; // [0x379]
        public bool MascActive;           // DAT_100ad350; only the player's equipped mech can toggle it

        // Game piece fields.
        public float X, Y, Z;           // +0x50..+0x58
        public float Heading;           // +0x60, degrees; 0 faces +Z (MW2 axes)
        public float VelX, VelY, VelZ;  // +0xe28 / +0xe2c / +0xe30, units per tick
        public int JumpMp;
        public int JumpFuel { get; private set; }
        public bool InfiniteJumpFuel { get; private set; }
        public float GeneratedJumpHeat { get; private set; }
        public bool Airborne { get; private set; }
        int _fuelRechargeRemainder;

        // Inputs (control block at game piece +0x4c).
        public float ThrottleInput;     // +0x08 * 64: 1 = full throttle (1/64); the player's channel reaches -0.117..1.057
        public float TurnInput;         // +0x0c, -1..1 (stored as -1024..1024)
        /// <summary>0x1003a449: the turn target is control / 1024 * this * sin(speed + 90); 180 (_DAT_10087fa0) for GP types 2 and 8, else 90.</summary>
        public float TurnLimit = 90f;
        public bool Reverse;            // +0x30
        public bool JumpInput;          // control +0x1d
        public bool JumpLeft, JumpRight; // control +0x1e, +0x1f
        public bool JumpForward, JumpBackward; // control +0x20, +0x21
        /// <summary>FEET_TO_TORSO (DAT_100a7094): the turn block target [0xe] set directly (+-55), see Mw2MechController.</summary>
        public float? TurnTargetOverride;
        public bool FeetToTorso;
        /// <summary>gp +0x10 != 2 (not an AI pilot): the stopped-turn walk of 0x1003a449 applies.</summary>
        public bool ManualControl = true;

        // Outputs for the animation (game piece +0x88 / +0x8c).
        public int AnimTarget = -1;
        public int AnimGait;

        public float SpeedKph => (float)(Math.Sqrt(VelX * VelX + VelZ * VelZ) * TicksPerSecond * 3600 / 100000)
                                 * (Speed.Current < 0 ? -1 : 1);

        /// <summary>
        /// Speed shown by the HUD (mercsw.dll 0x100696cb): trunc(|velocity| * 63356 / 10002 * 1.5), all three axes,
        /// negative while the speed block +0x2c is below zero (shown in colour 6).
        /// </summary>
        public int HudSpeedKph => (int)(Math.Sqrt(VelX * VelX + VelY * VelY + VelZ * VelZ) * 63356f / 10002f * 1.5f)
                                  * (Speed.Current < 0 ? -1 : 1);

        public MechMotion(int walkingMp, int now, int jumpMp = 0)
        {
            MaxSpeed = walkingMp * MpToSpeed;
            JumpMp = jumpMp;
            JumpFuel = jumpMp > 0 ? FullJumpFuel : 0;
            // Initial blocks, mercsw.dll 0x100380d0: 0.3 s for turning, 0.2 s for throttle and speed.
            Turn.Init(0, 0, 0.3, now);
            Speed.Init(0, 0, 0.2, now);
            Throttle.Init(ThrottleBias, ThrottleBias, 0.2, now);
        }

        public void SetJumpMp(int jumpMp)
        {
            bool hadJets = JumpMp > 0;
            JumpMp = Math.Max(0, jumpMp);
            if (JumpMp > 0 && !hadJets) JumpFuel = FullJumpFuel;
        }

        public void SetInfiniteJumpFuel(bool enabled)
        {
            InfiniteJumpFuel = enabled;
            if (enabled && JumpMp > 0) JumpFuel = FullJumpFuel;
        }

        /// <summary>One simulation step. <paramref name="animState"/> = current ANIM state (+0x84), <paramref name="footContact"/> = ANIM flag 2.</summary>
        public void Step(int now, int dt, int animState, bool footContact)
        {
            // Control (0x1003a449).
            float turnUnits = Math.Clamp(TurnInput, -1f, 1f) * 1024f;
            float speedFactor = Math.Min(Speed.Current / 10000f, 80f);
            Turn.Target = turnUnits / 1024f * TurnLimit * (float)Math.Sin((speedFactor + 90f) * Math.PI / 180.0);

            // 0x1003a449: [0x12] = control +8 * [0x379] + 1/64, unclamped (the throttle channel spans -120..1082 / 65536).
            Throttle.Target = ThrottleInput * FullThrottle * ThrottleScale + ThrottleBias;
            // Same function: a stopped mech that turns (+0xc != 0 or DAT_100a7094) walks at 0x3c900000 = 1/64 + 1/512 (gp +0x10 != 2).
            if (ManualControl && Throttle.Target == ThrottleBias && (TurnInput != 0 || FeetToTorso)) Throttle.Target = 0.017578125f;
            if (TurnTargetOverride.HasValue) Turn.Target = TurnTargetOverride.Value;
            float t = Throttle.Current - ThrottleBias;
            if (t < 0.00048828125f) t = 0;
            Speed.Target = t * MaxSpeed;
            if (Speed.Target != 0 && animState == 2) Speed.Target = Speed.Target / 2f * -1f;
            if (footContact && MascActive && !(JumpMp > 0 && JumpInput)) Speed.Target *= 1.5f; // 0x1003a449, +0x80 flag 2, local_14 == 0
            if (footContact) Speed.TimeConstant = SpeedFootContactTicks;

            // Gait selection (0x10028abb).
            if (Throttle.Current > 0.01611328125f)
            {
                float g = Throttle.Current - ThrottleBias;
                if (g < 0) g = 0;
                AnimTarget = 0;
                if (g < 0.00390625f) AnimGait = 1;
                else if (g < 0.01171875f) AnimGait = 2;
                else { AnimGait = 3; AnimTarget = 1; }
                if (animState == 2 && AnimGait > 2) AnimGait = 2;
                if (Reverse) AnimTarget = 2;
            }
            else
            {
                AnimGait = 0;
                AnimTarget = -1;
            }

            // Physics (0x1003880d): smoothing blocks in the original order (turn, throttle, speed).
            Turn.Update(now);
            Throttle.Update(now);
            Speed.Update(now);

            float speed = Speed.Current;
            if (speed < 0.00048828125f && speed > -0.00048828125f) speed = 0;
            double h = Heading * Math.PI / 180.0;
            float targetX = speed * (float)Math.Sin(h);     // +0xbc
            float targetZ = speed * (float)Math.Cos(h);     // +0xb8
            bool firingJets = JumpMp > 0 && JumpInput && JumpFuel > 0;
            GeneratedJumpHeat = firingJets ? JumpHeatPerTick * dt : 0f;
            float thrust = MaxSpeed > 0 ? JumpThrustPerMp * JumpMp / (MaxSpeed / MpToSpeed) : 0f;
            float accelX = 0f, accelZ = 0f;
            float accelY = -Gravity;

            // 0x1003880d: flight uses quadratic horizontal drag, not the ground speed target.
            // The original uses game-piece +0x78 (ground contact) as the other flight condition.
            if (Airborne || firingJets)
            {
                float horizontalSpeed = (float)Math.Sqrt(VelX * VelX + VelZ * VelZ);
                float referenceSpeed = MaxSpeed * FullThrottle * 2.5f;
                if (horizontalSpeed > 1e-7f && referenceSpeed > 1e-7f && thrust > 1e-7f)
                {
                    float square = referenceSpeed * referenceSpeed;
                    float quadratic = (thrust - (thrust / referenceSpeed) / 3f) /
                                      (square - 3f * referenceSpeed / 4f);
                    float linear = (thrust - quadratic * square) / referenceSpeed;
                    float drag = -((quadratic * horizontalSpeed + linear) * horizontalSpeed);
                    accelX = drag * VelX / horizontalSpeed;
                    accelZ = drag * VelZ / horizontalSpeed;
                }
            }
            else
            {
                float div = dt < 45 ? 45f : dt;
                accelX = dt < 1 ? 0 : (targetX - VelX) / div;
                accelZ = dt < 1 ? 0 : (targetZ - VelZ) / div;
            }

            // 0x1003880d: +0x1d enables vertical thrust; directional controls +0x1e..+0x21
            // are honoured only while turn input (+0xc) is exactly zero.
            if (firingJets)
            {
                Airborne = true;
                if (VelY > 0) accelY += ((Gravity - thrust) * 3620f * VelY) / 100000f;
                accelY += thrust;
                if (TurnInput == 0f && (JumpLeft || JumpRight || JumpForward || JumpBackward))
                {
                    accelY = VelY > 0 ? -Gravity : 0f;
                    float sin = (float)Math.Sin(h), cos = (float)Math.Cos(h);
                    // Source priority: left, right, forward, backward (one direction per tick).
                    if (JumpLeft) { accelX -= cos * thrust; accelZ += sin * thrust; }
                    else if (JumpRight) { accelX += cos * thrust; accelZ -= sin * thrust; }
                    else if (JumpForward) { accelX += sin * thrust; accelZ += cos * thrust; }
                    else { accelX -= sin * thrust; accelZ -= cos * thrust; }
                }
            }
            if (JumpMp > 0 && JumpInput && InfiniteJumpFuel)
            {
                JumpFuel = FullJumpFuel;
                _fuelRechargeRemainder = 0;
            }
            else if (JumpMp > 0 && JumpInput)
            {
                // 0x1003a449 keeps consuming/clamping fuel while jump is held, even at zero.
                // Recharging an empty tank here would produce periodic thrust without releasing the key.
                JumpFuel = Math.Max(0, JumpFuel - dt);
                _fuelRechargeRemainder = 0;
            }
            else if (JumpMp > 0)
            {
                // 0x1003a449: integer quarter-rate recharge, preserving sub-frame ticks.
                _fuelRechargeRemainder += dt;
                JumpFuel = Math.Min(FullJumpFuel, JumpFuel + _fuelRechargeRemainder / 4);
                _fuelRechargeRemainder %= 4;
            }

            float dx = 0, dy = 0, dz = 0;
            Integrate(ref dx, ref VelX, accelX, dt);
            if (Airborne) Integrate(ref dy, ref VelY, accelY, dt);
            Integrate(ref dz, ref VelZ, accelZ, dt);
            if (VelX < 0.0625f && VelX > -0.0625f) VelX = 0;
            if (VelZ < 0.0625f && VelZ > -0.0625f) VelZ = 0;
            X += dx;
            Y += dy;
            Z += dz;

            Heading = (float)((Heading + dt / 181.0 * Turn.Current) % 360.0);
        }

        /// <summary>Terrain contact after 0x1003880d integrates vertical velocity.</summary>
        public float LandAt(float groundY)
        {
            float damage = 0f;
            // 0x1003880d: a mech above the ground (game piece +0x78 = 0) is not held up, gravity takes it down; this is
            // how units placed at a high start point drop to the ground at mission start. Approximation: 1 m (100 units)
            // of tolerance keeps slopes and steps walkable (the original compares with the leg offset [0x380]).
            if (!Airborne && Y > groundY + 100f)
            {
                Airborne = true;
                return 0f;
            }
            if (!Airborne || (VelY <= 0 && Y <= groundY))
            {
                if (Airborne && VelY < HardLandingSpeed && Y < groundY)
                    damage = (VelY - HardLandingSpeed) / LandingDamageScale * 50f;
                Y = groundY;
                VelY = 0;
                Airborne = false;
            }
            return damage;
        }

        /// <summary>mercsw.dll 0x100060a0.</summary>
        static void Integrate(ref float position, ref float velocity, float accel, int dt)
        {
            position += (dt * accel * 0.5f + velocity) * dt;
            velocity += dt * accel;
        }
    }
}
