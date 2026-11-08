using System;

namespace MW2.Formats
{
    /// <summary>AI_LeadPoint 0x10005caa. GP origins/distance are model units,
    /// target velocity is mech +0xe28..0xe30 in units per tick. No position sampling.</summary>
    public static class AiLeadPoint
    {
        public struct Vector { public float X, Y, Z; public Vector(float x, float y, float z) { X = x; Y = y; Z = z; } }
        static readonly float[] Atan = BuildTable(false), Sin = BuildTable(true);

        /// <summary>FUN_10045e50: 512 entries plus exact endpoint, using the original double constants.</summary>
        static float[] BuildTable(bool sine)
        {
            var table = new float[513];
            for (int i = 0; i < 512; i++)
                table[i] = (float)(sine ? Math.Sin(i * 0.003067961572265625) : Math.Atan(i / 512.0) * 57.29577957855229);
            table[512] = sine ? 1f : 45f;
            return table;
        }

        /// <summary>Math_HeadingDeg 0x10045fd8: nearest table ratio, including heading(0,0)=45.</summary>
        public static float Heading(float x, float z)
        {
            float ax = Math.Abs(x), az = Math.Abs(z);
            if (ax < 1e-7f) return az < 1e-7f ? 45f : z < 0 ? 180f : 0f;
            if (az < 1e-7f) return x < 0 ? -90f : 90f;
            float angle;
            if (Math.Abs((double)ax - az) < 1e-7f) angle = 45f;
            else if (ax < az) angle = Atan[(int)((double)ax / az * 512 + 0.5)];
            else angle = 90f - Atan[(int)((double)az / ax * 512 + 0.5)];
            if (z < 0) angle = 180f - angle;
            return x < 0 ? -angle : angle;
        }

        /// <summary>FUN_10045ed6: mirrored nearest sine lookup.</summary>
        static float Sine(float angle)
        {
            if (Math.Abs(angle) < 1e-7f) return 0;
            bool negative = angle < 0;
            angle = Math.Abs(angle) % 360f;
            if (angle > 180f) { angle -= 180f; negative = !negative; }
            double a = angle > 90 ? 180.0 - angle : angle;
            float value = Sin[(int)(a * 5.688889026641846 + 0.5)];
            return negative ? -value : value;
        }

        /// <summary>Vector_ToSpherical 0x10067cff: yaw, cached 3D/flat distances and upward pitch.</summary>
        public static void Spherical(Vector delta, out float yaw, out float distance, out float flat, out float pitch)
        {
            yaw = Heading(delta.X, delta.Z);
            bool useX = Math.Abs(delta.X) > Math.Abs(delta.Z);
            float divisor = Sine(useX ? yaw : yaw + 90f);
            flat = Math.Abs(divisor) < 1e-7f ? 0 : (useX ? delta.X : delta.Z) / divisor;
            pitch = Heading(delta.Y, flat);
            divisor = Sine(pitch + 90f);
            distance = Math.Abs(divisor) < 1e-7f ? 0 : flat / divisor;
        }

        /// <summary>0x10005d5c..0x1000602e: writes outputs only after velocity/time gates pass.
        /// The dot-product sign bit (including -0) signs the speed; equality is exact.
        /// The distance is the caller's cached GP +0xc4, and frameTicks is the integer timer delta.</summary>
        public static bool Predict(Vector self, Vector target, Vector velocity, float cachedDistance,
            float weaponSpeed, int lifetime, int frameTicks, float heading,
            ref Vector point, ref float bearing, ref float pitch)
        {
            if (Math.Abs(velocity.X) < 1e-7f && Math.Abs(velocity.Y) < 1e-7f && Math.Abs(velocity.Z) < 1e-7f) return false;
            float dx = target.X - self.X, dy = target.Y - self.Y, dz = target.Z - self.Z;
            float dot = (float)((double)dx * velocity.X + (double)dy * velocity.Y + (double)dz * velocity.Z);
            float speed = (float)Math.Sqrt((double)velocity.X * velocity.X + (double)velocity.Y * velocity.Y + (double)velocity.Z * velocity.Z);
            if (BitConverter.SingleToInt32Bits(dot) < 0) speed = -speed;
            float time = Math.Abs(speed) == weaponSpeed ? 2f : (float)(cachedDistance / ((double)weaponSpeed - speed));
            if (time == 0 || !(time <= lifetime) || !(frameTicks < time)) return false;
            int ticks = (int)time;
            point = new Vector((float)((double)velocity.X * ticks + target.X),
                (float)((double)velocity.Y * ticks + target.Y), (float)((double)velocity.Z * ticks + target.Z));
            Spherical(new Vector(point.X - self.X, point.Y - self.Y, point.Z - self.Z), out float yaw, out _, out _, out pitch);
            bearing = yaw - heading;
            if (bearing > 180) bearing -= 360;
            else if (bearing < -180) bearing += 360;
            return true;
        }
    }
}
