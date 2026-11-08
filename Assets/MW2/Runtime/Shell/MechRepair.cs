using System;
using System.Collections.Generic;
using MW2.Formats;

namespace MW2.Shell
{
    /// <summary>
    /// The repair logic of the repair bay 0xc028 (MW2SHELL.DLL, around FUN_1005c500 0x1005c500). The mech being repaired
    /// (DAT_100db770) is compared with its pristine copy (DAT_100db774, the unit copy with the same id +0x990, list
    /// DAT_100cd10c); a clone taken when it is selected (DAT_100db778) is the undo point. Armour costs 625 a point
    /// (FUN_1004a4a1), internal structure 200 (FUN_1004a4af); a critical takes its replacement from the unit's
    /// inventory (FUN_1005cf8c): weapons and ammunition are free but need a stored item, heat sinks and 8500 need one
    /// and cost the equipment price, other equipment just costs its price. Items taken are kept in
    /// DAT_100db794 / DAT_100db798 and go back on undo (FUN_1005eef0). Nothing is paid until the repairs are accepted
    /// (FUN_1005efd4). These lists are made once per opening and never emptied on acceptance, so an undo after
    /// switching mechs gives back the items of repairs already accepted too: that is how the original behaves.
    /// </summary>
    public sealed class MechRepair
    {
        readonly Career _c;
        readonly ShellTables _t;
        public MechRecord Mech, Pristine, Backup;           // DAT_100db770, 774, 778
        public int Index;                                   // DAT_100db76c
        public readonly List<ItemRecord> TakenWeapons = new List<ItemRecord>(), TakenAmmo = new List<ItemRecord>();
        /// <summary>FUN_1007215f(DAT_100db780): the repair sound, played by FUN_1005c922 for each critical replaced.</summary>
        public Action PartReplaced;

        public MechRepair(Career c, ShellTables t)
        {
            _c = c;
            _t = t;
        }

        public static int ArmorCost(int points) => points * 625;        // FUN_1004a4a1 0x1004a4a1
        public static int InternalCost(int points) => points * 200;     // FUN_1004a4af 0x1004a4af

        public int Section => Mech.I32(0x364);

        /// <summary>
        /// FUN_1005dafe 0x1005dafe: the player's mech (FUN_1004a2b4: the mech of the pilot with +0x2064) or the first one;
        /// its +0x9cc repaired flag cleared, the pristine copy and the undo clone.
        /// </summary>
        public void Open()
        {
            Mech = null;
            foreach (var p in _c.UnitPilots)
                if (p.Player)
                {
                    Mech = _c.UnitMechs.Find(m => m.PilotId == p.Id);
                    break;
                }
            Index = Mech != null ? _c.UnitMechs.IndexOf(Mech) : -1;
            if (Mech == null)
            {
                Mech = _c.UnitMechs[0];
                Index = 0;
            }
            Mech.Data[0x9cc] = 0;
            Pristine = _c.UnitCopies.Find(m => m.MechId == Mech.MechId);
            Backup = Mech.Clone();
        }

        /// <summary>FUN_1005fe63 0x1005fe63 / FUN_1005ff32 0x1005ff32: the next (+1) or previous (-1) mech, wrapping.</summary>
        public void Cycle(int step)
        {
            int n = _c.UnitMechs.Count;
            Index += step;
            if (Index >= n) Index = 0;
            if (Index < 0) Index = n - 1;
            Mech = _c.UnitMechs[Index];
            Pristine = _c.UnitCopies.Find(m => m.MechId == Mech.MechId);
            Backup = Mech.Clone();
        }

        /// <summary>
        /// FUN_1005c500 0x1005c500: the cost of bringing <paramref name="m"/> back to the pristine copy (armour, internal
        /// structure, then each critical that differs); without <paramref name="dryRun"/> the repairs are made. Sets
        /// +0x9cd, cleared again when a critical differs.
        /// </summary>
        public int Cost(MechRecord m, MechRecord pristine, bool dryRun)
        {
            if (pristine == null) return 0;
            int total = 0;
            m.Data[0x9cd] = 1;
            for (int s = 0; s < 8; s++)
            {
                total += Armor(m, pristine, s, dryRun) + Internal(m, pristine, s, dryRun);
                for (int j = 0; j < 12; j++)
                    if (m.Crit(s, j) != pristine.Crit(s, j))
                    {
                        int price = Critical(s, j, dryRun, m, pristine);
                        m.Data[0x9cd] = 0;
                        if (price > 0) total += price;
                    }
            }
            return total;
        }

        /// <summary>FUN_1005c615 0x1005c615: front and rear armour of one section.</summary>
        public int Armor(MechRecord m, MechRecord pristine, int s, bool dryRun)
        {
            int o = 0x8e0 + s * 0x12;
            int points = pristine.I32(o + 8) - m.I32(o + 8) + pristine.I32(o + 12) - m.I32(o + 12);
            int cost = ArmorCost(points);
            if (!dryRun)
            {
                m.SetI32(o + 8, pristine.I32(o + 8));
                m.SetI32(o + 12, pristine.I32(o + 12));
                if (points > 0) Touched(m);
            }
            return cost;
        }

        /// <summary>FUN_1005c6d4 0x1005c6d4: internal structure of one section.</summary>
        public int Internal(MechRecord m, MechRecord pristine, int s, bool dryRun)
        {
            int o = 0x8e0 + s * 0x12;
            int points = pristine.I32(o) - m.I32(o);
            int cost = InternalCost(points);
            if (!dryRun)
            {
                m.SetI32(o, pristine.I32(o));
                if (points > 0) Touched(m);
            }
            return cost;
        }

        /// <summary>+0x9cc repaired and the career's changed byte (*DAT_100d3d58).</summary>
        void Touched(MechRecord m)
        {
            m.Data[0x9cc] = 1;
            _c.Started = true;
        }

        /// <summary>
        /// FUN_1005c922 0x1005c922: one critical; 0 when it needs nothing (empty in the pristine copy, the same, or
        /// ammunition of the same weapon), -1 without a replacement, else the price. A weapon or ammunition replaced
        /// brings its other slots and its weapon / bin entry with it (FUN_1005c820).
        /// </summary>
        public int Critical(int s, int j, bool dryRun, MechRecord m, MechRecord pristine)
        {
            int want = pristine.Crit(s, j), have = m.Crit(s, j);
            if (want < 0 || want == have) return 0;
            int kw = Campaign.ItemKind(want), kh = Campaign.ItemKind(have);
            if (kh == kw && kw == 1 && Career.ItemIndex(kw, want, _t) == Career.ItemIndex(kh, have, _t)) return 0;
            if (!Replacement(want, false, out int price)) return -1;
            if (!dryRun)
            {
                m.SetCrit(s, j, want);
                Touched(m);
                if (kw != 2)
                {
                    CopyItem(m, pristine, s, j, want);
                    Touched(m);
                }
                PartReplaced?.Invoke();
                Replacement(want, true, out price);
                Touched(m);
            }
            return price;
        }

        /// <summary>
        /// FUN_1005c820 0x1005c820: a weapon goes to the first free weapon entry of the current mech (FUN_1005c774, the
        /// last one when none is free), ammunition to the first free bin (FUN_1005c7ca); the engine slots 7800 / 7850
        /// stop there; then every other slot holding the same item in the pristine copy gets it too.
        /// </summary>
        public void CopyItem(MechRecord m, MechRecord pristine, int section, int slot, int item)
        {
            int kind = Campaign.ItemKind(item);
            if (kind == 0)
            {
                int k = 0;
                while (k < 0xd && Mech.I32(0x2f0 + k * 8) != -1) k++;
                m.SetI32(0x2f0 + k * 8, item);
            }
            else if (kind == 1)
            {
                int k = 0;
                while (k < 0x18 && Mech.I32(0x28c + k * 4) != -1) k++;
                m.SetI32(0x28c + k * 4, item);
            }
            else if (kind == 2 && (item == 0x1e78 || item == 0x1eaa)) return;
            for (int s = 0; s < 8; s++)
                for (int j = 0; j < 12; j++)
                    if ((s != section || j != slot) && pristine.Crit(s, j) == item) m.SetCrit(s, j, item);
        }

        /// <summary>
        /// FUN_1005cf8c 0x1005cf8c: whether <paramref name="item"/> can be replaced and its price; with
        /// <paramref name="take"/> the stored item leaves the inventory for the taken lists. Weapons: a stored weapon of
        /// the same index (kind 0, +0x16 = 0); ammunition: one of the same weapon (kind 1); heat sinks 8000..8099: stored
        /// equipment 1 (single, +0x228 = 0) or 2 (double, +0x228 = 1); 8500..8599: equipment 6;
        /// other equipment needs nothing. Equipment costs the price of its entry in the table at 0x100a8198.
        /// </summary>
        public bool Replacement(int item, bool take, out int price)
        {
            price = 0;
            int kind = Campaign.ItemKind(item);
            int idx = Career.ItemIndex(kind, item, _t);
            if (kind == 0) return Take(_c.UnitWeapons, TakenWeapons, it => it.Index == idx && it.Kind == 0 && it.Field16 == 0, take);
            if (kind == 1) return Take(_c.UnitAmmo, TakenAmmo, it => it.Index == idx && it.Kind == 1, take);
            if (kind != 2 || idx < 0) return false;
            int hundreds = item / 100 * 100;
            int stored = -1;
            if (hundreds == 8000)
            {
                int type = Mech.I32(0x228);
                if (type == 0) stored = 1;
                else if (type == 1) stored = 2;
                else return false;
            }
            else if (hundreds == 8500) stored = 6;
            // The original also compares (item / 100) * 100 with 9550 for equipment 5, which can never be equal.
            if (stored >= 0 && !Take(_c.UnitWeapons, TakenWeapons, it => it.Kind == 2 && it.Field16 == 0 && it.Index == stored, take))
                return false;
            price = _t.Item(idx).Price;
            return true;
        }

        static bool Take(List<ItemRecord> from, List<ItemRecord> to, Predicate<ItemRecord> match, bool take)
        {
            var it = from.Find(match);
            if (it == null) return false;
            if (take)
            {
                Career.RemoveItem(from, it);
                to.Add(it);
            }
            return true;
        }

        /// <summary>FUN_1005ecb2 0x1005ecb2: what the repairs made since the undo point cost.</summary>
        public int Pending() => Cost(Backup, Pristine, true) - Cost(Mech, Pristine, true);

        /// <summary>FUN_1005eef0 0x1005eef0: the taken items go back to the inventory (at its end) and the mech to the clone.</summary>
        public void Undo()
        {
            _c.UnitWeapons.AddRange(TakenWeapons);
            TakenWeapons.Clear();
            _c.UnitAmmo.AddRange(TakenAmmo);
            TakenAmmo.Clear();
            Buffer.BlockCopy(Backup.Data, 0, Mech.Data, 0, MechRecord.Size);
        }

        /// <summary>FUN_1005f9d6 0x1005f9d6: repair everything (the pristine copy found again by id).</summary>
        public int RepairAll(bool dryRun) => Cost(Mech, _c.UnitCopies.Find(m => m.MechId == Mech.MechId), dryRun);

        /// <summary>FUN_1005fc33 0x1005fc33: the selected section has all its internal structure.</summary>
        public bool InternalsDone() => Pristine.I32(0x8e0 + Section * 0x12) - Mech.I32(0x8e0 + Section * 0x12) < 1;

        /// <summary>
        /// FUN_1005fca2 0x1005fca2 for the selected critical <paramref name="slot"/> of the section: -3 empty in the
        /// pristine copy, -2 nothing to repair, -1 no replacement, else the price (paid on acceptance). Marks the mech
        /// repaired only when there is a price.
        /// </summary>
        public int RepairCritical(int slot, bool dryRun)
        {
            int s = Section;
            int want = Pristine.Crit(s, slot);
            if (want == -1) return -3;
            if (want == Mech.Crit(s, slot)) return -2;
            if (!Replacement(want, false, out int price)) return -1;
            if (price > 0) Touched(Mech);
            if (dryRun) return price;
            Mech.SetCrit(s, slot, want);
            CopyItem(Mech, Pristine, s, slot, want);
            PartReplaced?.Invoke();
            Replacement(want, true, out price);
            if (price > 0) Touched(Mech);
            return price;
        }

        /// <summary>
        /// FUN_1005efd4 0x1005efd4 after its "Accept repairs?" question: No (1) undoes; Yes (0) pays what the repairs
        /// cost (none when negative) and clears +0x9cc, or, without the funds, returns false so the screen can ask
        /// "Undo customizations?". The result says whether the screen may go on.
        /// </summary>
        public bool Answer(int answer)
        {
            if (answer == 1)
            {
                Undo();
                return true;
            }
            int cost = Math.Max(0, Pending());
            if (_c.Funds < cost) return false;
            if (cost > 0)
            {
                _c.Funds -= cost;
                Mech.Data[0x9cc] = 0;
            }
            return true;
        }

        /// <summary>The text of FUN_1005d3b4 for a critical (the top bit cleared).</summary>
        public static string CriticalName(int item, ShellTables t)
        {
            int v = item & 0x7fffffff;
            if (v == 0) return "-";
            if (v < 7000) return $"{t.WeaponName(v / 100)} #{v % 100}";
            if (v < 10000)
            {
                for (int k = 0x16; k >= 0; k--)
                    if (v / 0x32 == t.Item(k).Code / 0x32) return t.Item(k).Name;
                return v / 10 == 0x3bb ? "CASE" : $"BAD CRITICAL {v}";
            }
            return $"Ammo ({t.WeaponName((v - 10000) / 10000)})";
        }
    }
}
