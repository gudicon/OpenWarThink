using System.Collections.Generic;

namespace MW2.Shell
{
    /// <summary>
    /// Arms Merchant 0xc01e (armsdlr.cpp: open FUN_10003b02 0x10003b02, screen function FUN_10004455 0x10004455; the
    /// trade itself is <see cref="ArmsDealer"/>). Background 19 (DAT_10096550), colour 1 → 0x16 / 0x11 / 0x38. Tabs
    /// (spots 2..6, "bamiss" "baenrg" "baproj" "baequp" "baammo" at y 12) pick the lists and the picture at (40, 118)
    /// ("amissle", "alaser", "aproj", "aequipt", "aammo", flags 0x68); the unit's list is at x 459 and the dealer's at
    /// x 252 from y 90 (FUN_100056e2: title, column header, "Qty"/"Tons" and "Price/unit"/"Price/ton", a row per index
    /// with the count "n" or "n /" and the damaged count in 0x38, and the price; the unit's side adds "Current Load" and
    /// "Remaining Space" at y 339 / 357). A click selects a row (FUN_10006de0 / FUN_10006e59: price and balance, the
    /// weapon's figures at (43, 288), the repair cost at (43, 385)); a double click or spot 7 sells, spot 8 buys,
    /// spot 9 ("repair" at (80, 430)) repairs a damaged copy; spot 0 leaves for the drop ship 0xc032. Selling everything
    /// of worth so that not even the cheapest mech can be bought (FUN_1004ac2c) leaves for 0xc037.
    /// </summary>
    public static class ArmsDealerScreen
    {
        static readonly ShellRegions.Entry[] Spots =
        {
            new ShellRegions.Entry(502, 432, 603, 473, 0, 0, ""),
            new ShellRegions.Entry(397, 432, 501, 473, 0, 0, ""),
            new ShellRegions.Entry(9, 13, 102, 38, 0, 0, ""),
            new ShellRegions.Entry(103, 13, 192, 38, 0, 0, ""),
            new ShellRegions.Entry(193, 13, 281, 38, 0, 0, ""),
            new ShellRegions.Entry(282, 13, 370, 38, 0, 0, ""),
            new ShellRegions.Entry(371, 13, 462, 38, 0, 0, ""),
            new ShellRegions.Entry(487, 380, 586, 413, 0, 0, ""),
            new ShellRegions.Entry(285, 380, 382, 413, 0, 0, ""),
            new ShellRegions.Entry(80, 430, 165, 455, 0, 0, "")
        };
        const int Below = unchecked((int)0x80000011), TwoBelow = unchecked((int)0x80000022), Same = unchecked((int)0x80000000);

        static Mw2Shell _s;
        static ArmsDealer _d;
        static ShellRegions _regions;
        static ShellSound _click, _ambient, _exit;              // DAT_100c1f3c, f24, f28
        static ShellAnim _picture;                              // DAT_100c1f54
        static readonly ShellAnim[] _pictures = new ShellAnim[5];
        static ShellAnim[] _tabs;
        static ShellAnim _exitButton, _buy, _sell, _repair;
        static ShellItemList _stock, _offer, _stats, _info, _funds;
        static ShellItemList.Entry _price, _balance;
        static string _stockTitle, _offerTitle;                 // DAT_100c1dd0, DAT_100c1cc8
        static int _statsCode;                                  // DAT_100c1f1c
        static ItemRecord _infoItem;                            // the info entries' +0x30
        static bool _started, _firstLists;                      // DAT_100c1dcc, DAT_100c1dc8
        static byte[] _normal, _selected, _damaged;

        sealed class RowEntries
        {
            public ArmsDealer.Row Row;
            public ShellItemList.Entry Name, Count, Damaged;
        }
        static readonly Dictionary<ShellItemList.Entry, RowEntries> _rows = new Dictionary<ShellItemList.Entry, RowEntries>();

        public static void Open(Mw2Shell s)
        {
            _s = s;
            _normal = ShellSurface.MakeRemap(0x16);
            _selected = ShellSurface.MakeRemap(0x11);
            _damaged = ShellSurface.MakeRemap(0x38);
            _started = false;
            _firstLists = true;
            _statsCode = -1;
            _infoItem = null;
            _stock = _offer = null;
            _rows.Clear();
            for (int i = 0; i < 5; i++) _pictures[i] = null;
            _regions = new ShellRegions(s.Surface, s.Font7, false, Spots, () => true);
            s.Anims.Clear();
            _click = Sound(0x35, 0x32);
            _ambient = Sound(0x3b, 0x28);
            _ambient.LoopForever();
            _ambient.Play();
            _exit = Sound(0x39, 0x28);
            _pictures[0] = new ShellAnim(s.Anims, "amissle", 0x28, 0x76, 0x68, 0);
            _picture = _pictures[0];
            _tabs = new[]
            {
                new ShellAnim(s.Anims, "bamiss", 0xf, 0xc, 0x61, 0), new ShellAnim(s.Anims, "baenrg", 0x69, 0xc, 0x61, 0),
                new ShellAnim(s.Anims, "baproj", 0xc2, 0xc, 0x61, 0), new ShellAnim(s.Anims, "baequp", 0x11c, 0xc, 0x61, 0),
                new ShellAnim(s.Anims, "baammo", 0x175, 0xc, 0x61, 0)
            };
            _tabs[0].SetFrame(1);
            _exitButton = new ShellAnim(s.Anims, "baexit", 0x1f1, 0x1b8, 0x61, 0);
            _buy = new ShellAnim(s.Anims, "babuy", 0x123, 0x17e, 0x61, 0);
            _sell = new ShellAnim(s.Anims, "basell", 0x1f0, 0x17e, 0x61, 0);
            _repair = new ShellAnim(s.Anims, "repair", 0x50, 0x1ae, 0x61, 0);
            _repair.Show();
            _info = Info();
            _info.Init();
            ShowInfo(null, 0);
            _stats = Stats();
            _funds = ShellFunds.Build(s, 23, 36, out _price, out _balance);     // DAT_10096558
            _funds.Init();
            ShellFunds.ShowPrice(_funds, false);                                // FUN_10004341
            s.Anims.Step();
            _d = new ArmsDealer(s.Career);
            _d.Split();                                                         // FUN_10049c6c
            s.Register(Tick);
        }

        static ShellSound Sound(int record, int volume)
        {
            var snd = new ShellSound(_s.gameObject, _s.Db, record);
            snd.SetVolume(volume);
            return snd;
        }

        static ShellText Draw(string text, int x, int y, byte[] remap)
        {
            if (string.IsNullOrEmpty(text)) return null;
            var t = new ShellText(_s.Surface, _s.Font11, text, x, y, remap);
            t.Show();
            return t;
        }

        static ShellText Right(string text, int x, int y, byte[] remap) => Draw(text, x - _s.Font11.Width(text), y, remap);
        static ShellTables Tables => _s.Data.Tables;

        // ---- the info lines DAT_100c1edc and the figures DAT_100966e0 ----

        /// <summary>The four lines built in FUN_10003b02: three empty formats (DAT_100c1f80..88) at (45, 300 / 312 / 324)
        /// and the repair cost (FUN_100037ae) at (43, 385).</summary>
        static ShellItemList Info()
        {
            var list = new ShellItemList();
            list.Entries.Add(new ShellItemList.Entry { X = 0x2d, Y = 300 });
            list.Entries.Add(new ShellItemList.Entry { X = 0x2d, Y = 0x138 });
            list.Entries.Add(new ShellItemList.Entry { X = 0x2d, Y = 0x144 });
            list.Entries.Add(new ShellItemList.Entry { X = 0x2b, Y = 0x181, Draw = DrawRepairCost });
            return list;
        }

        /// <summary>FUN_100037ae 0x100037ae: "Repair Cost : %d" (0.25 of +4) when the unit has a damaged copy of the item;
        /// nothing on the ammunition tab.</summary>
        static ShellText DrawRepairCost(ShellItemList.Entry e)
        {
            var it = _infoItem;
            if (it == null || _d.AmmoTab) return null;
            bool damaged = _d.Stock.Items.Exists(x => x.Field16 != 0 && x.Index == it.Index && x.Kind == it.Kind);
            return damaged ? Draw($"Repair Cost : {ArmsDealer.Value(it, ArmsDealer.ShownRepair)}", e.X, e.Y, _damaged) : null;
        }

        /// <summary>FUN_10003480 0x10003480: the item the info lines are about: the first record of its index and kind in
        /// the unit's (0) or the dealer's (1) list.</summary>
        static void ShowInfo(ItemRecord of, int side)
        {
            _infoItem = null;
            if (of != null)
            {
                var l = side == 0 ? _d.Stock : _d.Offer;
                for (int k = l.Count - 1; k >= 0; k--)
                    if (l[k].Kind == of.Kind && l[k].Index == of.Index) _infoItem = l[k];
                _info.Free();
                _info.Init();
            }
            _info.Refresh();
        }

        static ShellItemList Stats()
        {
            var list = new ShellItemList();
            string[] labels = { "Type", "Heat", "Damage", "Range", "Mass", "Crit", "Ammo" };
            int[] fields = { 0, 1, 2, 6, 7, 8, 9 };
            for (int i = 0; i < labels.Length; i++)
            {
                string label = labels[i];
                list.Entries.Add(new ShellItemList.Entry { X = 0x2b, Y = i == 0 ? 0x120 : i == 1 ? TwoBelow : Below, Draw = e => Draw(label, e.X, e.Y, _normal) });
                int field = fields[i];
                list.Entries.Add(new ShellItemList.Entry { X = i == 0 ? 0x53 : 0x5d, Y = Same, Draw = e => _statsCode < 0 ? null : Draw(ArmsDealer.StatText(field, _statsCode, _d.EquipmentTab, _d.AmmoTab, Tables), e.X, e.Y, _normal) });
            }
            return list;
        }

        /// <summary>The figures of the selected item (the end of FUN_100056e2, FUN_10006de0, FUN_1000360c).</summary>
        static void ShowStats(ItemRecord first)
        {
            if (_statsCode == -1) _stats.Init();
            _statsCode = first.Index * 100;
            _stats.Refresh();
        }

        static RowEntries Selected(ShellItemList list)
        {
            if (list == null) return null;
            foreach (var e in list.Entries) if (e.State > 0) return _rows.TryGetValue(e, out var r) ? r : null;
            return null;
        }

        // ---- the lists (FUN_100056e2) ----

        static ShellItemList Build(ShellCollection l, int side)
        {
            var old = side == 0 ? _stock : _offer;
            int x = side == 0 ? 0x1cb : 0xfc;
            const int top = 0x5a;
            int h = _s.Font11.Height;
            _statsCode = -1;
            _stats.Free();
            var rows = _d.Rows(l, side == 1);
            var list = new ShellItemList();
            string title = side == 0 ? _stockTitle : _offerTitle;
            list.Entries.Add(new ShellItemList.Entry
            {
                X = side == 0 ? 0x213 : 0x147, Y = top,
                Draw = e => Draw(title, e.X - _s.Font11.Width(title) / 2, e.Y, _selected)      // FUN_10006d7e / FUN_10006d1c
            });
            int header = top + h * 2;
            if (l.Count == 0) list.Entries.Add(new ShellItemList.Entry { X = x, Y = header, Draw = e => Draw("None", e.X, e.Y, _normal) });
            else
            {
                string kind = _d.AmmoTab ? "Ammo" : _d.EquipmentTab ? "Equipment" : "Weapon";
                list.Entries.Add(new ShellItemList.Entry { X = x, Y = header, Draw = e => Draw(kind, e.X, e.Y, _selected) });
                string qty = _d.AmmoTab ? "Tons" : "Qty", per = _d.AmmoTab ? "Price/ton" : "Price/unit";
                list.Entries.Add(new ShellItemList.Entry { X = x + 100, Y = header, Draw = e => Right(qty, e.X, e.Y, _selected) });
                list.Entries.Add(new ShellItemList.Entry { X = x + 0xa0, Y = header, Draw = e => Right(per, e.X, e.Y, _selected) });
            }
            for (int k = 0; k < rows.Count; k++)
            {
                var row = rows[k];
                int y = header + (3 + k) * h;
                var r = new RowEntries { Row = row };
                r.Name = new ShellItemList.Entry { X = x, Y = y, W = 0xd2, Remap = _normal, Data = row.Index, Draw = DrawName, Click = side == 0 ? ClickStock : ClickOffer };
                r.Count = new ShellItemList.Entry { X = x + 100, Y = y, Remap = _normal, Data = -1, Data2 = row.Pristine };
                r.Damaged = new ShellItemList.Entry { X = x + 0x6e, Y = y, Remap = _normal, Data = -1, Data2 = row.Damaged, Draw = DrawDamaged };
                var price = new ShellItemList.Entry { X = x + 0xa0, Y = y, Remap = _normal, Data = -1, Data2 = row.Price, Draw = DrawPrice };
                // FUN_10006a57: "n" right-aligned on the damaged column without damaged copies, else "n /" on its own.
                r.Count.Draw = e => _d.AmmoTab || r.Damaged.Data2 != 0
                    ? Right(_d.AmmoTab ? $"{e.Data2}" : $"{e.Data2} /", e.X, e.Y, e.State == 1 ? _selected : e.Remap)
                    : Right($"{e.Data2}", r.Damaged.X, e.Y, e.State == 1 ? _selected : e.Remap);
                foreach (var e in new[] { r.Name, r.Count, r.Damaged }) _rows[e] = r;
                list.Entries.Add(r.Name); list.Entries.Add(r.Count); list.Entries.Add(r.Damaged); list.Entries.Add(price);
            }
            if (side == 0)
            {
                list.Entries.Add(new ShellItemList.Entry { X = x, Y = 0x153, H = 0, Draw = e => Draw($"Current Load                 : {_d.Load(Tables)}", e.X, e.Y, _normal) });
                list.Entries.Add(new ShellItemList.Entry { X = x, Y = 0x165, H = 0, Draw = e => Draw($"Remaining Space           : {ArmsDealer.Capacity - _d.Load(Tables)}", e.X, e.Y, _normal) });
            }
            // The selection of the list being replaced decides what the info lines and the figures show.
            var sel = Selected(old);
            if (sel == null) ShowInfo(null, side);
            else
            {
                ShowInfo(sel.Row.First, side);
                ShowStats(sel.Row.First);
            }
            return list;
        }

        /// <summary>FUN_1000689a 0x1000689a: equipment "%s:" (or "BAD EQUIP!! %d"), weapons and ammunition "%s".</summary>
        static ShellText DrawName(ShellItemList.Entry e)
        {
            string text;
            if (_d.EquipmentTab) text = e.Data < 0 || e.Data > 0xb ? $"BAD EQUIP!! {e.Data}" : $"{Tables.EquipmentName(e.Data)}:";
            else text = Tables.WeaponName(e.Data);
            return Draw(text, e.X, e.Y, e.State == 1 ? _selected : e.Remap);
        }

        /// <summary>FUN_10006b64 0x10006b64: the damaged count right-aligned, in 0x38; nothing for none.</summary>
        static ShellText DrawDamaged(ShellItemList.Entry e) => e.Data2 == 0 ? null : Right($"{e.Data2}", e.X, e.Y, _damaged);

        /// <summary>FUN_10006c3b 0x10006c3b.</summary>
        static ShellText DrawPrice(ShellItemList.Entry e) =>
            Right(e.Data2 < 0 ? "Error!" : Debriefing.Money(e.Data2), e.X, e.Y, e.State == 1 ? _selected : e.Remap);

        /// <summary>FUN_10006ed2 0x10006ed2 / FUN_10007017 0x10007017: the list again, the row of the index selected before
        /// selected again (when it had records), then the price lines shown.</summary>
        static void Rebuild(int side, ShellCollection l, string title)
        {
            if (side == 0 && title != null) _stockTitle = title;
            if (side == 1 && title != null) _offerTitle = title;
            var old = side == 0 ? _stock : _offer;
            var sel = Selected(old);
            int keep = sel != null && sel.Row.Total > 0 ? sel.Row.Index : -1;
            old?.Free();
            var list = Build(l, side);
            if (side == 0) { _stock = list; _d.Stock = l; } else { _offer = list; _d.Offer = l; }
            list.Init();
            if (keep >= 0)
                foreach (var e in list.Entries)
                    if (_rows.TryGetValue(e, out var r) && r.Name == e && r.Row.Index == keep)
                    {
                        r.Name.State = r.Count.State = r.Damaged.State = 2;
                        list.Refresh();
                        break;
                    }
            ShellFunds.ShowPrice(_funds, true);     // FUN_10003960
        }

        /// <summary>FUN_10006de0 0x10006de0: a row of the unit's list: the price it sells for, the balance after.</summary>
        static void ClickStock(ShellItemList.Entry e)
        {
            var r = _rows[e];
            r.Name.State = r.Count.State = r.Damaged.State = 2;
            SetPrice(r.Row.Value, _s.Career.Funds + r.Row.Value);
            ShowInfo(r.Row.First, 0);
            ShowStats(r.Row.First);
        }

        /// <summary>FUN_10006e59 0x10006e59: a row of the dealer's list.</summary>
        static void ClickOffer(ShellItemList.Entry e)
        {
            var r = _rows[e];
            r.Name.State = r.Count.State = r.Damaged.State = 2;
            SetPrice(r.Row.Value, _s.Career.Funds - r.Row.Value);
            ShowInfo(r.Row.First, 1);
            ShowStats(r.Row.First);
        }

        /// <summary>FUN_1000399a 0x1000399a / FUN_1000392a 0x1000392a.</summary>
        static void SetPrice(int price, int balance)
        {
            _price.Data = price;
            _balance.Data = balance;
            ShellFunds.ShowPrice(_funds, true);
        }

        /// <summary>FUN_1000360c 0x1000360c.</summary>
        static void ShowSelection()
        {
            var sel = Selected(_stock);
            if (sel != null)
            {
                ShowInfo(sel.Row.First, 0);
                ShowStats(sel.Row.First);
                return;
            }
            sel = Selected(_offer);
            if (sel == null) ShowInfo(null, 1);
            else
            {
                ShowInfo(sel.Row.First, 1);
                ShowStats(sel.Row.First);
            }
        }

        static void HideIfNothingSelected()
        {
            if (Selected(_stock) == null && Selected(_offer) == null) ShellFunds.ShowPrice(_funds, false);
        }

        static uint SellSelected(uint ev)
        {
            var sel = Selected(_stock);
            if (sel == null)
            {
                _s.Dialog("Click on item to sell first#Ok", null);
                return ev;
            }
            _click.Play();
            if (!_d.AmmoTab)
            {
                // FUN_100056e2(DAT_100cd134, 0) on the emptied unit list: only the figures are laid out again.
                _statsCode = -1;
                _stats.Free();
                ShowInfo(sel.Row.First, 0);
                ShowStats(sel.Row.First);
            }
            _d.SellOne(sel.Row.Index);
            SetPrice(sel.Row.Value, _s.Career.Funds + sel.Row.Value);
            Rebuild(0, _d.Stock, null);
            Rebuild(1, _d.Offer, null);
            ShowSelection();
            if (!ShellEconomy.CanAffordAnyMech(_s.Career, Tables)) ev = 0xc037;
            HideIfNothingSelected();
            _sell.SetFrame(0);
            return ev;
        }

        static void BuySelected()
        {
            var sel = Selected(_offer);
            if (sel == null)
            {
                _s.Dialog("Click on item to buy first#Ok", null);
                return;
            }
            _click.Play();
            var result = _d.BuyOne(sel.Row.Index, Tables);
            if (result == ArmsDealer.Result.NoSpace)
            {
                _s.Dialog("Insufficent space on dropship!|#Ok", null);
                return;
            }
            if (result == ArmsDealer.Result.NoFunds) _s.Dialog("Insufficient Funds!#Ok", null);
            SetPrice(sel.Row.Value, _s.Career.Funds - sel.Row.Value);
            Rebuild(0, _d.Stock, null);
            Rebuild(1, _d.Offer, null);
            HideIfNothingSelected();
            _buy.SetFrame(0);
        }

        static void RepairSelected()
        {
            if (_d.AmmoTab)
            {
                _s.Dialog("Cannot repair ammo!!|#Ok", null);
                return;
            }
            var sel = Selected(_stock) ?? Selected(_offer);
            if (sel == null || sel.Row.First == null) return;
            var result = _d.RepairOne(sel.Row.First);
            if (result == ArmsDealer.Result.NothingToRepair) _s.Dialog("No repairs are required|#Ok", null);
            else if (result == ArmsDealer.Result.NoFunds) _s.Dialog("Insufficent funds|#Ok", null);
            else
            {
                ShowInfo(null, 0);
                Rebuild(0, _d.Stock, null);
                Rebuild(1, _d.Offer, null);
                ShowSelection();
            }
        }

        /// <summary>Tabs 2..6: the tab's lists, picture and button (FUN_1000715c puts the other buttons back).</summary>
        static void Tab(int tab)
        {
            _click.Play();
            foreach (var t in _tabs) t.SetFrame(0);
            _buy.SetFrame(0);
            _sell.SetFrame(0);
            _tabs[tab].SetFrame(1);
            _d.EquipmentTab = tab == 3;
            _d.AmmoTab = tab == 4;
            string[] pictures = { "amissle", "alaser", "aproj", "aequipt", "aammo" };
            if (_pictures[tab] == null) _pictures[tab] = new ShellAnim(_s.Anims, pictures[tab], 0x28, 0x76, 0x68, 0);
            if (_picture != _pictures[tab])
            {
                if (_picture != null) { _picture.Hide(); _picture.Stop(); }
                _picture = _pictures[tab];
                _picture.Show();
                _picture.Run();
            }
            switch (tab)
            {
                case 0: Rebuild(0, _d.Missiles, "Missiles in Inventory"); Rebuild(1, _d.DealerMissiles, "Missiles for Sale"); break;
                case 1: Rebuild(0, _d.Energy, "Energy Weapons in Inventory"); Rebuild(1, _d.DealerEnergy, "Energy Weapons for Sale"); break;
                case 2: Rebuild(0, _d.Projectiles, "Projectile Weapons in Inventory"); Rebuild(1, _d.DealerProjectiles, "Projectiles Weapons for Sale"); break;
                case 3: Rebuild(0, _d.Equipment, "Equipment in Inventory"); Rebuild(1, _d.DealerEquipment, "Equipment for Sale"); break;
                case 4: Rebuild(0, _d.Ammo, "Ammo in Inventory"); Rebuild(1, _d.DealerAmmo, "Ammo for Sale"); break;
            }
        }

        static void Tick(uint ev)
        {
            var s = _s;
            if (!_started)
            {
                _started = true;
                s.Surface.LoadBackground(ShellPcx.Decode(s.Db.Unpack(0x13)));
                _ambient.LoopForever();
                _pictures[0].Show();
                foreach (var t in _tabs) t.Show();
                _exitButton.Show(); _buy.Show(); _sell.Show();
            }
            if (ev == 0xc004)
            {
                if (_firstLists)
                {
                    _firstLists = false;
                    _stockTitle = "Missiles in Inventory";
                    _stock = Build(_d.Missiles, 0);
                    _d.Stock = _d.Missiles;
                    _stock.Init();
                    _offerTitle = "Missiles for Sale";
                    _offer = Build(_d.DealerMissiles, 1);
                    _d.Offer = _d.DealerMissiles;
                    _offer.Init();
                }
                s.Anims.Step();
                bool down = s.MouseClicked;
                bool traded = false;
                if (down)
                {
                    var e = _stock.Hit(s.MouseX, s.MouseY);
                    if (e != null)
                    {
                        _offer.ClearStates();
                        e.Click(e);
                        if (s.MouseDouble) { ev = SellSelected(ev); traded = true; }
                    }
                    else if ((e = _offer.Hit(s.MouseX, s.MouseY)) != null)
                    {
                        _stock.ClearStates();
                        e.Click(e);
                        if (s.MouseDouble) { BuySelected(); traded = true; }
                    }
                    if (!traded)
                    {
                        _stock.Refresh();
                        _offer.Refresh();
                    }
                }
                if (!traded && down)
                {
                    int spot = _regions.Hit(s.MouseX, s.MouseY);
                    switch (spot)
                    {
                        case 0:
                            _exitButton.SetFrame(1);
                            s.Anims.Step();
                            s.BlockUntilEnd(_exit);
                            ev = 0xc032;
                            break;
                        case 2: case 3: case 4: case 5: case 6:
                            Tab(spot - 2);
                            break;
                        case 7:
                            _sell.SetFrame(1);
                            s.Anims.Step();
                            ev = SellSelected(ev);
                            break;
                        case 8:
                            _buy.SetFrame(1);
                            s.Anims.Step();
                            BuySelected();
                            break;
                        case 9:
                            // The button is held down for 150 ms (timeGetTime) before it comes back up.
                            _repair.SetFrame(1);
                            s.Anims.Step();
                            _repair.SetFrame(0);
                            s.Anims.Step();
                            RepairSelected();
                            break;
                    }
                }
            }
            if (ev == 0xc004) return;
            _regions.Dispose(); _regions = null;
            s.Anims.Clear();
            _funds.Free();
            _stock?.Free();
            _offer?.Free();
            _stats.Free();
            _info.Free();
            s.Surface.DrawAfter.Clear(); s.Surface.DrawBefore.Clear();
            _ambient.Release(); _exit.Release(); _click.Release();
            _d.Merge();         // FUN_10049df5
            s.Post(ev, 0xc01e);
            s.Unregister();
        }
    }
}
