namespace MW2.Formats
{
    /// <summary>Mech_UpdateControl 0x1003a449 (0x1003b448–0x1003b567): state 0 schedules state 1, which ends strictly after its deadline.</summary>
    public static class MechPowerTiming
    {
        public const int BaseTicks = 0x43e;
        public const int RandomWindow = 0x16a;

        /// <summary>0x1003b448–0x1003b465: current tick + 0x43e + RandomInt(0x16a).</summary>
        public static int Deadline(int now, int random) => now + BaseTicks + random;

        /// <summary>0x1003b558–0x1003b569: state 1 becomes state 2 only when now is greater than the deadline.</summary>
        public static bool Ready(int now, int deadline) => now > deadline;

        /// <summary>Weapons_Update 0x1006bc57: commands and weapon timers run only in power state 2.</summary>
        public static bool WeaponControlsEnabled(int state) => state == 2;
    }
}
