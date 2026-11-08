using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace MW2.Shell
{
    /// <summary>
    /// The state of instant action (career mode 2; the globals of MW2SHELL.DLL around 0x100b78c8 / 0x100bb5b0 / 0x100d4650),
    /// kept for the session like the original's. Six points: 0..2 the player's lance (roster DAT_100bb5b0), 3..5 the enemy
    /// (roster DAT_100bb6c8). Each point has a chassis of the mech table 0x100aac58 (-1 none, -2 random: the generator's
    /// first enemy star MEK) or, when its variants are on (DAT_100d4680), one of its variants: "&lt;code&gt;NNstd" MEKs of
    /// MW2.PRJ (1..49) and the user's "&lt;code&gt;NNusr.mek" (FUN_10054047). A roster (0x114 bytes, part of mw2prm.cfg):
    /// +0 message, +4 current slot, +8 slots (up to 3), +0xc slots in use, +0x10 heaviest chassis allowed; 0x40 per slot
    /// from +0x14: chassis index, +4 MEK (0x10), +0x14 pilot (0x16), +0x2a third name (0x16). Changing a point rewrites
    /// userstar.bwd (FUN_100748e8) or the enemy stars en01..05star.bwd (FUN_10074909) beside the game (here in memory).
    /// User variants live in the "mek" folder of Unity's user-data folder (the game's mek folder is read too).
    /// </summary>
    public static class InstantAction
    {
        public const int Points = 6, LastChassis = 0x3d;
        public static readonly int[] Chassis = new int[Points];                 // list +0x28
        public static readonly bool[] VariantOn = new bool[Points];             // DAT_100d4680
        public static readonly int[] VariantIndex = new int[Points];            // DAT_100d8498
        public static readonly List<string>[] Variants = new List<string>[Points];  // DAT_100d47a8 (+ count DAT_100d4668)
        public static readonly MechRecord[] VariantMech = new MechRecord[Points];   // DAT_100d4650
        public static int Selected;                                              // DAT_100d84d4
        public static MechRecord LabMech;                                       // DAT_100d84d0
        /// <summary>DAT_100cc7a8: the generator's parameters (mw2rmg.cfg).</summary>
        public static RandomMissionParams Params = new RandomMissionParams();
        public static readonly byte[][] Rosters = new byte[2][];
        static int _roster;                                                      // PTR_DAT_100bb7dc
        static bool _ready;

        /// <summary>Unity's user-data folder (set by the shell); its "mek" folder holds the user variants.</summary>
        public static string UserFolder;
        public static string UserMekFolder => Path.Combine(UserFolder ?? "", "mek");

        /// <summary>The .data values of the lists and rosters, once a session.</summary>
        public static void Init(ShellTables t)
        {
            if (_ready) return;
            _ready = true;
            for (int i = 0; i < Points; i++)
            {
                Chassis[i] = t.Instant.PointChassis[i]; // 0x100b78c8 + i x 0x38 + 0x28
                Variants[i] = new List<string>();
            }
            Rosters[0] = t.Instant.Rosters[0].ToBytes(); // DAT_100bb5b0
            Rosters[1] = t.Instant.Rosters[1].ToBytes(); // DAT_100bb6c8
        }

        static int I32(byte[] d, int o) => BitConverter.ToInt32(d, o);
        static void Set32(byte[] d, int o, int v) { var b = BitConverter.GetBytes(v); Buffer.BlockCopy(b, 0, d, o, 4); }
        static string Str(byte[] d, int o, int max)
        {
            int e = o;
            while (e < o + max && d[e] != 0) e++;
            return Encoding.ASCII.GetString(d, o, e - o);
        }

        public static int SlotChassis(int roster, int slot) => I32(Rosters[roster], 0x14 + slot * 0x40);
        public static string SlotMek(int roster, int slot) => Str(Rosters[roster], 0x18 + slot * 0x40, 0x10);
        public static string SlotPilot(int roster, int slot) => Str(Rosters[roster], 0x28 + slot * 0x40, 0x16);
        public static string SlotThird(int roster, int slot) => Str(Rosters[roster], 0x3e + slot * 0x40, 0x16);

        /// <summary>FUN_1007484a 0x1007484a: choose a roster, then set +0 / +8 (≤ 3) / +0xc (≤ 3) / +0x10 when not negative.</summary>
        public static void Select(int roster, int message = -1, int slots = -1, int inUse = -1, int tons = -1)
        {
            if (roster >= 0) _roster = roster;
            var r = Rosters[_roster];
            if (message >= 0) Set32(r, 0, message);
            if (slots >= 0) Set32(r, 8, Math.Min(slots, 3));
            if (inUse >= 0) Set32(r, 0xc, Math.Min(inUse, 3));
            if (tons >= 0) Set32(r, 0x10, tons);
        }

        /// <summary>
        /// FUN_10074511 0x10074511: the slot's MEK (a three-letter code gets "00std") and chassis by its first three letters,
        /// not heavier than +0x10; then the slots in use counted again. An unknown MEK of the last slot in use lowers the
        /// count instead.
        /// </summary>
        public static int SetSlot(ShellTables t, int slot, string mek)
        {
            var r = Rosters[_roster];
            if (slot < 0) slot = I32(r, 4);
            else Set32(r, 4, slot);
            if (mek != null)
            {
                int k = 0;
                while (k < ShellTables.MechCount && !string.IsNullOrEmpty(t.MechCode(k)) &&
                       !t.MechCode(k).StartsWith(mek.Length >= 3 ? mek.Substring(0, 3) : mek, StringComparison.OrdinalIgnoreCase)) k++;
                if (k >= ShellTables.MechCount || string.IsNullOrEmpty(t.MechCode(k)))
                {
                    if (slot != 0 && slot + 1 == I32(r, 0xc))
                    {
                        Set32(r, 0xc, I32(r, 0xc) - 1);
                        return -1;
                    }
                    return I32(r, 0xc) <= slot ? 1 : 0;
                }
                if (I32(r, 0x10) < t.Mech(k).Tons) return 0;
                int o = 0x18 + slot * 0x40;
                Array.Clear(r, o, 9);
                var b = Encoding.ASCII.GetBytes(mek.Length > 8 ? mek.Substring(0, 8) : mek);
                Buffer.BlockCopy(b, 0, r, o, b.Length);
                if (mek.Length == 3) Buffer.BlockCopy(Encoding.ASCII.GetBytes("00std"), 0, r, o + 3, 5);
                Set32(r, 0x14 + slot * 0x40, k);
            }
            int n = 0;
            for (int i = 0; i < I32(r, 8); i++) if (I32(r, 0x14 + i * 0x40) >= 0) n++;
            Set32(r, 0xc, n);
            return -1;
        }

        /// <summary>
        /// Case 8 of FUN_1005475d 0x1005475d, the enemy points: one whose slot holds the deleted MEK (first 8 letters) goes to
        /// random on point 3 (the random MEK set only when there is one) and to none on the others, then the enemy stars
        /// again. The player's points are compared too, but the original drops the result.
        /// </summary>
        public static void ForgetEnemyMek(ShellData data, string mek)
        {
            for (int point = 3; point < Points; point++)
            {
                int slot = point - 3;
                if (Chassis[point] < 0 || I32(Rosters[1], 0x14 + slot * 0x40) < 0) continue;
                string held = SlotMek(1, slot);
                if (string.CompareOrdinal(Prefix(held), Prefix(mek)) != 0) continue;
                Select(1);
                if (point == 3)
                {
                    Chassis[point] = -2;
                    string random = Params.Str(0x28 + slot * 0x10, 3);
                    if (random.Length != 0) SetSlot(data.Tables, slot, random);
                }
                else
                {
                    Chassis[point] = -1;
                    ClearSlot(slot);
                }
                WriteEnemyStars(data, Difficulty);
            }
        }

        static string Prefix(string s) => s.Length > 8 ? s.Substring(0, 8) : s;

        /// <summary>FUN_100747ef 0x100747ef: the slot empty (the count is not touched).</summary>
        public static void ClearSlot(int slot) => Set32(Rosters[_roster], 0x14 + slot * 0x40, -1);

        /// <summary>FUN_10074440 0x10074440: the player roster's slots in use (-1 without the player's own), the enemy's.</summary>
        public static int InUse(int roster) =>
            roster == 0 ? (I32(Rosters[0], 0x14) < 0 ? -1 : I32(Rosters[0], 0xc)) : I32(Rosters[1], 0xc);

        /// <summary>FUN_100748e8 0x100748e8 → FUN_10052829: userstar.bwd from the player roster (team 0, no pilot record).</summary>
        public static void WriteUserStar(ShellData data)
        {
            var r = Rosters[0];
            int n = I32(r, 0xc);
            if (n <= 0) return;
            var chunks = new List<byte[]>();
            int written = 0;
            for (int i = 0; i < 3; i++)
            {
                if (I32(r, 0x14 + i * 0x40) < 0) continue;
                var c = UserStar.RosterGps(SlotMek(0, i), SlotPilot(0, i), SlotThird(0, i), i, 0, 0, data.Tables, data.Prj);
                if (c != null) chunks.Add(c);
                if (++written >= n) break;
            }
            data.Files["userstar.bwd"] = UserStar.File(chunks);
        }

        /// <summary>DAT_100a4358, the shell's mw2dif.cfg block (set by the shell after it is read; null: medium).</summary>
        public static byte[] Difficulty;

        /// <summary>
        /// FUN_10074909 0x10074909 → FUN_10052829: five enemy stars en01..05star.bwd with the enemy roster, skill row 6 (7 on
        /// difficulty 0, 5 on 2 by mw2dif.cfg +5).
        /// </summary>
        public static void WriteEnemyStars(ShellData data, byte[] difficulty)
        {
            var r = Rosters[1];
            int n = I32(r, 0xc);
            if (n <= 0) return;
            int baseRow = data.Tables.Instant.BaseRow; // DAT_100b78c0
            for (int star = 1; star < 6; star++)
            {
                int row = baseRow - 2;
                if (difficulty != null)
                {
                    if (difficulty[5] == 0) row = baseRow - 1;
                    else if (difficulty[5] == 2) row = baseRow - 3;
                }
                var chunks = new List<byte[]>();
                int written = 0;
                for (int i = 0; i < 3; i++)
                {
                    if (I32(r, 0x14 + i * 0x40) < 0) continue;
                    var c = UserStar.RosterGps(SlotMek(1, i), SlotPilot(1, i), SlotThird(1, i), i, star, row, data.Tables, data.Prj);
                    if (c != null) chunks.Add(c);
                    if (++written >= n) break;
                }
                data.Files[$"en{star:00}star.bwd"] = UserStar.File(chunks);
            }
        }

        /// <summary>The launch entries of the player roster's slots in use (FUN_1004b33c passes mw2prm.cfg to the engine).</summary>
        public static List<Career.LaunchEntry> LaunchRoster(ShellData data)
        {
            var roster = new List<Career.LaunchEntry>();
            for (int i = 0; i < 3; i++)
            {
                int chassis = SlotChassis(0, i);
                if (chassis < 0) continue;
                string mek = SlotMek(0, i);
                roster.Add(new Career.LaunchEntry
                {
                    Slot = i, MekName = mek, PilotName = SlotPilot(0, i), Model = data.Tables.MechModel(chassis), MekData = LoadMek(data, mek)
                });
            }
            return roster;
        }

        /// <summary>A MEK by name: "...std" from MW2.PRJ, else the user's mek folder, else the game's (Mech_FromMek's search).</summary>
        public static byte[] LoadMek(ShellData data, string name)
        {
            if (name.Length >= 8 && string.Compare(name, 5, "std", 0, 3, StringComparison.OrdinalIgnoreCase) == 0)
                return data.ReadMek(name.Substring(0, 8));
            string file = name.EndsWith(".mek", StringComparison.OrdinalIgnoreCase) ? name : name + ".mek";
            string own = UserFolder == null ? null : Path.Combine(UserMekFolder, file);
            if (own != null && File.Exists(own)) return File.ReadAllBytes(own);
            return MechRecord.LoadMek(name, data.ReadMek, data.GameFolder);
        }

        public static MechRecord FromMek(ShellData data, string name, Career c) =>
            MechRecord.FromMek(name, LoadMek(data, name), data.Tables, c.NextId);

        /// <summary>FUN_10054047 0x10054047: the variants of a chassis code: "&lt;code&gt;NNstd" of MW2.PRJ for 1..49, then
        /// the user's "&lt;code&gt;??usr.mek" files (name up to 8 letters).</summary>
        public static void FillVariants(ShellData data, string code, int point)
        {
            var list = Variants[point];
            list.Clear();
            for (int i = 1; i < 0x32; i++)
            {
                string name = $"{code}{i:00}std";
                if (data.Prj.TryFind("MEK", name, out _)) list.Add(name);
            }
            var files = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var folder in new[] { UserFolder == null ? null : UserMekFolder, Path.Combine(data.GameFolder ?? "", "mek") })
                if (folder != null && Directory.Exists(folder))
                    foreach (var f in Directory.GetFiles(folder, code + "??usr.mek"))
                    {
                        string n = Path.GetFileNameWithoutExtension(f);
                        files.Add(n.Length > 8 ? n.Substring(0, 8) : n);
                    }
            list.AddRange(files);
        }

        /// <summary>FUN_10053f97 0x10053f97: one more than the highest NN of the user's "&lt;code&gt;NNusr.mek" files.</summary>
        public static int NextUserNumber(ShellData data, string code)
        {
            int last = -1;
            foreach (var folder in new[] { UserFolder == null ? null : UserMekFolder, Path.Combine(data.GameFolder ?? "", "mek") })
                if (folder != null && Directory.Exists(folder))
                    foreach (var f in Directory.GetFiles(folder, code + "??usr.mek"))
                    {
                        string n = Path.GetFileName(f);
                        if (n.Length >= 5 && char.IsDigit(n[3]) && char.IsDigit(n[4])) last = Math.Max(last, (n[3] - '0') * 10 + n[4] - '0');
                    }
            return last + 1;
        }

        static string Code(ShellTables t, int chassis) => chassis >= 0 ? t.MechCode(chassis) : "";

        /// <summary>The point's slot in its roster from its chassis or variant (the switches of FUN_10054234 / FUN_10053192).</summary>
        static void ApplyPoint(ShellData data, int point, bool variant)
        {
            var t = data.Tables;
            int c = Chassis[point];
            if (point < 3)
            {
                Select(0);
                if (c < 0) ClearSlot(point);
                else SetSlot(t, point, variant ? Variants[point][VariantIndex[point]] : Code(t, c));
                WriteUserStar(data);
            }
            else
            {
                Select(1);
                int slot = point - 3;
                if (c < 0)
                {
                    if (c == -1) ClearSlot(slot);
                    else
                    {
                        string random = Params.Str(0x28 + slot * 0x10, 3);
                        if (random.Length == 0) ClearSlot(slot);
                        else SetSlot(t, slot, random);
                    }
                }
                else SetSlot(t, slot, variant ? Variants[point][VariantIndex[point]] : Code(t, c));
                WriteEnemyStars(data, Difficulty);
            }
        }

        /// <summary>
        /// FUN_10054234 0x10054234: <paramref name="variantStep"/> moves through the point's variants (turning them on from
        /// the chassis' list; past the first they go off), otherwise <paramref name="chassisStep"/> moves through the
        /// chassis (-1 none, players; -2 random, enemies; point 3 skips none) wrapping at 0x3d; the roster follows.
        /// Returns the chassis code to show (null: no change of picture).
        /// </summary>
        public static string Step(ShellData data, int point, int chassisStep, int variantStep)
        {
            var t = data.Tables;
            if (variantStep != 0)
            {
                if (!VariantOn[point])
                {
                    if (variantStep < 0) return null;
                    if (Chassis[point] >= 0) FillVariants(data, Code(t, Chassis[point]), point);
                    if (Variants[point].Count == 0)
                    {
                        VariantOn[point] = false;
                        VariantIndex[point] = 0;
                        VariantMech[point] = null;
                        return null;
                    }
                    VariantOn[point] = true;
                    VariantIndex[point] = 0;
                    ApplyPoint(data, point, true);
                    return null;
                }
                VariantIndex[point] += variantStep;
                if (VariantIndex[point] >= 0)
                {
                    if (VariantIndex[point] >= Variants[point].Count) VariantIndex[point] = Variants[point].Count - 1;
                    ApplyPoint(data, point, true);
                    return null;
                }
                chassisStep = 0;
            }
            else if (chassisStep == 0) return null;
            VariantOn[point] = false;
            VariantIndex[point] = 0;
            Variants[point].Clear();
            VariantMech[point] = null;
            int c = Chassis[point] + chassisStep;
            if (point == 3 && c == -1) c = chassisStep < 0 ? -2 : 0;
            if (point < 3)
            {
                if (c < -1) c = LastChassis;
                else if (c > LastChassis) c = -1;
            }
            else if (c < -2) c = LastChassis;
            else if (c > LastChassis) c = -2;
            Chassis[point] = c;
            ApplyPoint(data, point, false);
            return c < 0 ? "" : Code(t, c);
        }

        /// <summary>FUN_10053192 0x10053192: every point's slot from its state, then userstar and the enemy stars.</summary>
        public static void ApplyAll(ShellData data)
        {
            for (int p = 0; p < Points; p++) ApplyPoint(data, p, VariantOn[p] && Chassis[p] >= 0 && Variants[p].Count > 0);
        }

        /// <summary>
        /// FUN_100533f3 0x100533f3, run when the screen is set up. Coming back from Customizing (0xc02f) with a MechLab mech
        /// (DAT_100d84d0): a selected point without variants gets the chassis' list (index 0); otherwise the mech is added
        /// to the point's list at the end, becomes its variant (FUN_10054234 steps to it) and the point's roster slot
        /// follows. Every other point with the same chassis (first three letters of its first variant) gets the new name too,
        /// and the selected point's loaded mech is then read again from that point's current variant (as the original does).
        /// From the title or a mission (0xc02b / 0xc029) the variants are forgotten and point 0 selected. The mech is let go.
        /// </summary>
        public static void Enter(uint from, ShellData data, Career career)
        {
            if (from == 0xc02f && LabMech != null)
            {
                int sel = Selected;
                if (!VariantOn[sel])
                {
                    FillVariants(data, Code(data.Tables, Chassis[sel]), sel);
                    VariantOn[sel] = true;
                    VariantIndex[sel] = 0;
                }
                else
                {
                    VariantIndex[sel] = Variants[sel].Count;
                    VariantMech[sel] = LabMech;
                    Variants[sel].Add(LabMech.MekName);
                    Step(data, sel, 0, 1);
                }
                for (int q = 0; q < Points; q++)
                {
                    if (q == sel || VariantMech[sel] == null) continue;
                    string first = Variants[q].Count > 0 ? Variants[q][0] : "";
                    string mine = VariantMech[sel].MekName;
                    if (string.CompareOrdinal(mine, 0, first, 0, 3) != 0) continue;
                    Variants[q].Add(LabMech.MekName);
                    VariantMech[sel] = FromMek(data, Variants[q][Math.Min(VariantIndex[q], Variants[q].Count - 1)], career);
                }
            }
            if (from == 0xc02b || from == 0xc029)
            {
                for (int p = 0; p < Points; p++)
                {
                    VariantMech[p] = null;
                    Variants[p]?.Clear();
                    VariantIndex[p] = 0;
                    VariantOn[p] = false;
                }
                Selected = 0;
            }
            LabMech = null;
        }

        /// <summary>
        /// Case 6 of FUN_1005475d 0x1005475d after its checks: the variants of the chassis read again and switched on, the
        /// MechLab mech (DAT_100d84d0) made — from the chassis' "&lt;code&gt;00std" when the point shows no variant, else a clone
        /// of the shown variant with the chassis' technology — named "Variant" and "&lt;code&gt;NNusr.mek" (FUN_10053f97), and
        /// the point's roster slot set to it. False (an error shown first) when 200 user variants exist.
        /// </summary>
        public static bool MakeLabMech(ShellData data, Career career, Action<string> say)
        {
            var t = data.Tables;
            int sel = Selected, chassis = Chassis[sel];
            string code = Code(t, chassis);
            FillVariants(data, code, sel);
            VariantOn[sel] = true;
            MechRecord lab;
            if (VariantMech[sel] == null)
            {
                int number = NextUserNumber(data, code);
                lab = FromMek(data, code + "00std", career);
                if (lab == null) return false;
                lab.MekName = $"{code}{number:00}usr.mek";
                lab.SetStr(0x100, "Variant");
            }
            else
            {
                var shown = VariantMech[sel];
                lab = shown.Clone();
                lab.SetI32(0x9cf, t.Mech(chassis).Technology);
                lab.SetStr(0x100, "Variant");
                int number = NextUserNumber(data, shown.Str(0x99c, 3));
                if (number == 0)
                {
                    VariantIndex[sel] = 0;
                    var list = Variants[sel];
                    if (list.Count > 1) list.RemoveRange(1, list.Count - 1);
                    else if (list.Count == 0) list.Add("");
                }
                if (number > 199)
                {
                    say("Error: Too many mechs of this variant.#Ok");
                    return false;
                }
                lab.MekName = $"{shown.Str(0x99c, 3)}{number:00}usr.mek";
            }
            LabMech = lab;
            if (sel < 3)
            {
                Select(0);
                SetSlot(t, sel, lab.MekName);
                WriteUserStar(data);
            }
            else
            {
                Select(1);
                SetSlot(t, sel - 3, lab.MekName);
                WriteEnemyStars(data, Difficulty);
            }
            Select(0);
            return true;
        }

        /// <summary>FUN_10048514 0x10048514 on the MechLab mech: its MEK written as the user's "mek\&lt;name&gt;".</summary>
        public static bool WriteLabMek(MechRecord mech)
        {
            try
            {
                if (UserFolder == null) return false;
                Directory.CreateDirectory(UserMekFolder);
                string name = mech.MekName;
                File.WriteAllBytes(Path.Combine(UserMekFolder, name), mech.ToMek());
                return true;
            }
            catch (IOException) { return false; }
        }

        /// <summary>FUN_1004a3f5 0x1004a3f5: mw2rmg.cfg (the user-data folder, else the game's) into the parameters.</summary>
        public static bool LoadParams(string gameFolder)
        {
            foreach (var folder in new[] { UserFolder, gameFolder })
            {
                string path = Path.Combine(folder ?? "", "mw2rmg.cfg");
                if (!File.Exists(path)) continue;
                try
                {
                    byte[] d = File.ReadAllBytes(path);
                    Array.Clear(Params.Data, 0, RandomMissionParams.Size);
                    Buffer.BlockCopy(d, 0, Params.Data, 0, Math.Min(d.Length, RandomMissionParams.Size));
                    return true;
                }
                catch (IOException) { return false; }
            }
            return false;
        }

        /// <summary>FUN_1004a44b 0x1004a44b: the parameters into mw2rmg.cfg (the user-data folder).</summary>
        public static bool SaveParams()
        {
            try
            {
                if (UserFolder == null) return false;
                Directory.CreateDirectory(UserFolder);
                File.WriteAllBytes(Path.Combine(UserFolder, "mw2rmg.cfg"), Params.Data);
                return true;
            }
            catch (IOException) { return false; }
        }
    }
}
