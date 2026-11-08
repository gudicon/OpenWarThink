using System;
using System.Collections.Generic;
using MW2.Formats;

namespace MW2.Shell
{
    /// <summary>Salvage 0xc039 (open FUN_10069bc0 0x10069bc0, messages FUN_10065e2f 0x10065e2f):
    /// the recovered chassis list, category inventory and individual critical transfer.</summary>
    public static class SalvageScreen
    {
        static readonly ShellRegions.Entry[] Spots =
        {
            new ShellRegions.Entry(502,432,604,473,0,0,""), new ShellRegions.Entry(399,432,501,473,0,0,""),
            new ShellRegions.Entry(8,8,100,44,0,0,""), new ShellRegions.Entry(101,8,190,44,0,0,""),
            new ShellRegions.Entry(191,8,280,44,0,0,""), new ShellRegions.Entry(281,8,369,44,0,0,""),
            new ShellRegions.Entry(373,8,460,44,0,0,""), new ShellRegions.Entry(462,8,520,44,0,0,""),
            new ShellRegions.Entry(281,378,386,418,0,0,""), new ShellRegions.Entry(485,378,590,418,0,0,""),
            new ShellRegions.Entry(104,122,135,157,0,0,""), new ShellRegions.Entry(164,121,218,228,0,0,""),
            new ShellRegions.Entry(18,121,67,228,0,0,""), new ShellRegions.Entry(99,156,140,229,0,0,""),
            new ShellRegions.Entry(135,101,163,156,0,0,""), new ShellRegions.Entry(74,101,104,156,0,0,""),
            new ShellRegions.Entry(143,228,231,355,0,0,""), new ShellRegions.Entry(7,228,107,354,0,0,""),
            new ShellRegions.Entry(25,409,170,471,0,0,"")
        };
        static readonly string[] SectionNames = { "head", "rtor", "ctor", "ltor", "rarm", "larm", "rleg", "lleg" };
        static readonly int[] SectionX = { 105, 75, 88, 120, 26, 154, 11, 129 };
        static readonly int[] SectionY = { 122, 108, 154, 107, 126, 126, 173, 173 };
        static readonly string[] TabNames = { "bcmiss", "bcenrg", "bcproj", "bcequp", "bcammo", "chss" };
        static readonly int[] TabX = { 15, 105, 194, 284, 373, 462 };
        static readonly byte[] Normal = ShellSurface.MakeRemap(0x16), Selected = ShellSurface.MakeRemap(0x11);

        static Mw2Shell _s;
        static ShellRegions _regions;
        static ShellItemList _list, _critList, _stockList;
        static ShellText _heading;
        static ShellAnim _exit, _recover, _take, _leaveItem, _arrow;
        static readonly List<SalvageRecovery.Candidate> Candidates = new List<SalvageRecovery.Candidate>();
        static readonly List<SalvageRecovery.Candidate> Chosen = new List<SalvageRecovery.Candidate>();
        static ShellAnim[] _sections;
        static int _selected;
        static int _section, _slot, _tab, _stockIndex;
        static int _discardIndex;
        static bool _viewDiscarded;
        static readonly List<ItemRecord> Weapons = new List<ItemRecord>(), Ammo = new List<ItemRecord>();
        static readonly List<ItemRecord> Discarded = new List<ItemRecord>();
        static bool _closed;

        /// <summary>FUN_10069bc0 0x10069bc0: read salvage.mw2, load background 47 and create the chassis controls.</summary>
        public static void Open(Mw2Shell s)
        {
            _s = s;
            _closed = false;
            s.SalvageOpened = true;
            Candidates.Clear(); Chosen.Clear();
            Weapons.Clear(); Weapons.AddRange(s.Career.UnitWeapons);
            Ammo.Clear(); Ammo.AddRange(s.Career.UnitAmmo);
            Discarded.Clear();
            _selected = _section = _tab = 0; _slot = _stockIndex = _discardIndex = -1;
            _viewDiscarded = false;
            SalvageFile file = null;
            if (s.SalvageFile != null)
            {
                try { file = SalvageFile.Read(s.SalvageFile); }
                catch (Exception) { s.Dialog("No salvage !!!!!|#Ok", _ => s.Post(0xc00a, 0xc039)); return; }
            }
            if (file != null)
                for (int i = 0; i < file.Wrecks.Count; i++)
                {
                    var c = SalvageRecovery.Prepare(file.Wrecks[i], s.Data, file.Wrecks[i].Rolls[Math.Min(i, 95)]);
                    if (c != null) Candidates.Add(c);
                }
            string mission = s.Career.Mission;
            if (mission.Length >= 4 && mission.StartsWith("out", StringComparison.OrdinalIgnoreCase) && mission[3] == '3')
            {
                var kodiak = SalvageRecovery.PrepareKodiak(s.Data);
                if (kodiak != null) Candidates.Add(kodiak);
            }
            if (Candidates.Count == 0) { s.Dialog("No salvage !!!!!|#Ok", _ => s.Post(0xc00a, 0xc039)); return; }
            s.Surface.LoadBackground(ShellPcx.Decode(s.Db.Unpack(47)));
            s.Anims.Clear();
            _exit = Button("bcexit", 0x1fd, 0x1b8);
            _recover = Button("rchass", 0x19, 0x199);
            _take = Button("take", 0x129, 0x17f);
            _leaveItem = Button("leave", 0x1f7, 0x17f);
            _arrow = Button("arrow", 200, 60);
            for (int i = 0; i < TabNames.Length; i++) Button(TabNames[i], TabX[i], 12);
            _sections = new ShellAnim[8];
            for (int i = 0; i < 8; i++) _sections[i] = Button(SectionNames[i], SectionX[i], SectionY[i]);
            _regions = new ShellRegions(s.Surface, s.Font7, false, Spots, () => true);
            ShowCandidate();
            ShowStock();
            s.Register(Tick);
        }

        /// <summary>FUN_10069bc0 0x10069bc0: create and show an SHP animation control.</summary>
        static ShellAnim Button(string name, int x, int y)
        {
            var a = new ShellAnim(_s.Anims, name, x, y, 0x21, 0);
            a.Show();
            return a;
        }

        /// <summary>FUN_10069105 0x10069105: selected chassis name and eight section damage pictures.</summary>
        static void ShowCandidate()
        {
            _heading?.Hide();
            _list?.Free();
            _list = null;
            _critList?.Free(); _critList = null;
            if (Candidates.Count == 0) { _heading = null; _recover.Hide(); return; }
            if (_selected >= Candidates.Count) _selected = Candidates.Count - 1;
            var chosen = Candidates[_selected];
            string title = chosen.Current.Str(0, 0x100);
            _heading = new ShellText(_s.Surface, _s.Font11, title, 18, 63, Normal);
            _heading.Show();
            if (chosen.CanRecover) _recover.Show(); else _recover.Hide();
            for (int i = 0; i < 8; i++)
            {
                int state = chosen.Current.SectionState(i);
                if (state == 3) _sections[i].Hide();
                else { _sections[i].SetFrame(state + (i == 0 ? 3 : 0)); _sections[i].Show(); }
            }
            ShowCrits();
        }

        /// <summary>FUN_10061f3a 0x10061f3a: the mech heading opens the wreck chooser.</summary>
        static void ShowCandidates()
        {
            _list?.Free();
            _list = new ShellItemList();
            for (int i = 0; i < Candidates.Count; i++)
            {
                int index = i;
                _list.Entries.Add(new ShellItemList.Entry
                {
                    X = 18, Y = 90 + i * 12, W = 197, H = 12, Data = i, Remap = Normal,
                    Draw = e =>
                    {
                        var c = Candidates[index];
                        var t = new ShellText(_s.Surface, _s.Font11, (index == _selected ? "[*] " : "[ ] ") +
                            c.Current.Str(0, 0x100), e.X, e.Y, index == _selected ? Selected : Normal);
                        t.Show(); return t;
                    },
                    Click = e => { _selected = index; _slot = -1; ShowCandidate(); }
                });
            }
            _list.Init();
        }

        /// <summary>FUN_100630ed 0x100630ed: show the selected section's six or twelve critical slots.</summary>
        static void ShowCrits()
        {
            _critList?.Free();
            _critList = new ShellItemList();
            if (_viewDiscarded)
            {
                int row = 0;
                for (int i = 0; i < Discarded.Count && row < 10; i++)
                {
                    var discarded = Discarded[i];
                    int kind = discarded.Kind == 1 ? 4 : discarded.Kind == 2 ? 3 :
                        SalvageRecovery.CriticalKind(discarded.Index * 100);
                    if (kind != _tab) continue;
                    int index = i;
                    _critList.Entries.Add(new ShellItemList.Entry
                    {
                        X = 256, Y = 110 + row * 12, W = 120, H = 12, Data = i,
                        Draw = e =>
                        {
                            var item = Discarded[index];
                            string name = item.Kind == 2 ? _s.Data.Tables.EquipmentName(item.Index) :
                                _s.Data.Tables.WeaponName(item.Index) + (item.Kind == 1 ? " Ammo" : "");
                            var t = new ShellText(_s.Surface, _s.Font11,
                                (index == _discardIndex ? "> " : "  ") + name, e.X, e.Y,
                                index == _discardIndex ? Selected : Normal);
                            t.Show(); return t;
                        },
                        Click = e => { _discardIndex = index; ShowCrits(); }
                    });
                    row++;
                }
                _critList.Init();
                return;
            }
            if (Candidates.Count == 0) return;
            var c = Candidates[_selected];
            int limit = _section == 0 || _section >= 6 ? 6 : 12;
            for (int slot = 0; slot < limit; slot++)
            {
                int index = slot, id = c.Current.Crit(_section, slot);
                _critList.Entries.Add(new ShellItemList.Entry
                {
                    X = 256, Y = 110 + slot * 12, W = 120, H = 12, Data = slot,
                    Draw = e =>
                    {
                        string name = CriticalName(id);
                        var t = new ShellText(_s.Surface, _s.Font11,
                            (index == _slot ? "> " : "  ") + name, e.X, e.Y,
                            index == _slot ? Selected : Normal);
                        t.Show(); return t;
                    },
                    Click = e => { _slot = index; ShowCrits(); }
                });
            }
            _critList.Init();
        }

        /// <summary>FUN_100637de 0x100637de: names of weapon, ammunition and equipment criticals.</summary>
        static string CriticalName(int id)
        {
            if (id <= 0) return "";
            if (id >= 10000) return _s.Data.Tables.WeaponName((id - 10000) / 100) + " Ammo";
            if (id < 7000) return _s.Data.Tables.WeaponName(id / 100);
            if (id / 100 == 80) return "Heat Sink";
            if (id / 100 == 85) return "Jump Jet";
            return "Critical " + id;
        }

        /// <summary>FUN_100647cf 0x100647cf: group the provisional hold by the selected category.</summary>
        static void ShowStock()
        {
            _stockList?.Free();
            _stockList = new ShellItemList();
            if (_tab == 5) return;
            var rows = new List<InventoryCategories.Group>();
            foreach (var item in _tab == 4 ? Ammo : Weapons)
            {
                int kind = item.Kind == 1 ? 4 : item.Kind == 2 ? 3 : SalvageRecovery.CriticalKind(item.Index * 100);
                int category = kind;
                if (category != _tab) continue;
                var group = rows.Find(g => g.Index == item.Index);
                if (group == null) { group = new InventoryCategories.Group { Index = item.Index }; rows.Add(group); }
                group.Total++;
            }
            rows.Sort((a, b) => a.Index.CompareTo(b.Index));
            for (int i = 0; i < rows.Count && i < 10; i++)
            {
                int index = rows[i].Index, count = rows[i].Total;
                _stockList.Entries.Add(new ShellItemList.Entry
                {
                    X = 459, Y = 110 + i * 12, W = 120, H = 12, Data = index,
                    Draw = e =>
                    {
                        string name = _tab == 3 ? _s.Data.Tables.EquipmentName(index) : _s.Data.Tables.WeaponName(index);
                        var t = new ShellText(_s.Surface, _s.Font11, name + " x" + count,
                            e.X, e.Y, index == _stockIndex ? Selected : Normal);
                        t.Show(); return t;
                    },
                    Click = e => { _stockIndex = index; ShowStock(); }
                });
            }
            _stockList.Init();
        }

        /// <summary>FUN_10065e2f 0x10065e2f: select a wreck, recover its chassis, or return to the debrief.</summary>
        static void Tick(uint ev)
        {
            var s = _s;
            if (ev != 0xc004) { Leave(ev); return; }
            s.Anims.Step();
            if (!s.MouseClicked) return;
            var row = _list?.Hit(s.MouseX, s.MouseY);
            if (row != null) { row.Click(row); return; }
            row = _critList?.Hit(s.MouseX, s.MouseY);
            if (row != null) { row.Click(row); return; }
            row = _stockList?.Hit(s.MouseX, s.MouseY);
            if (row != null) { row.Click(row); return; }
            if (s.MouseX >= 200 && s.MouseX < 232 && s.MouseY >= 60 && s.MouseY < 86)
            { _viewDiscarded = !_viewDiscarded; _slot = _discardIndex = -1; ShowCrits(); return; }
            if (s.MouseX >= 18 && s.MouseX < 215 && s.MouseY >= 63 && s.MouseY < 89)
            { ShowCandidates(); return; }
            int spot = _regions.Hit(s.MouseX, s.MouseY);
            if (spot == 0) { _exit.SetFrame(1); s.Anims.Step(); ConfirmLeave(); }
            else if (spot >= 2 && spot <= 7)
            { _tab = spot - 2; _stockIndex = _discardIndex = -1; ShowStock(); ShowCrits(); }
            else if (spot == 8) TakeItem();
            else if (spot == 9) LeaveItem();
            else if (spot >= 10 && spot <= 17)
            { _section = spot - 10; _slot = -1; _viewDiscarded = false; ShowCrits(); }
            else if (spot == 18) TakeChassis();
        }

        /// <summary>FUN_100652b8 0x100652b8: take the selected section critical into the provisional hold.</summary>
        static void TakeItem()
        {
            if (_viewDiscarded)
            {
                if (_discardIndex < 0 || _discardIndex >= Discarded.Count) return;
                var restored = Discarded[_discardIndex];
                Discarded.RemoveAt(_discardIndex);
                (_tab == 4 ? Ammo : Weapons).Add(restored);
                _discardIndex = -1;
                ShowCrits(); ShowStock();
                return;
            }
            if (Candidates.Count == 0 || _slot < 0 || _tab == 5) return;
            var c = Candidates[_selected];
            int id = c.Current.Crit(_section, _slot);
            int kind = SalvageRecovery.CriticalKind(id);
            int category = kind;
            if (category != _tab) return;
            var item = SalvageRecovery.TakeCritical(c, _section, _slot, Weapons, Ammo, _s.Data.Tables, out string error);
            if (error != null) { _s.Dialog(error + "|#Ok", null); return; }
            if (item == null) return;
            _slot = -1;
            ShowCrits(); ShowStock();
        }

        /// <summary>FUN_1006581f 0x1006581f: move one stock item to the provisional discarded list,
        /// preferring a damaged record of the selected kind and index.</summary>
        static void LeaveItem()
        {
            if (_tab == 5 || _stockIndex < 0) return;
            var source = _tab == 4 ? Ammo : Weapons;
            ItemRecord selected = null;
            foreach (var item in source)
            {
                int kind = item.Kind == 1 ? 4 : item.Kind == 2 ? 3 : SalvageRecovery.CriticalKind(item.Index * 100);
                if (item.Index != _stockIndex || kind != _tab) continue;
                if (selected == null || item.Field16 != 0) selected = item;
                if (item.Field16 != 0) break;
            }
            if (selected == null) return;
            source.Remove(selected);
            Discarded.Add(selected);
            _viewDiscarded = true; _discardIndex = -1; _stockIndex = -1;
            ShowCrits(); ShowStock();
        }

        /// <summary>FUN_10065c2e 0x10065c2e: move a recoverable chassis to the provisional unit list, at most 12.</summary>
        static void TakeChassis()
        {
            var s = _s;
            if (Candidates.Count == 0 || !Candidates[_selected].CanRecover) return;
            if (s.Career.UnitMechs.Count + Chosen.Count >= 12)
            {
                s.Dialog("Cannot store any more 'mechs!|#Ok", null);
                return;
            }
            Chosen.Add(Candidates[_selected]);
            Candidates.RemoveAt(_selected);
            ShowCandidate();
        }

        /// <summary>FUN_10065e2f 0x10065e2f: accepting salvage commits selections and removes salvage.mw2.</summary>
        static void ConfirmLeave()
        {
            _s.Dialog("Accept Salvage ?|#Yes|No", answer =>
            {
                if (answer != 0) { _exit.SetFrame(0); return; }
                CommitChassis();
                _s.Career.UnitWeapons.Clear(); _s.Career.UnitWeapons.AddRange(Weapons);
                _s.Career.UnitAmmo.Clear(); _s.Career.UnitAmmo.AddRange(Ammo);
                Leave(0xc00a);
            });
        }

        /// <summary>FUN_10069597 0x10069597: recovered mechs get the first free unit number, an ID and an unassigned pilot.</summary>
        static void CommitChassis()
        {
            var c = _s.Career;
            foreach (var selected in Chosen) SalvageRecovery.Commit(selected, c, _s.Data.Tables);
            Chosen.Clear();
            _s.SalvageFile = null;
        }

        /// <summary>FUN_10065e2f 0x10065e2f: remove the screen objects and post the destination.</summary>
        static void Leave(uint ev)
        {
            if (_closed) return;
            _closed = true;
            _regions?.Dispose(); _regions = null;
            _list?.Free(); _list = null;
            _critList?.Free(); _critList = null;
            _stockList?.Free(); _stockList = null;
            _heading?.Hide(); _heading = null;
            _s.Anims.Clear();
            _s.Surface.DrawAfter.Clear(); _s.Surface.DrawBefore.Clear();
            _s.Post(ev, 0xc039);
            _s.Unregister();
        }
    }
}
