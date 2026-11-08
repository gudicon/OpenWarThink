using System;
using System.IO;
using System.Text;

namespace MW2.Shell
{
    /// <summary>MW2SHELL.DLL FUN_1004b27e 0x1004b27e / FUN_1004b33c 0x1004b33c:
    /// the 0x344-byte mw2prm.cfg exchange block. FUN_10074483 copies two 0x114-byte rosters;
    /// FUN_10074511 lays out each 0x40-byte slot.</summary>
    public sealed class ShellLaunchConfig
    {
        public const int Size = 0x344;
        public readonly byte[] Data;
        public ShellLaunchConfig(byte[] data)
        {
            if (data == null || data.Length != Size) throw new ArgumentException("mw2prm.cfg must be 0x344 bytes", nameof(data));
            Data = (byte[])data.Clone();
        }

        public int Message => BitConverter.ToInt32(Data, 0);
        public int CurrentRoster => BitConverter.ToInt32(Data, 0xc);
        public string Command => ReadString(0x240, 0x104);

        string ReadString(int start, int length)
        {
            int end = start;
            while (end < start + length && Data[end] != 0) end++;
            return Encoding.ASCII.GetString(Data, start, end - start);
        }

        public int RosterField(int roster, int field)
        {
            if (roster < 0 || roster > 1 || field < 0 || field > 4) throw new ArgumentOutOfRangeException();
            return BitConverter.ToInt32(Data, 0x10 + roster * 0x114 + field * 4);
        }

        public (int modelIndex, string mekName, string pilotName) Slot(int roster, int slot)
        {
            if (roster < 0 || roster > 1 || slot < 0 || slot > 3) throw new ArgumentOutOfRangeException();
            int p = 0x10 + roster * 0x114 + 0x14 + slot * 0x40;
            return (BitConverter.ToInt32(Data, p), ReadString(p + 4, 0x10), ReadString(p + 0x14, 0x16));
        }
    }

    /// <summary>mercsw.dll FUN_100424dd 0x100424dd writes MW2MSN.CFG; MW2SHELL.DLL FUN_1001c67e
    /// 0x1001c67e reads its 0x9d4-byte report into career state +0x243b.</summary>
    public sealed class ShellMissionReport
    {
        public const int Size = 0x9d4;
        public const int MaxObjectives = (Size - 0x14) / 0x34;
        public readonly byte[] Data;

        public ShellMissionReport(byte[] data)
        {
            if (data == null || data.Length != Size) throw new ArgumentException("MW2MSN.CFG must be 0x9d4 bytes", nameof(data));
            if (BitConverter.ToUInt32(data, 0) != 0x4d32574d) throw new ArgumentException("MW2MSN.CFG magic differs", nameof(data));
            if (BitConverter.ToInt32(data, 4) < 0 || BitConverter.ToInt32(data, 4) > MaxObjectives)
                throw new ArgumentException("MW2MSN.CFG objective count is outside the record", nameof(data));
            Data = (byte[])data.Clone();
        }

        public int Count => BitConverter.ToInt32(Data, 4);
        public int TeamField4 => BitConverter.ToInt32(Data, 8);
        public int TeamField8 => BitConverter.ToInt32(Data, 12);
        public int TeamByte38 => Data[0x10];

        /// <summary>FUN_100424dd: state 5 flag, +0x3b byte, +0xa1/+0xa5/+0xaf dwords and +0xe7 text.</summary>
        public (bool complete, byte field3b, int fieldA1, int fieldA5, int fieldAf, string text) Objective(int index)
        {
            if (index < 0 || index >= Count) throw new ArgumentOutOfRangeException(nameof(index));
            int p = 0x14 + index * 0x34, end = p + 0x34;
            int t = p + 0x14;
            while (t < end && Data[t] != 0) t++;
            return (BitConverter.ToInt32(Data, p) != 0, Data[p + 4], BitConverter.ToInt32(Data, p + 8),
                BitConverter.ToInt32(Data, p + 12), BitConverter.ToInt32(Data, p + 16),
                Encoding.ASCII.GetString(Data, p + 0x14, t - p - 0x14));
        }
    }

    /// <summary>A fixed-size record of the shell kept byte for byte as the original stores it (so SHELLMER.CFG stays compatible).</summary>
    public abstract class ShellRecord
    {
        public readonly byte[] Data;
        protected ShellRecord(int size) { Data = new byte[size]; }
        protected ShellRecord(byte[] data) { Data = data; }

        public int I32(int o) => BitConverter.ToInt32(Data, o);
        public void SetI32(int o, int v) { Data[o] = (byte)v; Data[o + 1] = (byte)(v >> 8); Data[o + 2] = (byte)(v >> 16); Data[o + 3] = (byte)(v >> 24); }
        public short I16(int o) => BitConverter.ToInt16(Data, o);
        public void SetI16(int o, int v) { Data[o] = (byte)v; Data[o + 1] = (byte)(v >> 8); }

        public string Str(int o, int max)
        {
            int e = o;
            while (e < o + max && e < Data.Length && Data[e] != 0) e++;
            return Encoding.ASCII.GetString(Data, o, e - o);
        }

        /// <summary>strcpy: the string and its terminating zero (bytes after it keep their values).</summary>
        public void SetStr(int o, string s)
        {
            var b = Encoding.ASCII.GetBytes(s ?? "");
            Buffer.BlockCopy(b, 0, Data, o, b.Length);
            Data[o + b.Length] = 0;
        }
    }

    /// <summary>
    /// Mech record (0x9d3 bytes, mechshit.cpp). +0 display name, +0x100 MEK name block, +0x200 tons x 100, +0x204 used mass,
    /// +0x208 free mass, +0x20c engine index (+10000 XL), +0x210 rating, +0x214 engine mass, +0x218 XL slots, +0x21c,
    /// +0x220 gyro, +0x224 cockpit (300), +0x228 heat sink type (0 single, 1 double), +0x22c heat sink mass, +0x230 heat sinks
    /// in criticals, +0x234 mass per jump jet, +0x238 jump jet mass, +0x240 endo steel, +0x244 internal structure mass,
    /// +0x24c ferro-fibrous, +0x250 armour mass, +0x258 armour points allowed, +0x25c armour points, +0x260 weapon mass,
    /// +0x268 ammunition mass, +0x270 MASC mass, +0x278 CASE mass, +0x280 walk MP, +0x284 run MP, +0x288 jump MP,
    /// +0x28c 25 ammunition bins, +0x2f0 14 weapons (id, value), +0x368 criticals 8 sections x 12, +0x670 78 loose items,
    /// +0x8e0 8 sections x 0x12 (internal, maximum armour, front armour, rear armour or -1, flags), +0x974 MASC flag,
    /// +0x988 MASC slots, +0x98c pilot id, +0x990 mech id, +0x994, +0x998, +0x99c code (3), +0x9a3 MEK file name,
    /// +0x9c3 tech year, +0x9c7 salvage chance, +0x9cb custom MEK written (TMP), +0x9ce salvaged, +0x9cf technology.
    /// Sections: 0 head, 1 left torso, 2 centre torso, 3 right torso, 4 left arm, 5 right arm, 6 left leg, 7 right leg.
    /// </summary>
    public sealed class MechRecord : ShellRecord
    {
        public const int Size = 0x9d3;
        public MechRecord() : base(Size) { }
        public MechRecord(byte[] data) : base(data) { }

        public string Name => Str(0, 0x100);
        public string Code => Str(0x99c, 3);
        public string MekName { get => Str(0x9a3, 0x20); set => SetStr(0x9a3, value); }
        public int Tons100 => I32(0x200);
        public int PilotId { get => I32(0x98c); set => SetI32(0x98c, value); }
        public int MechId { get => I32(0x990); set => SetI32(0x990, value); }
        public int Field998 { get => I32(0x998); set => SetI32(0x998, value); }
        public int TechYear { get => I32(0x9c3); set => SetI32(0x9c3, value); }
        public int SalvageChance { get => I32(0x9c7); set => SetI32(0x9c7, value); }
        public bool Custom { get => Data[0x9cb] != 0; set => Data[0x9cb] = (byte)(value ? 1 : 0); }
        /// <summary>Mech_IsDestroyed 0x1004a391: the head (+0x8e0) or the centre torso (+0x904) has no internal structure,
        /// front or rear armour left.</summary>
        public bool Destroyed => I32(0x8e0) == 0 && I32(0x8e8) == 0 && I32(0x8ec) == 0 ||
                                 I32(0x904) == 0 && I32(0x90c) == 0 && I32(0x910) == 0;
        public int Technology => I32(0x9cf);
        public int Crit(int section, int slot) => I32(0x368 + section * 0x30 + slot * 4);
        public void SetCrit(int section, int slot, int v) => SetI32(0x368 + section * 0x30 + slot * 4, v);

        /// <summary>FUN_10048d9e 0x10048d9e: a copy of the record.</summary>
        public MechRecord Clone() => new MechRecord((byte[])Data.Clone());

        /// <summary>FUN_10048514 0x10048514: serialize the active mech configuration for launch.
        /// Header, eight sections, compacted weapons, ammunition and 0x3a-byte name block.</summary>
        public byte[] ToMek()
        {
            var weapons = new System.Collections.Generic.List<(int id, int value)>();
            for (int i = 0; i < 14; i++)
            {
                int id = I32(0x2f0 + i * 8);
                if (id == -1) continue;
                bool duplicate = false;
                foreach (var w in weapons) if (w.id == id) { duplicate = true; break; }
                if (!duplicate) weapons.Add((id, I32(0x2f4 + i * 8)));
            }
            var ammo = new System.Collections.Generic.List<(int id, int type)>();
            for (int i = 0; i < 25 && I32(0x28c + i * 4) != -1; i++)
            {
                int raw = I32(0x28c + i * 4);
                int id = (raw - 10000) / 10000 * 100 + 10001;
                foreach (var existing in ammo) if (existing.id == id) id++;
                ammo.Add((id, (raw - 10000) / 100));
            }
            int ammoStart = 0x18 + 0x140 + weapons.Count * 8;
            int nameStart = ammoStart + ammo.Count * 8;
            var mek = new byte[nameStart + 0x3a];
            void Put32(int o, int v) => Buffer.BlockCopy(BitConverter.GetBytes(v), 0, mek, o, 4);
            void Put16(int o, int v) => Buffer.BlockCopy(BitConverter.GetBytes((short)v), 0, mek, o, 2);
            Put32(0, (Tons100 + 50) / 100);
            Put32(4, I32(0x280)); Put32(8, I32(0x288));
            Put32(12, ((I32(0x22c) + 50) / 100 + 10) * (I32(0x228) + 1));
            Put32(16, weapons.Count); Put32(20, ammo.Count);
            for (int section = 0; section < 8; section++)
            {
                int dst = 0x18 + section * 0x28, src = 0x8e0 + section * 0x12;
                Put32(dst, I32(src + 8));
                if (section >= 1 && section <= 3) Put32(dst + 4, I32(src + 12));
                Put32(dst + 8, I16(src));
                Put16(dst + 0x24, section == 0 || section >= 6 ? 6 : 12);
                Put16(dst + 0x26, (ushort)I16(src + 0x10) | (Technology == 0 ? 0x100 : 0));
                for (int slot = 0; slot < 12; slot++)
                {
                    int crit = Crit(section, slot), value = 0;
                    if (crit >= 0 && crit < 10000) value = crit;
                    else if (crit >= 10000)
                        for (int i = 0; i < ammo.Count; i++)
                            if (I32(0x28c + i * 4) == crit) { value = ammo[i].id; break; }
                    Put16(dst + 0xc + slot * 2, value);
                }
            }
            for (int i = 0; i < weapons.Count; i++)
            {
                Put32(0x158 + i * 8, weapons[i].id);
                Put32(0x15c + i * 8, weapons[i].value);
            }
            for (int i = 0; i < ammo.Count; i++)
            {
                Put32(ammoStart + i * 8, ammo[i].id);
                Put32(ammoStart + i * 8 + 4, ammo[i].type);
            }
            // FUN_10048514 clears the whole block, then strcpy copies only through the first zero.
            int nameLength = 0;
            while (nameLength < 0x3a - 1 && Data[0x100 + nameLength] != 0) nameLength++;
            Buffer.BlockCopy(Data, 0x100, mek, nameStart, nameLength);
            Put32(nameStart + 0x22, Math.Max(0, I32(0x228)));
            return mek;
        }

        /// <summary>FUN_10046031 0x10046031: "xxx00std" names come from MW2.PRJ (MEK), others from the game's mek folder.</summary>
        public static byte[] LoadMek(string name, Func<string, byte[]> prjMek, string gameFolder)
        {
            if (name.Length >= 8 && string.Compare(name, 5, "std", 0, 3, StringComparison.OrdinalIgnoreCase) == 0) return prjMek(name);
            string path = Path.Combine(gameFolder ?? "", "mek", name + (name.Contains(".") ? "" : ".mek"));
            if (!File.Exists(path)) return null;
            var d = new byte[0x800];
            using (var f = File.OpenRead(path)) f.Read(d, 0, d.Length);
            return d;
        }

        /// <summary>
        /// FUN_10047700 0x10047700: a mech record from a MEK (header 0x18: tons, walk MP, jump MP, heat sinks, weapon count, ammo
        /// count; 8 sections x 0x28: front armour, rear armour, internal, 12 critical shorts, slot count, flags; weapons 8 bytes;
        /// ammunition 8 bytes (critical id, type); a 0x3a-byte name block whose int +0x22 is the heat sink type).
        /// nextId is FUN_10048d73 (state +0x15e counted up). Null when the MEK or the mech code is unknown.
        /// </summary>
        public static MechRecord FromMek(string name, byte[] mek, ShellTables t, Func<int> nextId)
        {
            if (mek == null) return null;
            int tons = BitConverter.ToInt32(mek, 0), walk = BitConverter.ToInt32(mek, 4), jump = BitConverter.ToInt32(mek, 8);
            int heatSinks = BitConverter.ToInt32(mek, 0xc), weaponCount = BitConverter.ToInt32(mek, 0x10), ammoCount = BitConverter.ToInt32(mek, 0x14);
            const int sec = 0x18;
            int p = sec + 0x140;
            int weapons = p;
            p += weaponCount * 8;
            int ammo = p;
            p += ammoCount * 8;
            int nameBlock = p;
            int S32(int section, int o) => BitConverter.ToInt32(mek, sec + section * 0x28 + o);
            int CritCount(int section) => BitConverter.ToInt16(mek, sec + section * 0x28 + 0x24);
            int MekCrit(int section, int j) => BitConverter.ToUInt16(mek, sec + section * 0x28 + 0xc + j * 2);
            int AmmoCrit(int k) => k < Math.Min(ammoCount, 0x19) ? BitConverter.ToInt32(mek, ammo + k * 8) : 0;
            int AmmoType(int k) => BitConverter.ToInt32(mek, ammo + k * 8 + 4);
            // FUN_10048452 / FUN_100484af: an item in the MEK's critical lists (one section / any).
            bool InSection(int item, int section, out int slot)
            {
                for (slot = 0; slot < CritCount(section); slot++) if (MekCrit(section, slot) == item) return true;
                return false;
            }
            bool Anywhere(int item, out int section, out int slot)
            {
                for (section = 0; section < 8; section++) if (InSection(item, section, out slot)) return true;
                slot = 0;
                return false;
            }

            var r = new MechRecord();
            r.SetStr(0x9a3, name);
            var code = Encoding.ASCII.GetBytes(name.Length >= 3 ? name.Substring(0, 3) : name);
            Buffer.BlockCopy(code, 0, r.Data, 0x99c, code.Length);
            int mi = t.FindMech(r.Code);
            if (mi < 0) return null;
            r.SetStr(0, t.MechName(mi));
            for (int k = 0; k < 0x4e; k++) r.SetI32(0x670 + k * 8, -1);
            for (int k = 0; k < 0xe; k++)
            {
                if (k < weaponCount)
                {
                    r.SetI32(0x2f0 + k * 8, BitConverter.ToInt32(mek, weapons + k * 8));
                    r.SetI32(0x2f4 + k * 8, BitConverter.ToInt32(mek, weapons + k * 8 + 4));
                }
                else r.SetI32(0x2f0 + k * 8, -1);
            }
            for (int k = 0; k < 0x19; k++) r.SetI32(0x28c + k * 4, -1);
            for (int k = 0; k < ammoCount; k++)
            {
                r.AddAmmoBin(AmmoType(k) * 100 + 0x2711);
                r.SetI32(0x268, r.I32(0x268) + 100);
            }
            for (int s = 0; s < 8; s++)
                for (int j = 0; j < 0xc; j++)
                {
                    if (j < CritCount(s))
                    {
                        int v = MekCrit(s, j);
                        if (v > 9999)
                            for (int k = 0; k < 0x19; k++)
                                if (AmmoCrit(k) == v) { v = r.I32(0x28c + k * 4); break; }
                        r.SetCrit(s, j, v);
                    }
                    else r.SetCrit(s, j, -1);
                }
            r.SetI32(0x994, 0);
            int nameEnd = nameBlock;
            while (nameEnd < nameBlock + 0x3a && nameEnd < mek.Length && mek[nameEnd] != 0) nameEnd++;
            r.SetStr(0x100, Encoding.ASCII.GetString(mek, nameBlock, nameEnd - nameBlock));
            r.SetI32(0x200, tons * 100);
            r.SetI32(0x280, walk);
            r.SetI32(0x288, jump);
            int rating = (tons * walk * 5 + 4) / 5;
            if (rating < 10) rating = 10;
            if (rating > 400) rating = 400;
            r.SetI32(0x210, rating);
            r.SetI32(0x20c, (rating - 10) / 5);
            r.SetI32(0x9cf, t.Mech(mi).Technology);
            r.SetI32(0x22c, (heatSinks - 10) * 100);
            if (InSection(0x1eaa, 1, out _)) r.SetI32(0x20c, r.I32(0x20c) + 10000);
            if (Anywhere(9000, out _, out _) || Anywhere(0x2329, out _, out _)) r.SetI32(0x240, 1);
            if (Anywhere(0x251c, out _, out _) || Anywhere(0x251d, out _, out _)) r.SetI32(0x24c, 1);
            int points = 0;
            for (int s = 0; s < 8; s++) points += S32(s, 0) + S32(s, 4);
            r.SetI32(0x258, points);
            // ftol truncates: ferro-fibrous points / 19.2 x 100, standard / 16 x 100; then rounded to half tons (FUN_1004630c).
            r.SetI32(0x250, r.I32(0x24c) == 1 ? (int)(points / 19.2 * 100.0) : (int)(points / 16.0 * 100.0));
            r.SetI32(0x250, Round100(r.I32(0x250) << 1) / 2);
            r.SetI32(0x25c, points);
            int ti = (r.Tons100 / 100 - 10) / 5;
            r.SetI32(0x8e0, 3);
            r.SetI32(0x8f2, t.Structure(ti, 1)); r.SetI32(0x904, t.Structure(ti, 0)); r.SetI32(0x916, t.Structure(ti, 1));
            r.SetI32(0x928, t.Structure(ti, 2)); r.SetI32(0x93a, t.Structure(ti, 2));
            r.SetI32(0x94c, t.Structure(ti, 3)); r.SetI32(0x95e, t.Structure(ti, 3));
            r.SetI32(0x8e4, 9);
            for (int s = 1; s < 8; s++) r.SetI32(0x8e0 + s * 0x12 + 4, r.I32(0x8e0 + s * 0x12) << 1);
            for (int s = 0; s < 8; s++)
            {
                r.SetI32(0x8e0 + s * 0x12, S32(s, 8));
                r.SetI32(0x8e8 + s * 0x12, S32(s, 0));
                r.SetI32(0x8ec + s * 0x12, s >= 1 && s <= 3 ? S32(s, 4) : -1);
                r.SetI16(0x8f0 + s * 0x12, BitConverter.ToInt16(mek, sec + s * 0x28 + 0x26));
            }
            r.SetI32(0x364, 0);
            for (int o = 0x974; o < 0x988; o++) r.Data[o] = 0;
            if (r.Technology == 0) r.SetI32(0x988, (r.Tons100 / 0x19 + 0x32) / 100);
            else if (r.Technology == 1) r.SetI32(0x988, (r.Tons100 / 0x14 + 0x32) / 100);
            if (Anywhere(7000, out _, out _))
            {
                r.SetI32(0x270, r.I32(0x988) * 100);
                r.SetI32(0x974, 1);
            }
            r.Engine(t);
            r.Movement();
            r.WeaponMass(t);
            r.AmmoMass();
            r.Totals();
            r.SetI32(0x228, 0);
            if (Anywhere(0x1f41, out int hs, out int hslot))
            {
                // The first double heat sink is masked (0xffff) while a second one is looked for, then put back.
                bool second = false;
                for (int s = 0; s < 8 && !second; s++)
                    for (int j = 0; j < CritCount(s); j++)
                        if (MekCrit(s, j) == 0x1f41 && !(s == hs && j == hslot)) { second = true; break; }
                r.SetI32(0x228, second ? 2 : 1);
            }
            if (r.I32(0x228) == 0)
            {
                if (r.Tons100 < r.I32(0x204) && heatSinks > 0x13 && (heatSinks & 1) == 0) r.SetI32(0x228, 2);
                else
                {
                    r.SetI32(0x228, BitConverter.ToInt32(mek, nameBlock + 0x22) + 1);
                    if (r.I32(0x228) != 1 && r.I32(0x228) != 2) r.SetI32(0x228, 2);
                }
            }
            if (r.I32(0x228) == 2)
            {
                r.SetI32(0x22c, r.I32(0x22c) / 2 - 500);
                r.Totals();
            }
            r.SetI32(0x228, r.I32(0x228) - 1);
            if (r.I32(0x228) < 0) r.SetI32(0x228, 0);
            r.SetI32(0x230, -1);
            r.HeatSinks();
            r.Totals();
            r.PilotId = -1;
            r.MechId = nextId();
            return r;
        }

        /// <summary>FUN_100462f7(x + 0x32): to the nearest multiple of 100 (FUN_1004630c).</summary>
        public static int Round100(int x) { x += 0x32; return x - x % 100; }
        /// <summary>FUN_100462c2: up to a multiple of 100.</summary>
        public static int Ceil100(int x) => x % 100 == 0 ? x : x + 100 - x % 100;

        /// <summary>FUN_1004839b 0x1004839b: a new ammunition bin id (type x 100 + 1.., up to 20) in the first free bin.</summary>
        public int AddAmmoBin(int v)
        {
            v = v - v % 100 + 1;
            for (int k = 0; k < 0x19; k++)
                if (I32(0x28c + k * 4) == v) { v++; k = -1; }
            if (v % 100 < 0x15)
                for (int k = 0; k < 0x19; k++)
                    if (I32(0x28c + k * 4) == -1) { SetI32(0x28c + k * 4, v); return v; }
            return 0;
        }

        /// <summary>
        /// FUN_10048fa8 0x10048fa8: take the state of a mech rebuilt from the MEK the engine wrote after the mission: the 25
        /// ammunition bins +0x28c, the 14 weapons +0x2f0, the criticals +0x368, the eight section blocks +0x8e0 (0x12 bytes
        /// each) and the masses and movement +0x200..+0x208 / +0x220..+0x27c, then FUN_10046c10, FUN_10046d82, FUN_10046e58,
        /// FUN_10047103 and FUN_10046e3b.
        /// </summary>
        public void MergeMissionDamage(MechRecord from, ShellTables t)
        {
            if (from == null) return;
            Buffer.BlockCopy(from.Data, 0x28c, Data, 0x28c, 0x19 * 4);
            Buffer.BlockCopy(from.Data, 0x2f0, Data, 0x2f0, 0xe * 8);
            Buffer.BlockCopy(from.Data, 0x368, Data, 0x368, 8 * 0x30);
            Buffer.BlockCopy(from.Data, 0x8e0, Data, 0x8e0, 8 * 0x12);
            Buffer.BlockCopy(from.Data, 0x200, Data, 0x200, 0xc);
            Buffer.BlockCopy(from.Data, 0x220, Data, 0x220, 0x280 - 0x220);
            Engine(t);
            Movement();
            WeaponMass(t);
            AmmoMass();
            Totals();
        }

        /// <summary>FUN_10046c10 0x10046c10: rating and engine mass from the index (XL halves it), rounded to half tons.</summary>
        public void Engine(ShellTables t)
        {
            int idx = I32(0x20c) % 10000;
            SetI32(0x210, t.EngineRating(idx));
            SetI32(0x214, t.EngineMass(idx));
            if (I32(0x20c) > 9999) SetI32(0x214, I32(0x214) / 2);
            int m = I32(0x214), rest = (m / 10 % 10) * 10 + m % 10;
            if (rest < 0x19) m -= rest;
            else if (rest < 0x4b) m += 0x32 - rest;
            else m += 100 - rest;
            SetI32(0x214, m);
            SetI32(0x218, I32(0x20c) < 10000 ? 0 : 7);
            SetI32(0x21c, I32(0x200) - I32(0x214));
        }

        /// <summary>FUN_10046d82 0x10046d82: movement, jump jets above walking MP dropped (the mech lab copy FUN_10046320 too),
        /// jump jet mass (0.5 t below 55 t, 1 t below 85 t, else 2 t).</summary>
        public void Movement()
        {
            Mp();
            while (I32(0x280) < I32(0x288)) SetI32(0x288, I32(0x288) - 1);
            SetI32(0x234, I32(0x200) < 0x157d ? 0x32 : I32(0x200) < 0x2135 ? 100 : 200);
            SetI32(0x238, I32(0x234) * I32(0x288));
        }

        /// <summary>FUN_10046aa1 0x10046aa1: walk = rating x 100 / tons, run = (walk x 3 + 2) / 2.</summary>
        public void Mp()
        {
            if (I32(0x200) == 0) SetI32(0x200, 1);
            SetI32(0x280, I32(0x210) * 100 / I32(0x200));
            SetI32(0x284, (I32(0x280) * 3 + 2) / 2);
        }

        /// <summary>FUN_10046e58 0x10046e58: weapon mass from the weapon table.</summary>
        public void WeaponMass(ShellTables t)
        {
            SetI32(0x260, 0);
            for (int k = 0; k < 0xe; k++)
                if (I32(0x2f0 + k * 8) != -1) SetI32(0x260, I32(0x260) + t.Weapon(I32(0x2f0 + k * 8) / 100).Mass);
        }

        /// <summary>FUN_10047103 0x10047103: 1 t per ammunition bin.</summary>
        public void AmmoMass()
        {
            SetI32(0x268, 0);
            for (int k = 0; k < 0x19; k++) if (I32(0x28c + k * 4) != -1) SetI32(0x268, I32(0x268) + 100);
        }

        /// <summary>FUN_10046e3b 0x10046e3b: FUN_10046928 (CASE, gyro, structure, cockpit, used / free mass) and FUN_10046aa1.</summary>
        public void Totals()
        {
            SetI32(0x278, 0);
            if (Technology == 1)
                for (int s = 0; s < 8; s++)
                    for (int j = 0; j < 0xc; j++)
                        if (Crit(s, j) / 10 == 0x3bb) SetI32(0x278, I32(0x278) + 0x32);
            SetI32(0x220, Ceil100((int)(I32(0x210) / 100.0 * 100.0)));
            SetI32(0x244, I32(0x200) / (I32(0x240) != 0 ? 20 : 10));
            SetI32(0x224, 300);
            SetI32(0x204, I32(0x214) + I32(0x220) + I32(0x224) + I32(0x22c) + I32(0x238) + I32(0x244) + I32(0x250) + I32(0x260) +
                          I32(0x268) + I32(0x270) + I32(0x278));
            SetI32(0x208, I32(0x200) - I32(0x204));
            Mp();
        }

        /// <summary>FUN_10046afb 0x10046afb: heat sinks counted in the criticals (items 8000..8049), one per 1 / 2 (Clan
        /// double) / 3 (Inner Sphere double) slots; their mass 1 t each.</summary>
        public void HeatSinks()
        {
            int n = 0;
            for (int s = 0; s < 8; s++)
                for (int j = 0; j < 0xc; j++)
                    if (Crit(s, j) / 0x32 == 0xa0) n++;
            SetI32(0x230, n);
            int per = I32(0x228) == 0 ? 1 : Technology == 0 ? 2 : 3;
            SetI32(0x22c, n / per * 100);
        }

        /// <summary>
        /// FUN_1004ae6f 0x1004ae6f: the damage picture of a section: 3 when internal, front and rear are all gone, else
        /// from the worse side (15 - 3 x (internal + armour) / full, the full values being the nibbles of the flags +0x10:
        /// low = front, high = rear, FUN_1004b041) 0 intact, 1 for 1..11, 2 for 12..15.
        /// </summary>
        public int SectionState(int s)
        {
            int o = 0x8e0 + s * 0x12;
            int flags = I16(o + 0x10);
            int Level(int armour, int full)
            {
                int inner = Math.Max(0, I32(o));
                armour = Math.Max(0, armour);
                int v = full != 0 ? 0xf - (inner * 0x10000 + armour * 0x10000) * 3 / (full << 16) : 0;
                return v < 1 ? 0 : v > 0xf ? 0xf : v;
            }
            int worst = Math.Max(Level(I32(o + 12), (flags & 0xf0) >> 4), Level(I32(o + 8), flags & 0xf));
            if (I32(o + 8) < 1 && I32(o + 12) < 1 && I32(o) < 1) return 3;
            return worst < 1 ? 0 : worst < 0xc ? 1 : 2;
        }

        /// <summary>FUN_1004b041 0x1004b041: the full values SectionState compares with, in the low byte of the section flags
        /// +0x10: (internal + front) / 5 and (internal + rear) / 5 as nibbles (0..15), and 0x800.</summary>
        public void MarkFullArmor()
        {
            for (int s = 0; s < 8; s++)
            {
                int o = 0x8e0 + s * 0x12;
                int flags = (ushort)I16(o + 0x10) & 0xff00;
                int front = Math.Min(0xf, Math.Max(0, (I32(o) + I32(o + 8)) / 5));
                int rear = Math.Min(0xf, Math.Max(0, (I32(o) + I32(o + 12)) / 5));
                SetI16(o + 0x10, flags | front | rear << 4 | 0x800);
            }
        }

        /// <summary>
        /// FUN_1004b147 0x1004b147: armour mass from the points present: ftol(points / 16 x 100), ferro-fibrous
        /// / 19.2 (Inner Sphere) or / 17.92 (Clan), rounded to half tons (FUN_1004630c of twice the mass, halved).
        /// </summary>
        public void ArmorMass()
        {
            int points = 0;
            for (int s = 0; s < 8; s++)
            {
                if (I32(0x8e8 + s * 0x12) > 0) points += I32(0x8e8 + s * 0x12);
                if (I32(0x8ec + s * 0x12) > 0) points += I32(0x8ec + s * 0x12);
            }
            double div = I32(0x24c) != 1 ? 16.0 : Technology != 0 ? 17.92 : 19.2;
            SetI32(0x250, (int)(points / div * 100.0));
            SetI32(0x250, Round100(I32(0x250) << 1) / 2);
        }

        /// <summary>DAT_100d3d5c: the section FUN_10046ec7 takes surplus armour from next (shared by every mech).</summary>
        public static int ArmorTrimSection;

        /// <summary>
        /// FUN_10046ec7 0x10046ec7: the armour points the mass allows, ftol(ftol(mass x 16 (x 1.2 Inner Sphere / 1.12 Clan
        /// for ferro-fibrous) + 49) / 100); the points present +0x25c; while there are more, one front and one rear point
        /// are taken from section DAT_100d3d5c, which then moves on.
        /// </summary>
        public void ArmorAllowed()
        {
            double factor = Technology != 0 ? 1.12 : 1.2;
            double m = I32(0x250) * 16.0;
            if (I32(0x24c) != 0) m *= factor;
            SetI32(0x258, (int)((int)(m + 49.0) / 100.0));
            int points = 0;
            for (int s = 0; s < 8; s++)
            {
                points += I32(0x8e8 + s * 0x12);
                if (I32(0x8ec + s * 0x12) >= 0) points += I32(0x8ec + s * 0x12);
            }
            SetI32(0x25c, points);
            while (I32(0x25c) > I32(0x258))
            {
                int o = 0x8e0 + ArmorTrimSection * 0x12;
                if (I32(o + 8) > 0) { SetI32(o + 8, I32(o + 8) - 1); SetI32(0x25c, I32(0x25c) - 1); }
                if (I32(0x25c) <= I32(0x258)) break;
                if (I32(o + 12) > 0) { SetI32(o + 12, I32(o + 12) - 1); SetI32(0x25c, I32(0x25c) - 1); }
                if (++ArmorTrimSection > 7) ArmorTrimSection = 0;
            }
        }

        /// <summary>FUN_1004ad22 0x1004ad22: all masses and movement again (engine, movement, weapons, heat sinks, armour mass,
        /// armour points, ammunition, totals).</summary>
        public void Refit(ShellTables t)
        {
            Engine(t);
            Movement();
            WeaponMass(t);
            HeatSinks();
            ArmorMass();
            ArmorAllowed();
            AmmoMass();
            Totals();
        }
    }

    /// <summary>Pilot record (0x2066 bytes): +0 name, +0x20 mech id (-1), +0x24 pilot id, +0x28 8 skill shorts, +0x38, +0x3c
    /// salary, +0x40, +0x44 / +0x54 strings, +100 dossier text, +0x2064 the player.</summary>
    public sealed class PilotRecord : ShellRecord
    {
        public const int Size = 0x2066;
        public PilotRecord() : base(Size) { }
        public PilotRecord(byte[] data) : base(data) { }
        public string Name => Str(0, 0x20);
        public int MechId { get => I32(0x20); set => SetI32(0x20, value); }
        public int Id { get => I32(0x24); set => SetI32(0x24, value); }
        public bool Player => Data[0x2064] != 0;
    }

    /// <summary>Tech record (0x205e bytes): +0 name, +0x20 (-1), +0x24 salary, +0x2d 8 shorts, +0x3d / +0x4d strings, +0x5e dossier.</summary>
    public sealed class TechRecord : ShellRecord
    {
        public const int Size = 0x205e;
        public TechRecord() : base(Size) { }
        public TechRecord(byte[] data) : base(data) { }
        public string Name => Str(0, 0x20);
    }

    /// <summary>Item record (0x1a bytes, items.cpp): +0 table index, +4 price, +8 kind (0 weapon, 1 ammunition, 2 equipment),
    /// +9 salvaged, +0xa, +0xe, +0x12, +0x16.</summary>
    public sealed class ItemRecord : ShellRecord
    {
        public const int Size = 0x1a;
        public ItemRecord() : base(Size) { }
        public ItemRecord(byte[] data) : base(data) { }
        public int Index { get => I32(0); set => SetI32(0, value); }
        public int Price { get => I32(4); set => SetI32(4, value); }
        public int Kind { get => Data[8]; set => Data[8] = (byte)value; }
        public int FieldA { get => I32(0xa); set => SetI32(0xa, value); }
        public int FieldE { get => I32(0xe); set => SetI32(0xe, value); }
        public int Field12 { get => I32(0x12); set => SetI32(0x12, value); }
        public int Field16 { get => I32(0x16); set => SetI32(0x16, value); }
    }
}
