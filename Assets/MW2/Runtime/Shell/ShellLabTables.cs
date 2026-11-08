using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using MW2.Formats;

namespace MW2.Shell
{
    /// <summary>
    /// MechLab weapon table 0x100a96f8 (FUN_10044190), 0x18 bytes per weapon up to a null name. FUN_10044190 reads its
    /// mass at 0x100a9704 (+0xc), slots 0x100a9706, name 0x100a970a and type 0x100a970e; the six shorts before the mass
    /// are the weapon's own figures (the first record holds LRM 20's, as the shell weapon table 0x100ac088 does).
    /// </summary>
    [Serializable]
    public sealed class LabWeaponDef
    {
        public int Heat;           // +0x00 s16
        public int Damage;         // +0x02 s16; negative per missile
        public int Field04, Field06, Field08; // +0x04..+0x08 s16, as +0x08..+0x10 of the shell weapon record
        public int Range;          // +0x0a s16
        public int Mass;           // +0x0c s16: tons x 100
        public int Slots;          // +0x0e s16
        public int ShotsPerTon;    // +0x10 s16
        public string Name;        // +0x12 pointer; the list ends at a null one
        public int Weapon;         // +0x16 s16: weapon type; the check's code index is 2 x this
    }

    /// <summary>MechLab equipment table 0x100a9050 (0x10 per entry up to code -1).</summary>
    [Serializable]
    public sealed class LabEquipmentDef
    {
        public int Code;           // +0x0: critical code; the check indexes by code / 0x32
        public int Mass;           // +0x4: tons x 100
        public int Slots;          // +0x8
        public int Minimum;        // +0xc: how many the chassis must carry ("Missing some mandatory components")
    }

    /// <summary>MechLab engine table 0x100a9338 (0xc per 5-point rating step from 10; the check reads +4 at 0x100a933c).</summary>
    [Serializable]
    public sealed class LabEngineDef
    {
        public int Rating;         // +0x0
        public int Mass;           // +0x4: tons x 100
        public string Maker;       // +0x8
    }

    /// <summary>The mandatory criticals of one section (0x100a8ed0 + section x 0x30): 12 codes, -1 none, bit 31 mandatory.</summary>
    [Serializable]
    public sealed class LabSectionTemplate
    {
        public int[] Slots = new int[12];
    }

    /// <summary>A name FUN_10044190 gives a code index (the 0x100d09c0 table it fills each call).</summary>
    [Serializable]
    public sealed class LabName
    {
        public int Index;
        public string Name;
    }

    /// <summary>The strings FUN_10044190 returns or formats, by meaning.</summary>
    [Serializable]
    public sealed class LabMessages
    {
        public string ExemptChassis;        // 0x100aa40c "Elemental": refused before any other test
        public string ExemptChassisRefused; // 0x100aa418
        public string DefaultName;          // 0x100aa444 "???": every code index starts with it
        public string AmmoFor;              // 0x100aa720 "ammo for %s": names 200.. from the weapon names
        public string NegativeCount;        // 0x100aa72c
        public string WrongChassisMass;     // 0x100aa74c
        public string IllegalTonnage;       // 0x100aa774
        public string WrongSlotCount;       // 0x100aa78c
        public string OlderVersion;         // 0x100aa7b8
        public string MissingMandatory;     // 0x100aa7ec "%s in %s": item, section
        public string TooMuchArmor;         // 0x100aa814
        public string RearArmor;            // 0x100aa838
        public string UnknownCodes;         // 0x100aa864
        public string UnknownPrefix;        // 0x100aa888 "???": a name starting with it is unknown equipment
        public string UnknownEquipment;     // 0x100aa88c
        public string TooManyHeadLegSlots;  // 0x100aa8fc
        public string EngineSlots;          // 0x100aa928
        public string FerroSlots;           // 0x100aa958
        public string EndoSlots;            // 0x100aa990
        public string EngineRatingTooHigh;  // 0x100aa9c8
        public string MixedHeatSinks;       // 0x100aaa10
        public string Overweight;           // 0x100aaa44
        public string UnknownWeapon;        // 0x100aaa70
        public string IllegalWeaponCount;   // 0x100aaa94 "%s"
        public string IllegalAmmo;          // 0x100aaab4
        public string IllegalJumpJets;      // 0x100aab2c
        public string JumpOverWalk;         // 0x100aab74
        public string MissingComponents;    // 0x100aabac "%s"
        public string WeaponDenied;         // 0x100aabd4 "%s"
        public string AmmoDenied;           // 0x100aac04 "%s"
    }

    /// <summary>
    /// The tables of the MechLab check (FUN_10044190 0x10044190) from MW2SHELL.DLL as one extracted file: messages, the
    /// names it gives code indexes, its weapon / equipment / engine tables, the internal structure by tonnage
    /// (0x100a91e8 + tons / 5 x 0x10, i.e. rows from 10 tons right after the eight section names at 0x100a91e8) and the
    /// mandatory criticals per section. <see cref="Load"/> reads the library, <see cref="Get"/> the store.
    /// </summary>
    [Serializable]
    public sealed class ShellLabTables
    {
        public const string TableName = "shell-mechlab";
        public const int CurrentFormat = 1;

        /// <summary>FUN_10044190's assignments into 0x100d09c0: code index and the string's address.</summary>
        static readonly uint[,] NameAddresses =
        {
            {0x0, 0x100aa448}, {0x2, 0x100aa450}, {0x4, 0x100aa458}, {0x6, 0x100aa460}, {0x8, 0x100aa468},
            {0xa, 0x100aa470}, {0xc, 0x100aa478}, {0xe, 0x100aa480}, {0x10, 0x100aa488}, {0x12, 0x100aa490},
            {0x14, 0x100aa498}, {0x16, 0x100aa4a0}, {0x18, 0x100aa4a8}, {0x1a, 0x100aa4b0}, {0x1c, 0x100aa4b8},
            {0x1e, 0x100aa4c0}, {0x20, 0x100aa4c8}, {0x22, 0x100aa4d0}, {0x24, 0x100aa4d8}, {0x26, 0x100aa4e0},
            {0x28, 0x100aa4e8}, {0x2a, 0x100aa4f0}, {0x2c, 0x100aa4f4}, {0x2e, 0x100aa4fc}, {0x30, 0x100aa504},
            {0x32, 0x100aa50c}, {0x34, 0x100aa514}, {0x36, 0x100aa51c}, {0x38, 0x100aa524}, {0x3a, 0x100aa52c},
            {0x3c, 0x100aa530}, {0x3e, 0x100aa538}, {0x40, 0x100aa540}, {0x42, 0x100aa548}, {0x44, 0x100aa554},
            {0x46, 0x100aa55c}, {0x48, 0x100aa564}, {0x4a, 0x100aa56c}, {0x4c, 0x100aa574}, {0x4e, 0x100aa57c},
            {0x50, 0x100aa584}, {0x52, 0x100aa58c}, {0x54, 0x100aa594}, {0x56, 0x100aa59c}, {0x58, 0x100aa5a4},
            {0x5a, 0x100aa5ac}, {0x5c, 0x100aa5b4}, {0x5e, 0x100aa5bc}, {0x60, 0x100aa5c4}, {0x62, 0x100aa5cc},
            {0x64, 0x100aa5d4}, {0x66, 0x100aa5dc}, {0x68, 0x100aa5e4}, {0x6a, 0x100aa5ec}, {0x6c, 0x100aa5f4},
            {0x6e, 0x100aa5fc}, {0x70, 0x100aa604}, {0x72, 0x100aa60c}, {0x74, 0x100aa614}, {0x76, 0x100aa61c},
            {0x78, 0x100aa624}, {0x7a, 0x100aa62c}, {0x7c, 0x100aa634}, {0x7e, 0x100aa63c}, {0x80, 0x100aa648},
            {0x82, 0x100aa658}, {0x84, 0x100aa664}, {0x8c, 0x100aa66c}, {0x8d, 0x100aa674}, {0x8e, 0x100aa67c},
            {0x8f, 0x100aa680}, {0x90, 0x100aa688}, {0x91, 0x100aa68c}, {0x92, 0x100aa690}, {0x93, 0x100aa698},
            {0x94, 0x100aa6a0}, {0x95, 0x100aa6a8}, {0x96, 0x100aa6b0}, {0x97, 0x100aa6b4}, {0x98, 0x100aa6bc},
            {0x99, 0x100aa6c4}, {0x9a, 0x100aa6cc}, {0x9b, 0x100aa6d4}, {0x9c, 0x100aa6dc}, {0x9d, 0x100aa6e4},
            {0x9e, 0x100aa6ec}, {0xa0, 0x100aa6f8}, {0xaa, 0x100aa700}, {0xb4, 0x100aa708}, {0xbe, 0x100aa710},
            {0xbf, 0x100aa718}
        };

        public const uint WeaponVa = 0x100a96f8, EquipmentVa = 0x100a9050, EngineVa = 0x100a9338, TemplateVa = 0x100a8ed0,
            SectionNameVa = 0x100a91e8;
        public const int EngineCount = 79; // ratings 10 .. 400; the check never reads past 400

        public int Format = CurrentFormat;
        public LabMessages Messages = new LabMessages();
        public List<LabName> Names = new List<LabName>();
        public List<LabWeaponDef> Weapons = new List<LabWeaponDef>();
        public List<LabEquipmentDef> Equipment = new List<LabEquipmentDef>();
        public List<LabEngineDef> Engines = new List<LabEngineDef>();
        /// <summary>Row 0 is 10 tons (0x100a91e8 + 2 x 0x10), row 18 is 100 tons.</summary>
        public List<ShellStructureDef> Structure = new List<ShellStructureDef>();
        public List<string> SectionNames = new List<string>();
        public List<LabSectionTemplate> Templates = new List<LabSectionTemplate>();

        /// <summary>Internal structure of a legal tonnage (10..100, a multiple of 5).</summary>
        public ShellStructureDef StructureFor(int tons) => Structure[tons / 5 - 2];

        public static ShellLabTables Get(string gameFolder, TableStore store = null)
        {
            if (store == null) store = TableStore.Default;
            var t = store.Extract(TableName, () => Load(Path.Combine(gameFolder, "MW2SHELL.DLL")));
            if (t.Format != CurrentFormat)
                throw new InvalidDataException($"{store.PathFor(TableName)} has format {t.Format}, this build reads " +
                    $"{CurrentFormat}: move it aside (keeping your edits) and run MW2 -> Tables -> Extract missing.");
            return t;
        }

        public static ShellLabTables Load(string shellDll)
        {
            byte[] d = File.ReadAllBytes(shellDll);
            int I(uint va) => BitConverter.ToInt32(d, PeImage.FileOffset(d, va));
            int H(uint va) => BitConverter.ToInt16(d, PeImage.FileOffset(d, va));
            string Lit(uint va)
            {
                if (PeImage.IsZeroFill(d, va)) return "";
                int o = PeImage.FileOffset(d, va), e = o;
                while (e < d.Length && d[e] != 0) e++;
                return Encoding.ASCII.GetString(d, o, e - o);
            }
            string S(uint va) { uint p = (uint)I(va); return p == 0 ? "" : Lit(p); }

            var t = new ShellLabTables();
            var m = t.Messages;
            m.ExemptChassis = Lit(0x100aa40c); m.ExemptChassisRefused = Lit(0x100aa418); m.DefaultName = Lit(0x100aa444);
            m.AmmoFor = Lit(0x100aa720); m.NegativeCount = Lit(0x100aa72c); m.WrongChassisMass = Lit(0x100aa74c);
            m.IllegalTonnage = Lit(0x100aa774); m.WrongSlotCount = Lit(0x100aa78c); m.OlderVersion = Lit(0x100aa7b8);
            m.MissingMandatory = Lit(0x100aa7ec); m.TooMuchArmor = Lit(0x100aa814); m.RearArmor = Lit(0x100aa838);
            m.UnknownCodes = Lit(0x100aa864); m.UnknownPrefix = Lit(0x100aa888); m.UnknownEquipment = Lit(0x100aa88c);
            m.TooManyHeadLegSlots = Lit(0x100aa8fc); m.EngineSlots = Lit(0x100aa928); m.FerroSlots = Lit(0x100aa958);
            m.EndoSlots = Lit(0x100aa990); m.EngineRatingTooHigh = Lit(0x100aa9c8); m.MixedHeatSinks = Lit(0x100aaa10);
            m.Overweight = Lit(0x100aaa44); m.UnknownWeapon = Lit(0x100aaa70); m.IllegalWeaponCount = Lit(0x100aaa94);
            m.IllegalAmmo = Lit(0x100aaab4); m.IllegalJumpJets = Lit(0x100aab2c); m.JumpOverWalk = Lit(0x100aab74);
            m.MissingComponents = Lit(0x100aabac); m.WeaponDenied = Lit(0x100aabd4); m.AmmoDenied = Lit(0x100aac04);

            for (int i = 0; i < NameAddresses.GetLength(0); i++)
                t.Names.Add(new LabName { Index = (int)NameAddresses[i, 0], Name = Lit(NameAddresses[i, 1]) });
            for (uint i = 0; I(WeaponVa + i * 0x18 + 0x12) != 0; i++)
            {
                uint r = WeaponVa + i * 0x18;
                t.Weapons.Add(new LabWeaponDef
                {
                    Heat = H(r), Damage = H(r + 2), Field04 = H(r + 4), Field06 = H(r + 6), Field08 = H(r + 8),
                    Range = H(r + 0xa), Mass = H(r + 0xc), Slots = H(r + 0xe), ShotsPerTon = H(r + 0x10),
                    Name = S(r + 0x12), Weapon = H(r + 0x16),
                });
            }
            for (uint i = 0; I(EquipmentVa + i * 0x10) != -1; i++)
            {
                uint r = EquipmentVa + i * 0x10;
                t.Equipment.Add(new LabEquipmentDef { Code = I(r), Mass = I(r + 4), Slots = I(r + 8), Minimum = I(r + 0xc) });
            }
            for (uint i = 0; i < EngineCount; i++)
            {
                uint r = EngineVa + i * 0xc;
                t.Engines.Add(new LabEngineDef { Rating = I(r), Mass = I(r + 4), Maker = S(r + 8) });
            }
            for (uint tons = 10; tons <= 100; tons += 5)
            {
                uint r = SectionNameVa + tons / 5 * 0x10;
                t.Structure.Add(new ShellStructureDef { CenterTorso = I(r), SideTorso = I(r + 4), Arm = I(r + 8), Leg = I(r + 0xc) });
            }
            for (uint s = 0; s < 8; s++) t.SectionNames.Add(S(SectionNameVa + s * 4));
            for (uint s = 0; s < 8; s++)
            {
                var tpl = new LabSectionTemplate();
                for (uint j = 0; j < 12; j++) tpl.Slots[j] = I(TemplateVa + s * 0x30 + j * 4);
                t.Templates.Add(tpl);
            }
            return t;
        }
    }
}
