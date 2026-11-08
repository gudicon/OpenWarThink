using System;

namespace MW2.Formats
{
    /// <summary>
    /// Damage of an impact: FUN_100350fb (another unit, relative velocity), FUN_100353af (scenery, own velocity) and FUN_1003519e (where it lands).
    /// Speed is the FUN_100461fe length of the integer velocity (mech +0xe34..): above 2 units per tick, damage = (speed - 2) / 148 * 3.
    /// The location comes from the contact normal DAT_100a66fc: above 0.7071 up, half the damage to each leg (7, 8); below -0.866 up, the head (1), scaled by the
    /// masses when the other is a unit (+0xe18); otherwise the arc of the obstacle's bearing relative to the torso (gp +0x60 + gp +0x6c).
    /// </summary>
    public static class CollisionDamage
    {
        public const float NoDamageSpeed = 2f;      // _DAT_10087ebc

        public static float Amount(float speed) => speed > NoDamageSpeed ? (speed - NoDamageSpeed) / 148f * 3f : 0f;

        public struct Hit { public int Location; public float Damage; public int Location2; }

        /// <param name="bearing">Math_HeadingDeg(-nx, -nz).</param>
        /// <param name="otherTons">The other unit's tons, or -1 for scenery.</param>
        public static Hit Locate(float damage, float normalY, float bearing, float heading, float torso, int tons, int otherTons)
        {
            if (normalY < -0.866f)
            {
                float head = damage;
                if (otherTons >= 0)
                {
                    float own = tons < 1 ? 1f : tons;
                    head = damage * 2f * otherTons / own;
                    if (head < damage) head = damage;
                }
                return new Hit { Location = 1, Damage = head };
            }
            if (normalY > 0.7071f) return new Hit { Location = 7, Location2 = 8, Damage = damage / 2f };
            float relative = bearing - heading - torso;
            if (relative < -180f) relative += 360f;
            else if (relative > 180f) relative -= 360f;
            if (relative <= 0f)
                return new Hit { Location = relative > -45f || relative < -135f ? 4 : 6, Damage = damage };
            return new Hit { Location = relative < 45f || relative > 135f ? 2 : 5, Damage = damage };
        }
    }
}
