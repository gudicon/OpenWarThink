using System;
using System.Collections.Generic;
using UnityEngine;

namespace MW2.Shell
{
    /// <summary>
    /// Customizing 0xc02f (custom.cpp: open FUN_10016428 0x10016428, screen function FUN_10018e95 0x10018e95; the logic
    /// is MechCustomizer). Background 2 of SHELLFIX.MW2 (5 while the summary page is up). Colours: 1 → 0x16 (DAT_100ccfb0),
    /// 0x11 (DAT_100cceb0, headings and selections). Lists: DAT_1009fd50 funds, price and balance (shown in career mode 1),
    /// the name (click spot 0x15 to rename, FUN_10035083) and the mass line; DAT_100a0ae0 the section (click: next) and its
    /// twelve criticals (click: select the item's slots, double click: remove); the inventory list of the tab
    /// (FUN_10017920: name, criticals, in stock; click: select and show the weapon's figures DAT_1009fa08, double click:
    /// add); the armour panel DAT_100a0338 and the engine panel DAT_1009ff48 opened by the equipment rows 9 / 0; the
    /// assigning list FUN_10013161 for endo steel / ferro-fibrous; the summary DAT_1009f4c8. Spots (table 0x1009f1f8):
    /// 0 exit, 2..6 tabs missiles / energy / projectiles / equipment / ammunition, 7 / 8 previous / next mech, 9 remove,
    /// 10 add, 0xb..0x12 the sections, 0x13 the summary, 0x15 the name; 1 is not handled, 0x14 is disabled.
    /// Not ported: the glyph check of the name editor (FUN_100297d2).
    /// </summary>
    public static class CustomizingScreen
    {
        static readonly ShellRegions.Entry[] Spots =
        {
            new ShellRegions.Entry(502, 432, 604, 473, 0, 0, ""), new ShellRegions.Entry(399, 432, 501, 473, 0, 0, ""),
            new ShellRegions.Entry(8, 8, 100, 44, 0, 0, ""), new ShellRegions.Entry(101, 8, 190, 44, 0, 0, ""),
            new ShellRegions.Entry(191, 8, 280, 44, 0, 0, ""), new ShellRegions.Entry(281, 8, 369, 44, 0, 0, ""),
            new ShellRegions.Entry(370, 8, 462, 44, 0, 0, ""), new ShellRegions.Entry(28, 384, 124, 431, 0, 0, ""),
            new ShellRegions.Entry(125, 384, 219, 431, 0, 0, ""), new ShellRegions.Entry(281, 378, 386, 418, 0, 0, ""),
            new ShellRegions.Entry(485, 378, 590, 418, 0, 0, ""), new ShellRegions.Entry(104, 122, 135, 157, 0, 0, ""),
            new ShellRegions.Entry(164, 121, 218, 228, 0, 0, ""), new ShellRegions.Entry(18, 121, 67, 228, 0, 0, ""),
            new ShellRegions.Entry(99, 156, 140, 229, 0, 0, ""), new ShellRegions.Entry(135, 101, 163, 156, 0, 0, ""),
            new ShellRegions.Entry(74, 101, 104, 156, 0, 0, ""), new ShellRegions.Entry(143, 228, 231, 355, 0, 0, ""),
            new ShellRegions.Entry(7, 228, 107, 354, 0, 0, ""), new ShellRegions.Entry(70, 429, 170, 471, 0, 0, ""),
            new ShellRegions.Entry(179, 429, 282, 471, 0, 0, ""), new ShellRegions.Entry(23, 55, 217, 87, 0, 0, "")
        };
        /// <summary>Spots 0xb..0x12 → section (cases 0xb..0x12 of FUN_10018e95).</summary>
        static readonly int[] SpotSection = { 0, 5, 4, 2, 3, 1, 7, 6 };
        /// <summary>DAT_100a17e4.. names and DAT_1009f470 / DAT_1009f490 positions of the section pictures.</summary>
        static readonly string[] SectionAnims = { "head", "rtor", "ctor", "ltor", "rarm", "larm", "rleg", "lleg" };
        static readonly int[] SectionX = { 105, 75, 88, 120, 26, 154, 11, 129 }, SectionY = { 122, 108, 154, 107, 126, 126, 173, 173 };
        /// <summary>PTR_DAT_100ab978.</summary>
        static readonly string[] SectionNames = { "Head", "Right Torso", "Center Torso", "Left Torso", "Right Arm", "Left Arm", "Right Leg", "Left Leg" };
        const int Below = unchecked((int)0x80000011), TwoBelow = unchecked((int)0x80000022), Same = unchecked((int)0x80000000);

        static Mw2Shell _s;
        static MechCustomizer _m;
        static ShellRegions _regions;
        static ShellSound _exit, _click, _select, _ambient, _pay, _sectionSound;   // DAT_100c7758, 774c, 7750, 7744, 7748, 7754
        static ShellAnim _exitButton, _addButton, _removeButton, _summaryButton, _prevButton, _nextButton;
        static readonly ShellAnim[] _tabs = new ShellAnim[5];       // bcmiss, bcenrg, bcproj, bcequp, bcammo
        static readonly ShellAnim[] _sections = new ShellAnim[8];
        static ShellItemList _stats, _crits, _items, _info, _armor, _engine, _summary, _assign;
        static ShellItemList.Entry _price, _balance, _name;
        static readonly Dictionary<ShellItemList.Entry, MechCustomizer.Row> _rows = new Dictionary<ShellItemList.Entry, MechCustomizer.Row>();
        static byte[] _normal, _bright;
        static int _shown;              // DAT_100c7580: the section shown selected
        static bool _summaryUp;         // DAT_100c7670
        static int _infoCode = -1;      // DAT_100c758c
        static bool _closed;
        // The name editor (FUN_10035083), modal in the original.
        static bool _editing;
        static string _edited;
        static ShellText _editText;
        /// <summary>The summary's slot marks DAT_100c7678.</summary>
        static readonly bool[] _marks = new bool[96];

        public static void Open(Mw2Shell s)
        {
            _s = s;
            var c = s.Career;
            _closed = false;
            _editing = false;
            c.Byte23BA = false;
            _shown = -1;
            _summaryUp = false;
            _infoCode = -1;
            _normal = ShellSurface.MakeRemap(0x16);
            _bright = ShellSurface.MakeRemap(0x11);
            s.Surface.LoadBackground(ShellPcx.Decode(s.Fix.Unpack(c.Mode == 2 ? 1 : 2)));
            if (c.Mode != 2 && c.UnitMechs.Count == 0)
            {
                s.Dialog("You have no mechs to customize.#Ok", null);
                s.Post(c.Msg23C3, 0xc02f);
                return;
            }
            _exit = Sound(0x39, 0x28);
            _click = Sound(0x35, 0x32);
            _select = Sound(0x34, 0x32);
            _ambient = Sound(0x66, 10);
            _ambient.LoopForever();
            _ambient.Play();
            _pay = Sound(0x67, 0x32);
            _sectionSound = Sound(0x33, 0x28);
            _regions = new ShellRegions(s.Surface, s.Font7, false, Spots, () => true);
            _regions.Disable(0x14);
            s.Anims.Clear();
            string[] tabNames = { "bcmiss", "bcenrg", "bcproj", "bcequp", "bcammo" };
            int[] tabX = { 0xf, 0x69, 0xc2, 0x11c, 0x175 };
            for (int i = 0; i < 5; i++) _tabs[i] = Button(tabNames[i], tabX[i], 0xc, 0x61);
            _exitButton = Button("bcexit", 0x1fd, 0x1b8, 0x21);
            _addButton = Button("bcadd", 0x1f0, 0x17e, 0x21);
            _removeButton = Button("bcrem", 0x123, 0x17e, 0x21);
            _summaryButton = new ShellAnim(s.Anims, "mlbch", 0x50, 0x1b8, 0x21, 0);
            _summaryButton.SetFrame(1);
            _summaryButton.Show();
            new ShellAnim(s.Anims, "mlboxa", 4, 99, 0x21, 0);       // made, never shown
            if (c.Mode != 2)
            {
                _prevButton = Button("bcprev", 0x29, 0x189, 0x21);
                _nextButton = Button("bcnext", 0x83, 0x18b, 0x21);
            }
            for (int i = 0; i < 8; i++) _sections[i] = new ShellAnim(s.Anims, SectionAnims[i], SectionX[i], SectionY[i], 0x21, 0);
            _m = new MechCustomizer(c, s.Data.Tables) { Say = text => s.Dialog(text, null) };
            if (c.Mode == 2)
            {
                // The technology marks (mlbi / mlbc at 0x128 / 0x193, 0x1b8), the chassis' one on frame 1; the lab mech's tech 0 is Clan.
                var inner = new ShellAnim(s.Anims, "mlbi", 0x128, 0x1b8, 0x61, 0);
                var clan = new ShellAnim(s.Anims, "mlbc", 0x193, 0x1b8, 0x61, 0);
                (InstantAction.LabMech.Technology == 0 ? clan : inner).SetFrame(1);
                inner.Show();
                clan.Show();
                _m.OpenLab(InstantAction.LabMech);
            }
            else _m.Open();
            if (_m.Section >= 0 && _m.Section <= 7) _shown = _m.Section;
            ShowSections();
            _stats = Stats();
            _stats.Init();
            UpdatePrice();
            _crits = Crits();
            _crits.Init();
            _tabs[0].SetFrame(1);
            _m.ShowTab(2);
            BuildItems();
            s.Anims.Step();
            if (c.Mode == 1 && _m.NeedsRepair()) s.Dialog("Warning: Customizing prevents future repairs.#Ok", null);
            s.Register(Tick);
        }

        static ShellSound Sound(int record, int volume)
        {
            var snd = new ShellSound(_s.gameObject, _s.Db, record);
            snd.SetVolume(volume);
            return snd;
        }

        static ShellAnim Button(string name, int x, int y, uint flags)
        {
            var a = new ShellAnim(_s.Anims, name, x, y, flags, 0);
            a.Show();
            return a;
        }

        static ShellText Draw(ShellFont font, string text, int x, int y, byte[] remap)
        {
            var t = new ShellText(_s.Surface, font, text, x, y, remap);
            t.Show();
            return t;
        }

        static ShellText Text(ShellItemList.Entry e, string text) => Draw(_s.Font11, text, e.X, e.Y, e.Remap);
        /// <summary>FUN_10049693 0x10049693: right-aligned on x.</summary>
        static ShellText Right(ShellItemList.Entry e, string text, string measured, byte[] remap) =>
            Draw(_s.Font11, text, e.X - _s.Font11.Width(measured), e.Y, remap);
        static ShellItemList.Entry Label(int x, int y, string text, byte[] remap = null) =>
            new ShellItemList.Entry { X = x, Y = y, Remap = remap ?? _normal, Draw = e => Text(e, text) };

        static MechRecord Mech => _m.Mech;
        static ShellTables Tables => _s.Data.Tables;
        static string Tons(int v) => $"{v / 100}.{v / 10 % 10}{v % 10}";
        static string Kph(int mp) => (mp * 10.8).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " kph";

        // ---- section pictures ----

        static int State(int section) => _s.Career.Mode == 1 ? Mech.SectionState(section) : 0;

        /// <summary>FUN_1001770b 0x1001770b: every section at its state, the selected one +3; lost ones hidden.</summary>
        static void ShowSections()
        {
            for (int i = 0; i < 8; i++)
            {
                var a = _sections[i];
                int st = State(i);
                if (st == 3) { a.Hide(); continue; }
                if (i == _m.Section)
                {
                    a.SetFrame(st + 3);
                    _shown = i;
                }
                else a.SetFrame(st);
                a.Show();
            }
        }

        /// <summary>FUN_1001213b 0x1001213b: the old selection back to its state, the new one at +3 (shown even when lost).</summary>
        static void MarkSection(bool sound)
        {
            if (sound) _sectionSound.Play();
            if (!_summaryUp && _shown != -1)
            {
                var old = _sections[_shown];
                int st = State(_shown);
                if (st == 3) old.Hide();
                else { old.SetFrame(st); old.Show(); }
                var a = _sections[_m.Section];
                a.SetFrame(State(_m.Section) + 3);
                a.Show();
            }
            _shown = _m.Section;
        }

        // ---- funds, name and mass (DAT_1009fd50) ----

        static ShellItemList Stats()
        {
            var list = ShellFunds.Build(_s, 0x16, 0x23, out _price, out _balance);
            _name = new ShellItemList.Entry { X = 0x78, Y = 0x3f, Remap = _normal, Data = 1, Draw = DrawName };
            list.Entries.Add(_name);
            list.Entries.Add(new ShellItemList.Entry { X = 0x102, Y = 0x160, Remap = _normal, Draw = DrawMass });
            return list;
        }

        /// <summary>FUN_1001123f 0x1001123f: the name (+0x100, else the table name of the MEK's code), its first word
        /// dropped when wider than 188 (FUN_1004a860), centred in font 6, colour 0x11; hidden while renaming.</summary>
        static ShellText DrawName(ShellItemList.Entry e)
        {
            if (e.Data == 0) return null;
            string name = Mech.Str(0x100, 0x3a);
            if (name.Length == 0)
            {
                int i = Tables.FindMech(Mech.MekName);
                name = i >= 0 ? Tables.MechName(i) : "";
            }
            name = SkipFirstWord(name, _s.Font6, 0xbc);
            return Draw(_s.Font6, name, e.X - _s.Font6.Width(name) / 2, e.Y, _bright);
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

        /// <summary>FUN_100110dc 0x100110dc: drawing it recomputes the engine, movement, weapon and ammunition masses, the
        /// armour mass from the points (not while the armour panel is up) and the totals.</summary>
        static ShellText DrawMass(ShellItemList.Entry e)
        {
            var m = Mech;
            m.Engine(Tables);
            m.Movement();
            m.WeaponMass(Tables);
            m.AmmoMass();
            if (!_m.ArmorPanel) m.ArmorMass();
            m.Totals();
            return Text(e, $"Mass: {Tons(m.I32(0x204))} T Max: {Tons(m.I32(0x200))} T");
        }

        /// <summary>FUN_10011be4 0x10011be4: price and balance shown, the price of the changes (FUN_10011862).</summary>
        static void UpdatePrice()
        {
            int price = _m.Price;
            _price.Data = price;
            _balance.Data = _s.Career.Funds - price;
            ShellFunds.ShowPrice(_stats, true);
        }

        // ---- criticals (DAT_100a0ae0) ----

        static ShellItemList Crits()
        {
            var list = new ShellItemList();
            list.Entries.Add(new ShellItemList.Entry
            {
                X = 0xff, Y = 0x5a, W = 0x64, Remap = _bright, Click = NextSection,
                Draw = e => Text(e, SectionNames[_m.Section])                                        // FUN_1003bbcf
            });
            list.Entries.Add(new ShellItemList.Entry { X = 0xff, Y = Below, Remap = _normal });    // FUN_1004a8f1
            for (int i = 0; i < 12; i++)
                list.Entries.Add(new ShellItemList.Entry { X = 0xff, Y = Below, W = 0x64, Remap = _normal, Data = i, Draw = DrawSlot, Click = PickSlot });
            return list;
        }

        /// <summary>FUN_10013578 0x10013578.</summary>
        static ShellText DrawSlot(ShellItemList.Entry e)
        {
            string text = _m.SlotText(e.Data);
            if (text == null) return null;
            return Draw(_s.Font11, text, e.X, e.Y, e.State == 1 ? _bright : e.Remap);
        }

        /// <summary>FUN_10013928 0x10013928: the next section.</summary>
        static void NextSection(ShellItemList.Entry e)
        {
            _crits.ClearStates();
            _sectionSound.Play();
            int next = _m.Section + 1;
            Mech.SetI32(0x364, next > 7 ? 0 : next);
            MarkSection(true);
        }

        /// <summary>FUN_100134b0 0x100134b0: every slot of the item selected (not while the armour panel is up).</summary>
        static void PickSlot(ShellItemList.Entry e)
        {
            if (_m.ArmorPanel) return;
            int v = Mech.Crit(_m.Section, e.Data);
            if (v <= 0) return;
            _crits.ClearStates();
            foreach (var x in _crits.Entries)
                if (x != _crits.Entries[0] && x.Data >= 0 && Mech.Crit(_m.Section, x.Data) == v) x.State = 1;
            _select.Play();
        }

        // ---- the inventory list (FUN_10017920) ----

        /// <summary>FUN_100177fc 0x100177fc: the list rebuilt for the list shown, the row of the index selected before
        /// selected again.</summary>
        static void BuildItems()
        {
            int keep = -2;
            if (_items != null)
            {
                foreach (var e in _items.Entries) if (e.State > 0) { keep = e.Data; break; }
                _items.Free();
            }
            _items = Items();
            _items.Init();
            for (int k = 0; k < _items.Entries.Count; k++)
                if (_rows.ContainsKey(_items.Entries[k]) && _items.Entries[k].Data == keep)
                {
                    for (int d = 0; d < 3 && k + d < _items.Entries.Count; d++) _items.Entries[k + d].State = 1;
                    _items.Refresh();
                    return;
                }
        }

        static ShellItemList Items()
        {
            var c = _s.Career;
            var list = new ShellItemList();
            _rows.Clear();
            var rows = _m.Rows();
            int h = _s.Font11.Height;
            int cratesX = c.Mode == 1 ? 0x231 : 0x267;
            if (rows.Count == 0)
            {
                list.Entries.Add(new ShellItemList.Entry
                {
                    X = 0x1cc, Y = 0x5c, Remap = _normal, Data = -1,
                    Draw = e => c.Mode == 2 ? null : Text(e, "None in stock")                       // FUN_1001883e
                });
                return list;
            }
            list.Entries.Add(new ShellItemList.Entry { X = 0x1cc, Y = 0x5c, Data = -1, Draw = e => Draw(_s.Font11, "Weapon:", e.X, e.Y, _bright) });
            list.Entries.Add(new ShellItemList.Entry
            {
                X = cratesX, Y = 0x5c, Data = -1,                                                         // FUN_100187c1
                Draw = e => { string t = _m.Tab == 6 ? "Crits/Ton" : "Crits"; return Right(e, t, t, _bright); }
            });
            list.Entries.Add(new ShellItemList.Entry
            {
                X = 0x267, Y = 0x5c, Data = -1,                                                           // FUN_10018744
                Draw = e => c.Mode == 2 || c.Mode == 0 ? null : Right(e, "In Stock", "In Stock", _bright)
            });
            for (int g = 0; g < rows.Count; g++)
            {
                var r = rows[g];
                int y = (2 + g) * h + 0x5c;
                var name = new ShellItemList.Entry { X = 0x1cc, Y = y, W = 0x9c, Remap = _normal, Data = r.Index, Data2 = r.Count, Draw = DrawRowName, Click = PickRow };
                var crits = new ShellItemList.Entry { X = cratesX, Y = y, Remap = _normal, Data = r.Index, Data2 = r.Count, Draw = DrawRowCrits };
                var stock = new ShellItemList.Entry { X = 0x267, Y = y, Remap = _normal, Data = r.Index, Data2 = r.Count, Draw = DrawRowStock };
                foreach (var e in new[] { name, crits, stock }) { _rows[e] = r; list.Entries.Add(e); }
            }
            return list;
        }

        /// <summary>FUN_1001816c 0x1001816c: "%s:" of the equipment (0x100abacc) or weapon name.</summary>
        static ShellText DrawRowName(ShellItemList.Entry e)
        {
            var r = _rows[e];
            string text;
            if (_m.Tab == 5) text = r.Index < 0 || r.Index > 0xb ? $"BAD EQUIP!! {r.Index}" : Tables.EquipmentName(r.Index) + ":";
            else text = Tables.WeaponName(r.Index) + ":";
            return Draw(_s.Font11, text, e.X, e.Y, e.State == 1 ? _bright : e.Remap);
        }

        /// <summary>FUN_10018253 0x10018253: right-aligned criticals, "-" for none.</summary>
        static ShellText DrawRowCrits(ShellItemList.Entry e)
        {
            int n = _m.RowCrits(_rows[e]);
            string t = n < 0 ? "-" : n.ToString();
            return Right(e, t, t, e.State == 1 ? _bright : e.Remap);
        }

        /// <summary>FUN_100184e1 0x100184e1 ("1"; selected, the price shows the criticals of the weapon entry of the index
        /// × 50000) or FUN_10018611 0x10018611 (the undamaged count, placed as if it were "-" when 0).</summary>
        static ShellText DrawRowStock(ShellItemList.Entry e)
        {
            var r = _rows[e];
            var c = _s.Career;
            if (r.Single)
            {
                if (c.Mode == 2 || c.Mode == 0) return null;
                if (e.State == 1)
                {
                    int crits = Tables.Weapon(r.Index).Slots;
                    _price.Data = crits * 50000;
                    _balance.Data = c.Funds - crits * 50000;
                }
                return Right(e, "1", "1", e.State == 1 ? _bright : e.Remap);
            }
            string t = r.InStock.ToString();
            return Right(e, t, r.InStock == 0 ? "-" : t, e.State == 1 ? _bright : e.Remap);
        }

        /// <summary>FUN_10018898 0x10018898: select the row (all three columns); a weapon's figures shown when no panel is
        /// up and the tab is not equipment or ammunition; an open panel closed.</summary>
        static void PickRow(ShellItemList.Entry e)
        {
            var r = _rows[e];
            _select.Play();
            if (!_m.EnginePanel && !_m.ArmorPanel && !_m.Assigning && _m.Tab != 5 && _m.Tab != 6)
            {
                _infoCode = MechCustomizer.Code(r.Index, r.Kind);
                _info?.Free();
                _info = Info();
                _info.Init();
            }
            if (_m.EnginePanel || _m.ArmorPanel)
            {
                _m.EnginePanel = _m.ArmorPanel = false;
                _stats.Refresh();
                _armor?.Free();
                _engine?.Free();
                _crits.Free();
                _crits.Init();
            }
            _items.ClearStates();
            int k = _items.Entries.IndexOf(e);
            for (int d = 0; d < 3 && k + d < _items.Entries.Count; d++) _items.Entries[k + d].State = 2;
        }

        // ---- weapon figures (DAT_1009fa08, FUN_10011335) ----

        static ShellItemList Info()
        {
            var list = new ShellItemList();
            string[] labels = { "Type", "Heat", "Damage", "Range", "Mass", "Crit", "Ammo" };
            int[] fields = { 0, 1, 2, 6, 7, 8, 9 };
            for (int i = 0; i < 7; i++)
            {
                int field = fields[i];
                list.Entries.Add(Label(0x102, i == 0 ? 0xf9 : i == 1 ? TwoBelow : Below, labels[i]));
                list.Entries.Add(new ShellItemList.Entry
                {
                    X = i == 0 ? 0x12a : 0x134, Y = Same, Remap = _normal,
                    Draw = e => _infoCode < 0 ? null : Text(e, ArmsDealer.StatText(field, _infoCode, false, false, Tables))
                });
            }
            return list;
        }

        // ---- armour panel (DAT_100a0338) ----

        static ShellItemList ArmorPanel()
        {
            var list = new ShellItemList();
            void Add(ShellItemList.Entry e) => list.Entries.Add(e);
            Add(Label(0x1cc, 0xe8, "ARMOR"));
            Add(Label(0x1cc, TwoBelow, "Factor"));
            Add(new ShellItemList.Entry { X = 0x230, Y = Same, Remap = _normal, Draw = e => Text(e, Mech.I32(0x258).ToString()) });     // FUN_10011f08
            Add(Label(0x1cc, Below, "Allocated"));
            Add(new ShellItemList.Entry { X = 0x230, Y = Same, Remap = _normal, Draw = e => Text(e, Mech.I32(0x25c).ToString()) });     // FUN_10011f62
            Add(Label(0x1cc, Below, "Mass"));
            Add(new ShellItemList.Entry { X = 0x230, Y = Same, Remap = _normal, Draw = e => Text(e, Tons(Mech.I32(0x250)) + " T") });   // FUN_1001203e
            Add(Label(0x1cc, Below, "Type"));
            Add(new ShellItemList.Entry { X = 0x230, Y = Same, Remap = _normal, Draw = e => Text(e, Mech.I32(0x24c) == 0 ? "Std" : "Ferro-F") });
            Add(new ShellItemList.Entry { X = 0x1cc, Y = TwoBelow, Remap = _bright, Draw = e => Text(e, "ADD"), Click = e => _m.ArmorTons(1) });
            Add(new ShellItemList.Entry { X = 0x1cc, Y = Below, Remap = _bright, Draw = e => Text(e, "DELETE"), Click = e => _m.ArmorTons(-1) });
            Add(new ShellItemList.Entry { X = 0x23a, Y = TwoBelow, Remap = _normal, Data = 0, Draw = DrawSide });
            Add(new ShellItemList.Entry { X = 0x258, Y = Same, Remap = _normal, Data = 1, Draw = DrawSide });
            Add(new ShellItemList.Entry { X = 0x237, Y = 0x144, Remap = _bright, Draw = e => Text(e, "\u0001"), Click = e => _m.ArmorPoint(false, true) });
            Add(new ShellItemList.Entry { X = 0x255, Y = Same, Remap = _bright, Draw = e => Text(e, "\u0001"), Click = e => _m.ArmorPoint(true, true) });
            Add(new ShellItemList.Entry { X = 0x237, Y = 0x166, Remap = _bright, Draw = e => Text(e, "\u0002"), Click = e => _m.ArmorPoint(false, false) });
            Add(new ShellItemList.Entry { X = 0x255, Y = Same, Remap = _bright, Draw = e => Text(e, "\u0002"), Click = e => _m.ArmorPoint(true, false) });
            Add(Label(0x103, 0xe8, "ARMOR ALLOCATION"));
            for (int s = 0; s < 8; s++)
            {
                int section = s;
                Add(new ShellItemList.Entry
                {
                    X = 0x103, Y = s == 0 ? TwoBelow : Below, Remap = _normal, Data = s,                           // FUN_10012767
                    Draw = e => Text(e, $"{SectionNames[section]} ({Mech.I32(0x8e4 + section * 0x12)})")
                });
                Add(new ShellItemList.Entry
                {
                    X = 0x17b, Y = Same, Remap = _normal, Data = s,                                                // FUN_100126d1
                    Draw = e =>
                    {
                        int o = 0x8e0 + section * 0x12;
                        return Text(e, Mech.I32(o + 12) < 0 ? $"~{Mech.I32(o + 8)}" : $"~{Mech.I32(o + 8)}/{Mech.I32(o + 12)}");
                    }
                });
            }
            return list;
        }

        /// <summary>FUN_10012340 0x10012340: the selected section's front / rear points, centred ("~--" without rear).</summary>
        static ShellText DrawSide(ShellItemList.Entry e)
        {
            int o = 0x8e0 + _m.Section * 0x12;
            if (e.Data == 0) return Text(e, $"~{Mech.I32(o + 8)}");
            return Text(e, Mech.I32(o + 12) < 0 ? "~--" : $"~{Mech.I32(o + 12)}");
        }

        // ---- engine panel (DAT_1009ff48) ----

        static ShellItemList EnginePanel()
        {
            var list = new ShellItemList();
            void Add(ShellItemList.Entry e) => list.Entries.Add(e);
            int Index() => Mech.I32(0x20c) % 10000;
            Add(Label(0x1cc, 0xe8, "ENGINE"));
            Add(Label(0x1cc, TwoBelow, "Rating"));
            Add(new ShellItemList.Entry                                                                       // FUN_1001285c
            {
                X = 0x230, Y = Same, Remap = _normal,
                Draw = e => Text(e, Tables.EngineRating(Index()) + (Mech.I32(0x20c) < 10000 ? "" : "XL"))
            });
            Add(Label(0x1cc, Below, "Type"));
            Add(new ShellItemList.Entry                                                                       // FUN_100128e8
            {
                X = 0x230, Y = Same, W = 0x32, Remap = _bright, Click = e => _m.ToggleXl(),
                Draw = e => Text(e, Mech.I32(0x20c) < 10000 ? "Std" : "XL")
            });
            Add(Label(0x1cc, Below, "Manufacturer"));
            Add(new ShellItemList.Entry { X = 0x230, Y = Same, Remap = _normal, Draw = e => Text(e, Tables.Engine(Index()).Maker) });
            Add(Label(0x1cc, Below, "Mass"));
            Add(new ShellItemList.Entry { X = 0x230, Y = Same, Remap = _normal, Draw = e => Text(e, Tons(Mech.I32(0x214)) + " T") });   // FUN_10011fbc
            Add(Label(0x1cc, Below, "Walking Speed"));
            Add(new ShellItemList.Entry { X = 0x230, Y = Same, Remap = _normal, Draw = e => Text(e, Kph(Mech.I32(0x280))) });          // FUN_100129a7
            Add(Label(0x1cc, Below, "Running Speed"));
            Add(new ShellItemList.Entry { X = 0x230, Y = Same, Remap = _normal, Draw = e => Text(e, Kph(Mech.I32(0x284))) });          // FUN_10012a0c
            Add(Label(0x1cc, Below, "Engine Price"));
            Add(new ShellItemList.Entry                                                                       // FUN_10012a71
            {
                X = 0x230, Y = Same, Remap = _normal,
                Draw = e => Text(e, _s.Career.Mode == 2 ? "None" : _m.EnginePrice == 0 ? "0" : Debriefing.Money(_m.EnginePrice))
            });
            Add(new ShellItemList.Entry { X = 0x1cc, Y = TwoBelow, Remap = _bright, Draw = e => Text(e, "FASTER"), Click = e => _m.Speed(1) });
            Add(new ShellItemList.Entry { X = 0x1cc, Y = Below, Remap = _bright, Draw = e => Text(e, "SLOWER"), Click = e => _m.Speed(-1) });
            return list;
        }

        // ---- endo steel / ferro-fibrous (FUN_10013161) ----

        static void BuildAssign()
        {
            _assign?.Free();
            var list = new ShellItemList();
            int h = _s.Font11.Height;
            list.Entries.Add(new ShellItemList.Entry { X = 0x1ce, Y = 0x5b, Draw = e => Draw(_s.Font11, "Assign Crits", e.X, e.Y, _bright) });
            string row = _m.AssignCode == 9000 ? "Endo Steel (1)" : _m.AssignCode == 0x251c ? "Ferro-Fibrous (1)" : "";
            for (int i = 0; i < _m.AssignLeft; i++)
                list.Entries.Add(new ShellItemList.Entry
                {
                    X = 0x1ce, Y = (i + 2) * h + 0x5b, Data = 1,
                    Draw = e => Draw(_s.Font11, row, e.X, e.Y, _bright), Click = e => { if (_m.AssignOne()) _select.Play(); }
                });
            _assign = list;
            _assign.Init();
        }

        /// <summary>FUN_10012fba as the screen does it: the check, then the assigning list in place of the inventory list.</summary>
        static void StartAssigning()
        {
            if (!_m.StartAssigning(_m.AssignCode, _m.AssignLeft)) return;
            _armor?.Free();
            BuildAssign();
            _items?.Free();
            _items = null;
        }

        /// <summary>FUN_10018c56 0x10018c56: one critical assigned (the first row when <paramref name="e"/> is null); when none
        /// is left assigning ends and the inventory list comes back.</summary>
        static void AssignRow(ShellItemList.Entry e)
        {
            if (e == null && _assign.Entries.Count > 1) e = _assign.Entries[1];
            e?.Click?.Invoke(e);
            _crits.Refresh();
            StartAssigning();
            if (_m.AssignLeft < 1)
            {
                _m.FinishAssigning();
                _assign.Free();
                _assign = null;
                _stats.Refresh();
                BuildItems();
            }
            if (_summaryUp) _summary.Refresh();
            UpdatePrice();
        }

        // ---- summary (DAT_1009f4c8) ----

        static ShellItemList Summary()
        {
            var list = new ShellItemList();
            list.Entries.Add(new ShellItemList.Entry { X = 0x2b, Y = 0x7b, Remap = _normal, Draw = e => Text(e, SummaryName()) });     // FUN_10010320
            list.Entries.Add(new ShellItemList.Entry { X = 0x2b, Y = Below, Remap = _normal,                                         // FUN_100104ec
                Draw = e => Text(e, string.Format(System.Globalization.CultureInfo.InvariantCulture, "walk: {0:0.0} kph run: {1:0.0} kph",
                    Mech.I32(0x280) * 10.8, Mech.I32(0x284) * 10.8)) });
            list.Entries.Add(new ShellItemList.Entry { X = 0x2b, Y = Below, Remap = _normal, Draw = e => Text(e, JumpJets()) });          // FUN_10010570
            list.Entries.Add(new ShellItemList.Entry { X = 0x2b, Y = Below, Remap = _normal,                                         // FUN_100106e2
                Draw = e => Text(e, $"{Mech.I32(0x210) / 0x19} internal {(Mech.I32(0x228) == 0 ? "single" : "double")} engine heat sinks") });
            list.Entries.Add(new ShellItemList.Entry { X = 0x2b, Y = Below, Remap = _normal, Draw = e => Text(e, ExternalSinks()) });     // FUN_10010949
            list.Entries.Add(new ShellItemList.Entry { X = 0x2b, Y = Below, Remap = _normal, Draw = e => Text(e, $"Armor: {Tons(Mech.I32(0x250))} T") });   // FUN_10010bbd
            list.Entries.Add(new ShellItemList.Entry { X = 0x2b, Y = Same, Remap = _normal, Draw = e => { Array.Clear(_marks, 0, _marks.Length); return null; } });   // FUN_10010c65
            for (int i = 0; i < 16; i++)
                list.Entries.Add(new ShellItemList.Entry { X = 0x2b, Y = Below, Remap = _normal, Data = i, Draw = e => { string t = WeaponLine(); return t == null ? null : Text(e, t); } });
            return list;
        }

        static string SummaryName()
        {
            string name = Mech.Str(0x100, 0x3a);
            if (name.Length == 0)
            {
                int i = Tables.FindMech(Mech.MekName);
                name = i >= 0 ? Tables.MechName(i) : "";
            }
            return $"{name} {Tons(Mech.I32(0x204))}/{Tons(Mech.I32(0x200))} T";
        }

        /// <summary>The criticals below 10000 of the equipment row <paramref name="code"/> (FUN_10010570 / FUN_100106e2).</summary>
        static int CountEquipment(int code)
        {
            int n = 0;
            for (int s = 0; s < 8; s++)
                for (int j = 0; j < 12; j++)
                {
                    int v = Mech.Crit(s, j);
                    if (v >= 10000) continue;
                    for (int k = 0x16; k >= 0; k--)
                    {
                        int e = Tables.Item(k).Code;
                        if (v / 0x32 == e / 0x32 && e == code) n++;
                    }
                }
            return n;
        }

        static string JumpJets()
        {
            int n = CountEquipment(0x2134);
            return n < 1 ? "No Jump Jets" : $"{n} Jump Jets";
        }

        /// <summary>FUN_10010949 0x10010949: Clan doubles take 2 slots, Inner Sphere 3.</summary>
        static string ExternalSinks()
        {
            int n = CountEquipment(8000);
            bool single = false, dbl = false;
            int singles = 0, doubles = 0;
            if (n > 0)
            {
                if (Mech.Technology == 0)
                {
                    dbl = Mech.I32(0x228) != 0;
                    if (dbl) doubles = n / 2; else singles = n;
                    single = !dbl;
                }
                else if (Mech.I32(0x228) == 0) { single = true; singles = n; }
                else { dbl = true; doubles = n / 3; }
            }
            if (single) return $"{singles} external single heat sinks";
            if (dbl) return $"{doubles} external double heat sinks";
            return "No external heat sinks";
        }

        /// <summary>
        /// FUN_10010cb6 0x10010cb6: the next unmarked weapon or ammunition critical in slot order, with the other criticals
        /// of the same index counted (and marked, FUN_10011071) from its slot on in every later section: "%d %s" or
        /// "%d ton(s) %s Ammo"; null when none is left.
        /// </summary>
        static string WeaponLine()
        {
            void MarkCode(int s0, int j0, int code)
            {
                for (int s = s0; s < 8; s++)
                    for (int j = j0 + 1; j < 12; j++)
                        if (Mech.Crit(s, j) == code) _marks[s * 12 + j] = true;
            }
            for (int s = 0; s < 8; s++)
                for (int j = 0; j < 12; j++)
                {
                    if (_marks[s * 12 + j]) continue;
                    _marks[s * 12 + j] = true;
                    int v = Mech.Crit(s, j);
                    int kind = MW2.Formats.Campaign.ItemKind(v);
                    if (v < 0 || kind == 3 || kind == 2) continue;
                    int idx = Career.ItemIndex(kind, v, Tables), more = 0;
                    MarkCode(s, j, v);
                    for (int s2 = s; s2 < 8; s2++)
                        for (int j2 = j; j2 < 12; j2++)
                        {
                            if (_marks[s2 * 12 + j2]) continue;
                            int w = Mech.Crit(s2, j2);
                            if (w < 0) continue;
                            int k2 = MW2.Formats.Campaign.ItemKind(w);
                            if (k2 == 3 || k2 == 2 || Career.ItemIndex(k2, w, Tables) != idx || k2 != kind) continue;
                            _marks[s2 * 12 + j2] = true;
                            more++;
                            MarkCode(s2, j2, w);
                        }
                    if (kind == 0) return $"{more + 1} {Tables.WeaponName(idx)}";
                    return more + 1 == 1 ? $"1 ton {Tables.WeaponName(idx)} Ammo" : $"{more + 1} tons {Tables.WeaponName(idx)} Ammo";
                }
            return null;
        }

        static void RefreshSummary()
        {
            if (_summaryUp) _summary.Refresh();
        }

        // ---- actions ----

        /// <summary>The add of FUN_10018e95 (LAB_100194a3): not into a lost piece; the selected row of the list goes through
        /// FUN_10014781 (ammunition), FUN_100160da (equipment: rows 0 / 9 open the engine / armour panels) or FUN_100161fc.</summary>
        static void AddSelected()
        {
            if (Mech.I32(0x8e0 + _m.Section * 0x12) == 0)
            {
                _s.Dialog("Cannot add items to|destroyed piece!#Ok", null);
                return;
            }
            ShellItemList.Entry sel = null;
            if (_items != null) foreach (var e in _items.Entries) if (e.State > 0) { sel = e; break; }
            if (sel == null || !_rows.TryGetValue(sel, out var row))
            {
                _s.Dialog("Select item to add first#Ok", null);
                return;
            }
            if (_m.Current == _m.Stock.Equipment && row.Index == 0)
            {
                _m.EnginePanel = true;
                _m.ArmorPanel = false;
                _armor?.Free();
                _engine?.Free();
                _engine = EnginePanel();
                _engine.Init();
                _crits.Free();
                _crits.Init();
            }
            else if (_m.Current == _m.Stock.Equipment && row.Index == 9)
            {
                _m.ArmorPanel = true;
                _m.EnginePanel = false;
                _engine?.Free();
                _crits.Free();
                _armor?.Free();
                _armor = ArmorPanel();
                _armor.Init();
            }
            else
            {
                // FUN_100160da frees the engine panel too; FUN_10014781 / FUN_100161fc leave its texts.
                if (_m.Current == _m.Stock.Equipment) _engine?.Free();
                _m.ArmorPanel = _m.EnginePanel = false;
                _armor?.Free();
                _crits.Free();
                _crits.Init();
                bool assigning = _m.Assigning;
                // In mode 1 the original also lowers the row's count (+0x28); the list is rebuilt right after.
                _m.Add(row.Index, row.Kind);
                if (!assigning && _m.Assigning)
                {
                    _armor?.Free();
                    BuildAssign();
                    _items?.Free();
                    _items = null;
                }
            }
            Mech.Refit(Tables);
            _stats.Refresh();
            if (!_m.Assigning) BuildItems();
            if (!_m.ArmorPanel) _crits.Refresh();
        }

        /// <summary>The remove of FUN_10018e95 (LAB_10019308): the first selected slot through FUN_1001aa50.</summary>
        static void RemoveSelected()
        {
            ShellItemList.Entry sel = null;
            foreach (var e in _crits.Entries) if (e.State > 0) { sel = e; break; }
            if (sel == null || Mech.Crit(_m.Section, sel.Data) < 1)
            {
                _s.Dialog("Select critical to remove first#Ok", null);
                return;
            }
            _m.RemoveSlot(sel.Data);
            Mech.Data[0x9cc] = 1;
            _crits.Refresh();
            Mech.Refit(Tables);
            _stats.Refresh();
            if (_m.Assigning) StartAssigning();
            else BuildItems();
        }

        /// <summary>
        /// FUN_10011ce9 0x10011ce9: without +0x9cc nothing to ask. "Accept customizations?": No undoes (FUN_10011a36).
        /// Yes: over the maximum tonnage refused; in mode 1 the price must be covered, else "Undo customizations?" (Yes
        /// undoes, No stays); paid: the pristine copy replaced (FUN_10011c64) and "Customization cost %s CBills". Mode 0
        /// accepts without a price.
        /// </summary>
        static void Accept(Action<bool> done)
        {
            var c = _s.Career;
            _stats.Refresh();
            if (Mech.Data[0x9cc] != 1)
            {
                done(true);
                return;
            }
            _s.Dialog("Accept customizations?#Yes|No", a =>
            {
                if (a == 1)
                {
                    _click.Play();
                    _m.Undo();
                    UpdatePrice();
                    _m.ForgetItems();
                    done(true);
                    return;
                }
                Mech.Refit(Tables);
                _stats.Refresh();
                if (Mech.I32(0x200) < Mech.I32(0x204))
                {
                    _s.Dialog("Mech exceeds|Defined Maximum Tonnage.#Ok", null);
                    done(false);
                    return;
                }
                if (c.Mode == 1)
                {
                    int cost = _m.Price;
                    if (c.Funds < cost)
                    {
                        _s.Dialog("Insufficient Funds!|Undo customizations?#Yes|No", b =>
                        {
                            _click.Play();
                            if (b != 0) { done(false); return; }
                            _m.Undo();
                            UpdatePrice();
                            _m.ForgetItems();
                            done(true);
                        });
                        return;
                    }
                    _s.BlockUntilEnd(_pay);
                    _m.Commit(cost);
                    _s.Dialog($"Customization cost {Debriefing.Money(cost)} CBills#Ok", null);
                }
                else if (c.Mode == 0)
                {
                    _s.BlockUntilEnd(_pay);
                    _m.Commit(0);
                }
                _m.ForgetItems();
                done(true);
            });
        }

        /// <summary>
        /// Case 0 of FUN_10018e95 in mode 2: "Accept Customizations?" — Yes checks the mech with FUN_100189b3 (its MEK is
        /// checked by MechLabCheck; the message is shown with "#Ok") and writes it as the user's MEK; when it is refused
        /// "Abort Customization?" — No stays, Yes (or No to the first question) lets the mech go. Then instant action 0xc029.
        /// </summary>
        static void LabExit()
        {
            var lab = InstantAction.LabMech;
            _s.Dialog("Accept Customizations?#Yes|No", a =>
            {
                if (a == 0)
                {
                    string refusal = MechLabCheck.Check(lab.ToMek(), lab.Tons100 / 100, lab.Name, lab.Technology, Tables);
                    if (refusal == null)
                    {
                        if (!InstantAction.WriteLabMek(lab)) Debug.LogWarning("MW2 shell: could not write the variant's MEK");
                        Leave(0xc029);
                        return;
                    }
                    _s.Dialog(refusal + "#Ok", _ => _s.Dialog("Abort Customization?#Yes|No", b =>
                    {
                        if (b == 1) { _exitButton.SetFrame(0); return; }
                        InstantAction.LabMech = null;
                        Leave(0xc029);
                    }));
                    return;
                }
                InstantAction.LabMech = null;
                Leave(0xc029);
            });
        }

        /// <summary>Cases 7 / 8: after an accepted FUN_10011ce9 the previous / next mech.</summary>
        static void Cycle(int step, ShellAnim button)
        {
            var c = _s.Career;
            button.SetFrame(1);
            _s.Anims.Step();
            _click.Play();
            Accept(ok =>
            {
                if (ok)
                {
                    _armor?.Free();
                    _engine?.Free();
                    _m.Cycle(step);
                    if (!_summaryUp) ShowSections();
                    else _summary.Refresh();
                    _stats.Refresh();
                    MarkSection(false);
                    _crits.ClearStates();
                    if (!_m.ArmorPanel) _crits.Refresh();
                    BuildItems();
                    UpdatePrice();
                    button.SetFrame(0);
                    _s.Anims.Step();
                    if (c.Mode == 1 && _m.NeedsRepair()) _s.Dialog("Warning: Customizing prevents future repairs.#Ok", null);
                }
            });
        }

        static void Press(ShellAnim button)
        {
            _click.Play();
            button.SetFrame(1);
            _s.Anims.Step();
        }

        /// <summary>Cases 2..6: a tab; the armour or engine panel closes, except on the equipment tab.</summary>
        static void PickTab(int tab)
        {
            _info?.Free();
            _info = null;
            _m.ShowTab(tab);
            for (int i = 0; i < 5; i++) _tabs[i].SetFrame(0);
            _tabs[tab - 2].SetFrame(1);
            _click.Play();
            if (tab != 5)
            {
                if (_m.ArmorPanel)
                {
                    _m.ArmorPanel = false;
                    _stats.Refresh();
                    _armor?.Free();
                    _crits.Init();
                    _crits.Refresh();
                }
                else if (_m.EnginePanel)
                {
                    _m.EnginePanel = false;
                    _engine?.Free();
                }
            }
            BuildItems();
        }

        /// <summary>Cases 0xb..0x12: a section picked.</summary>
        static void PickSection(int section)
        {
            _crits.Free();
            Mech.SetI32(0x364, section);
            MarkSection(true);
            if (!_m.ArmorPanel)
            {
                _crits.ClearStates();
                _crits.Init();
                _crits.Refresh();
            }
            else _armor.Refresh();
        }

        /// <summary>Case 0x13: the summary page (FUN_10018a7c 0x10018a7c) or back (FUN_10018b89 0x10018b89).</summary>
        static void ToggleSummary()
        {
            var c = _s.Career;
            if (!_summaryUp)
            {
                _summaryUp = true;
                _summaryButton.SetFrame(0);
                _s.Surface.LoadBackground(ShellPcx.Decode(_s.Fix.Unpack(c.Mode == 2 ? 4 : 5)));
                for (int i = 0xb; i <= 0x12; i++) _regions.Disable(i);
                foreach (var a in _sections) a.Hide();
                _summary?.Free();
                _summary = Summary();
                _summary.Init();
            }
            else
            {
                _summaryUp = false;
                _summaryButton.SetFrame(1);
                _s.Surface.LoadBackground(ShellPcx.Decode(_s.Fix.Unpack(c.Mode == 2 ? 1 : 2)));
                for (int i = 0xb; i <= 0x12; i++) _regions.Enable(i);
                _summary?.Free();
                ShowSections();
            }
            _s.Anims.Step();
        }

        /// <summary>FUN_10035083 0x10035083 for the name (font 6 at (27, 63), colour 0x11, up to 18 characters narrower
        /// than 197 pixels): Enter or a click keep it, Esc drops it.</summary>
        static void StartRename()
        {
            _name.Data = 0;
            _stats.Refresh();
            _edited = Mech.Str(0x100, 0x3a);
            _editing = true;
            ShowEdit();
        }

        static void ShowEdit()
        {
            _editText?.Hide();
            _editText = Draw(_s.Font6, _edited + "_", 0x1b, 0x3f, _bright);
        }

        static void EditTick()
        {
            var s = _s;
            bool done = false, keep = false;
            foreach (char ch in Input.inputString)
            {
                if (ch == '\b')
                {
                    if (_edited.Length > 0) _edited = _edited.Substring(0, _edited.Length - 1);
                }
                else if (ch == '\n' || ch == '\r') { done = true; keep = true; }
                else if (ch > 31 && ch < 128 && ch != '~' && _edited.Length != 0x12 && s.Font6.Width(_edited + ch + "_") < 0xc5)
                    _edited += ch;
            }
            if (Input.GetKeyDown(KeyCode.Escape)) done = true;
            if (s.MouseClicked) { done = true; keep = true; }
            if (!done)
            {
                ShowEdit();
                return;
            }
            _editing = false;
            _editText?.Hide();
            _editText = null;
            if (keep)
            {
                // FUN_1000739d upper-cases it; +0x994 is set on the mech and its pristine copy.
                string name = _edited.ToUpperInvariant();
                Mech.SetStr(0x100, name);
                Mech.SetI32(0x994, 1);
                var pristine = s.Career.UnitCopies.Find(m => m.MechId == Mech.MechId);
                if (pristine != null)
                {
                    pristine.SetStr(0x100, name);
                    pristine.SetI32(0x994, 1);
                }
            }
            _name.Data = 1;
            _stats.Refresh();
        }

        static void Tick(uint ev)
        {
            var s = _s;
            var c = s.Career;
            if (_editing)
            {
                s.Anims.Step();
                EditTick();
                return;
            }
            if (ev == 0xc004)
            {
                s.Anims.Step();
                bool clicked = s.MouseClicked, twice = s.MouseDouble;
                int x = s.MouseX, y = s.MouseY;
                if (clicked) Clicked(x, y, twice);
                if (_closed) return;
            }
            // +0x23ba (a hot key) or 0xc02e (the menu): accept first; staying when refused.
            if (c.Byte23BA && c.Mode != 2 || ev == 0xc02e)
            {
                c.Byte23BA = false;
                uint target = ev;
                Accept(ok =>
                {
                    if (!ok) _exitButton.SetFrame(0);
                    else if (target != 0xc004) Leave(target);
                });
                return;
            }
            if (ev != 0xc004) Leave(ev);
        }

        static void Clicked(int x, int y, bool twice)
        {
            var s = _s;
            var c = s.Career;
            ShellItemList.Entry hit;
            if (_m.EnginePanel && (hit = _engine.Hit(x, y)) != null)
            {
                hit.Click(hit);
                if (Mech.I32(0x20c) != _m.Backup.I32(0x20c)) Mech.Data[0x9cc] = 1;
                _engine.Refresh();
                Mech.Refit(Tables);
                _stats.Refresh();
                UpdatePrice();
                RefreshSummary();
            }
            if (_m.ArmorPanel && (hit = _armor.Hit(x, y)) != null)
            {
                hit.Click(hit);
                _armor.Refresh();
                _stats.Refresh();
                UpdatePrice();
                MarkSection(false);
                RefreshSummary();
            }
            if (_m.Assigning && _assign != null && (hit = _assign.Hit(x, y)) != null && twice)
                AssignRow(hit);
            if (!_m.Assigning && _items != null && (hit = _items.Hit(x, y)) != null)
            {
                if (!_m.ArmorPanel)
                {
                    _crits.ClearStates();
                    _crits.Refresh();
                }
                hit.Click(hit);
                _items.Refresh();
                RefreshSummary();
                if (twice)
                {
                    AddSelected();
                    _addButton.SetFrame(0);
                    UpdatePrice();
                    RefreshSummary();
                    return;
                }
            }
            if (!_m.ArmorPanel && (hit = _crits.Hit(x, y)) != null)
            {
                _items?.ClearStates();
                _items?.Refresh();
                hit.Click(hit);
                _crits.Refresh();
                RefreshSummary();
                if (twice)
                {
                    RemoveSelected();
                    _removeButton.SetFrame(0);
                    UpdatePrice();
                    RefreshSummary();
                    return;
                }
            }
            int spot = _regions.Hit(x, y);
            switch (spot)
            {
                case 0:
                    if (_m.Assigning) break;
                    _exitButton.SetFrame(1);
                    s.Anims.Step();
                    s.BlockUntilEnd(_exit);
                    if (c.Mode == 2)
                    {
                        LabExit();
                        break;
                    }
                    Accept(ok =>
                    {
                        if (!ok) { _exitButton.SetFrame(0); return; }
                        Leave(c.Mode == 0 ? 0xc036u : 0xc032u);
                    });
                    break;
                case 2: case 3: case 4: case 5: case 6:
                    if (!_m.Assigning) PickTab(spot);
                    break;
                case 7:
                case 8:
                    if (c.Mode != 2 && c.UnitMechs.Count != 1 && !_m.Assigning) Cycle(spot == 7 ? -1 : 1, spot == 7 ? _prevButton : _nextButton);
                    break;
                case 9:
                    Press(_removeButton);
                    RemoveSelected();
                    _removeButton.SetFrame(0);
                    UpdatePrice();
                    RefreshSummary();
                    break;
                case 10:
                    if (_m.ArmorPanel || _m.EnginePanel) break;
                    Press(_addButton);
                    if (_m.Assigning) AssignRow(null);
                    else
                    {
                        AddSelected();
                        UpdatePrice();
                        RefreshSummary();
                    }
                    _addButton.SetFrame(0);
                    break;
                case 0x13:
                    ToggleSummary();
                    break;
                case 0x15:
                    StartRename();
                    break;
                default:
                    if (spot >= 0xb && spot <= 0x12) PickSection(SpotSection[spot - 0xb]);
                    break;
            }
        }

        static void Leave(uint ev)
        {
            if (_closed) return;
            _closed = true;
            var s = _s;
            _regions.Dispose(); _regions = null;
            foreach (var l in new[] { _crits, _items, _info, _stats, _armor, _engine, _summary, _assign }) l?.Free();
            _items = _info = _armor = _engine = _summary = _assign = null;
            _m.Close();
            s.Anims.Clear();
            s.Surface.DrawAfter.Clear(); s.Surface.DrawBefore.Clear();
            _click.Release(); _ambient.Release(); _pay.Release(); _sectionSound.Release(); _select.Release(); _exit.Release();
            s.Post(ev, 0xc02f);
            s.Unregister();
        }
    }
}
