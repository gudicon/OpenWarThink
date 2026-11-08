namespace MW2.Shell
{
    /// <summary>
    /// The fire groups of a mech's weapons (grouping.cpp, MW2SHELL.DLL): bits 8..10 of each weapon's second int (+0x2f4 +
    /// k × 8), 0 (none) to 4. The MEK carries them to the engine (Mech_WriteUnit writes them back).
    /// </summary>
    public static class WeaponGroups
    {
        public static int Group(MechRecord m, int slot) => (m.I32(0x2f4 + slot * 8) & 0x700) >> 8;

        static void Set(MechRecord m, int slot, int group) =>
            m.SetI32(0x2f4 + slot * 8, (int)((uint)m.I32(0x2f4 + slot * 8) & 0xfffff0ffu) | group << 8);

        /// <summary>FUN_1002a511 0x1002a511: a group above 4 is cleared (bits 8..11) when the list is built.</summary>
        public static void Clamp(MechRecord m, int slot)
        {
            if ((uint)Group(m, slot) > 4) Set(m, slot, 0);
        }

        /// <summary>FUN_1002aa21 0x1002aa21: the next group, after 4 none again.</summary>
        public static int Next(MechRecord m, int slot)
        {
            int g = Group(m, slot) + 1;
            if (g > 4) g = 0;
            Set(m, slot, g);
            return g;
        }

        /// <summary>FUN_1002ac31 spot 4 ("bwdefl"): every weapon without a group.</summary>
        public static void Clear(MechRecord m)
        {
            for (int k = 0; k < 0xe; k++) Set(m, k, 0);
        }
    }
}
