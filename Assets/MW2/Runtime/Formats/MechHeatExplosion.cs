using System;
using System.Collections.Generic;

namespace MW2.Formats
{
    /// <summary>Mech_HeatExplode 0x10035915: find the first intact section slot containing live ammunition.</summary>
    public static class MechHeatExplosion
    {
        /// <summary>0x10035915 scans sections 1..8, then their slots, skipping destroyed sections; <paramref name="hasAmmo"/>
        /// gets the slot's critical id (a bin with it whose pool has rounds).</summary>
        public static (int section, int slot) FirstLiveAmmo(IList<int>[] sections, bool[] sectionGone, Func<int, bool> hasAmmo)
        {
            if (sections == null || hasAmmo == null) return (0, -1);
            for (int section = 0; section < Math.Min(8, sections.Length); section++)
            {
                if (sectionGone != null && section < sectionGone.Length && sectionGone[section]) continue;
                var slots = sections[section];
                if (slots == null) continue;
                for (int slot = 0; slot < slots.Count; slot++)
                    if (slots[slot] > 10000 && hasAmmo(slots[slot]))
                        return (section + 1, slot);
            }
            return (0, -1);
        }
    }
}
