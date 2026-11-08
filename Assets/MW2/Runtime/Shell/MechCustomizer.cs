using System;
using System.Collections.Generic;
using MW2.Formats;

namespace MW2.Shell
{
    /// <summary>
    /// The logic of Customizing 0xc02f (custom.cpp, MW2SHELL.DLL; screen in CustomizingScreen). The mech DAT_100c771c is
    /// edited in place; DAT_100c7720 is its clone taken when it is selected: the undo point and what the price is
    /// measured against (FUN_10011862). Items taken from the inventory go to DAT_100c7728, items made from removed
    /// criticals to DAT_100c772c, so an undo (FUN_10011a36) can give them back. The inventory is split into the Arms
    /// Merchant's lists (FUN_10049c6c; ammunition DAT_100cd138, energy 13c, projectiles 140, missiles 144, equipment
    /// 148), with two pseudo items in the equipment list: 9 "Armor Allocation" and 0 "Engine", which open the armour and
    /// engine panels. Modes 0 (novice) and 1 edit the unit's mechs; mode 2 (instant action) edits the MechLab mech
    /// DAT_100d84d0 against the stock of FUN_100457e6 (OpenLab).
    /// Criticals: weapons index × 100 + number (1..), equipment by the table at 0x100a8198 (heat sinks 8000 + n, jump
    /// jets 8500 + n, CASE 9550 + n, MASC 7000, endo steel 9000, ferro-fibrous 9500, XL engine 7850), ammunition
    /// 10000 + index × 10000 + 100 + bin (FUN_1004a7a1 / Mech_AddAmmoBin 0x1004839b). 0 is an empty slot, -1 none.
    /// </summary>
    public sealed class MechCustomizer
    {
        readonly Career _c;
        readonly ShellTables _t;
        public readonly ArmsDealer Stock;                   // the split lists DAT_100cd138..148
        public MechRecord Mech, Backup;                     // DAT_100c771c, DAT_100c7720
        public int Index;                                   // DAT_100c7718
        public readonly List<ItemRecord> Taken = new List<ItemRecord>(), Removed = new List<ItemRecord>();   // 7728, 772c
        /// <summary>DAT_100c7734: the inventory list shown; DAT_100c765c: its tab (2 missiles, 3 energy, 4 projectiles,
        /// 5 equipment, 6 ammunition).</summary>
        public ShellCollection Current;
        public int Tab;
        /// <summary>DAT_100c7738: assigning endo steel / ferro-fibrous criticals one by one; DAT_100c75c0 the code,
        /// DAT_100c75bc how many are left.</summary>
        public bool Assigning;
        public int AssignCode, AssignLeft;
        /// <summary>DAT_100c773c: the armour panel (list DAT_100a0338) is up; DAT_100c7740: the engine panel (DAT_1009ff48).</summary>
        public bool ArmorPanel, EnginePanel;
        /// <summary>FUN_100280ba with a single "#Ok" button: the message only.</summary>
        public Action<string> Say = _ => { };

        public MechCustomizer(Career c, ShellTables t)
        {
            _c = c;
            _t = t;
            Stock = new ArmsDealer(c);
        }

        public int Section => Mech.I32(0x364);

        // ---- opening, leaving ----

        /// <summary>
        /// FUN_10016428 0x10016428 (modes 0 and 1): the first unit mech, its section kept when valid, recomputed
        /// (FUN_1004ad22) and cloned; the inventory split (FUN_10049c6c) with the pseudo items 9 and 0 appended to the
        /// equipment list (+0x16 is left as HeapAlloc returns it; taken as 0), the missile tab shown.
        /// </summary>
        public void Open()
        {
            Index = 0;
            Mech = _c.UnitMechs[0];
            if (Section < 0 || Section > 7) Mech.SetI32(0x364, 0);
            Mech.Refit(_t);
            Backup = Mech.Clone();
            Stock.Split();
            foreach (int i in new[] { 9, 0 })
                Stock.Equipment.Add(new ItemRecord { Index = i, Price = 0, Kind = 2, FieldA = 0, FieldE = 0, Field12 = 0 });
            Tab = 2;
            Current = Stock.Missiles;
        }

        /// <summary>
        /// FUN_10016428 0x10016428 mode 2 and FUN_100457e6 0x100457e6: the MechLab mech (DAT_100d84d0) is edited in place,
        /// recomputed and cloned, and the stock is everything of its technology (Inner Sphere when its +0x9cf is not 0, the
        /// weapon table's field 0x100ac0c4): every weapon (price 0, no crates), the ammunition of those that take any
        /// (0x100ac0c0 above 0) and the equipment 0..10, split into the lists (FUN_10049c6c).
        /// </summary>
        public void OpenLab(MechRecord lab)
        {
            Index = 0;
            Mech = lab;
            Mech.Refit(_t);
            Backup = Mech.Clone();
            if (Section < 0 || Section > 7) Mech.SetI32(0x364, 0);
            _c.UnitWeapons.Clear();
            _c.UnitAmmo.Clear();
            int technology = lab.Technology != 0 ? 1 : 0;
            for (int i = 0; i < 0x42; i++)
                if (_t.Weapon(i).Technology == technology)
                    _c.UnitWeapons.Add(new ItemRecord { Index = i, Price = 0, Kind = 0, FieldA = -1, FieldE = 0xbdf, Field12 = 100, Field16 = 0 });
            for (int i = 0; i < 0x42; i++)
                if (_t.Weapon(i).Technology == technology && _t.Weapon(i).AmmoPrice > 0)
                    _c.UnitAmmo.Add(new ItemRecord { Index = i, Price = 0, Kind = 1, FieldA = i, FieldE = 1, Field12 = 0xbdf, Field16 = 100 });
            for (int i = 0; i < 0xb; i++)
                _c.UnitWeapons.Add(new ItemRecord { Index = i, Price = 0, Kind = 2, FieldA = 0, FieldE = 0, Field12 = 0, Field16 = 0 });
            Stock.Split();
            Tab = 2;
            Current = Stock.Missiles;
        }

        /// <summary>The end of FUN_10018e95 0x10018e95: the "Engine" records leave the equipment list, the lists go back
        /// (FUN_10049df5, which drops index 9) and DAT_100c7728 / 772c are emptied.</summary>
        public void Close()
        {
            var eq = Stock.Equipment.Items;
            for (int k = 0; k < eq.Count; k++)
                if (eq[k].Kind == 2 && eq[k].Index == 0) Stock.Equipment.RemoveSameIndex(eq[k]);
            Stock.Merge();
            if (_c.Mode == 2)
            {
                _c.UnitWeapons.Clear();     // the MechLab stock is made again each time (FUN_100457e6)
                _c.UnitAmmo.Clear();
            }
            Taken.Clear();
            Removed.Clear();
        }

        /// <summary>FUN_1001a993 0x1001a993 / FUN_1001a9ef 0x1001a9ef (after an accepted FUN_10011ce9): the next / previous
        /// unit mech, wrapping; recomputed and cloned again (the screen part of cases 7 / 8). The panels' texts are freed
        /// there but DAT_100c773c / DAT_100c7740 keep their values.</summary>
        public void Cycle(int step)
        {
            Index += step;
            if (Index >= _c.UnitMechs.Count) Index = 0;
            if (Index < 0) Index = _c.UnitMechs.Count - 1;
            Mech = _c.UnitMechs[Index];
            Mech.Refit(_t);
            Backup = Mech.Clone();
        }

        /// <summary>The tabs (cases 2..6 of FUN_10018e95): the list shown.</summary>
        public void ShowTab(int tab)
        {
            Tab = tab;
            Current = tab == 2 ? Stock.Missiles : tab == 3 ? Stock.Energy : tab == 4 ? Stock.Projectiles :
                      tab == 5 ? Stock.Equipment : Stock.Ammo;
        }

        // ---- price, accepting, undo ----

        /// <summary>
        /// FUN_10011862 0x10011862: the price of the changes: armour 625 a point of the per-section difference of front
        /// plus rear (FUN_1004a4a1), 50000 for each critical that differs (an empty 0 against none -1 does not), and an
        /// engine changed tons × rating × 5000 / 75 (XL 20000 / 75).
        /// </summary>
        public static int Cost(MechRecord m, MechRecord b)
        {
            int crits = 0, armor = 0;
            for (int s = 0; s < 8; s++)
            {
                int o = 0x8e0 + s * 0x12;
                armor += Math.Abs(b.I32(o + 8) - m.I32(o + 8) + (b.I32(o + 12) - m.I32(o + 12)));
                for (int j = 0; j < 12; j++)
                    if (m.Crit(s, j) != b.Crit(s, j) && (m.Crit(s, j) != 0 || b.Crit(s, j) != -1)) crits++;
            }
            int engine = 0;
            if (m.I32(0x20c) != b.I32(0x20c))
                engine = m.I32(0x200) / 100 * m.I32(0x210) * (m.I32(0x20c) < 10000 ? 5000 : 20000) / 0x4b;
            return engine + crits * 50000 + MechRepair.ArmorCost(armor);
        }

        public int Price => Cost(Mech, Backup);

        /// <summary>FUN_1004ad8f 0x1004ad8f: the mech needs repairs to reach its pristine copy (FUN_1005c500 dry run) and
        /// they are not only criticals (+0x9cd, which FUN_1005c500 clears when a critical differs).</summary>
        public bool NeedsRepair()
        {
            var pristine = _c.UnitCopies.Find(m => m.MechId == Mech.MechId);
            if (pristine == null) return false;
            int cost = new MechRepair(_c, _t).Cost(Mech, pristine, true);
            return cost != 0 && Mech.Data[0x9cd] != 1;
        }

        /// <summary>The "Yes" of FUN_10011ce9 0x10011ce9 after the checks: FUN_10011c64 0x10011c64 replaces the pristine copy
        /// with a clone (both +0x994 = 1); the career pays <paramref name="cost"/> (mode 1) and is marked changed.</summary>
        public void Commit(int cost)
        {
            var old = _c.UnitCopies.Find(m => m.MechId == Mech.MechId);
            if (old != null) _c.UnitCopies.Remove(old);
            var copy = Mech.Clone();
            _c.UnitCopies.Add(copy);
            Mech.SetI32(0x994, 1);
            copy.SetI32(0x994, 1);
            if (_c.Mode == 1) _c.Funds -= cost;
            _c.Started = true;
            Mech.Data[0x9cc] = 0;
        }

        /// <summary>The end of FUN_10011ce9: DAT_100c7728 / 772c emptied, their records kept.</summary>
        public void ForgetItems()
        {
            Taken.Clear();
            Removed.Clear();
        }

        /// <summary>
        /// FUN_10011a36 0x10011a36: the clone copied back over the mech (its selected section kept, +0x9cc cleared), the
        /// items taken returned (weapons and equipment by FUN_10049a12, ammunition appended), the items made from removed
        /// criticals taken out again (FUN_10049936: the first record of the index in a category list; ammunition: every
        /// record of the index, the one after each removal skipped as the original loop does).
        /// </summary>
        public void Undo()
        {
            int section = Mech.I32(0x364);
            Buffer.BlockCopy(Backup.Data, 0, Mech.Data, 0, MechRecord.Size);
            Mech.SetI32(0x364, section);
            Mech.Data[0x9cc] = 0;
            foreach (var it in Taken)
            {
                if (it.Kind == 0 || it.Kind == 2) Stock.PlaceUnit(it);
                else if (it.Kind == 1) Stock.Ammo.Add(it);
                else Say("Unknown item flag#Ok");
            }
            foreach (var it in Removed)
            {
                if (it.Kind == 0 || it.Kind == 2)
                {
                    foreach (var l in new[] { Stock.Projectiles, Stock.Energy, Stock.Missiles, Stock.Equipment })
                        if (l.FirstOfIndex(it.Index) != null) { l.RemoveSameIndex(it); break; }
                }
                else if (it.Kind == 1)
                {
                    var ammo = Stock.Ammo;
                    for (int k = 0; k < ammo.Count; k++)
                        if (ammo[k].Index == it.Index) ammo.RemoveSameIndex(ammo[k]);
                }
            }
        }

        // ---- snapshot around an addition ----

        static readonly (int at, int len)[] Saved =
        {
            (0x970, 4), (0x270, 4), (0x28c, 0x19 * 4), (0x2f0, 0xe * 8), (0x280, 0xc), (0x234, 0xc), (0x228, 0xc),
            (0x278, 8), (0x20c, 0x14)
        };
        readonly byte[] _saved = new byte[4 + 4 + 0x19 * 4 + 0xe * 8 + 0xc + 0xc + 0xc + 8 + 0x14];

        /// <summary>FUN_10014386 0x10014386: the fields an addition may change (not the criticals).</summary>
        void Snapshot()
        {
            int p = 0;
            foreach (var (at, len) in Saved) { Buffer.BlockCopy(Mech.Data, at, _saved, p, len); p += len; }
        }

        /// <summary>FUN_1001453b 0x1001453b.</summary>
        void Restore()
        {
            int p = 0;
            foreach (var (at, len) in Saved) { Buffer.BlockCopy(_saved, p, Mech.Data, at, len); p += len; }
        }

        /// <summary>The Arrow IV refusal of FUN_100147ef puts back only the bins and the weapons.</summary>
        void RestoreBinsAndWeapons()
        {
            Buffer.BlockCopy(_saved, 8, Mech.Data, 0x28c, 0x19 * 4);
            Buffer.BlockCopy(_saved, 8 + 0x19 * 4, Mech.Data, 0x2f0, 0xe * 8);
        }

        // ---- adding ----

        /// <summary>FUN_1004a7a1 0x1004a7a1: the critical code of an inventory item.</summary>
        public static int Code(int index, int kind)
        {
            if (kind == 0) return index * 100;
            if (kind == 1) return (index * 100 + 1) * 100 + 10000;
            if (kind == 2)
                switch (index)
                {
                    case 0: return 0x1eaa;
                    case 1: case 2: return 8000;
                    case 3: case 4: return -1;
                    case 5: return 0x254e;
                    case 6: return 0x2134;
                    case 10: return 7000;
                }
            return 0;   // left unset by the original for the other equipment
        }

        /// <summary>
        /// FUN_100147ef 0x100147ef: add one item of the list to the selected section. In career mode 1 an undamaged record
        /// of the item must be in the list shown (not checked on the ammunition tab). Weapons get the next free number
        /// and one of the 14 weapon entries (FUN_100158f5), ammunition a bin (Mech_AddAmmoBin); equipment: heat sinks of
        /// the mech's type only (FUN_10014703 first resets the type of a mech without any), MASC once, a jump jet only
        /// below the walking MP (FUN_10015ad9), CASE only on Inner Sphere and once a section; 3 / 4 change the heat sink
        /// type, 7 / 8 start assigning ferro-fibrous / endo steel criticals. Jump jets only in the torsos and legs, Arrow
        /// IV only in the side torsos and arms. Then FUN_10014fe2 places it and takes it from the stock. Returns whether
        /// it was added.
        /// </summary>
        public bool Add(int index, int kind)
        {
            Snapshot();
            int code = Code(index, kind);
            int crits = -1;
            if (_c.Mode == 1)
            {
                bool found = false;
                foreach (var it in Current.Items)
                    if (it.Index == index && it.Kind == kind && it.Field16 == 0) found = true;
                if (!found && Tab != 6) return false;
            }
            if (kind == 0)
            {
                crits = _t.Weapon(index).Slots;
                code = NumberWeapon(code);
                if (code == 0) return false;
            }
            else if (kind == 1)
            {
                crits = 1;
                code = Mech.AddAmmoBin(code);
                if (code == 0)
                {
                    Say("No more ammo may be|placed for this weapon.#Ok");
                    Restore();
                    return false;
                }
            }
            else if (kind == 2)
            {
                crits = _t.Equipment(index).Space;
                switch (index)
                {
                    case 0:
                        if (Assigning) return false;
                        break;
                    case 3:
                        if (Mech.I32(0x228) != 0) { Say("Heat sinks do not need to be upgraded#Ok"); return false; }
                        UpgradeHeatSinks();
                        return true;
                    case 4:
                        if (Mech.I32(0x228) != 1) { Say("Heat sinks do not need to be downgraded#Ok"); return false; }
                        DowngradeHeatSinks();
                        return true;
                    case 7:
                    case 8:
                        if (Assigning) return false;
                        AssignLeft = Mech.Technology == 0 ? 7 : 0xe;
                        AssignCode = index == 7 ? 0x251c : 9000;
                        return StartAssigning(AssignCode, AssignLeft);
                    case 9:
                        if (Assigning) return false;
                        break;
                }
                if (code == 8000)
                {
                    if (index == 1)
                    {
                        HeatSinkType(0);
                        if (Mech.I32(0x228) == 1) { Say("Double heat sinks must first be downgraded.#Ok"); return false; }
                        code = AddHeatSink();
                    }
                    else if (index == 2)
                    {
                        HeatSinkType(1);
                        if (Mech.I32(0x228) == 0) { Say("Single heat sinks must first be upgraded#Ok"); return false; }
                        code = AddHeatSink();
                        crits = Mech.Technology == 0 ? 2 : 3;
                    }
                    else return false;
                }
                else if (code == 7000)
                {
                    if (HasMasc()) { Say("Mech already contains MASC!#Ok"); return false; }
                    Mech.SetI32(0x270, Mech.I32(0x988) * 100);
                    Mech.SetI32(0x974, 1);
                    crits = Mech.I32(0x988);
                }
                else if (code == 0x2134)
                {
                    code = AddJumpJet();
                    if (code == 0) return false;
                }
                else if (code == 0x254e)
                {
                    if (Mech.Technology == 0) { Say("Clan chassis already contains CASE#Ok"); return false; }
                    int flags = 0x8f0 + Section * 0x12;
                    for (int j = 0; j < 12; j++)
                    {
                        if (Mech.Crit(Section, j) / 10 == 0x3bb) { Say("CASE already present in this piece#Ok"); return false; }
                        Mech.SetI16(flags, Mech.I16(flags) | 0x100);
                    }
                    code = AddCase();
                }
            }
            // LAB_10014d86
            if (Section >= 0)
            {
                int s = Section;
                if (code - code % 100 == 0x2134 && (s == 0 || s == 4 || s == 5))
                {
                    Say("Jump Jets may only|be assigned to torso|or leg sections.#Ok");
                    Restore();
                    return false;
                }
                if (code - code % 100 == 0xd48 && s != 3 && s != 1 && s != 4 && s != 5)
                {
                    Say("Arrow IV (C) may only|be assigned to left/right torso|or arm sections.#Ok");
                    RestoreBinsAndWeapons();
                    return false;
                }
                if (code - code % 100 == 0x1964 && s != 3 && s != 1 && s != 4 && s != 5)
                {
                    Say("Arrow IV may only|be assigned to left/right torso|or arm sections|#Ok");
                    Restore();
                    return false;
                }
                if (!PlaceAndTake(s, code, crits))
                {
                    Say("Insufficient criticals|for item placement.#Ok");
                    Restore();
                    return false;
                }
            }
            Mech.Refit(_t);
            Mech.Data[0x9cc] = 1;
            return true;
        }

        /// <summary>FUN_100158f5 0x100158f5: the lowest number not used by this weapon's criticals, and a free weapon entry
        /// (id, -1); 0 when all 14 are used.</summary>
        int NumberWeapon(int code)
        {
            int b = code - code % 100;
            int n = Lowest(v => v / 100 == b / 100, v => v % 100);
            for (int k = 0; k < 0xe; k++)
                if (Mech.I32(0x2f0 + k * 8) == -1)
                {
                    Mech.SetI32(0x2f0 + k * 8, b + n);
                    Mech.SetI32(0x2f4 + k * 8, -1);
                    return b + n;
                }
            Say("Only 14 Weapons allowed on chassis#Ok");
            return 0;
        }

        /// <summary>The numbering loop of FUN_100158f5 / FUN_10015a18 / FUN_10015bb7: from 1 up, the first number no
        /// critical of the kind carries.</summary>
        int Lowest(Func<int, bool> ofKind, Func<int, int> number)
        {
            int n = 1;
            for (bool again = true; again; )
            {
                again = false;
                for (int s = 0; s < 8 && !again; s++)
                    for (int j = 0; j < 12; j++)
                    {
                        int v = Mech.Crit(s, j);
                        if (ofKind(v) && number(v) == n) { n++; again = true; break; }
                    }
            }
            return n;
        }

        /// <summary>FUN_10014703 0x10014703: without any heat sink criticals the type becomes <paramref name="type"/>.</summary>
        void HeatSinkType(int type)
        {
            for (int s = 0; s < 8; s++)
                for (int j = 0; j < 12; j++)
                    if (Mech.Crit(s, j) / 100 * 100 == 8000) return;
            Mech.SetI32(0x228, type);
        }

        /// <summary>FUN_10015a18 0x10015a18: one more ton (FUN_10046bfb recounts the criticals right after), the next
        /// heat sink number.</summary>
        int AddHeatSink()
        {
            Mech.SetI32(0x22c, Mech.I32(0x22c) + 100);
            Mech.HeatSinks();
            Mech.Totals();
            return 8000 + Lowest(v => v / 100 * 100 == 8000, v => v % 100);
        }

        /// <summary>FUN_10016071 0x10016071.</summary>
        bool HasMasc()
        {
            for (int s = 0; s < 8; s++)
                for (int j = 0; j < 12; j++)
                    if (Mech.Crit(s, j) / 10 == 700) return true;
            return false;
        }

        /// <summary>FUN_10015ad9 0x10015ad9: below the walking MP one more jump MP (Mech_Movement, Mech_Recalc) and the
        /// next jump jet code; else 0.</summary>
        int AddJumpJet()
        {
            if (Mech.I32(0x288) >= Mech.I32(0x280)) return 0;
            Mech.SetI32(0x288, Mech.I32(0x288) + 1);
            int used = Lowest(v => v / 100 * 100 == 0x2134, v => v % 100) - 1;
            Mech.Movement();
            Mech.Totals();
            return used + 0x2135;
        }

        /// <summary>FUN_10015bb7 0x10015bb7: +0x970 = 1, the CASE mass (Mech_Recalc counts it again from the criticals),
        /// the next CASE number (by tens).</summary>
        int AddCase()
        {
            Mech.SetI32(0x970, 1);
            if (Mech.Technology == 0) Mech.SetI32(0x278, 0);
            else if (Mech.Technology == 1)
            {
                Mech.SetI32(0x278, Mech.I32(0x278) + 0x32);
                if (Mech.I32(0x278) > 400) Mech.SetI32(0x278, 400);
            }
            Mech.Totals();
            return 0x254e + Lowest(v => v / 10 * 10 == 0x254e, v => v % 10);
        }

        /// <summary>
        /// FUN_10014fe2 0x10014fe2: FUN_10015485 places the criticals; then in mode 1 the item leaves the stock: a weapon's
        /// first undamaged record of the list shown, ammunition the first record of the index, equipment the first record
        /// of the equipment list with the index (any heat sink for a heat sink); each goes to DAT_100c7728.
        /// </summary>
        bool PlaceAndTake(int section, int code, int crits)
        {
            if (code < 1) return false;
            if (!Place(section, code, crits)) return false;
            if (_c.Mode != 1) return true;
            int kind = Campaign.ItemKind(code), idx = Career.ItemIndex(kind, code, _t);
            if (kind == 0)
            {
                var it = Current.Items.Find(x => x.Field16 == 0 && x.Index == idx && x.Kind == 0);
                if (it != null)
                {
                    Current.RemoveRecord(it);
                    it.Kind = 0;
                    Taken.Add(it);
                }
            }
            else if (kind == 1)
            {
                var it = Stock.Ammo.FirstOfIndex(idx);
                if (it != null)
                {
                    Stock.Ammo.RemoveSameIndex(it);
                    it.Kind = 1;
                    Taken.Add(it);
                }
            }
            else if (kind == 2)
            {
                int table = _t.Item(idx).Code;
                foreach (var it in Stock.Equipment.Items)
                {
                    if (table == 8000 && (it.Index == 1 || it.Index == 2) || table == 0x2134 && it.Index == 6 ||
                        table == 0x254e && it.Index == 5)
                    {
                        Current.RemoveRecord(it);
                        Taken.Add(it);
                        return true;
                    }
                    if (table == 7000 && it.Index == 10)
                    {
                        Stock.Equipment.RemoveSameIndex(it);
                        Taken.Add(it);
                        return true;
                    }
                }
            }
            return true;
        }

        /// <summary>
        /// FUN_10015485 0x10015485: the code into the section's empty (0) slots, skipping slots whose pristine copy holds
        /// a structural item (7251..7699); Arrow IV overflows into the neighbouring piece (right torso ↔ right arm, left
        /// torso ↔ left arm). Without room everything placed is taken out again.
        /// </summary>
        bool Place(int section, int code, int crits)
        {
            if (code < 1) return false;
            var pristine = _c.UnitCopies.Find(m => m.MechId == Mech.MechId);
            for (int j = 0; crits != 0 && j < 12; j++)
                if (Mech.Crit(section, j) == 0 &&
                    (pristine == null || pristine.Crit(section, j) < 0x1c53 || pristine.Crit(section, j) > 0x1e13))
                {
                    Mech.SetCrit(section, j, code);
                    crits--;
                }
            bool arrow = code - code % 100 == 0xd48 || code - code % 100 == 0x1964;
            if (arrow && crits != 0)
            {
                int other = section == 1 ? 4 : section == 3 ? 5 : section == 4 ? 1 : section == 5 ? 3 : section;
                for (int j = 0; crits != 0 && j < 12; j++)
                    if (Mech.Crit(other, j) == 0) { Mech.SetCrit(other, j, code); crits--; }
                if (crits != 0)
                {
                    for (int j = 0; j < 12; j++)
                    {
                        if (Mech.Crit(section, j) == code) Mech.SetCrit(section, j, 0);
                        if (Mech.Crit(other, j) == code) Mech.SetCrit(other, j, 0);
                    }
                    return false;
                }
            }
            else if (crits != 0)
            {
                for (int j = 0; j < 12; j++)
                    if (Mech.Crit(section, j) == code) Mech.SetCrit(section, j, 0);
                return false;
            }
            return true;
        }

        // ---- heat sink type ----

        /// <summary>
        /// FUN_10015cbe 0x10015cbe: every heat sink critical cleared, the heat sink tons lowered by those above the
        /// engine's own (rating / 25 above 10), one "Double heat sink" item (equipment 2, its price from the 0x100a8198 row
        /// 2) per critical into the stock and DAT_100c772c, the type set to double.
        /// </summary>
        void UpgradeHeatSinks()
        {
            int n = 0;
            for (int s = 0; s < 8; s++)
                for (int j = 0; j < 12; j++)
                    if (Mech.Crit(s, j) / 100 == 0x50) { n++; Mech.SetCrit(s, j, 0); }
            int own = Mech.I32(0x210) / 0x19;
            own = own < 0xb ? 0 : own - 10;
            for (int k = 0; k < n - own; k++) Mech.SetI32(0x22c, Mech.I32(0x22c) - 100);
            for (int k = 0; k < n; k++)
            {
                var it = new ItemRecord { Index = 2, Kind = 2, FieldA = -1, FieldE = -1, Field12 = -1, Field16 = 0 };
                it.Price = _t.Item(2).Price;
                Stock.Equipment.Add(it);
                Removed.Add(it);
            }
            Mech.HeatSinks();
            Mech.Totals();
            Mech.SetI32(0x228, 1);
            Say(n < 1 ? "Chassis has been modified to accept|double heat sinks#Ok"
                      : "Upgraded heat sinks have been placed in inventory#Ok");
        }

        /// <summary>FUN_10015ee3 0x10015ee3: of each heat sink only its first slot stays, the type set to single.</summary>
        void DowngradeHeatSinks()
        {
            for (int s = 0; s < 8; s++)
                for (int j = 0; j < 12; j++)
                {
                    int v = Mech.Crit(s, j);
                    if (v / 100 != 0x50) continue;
                    for (int s2 = 0; s2 < 8; s2++)
                        for (int j2 = 0; j2 < 12; j2++)
                            if ((s != s2 || j != j2) && Mech.Crit(s2, j2) / 100 == 0x50 && v % 100 == Mech.Crit(s2, j2) % 100)
                                Mech.SetCrit(s2, j2, 0);
                }
            Say("Chassis has been modified to accept|single heat sinks#Ok");
            Mech.SetI32(0x228, 0);
        }

        // ---- endo steel / ferro-fibrous ----

        /// <summary>FUN_10012fba 0x10012fba: enough empty slots on the whole mech for <paramref name="n"/> criticals, then
        /// the assigning list (FUN_10013161) replaces the inventory list.</summary>
        public bool StartAssigning(int code, int n)
        {
            if (code < 0 || n < 0) return false;
            int free = 0;
            for (int s = 0; s < 8; s++)
                for (int j = 0; j < 12; j++)
                    if (Mech.Crit(s, j) == 0) free++;
            if (free < n)
            {
                Say("Not enough free criticals on mech#Ok");
                return false;
            }
            ArmorPanel = EnginePanel = false;
            Assigning = true;
            return true;
        }

        /// <summary>FUN_1001340c 0x1001340c: one critical into the first empty slot of the section.</summary>
        public bool AssignOne()
        {
            if (AssignLeft <= 0) return false;
            for (int j = 0; j < 12; j++)
                if (Mech.Crit(Section, j) == 0)
                {
                    Mech.SetCrit(Section, j, AssignCode);
                    AssignLeft--;
                    return true;
                }
            Say("Insufficient criticals|for item placement.#Ok");
            return false;
        }

        /// <summary>
        /// The rest of FUN_10018c56 0x10018c56 once none is left: +0x9cc, the endo steel (+0x240) or ferro-fibrous
        /// (+0x24c) flag, in mode 1 the item (equipment 8 / 7) leaves the stock into DAT_100c7728; assigning ends and
        /// the mech is recomputed.
        /// </summary>
        public void FinishAssigning()
        {
            Mech.Data[0x9cc] = 1;
            if (AssignCode == 9000) Mech.SetI32(0x240, 1);
            else if (AssignCode == 0x251c) Mech.SetI32(0x24c, 1);
            if (_c.Mode == 1)
            {
                int want = AssignCode == 9000 ? 8 : 7;
                var eq = Stock.Equipment.Items;
                for (int k = 0; k < eq.Count; k++)
                    if (eq[k].Kind == 2 && eq[k].Index == want)
                    {
                        var it = eq[k];
                        Stock.Equipment.RemoveSameIndex(it);
                        Taken.Add(it);
                        break;
                    }
            }
            Assigning = false;
            Mech.Refit(_t);
        }

        // ---- removing ----

        /// <summary>FUN_1001aa50 0x1001aa50: the critical in a slot of the section, unless structural (7300..7999);
        /// false with the message for those.</summary>
        public void RemoveSlot(int slot)
        {
            if (Section < 0) return;
            int v = Mech.Crit(Section, slot);
            if (v < 0) return;
            int b = v / 100 * 100;
            if (b < 0x1c84 || b > 7999) Remove(v);
            else Say("Selected critical|can not be removed.#Ok");
        }

        /// <summary>
        /// FUN_100139a2 0x100139a2: in mode 1 not when the drop ship would overflow (FUN_100027d0 + FUN_10061c29 of the
        /// code &gt; 200). Endo steel / ferro-fibrous go through FUN_10014153. Otherwise every slot of the code is
        /// cleared (FUN_10013e40) and an item made for it: a weapon (its entry removed, FUN_10013f6f), ammunition (its bin,
        /// FUN_10013ed6), a heat sink (single or double by the type, FUN_10014078), MASC, a jump jet (FUN_1001403a), CASE
        /// (FUN_100140aa); it goes into the stock and DAT_100c772c.
        /// </summary>
        public void Remove(int code)
        {
            int load = Stock.Load(_t);
            if (code <= 0) return;
            int kind = Campaign.ItemKind(code), idx = Career.ItemIndex(kind, code, _t);
            if (_c.Mode == 1 && load + ArmsDealer.Space(code, _t) > 200)
            {
                Say("Cannot remove critical, dropship inventory is full|#Ok");
                return;
            }
            if (kind == 2)
            {
                int table = _t.Item(idx).Code;
                if (table == 9000 || table == 0x251c) { RemoveArmorKind(table); return; }
            }
            if (Clear(code) == 0) return;
            if (kind == 0)
            {
                RemoveWeaponEntry(code);
                Mech.WeaponMass(_t);
                Mech.AmmoMass();
                Mech.Totals();
                var it = new ItemRecord { Index = idx, Kind = 0, FieldA = -1, FieldE = -1, Field12 = 100, Field16 = 0 };
                Career.InitWeapon(it, _t);
                Stock.PlaceUnit(it);
                Removed.Add(it);
            }
            else if (kind == 1)
            {
                RemoveBin(code);
                Mech.WeaponMass(_t);
                Mech.AmmoMass();
                Mech.Totals();
                var it = new ItemRecord { Index = idx, Kind = 1, FieldE = 1, Field12 = -1, Field16 = 100 };
                Career.InitAmmo(it, _t);
                Stock.Ammo.Add(it);
                Removed.Add(it);
            }
            else if (kind == 2)
            {
                int table = _t.Item(idx).Code;
                var it = new ItemRecord();
                if (table == 8000)
                {
                    it.Index = Mech.I32(0x228) == 0 ? 1 : 2;
                    Mech.SetI32(0x22c, Mech.I32(0x22c) - 100);
                    Mech.HeatSinks();
                    Mech.Totals();
                }
                else if (table == 7000)
                {
                    it.Index = 10;
                    Mech.SetI32(0x270, 0);
                    Mech.SetI32(0x974, 0);
                }
                else if (table == 0x2134)
                {
                    it.Index = 6;
                    if (Mech.I32(0x288) != 0) Mech.SetI32(0x288, Mech.I32(0x288) - 1);
                    Mech.Movement();
                    Mech.Totals();
                }
                else if (table == 0x254e)
                {
                    it.Index = 5;
                    RemoveCase();
                }
                else return;
                it.Kind = 2;
                it.Price = 0;
                it.FieldA = it.FieldE = it.Field12 = -1;
                it.Field16 = 0;
                Stock.PlaceUnit(it);
                EquipmentPrice(it);
                Removed.Add(it);
            }
        }

        /// <summary>FUN_10013e40 0x10013e40: every slot of the code emptied; how many.</summary>
        int Clear(int code)
        {
            if (code < 1) return 0;
            int n = 0;
            for (int s = 0; s < 8; s++)
                for (int j = 0; j < 12; j++)
                    if (Mech.Crit(s, j) == code) { Mech.SetCrit(s, j, 0); n++; }
            return n;
        }

        /// <summary>FUN_10013f6f 0x10013f6f: the weapon entry of the code taken out, the rest moved up (the scan runs
        /// over 14 entries, the move into the 15th, the reserve +0x358).</summary>
        void RemoveWeaponEntry(int code)
        {
            int k = 0;
            while (k <= 0xd && Mech.I32(0x2f0 + k * 8) != code) k++;
            if (k <= 0xd)
            {
                Mech.SetI32(0x2f0 + k * 8, -1);
                Mech.SetI32(0x2f4 + k * 8, -1);
            }
            for (; k < 0xd; k++)
            {
                Mech.SetI32(0x2f0 + k * 8, Mech.I32(0x2f8 + k * 8));
                Mech.SetI32(0x2f4 + k * 8, Mech.I32(0x2fc + k * 8));
            }
            Mech.SetI32(0x358, -1);
            Mech.SetI32(0x35c, -1);
        }

        /// <summary>FUN_10013ed6 0x10013ed6: the bin of the code taken out, the rest moved up (the 25th, +0x2ec, freed).</summary>
        void RemoveBin(int code)
        {
            int k = 0;
            while (k <= 0x18 && Mech.I32(0x28c + k * 4) != code) k++;
            if (k <= 0x18) Mech.SetI32(0x28c + k * 4, -1);
            for (; k < 0x18; k++) Mech.SetI32(0x28c + k * 4, Mech.I32(0x290 + k * 4));
            Mech.SetI32(0x2ec, -1);
        }

        /// <summary>FUN_100140aa 0x100140aa: CASE mass off (Clan none, Inner Sphere 0.5 t less), the section's CASE flag
        /// 0x100 cleared, Mech_Recalc. (Its FUN_10046320 renumbers the MechLab's own copy DAT_100d1000, not this mech.)</summary>
        void RemoveCase()
        {
            if (Mech.Technology == 0) Mech.SetI32(0x278, 0);
            else if (Mech.Technology == 1)
            {
                Mech.SetI32(0x278, Mech.I32(0x278) - 0x32);
                if (Mech.I32(0x278) < 0) Mech.SetI32(0x278, 0);
                int f = 0x8f0 + Section * 0x12;
                Mech.SetI16(f, Mech.I16(f) & 0xfeff);
            }
            Mech.Totals();
        }

        /// <summary>
        /// FUN_10014153 0x10014153: while assigning, one slot of the code in the section (counted back when it is the
        /// code being assigned); otherwise every critical of the code (by tens) on the mech, the flag (+0x240 / +0x24c)
        /// cleared and the item (equipment 8 / 7) back into the stock and DAT_100c772c.
        /// </summary>
        void RemoveArmorKind(int code)
        {
            if (Assigning)
            {
                int cleared = 0;
                for (int j = 0; j < 12; j++)
                    if (Mech.Crit(Section, j) == code) { Mech.SetCrit(Section, j, 0); cleared = 1; break; }
                if (AssignCode == code) AssignLeft += cleared;
                return;
            }
            for (int s = 0; s < 8; s++)
                for (int j = 0; j < 12; j++)
                    if (Mech.Crit(s, j) / 10 == code / 10) Mech.SetCrit(s, j, 0);
            var it = new ItemRecord();
            if (code == 9000)
            {
                it.Index = 8;
                Mech.SetI32(0x240, 0);
            }
            else
            {
                it.Index = 7;
                Mech.SetI32(0x24c, 0);
            }
            it.Price = _t.Item(it.Index).Price;
            Mech.Totals();
            it.Kind = 2;
            it.FieldA = it.FieldE = it.Field12 = -1;
            Stock.PlaceUnit(it);
            EquipmentPrice(it);
            Removed.Add(it);
        }

        /// <summary>FUN_1002694a 0x1002694a: 10000, 50000 for 3 / 4, and for 1, 2, 5, 6 the price column of the
        /// 0x100a8198 row of the same number (the original indexes that table with the equipment index).</summary>
        void EquipmentPrice(ItemRecord it)
        {
            it.Price = 10000;
            switch (it.Index)
            {
                case 1: case 2: case 5: case 6: it.Price = _t.Item(it.Index).Price; break;
                case 3: case 4: it.Price = 50000; break;
            }
        }

        // ---- armour panel (DAT_100a0338) ----

        /// <summary>FUN_1001229c 0x1001229c / FUN_100122e7 0x100122e7: half a ton of armour more / less (FUN_10046ec7 trims
        /// the points), then FUN_10012111.</summary>
        public void ArmorTons(int step)
        {
            if (step > 0) Mech.SetI32(0x250, Mech.I32(0x250) + 0x32);
            else if (Mech.I32(0x250) != 0) Mech.SetI32(0x250, Mech.I32(0x250) - 0x32);
            Mech.ArmorAllowed();
            Mech.Totals();
            ArmorChanged();
        }

        /// <summary>
        /// FUN_100123f3 0x100123f3 (front +) / FUN_1001257e 0x1001257e (rear +): one point more while points are left and
        /// front + rear stays within the section's maximum (+4); otherwise a point moved over from the other side.
        /// FUN_10012516 0x10012516 / FUN_10012669 0x10012669: one point less.
        /// </summary>
        public void ArmorPoint(bool rear, bool more)
        {
            int o = 0x8e0 + Section * 0x12;
            int front = o + 8, back = o + 12, max = o + 4;
            if (!rear && more)
            {
                if (Mech.I32(0x25c) < Mech.I32(0x258))
                {
                    if (Mech.I32(back) < 0)
                    {
                        if (Mech.I32(front) < Mech.I32(max)) { Inc(front, 1); Inc(0x25c, 1); }
                    }
                    else if (Mech.I32(front) + Mech.I32(back) < Mech.I32(max)) { Inc(front, 1); Inc(0x25c, 1); }
                    else if (Mech.I32(back) != 0) { Inc(front, 1); Inc(back, -1); }
                }
                else if (Mech.I32(back) > 0) { Inc(front, 1); Inc(back, -1); }
            }
            else if (rear && more)
            {
                if (Mech.I32(back) < 0) return;
                if (Mech.I32(0x25c) < Mech.I32(0x258))
                {
                    if (Mech.I32(front) + Mech.I32(back) < Mech.I32(max)) { Inc(back, 1); Inc(0x25c, 1); }
                    else if (Mech.I32(front) != 0) { Inc(front, -1); Inc(back, 1); }
                }
                else if (Mech.I32(front) > 0) { Inc(front, -1); Inc(back, 1); }
            }
            else if (!rear)
            {
                if (Mech.I32(front) > 0) { Inc(front, -1); Inc(0x25c, -1); }
            }
            else if (Mech.I32(back) > 0) { Inc(back, -1); Inc(0x25c, -1); }
            ArmorChanged();
        }

        void Inc(int o, int d) => Mech.SetI32(o, Mech.I32(o) + d);

        /// <summary>FUN_10012111 0x10012111: the full-armour nibbles (FUN_1004b041) and +0x9cc (the screen redraws the price).</summary>
        void ArmorChanged()
        {
            Mech.MarkFullArmor();
            Mech.Data[0x9cc] = 1;
        }

        // ---- engine panel (DAT_1009ff48) ----

        /// <summary>FUN_10012b95 0x10012b95 (faster) / FUN_10012c6e 0x10012c6e (slower, not below walking 0): the rating
        /// for one walking MP more / less, (tons × MP × 5 + 4) / 5, inside 10..400; the XL bit kept.</summary>
        public void Speed(int step)
        {
            if (step < 0 && Mech.I32(0x280) == 0) return;
            int rating = (Mech.I32(0x200) / 100 * (Mech.I32(0x280) + step) * 5 + 4) / 5;
            if (rating <= 9 || rating >= 0x191) return;
            int index = (rating - 10) / 5;
            if (Mech.I32(0x20c) > 9999) index += 10000;
            Mech.SetI32(0x20c, index);
            Mech.Engine(_t);
            Mech.HeatSinks();
            Mech.Movement();
            Mech.Totals();
        }

        /// <summary>
        /// FUN_10012d5b 0x10012d5b: to XL the side torsos' first two slots (three on Inner Sphere) are emptied of what they
        /// hold (FUN_100139a2) and take the engine 7850; back to standard those 7850s are emptied.
        /// </summary>
        public void ToggleXl()
        {
            int[] slots = Mech.Technology == 1 ? new[] { 0, 1, 2 } : new[] { 0, 1 };
            if (Mech.I32(0x20c) < 10000)
            {
                Mech.SetI32(0x20c, Mech.I32(0x20c) + 10000);
                foreach (int section in new[] { 3, 1 })
                    foreach (int j in slots)
                        if (Mech.Crit(section, j) > 0) Remove(Mech.Crit(section, j));
                foreach (int section in new[] { 3, 1 })
                    foreach (int j in slots) Mech.SetCrit(section, j, 0x1eaa);
            }
            else
            {
                Mech.SetI32(0x20c, Mech.I32(0x20c) - 10000);
                for (int j = 0; j < 12; j++)
                {
                    if (Mech.Crit(1, j) == 0x1eaa) Mech.SetCrit(1, j, 0);
                    if (Mech.Crit(3, j) == 0x1eaa) Mech.SetCrit(3, j, 0);
                }
            }
            Mech.Engine(_t);
            Mech.Totals();
        }

        /// <summary>The price line of the engine panel (FUN_10012a71 0x10012a71): "0" while unchanged.</summary>
        public int EnginePrice => Mech.I32(0x20c) == Backup.I32(0x20c) ? 0
            : Mech.I32(0x200) / 100 * Mech.I32(0x210) * (Mech.I32(0x20c) < 10000 ? 5000 : 20000) / 0x4b;

        // ---- rows of the inventory list (FUN_10017920) ----

        public sealed class Row
        {
            public int Index, Kind, Count, InStock;
            public int Price;               // FUN_10026a0b: +4 of the first record
            /// <summary>The third column is FUN_100184e1 ("1", and the crits × 50000 price when selected) rather than the
            /// undamaged count FUN_10018611.</summary>
            public bool Single;
        }

        /// <summary>
        /// FUN_10017920 0x10017920: the list sorted (FUN_100098b9), one row per index in order; the count of the index
        /// (99999999 outside mode 1), the kind of the list (0 weapons, 1 ammunition, 2 equipment), the undamaged records of
        /// the kind (on the ammunition tab the count). The equipment tab's third column follows the original test: the row
        /// numbered 0 or the one whose 3 × number + 2 equals 4 × rows − 3.
        /// </summary>
        public List<Row> Rows()
        {
            var l = Current;
            l.SortIfUnsorted();
            int kind = l == Stock.Ammo ? 1 : l == Stock.Equipment ? 2 : 0;
            var rows = new List<Row>();
            var seen = new HashSet<int>();
            foreach (var it in l.Items)
            {
                if (!seen.Add(it.Index)) continue;
                int count = 0;
                foreach (var x in l.Items) if (x.Index == it.Index) count++;
                rows.Add(new Row { Index = it.Index, Kind = kind, Count = _c.Mode != 1 ? 99999999 : count, Price = it.Price });
            }
            int want = Tab == 5 ? 2 : 0;
            for (int g = 0; g < rows.Count; g++)
            {
                var r = rows[g];
                int stock = 0;
                foreach (var x in l.Items) if (x.Index == r.Index && x.Kind == want && x.Field16 == 0) stock++;
                r.InStock = l == Stock.Ammo ? r.Count : stock;
                int n = g * 3 + 2;
                r.Single = (n == 2 || n == rows.Count * 4 - 3) && Tab == 5 || _c.Mode == 2 || _c.Mode == 0;
            }
            return rows;
        }

        /// <summary>FUN_10018253 0x10018253: the criticals column (ammunition 1; equipment from 0x100abad0, heat sinks
        /// 2 / 3 by technology, CASE none on Clan, ferro / endo 7 / 14, MASC +0x988; -1 shows "-").</summary>
        public int RowCrits(Row r)
        {
            if (Current == Stock.Ammo) return 1;
            if (Current == Stock.Equipment)
            {
                if (r.Index < 0 || r.Index >= 0xc) return -1;
                switch (_t.Equipment(r.Index).Kind)
                {
                    case 2: return Mech.Technology == 0 ? 2 : 3;
                    case 5: return Mech.Technology == 0 ? -1 : 1;
                    case 7: case 8: return Mech.Technology == 0 ? 7 : 0xe;
                    case 10: return Mech.I32(0x988);
                    default: return _t.Equipment(r.Index).Space;
                }
            }
            return _t.Weapon(r.Index).Slots;
        }

        /// <summary>The text of a critical slot (FUN_10013578 0x10013578), null where the section has no slot; a missing
        /// slot below 12 of a twelve-slot piece is made empty (0) on the way, as the original does.</summary>
        public string SlotText(int slot)
        {
            int s = Section;
            if (s < 0) return null;
            if (slot >= 6 && (s == 0 || s == 6 || s == 7)) return null;
            int v = Mech.Crit(s, slot);
            if (v < 0)
            {
                if ((s == 0 || s == 6 || s == 7) && slot > 5) return null;
                if (slot < 0xc) { v = 0; Mech.SetCrit(s, slot, 0); }
            }
            return MechRepair.CriticalName(v, _t);
        }
    }
}
