using System;

namespace MW2.Shell
{
    /// <summary>
    /// The MechLab check of a variant (FUN_10044190 0x10044190, called by FUN_100189b3 0x100189b3 on the MEK it has just
    /// written as mek\TMP.MEK): null when the MEK is legal, else the message (FUN_100189b3 shows it with "#Ok"). It works on
    /// the file (header tons, walk, jump, heat sinks, weapon and ammunition counts; eight sections of 0x28 bytes from 0x18:
    /// front and rear armour at +0 / +4, twelve critical shorts at +0xc, their count at +0x24; the weapon pairs from 0x158,
    /// the ammunition pairs, then the name block whose +0x22 is the double heat sink flag). The tables are built each call:
    /// names (0x100d09c0, "???" by default, "ammo for %s" from 200), mass in 1/100 t (0x100d0488) and slots (0x100cfe50) from
    /// the weapon table 0x100a96f8 (mass at +0xc, 0x100a9704; index 2 × weapon) and the equipment table 0x100a9050 (code, mass,
    /// slots, minimum), all from the extracted ShellLabTables (shell-mechlab.json). The
    /// section armour is read as floats exactly as the original reads it. The fixes it makes when DAT_100d0ffc is 1 (mandatory
    /// slots refilled, endo and ferro instance numbers, the heat sink flag and count) only touch the loaded copy;
    /// DAT_100d0ff8 is never set, so the "didn't count" checks are off. Technology (+0x9cf): 0 Clan, otherwise Inner Sphere.
    /// </summary>
    public static class MechLabCheck
    {
        const int Codes = 0x14d;

        /// <summary>FUN_100455d7 0x100455d7: the code of an older MEK's critical in this version, -1 none.</summary>
        static int Upgrade(int code)
        {
            int b = code - code % 0x32;
            if (b == 0x157c || b == 0x14b4 || b == 0x1518 || b == 0x14e6 || b == 0x15e0 || b == 0x15ae || b == 0x154a || b == 0x1612)
                return code + 2000;
            if (code >= 5000 && code <= 5999 && code % 0x32 == 0) return code + 2000;
            if (code == 6000) return 0x1d4c;
            if (code == 7000) return 8000;
            if (code == 8000) return 0x2134;
            return -1;
        }

        public static string Check(byte[] file, int tons, string chassis, int technology, ShellTables t)
        {
            var lab = t.Lab;
            var msg = lab.Messages;
            if (string.Equals(chassis, msg.ExemptChassis, StringComparison.OrdinalIgnoreCase)) return msg.ExemptChassisRefused;
            var m = (byte[])file.Clone();
            int I(int o) => BitConverter.ToInt32(m, o);
            void SetI(int o, int v) => Buffer.BlockCopy(BitConverter.GetBytes(v), 0, m, o, 4);
            int U16(int o) => BitConverter.ToUInt16(m, o);
            void SetU16(int o, int v) => Buffer.BlockCopy(BitConverter.GetBytes((ushort)v), 0, m, o, 2);
            float F(int o) => BitConverter.ToSingle(m, o);
            string Msg(string format, string arg1, string arg2 = null)
            {
                string s = format;
                foreach (var a in new[] { arg1, arg2 })
                {
                    int k = s.IndexOf("%s", StringComparison.Ordinal);
                    if (a != null && k >= 0) s = s.Substring(0, k) + a + s.Substring(k + 2);
                }
                return s;
            }

            var name = new string[Codes];
            int[] mass = new int[Codes], slots = new int[Codes], count = new int[Codes], weapons = new int[Codes];
            for (int i = 0; i < Codes; i++) name[i] = msg.DefaultName;
            foreach (var n in lab.Names) name[n.Index] = n.Name;
            for (int i = 0; i < 0x85; i++)
            {
                name[200 + i] = Msg(msg.AmmoFor, name[i]);
                mass[200 + i] = 100;
                slots[200 + i] = 1;
            }
            // The weapon table 0x100a96f8 up to its null name pointer, then the equipment table 0x100a9050 up to code -1.
            foreach (var w in lab.Weapons)
            {
                slots[w.Weapon * 2] = w.Slots;
                mass[w.Weapon * 2] = w.Mass;
            }
            foreach (var e in lab.Equipment)
            {
                int c = e.Code / 0x32;
                mass[c] = e.Mass;
                slots[c] = e.Slots;
            }

            int nWeapons = I(0x10), nAmmo = I(0x14);
            int trailer = 0x158 + nWeapons * 8 + nAmmo * 8;
            if (I(0xc) < 0 || I(8) < 0) return msg.NegativeCount;
            if (tons != -0x29a && I(0) != tons) return msg.WrongChassisMass;
            int mechTons = I(0);
            if (mechTons % 5 != 0 || mechTons < 10 || mechTons > 100) return msg.IllegalTonnage;
            var structure = lab.StructureFor(mechTons); // 0x100a91e8 + tons / 5 x 0x10
            int v = mechTons < 0x5a ? 100 : 200;
            if (mechTons < 0x3c) v /= 2;
            mass[170] = v;
            slots[170] = 1;
            if (technology == 0) v = (mechTons * 100 / 0x19 + 0x32) / 100;
            else if (technology == 1) v = (mechTons * 100 / 0x14 + 0x32) / 100;
            mass[140] = v * 100;
            slots[140] = v;

            bool doubleSinks = false;
            for (int s = 0; s < 8; s++)
            {
                int sec = 0x18 + s * 0x28, n = (short)U16(sec + 0x24);
                if (n != 6 && n != 12) return msg.WrongSlotCount;
                for (int j = 0; j < n; j++)
                {
                    int crit = U16(sec + 0xc + j * 2), c = crit / 0x32;
                    if (!doubleSinks && c == 0xa0)
                        for (int k = 0; k < n; k++)
                            if (k != j && crit == U16(sec + 0xc + k * 2)) doubleSinks = true;
                    uint template = (uint)lab.Templates[s].Slots[j]; // 0x100a8ed0 + s x 0x30 + j x 4
                    if (template == 0xffffffff || (template & 0x80000000) == 0) continue;
                    int tc = (int)((template & 0x7fffffff) / 0x32);
                    if (tc == c) continue;
                    int up = Upgrade(crit);
                    if (up / 0x32 != tc)
                    {
                        if (c < 0x84 && (c & 1) != 0) return msg.OlderVersion;
                        if (crit != 0) return Msg(msg.MissingMandatory, name[tc], lab.SectionNames[s]);
                        SetU16(sec + 0xc + j * 2, (int)(template & 0x7fffffff));
                    }
                    else SetU16(sec + 0xc + j * 2, up);
                }
            }

            int armor = 0;
            for (int s = 0; s < 8; s++)
            {
                int sec = 0x18 + s * 0x28;
                int a = (int)(F(sec) + F(sec + 4));
                int limit = s == 0 ? 9 : s == 1 || s == 3 ? structure.SideTorso << 1 : s == 2 ? structure.CenterTorso << 1 :
                            s == 4 || s == 5 ? structure.Arm << 1 : structure.Leg << 1;
                if (limit < a) return msg.TooMuchArmor;
                if ((s < 1 || s > 3) && F(sec + 4) != 0f) return msg.RearArmor;
                armor += a;
                int used = 0, n = (short)U16(sec + 0x24);
                for (int j = 0; j < n; j++)
                {
                    int crit = U16(sec + 0xc + j * 2);
                    if (crit == 0) continue;
                    used++;
                    int c = crit / 0x32;
                    if (c > 0x14c) return msg.UnknownCodes;
                    if (name[c].StartsWith(msg.UnknownPrefix, StringComparison.Ordinal)) return msg.UnknownEquipment;
                    if (c == 0xb4 && crit != 9000) SetU16(sec + 0xc + j * 2, 9000);
                    if (c == 0xbe && crit != 0x251c) SetU16(sec + 0xc + j * 2, 0x251c);
                    count[c]++;
                }
                if (used > 6 && (s == 6 || s == 7 || s == 0)) return msg.TooManyHeadLegSlots;
            }

            bool clan = technology == 0;
            bool xl, ferro, endo;
            if (count[157] == 6) xl = false;
            else if (count[157] == 10 && clan || count[157] == 12 && !clan) xl = true;
            else return msg.EngineSlots;
            if (count[190] == 0) ferro = false;
            else if (count[190] == 7 && clan || count[190] == 14 && !clan) ferro = true;
            else return msg.FerroSlots;
            if (count[180] == 0) endo = false;
            else if (count[180] == 7 && clan || count[180] == 14 && !clan) endo = true;
            else return msg.EndoSlots;

            int armorMass = !ferro ? armor * 100 / 16 : clan ? armor * 1000 / 0xc0 : armor * 1000 / 0xb3;
            int rating = mechTons * I(4);
            if (rating > 400) return msg.EngineRatingTooHigh;
            int row = Math.Max(rating / 5 - 2, 0);
            int engine = lab.Engines[row].Mass; // 0x100a933c + row x 0xc
            if (xl) engine /= 2;
            int internalMass = !endo ? mechTons * 100 / 10 : mechTons * 100 / 0x14;
            int total = (armorMass + 0x19) / 0x32 * 0x32 + (engine + 0x19) / 0x32 * 0x32 + (rating + 99) / 100 * 100 + internalMass;

            int flag = I(trailer + 0x22);
            if (count[160] == 0 && flag != 0) doubleSinks = clan;
            if ((doubleSinks ? 1 : 0) != flag) SetI(trailer + 0x22, doubleSinks ? 1 : 0);
            mass[160] = 100;
            slots[160] = !doubleSinks ? 1 : clan ? 2 : 3;
            int sinks = count[160] / slots[160];
            if (sinks * slots[160] != count[160]) return msg.MixedHeatSinks;
            for (int i = 0; i < Codes; i++)
                if (count[i] > 0 && slots[i] != 0) total += mass[i] * (count[i] / slots[i]);
            total += 0x19;
            if (mechTons * 2 < total / 0x32) return msg.Overweight;

            for (int i = 0; i < nWeapons; i++)
            {
                int c = I(0x158 + i * 8) / 0x32;
                if (c < 0 || c > 0x14c) return msg.UnknownWeapon;
                weapons[c]++;
            }
            for (int i = 0; i < 0x84; i++)
                if (slots[i] * weapons[i] != count[i] && count[i] < slots[i] * weapons[i]) return Msg(msg.IllegalWeaponCount, name[i]);
            int ammo = 0;
            for (int i = 0; i < 0x84; i++) ammo += count[200 + i];
            if (ammo != nAmmo) return msg.IllegalAmmo;
            int heatSinks = sinks + 10;
            if (doubleSinks) heatSinks *= 2;
            if (heatSinks != I(0xc)) SetI(0xc, heatSinks);
            if (count[170] != I(8) && count[170] < I(8)) return msg.IllegalJumpJets;
            if (I(4) < I(8)) return msg.JumpOverWalk;
            foreach (var e in lab.Equipment)
            {
                int c = e.Code / 0x32;
                if (count[c] < e.Minimum) return Msg(msg.MissingComponents, name[c]);
            }
            int from = clan ? 0x46 : 0, to = clan ? 0x84 : 0x46;
            for (int i = from; i < to; i++) if (count[i] != 0) return Msg(msg.WeaponDenied, name[i]);
            for (int i = from + 200; i < to + 200; i++) if (count[i] != 0) return Msg(msg.AmmoDenied, name[i]);
            return null;
        }
    }
}
