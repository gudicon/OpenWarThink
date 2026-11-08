using System;
using System.Collections.Generic;
using System.Text;
using MW2.Formats;

namespace MW2.Shell
{
    /// <summary>
    /// userstar.bwd, the player's lance the shell writes before a mission (FUN_100748e8 0x100748e8 -> FUN_10052829
    /// 0x10052829 with the launch roster of FUN_10024a97): FUN_1000813a starts a BWD (header "BWD\0", size, largest
    /// chunk; REV "1.22"; DTBL of zeros), FUN_10052540 0x10052540 adds one GPS chunk (0x62 bytes, padded to 0x64 by
    /// FUN_100080c7) for each of the first three roster slots, FUN_100081ec writes it. GPS body: +0 MEK id (only for a
    /// "...std" MEK, else 0xfffe), +2 model BWD id, +4 team 0, +5 leader (slot 0), +6 0 for the player else 2, +8 eight
    /// shorts: the pilot's +0x28 values (the pilot whose mech has the slot's MEK name), negatives replaced from
    /// DAT_100b78b0 (1, 500, 300, 400, 1, 0, 0, 0); +0x18 6, +0x1a 0x400, +0x1c model, +0x25 MEK, +0x2e pilot name.
    /// The AeroTech slot 3 is not part of it.
    /// </summary>
    public static class UserStar
    {
        static readonly short[] Defaults = { 1, 500, 300, 400, 1, 0, 0, 0 };     // DAT_100b78b0

        public static byte[] Build(List<Career.LaunchEntry> roster, Career career, ShellTables tables, PrjArchive prj)
        {
            var chunks = new List<byte[]>();
            int pilotId = -1;
            var slots = new List<Career.LaunchEntry>(roster);
            slots.Sort((x, y) => x.Slot.CompareTo(y.Slot));
            foreach (var slot in slots)
            {
                if (slot.Technician || slot.Slot > 2) continue;
                int model = tables.FindMech(slot.MekName);
                if (model < 0) continue;
                // FUN_10052829: the pilot whose mech carries this MEK name (the last match is kept between slots).
                foreach (var p in career.UnitPilots)
                {
                    if (p.MechId == -1) continue;
                    var mech = career.UnitMechs.Find(m => m.MechId == p.MechId);
                    if (mech == null) continue;
                    // FUN_10052829 calls FUN_1000739d on both strings before comparing them.
                    // It uppercases them in place; the subsequent GPS contains that changed MEK name.
                    mech.MekName = mech.MekName.ToUpperInvariant();
                    slot.MekName = slot.MekName.ToUpperInvariant();
                    if (string.Compare(mech.MekName, 0, slot.MekName, 0, 8, StringComparison.Ordinal) == 0)
                    {
                        pilotId = p.Id;
                        break;
                    }
                }
                chunks.Add(Gps(slot, slot.Slot, pilotId, career, tables.MechModel(model), prj));
            }
            int size = 12 + 12 + 0x1c;
            int largest = 0x1c;
            foreach (var c in chunks) { size += c.Length; largest = Math.Max(largest, c.Length); }
            var d = new byte[size];
            Encoding.ASCII.GetBytes("BWD\0", 0, 4, d, 0);
            Put32(d, 4, size);
            Put32(d, 8, largest);
            Encoding.ASCII.GetBytes("REV\0", 0, 4, d, 12);
            Put32(d, 16, 12);
            Encoding.ASCII.GetBytes("1.22", 0, 4, d, 20);
            Encoding.ASCII.GetBytes("DTBL", 0, 4, d, 24);
            Put32(d, 28, 0x1c);
            int o = 24 + 0x1c;
            foreach (var c in chunks) { Buffer.BlockCopy(c, 0, d, o, c.Length); o += c.Length; }
            return d;
        }

        /// <summary>FUN_10052540 for team 0 (difficulty row 0).</summary>
        static byte[] Gps(Career.LaunchEntry slot, int index, int pilotId, Career career, string model, PrjArchive prj)
        {
            var c = new byte[0x64];
            Encoding.ASCII.GetBytes("GPS\0", 0, 4, c, 0);
            Put32(c, 4, 100);
            string mek = slot.MekName.Length > 8 ? slot.MekName.Substring(0, 8) : slot.MekName;
            bool std = slot.MekName.Length >= 8 && string.Compare(slot.MekName, 5, "std", 0, 3, StringComparison.OrdinalIgnoreCase) == 0;
            int mekId = std && prj != null && prj.TryFind("MEK", mek, out var me) ? me.Id : 0xfffe;
            int modelId = prj != null && prj.TryFind("BWD", model, out var be) ? be.Id : -1;
            Put16(c, 8, mekId);
            Put16(c, 0xa, modelId);
            c[0xc] = 0;
            c[0xd] = (byte)(index == 0 ? 1 : 0);
            c[0xe] = (byte)(index == 0 ? 0 : 2);
            var skills = new short[] { 0, 0, 0, 0, 0, 0, 0, (short)index };
            var pilot = pilotId >= 0 ? career.UnitPilots.Find(p => p.Id == pilotId) : null;
            if (pilot != null) for (int k = 0; k < 8; k++) skills[k] = pilot.I16(0x28 + k * 2);
            for (int k = 0; k < 8; k++)
            {
                if (skills[k] < 0) skills[k] = Defaults[k];
                Put16(c, 0x10 + k * 2, skills[k]);
            }
            Put16(c, 0x20, 6);
            Put16(c, 0x22, 0x400);
            Ascii(c, 0x24, model, 8);
            Ascii(c, 0x2d, mek, 8);
            Ascii(c, 0x36, slot.PilotName, 0x15);
            return c;
        }

        static void Ascii(byte[] d, int o, string s, int max)
        {
            var b = Encoding.ASCII.GetBytes(s ?? "");
            Buffer.BlockCopy(b, 0, d, o, Math.Min(b.Length, max));
        }

        /// <summary>
        /// FUN_10052540 0x10052540 in general (the instant-action rosters): the chassis found by the MEK's first three
        /// letters (none: no chunk); +8 the MEK id for a "...std" MEK else 0xfffe, +0xa the model's BWD id, +0xc the team,
        /// +0xd leader (slot 0), +0xe 0 for the player (slot 0, team 0) else 2, +0x10 the skill row 0x100b7850 (team 0 row 0,
        /// others 1..8), +0x1e the slot, negative skills from DAT_100b78b0; +0x20 6, +0x22 0x400, +0x24 model, +0x2d MEK,
        /// +0x36 pilot, +0x4c the third name.
        /// </summary>
        public static byte[] RosterGps(string mek, string pilot, string third, int slot, int team, int row, ShellTables t, PrjArchive prj)
        {
            int chassis = t.FindMech(mek.Length >= 3 ? mek.Substring(0, 3) : mek);
            if (chassis < 0) return null;
            if (row < 1) row = 1;
            if (row > 8) row = 8;
            if (team == 0) row = 0;
            var c = new byte[0x64];
            Encoding.ASCII.GetBytes("GPS\0", 0, 4, c, 0);
            Put32(c, 4, 100);
            string mek8 = mek.Length > 8 ? mek.Substring(0, 8) : mek, model = t.MechModel(chassis);
            bool std = mek.Length >= 8 && string.Compare(mek, 5, "std", 0, 3, StringComparison.OrdinalIgnoreCase) == 0;
            Put16(c, 8, std && prj != null && prj.TryFind("MEK", mek8, out var me) ? me.Id : 0xfffe);
            Put16(c, 0xa, prj != null && prj.TryFind("BWD", model, out var be) ? be.Id : -1);
            c[0xc] = (byte)team;
            c[0xd] = (byte)(slot == 0 ? 1 : 0);
            c[0xe] = (byte)(slot == 0 && team == 0 ? 0 : 2);
            var skills = new short[8];
            for (int k = 0; k < 5; k++) skills[k] = (short)t.Instant.Skills[row].Values[k]; // 0x100b7850 + row x 10
            skills[7] = (short)slot;
            for (int k = 0; k < 8; k++) Put16(c, 0x10 + k * 2, skills[k] < 0 ? Defaults[k] : skills[k]);
            Put16(c, 0x20, 6);
            Put16(c, 0x22, 0x400);
            Ascii(c, 0x24, model, 8);
            Ascii(c, 0x2d, mek8, 8);
            Ascii(c, 0x36, pilot, 0x15);
            Ascii(c, 0x4c, third, 0x15);
            return c;
        }

        /// <summary>FUN_1000813a 0x1000813a + FUN_100081ec 0x100081ec: header, REV "1.22", DTBL of zeros, the chunks.</summary>
        public static byte[] File(List<byte[]> chunks)
        {
            int size = 12 + 12 + 0x1c;
            int largest = 0x1c;
            foreach (var c in chunks) { size += c.Length; largest = Math.Max(largest, c.Length); }
            var d = new byte[size];
            Encoding.ASCII.GetBytes("BWD\0", 0, 4, d, 0);
            Put32(d, 4, size);
            Put32(d, 8, largest);
            Encoding.ASCII.GetBytes("REV\0", 0, 4, d, 12);
            Put32(d, 16, 12);
            Encoding.ASCII.GetBytes("1.22", 0, 4, d, 20);
            Encoding.ASCII.GetBytes("DTBL", 0, 4, d, 24);
            Put32(d, 28, 0x1c);
            int o = 24 + 0x1c;
            foreach (var c in chunks) { Buffer.BlockCopy(c, 0, d, o, c.Length); o += c.Length; }
            return d;
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
