using System;
using System.Collections.Generic;
using System.Text;

namespace MW2.Formats
{
    /// <summary>
    /// Mercenaries campaign step: a BWD resource with a CPGN chunk (MW2SHELL.DLL Campaign_Load 0x1006b879). The loader walks the
    /// chunks after REV/DTBL; CPGN holds a fixed 0x98-byte body (the loader steps 0xa0 past its tag), and the chunks behind it are
    /// read as siblings: SLST/FLST (next steps), MHLT (mechs for sale), WNLT/AMMO/EQUP (arms dealer stock), SWNL/SMHL (items and
    /// mechs added to the unit), PTLT/THLT (pilots and techs for hire), NEWS and CTNT (texts). The shell's own compiler
    /// (0x1008ceb2) gives the keyword names; the shipped data was built by a later compiler (AMMO/EQUP and the year fields exist only there).
    /// Career_New 0x1006cbb4 starts a novice career with NOVICE and a regular one with CAMP.
    /// </summary>
    public sealed class Campaign
    {
        public string Name;
        /// <summary>CPGN +0x00 "campaign" -> state +0x1fe: 1 contract mission, 2 last mission of a contract, 3 career start,
        /// 4 career end (Career_End 0x1001ec9a), 0 in the unused instant-action steps (the shell rejects it, 0x1001e89a).</summary>
        public int Type;
        /// <summary>+0x04 -> state +0x122 (current year), +0x08 month index (0x100bf9d0 table) -> state +0x126.</summary>
        public int Year, Month;
        /// <summary>+0x0c mission -> state +0x166 (a RAN* step uses "ran%dscn1" with the 4th letter of its name).</summary>
        public string Mission;
        /// <summary>+0x1c newsflic -> +0x176, +0x2c newsbmp -> +0x186, +0x3c starmap -> +0x196.</summary>
        public string NewsFlic, NewsBmp, Starmap;
        /// <summary>+0x4c -> +0x1b6, which the loader at once overwrites with the empty string at 0x100f94bc.</summary>
        public string Field4C;
        /// <summary>+0x5c successpay -> +0x1e6, +0x60 failpay -> +0x1ea, +0x64 score (the loader does not read it).</summary>
        public int SuccessPay, FailPay, Score;
        /// <summary>+0x68 -> +0x1fa: multiplier of the unit's expenses (Expenses 0x1001b6ff).</summary>
        public int ExpenseMultiplier;
        /// <summary>+0x6c -> +0x1d6: the step loaded when the player aborts the contract (Contract_Abort 0x10023f12, "Are you
        /// sure") or the unit has no mech left (Unit_TooDamaged 0x100202e9); "DEATH" forbids aborting / ends the career.</summary>
        public string AbortCampaign;
        /// <summary>+0x7c -> +0x1ee paid per completed "Target" objective, +0x80 -> +0x1f2 per "Bonus" objective, +0x84 ->
        /// +0x1f6 per "Bounty" objective and per confirmed kill (Debrief_Objectives 0x1001bb15).</summary>
        public int TargetPay, BonusPay, BountyPay;
        /// <summary>+0x88 -> +0x1c6: employer (draconis, comstar, fedcom, freerass).</summary>
        public string Employer;

        /// <summary>SLST -> state +0x202 (count +0x2392), FLST -> +0x2a2 (count +0x2396): 16-byte names.</summary>
        public readonly List<string> SuccessList = new List<string>(), FailList = new List<string>();
        /// <summary>MHLT, 0x18 bytes: MEK name, +0x10 -> mech +0x998, +0x14 tech year -> +0x9c3 (lists 0x100cd11c/0x100cd120).</summary>
        public readonly List<MechEntry> Mechs = new List<MechEntry>();
        /// <summary>SMHL, 0x1c bytes: MEK name, +0x10 salvage chance -> +0x9c7, +0x14 -> +0x998, +0x18 tech year -> +0x9c3 (0x100cd130).</summary>
        public readonly List<MechEntry> UnitMechs = new List<MechEntry>();
        /// <summary>WNLT, 12 bytes: weapon index, price, year (item +0xe) -> 0x100cd14c.</summary>
        public readonly List<int[]> Weapons = new List<int[]>();
        /// <summary>AMMO, 16 bytes: weapon index, count of lots, price, year -> 0x100cd150.</summary>
        public readonly List<int[]> Ammo = new List<int[]>();
        /// <summary>EQUP, 20 bytes: equipment index, price, item +0xa, +0xe, +0x12 -> 0x100cd14c.</summary>
        public readonly List<int[]> Equipment = new List<int[]>();
        /// <summary>SWNL, 16 bytes: coded item (FUN_1004a6c4: below 7000 weapon*100, below 10000 equipment, else ammo), value
        /// -> item +0x12 (salvage chance, FUN_1001ce81), price, year -> 0x100cd12c.</summary>
        public readonly List<int[]> UnitItems = new List<int[]>();
        /// <summary>PTLT / THLT: 16-byte names of pilot / tech BWDs (0x100cd164 / 0x100cd168).</summary>
        public readonly List<string> Pilots = new List<string>(), Techs = new List<string>();
        /// <summary>NEWS -> state +0x342, CTNT -> state +0x1342 (length - 1 bytes, zero-terminated).</summary>
        public string News, Contract;

        /// <summary>Chunk tags in file order (Campaign_Load acts on them in this order; mech ids depend on it).</summary>
        public readonly List<string> ChunkOrder = new List<string>();

        public sealed class MechEntry
        {
            public string Mek;
            public int Value, TechYear, SalvageChance;
        }

        public static bool Has(byte[] bwd)
        {
            foreach (var (tag, _, _) in BwdChunks.Enumerate(bwd))
                if (tag == "CPGN") return true;
            return false;
        }

        /// <summary>Campaign_Load 0x1006b879: walk from 0xc, CPGN advances 0xa0, every other chunk its length.</summary>
        public static Campaign Decode(string name, byte[] d)
        {
            var c = new Campaign { Name = name };
            int total = BitConverter.ToInt32(d, 4);
            int pos = 0xc;
            bool found = false;
            while (pos + 8 <= Math.Min(total, d.Length))
            {
                string tag = Encoding.ASCII.GetString(d, pos, 4).TrimEnd('\0');
                int length = BitConverter.ToInt32(d, pos + 4);
                int b = pos + 8;
                int n(int size) => (length - 8) / size;
                int I(int off) => BitConverter.ToInt32(d, off);
                c.ChunkOrder.Add(tag);
                string S(int off) => BwdChunks.ReadString(d, off, 16);
                switch (tag)
                {
                    case "CPGN":
                        found = true;
                        c.Type = I(b); c.Year = I(b + 4); c.Month = I(b + 8);
                        c.Mission = S(b + 0x0c); c.NewsFlic = S(b + 0x1c); c.NewsBmp = S(b + 0x2c); c.Starmap = S(b + 0x3c);
                        c.Field4C = S(b + 0x4c);
                        c.SuccessPay = I(b + 0x5c); c.FailPay = I(b + 0x60); c.Score = I(b + 0x64); c.ExpenseMultiplier = I(b + 0x68);
                        c.AbortCampaign = S(b + 0x6c);
                        c.TargetPay = I(b + 0x7c); c.BonusPay = I(b + 0x80); c.BountyPay = I(b + 0x84);
                        c.Employer = S(b + 0x88);
                        pos += 0xa0;
                        continue;
                    case "SLST": for (int k = 0; k < (length - 8) >> 4; k++) c.SuccessList.Add(S(b + k * 16)); break;
                    case "FLST": for (int k = 0; k < (length - 8) >> 4; k++) c.FailList.Add(S(b + k * 16)); break;
                    case "PTLT": for (int k = 0; k < (length - 8) >> 4; k++) c.Pilots.Add(S(b + k * 16)); break;
                    case "THLT": for (int k = 0; k < (length - 8) >> 4; k++) c.Techs.Add(S(b + k * 16)); break;
                    case "MHLT":
                        for (int k = 0; k < n(0x18); k++)
                            c.Mechs.Add(new MechEntry { Mek = S(b + k * 0x18), Value = I(b + k * 0x18 + 0x10), TechYear = I(b + k * 0x18 + 0x14) });
                        break;
                    case "SMHL":
                        for (int k = 0; k < n(0x1c); k++)
                            c.UnitMechs.Add(new MechEntry
                            {
                                Mek = S(b + k * 0x1c), SalvageChance = I(b + k * 0x1c + 0x10), Value = I(b + k * 0x1c + 0x14),
                                TechYear = I(b + k * 0x1c + 0x18)
                            });
                        break;
                    case "WNLT": Ints(c.Weapons, d, b, n(12), 3); break;
                    case "AMMO": Ints(c.Ammo, d, b, (length - 8) >> 4, 4); break;
                    case "EQUP": Ints(c.Equipment, d, b, n(20), 5); break;
                    case "SWNL": Ints(c.UnitItems, d, b, (length - 8) >> 4, 4); break;
                    case "NEWS": c.News = Text(d, b, length); break;
                    case "CTNT": c.Contract = Text(d, b, length); break;
                }
                pos += length;
            }
            return found ? c : null;
        }

        /// <summary>BET4CAM1's EQUP claims 308 bytes but the resource ends 4 bytes into it (the original then reads past its buffer):
        /// only whole entries inside the resource are taken.</summary>
        static void Ints(List<int[]> list, byte[] d, int b, int count, int fields)
        {
            count = Math.Min(count, Math.Max(0, (d.Length - b) / (fields * 4)));
            for (int k = 0; k < count; k++)
            {
                var e = new int[fields];
                for (int f = 0; f < fields; f++) e[f] = BitConverter.ToInt32(d, b + (k * fields + f) * 4);
                list.Add(e);
            }
        }

        /// <summary>memcpy(length - 1) from the body, then a zero at [length]: the text ends at its first zero.</summary>
        static string Text(byte[] d, int b, int length) => BwdChunks.ReadString(d, b, Math.Max(0, length - 1));

        /// <summary>FUN_1004a6c4: kind of a coded item (0 weapon, 1 ammunition, 2 equipment, 3 none).</summary>
        public static int ItemKind(int code) => code == -1 || code == 0 ? 3 : code < 7000 ? 0 : code < 10000 ? 2 : 1;
    }
}
