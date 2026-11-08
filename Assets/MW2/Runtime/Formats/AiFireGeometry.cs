using System;

namespace MW2.Formats
{
    /// <summary>Weapon_UpdateConvergence 0x1006dc4a and integer approach update
    /// 0x10025312. GP+98 is a 0.2 s block; GP+a8 returns to 50000 over 20 s.
    /// Original integer ticks and wrapping multiplication are retained.</summary>
    public sealed class AimConvergence
    {
        int _fastStamp, _slowStamp;
        public int Distance { get; private set; } = 50000;
        public int LongDistance { get; private set; } = 50000;

        static int Advance(int current, int target, int period, int now, ref int stamp)
        {
            int difference = unchecked(target - current);
            if (difference != 0)
            {
                int step = unchecked(difference * unchecked(now - stamp)) / period;
                current = difference < 0 && step <= difference || difference > 0 && difference <= step
                    ? target : unchecked(current + step);
            }
            stamp = now;
            return current;
        }

        /// <summary>1006dc4a: only GP/GT-class hits replace the long block's current;
        /// empty scene or ordinary geometry lets it approach its original 50000 target.</summary>
        public int Update(int now, bool targetHit, float hitDistance)
        {
            LongDistance = Advance(LongDistance, 50000, 3620, now, ref _slowStamp);
            if (targetHit) LongDistance = hitDistance <= 2000f ? 2000 : (int)hitDistance;
            Distance = Advance(Distance, LongDistance, 36, now, ref _fastStamp);
            return Distance;
        }
    }

    /// <summary>AI_AimRayClear 0x10005050 and Weapon_AimNodeDirection 0x1006de54;
    /// scene intersections remain supplied by Unity.</summary>
    public static class AiFireGeometry
    {
        /// <summary>Weapon_ConvergedDirection 0x1006dd6d -> 0x1004640b:
        /// eye ray at integer GP+a0 minus POFO muzzle, normalized with 100461fe's
        /// max component plus one quarter of the other two.</summary>
        public static AiLeadPoint.Vector ConvergedDirection(AiLeadPoint.Vector eye, AiLeadPoint.Vector aim,
            AiLeadPoint.Vector muzzle, int distance)
        {
            float x = (float)((double)aim.X * distance + eye.X) - muzzle.X;
            float y = (float)((double)aim.Y * distance + eye.Y) - muzzle.Y;
            float z = (float)((double)aim.Z * distance + eye.Z) - muzzle.Z;
            double length = MechSphereContact.Length(x, y, z);
            if (length > 1e-7f)
            {
                float divisor = (float)length;
                x /= divisor; y /= divisor; z /= divisor;
            }
            return new AiLeadPoint.Vector(x, y, z);
        }

        /// <summary>AI_FireNoLead 0x100056a4 / AI_FireLead 0x1000540a:
        /// body GP+5c + weapon mech+1c + cached/predicted pitch, double fmod then float.
        /// The strict ten-degree gate also applies when the body is falling or lying.</summary>
        public static bool FirePitchAllowed(float bodyPitch, float weaponPitch, float targetPitch)
        {
            float sum = (float)(((double)bodyPitch + weaponPitch + targetPitch) % 360.0);
            return sum < 10f && sum > -10f;
        }

        /// <summary>0x1000505b..0x100050c2: C fmod of heading + torso twist - cached target bearing + 360.
        /// Both 90 and 270 degrees are accepted. checkAngle==0 bypasses this gate.</summary>
        public static bool AimInFront(float heading, float twist, float targetBearing, bool checkAngle = true)
        {
            float angle = (float)(((double)heading + twist - targetBearing + 360.0) % 360.0);
            return !checkAngle || angle >= 270f || angle <= 90f;
        }

        /// <summary>FUN_1003232a / FUN_1005a70f, the pitch/yaw outputs used by 1006de54.
        /// Preserve its two pitch branches and near-vertical special case; do not replace with Euler angles.</summary>
        public static void EyeAngles(AiLeadPoint.Vector forward, out float pitch, out float yaw)
        {
            if (forward.Y > 0.1 || forward.Y < -0.1)
            {
                double length = Math.Sqrt((double)forward.X * forward.X + (double)forward.Z * forward.Z);
                float flat = (float)length;
                if (length <= 0.003725290298462)
                {
                    pitch = 0;
                    yaw = BitConverter.SingleToInt32Bits(forward.Y) < 0 ? 90f : -90f;
                    return;
                }
                pitch = (float)(Math.Acos(flat) * 57.29577951308232);
                if (BitConverter.SingleToInt32Bits(forward.Y) < 0) pitch = -pitch;
            }
            else pitch = (float)(-Math.Asin(forward.Y) * 57.29577951308232);
            yaw = AiLeadPoint.Heading(forward.X, forward.Z);
        }

        /// <summary>Weapon_AimNodeDirection 0x1006de54 -> 100322ff/100317cf.
        /// World EYEO yaw, weapon pitch, and eye pitch only for mech +0xe42 bit 4.
        /// Pitch is native (down positive); roll does not affect the forward vector.</summary>
        public static AiLeadPoint.Vector Direction(float eyePitch, float eyeYaw, float weaponPitch, bool falling)
        {
            float pitch = falling ? eyePitch + weaponPitch : weaponPitch;
            double p = pitch * 0.017453292519943295, y = eyeYaw * 0.017453292519943295;
            return new AiLeadPoint.Vector((float)(Math.Cos(p) * Math.Sin(y)), (float)-Math.Sin(p), (float)(Math.Cos(p) * Math.Cos(y)));
        }
    }
}
