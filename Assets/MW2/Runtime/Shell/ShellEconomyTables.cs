using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using MW2.Formats;

namespace MW2.Shell
{
    /// <summary>MW2SHELL.DLL mech table 0x100aac58, one 0x28-byte record (FUN_10026db4, FUN_1004576c, FUN_1004aded).</summary>
    [Serializable]
    public sealed class ShellMechDef
    {
        public string ShortCode;   // +0x00: two letters ("cm"; empty for some entries)
        public string Code;        // +0x04: three letters, the MEK file prefix (FUN_1004576c / FUN_1004aded compare it)
        public string Model;       // +0x08: model name
        public string Name;        // +0x0c: display name
        public int Tons;           // +0x10
        public int Field14;        // +0x14: -1 in every retail record
        public int IntroYear;      // +0x18: year of introduction (-1 none)
        public int Field1C;        // +0x1c
        public int Technology;     // +0x20: 0 Clan, 1 Inner Sphere, 2 aerospace
        public int Price;          // +0x24: base price before FUN_10026a1b's year adjustment
    }

    /// <summary>
    /// MW2SHELL.DLL weapon table 0x100ac088, one 0x40-byte record per weapon type (0x42 types). The record starts at
    /// 0x100ac088, right where the 80 engine rows of 0x100abb88 end: FUN_100029e8 reads the figures from there, and the
    /// rest of the shell addresses the same records through the mass field 0x100ac0a0 (+0x18).
    /// </summary>
    [Serializable]
    public sealed class ShellWeaponDef
    {
        public int Heat;           // +0x00: Arms Merchant "Heat" (FUN_100029e8 field 1)
        public int Damage;         // +0x04: "Damage"; negative is per missile ("1/missile")
        public int Field08;        // +0x08: field 3, "-" when negative; not on the Arms Merchant screen
        public int Field0C;        // +0x0c: field 4, "1-n" (-1 "-"); not on the screen
        public int Field10;        // +0x10: field 5, with +0x0c "n+1-m"; not on the screen
        public int Range;          // +0x14: "Range" (-1 "-")
        public int Mass;           // +0x18 (0x100ac0a0): tons x 100
        public int Slots;          // +0x1c (0x100ac0a4): critical slots, "Crit"
        public int ShotsPerTon;    // +0x20 (0x100ac0a8): "Ammo"
        public string Name;        // +0x24: row label (FUN_10033846)
        public int Field28;        // +0x28: -1 in every retail record
        public string File;        // +0x2c: model / file name ("aclrm20")
        public int Year;           // +0x30
        public int Price;          // +0x34: FUN_100268b0
        public int AmmoPrice;      // +0x38: FUN_10026902; > 0 means the weapon takes ammunition (FUN_100457e6)
        public int Technology;     // +0x3c (0x100ac0c4): 0 Clan, 1 Inner Sphere
    }

    /// <summary>MW2SHELL.DLL equipment table 0x100abac8, 0x10 per entry, 12 entries (Engine .. MASC, Max equipment).</summary>
    [Serializable]
    public sealed class ShellEquipmentDef
    {
        public int Kind;           // +0x00: the entry's own index, switched on by the customizer (0x100abac8)
        public string Name;        // +0x04 (0x100abacc)
        public int Space;          // +0x08: critical slots, -1 none (0x100abad0)
        public int Price;          // +0x0c
    }

    /// <summary>MW2SHELL.DLL critical-item table 0x100a8198, 0xc per entry, 0x18 entries (MASC 7000 .. CASE 9550).</summary>
    [Serializable]
    public sealed class ShellItemDef
    {
        public int Code;           // +0x00: critical code; matched by code / 0x32 (Career.ItemIndex)
        public string Name;        // +0x04 (0x100a819c)
        public int Price;          // +0x08 (0x100a81a0)
    }

    /// <summary>MW2SHELL.DLL engine table 0x100abb88, 0x10 per 5-point rating step from 10; the last record is rating -1.</summary>
    [Serializable]
    public sealed class ShellEngineDef
    {
        public int Rating;         // +0x00
        public int Mass;           // +0x04: tons x 100
        public string Maker;       // +0x08 (0x100abb90)
        public int Price;          // +0x0c
    }

    /// <summary>MW2SHELL.DLL internal structure table 0x100ab998, 0x10 per 5 tons from 10 tons.</summary>
    [Serializable]
    public sealed class ShellStructureDef
    {
        public int CenterTorso, SideTorso, Arm, Leg; // +0x00 .. +0x0c
        public int Field(int i) => i == 0 ? CenterTorso : i == 1 ? SideTorso : i == 2 ? Arm : Leg;
    }

    /// <summary>
    /// The campaign economy tables of MW2SHELL.DLL — mechs, weapons, equipment, critical items, engines, internal
    /// structure and the 23 story planets — as one extracted file. <see cref="Load"/> reads them out of the library
    /// at their fixed addresses (the extraction primitive); <see cref="Get"/> is
    /// what the shell uses at run time: the file in the <see cref="TableStore"/>, extracted on first use. String
    /// pointers become strings, so no library address reaches the file.
    /// </summary>
    [Serializable]
    public sealed class ShellEconomyTables
    {
        public const string TableName = "shell-economy";

        public const uint MechVa = 0x100aac58, StructureVa = 0x100ab998, EngineVa = 0x100abb88, WeaponVa = 0x100ac088,
            EquipmentVa = 0x100abac8, ItemVa = 0x100a8198, PlanetVa = 0x100ab918;
        public const int MechCount = 0x47, StructureCount = 19, EngineCount = 80, WeaponCount = 0x42, EquipmentCount = 12,
            ItemCount = 0x18, PlanetCount = 0x17;

        /// <summary>
        /// Layout version of this file. 2: weapon records start at 0x100ac088 (heat, damage, ..., range first).
        /// 3: the entries just outside the tables that the original's unchecked reads reach (see <see cref="ItemBefore"/>).
        /// </summary>
        public const int CurrentFormat = 3;
        public int Format = CurrentFormat;

        public List<ShellMechDef> Mechs = new List<ShellMechDef>();
        public List<ShellWeaponDef> Weapons = new List<ShellWeaponDef>();
        public List<ShellEquipmentDef> Equipment = new List<ShellEquipmentDef>();
        public List<ShellItemDef> Items = new List<ShellItemDef>();
        public List<ShellEngineDef> Engines = new List<ShellEngineDef>();
        public List<ShellStructureDef> Structure = new List<ShellStructureDef>();
        public List<string> Planets = new List<string>();

        // The original indexes these tables unchecked, and some inputs it accepts reach just outside them. Those bytes
        // are kept here, as the numbers the original reads (at the library's preferred base 0x10000000), so no input the
        // shell produces needs the library: entry -1 of the critical-item table (Career.ItemIndex returns -1 for a code no
        // row matches, e.g. 8050, and FUN_1005cf8c then reads 0x100a818c), row -1 of the structure table (a MEK of 1..5
        // tons such as the stock Battle Suit: 0x100ab988, four pointers of the table before it, read as points), and
        // weapon types 66..69 (a critical code 6600..6999 / 100: 0x100ad108.., the shell objects after the table).

        /// <summary>0x100a818c, read for item index -1 (only its +0 and +8 are used; +4 is 0x17, not a string).</summary>
        public ShellItemDef ItemBefore = new ShellItemDef();
        /// <summary>0x100ab988, read for structure row -1.</summary>
        public ShellStructureDef StructureBefore = new ShellStructureDef();
        /// <summary>0x100ad108 + k x 0x40, read for weapon types 66 + k (numbers only; the names are not strings there).</summary>
        public List<ShellWeaponDef> WeaponsAfter = new List<ShellWeaponDef>();
        public const int WeaponsAfterCount = 4;

        /// <summary>The tables as they stand: the extracted file, or read from MW2SHELL.DLL and written on first use.</summary>
        public static ShellEconomyTables Get(string gameFolder, TableStore store = null)
        {
            if (store == null) store = TableStore.Default;
            var t = store.Extract(TableName, () => Load(Path.Combine(gameFolder, "MW2SHELL.DLL")));
            // A file of another layout would read with fields silently zero; it is never replaced behind the user's back.
            if (t.Format != CurrentFormat)
                throw new InvalidDataException($"{store.PathFor(TableName)} has format {t.Format}, this build reads " +
                    $"{CurrentFormat}: move it aside (keeping your edits) and run MW2 -> Tables -> Extract missing.");
            return t;
        }

        /// <summary>Reads the tables from MW2SHELL.DLL at their retail addresses.</summary>
        public static ShellEconomyTables Load(string shellDll)
        {
            byte[] d = File.ReadAllBytes(shellDll);
            int I(uint va) => BitConverter.ToInt32(d, PeImage.FileOffset(d, va));
            string S(uint va)
            {
                uint p = (uint)I(va);
                // A null pointer (only the engine terminator's maker) is "" — what JsonUtility reads back for null.
                if (p == 0 || PeImage.IsZeroFill(d, p)) return "";
                int o = PeImage.FileOffset(d, p), e = o;
                while (e < d.Length && d[e] != 0) e++;
                return Encoding.ASCII.GetString(d, o, e - o);
            }

            var t = new ShellEconomyTables();
            for (uint i = 0; i < MechCount; i++)
            {
                uint r = MechVa + i * 0x28;
                t.Mechs.Add(new ShellMechDef
                {
                    ShortCode = S(r), Code = S(r + 4), Model = S(r + 8), Name = S(r + 0xc), Tons = I(r + 0x10),
                    Field14 = I(r + 0x14), IntroYear = I(r + 0x18), Field1C = I(r + 0x1c), Technology = I(r + 0x20),
                    Price = I(r + 0x24),
                });
            }
            for (uint i = 0; i < WeaponCount; i++)
            {
                uint r = WeaponVa + i * 0x40;
                t.Weapons.Add(new ShellWeaponDef
                {
                    Heat = I(r), Damage = I(r + 4), Field08 = I(r + 8), Field0C = I(r + 0xc), Field10 = I(r + 0x10),
                    Range = I(r + 0x14), Mass = I(r + 0x18), Slots = I(r + 0x1c), ShotsPerTon = I(r + 0x20),
                    Name = S(r + 0x24), Field28 = I(r + 0x28), File = S(r + 0x2c), Year = I(r + 0x30),
                    Price = I(r + 0x34), AmmoPrice = I(r + 0x38), Technology = I(r + 0x3c),
                });
            }
            for (uint i = 0; i < EquipmentCount; i++)
            {
                uint r = EquipmentVa + i * 0x10;
                t.Equipment.Add(new ShellEquipmentDef { Kind = I(r), Name = S(r + 4), Space = I(r + 8), Price = I(r + 0xc) });
            }
            for (uint i = 0; i < ItemCount; i++)
            {
                uint r = ItemVa + i * 0xc;
                t.Items.Add(new ShellItemDef { Code = I(r), Name = S(r + 4), Price = I(r + 8) });
            }
            for (uint i = 0; i < EngineCount; i++)
            {
                uint r = EngineVa + i * 0x10;
                t.Engines.Add(new ShellEngineDef { Rating = I(r), Mass = I(r + 4), Maker = S(r + 8), Price = I(r + 0xc) });
            }
            for (uint i = 0; i < StructureCount; i++)
            {
                uint r = StructureVa + i * 0x10;
                t.Structure.Add(new ShellStructureDef { CenterTorso = I(r), SideTorso = I(r + 4), Arm = I(r + 8), Leg = I(r + 0xc) });
            }
            for (uint i = 0; i < PlanetCount; i++) t.Planets.Add(S(PlanetVa + i * 4));

            t.ItemBefore = new ShellItemDef { Code = I(ItemVa - 0xc), Name = "", Price = I(ItemVa - 4) };
            uint sb = StructureVa - 0x10;
            t.StructureBefore = new ShellStructureDef { CenterTorso = I(sb), SideTorso = I(sb + 4), Arm = I(sb + 8), Leg = I(sb + 0xc) };
            for (uint k = 0; k < WeaponsAfterCount; k++)
            {
                uint r = WeaponVa + (WeaponCount + k) * 0x40;
                t.WeaponsAfter.Add(new ShellWeaponDef
                {
                    Heat = I(r), Damage = I(r + 4), Field08 = I(r + 8), Field0C = I(r + 0xc), Field10 = I(r + 0x10),
                    Range = I(r + 0x14), Mass = I(r + 0x18), Slots = I(r + 0x1c), ShotsPerTon = I(r + 0x20), Name = "",
                    Field28 = I(r + 0x28), File = "", Year = I(r + 0x30), Price = I(r + 0x34), AmmoPrice = I(r + 0x38),
                    Technology = I(r + 0x3c),
                });
            }
            return t;
        }
    }
}
