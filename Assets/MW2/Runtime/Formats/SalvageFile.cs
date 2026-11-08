using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace MW2.Formats
{
    /// <summary>
    /// salvage.mw2, the engine's list of wrecks for the shell. FUN_10059a10 0x10059a10 keeps a 0x65f-byte record when a
    /// unit of the player's team or an enemy mech killed by the player is destroyed (Mech_Destroy 0x10035ef6): +0 the
    /// unit's base MEK code (GPS name table DAT_100afa45; the shell appends STD), +0x19 the MEK header, +0x31 the current
    /// sections with armour divided by the difficulty's factor (1 / 3 / 4 for mw2dif.cfg +5 = 0 / 1 / 2) and all three
    /// values zeroed when the structure is gone and either armour is, +0x4e1 the MEK name block, +0x51b the MEK's
    /// sections as loaded. Salvage_Write 0x10059d0f (only with records): u32 count, then per record 0x19 bytes of
    /// name, 0x32 of name block, two u32 0, eight 0x28 sections (ftol front, rear, internal; 12 u16 criticals; i16 count;
    /// u16 flags), the 0x140 loaded sections and 0x60 rand() values (srand(time)).
    /// </summary>
    public sealed class SalvageFile
    {
        public sealed class Wreck
        {
            public string MekName = "";
            public byte[] NameBlock = new byte[0x32];
            public readonly byte[] Sections = new byte[0x140];
            public readonly byte[] LoadedSections = new byte[0x140];
            public readonly int[] Rolls = new int[0x60];
        }

        public readonly List<Wreck> Wrecks = new List<Wreck>();

        /// <summary>FUN_100675e8 0x100675e8 reads the count, then each 0x453-byte wreck. The shell consumes the final
        /// 0x180 bytes as 96 random integers when assigning recovered criticals.</summary>
        public static SalvageFile Read(byte[] data)
        {
            if (data == null || data.Length < 4) throw new InvalidDataException("Missing salvage count");
            int count = BitConverter.ToInt32(data, 0);
            const int recordSize = 0x19 + 0x3a + 0x140 + 0x140 + 0x180;
            if (count < 0 || (long)count * recordSize + 4 > data.Length)
                throw new InvalidDataException("Truncated salvage records");
            var result = new SalvageFile();
            int offset = 4;
            for (int i = 0; i < count; i++)
            {
                var wreck = new Wreck();
                int nameEnd = Array.IndexOf(data, (byte)0, offset, 0x19);
                if (nameEnd < 0) nameEnd = offset + 0x19;
                wreck.MekName = Encoding.ASCII.GetString(data, offset, nameEnd - offset);
                offset += 0x19;
                Buffer.BlockCopy(data, offset, wreck.NameBlock, 0, 0x32);
                offset += 0x3a; // The last eight bytes are the two zero dwords written by Salvage_Write.
                Buffer.BlockCopy(data, offset, wreck.Sections, 0, 0x140);
                offset += 0x140;
                Buffer.BlockCopy(data, offset, wreck.LoadedSections, 0, 0x140);
                offset += 0x140;
                for (int k = 0; k < wreck.Rolls.Length; k++, offset += 4)
                    wreck.Rolls[k] = BitConverter.ToInt32(data, offset);
                result.Wrecks.Add(wreck);
            }
            return result;
        }

        /// <summary>The armour divisor of FUN_10059a10 for mw2dif.cfg +5.</summary>
        public static int DifficultyFactor(int difficulty) => difficulty == 0 ? 1 : difficulty == 2 ? 4 : 3;

        /// <summary>
        /// FUN_10059a10: a wreck from the unit's state at its destruction. <paramref name="flags"/> are the sections'
        /// +0x26 words, <paramref name="items"/> the critical lists as the unit keeps them.
        /// </summary>
        public Wreck Add(string mekName, byte[] mekBytes, int[] front, int[] rear, int[] intern, IList<int>[] items,
                         int[] flags, int difficulty)
        {
            // FUN_100675e8 appends "STD" to the saved name before Mech_FromMek. The mission loadout is the full
            // resource name (ANI00STD or a career ANI00MER); the wreck stores its five-character chassis code.
            string code = mekName ?? "";
            if (code.Length >= 8 && (code.EndsWith("STD", StringComparison.OrdinalIgnoreCase) ||
                                     code.EndsWith("MER", StringComparison.OrdinalIgnoreCase)))
                code = code.Substring(0, code.Length - 3);
            var w = new Wreck { MekName = code.Length > 8 ? code.Substring(0, 8) : code };
            int weapons = Math.Min(BitConverter.ToInt32(mekBytes, 0x10), 14), ammo = BitConverter.ToInt32(mekBytes, 0x14);
            int nameStart = 0x158 + weapons * 8 + ammo * 8;
            Buffer.BlockCopy(mekBytes, nameStart, w.NameBlock, 0, Math.Min(0x32, Math.Max(0, mekBytes.Length - nameStart)));
            Buffer.BlockCopy(mekBytes, 0x18, w.LoadedSections, 0, 0x140);
            float factor = DifficultyFactor(difficulty);
            for (int s = 0; s < 8; s++)
            {
                float f = front[s] / factor, r = rear[s] / factor, i = intern[s];
                if (i <= 0 && (f <= 0 || r <= 0)) f = r = i = 0;
                int o = s * 0x28;
                Put32(w.Sections, o, (int)f);
                Put32(w.Sections, o + 4, (int)r);
                Put32(w.Sections, o + 8, (int)i);
                var list = items != null && s < items.Length ? items[s] : null;
                int count = list != null ? Math.Min(list.Count, 12) : 0;
                for (int k = 0; k < count; k++) Put16(w.Sections, o + 0xc + k * 2, list[k]);
                Put16(w.Sections, o + 0x24, count);
                Put16(w.Sections, o + 0x26, flags != null && s < flags.Length ? flags[s] : 0);
            }
            Wrecks.Add(w);
            return w;
        }

        /// <summary>Salvage_Write: null without wrecks (no file).</summary>
        public byte[] Write(Func<int> rand)
        {
            if (Wrecks.Count == 0) return null;
            using (var m = new MemoryStream())
            using (var o = new BinaryWriter(m))
            {
                o.Write(Wrecks.Count);
                foreach (var w in Wrecks)
                {
                    var name = new byte[0x19];
                    var b = Encoding.ASCII.GetBytes(w.MekName);
                    Buffer.BlockCopy(b, 0, name, 0, Math.Min(b.Length, 0x18));
                    o.Write(name);
                    o.Write(w.NameBlock);
                    o.Write(0);
                    o.Write(0);
                    o.Write(w.Sections);
                    o.Write(w.LoadedSections);
                    for (int k = 0; k < 0x60; k++) o.Write(rand());
                }
                return m.ToArray();
            }
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
