using System;
using System.IO;
using System.Text;

namespace MW2.Shell
{
    /// <summary>
    /// Data of MW2SHELL.DLL. Every table the shell reads comes from an extracted, editable file (see
    /// <see cref="TableNames"/> and <see cref="FromStore"/>): <see cref="Economy"/> (mechs, weapons, equipment, critical
    /// items, engines, structure, planets), <see cref="Lab"/> (the MechLab check), <see cref="Rmg"/> (the random mission
    /// generator), <see cref="Instant"/> (instant action) and <see cref="Defaults"/> (high scores, cfg defaults, credits,
    /// menu). Once those files exist the library is not needed: it is opened only to extract a missing file, and lazily
    /// for the raw <see cref="I32"/> / <see cref="Str"/> / <see cref="Bytes"/> / <see cref="Resource"/> that
    /// checks compare against (no shell code calls them).
    /// </summary>
    public sealed class ShellTables
    {
        readonly string _dllPath;
        byte[] _dll;
        int _sections, _sectionTable;
        uint _imageBase;

        public const int MechCount = ShellEconomyTables.MechCount, WeaponCount = ShellEconomyTables.WeaponCount;

        /// <summary>The economy tables the accessors answer from.</summary>
        public ShellEconomyTables Economy { get; }

        /// <summary>The MechLab check's tables (FUN_10044190).</summary>
        public ShellLabTables Lab { get; }

        /// <summary>The random mission generator's tables (FUN_1004f490).</summary>
        public ShellRmgTables Rmg { get; }

        /// <summary>Instant action points, rosters, skill rows and mission-settings planets.</summary>
        public ShellInstantActionTables Instant { get; }

        /// <summary>Default high scores, mw2snd.cfg / mw2dif.cfg blocks, The First Circuit and the window menu.</summary>
        public ShellDefaultTables Defaults { get; }

        /// <summary>
        /// Raised once per accessor for an index outside both the table and the extracted entries around it (only a
        /// corrupt MEK or save reaches one); the accessor then answers a zeroed record.
        /// </summary>
        public static event Action<string> OutsideTables;

        /// <summary>
        /// Every table not passed in is read straight from the library (the extraction primitive; checks
        /// pass none). A table passed in, e.g. one from <see cref="MW2.Formats.TableStore"/>, is used as it is, and then
        /// the library is not opened.
        /// </summary>
        public ShellTables(string shellDll, ShellEconomyTables economy = null, ShellLabTables lab = null,
            ShellRmgTables rmg = null, ShellInstantActionTables instant = null, ShellDefaultTables defaults = null)
        {
            _dllPath = shellDll;
            Economy = economy ?? ShellEconomyTables.Load(shellDll);
            Lab = lab ?? ShellLabTables.Load(shellDll);
            Rmg = rmg ?? ShellRmgTables.Load(shellDll);
            Instant = instant ?? ShellInstantActionTables.Load(shellDll);
            Defaults = defaults ?? ShellDefaultTables.Load(shellDll);
        }

        /// <summary>The extracted files <see cref="FromStore"/> reads.</summary>
        public static readonly string[] TableNames =
        {
            ShellEconomyTables.TableName, ShellLabTables.TableName, ShellRmgTables.TableName,
            ShellInstantActionTables.TableName, ShellDefaultTables.TableName,
        };

        /// <summary>What the game uses: every table from its extracted file, extracted from the library on first use.</summary>
        public static ShellTables FromStore(string gameFolder, MW2.Formats.TableStore store = null) =>
            new ShellTables(Path.Combine(gameFolder, "MW2SHELL.DLL"),
                ShellEconomyTables.Get(gameFolder, store), ShellLabTables.Get(gameFolder, store),
                ShellRmgTables.Get(gameFolder, store), ShellInstantActionTables.Get(gameFolder, store),
                ShellDefaultTables.Get(gameFolder, store));

        // ---- the library itself, for the console's reference reads only ----

        byte[] Dll()
        {
            if (_dll != null) return _dll;
            _dll = File.ReadAllBytes(_dllPath);
            int pe = BitConverter.ToInt32(_dll, 0x3c);
            _sections = BitConverter.ToUInt16(_dll, pe + 6);
            _sectionTable = pe + 24 + BitConverter.ToUInt16(_dll, pe + 20);
            _imageBase = BitConverter.ToUInt32(_dll, pe + 24 + 28);
            return _dll;
        }

        int Offset(uint va)
        {
            var d = Dll();
            uint rva = va - _imageBase;
            for (int i = 0; i < _sections; i++)
            {
                int e = _sectionTable + i * 40;
                uint start = BitConverter.ToUInt32(d, e + 12), size = Math.Max(BitConverter.ToUInt32(d, e + 8), BitConverter.ToUInt32(d, e + 16));
                if (rva >= start && rva < start + size) return (int)(BitConverter.ToUInt32(d, e + 20) + rva - start);
            }
            throw new ArgumentException($"MW2SHELL.DLL address {va:x8} is not mapped");
        }

        public int I32(uint va) => BitConverter.ToInt32(Dll(), Offset(va));

        /// <summary>FUN_1002e9c2 0x1002e9c2: copy a fixed default-data block from the shell image.</summary>
        public byte[] Bytes(uint va, int count)
        {
            var result = new byte[count];
            Buffer.BlockCopy(Dll(), Offset(va), result, 0, count);
            return result;
        }

        public string Str(uint va)
        {
            if (va == 0) return null;
            var d = Dll();
            int o = Offset(va), e = o;
            while (e < d.Length && d[e] != 0) e++;
            return Encoding.ASCII.GetString(d, o, e - o);
        }

        /// <summary>A resource of the DLL (type and numeric id, first language), null if there is none.</summary>
        public byte[] Resource(int type, int id) => MW2.Formats.PeImage.Resource(Dll(), type, id);

        // ---- the accessors ----
        // They answer from the extracted tables. The original indexes them unchecked; the indexes its inputs can produce
        // outside a table (structure row -1 for a MEK of 1..5 tons, critical item -1 for an unmatched code, weapon types
        // 66..69 for a critical code 6600..6999) are extracted with the table (ShellEconomyTables.ItemBefore etc.), so
        // they read what the original reads. Anything further out only a corrupt file reaches; it gets a zeroed record
        // (the original would read whatever lies there) and one OutsideTables report.

        static readonly System.Collections.Generic.HashSet<string> _reported = new System.Collections.Generic.HashSet<string>();

        static bool Outside<T>(System.Collections.Generic.List<T> list, int i) => i < 0 || i >= list.Count;

        static void Report(string table, int index)
        {
            lock (_reported)
                if (!_reported.Add(table)) return;
            OutsideTables?.Invoke($"MW2SHELL {table} index {index} lies outside the table and its extracted neighbours; " +
                                  "answered with zeros (the original reads unrelated memory there)");
        }

        static ShellMechDef ZeroMech() => new ShellMechDef { ShortCode = "", Code = "", Model = "", Name = "" };

        /// <summary>Mech table 0x100aac58.</summary>
        public ShellMechDef Mech(int i)
        {
            if (!Outside(Economy.Mechs, i)) return Economy.Mechs[i];
            Report("mech", i);
            return ZeroMech();
        }

        public string MechCode(int i) => Mech(i).Code;
        public string MechModel(int i) => Mech(i).Model;
        public string MechName(int i) => Mech(i).Name;

        /// <summary>FUN_1004576c / FUN_1004aded: table index of a three-letter code (case-insensitive), -1 if none.</summary>
        public int FindMech(string code3, int limit = MechCount)
        {
            if (code3 == null) return -1;
            string c = code3.Length > 3 ? code3.Substring(0, 3) : code3;
            for (int i = 0; i < limit; i++)
                if (string.Equals(MechCode(i), c, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        /// <summary>Internal structure 0x100ab998 by (tons - 10) / 5: 0 centre torso, 1 side torso, 2 arm, 3 leg.</summary>
        public int Structure(int tonIndex, int field)
        {
            if (!Outside(Economy.Structure, tonIndex)) return Economy.Structure[tonIndex].Field(field);
            if (tonIndex == -1) return Economy.StructureBefore.Field(field);
            Report("structure", tonIndex);
            return 0;
        }

        /// <summary>Engine table 0x100abb88 by (rating - 10) / 5 (every caller keeps the rating within 10..400).</summary>
        public ShellEngineDef Engine(int index)
        {
            if (!Outside(Economy.Engines, index)) return Economy.Engines[index];
            Report("engine", index);
            return new ShellEngineDef { Maker = "" };
        }

        public int EngineRating(int index) => Engine(index).Rating;
        public int EngineMass(int index) => Engine(index).Mass;

        /// <summary>Weapon table 0x100ac088 (0x42 types; 66..69 are the extracted entries after it).</summary>
        public ShellWeaponDef Weapon(int index)
        {
            if (!Outside(Economy.Weapons, index)) return Economy.Weapons[index];
            int after = index - Economy.Weapons.Count;
            if (after >= 0 && after < Economy.WeaponsAfter.Count) return Economy.WeaponsAfter[after];
            Report("weapon", index);
            return new ShellWeaponDef { Name = "", File = "" };
        }

        /// <summary>Equipment table 0x100abac8 (12 entries; callers check the index).</summary>
        public ShellEquipmentDef Equipment(int index)
        {
            if (!Outside(Economy.Equipment, index)) return Economy.Equipment[index];
            Report("equipment", index);
            return new ShellEquipmentDef { Name = "" };
        }

        /// <summary>Critical-item table 0x100a8198 (MASC 7000 .. CASE 9550; -1 is the extracted entry before it).</summary>
        public ShellItemDef Item(int index)
        {
            if (!Outside(Economy.Items, index)) return Economy.Items[index];
            if (index == -1) return Economy.ItemBefore;
            Report("critical item", index);
            return new ShellItemDef { Name = "" };
        }

        /// <summary>FUN_10033846 0x10033846: inventory row labels.</summary>
        public string WeaponName(int index) => index >= 0 && index < WeaponCount ? Economy.Weapons[index].Name : "";
        public string EquipmentName(int index) => index >= 0 && index < ShellEconomyTables.EquipmentCount
            ? Economy.Equipment[index].Name : "";
    }
}
