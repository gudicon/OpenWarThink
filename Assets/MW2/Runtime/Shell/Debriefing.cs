using System;
using System.Collections.Generic;
using System.Text;

namespace MW2.Shell
{
    /// <summary>
    /// The career side of the debriefing 0xc00a (Debrief_Open 0x1001ede1 and the functions it calls): the objectives
    /// table with its pay (Debrief_Objectives 0x1001bb15), the funds summary (Debrief_Funds 0x1001b7e0), the deaths
    /// (Debrief_DeathText 0x1001db52), the salvage notes (FUN_1001d4ec 0x1001d4ec), which missions allow salvage, and the
    /// campaign advance when the screen is left (FUN_1001d1d3 0x1001d1d3 with Unit_Expenses 0x1001b6ff and
    /// Debrief_RemoveDead 0x1001ccbd). Money is written by FUN_100496d6 0x100496d6. Widths are font 11 (DAT_100cd0dc).
    /// </summary>
    public sealed class Debriefing
    {
        readonly Career _c;
        readonly ShellTables _t;
        readonly ShellFont _font;
        /// <summary>DAT_100c8f6c: income of this mission (objective pay, kill bounty, then the contract compensation).</summary>
        public int Income;
        /// <summary>DAT_100c8f74: contract compensation (success or failure pay).</summary>
        public int Compensation;
        /// <summary>DAT_100c8f70 / DAT_100c99d4 / DAT_100c8f68: completed Target, Bonus and Bounty objectives.</summary>
        public int TargetsDone, BonusesDone, BountiesDone;

        public Debriefing(Career career, ShellTables tables, ShellFont font11)
        {
            _c = career;
            _t = tables;
            _font = font11;
        }

        /// <summary>FUN_100496d6: "%d" with a comma before every group of three digits, the minus sign kept in front.</summary>
        public static string Money(int value)
        {
            string digits = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            bool negative = digits.StartsWith("-");
            if (negative) digits = digits.Substring(1);
            var sb = new StringBuilder();
            for (int i = 0; i < digits.Length; i++)
            {
                if (i > 0 && (digits.Length - i) % 3 == 0) sb.Append(',');
                sb.Append(digits[i]);
            }
            return negative ? "-" + sb : sb.ToString();
        }

        /// <summary>The reward column of a row: "\g514\bNNN" puts the right edge of the text at x 514.</summary>
        string Column(string value) => $"\\g{0x202:000}\\b{_font.Width(value):000}{value}";

        /// <summary>
        /// Debrief_Objectives 0x1001bb15: the header, one row per reported objective sorted by end second (unfinished last,
        /// qsort with FUN_1001b557), "time / type / objective text (whitespace runs collapsed, FUN_1001b5c5) / reward /
        /// status", then the kill bounty (a regular career: kills x bounty pay) or the kill count (novice). Kills are the
        /// statistics +0x1e and +0x4a (destroyed enemy units with GP flags 0x1400).
        /// </summary>
        public string Objectives(ShellMissionReport report, byte[] combat)
        {
            TargetsDone = BonusesDone = BountiesDone = 0;
            Income = 0;
            int kills = BitConverter.ToUInt16(combat, 0x1e) + BitConverter.ToUInt16(combat, 0x4a);
            bool regular = _c.Mode == 1;
            var rows = new List<int>();
            for (int i = 0; i < report.Count; i++) rows.Add(i);
            int End(int i) => report.Objective(i).fieldA5;
            int Compare(int a, int b)
            {
                if (End(a) < 0) return 1;
                if (End(b) < 0) return -1;
                return End(b) < End(a) ? 1 : End(a) < End(b) ? -1 : 0;
            }
            MsvcQsort(rows, Compare);
            var text = new StringBuilder();
            string header = regular ? "Reward" : "";
            text.Append($"Time\\g040Type\\g110Objective\\g400Status{Column(header)}\\n");
            for (int k = 0; k < rows.Count; k++)
            {
                var o = report.Objective(rows[k]);
                string type, reward = "";
                int pay = -1;
                switch (o.field3b)
                {
                    case 0: type = "Default"; break;
                    case 1:
                        type = "Target";
                        if (o.complete) { TargetsDone++; pay = _c.TargetPay; Income += pay; } else pay = 0;
                        break;
                    case 2:
                        type = "Bonus";
                        if (o.complete) { BonusesDone++; pay = _c.BonusPay; Income += pay; } else pay = 0;
                        break;
                    case 4:
                        type = "Bounty";
                        if (o.complete) { BountiesDone++; pay = _c.BountyPay; Income += pay; } else pay = 0;
                        break;
                    case 8: type = "Return"; pay = 0; break;
                    default: type = "Unknown"; pay = 0; break;
                }
                if (pay >= 0) reward = Column(regular ? Money(pay) : "");
                string status = o.complete ? "Successful" : "Failed";
                string time = o.fieldA5 < 0 ? "DNF" : $"{o.fieldA5 / 60 % 60:00}:{o.fieldA5 % 60:00}";
                text.Append($"{time}\\g040{type}\\g110{Collapse(o.text)}{reward}\\g400{status}");
                if (k < rows.Count - 1) text.Append("\\n");
                else if (kills > 0) text.Append("\\n");
            }
            if (regular)
            {
                if (kills > 0)
                {
                    string label = kills == 1 ? "Cash bounty for 1 confirmed kill" : $"Cash bounty for {kills} confirmed kills";
                    int bounty = kills * _c.BountyPay;
                    Income += bounty;
                    text.Append($"\\n\\g110{label}{Column(Money(bounty))}");
                }
            }
            else text.Append($"\\n\\n\\g110Total confirmed kills: {kills}");
            return text.ToString();
        }

        /// <summary>FUN_1001b5c5 0x1001b5c5: every run of characters below '!' becomes one space.</summary>
        static string Collapse(string s)
        {
            var sb = new StringBuilder();
            bool space = false;
            foreach (char ch in s)
            {
                if (ch < '!') { if (!space) { sb.Append(' '); space = true; } }
                else { space = false; sb.Append(ch); }
            }
            return sb.ToString();
        }

        /// <summary>
        /// qsort of MSVCRT (imported by MW2SHELL.DLL): the VC CRT algorithm, selection "shortsort" up to 8 elements,
        /// otherwise the middle element as pivot. Equal end seconds keep the order this algorithm gives (not a stable sort).
        /// </summary>
        static void MsvcQsort(List<int> a, Comparison<int> comp)
        {
            if (a.Count < 2) return;
            void Swap(int i, int j) { int t = a[i]; a[i] = a[j]; a[j] = t; }
            var loStack = new Stack<(int, int)>();
            int lo = 0, hi = a.Count - 1;
            while (true)
            {
                int size = hi - lo + 1;
                if (size <= 8)
                {
                    for (int h = hi; h > lo; h--)
                    {
                        int max = lo;
                        for (int p = lo + 1; p <= h; p++) if (comp(a[p], a[max]) > 0) max = p;
                        Swap(max, h);
                    }
                }
                else
                {
                    int mid = lo + size / 2;
                    Swap(mid, lo);
                    int loguy = lo, higuy = hi + 1;
                    while (true)
                    {
                        do loguy++; while (loguy <= hi && comp(a[loguy], a[lo]) <= 0);
                        do higuy--; while (higuy > lo && comp(a[higuy], a[lo]) >= 0);
                        if (higuy < loguy) break;
                        Swap(loguy, higuy);
                    }
                    Swap(lo, higuy);
                    if (higuy - 1 - lo >= hi - loguy)
                    {
                        if (lo + 1 < higuy) loStack.Push((lo, higuy - 1));
                        if (loguy < hi) { lo = loguy; continue; }
                    }
                    else
                    {
                        if (loguy < hi) loStack.Push((loguy, hi));
                        if (lo + 1 < higuy) { hi = higuy - 1; continue; }
                    }
                }
                if (loStack.Count == 0) return;
                (lo, hi) = loStack.Pop();
            }
        }

        /// <summary>
        /// Debrief_Funds 0x1001b7e0: funds, the contract compensation (added to the income), total income, the expenses of
        /// Unit_Expenses (shown negative), a rule and the balance funds + income - expenses.
        /// </summary>
        public string Funds()
        {
            var text = new StringBuilder();
            text.Append($"Funds: {Column(Money(_c.Funds))}\\n");
            Income += Compensation;
            text.Append($"Contract compensation: {Column(Money(Compensation))}\\n");
            text.Append($"Total Income: {Column(Money(Income))}\\n");
            text.Append($"Total expenses:  {Column(Money(-_c.UnitExpenses(false)))}\\n");
            text.Append($"{Column("--------------")}\\n");
            text.Append($"Balance: {Column(Money(_c.Funds + Income - _c.UnitExpenses(false)))}\\n");
            return text.ToString();
        }

        /// <summary>FUN_1004576c 0x1004576c: the table name of a mech code (its first three letters).</summary>
        string MechName(MechRecord m)
        {
            int i = _t.FindMech(m.Code);
            return i >= 0 ? _t.MechName(i) : "";
        }

        /// <summary>The MEK name of an on-duty technician's aerotech: +0x4d copied into a 5-byte buffer whose bytes 5..7
        /// become "MER", then ".MEK" appended (a source shorter than 8 letters runs into uninitialised bytes there).</summary>
        public static string TechMekName(TechRecord tech)
        {
            string source = tech.Str(0x4d, 0x10);
            string head = source.Length > 5 ? source.Substring(0, 5) : source;
            return head + "MER" + (source.Length > 8 ? source.Substring(8) : "") + ".MEK";
        }

        /// <summary>
        /// Debrief_DeathText 0x1001db52: every wingman whose mech (+0x98c = pilot +0x24) is destroyed (Mech_IsDestroyed)
        /// "died in battle losing a" it; every on-duty technician (+0x2c = 1) whose aerotech MEK written by the engine is
        /// destroyed gets +0x5d = 1 and the same line with " aerotech". <paramref name="writtenMek"/> rebuilds a written MEK.
        /// </summary>
        public string Deaths(Func<string, MechRecord> writtenMek)
        {
            var text = new StringBuilder();
            foreach (var pilot in _c.UnitPilots)
            {
                if (pilot.Player) continue;
                var mech = _c.UnitMechs.Find(m => m.PilotId == pilot.Id);
                if (mech != null && mech.Destroyed) text.Append($"{pilot.Name} died in battle losing a {MechName(mech)}\\n");
            }
            foreach (var tech in _c.UnitTechs)
            {
                if (tech.Data[0x2c] != 1) continue;
                var mech = writtenMek?.Invoke(TechMekName(tech));
                if (mech != null && mech.Destroyed)
                {
                    tech.Data[0x5d] = 1;
                    text.Append($"{tech.Name} died in battle losing a {MechName(mech)} aerotech\\n");
                }
            }
            return text.ToString();
        }

        /// <summary>
        /// FUN_1001d4ec 0x1001d4ec: only while salvage.mw2 exists and in a regular career: "No mechs were salvaged." when
        /// salvage was possible but the file lists none, "No opportunity for salvage was available." otherwise.
        /// </summary>
        public string SalvageNote(bool salvageFile, bool noOpportunity, int salvageCount)
        {
            if (!salvageFile || _c.Mode != 1) return "";
            if (noOpportunity) return "\\nNo opportunity for salvage was available.\\n";
            return salvageCount == 0 ? "\\nNo mechs were salvaged.\\n" : "";
        }

        /// <summary>
        /// Debrief_Open 0x1001ede1: missions whose salvage is allowed, by the first three letters of the mission and the
        /// number after them (DAT_100a2648..DAT_100a26a4).
        /// </summary>
        public static bool SalvageAllowed(string mission)
        {
            string m = mission ?? "";
            string prefix = (m.Length > 3 ? m.Substring(0, 3) : m).ToLowerInvariant();
            int n = m.Length > 3 && m[3] >= '0' && m[3] <= '9' ? m[3] - '0' : 0;
            bool In(params int[] yes) => Array.IndexOf(yes, n) >= 0;
            switch (prefix)
            {
                case "bet": return In(1, 3, 4, 5);
                case "gra": return In(1, 2, 3, 5);
                case "iva": return In(1);
                case "lut": return In(1, 2, 4, 5, 6);
                case "mcc": return In(3, 4, 6, 7);
                case "tra": return In(4);
                case "spa": case "wol": case "big": case "dou": case "dow": case "med": case "mou": case "myb":
                case "out": case "rev": case "rog": case "sur": case "sol":
                    return true;
                default: return false;      // buz, dom, rec, wou, ran and any other
            }
        }

        /// <summary>FUN_1001ce81 0x1001ce81 (not after the debriefing was left once, +0x23b7): every step item and step mech
        /// is salvaged (+9 / +0x9ce) when rand() % 100 is not above its chance (+0x12 / +0x9c7).</summary>
        public void RollSalvage(Func<int> rand)
        {
            if (_c.Byte23B7) return;
            foreach (var item in _c.StepItems) item.Data[9] = (byte)(item.Field12 < rand() % 100 ? 0 : 1);
            foreach (var mech in _c.StepMechs) mech.Data[0x9ce] = (byte)(mech.SalvageChance < rand() % 100 ? 0 : 1);
        }

        /// <summary>
        /// Debrief_RemoveDead 0x1001ccbd: a wingman whose mech is destroyed leaves the unit with the mech and its copy; the
        /// on-duty technicians' written MEKs are deleted and those marked dead (+0x5d) leave.
        /// </summary>
        public void RemoveDead()
        {
            foreach (var pilot in _c.UnitPilots.ToArray())
            {
                if (pilot.Player) continue;
                var mech = _c.UnitMechs.Find(m => m.PilotId == pilot.Id);
                if (mech == null || !mech.Destroyed) continue;
                var copy = _c.UnitCopies.Find(m => m.MechId == mech.MechId);
                _c.UnitMechs.Remove(mech);
                if (copy != null) _c.UnitCopies.Remove(copy);
                _c.UnitPilots.Remove(pilot);
            }
            _c.UnitTechs.RemoveAll(tech => tech.Data[0x2c] == 1 && tech.Data[0x5d] == 1);
        }

        /// <summary>
        /// FUN_1001dd8d 0x1001dd8d (a regular career, after the ejection flic): except in SIG2, SIG4, OUT3 and MCC4 the
        /// player's mech and its copy leave the unit and the player has no mech (+0x20 = -1).
        /// </summary>
        public void LoseEjectedMech()
        {
            string m = _c.Mission.ToUpperInvariant();
            if (m == "SIG2SCN1" || m == "SIG4SCN1" || m == "OUT3SCN1" || m == "MCC4SCN1") return;
            PilotRecord player = _c.UnitPilots.Find(p => p.Player);
            var mech = player != null ? _c.UnitMechs.Find(x => x.PilotId == player.Id) : null;
            var copy = mech != null ? _c.UnitCopies.Find(x => x.MechId == mech.MechId) : null;
            if (mech != null) _c.UnitMechs.Remove(mech);
            if (copy != null) _c.UnitCopies.Remove(copy);
            if (player != null) player.MechId = -1;
        }

        /// <summary>FUN_1001d436 0x1001d436 (novice, first visit): every unit mech is replaced by a fresh clone of its copy,
        /// keeping the pilot (+0x98c).</summary>
        public void RestoreFromCopies()
        {
            foreach (var copy in _c.UnitCopies)
            {
                var mech = _c.UnitMechs.Find(m => m.MechId == copy.MechId);
                copy.PilotId = mech != null ? mech.PilotId : copy.PilotId;
                if (mech != null) _c.UnitMechs.Remove(mech);
                _c.UnitMechs.Add(copy.Clone());
            }
        }

        /// <summary>
        /// FUN_1001d1d3 0x1001d1d3, the campaign advance when the debriefing is left. Novice: a won contract mission loads the
        /// first success step; a won last mission keeps only the mechs with +0x994 > 0 (FUN_1001c7cd) and the player as the
        /// only pilot (FUN_1001c8f6). Regular: the income goes to the funds, the expenses are paid, the dead leave, then by
        /// step type 0 the first success step, 1 the first success step after a win or else the first failure step (DEATH:
        /// title), 2 / 3 back to dock (+0x239d) unless a won type 3 goes on. Returns 0xc02b when the career ends here, else 0.
        /// </summary>
        public uint Advance(ShellData data)
        {
            if (_c.Mode == 0)
            {
                int type = _c.StepType;
                if (type == 1) { if (_c.Outcome239C) _c.LoadStep(_c.SuccessStep(0), data); }
                else if (type >= 2 && type <= 3 && _c.Outcome239C)
                {
                    var keep = _c.UnitMechs.FindAll(m => m.I32(0x994) > 0);
                    var keepCopies = keep.ConvertAll(m => _c.UnitCopies.Find(x => x.MechId == m.MechId));
                    _c.UnitMechs.Clear(); _c.UnitMechs.AddRange(keep);
                    _c.UnitCopies.Clear(); foreach (var x in keepCopies) if (x != null) _c.UnitCopies.Add(x);
                    var player = _c.UnitPilots.Find(p => p.Player);
                    if (player != null)
                    {
                        player.MechId = -1;
                        _c.UnitPilots.Clear();
                        _c.UnitPilots.Add(player);
                    }
                }
                return 0;
            }
            if (_c.Mode != 1) return 0;
            _c.Funds += Income;
            _c.UnitExpenses(true);
            RemoveDead();
            switch (_c.StepType)
            {
                case 0:
                    _c.LoadStep(_c.SuccessStep(0), data);
                    break;
                case 1:
                    if (_c.Outcome239C) _c.LoadStep(_c.SuccessStep(0), data);
                    else
                    {
                        if (string.Equals(_c.FailStep(0), "DEATH", StringComparison.OrdinalIgnoreCase)) return 0xc02b;
                        _c.LoadStep(_c.FailStep(0), data);
                    }
                    break;
                case 2:
                    if (!_c.Outcome239C && string.Equals(_c.FailStep(0), "DEATH", StringComparison.OrdinalIgnoreCase)) return 0xc02b;
                    _c.Finished239D = true;
                    break;
                case 3:
                    if (_c.Outcome239C) _c.LoadStep(_c.SuccessStep(0), data);
                    else _c.Finished239D = true;
                    break;
            }
            return 0;
        }
    }
}
