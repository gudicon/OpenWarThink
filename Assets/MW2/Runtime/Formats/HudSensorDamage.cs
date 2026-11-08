using System;

namespace MW2.Formats
{
    /// <summary>Mech_CriticalHit 0x100377ea / Hud_DamageGauges 0x10017bd1: damage each of the 34 player gauges independently.</summary>
    public static class HudSensorDamage
    {
        public const int GaugeCount = 0x22;

        /// <summary>0x10017bd1: ordinary damage uses RandomInt(10) &lt; 2; sensor damage uses &lt; 5.</summary>
        public static void Apply(int[] levels, bool sensors, Func<int, int> randomInt)
        {
            if (levels == null || levels.Length < GaugeCount) throw new ArgumentException("34 gauge levels required", nameof(levels));
            int threshold = sensors ? 5 : 2;
            for (int i = 0; i < GaugeCount; i++)
                if (randomInt(10) < threshold) levels[i]++;
        }

        /// <summary>Hud_DrawWindow 0x1003e925: only modes 3..8 flicker; damage 1 toggles the shared blank state, damage &gt;2 is dark.</summary>
        public static bool HideWindow(int level, int mode, ref bool blank, Func<int, int> randomInt)
        {
            if (mode < 3 || mode > 8) return false;
            if (level > 2) return true;
            if (level != 1) return false;
            if (blank)
            {
                if (randomInt(10) < 7) blank = false;
                return true;
            }
            if (randomInt(10) < 3) blank = true;
            return false;
        }
    }
}
