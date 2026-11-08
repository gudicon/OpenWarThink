using System;

namespace MW2.Shell
{
    /// <summary>
    /// Repair bay 0xc028 (open FUN_1005dafe 0x1005dafe, screen function FUN_1005f0c6 0x1005f0c6). Background 39 when
    /// called from the drop ship 0xc032 (buttons "b…"), else 13 (buttons "d…"). Colour 1 is 0x16 (normal), 0x11
    /// (selected), 0x22 (a critical that can be replaced, a lightly damaged section), 0x3a (no replacement, a heavily
    /// damaged or lost section). The mech's sections are eight pictures head…lleg (frame = FUN_1004ae6f state, +3 when
    /// selected, lost sections hidden), the mech "a&lt;code&gt;" at (454, 142). The list DAT_100b8d68 shows the name
    /// (centred on 535), the section (click: next section), tech year, value, armour and internals (click: select), and
    /// the twelve criticals of the pristine copy (click: select the item's slots); DAT_100b89b8 the funds, the price of
    /// the repairs made and the balance. Spots: 0 accept, 2 / 3 next / previous mech, 4 repair all, 5 clear, 6 repair
    /// the selection, 8..15 the sections (spot 7 is disabled, spot 1 does nothing). Leaving asks to accept
    /// (FUN_1005efd4) and recomputes the mech (FUN_1004ad22). The globals DAT_100b8d64 / DAT_100db740 (from
    /// FUN_1007475e / FUN_10074715) and the name "ashmp%s" set at the opening are not used by the screen.
    /// </summary>
    public static class RepairScreen
    {
        static readonly ShellRegions.Entry[] Spots =
        {
            new ShellRegions.Entry(500, 427, 602, 475, 0, 0, ""),
            new ShellRegions.Entry(400, 427, 499, 475, 0, 0, ""),
            new ShellRegions.Entry(537, 321, 631, 365, 0, 0, ""),
            new ShellRegions.Entry(443, 321, 536, 365, 0, 0, ""),
            new ShellRegions.Entry(47, 389, 190, 423, 0, 0, ""),
            new ShellRegions.Entry(47, 424, 190, 461, 0, 0, ""),
            new ShellRegions.Entry(283, 381, 383, 414, 0, 0, ""),
            new ShellRegions.Entry(283, 415, 383, 446, 0, 0, ""),
            new ShellRegions.Entry(104, 122, 135, 157, 0, 0, ""),
            new ShellRegions.Entry(164, 121, 218, 228, 0, 0, ""),
            new ShellRegions.Entry(18, 121, 67, 228, 0, 0, ""),
            new ShellRegions.Entry(99, 156, 140, 229, 0, 0, ""),
            new ShellRegions.Entry(135, 101, 163, 156, 0, 0, ""),
            new ShellRegions.Entry(74, 101, 104, 156, 0, 0, ""),
            new ShellRegions.Entry(143, 228, 231, 381, 0, 0, ""),
            new ShellRegions.Entry(7, 228, 107, 381, 0, 0, "")
        };
        /// <summary>Spots 8..15 → section (FUN_1005f0c6 cases 8..0xf).</summary>
        static readonly int[] SpotSection = { 0, 5, 4, 2, 3, 1, 7, 6 };
        /// <summary>DAT_100b9414.. names and DAT_100b8d18 / DAT_100b8d38 positions of the section pictures.</summary>
        static readonly string[] SectionAnims = { "head", "rtor", "ctor", "ltor", "rarm", "larm", "rleg", "lleg" };
        static readonly int[] SectionX = { 105, 75, 88, 120, 26, 154, 11, 129 }, SectionY = { 122, 108, 154, 107, 126, 126, 173, 173 };
        /// <summary>PTR_DAT_100ab978: section names.</summary>
        static readonly string[] SectionNames = { "Head", "Right Torso", "Center Torso", "Left Torso", "Right Arm", "Left Arm", "Right Leg", "Left Leg" };
        const int Below = unchecked((int)0x80000011), TwoBelow = unchecked((int)0x80000022), Same = unchecked((int)0x80000000);

        static Mw2Shell _s;
        static MechRepair _r;
        static ShellRegions _regions;
        static ShellSound _sectionSound, _click, _select, _repair, _ambient, _exit;    // DAT_100db77c, 784, 788, 780, 7a0, 78c
        static ShellAnim _accept, _repairAll, _clear, _repairItem, _next, _prev, _picture;
        static readonly ShellAnim[] _sections = new ShellAnim[8];
        static ShellItemList _info, _funds;
        static ShellItemList.Entry _armor, _internals, _price, _balance;
        static byte[] _normal, _selected, _good, _bad;
        static int _shown;              // DAT_100b89b0
        static bool _buttonsDown;       // DAT_100db7cc
        static bool _closed;

        public static void Open(Mw2Shell s)
        {
            _s = s;
            var c = s.Career;
            _closed = false;
            _shown = -1;
            _buttonsDown = false;
            c.Byte23BA = false;
            _normal = ShellSurface.MakeRemap(0x16);
            _selected = ShellSurface.MakeRemap(0x11);
            _good = ShellSurface.MakeRemap(0x22);
            _bad = ShellSurface.MakeRemap(0x3a);
            bool fromDropShip = c.Msg23C3 == 0xc032;
            s.Surface.LoadBackground(ShellPcx.Decode(s.Db.Unpack(fromDropShip ? 0x27 : 0xd)));
            if (c.UnitMechs.Count == 0)
            {
                s.Dialog("You have no Mechs to repair!#Ok", null);
                s.Post(c.Msg23C3, 0xc028);
                return;
            }
            _sectionSound = Sound(0x33, 0x28);
            _click = Sound(0x35, -1);
            _select = Sound(0x34, -1);
            _repair = Sound(0x67, -1);
            _ambient = Sound(0x37, 0x11);
            _ambient.LoopForever();
            _exit = Sound(0x39, 0x28);
            _regions = new ShellRegions(s.Surface, s.Font7, false, Spots, () => true);
            _regions.Disable(7);
            s.Anims.Clear();
            for (int i = 0; i < 8; i++) _sections[i] = new ShellAnim(s.Anims, SectionAnims[i], SectionX[i], SectionY[i], 0x21, 0);
            string p = fromDropShip ? "b" : "d";
            _accept = Button(p + "accept", 0x1fd, 0x1b8);
            _repairAll = Button(p + "repaira", 0x36, 0x185);
            _clear = Button(p + "cleara", 0x36, 0x1a8);
            _repairItem = Button(p + "repairi", 0x124, 0x180);
            _next = Button(p + "nextr", 0x222, 0x14a);
            _prev = Button(p + "prevr", 0x1c4, 0x146);
            _picture = null;
            _r = new MechRepair(c, s.Data.Tables) { PartReplaced = () => _repair.Play() };
            _r.Open();
            ShowPicture();
            ShowSections();
            if (_r.Section < 0 || _r.Section > 7) _r.Mech.SetI32(0x364, 0);
            else _shown = _r.Section;
            _ambient.Play();
            _info = Info();
            _info.Init();
            _funds = ShellFunds.Build(s, 22, 35, out _price, out _balance);   // DAT_100b89b8
            _funds.Init();
            UpdatePrice();
            s.Register(Tick);
        }

        static ShellSound Sound(int record, int volume)
        {
            var snd = new ShellSound(_s.gameObject, _s.Db, record);
            if (volume >= 0) snd.SetVolume(volume);
            return snd;
        }

        static ShellAnim Button(string name, int x, int y)
        {
            var a = new ShellAnim(_s.Anims, name, x, y, 0x21, 0);
            a.Show();
            return a;
        }

        static ShellText Draw(ShellFont font, string text, int x, int y, byte[] remap)
        {
            var t = new ShellText(_s.Surface, font, text, x, y, remap);
            t.Show();
            return t;
        }

        static MechRecord Mech => _r.Mech;
        static ShellTables Tables => _s.Data.Tables;

        /// <summary>FUN_1005ede4 0x1005ede4: the mech's "a&lt;code&gt;" picture at (454, 142), flags 0x68.</summary>
        static void ShowPicture()
        {
            if (_picture != null) { _picture.Hide(); _picture.Destroy(); }
            _picture = new ShellAnim(_s.Anims, "a" + Mech.Code, 0x1c6, 0x8e, 0x68, 0);
            _picture.Show();
        }

        /// <summary>FUN_1005ed11 0x1005ed11: every section picture at its state (+3 selected; a lost one shown only when
        /// selected, frame 6).</summary>
        static void ShowSections()
        {
            for (int i = 0; i < 8; i++)
            {
                var a = _sections[i];
                int st = Mech.SectionState(i);
                bool sel = i == Mech.I32(0x364);
                if (st == 3)
                {
                    if (sel) { a.SetFrame(6); a.Show(); }
                    else a.Hide();
                }
                else
                {
                    if (sel)
                    {
                        a.SetFrame(st + 3);
                        _shown = Mech.I32(0x364);
                    }
                    else a.SetFrame(st);
                    a.Show();
                }
            }
        }

        /// <summary>FUN_1005ceb6 0x1005ceb6: the previously selected section back to its own frame, the new one at +3.</summary>
        static void MarkSection()
        {
            if (_shown != -1)
            {
                var old = _sections[_shown];
                int st = Mech.SectionState(_shown);
                if (st == 3) old.Hide();
                else { old.SetFrame(st); old.Show(); }
            }
            var a = _sections[_r.Section];
            a.SetFrame(Mech.SectionState(_r.Section) + 3);
            a.Show();
            _shown = _r.Section;
        }

        /// <summary>FUN_10060001 0x10060001: a section spot; only a different section plays the sound and redraws.</summary>
        static void PickSection(int section)
        {
            Mech.SetI32(0x364, section);
            if (_shown == section) return;
            _sectionSound.Play();
            MarkSection();
            _info.ClearStates();
            _info.Refresh();
        }

        /// <summary>FUN_1005ec42 0x1005ec42: the price of the repairs (FUN_1005ecb2) and the balance.</summary>
        static void UpdatePrice()
        {
            int price = _r.Pending();
            _price.Data = price;
            _balance.Data = _s.Career.Funds - price;
            ShellFunds.ShowPrice(_funds, true);
        }

        // ---- list DAT_100b8d68 ----

        static ShellItemList Info()
        {
            var list = new ShellItemList();
            void Add(ShellItemList.Entry e) => list.Entries.Add(e);
            Add(new ShellItemList.Entry { X = 535, Y = 84, Remap = _normal, Draw = DrawName });
            Add(new ShellItemList.Entry { X = 253, Y = 74, W = 100, Remap = _selected, Draw = DrawSection, Click = NextSection });
            Add(new ShellItemList.Entry { X = 253, Y = Below, Remap = _normal });
            Add(new ShellItemList.Entry
            {
                X = 253, Y = Below, Remap = _normal,
                Draw = e => Draw(_s.Font11, $"Tech Year: {Mech.TechYear}", e.X, e.Y, e.Remap)        // FUN_1005d7c4
            });
            Add(new ShellItemList.Entry
            {
                X = 253, Y = Below, Remap = _normal,                                                    // FUN_1005d818
                Draw = e => Draw(_s.Font11, $"Value: {Debriefing.Money(ShellEconomy.MechPrice(Mech, _s.Career.Year, Tables))}", e.X, e.Y, e.Remap)
            });
            Add(new ShellItemList.Entry { X = 253, Y = Below, Remap = _normal });
            Add(new ShellItemList.Entry { X = 253, Y = Below, Remap = _normal });
            _armor = new ShellItemList.Entry { X = 253, Y = Below, W = 100, Remap = _normal, Draw = DrawArmor, Click = PickLine };
            Add(_armor);
            _internals = new ShellItemList.Entry { X = 253, Y = Below, W = 100, Remap = _normal, Data = 1, Draw = DrawInternals, Click = PickLine };
            Add(_internals);
            for (int i = 0; i < 3; i++) Add(new ShellItemList.Entry { X = 253, Y = Below, Remap = _normal });
            Add(new ShellItemList.Entry
            {
                X = 253, Y = Below, Remap = _normal,
                Draw = e => Draw(_s.Font7, "CRITICAL STATUS", e.X + 0x11, e.Y, e.Remap)               // FUN_1005d880
            });
            for (int i = 0; i < 12; i++)
                Add(new ShellItemList.Entry { X = 253, Y = i == 0 ? TwoBelow : Below, W = 100, Remap = _normal, Data = i, Draw = DrawCritical, Click = PickCritical });
            return list;
        }

        /// <summary>FUN_1005d6ec 0x1005d6ec: the name (+0x100, else the table name of the MEK's code), without its first
        /// word when wider than 166 (FUN_1004a860), centred in font 6.</summary>
        static ShellText DrawName(ShellItemList.Entry e)
        {
            string name = Mech.Str(0x100, 0x3a);
            if (name.Length == 0)
            {
                int i = Tables.FindMech(Mech.MekName);
                name = i >= 0 ? Tables.MechName(i) : "";
            }
            name = SkipFirstWord(name, _s.Font6, 0xa6);
            return Draw(_s.Font6, name, e.X - _s.Font6.Width(name) / 2, e.Y, _selected);
        }

        /// <summary>FUN_1004a860 0x1004a860.</summary>
        static string SkipFirstWord(string text, ShellFont font, int width)
        {
            if (font.Width(text) <= width) return text;
            int k = 0;
            while (k < text.Length && text[k] != ' ') k++;
            while (k < text.Length && text[k] == ' ') k++;
            return k < text.Length ? text.Substring(k) : text;
        }

        /// <summary>FUN_1005cb36 0x1005cb36.</summary>
        static ShellText DrawSection(ShellItemList.Entry e) => Draw(_s.Font11, SectionNames[_r.Section], e.X, e.Y, e.Remap);

        /// <summary>The colour of a section line by its state (FUN_1005cb91 / FUN_1005cd15), 0x11 when selected.</summary>
        static byte[] SectionRemap(ShellItemList.Entry e)
        {
            if (e.State == 1) return _selected;
            int st = Mech.SectionState(_r.Section);
            return st == 1 ? _good : st == 2 || st == 3 ? _bad : _normal;
        }

        /// <summary>FUN_1005cb91 0x1005cb91.</summary>
        static ShellText DrawArmor(ShellItemList.Entry e)
        {
            int o = 0x8e0 + _r.Section * 0x12;
            var m = Mech;
            var p = _r.Pristine;
            string text = p.I32(o + 12) < 0
                ? $"ARMOR: front: {m.I32(o + 8)}/{p.I32(o + 8)}  rear: None"
                : $"ARMOR: front: {m.I32(o + 8)}/{p.I32(o + 8)}  rear: {m.I32(o + 12)}/{p.I32(o + 12)}";
            return Draw(_s.Font11, text, e.X, e.Y, SectionRemap(e));
        }

        /// <summary>FUN_1005cd15 0x1005cd15.</summary>
        static ShellText DrawInternals(ShellItemList.Entry e)
        {
            int o = 0x8e0 + _r.Section * 0x12;
            return Draw(_s.Font11, $"INTERNALS: {Mech.I32(o)}/{_r.Pristine.I32(o)}", e.X, e.Y, SectionRemap(e));
        }

        /// <summary>
        /// FUN_1005d3b4 0x1005d3b4: the pristine copy's critical; empty slots have no text, 0 is "-". A slot lost on the
        /// mech is 0x22 with a replacement and 0x3a without one; selected 0x11. Names: weapon "%s #%d", equipment from
        /// the table at 0x100a8198, "CASE" (9550..9559), "Ammo (%s)".
        /// </summary>
        static ShellText DrawCritical(ShellItemList.Entry e)
        {
            int item = _r.Pristine.Crit(_r.Section, e.Data);
            if (item < 0) return null;
            if (item == 0) return Draw(_s.Font11, "-", e.X, e.Y, _normal);
            var remap = _normal;
            if (Mech.Crit(_r.Section, e.Data) < 1 && item > 0) remap = _r.Replacement(item, false, out _) ? _good : _bad;
            if (e.State == 1) remap = _selected;
            return Draw(_s.Font11, MechRepair.CriticalName(item, Tables), e.X, e.Y, remap);
        }

        /// <summary>FUN_1005ce55 0x1005ce55: the next section.</summary>
        static void NextSection(ShellItemList.Entry e)
        {
            _sectionSound.Play();
            int s = _r.Section + 1;
            Mech.SetI32(0x364, s > 7 ? 0 : s);
            _info.ClearStates();
            MarkSection();
        }

        /// <summary>FUN_1005d8ac 0x1005d8ac: select the armour or internals line.</summary>
        static void PickLine(ShellItemList.Entry e)
        {
            _info.ClearStates();
            e.State = 2;
            _select.Play();
        }

        /// <summary>FUN_1005d8d3 0x1005d8d3: select every slot of the item (only the one slot of an engine 7800 / 7850).</summary>
        static void PickCritical(ShellItemList.Entry e)
        {
            int item = _r.Pristine.Crit(_r.Section, e.Data);
            if (item <= 0) return;
            _info.ClearStates();
            if (item == 0x1eaa || item == 0x1e78) e.State = 1;
            else
                foreach (var x in _info.Entries)
                    if (x.Click == PickCritical && x.Data >= 0 && _r.Pristine.Crit(_r.Section, x.Data) == item) x.State = 1;
            _select.Play();
        }

        // ---- actions ----

        /// <summary>
        /// FUN_1005efd4 0x1005efd4: nothing to ask without +0x9cc; else "Accept repairs?": No gives everything back, Yes
        /// pays, or without the funds asks "Undo customizations?" (Yes undoes and goes on, No stays).
        /// </summary>
        static void Accept(Action<bool> done)
        {
            if (Mech.Data[0x9cc] == 0)
            {
                done(true);
                return;
            }
            UpdatePrice();
            _s.Dialog("Accept repairs?#Yes|No", a =>
            {
                if (a == 1)
                {
                    _click.Play();
                    _r.Undo();
                    done(true);
                }
                else if (_r.Answer(0)) done(true);
                else
                    _s.Dialog("Insufficient Funds!|Undo customizations?#Yes|No", b =>
                    {
                        _click.Play();
                        if (b == 0)
                        {
                            _r.Undo();
                            done(true);
                        }
                        else done(false);
                    });
            });
        }

        static void Redraw()
        {
            _info.Refresh();
            UpdatePrice();
            ShowSections();
        }

        static void Press(ShellAnim button)
        {
            _buttonsDown = true;
            button.SetFrame(1);
            _s.Anims.Step();
            _click.Play();
        }

        /// <summary>FUN_1005fa21 0x1005fa21: repair the selected line or critical.</summary>
        static void RepairSelection()
        {
            var c = _s.Career;
            ShellItemList.Entry sel = null;
            foreach (var e in _info.Entries) if (e.State > 0) { sel = e; break; }
            if (sel == null)
            {
                _s.Dialog("Select critical to repair first.#Ok", null);
                return;
            }
            int s = _r.Section;
            if (sel == _armor)
            {
                if (!_r.InternalsDone()) { _s.Dialog("Internals must be repaired first!#Ok", null); return; }
                int cost = _r.Armor(Mech, _r.Pristine, s, true);
                if (c.Funds < cost) _s.Dialog("Insufficient funds|for complete repair!#Ok", null);
                else
                {
                    if (cost > 0) _repair.Play();
                    _r.Armor(Mech, _r.Pristine, s, false);
                }
            }
            else if (sel == _internals)
            {
                int cost = _r.Internal(Mech, _r.Pristine, s, true);
                if (c.Funds < cost) _s.Dialog("Insufficent funds|for complete repair!#Ok", null);
                else
                {
                    if (cost > 0) _repair.Play();
                    _r.Internal(Mech, _r.Pristine, s, false);
                }
            }
            else
            {
                if (!_r.InternalsDone()) { _s.Dialog("Internals must be repaired first!#Ok", null); return; }
                // FUN_1005fca2: the first critical line in state 1.
                var crit = _info.Entries.Find(e => e.Click == PickCritical && e.State == 1);
                if (crit == null) return;
                int r = _r.RepairCritical(crit.Data, false);
                if (r == -1) _s.Dialog("No replacement in inventory#Ok", null);
                else if (r == -2) _s.Dialog("Critical needs no repairs#Ok", null);
            }
        }

        static void Tick(uint ev)
        {
            var s = _s;
            var c = s.Career;
            if (ev == 0xc004)
            {
                s.Anims.Step();
                bool down = s.MouseClicked;
                if (down)
                {
                    var e = _info.Hit(s.MouseX, s.MouseY);
                    if (e != null)
                    {
                        e.Click(e);
                        _info.Refresh();
                    }
                }
                else if (_buttonsDown)
                {
                    // FUN_10060115: the buttons back up.
                    _buttonsDown = false;
                    _repairAll.SetFrame(0); _clear.SetFrame(0); _repairItem.SetFrame(0); _prev.SetFrame(0); _next.SetFrame(0);
                }
                int spot = down ? _regions.Hit(s.MouseX, s.MouseY) : -1;
                switch (spot)
                {
                    case 0:
                        _accept.SetFrame(1);
                        s.Anims.Step();
                        s.BlockUntilEnd(_exit);
                        Accept(ok =>
                        {
                            if (!ok) _accept.SetFrame(0);
                            else if (c.Mode == 1) Leave(c.Byte23B9 ? 0xc036u : 0xc032u);
                        });
                        break;
                    case 2:
                    case 3:
                        if (c.UnitMechs.Count == 1) break;
                        Press(spot == 2 ? _next : _prev);
                        Accept(ok =>
                        {
                            if (!ok) return;
                            _r.Cycle(spot == 2 ? 1 : -1);
                            ShowSections();
                            ShowPicture();
                            _info.Refresh();
                            UpdatePrice();
                        });
                        break;
                    case 4:
                        Press(_repairAll);
                        if (_r.RepairAll(true) < c.Funds)
                        {
                            _r.RepairAll(false);
                            _repair.Play();
                            Redraw();
                        }
                        else s.Dialog("Insufficient funds|for complete repair!#Ok", null);
                        break;
                    case 5:
                        if (Mech.Data[0x9cc] == 0) break;
                        Press(_clear);
                        s.Dialog("Clear all Repairs?#Yes|No", a =>
                        {
                            if (a != 0) return;
                            _r.Undo();
                            Redraw();
                        });
                        break;
                    case 6:
                        Press(_repairItem);
                        RepairSelection();
                        Redraw();
                        break;
                    default:
                        if (spot >= 8 && spot <= 15) PickSection(SpotSection[spot - 8]);
                        break;
                }
                if (_closed) return;
            }
            // +0x23ba (a hot key) or 0xc02e (the menu): accept first; staying when refused.
            if (c.Byte23BA || ev == 0xc02e)
            {
                c.Byte23BA = false;
                uint target = ev;
                Accept(ok =>
                {
                    if (!ok) _accept.SetFrame(0);
                    else if (target != 0xc004) Leave(target);
                });
                return;
            }
            if (ev != 0xc004) Leave(ev);
        }

        static void Leave(uint ev)
        {
            if (_closed) return;
            _closed = true;
            var s = _s;
            Mech.Refit(Tables);     // FUN_1004ad22
            _regions.Dispose(); _regions = null;
            _info.Free();
            _funds.Free();
            s.Anims.Clear();
            s.Surface.DrawAfter.Clear(); s.Surface.DrawBefore.Clear();
            _ambient.Release(); _sectionSound.Release(); _exit.Release(); _click.Release(); _select.Release(); _repair.Release();
            s.Post(ev, 0xc028);
            s.Unregister();
        }
    }
}
