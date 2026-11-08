using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using MW2.Formats;

namespace MW2.Shell
{
    /// <summary>One slot of an instant action roster (0x40 bytes from +0x14).</summary>
    [Serializable]
    public sealed class IaRosterSlot
    {
        public int Chassis;        // +0x00: mech table index, -1 empty
        public string Mek;         // +0x04, 0x10 bytes
        public string Pilot;       // +0x14, 0x16 bytes
        public string Third;       // +0x2a, 0x16 bytes
    }

    /// <summary>An instant action roster, DAT_100bb5b0 (friends) / DAT_100bb6c8 (enemies), 0x114 bytes (FUN_1007484a).</summary>
    [Serializable]
    public sealed class IaRoster
    {
        public const int Size = 0x114, SlotCount = 4;

        public int Message;        // +0x00
        public int Slot;           // +0x04: the selected slot
        public int Slots;          // +0x08: slots offered (at most 3 are set by FUN_1007484a)
        public int InUse;          // +0x0c
        public int Tons;           // +0x10: heaviest chassis allowed
        public List<IaRosterSlot> Entries = new List<IaRosterSlot>();

        /// <summary>The 0x114-byte block InstantAction works on.</summary>
        public byte[] ToBytes()
        {
            var b = new byte[Size];
            void I(int o, int v) => Buffer.BlockCopy(BitConverter.GetBytes(v), 0, b, o, 4);
            void S(int o, int max, string s)
            {
                var a = Encoding.ASCII.GetBytes(s ?? "");
                Buffer.BlockCopy(a, 0, b, o, Math.Min(a.Length, max));
            }
            I(0, Message); I(4, Slot); I(8, Slots); I(0xc, InUse); I(0x10, Tons);
            for (int k = 0; k < SlotCount && k < Entries.Count; k++)
            {
                int o = 0x14 + k * 0x40;
                I(o, Entries[k].Chassis);
                S(o + 4, 0x10, Entries[k].Mek);
                S(o + 0x14, 0x16, Entries[k].Pilot);
                S(o + 0x2a, 0x16, Entries[k].Third);
            }
            return b;
        }

        public static IaRoster FromBytes(byte[] b, int o)
        {
            int I(int at) => BitConverter.ToInt32(b, o + at);
            string S(int at, int max)
            {
                int e = o + at;
                while (e < o + at + max && b[e] != 0) e++;
                return Encoding.ASCII.GetString(b, o + at, e - o - at);
            }
            var r = new IaRoster { Message = I(0), Slot = I(4), Slots = I(8), InUse = I(0xc), Tons = I(0x10) };
            for (int k = 0; k < SlotCount; k++)
            {
                int s = 0x14 + k * 0x40;
                r.Entries.Add(new IaRosterSlot { Chassis = I(s), Mek = S(s + 4, 0x10), Pilot = S(s + 0x14, 0x16), Third = S(s + 0x2a, 0x16) });
            }
            return r;
        }
    }

    /// <summary>A skill row 0x100b7850 + row x 10: five shorts of the GPS skills (UserStar, FUN_10052540).</summary>
    [Serializable]
    public sealed class IaSkillRow
    {
        public int[] Values = new int[5];
    }

    /// <summary>A planet of the instant action mission settings, 0x100b2888 (12 bytes: name, owner, prefix).</summary>
    [Serializable]
    public sealed class IaPlanet
    {
        public string Name;
        public int Owner;          // +4: faction 0..2
        public int Prefix;         // +8: planet-file prefix index (as the generator's planet table)
    }

    /// <summary>
    /// The instant action tables of MW2SHELL.DLL as one extracted file: the default chassis of the six points
    /// (0x100b78c8 + point x 0x38 + 0x28: mech table index, -1 none, -2 random), the enemy skill row base
    /// DAT_100b78c0, the two default rosters, the skill rows 0x100b7850 and the planets of the mission settings screen.
    /// </summary>
    [Serializable]
    public sealed class ShellInstantActionTables
    {
        public const string TableName = "shell-instant-action";
        public const int CurrentFormat = 1;
        public const uint PointVa = 0x100b78c8, BaseRowVa = 0x100b78c0, FriendRosterVa = 0x100bb5b0,
            EnemyRosterVa = 0x100bb6c8, SkillVa = 0x100b7850, PlanetVa = 0x100b2888;
        public const int Points = 6, SkillRows = 9, PlanetCount = 15;

        public int Format = CurrentFormat;
        public int[] PointChassis = new int[Points];
        /// <summary>DAT_100b78c0: the enemy stars' skill row is this − 2 (− 1 / − 3 by mw2dif.cfg +5).</summary>
        public int BaseRow;
        /// <summary>0 the player's side, 1 the enemy.</summary>
        public List<IaRoster> Rosters = new List<IaRoster>();
        /// <summary>Rows 0..8 (row 0 for team 0; others clamped to 1..8).</summary>
        public List<IaSkillRow> Skills = new List<IaSkillRow>();
        public List<IaPlanet> Planets = new List<IaPlanet>();

        public static ShellInstantActionTables Get(string gameFolder, TableStore store = null)
        {
            if (store == null) store = TableStore.Default;
            var t = store.Extract(TableName, () => Load(Path.Combine(gameFolder, "MW2SHELL.DLL")));
            if (t.Format != CurrentFormat)
                throw new InvalidDataException($"{store.PathFor(TableName)} has format {t.Format}, this build reads " +
                    $"{CurrentFormat}: move it aside (keeping your edits) and run MW2 -> Tables -> Extract missing.");
            return t;
        }

        public static ShellInstantActionTables Load(string shellDll)
        {
            byte[] d = File.ReadAllBytes(shellDll);
            int Off(uint va) => PeImage.FileOffset(d, va);
            int I(uint va) => BitConverter.ToInt32(d, Off(va));
            string P(uint va)
            {
                uint p = (uint)I(va);
                if (p == 0 || PeImage.IsZeroFill(d, p)) return "";
                int o = Off(p), e = o;
                while (e < d.Length && d[e] != 0) e++;
                return Encoding.ASCII.GetString(d, o, e - o);
            }

            var t = new ShellInstantActionTables();
            for (uint i = 0; i < Points; i++) t.PointChassis[i] = I(PointVa + i * 0x38 + 0x28);
            t.BaseRow = I(BaseRowVa);
            t.Rosters.Add(IaRoster.FromBytes(d, Off(FriendRosterVa)));
            t.Rosters.Add(IaRoster.FromBytes(d, Off(EnemyRosterVa)));
            for (uint r = 0; r < SkillRows; r++)
            {
                var row = new IaSkillRow();
                for (uint k = 0; k < 5; k++) row.Values[k] = BitConverter.ToInt16(d, Off(SkillVa + r * 10 + k * 2));
                t.Skills.Add(row);
            }
            for (uint i = 0; i < PlanetCount; i++)
                t.Planets.Add(new IaPlanet { Name = P(PlanetVa + i * 12), Owner = I(PlanetVa + i * 12 + 4), Prefix = I(PlanetVa + i * 12 + 8) });
            return t;
        }
    }
}
