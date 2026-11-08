using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using MW2.Formats;

namespace MW2.Shell
{
    /// <summary>One area slot of the mission template 0x100b5410 (0x17 bytes from +0x13).</summary>
    [Serializable]
    public sealed class RmgArea
    {
        public string Name;        // +0x00, up to 0xb bytes
        public float Width;        // +0x0b
        public float Flag;         // +0x0f: 1 marks an objective once picked
        public float Depth;        // +0x13
    }

    /// <summary>Planet table 0x100b5aa0 (8 bytes: name pointer, owner s16, planet-file prefix s16).</summary>
    [Serializable]
    public sealed class RmgPlanet
    {
        public string Name;
        public int Owner;          // +4 s16: faction index 0..2
        public int Prefix;         // +6 s16: index into the planet-file prefixes
    }

    /// <summary>Faction table 0x100b5a28 (0xe bytes: name, short name +4, contract code +0xa).</summary>
    [Serializable]
    public sealed class RmgFaction
    {
        public string Name;        // +0x0: "Federated Commonwealth"
        public string Short;       // +0x4: "FC", the prefix of enemy unit names
        public string Code;        // +0xa: "FEDCOM", the employer written into the contract (CTNT +0x90)
    }

    /// <summary>A chassis of the generator's mech lists (0x20 bytes, the layout of the shell mech table's first 0x20).</summary>
    [Serializable]
    public sealed class RmgChassis
    {
        public string ShortCode;   // +0x00
        public string Code;        // +0x04: MEK prefix ("%s00std")
        public string Model;       // +0x08
        public string Name;        // +0x0c
        public int Tons;           // +0x10
        public int Field14;        // +0x14
        public int IntroYear;      // +0x18
        public int Field1C;        // +0x1c
    }

    /// <summary>A difficulty row 0x100b5b88 + row x 0xe: seven shorts written into the enemy GPS records.</summary>
    [Serializable]
    public sealed class RmgDifficulty
    {
        public int[] Values = new int[7];
    }

    /// <summary>The generator's strings by meaning (formats for sprintf, file names, log lines, error boxes).</summary>
    [Serializable]
    public sealed class RmgText
    {
        public string LogHeader, InstantActionLabel, LogPay, LogBadDate, LogPlanetTooLong, LogBadType, LogBadDifficulty,
            LogBadPlanet, LogTypeDifficulty, WorldFile, LogCreating, LogFactions, LogPlanetFile, EnemyStarInclude,
            AreaSource, AreaFile, LogCopy, AreaName, StepName, NavName, NavAlpha, NavBeta, NavGamma, NavFile, NavLabel,
            LogCreatingNav, SceneSource, SceneFile, LogCopyScene, Briefing, BriefingFile, DebriefingFailed,
            DebriefingFailedFile, DebriefingSucceeded, DebriefingSucceededFile, StrengthLight, StrengthMedium,
            StrengthHeavy, Contract, ContractFile, UnitName, StockMek, LogGps, EnemyStarFile, PlanetFile,
            ErrorCreate, ErrorParameter, ErrorOpen, ErrorMemory;
    }

    /// <summary>
    /// The tables of the random mission generator (FUN_1004f490 and helpers) from MW2SHELL.DLL as one extracted file.
    ///
    /// Every random index of the generator is FUN_100507b4, which gives b + 1 when rand() returns 32767, so the
    /// original reads one entry past most of its tables. Those entries are part of the file, at the end of each list,
    /// holding what the original reads there: planet 15 (its name pointer is the int 8 of 0x100b5b18 — no string),
    /// faction 7 (empty), prefix 55 (a null pointer) and pilot 13 (empty). The four chassis lists lie back to back in
    /// the library (light 0x100b5c08, medium 0x100b5d68, heavy 0x100b5ea8, Clan 0x100b5fa8), so one past a list is
    /// the next list's first chassis; they are kept as one list with <see cref="ClassStart"/>, plus the empty entry
    /// that follows the Clan list.
    ///
    /// Strings are read as the loaded image holds them: a pointer into the zero-filled tail of .data is "". The Clan
    /// factions' contract codes point there; the port's old direct read took bytes of the next file section instead
    /// (only reachable for faction 3 as employer, with rand() = 32767).
    /// </summary>
    [Serializable]
    public sealed class ShellRmgTables
    {
        public const string TableName = "shell-rmg";
        public const int CurrentFormat = 1;

        public const uint TemplateVa = 0x100b5410, PlanetVa = 0x100b5aa0, DifficultyBaseVa = 0x100b5b18,
            TypeVa = 0x100b5a8c, FactionVa = 0x100b5a28, PrefixVa = 0x100b5948, PilotVa = 0x100b5b24,
            DifficultyVa = 0x100b5b88, ChassisVa = 0x100b5c08;
        public const int TemplateSize = 0x532, AreaSlots = (TemplateSize - 0x13) / 0x17;

        public int Format = CurrentFormat;
        public RmgText Text = new RmgText();
        /// <summary>Template +0xf: areas in use; +0x11: how many of them (the first) are buildings.</summary>
        public int TemplateCount, TemplateBuildings;
        public List<RmgArea> TemplateAreas = new List<RmgArea>();
        /// <summary>15 planets and the entry past them.</summary>
        public List<RmgPlanet> Planets = new List<RmgPlanet>();
        /// <summary>0x100b5b18: the enemy difficulty row base for a difficulty outside 1..3 (minus 2).</summary>
        public int DifficultyBase;
        /// <summary>0x100b5a8c: index 1 Search and Destroy, 2 Defend, 3 Recon (0 is empty).</summary>
        public List<string> MissionTypes = new List<string>();
        /// <summary>0..2 Inner Sphere houses, 3..6 Clans, 7 the entry past them.</summary>
        public List<RmgFaction> Factions = new List<RmgFaction>();
        /// <summary>0x100b5948: planet-file prefixes ("%splt1"), 55 and the null entry after them.</summary>
        public List<string> PlanetPrefixes = new List<string>();
        /// <summary>0x100b5b24 (8 bytes each): pilot names, 13 and the entry past them.</summary>
        public List<string> Pilots = new List<string>();
        /// <summary>Rows 0..8 (the generator clamps to 1..8).</summary>
        public List<RmgDifficulty> DifficultyRows = new List<RmgDifficulty>();
        public List<RmgChassis> Chassis = new List<RmgChassis>();
        /// <summary>First chassis and count of the light, medium, heavy and Clan lists (FUN_10050e40).</summary>
        public int[] ClassStart = { 0, 11, 21, 29 };
        public int[] ClassCount = { 0xb, 10, 8, 0x12 };

        public static ShellRmgTables Get(string gameFolder, TableStore store = null)
        {
            if (store == null) store = TableStore.Default;
            var t = store.Extract(TableName, () => Load(Path.Combine(gameFolder, "MW2SHELL.DLL")));
            if (t.Format != CurrentFormat)
                throw new InvalidDataException($"{store.PathFor(TableName)} has format {t.Format}, this build reads " +
                    $"{CurrentFormat}: move it aside (keeping your edits) and run MW2 -> Tables -> Extract missing.");
            return t;
        }

        public static ShellRmgTables Load(string shellDll)
        {
            byte[] d = File.ReadAllBytes(shellDll);
            int Off(uint va) => PeImage.FileOffset(d, va);
            int I(uint va) => BitConverter.ToInt32(d, Off(va));
            int H(uint va) => BitConverter.ToInt16(d, Off(va));
            string At(uint va)
            {
                if (PeImage.IsZeroFill(d, va)) return "";
                int o = Off(va), e = o;
                while (e < d.Length && d[e] != 0) e++;
                return Encoding.ASCII.GetString(d, o, e - o);
            }
            // The string a table entry names; "" for a null, zero-filled or unmapped pointer.
            string P(uint va)
            {
                uint p = (uint)I(va);
                if (p == 0) return "";
                try { return At(p); }
                catch (InvalidDataException) { return ""; }
            }

            var t = new ShellRmgTables();
            var x = t.Text;
            x.InstantActionLabel = At(0x100b6b44); x.LogHeader = At(0x100b6b54); x.LogPay = At(0x100b6b90);
            x.LogBadDate = At(0x100b6bb4); x.LogPlanetTooLong = At(0x100b6bd0); x.LogBadType = At(0x100b6bec);
            x.LogBadDifficulty = At(0x100b6c1c); x.LogBadPlanet = At(0x100b6c48); x.LogTypeDifficulty = At(0x100b6c6c);
            x.WorldFile = At(0x100b6ca0); x.LogCreating = At(0x100b6cb0); x.LogFactions = At(0x100b6cc0);
            x.LogPlanetFile = At(0x100b6cd4); x.EnemyStarInclude = At(0x100b6cfc); x.AreaSource = At(0x100b6d0c);
            x.AreaFile = At(0x100b6d18); x.LogCopy = At(0x100b6d28); x.AreaName = At(0x100b6d3c); x.StepName = At(0x100b6d44);
            x.NavName = At(0x100b6d50); x.NavAlpha = At(0x100b6d64); x.NavBeta = At(0x100b6d74); x.NavGamma = At(0x100b6d84);
            x.NavFile = At(0x100b6d8c); x.NavLabel = At(0x100b6d9c); x.LogCreatingNav = At(0x100b6da4);
            x.SceneSource = At(0x100b6db4); x.SceneFile = At(0x100b6dc0); x.LogCopyScene = At(0x100b6dd0);
            x.Briefing = At(0x100b6de4); x.BriefingFile = At(0x100b7074); x.DebriefingFailed = At(0x100b7084);
            x.DebriefingFailedFile = At(0x100b70f0); x.DebriefingSucceeded = At(0x100b7100);
            x.DebriefingSucceededFile = At(0x100b7180); x.StrengthLight = At(0x100b7190); x.StrengthMedium = At(0x100b7198);
            x.StrengthHeavy = At(0x100b71a0); x.Contract = At(0x100b71a8); x.ContractFile = At(0x100b73dc);
            x.UnitName = At(0x100b74ac); x.StockMek = At(0x100b74b4); x.LogGps = At(0x100b74e4);
            x.EnemyStarFile = At(0x100b750c); x.PlanetFile = At(0x100b751c);
            x.ErrorCreate = At(0x100bac1c); x.ErrorParameter = At(0x100bac3c); x.ErrorOpen = At(0x100bac60);
            x.ErrorMemory = At(0x100bac80);

            int tpl = Off(TemplateVa);
            t.TemplateCount = BitConverter.ToInt16(d, tpl + 0xf);
            t.TemplateBuildings = BitConverter.ToInt16(d, tpl + 0x11);
            for (int k = 0; k < AreaSlots; k++)
            {
                int o = tpl + 0x13 + k * 0x17, e = o;
                while (e < o + 0xb && d[e] != 0) e++;
                t.TemplateAreas.Add(new RmgArea
                {
                    Name = Encoding.ASCII.GetString(d, o, e - o), Width = BitConverter.ToSingle(d, o + 0xb),
                    Flag = BitConverter.ToSingle(d, o + 0xf), Depth = BitConverter.ToSingle(d, o + 0x13),
                });
            }
            for (uint k = 0; k < 16; k++)
                t.Planets.Add(new RmgPlanet { Name = P(PlanetVa + k * 8), Owner = H(PlanetVa + k * 8 + 4), Prefix = H(PlanetVa + k * 8 + 6) });
            t.DifficultyBase = I(DifficultyBaseVa);
            for (uint k = 0; k < 4; k++) t.MissionTypes.Add(P(TypeVa + k * 4));
            for (uint k = 0; k < 8; k++)
                t.Factions.Add(new RmgFaction { Name = P(FactionVa + k * 0xe), Short = P(FactionVa + k * 0xe + 4), Code = P(FactionVa + k * 0xe + 0xa) });
            for (uint k = 0; k < 0x38; k++) t.PlanetPrefixes.Add(P(PrefixVa + k * 4));
            for (uint k = 0; k < 14; k++) t.Pilots.Add(P(PilotVa + k * 8));
            for (uint k = 0; k < 9; k++)
            {
                var row = new RmgDifficulty();
                for (uint v = 0; v < 7; v++) row.Values[v] = H(DifficultyVa + k * 0xe + v * 2);
                t.DifficultyRows.Add(row);
            }
            for (uint k = 0; k < 0xb + 10 + 8 + 0x12 + 1; k++)
            {
                uint r = ChassisVa + k * 0x20;
                t.Chassis.Add(new RmgChassis
                {
                    ShortCode = P(r), Code = P(r + 4), Model = P(r + 8), Name = P(r + 0xc), Tons = I(r + 0x10),
                    Field14 = I(r + 0x14), IntroYear = I(r + 0x18), Field1C = I(r + 0x1c),
                });
            }
            return t;
        }
    }
}
