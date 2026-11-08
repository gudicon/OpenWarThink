using System;
using System.Collections.Generic;

namespace MW2.Formats
{
    /// <summary>
    /// Mek_WriteUnit 0x10058f90: the MEK a career unit leaves after the mission (Units_WriteMeks 0x100598b1 writes it over
    /// "mek\&lt;name&gt;.mek", which the shell wrote for the launch). Layout as read by Mech_LoadMek 0x1002f9e0: the name
    /// block 0x32 bytes before the header in the unit (+0xe89) is written last.
    /// Header (+0xebb): tons, walk MP = ftol(max speed +0xdb8 / 105.49) (the max speed is walk x 105.49 divided by the
    /// gravity factor DAT_1009014c in Mech_InitMovement), jump MP [0x37e], the MEK's heat sinks (+0xec7 unchanged), the
    /// weapon and ammunition counts. Sections: armour / armour factor of the unit's side and difficulty (the factor
    /// Mech_LoadMek applied), internal structure, all zero for a section without structure; the criticals still present
    /// keep their positions in the MEK's list (removed ones become 0), the MEK's count, flags (&amp; 0xfff) | 0x800.
    /// Before that, the critical ids of empty bins leave the section lists. Weapons: those not destroyed (slot +0x38),
    /// in MEK order, with mount section (legs to their torsos) | fire group &lt;&lt; 8. Ammunition: bins with rounds left.
    /// </summary>
    public static class MekWriter
    {
        public sealed class Unit
        {
            public MekLoadout Mek;
            public byte[] MekBytes;
            public int ArmorFactor;
            public int[] Front, Rear, Internal;
            public List<int>[] Items;
            /// <summary>Weapon ids still working (not hit by a critical) and their current fire groups, any order.</summary>
            public Dictionary<int, int> WorkingWeapons;
            public AmmoStore Ammo;
            /// <summary>The unit's section words (+0x26 after Mech_LoadMek set the damage scale nibbles and 0x800); null: the MEK's.</summary>
            public int[] SectionFlags;
            public int JumpMp;
            public float GravityFactor = 1f;
        }

        public static byte[] Write(Unit u)
        {
            var mek = u.Mek;
            byte[] src = u.MekBytes;
            int weaponsIn = Math.Min(BitConverter.ToInt32(src, 0x10), 14), ammoIn = BitConverter.ToInt32(src, 0x14);
            // Current criticals as the unit keeps them: 12 slots, the list then zeros.
            var current = new int[8][];
            var counts = new int[8];
            for (int s = 0; s < 8; s++)
            {
                current[s] = new int[12];
                var items = u.Items != null && s < u.Items.Length && u.Items[s] != null ? u.Items[s] : new List<int>();
                for (int k = 0; k < items.Count && k < 12; k++) current[s][k] = items[k];
                counts[s] = Math.Min(items.Count, 12);
            }
            // Empty bins: their critical ids leave the lists (shifted, last slot 0, count unchanged).
            if (u.Ammo != null)
                for (int b = 0; b < u.Ammo.Bins.Count && b < 0x19; b++)
                {
                    var bin = u.Ammo.Bins[b];
                    if (bin.Count >= 1) continue;
                    for (int s = 0; s < 8; s++)
                        for (int j = 0; j < 12; j++)
                            if (current[s][j] == bin.CritId)
                            {
                                int l = j;
                                for (; l < 11; l++) current[s][l] = current[s][l + 1];
                                current[s][l] = 0;
                            }
                }
            var sections = new byte[0x140];
            for (int s = 0; s < 8; s++)
            {
                int e = 0x18 + s * 0x28;
                var original = new int[12];
                for (int k = 0; k < 12; k++) original[k] = BitConverter.ToUInt16(src, e + 0xc + k * 2);
                int count = BitConverter.ToInt16(src, e + 0x24);
                int word = u.SectionFlags != null && s < u.SectionFlags.Length ? u.SectionFlags[s] : BitConverter.ToUInt16(src, e + 0x26);
                int flags = (word & 0xfff) | 0x800;
                int front = 0, rear = 0, intern = 0;
                var crits = new int[12];
                if (u.Internal[s] > 0)
                {
                    intern = u.Internal[s];
                    front = (int)((float)u.Front[s] / u.ArmorFactor);
                    rear = (int)((float)u.Rear[s] / u.ArmorFactor);
                    for (int j = 0; j < 12 && j < counts[s]; j++)
                    {
                        int id = current[s][j];
                        for (int k = 0; k < 12; k++)
                            if (id == original[k] && crits[k] == 0)
                            {
                                crits[k] = id;
                                original[k] = 0;
                                break;
                            }
                    }
                }
                int o = s * 0x28;
                Put32(sections, o, front);
                Put32(sections, o + 4, rear);
                Put32(sections, o + 8, intern);
                for (int k = 0; k < 12; k++) Put16(sections, o + 0xc + k * 2, crits[k]);
                Put16(sections, o + 0x24, count);
                Put16(sections, o + 0x26, flags);
            }
            var weapons = new List<(int first, int second)>();
            for (int i = 0; i < weaponsIn; i++)
            {
                int first = BitConverter.ToInt32(src, 0x158 + i * 8);
                int id = first & 0xffff;
                if (u.WorkingWeapons == null || !u.WorkingWeapons.TryGetValue(id, out int group)) continue;
                int mount = 0;
                for (int s = 0; s < 8; s++)
                    for (int k = 0; k < 12; k++)
                        if (BitConverter.ToUInt16(src, 0x18 + s * 0x28 + 0xc + k * 2) == first)
                            mount = s == 6 ? 1 : s == 7 ? 3 : s;
                weapons.Add((first, mount | (group & 0xf) << 8));
            }
            var ammo = u.Ammo != null ? u.Ammo.Remaining() : new List<(int, int)>();
            int nameStart = 0x158 + weaponsIn * 8 + ammoIn * 8;
            var result = new byte[0x18 + 0x140 + weapons.Count * 8 + ammo.Count * 8 + 0x32];
            float speed = (float)(mek.WalkingMp * 105.49);
            speed /= u.GravityFactor;
            Put32(result, 0, mek.Tons);
            Put32(result, 4, (int)(speed / 105.49));
            Put32(result, 8, u.JumpMp);
            Put32(result, 0xc, mek.HeatSinks);
            Put32(result, 0x10, weapons.Count);
            Put32(result, 0x14, ammo.Count);
            Buffer.BlockCopy(sections, 0, result, 0x18, 0x140);
            int p = 0x158;
            foreach (var (first, second) in weapons) { Put32(result, p, first); Put32(result, p + 4, second); p += 8; }
            foreach (var (crit, weapon) in ammo) { Put32(result, p, (short)crit); Put32(result, p + 4, weapon); p += 8; }
            // Name block (+0xe89): the name by strcpy, then +0x1e..+0x31 of the MEK's block.
            int n = 0;
            while (n < 0x1e && nameStart + n < src.Length && src[nameStart + n] != 0) n++;
            Buffer.BlockCopy(src, nameStart, result, p, n);
            if (nameStart + 0x32 <= src.Length) Buffer.BlockCopy(src, nameStart + 0x1e, result, p + 0x1e, 0x14);
            return result;
        }

        static void Put32(byte[] d, int o, int v)
        {
            d[o] = (byte)v; d[o + 1] = (byte)(v >> 8); d[o + 2] = (byte)(v >> 16); d[o + 3] = (byte)(v >> 24);
        }

        static void Put16(byte[] d, int o, int v)
        {
            d[o] = (byte)v; d[o + 1] = (byte)(v >> 8);
        }
    }
}
