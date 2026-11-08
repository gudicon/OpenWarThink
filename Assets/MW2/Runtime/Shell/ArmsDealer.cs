using System;
using System.Collections.Generic;

namespace MW2.Shell
{
    /// <summary>
    /// The trade of the Arms Merchant 0xc01e (armsdlr.cpp, MW2SHELL.DLL). On opening FUN_10049c6c 0x10049c6c moves the
    /// unit's weapons and equipment (DAT_100cd134) and the dealer's (DAT_100cd14c, their +0x16 cleared) into four lists
    /// each by FUN_10049a12 0x10049a12: missiles DAT_100cd144 / 15c, energy 13c / 154, projectiles 140 / 158,
    /// equipment 148 / 160; indices 30 and 32 (Nuke, Torpedo) go nowhere and are lost. Ammunition stays in DAT_100cd138 /
    /// 150. Leaving, FUN_10049df5 0x10049df5 appends them back, the unit's equipment of index 9 left out. Prices are
    /// float products truncated by ftol: selling 0.6 of +4 (0.15 when damaged, +0x16 ≠ 0), buying 1.0, repairing 0.15
    /// of the damaged copy (the screen shows 0.25 as the repair cost). The drop ship holds 200 (FUN_100027d0).
    /// </summary>
    public sealed class ArmsDealer
    {
        public const float Sell = 0.6f, SellDamaged = 0.15f, ShownRepair = 0.25f;     // 0x10093000, 0x10093018, 0x10093004
        public const int Capacity = 200;

        readonly Career _c;
        public readonly ShellCollection Energy = new ShellCollection(), Projectiles = new ShellCollection(),
            Missiles = new ShellCollection(), Equipment = new ShellCollection();                 // DAT_100cd13c..148
        public readonly ShellCollection DealerEnergy = new ShellCollection(), DealerProjectiles = new ShellCollection(),
            DealerMissiles = new ShellCollection(), DealerEquipment = new ShellCollection();     // DAT_100cd154..160
        public readonly ShellCollection Ammo, DealerAmmo;                                        // DAT_100cd138 / 150

        /// <summary>The lists shown (DAT_100c1ee0 / DAT_100c1ee4) and the tab flags DAT_100c1f14 (equipment) /
        /// DAT_100c1f18 (ammunition).</summary>
        public ShellCollection Stock, Offer;
        public bool EquipmentTab, AmmoTab;

        public ArmsDealer(Career c)
        {
            _c = c;
            Ammo = new ShellCollection(c.UnitAmmo);
            DealerAmmo = new ShellCollection(c.DealerAmmo);
            Stock = Missiles;
            Offer = DealerMissiles;
        }

        /// <summary>FUN_10049c6c.</summary>
        public void Split()
        {
            foreach (var it in _c.UnitWeapons) Place(it, false);
            _c.UnitWeapons.Clear();
            foreach (var it in _c.DealerWeapons) Place(it, true);
            _c.DealerWeapons.Clear();
        }

        /// <summary>FUN_10049a12(item, 0): a unit record into its category list (Customizing 0xc02f uses it too).</summary>
        public void PlaceUnit(ItemRecord it) => Place(it, false);

        /// <summary>FUN_10049a12.</summary>
        void Place(ItemRecord it, bool dealer)
        {
            if (dealer) it.Field16 = 0;
            if (it.Kind == 2)
            {
                (dealer ? DealerEquipment : Equipment).Add(it);
                return;
            }
            switch (Category(it.Index))
            {
                case 0: (dealer ? DealerMissiles : Missiles).Add(it); break;
                case 1: (dealer ? DealerProjectiles : Projectiles).Add(it); break;
                case 2: (dealer ? DealerEnergy : Energy).Add(it); break;
            }
        }

        /// <summary>The switch of FUN_10049a12: 0 missiles, 1 projectiles, 2 energy, -1 none (30, 32 and unknown ones, for
        /// which the original shows "unknown weapon in inventory").</summary>
        public static int Category(int i)
        {
            if (i >= 0 && i <= 9 || i == 0x1c || i == 0x1f || i >= 0x21 && i <= 0x2a || i == 0x3d || i >= 0x3f && i <= 0x41) return 0;
            if (i >= 10 && i <= 0x13 || i == 0x1d || i >= 0x2b && i <= 0x32 || i == 0x3e) return 1;
            if (i >= 0x14 && i <= 0x1b || i >= 0x33 && i <= 0x3c) return 2;
            return -1;
        }

        /// <summary>FUN_10049df5.</summary>
        public void Merge()
        {
            foreach (var l in new[] { Energy, Projectiles, Missiles }) { _c.UnitWeapons.AddRange(l.Items); l.Items.Clear(); }
            foreach (var it in Equipment.Items) if (it.Index != 9) _c.UnitWeapons.Add(it);
            Equipment.Items.Clear();
            foreach (var l in new[] { DealerEnergy, DealerProjectiles, DealerMissiles, DealerEquipment })
            {
                _c.DealerWeapons.AddRange(l.Items);
                l.Items.Clear();
            }
        }

        /// <summary>FUN_10061b13 0x10061b13: the class of an item code. For a weapon index 1..65 (35 taken off above 34):
        /// 0 for 0..9 and 31..34 (missiles), 2 for 10..19 (ballistic), 1 for 20..27 (energy); an index above 99 is
        /// ammunition (4); otherwise 3 for 8000.., 8500.. and 9550.. by tens, else -1 (index 0 too).</summary>
        public static int CodeClass(int code)
        {
            int i = code / 100;
            if (i >= 1 && i <= 0x41)
            {
                if (i > 0x22) i -= 0x23;
                if (i >= 0 && i < 10 || i > 0x1e && i < 0x23) return 0;
                if (i > 9 && i < 0x14) return 2;
                if (i > 0x13 && i < 0x1c) return 1;
            }
            else if (i > 99) return 4;
            int tens = code / 10 * 10;
            return tens == 8000 || tens == 0x2134 || tens == 0x254e ? 3 : -1;
        }

        /// <summary>FUN_10061bdd 0x10061bdd / FUN_10061c29 0x10061c29: the space of an item: the weapon table's +4 (at
        /// 0x100ac0a4) for classes 0..2, else 1 (the index is taken as a weapon's even for equipment).</summary>
        public static int Space(int index, ShellTables t)
        {
            int k = CodeClass(index * 100);
            return k >= 0 && k <= 2 ? t.Weapon(index).Slots : 1;
        }

        /// <summary>FUN_100027d0 0x100027d0: missiles, energy, projectiles and equipment by space, ammunition 1 a ton.</summary>
        public int Load(ShellTables t)
        {
            int load = 0;
            foreach (var l in new[] { Missiles, Energy, Projectiles, Equipment })
                foreach (var it in l.Items) load += Space(it.Index, t);
            return load + Ammo.Count;
        }

        public static int Value(ItemRecord it, float factor) => (int)(it.Price * (double)factor);

        /// <summary>A row of FUN_100056e2 0x100056e2: one per index, in list order.</summary>
        public sealed class Row
        {
            public int Index, Total, Pristine, Damaged;
            public int Value;           // the name entry's +0x30: what selling or buying one is shown at
            public int Price;           // the price entry's +0x30
            public ItemRecord First;    // +0x34
        }

        /// <summary>
        /// FUN_100056e2: sorts an unsorted list (FUN_100098b9), then FUN_1000328c 0x1000328c (a bubble sort by index, then
        /// one bubble pass by +0x16 over each index's records), marks every record's +9, and makes a row per index:
        /// all records of the index, the pristine and damaged ones of the tab's kind (ammunition: all pristine).
        /// </summary>
        public List<Row> Rows(ShellCollection l, bool dealer)
        {
            l.SortIfUnsorted();
            Arrange(l.Items);
            var rows = new List<Row>();
            foreach (var it in l.Items) it.Data[9] = 0;
            int kind = EquipmentTab ? 2 : 0;
            for (int i = 0; i < l.Count; i++)
            {
                var first = l[i];
                if (first.Data[9] == 1) continue;
                first.Data[9] = 1;
                var row = new Row { Index = first.Index, Total = 1, First = first };
                for (int k = i + 1; k < l.Count; k++)
                    if (l[k].Index == first.Index) { row.Total++; l[k].Data[9] = 1; }
                foreach (var x in l.Items)
                    if (x.Index == first.Index && x.Kind == kind) { if (x.Field16 == 0) row.Pristine++; else row.Damaged++; }
                if (l == Ammo) { row.Pristine = row.Total; row.Damaged = 0; }
                if (AmmoTab) row.Pristine = row.Total;
                ItemRecord damaged = l.Items.Find(x => x.Index == first.Index && x.Kind == first.Kind && x.Field16 != 0);
                if (dealer)
                {
                    row.Value = Value(first, 1f);
                    row.Price = Value(first, 1f);
                }
                else
                {
                    row.Value = AmmoTab || first.Field16 == 0 ? Value(first, Sell) : Value(first, SellDamaged);
                    var priced = damaged ?? first;
                    row.Price = damaged != null && !AmmoTab ? Value(priced, SellDamaged) : Value(priced, Sell);
                }
                rows.Add(row);
            }
            return rows;
        }

        /// <summary>FUN_1000328c.</summary>
        public static void Arrange(List<ItemRecord> l)
        {
            int n = l.Count;
            for (int pass = 0; pass < n; pass++)
                for (int j = 0; j < n - 1; j++)
                    if (l[j + 1].Index < l[j].Index) (l[j], l[j + 1]) = (l[j + 1], l[j]);
            for (int i = 0; i < n;)
            {
                int run = 1;
                for (int k = i + 1; k < n; k++)
                    if (l[k].Index == l[i].Index && l[k].Kind == l[i].Kind) run++;
                for (int j = i; j < i - 1 + run; j++)
                    if (l[j + 1].Field16 < l[j].Field16) (l[j], l[j + 1]) = (l[j + 1], l[j]);
                i += run + 1;
            }
        }

        public enum Result { Done, NoSpace, NoFunds, NothingToRepair, Ammo }

        /// <summary>
        /// The sale of FUN_10004455 0x10004455 (spot 7 or a double click): the first record of the index, or with a pristine
        /// one and not on the ammunition tab the first damaged one of the tab's kind; funds + its value (0.15 damaged, else
        /// 0.6); a weapon's +0xa = -1; it leaves the list itself (comparator off) and goes to the dealer, a damaged one is
        /// thrown away. Returns the record sold.
        /// </summary>
        public ItemRecord SellOne(int index)
        {
            var it = Stock.FirstOfIndex(index);
            if (it == null) return null;
            if (it.Field16 == 0 && !AmmoTab)
            {
                int kind = EquipmentTab ? 2 : 0;
                var damaged = Stock.Items.Find(x => x.Index == it.Index && x.Kind == kind && x.Field16 != 0);
                if (damaged != null) it = damaged;
            }
            _c.Started = true;
            _c.Funds += Value(it, it.Field16 == 0 ? Sell : SellDamaged);
            if (it.Kind == 0) it.FieldA = -1;
            Stock.RemoveRecord(it);
            if (it.Field16 == 0 || AmmoTab) Offer.Add(it);
            return it;
        }

        /// <summary>The purchase (spot 8 or a double click): the first offered record of the index if it fits the drop
        /// ship (ammunition: one more ton) and the funds; it goes to the unit, and the first offered record of its index
        /// leaves the dealer.</summary>
        public Result BuyOne(int index, ShellTables t)
        {
            var it = Offer.FirstOfIndex(index);
            int load = Load(t);
            bool fits = AmmoTab ? load + 1 < 0xc9 : load + Space(it.Index, t) <= Capacity;
            if (!fits) return Result.NoSpace;
            int cost = Value(it, 1f);
            if (_c.Funds < cost) return Result.NoFunds;
            _c.Started = true;
            _c.Funds -= cost;
            Stock.Add(it);
            Offer.RemoveSameIndex(it);
            return Result.Done;
        }

        /// <summary>The repair (spot 9): the last damaged record of the index and kind in the unit's list, for 0.15 of its
        /// +4; +0x16 cleared.</summary>
        public Result RepairOne(ItemRecord of)
        {
            if (AmmoTab) return Result.Ammo;
            ItemRecord damaged = null;
            foreach (var x in Stock.Items)
                if (x.Index == of.Index && x.Kind == of.Kind && x.Field16 != 0) damaged = x;
            if (damaged == null) return Result.NothingToRepair;
            int cost = Value(damaged, SellDamaged);
            if (_c.Funds < cost) return Result.NoFunds;
            damaged.Field16 = 0;
            _c.Funds -= cost;
            return Result.Done;
        }

        /// <summary>FUN_100029e8 0x100029e8: one figure of the weapon record (0x100ac088 + index × 0x40: heat, damage,
        /// +8, +0xc, +0x10, range, mass, criticals, ammunition); equipment and ammunition show the name, "1" and "N/A"
        /// (equipment criticals from 0x100abad0).</summary>
        public static string StatText(int field, int code, bool equipment, bool ammo, ShellTables t)
        {
            int i = code / 100;
            if (!equipment && !ammo)
            {
                var w = t.Weapon(i);
                switch (field)
                {
                    case 0: return code % 100 == 0 ? t.WeaponName(i) : $"{t.WeaponName(i)} #{code % 100}";
                    case 1: return w.Heat.ToString();
                    case 2: return w.Damage == 0 ? "-" : w.Damage < 0 ? $"{-w.Damage}/missile" : w.Damage.ToString();
                    case 3: return w.Field08 < 0 ? "-" : w.Field08.ToString();
                    case 4: return w.Field0C == -1 ? "-" : w.Field0C == 1 ? "1" : $"1-{w.Field0C}";
                    case 5:
                        if (w.Field10 == -1) return "-";
                        return w.Field0C + 1 == w.Field10 ? w.Field10.ToString() : $"{w.Field0C + 1}-{w.Field10}";
                    case 6: return w.Range == -1 ? "-" : w.Range.ToString();
                    case 7:
                        {
                            int m = w.Mass;
                            if (m % 10 != 0) return $"{m / 100}.{m / 10 % 10}{m % 10}T";
                            return m % 100 == 0 ? $"{m / 100}T" : $"{m / 100}.{m / 10 % 10}T";
                        }
                    case 8: return w.Slots.ToString();
                    case 9: return w.ShotsPerTon < 0 ? "-" : w.ShotsPerTon.ToString();
                }
            }
            else
                switch (field)
                {
                    case 0: return equipment ? t.Equipment(i).Name : t.WeaponName(i);
                    case 7: return "1";
                    case 8: return equipment ? t.Equipment(i).Space.ToString() : "1";
                    default: return "N/A";
                }
            return "";
        }
    }
}
