using System.Collections.Generic;
using System.Globalization;

namespace MW2.Shell
{
    /// <summary>
    /// Personnel / hiring 0xc022 (hiring.cpp: FUN_1002bd88 0x1002bd88, screen function FUN_1002ce0e 0x1002ce0e).
    /// Background 32; four item lists in font 11 (colour 1 -> 0x16, selected 0x11): current MechWarriors (x 230, the
    /// player left out) and those for hire (x 445) under y 95, current and hireable AeroTech pilots under y 195
    /// (FUN_1002c50e / FUN_1002c9a6: name 150 wide, salary right-aligned at x + 165). Clicking a name selects it, clears
    /// the other lists, enables Hire (spot 0, for a hireable one) or Fire (spot 1) and types the biography (+100 of a
    /// pilot, +0x5e of a technician) into a page at (35, 96) 144 x 259; a double click on the selected name hires or
    /// fires at once. Hiring gives a new id (Career_NextId) and needs fewer than 8 pilots or no AeroTech pilot yet;
    /// firing a pilot frees his mech (+0x98c). Funds at the top (FUN_1004a4bd / FUN_100456d3), exit (spot 2) to the
    /// office computer 0xc021.
    /// </summary>
    public static class PersonnelScreen
    {
        static readonly ShellRegions.Entry[] Spots =
        {
            new ShellRegions.Entry(472, 380, 589, 421, 0, 0, ""),
            new ShellRegions.Entry(257, 379, 370, 420, 0, 0, ""),
            new ShellRegions.Entry(500, 431, 603, 473, 0, 0, "")
        };
        static Mw2Shell _s;
        static ShellRegions _regions;
        static ShellSound _click, _open, _ambient, _exit;      // DAT_100cd564, 568, 570, 56c
        static ShellAnim _hire, _fire, _exitButton;
        static ShellItemList _pilots, _pilotsForHire, _aero, _aeroForHire, _funds;
        static ShellTextPage _bio;
        static readonly List<ShellText> Headers = new List<ShellText>();
        static bool _buttonsDown;                              // DAT_100cd554
        static byte[] _normal, _selected;

        public static void Open(Mw2Shell s)
        {
            _s = s;
            _normal = ShellSurface.MakeRemap(0x16);
            _selected = ShellSurface.MakeRemap(0x11);
            _bio = null;
            _buttonsDown = false;
            s.Anims.Clear();
            s.Surface.LoadBackground(ShellPcx.Decode(s.Db.Unpack(0x20)));
            _open = Sound(0x4b, 0x32); _open.Play();
            _ambient = Sound(0x3d, 0x1e); _ambient.LoopForever(); _ambient.Play();
            _exit = Sound(0x39, 0x32);
            _click = Sound(0x35, 0x32);
            _hire = new ShellAnim(s.Anims, "ocbhh", 0x1e8, 0x17f, 0x21, 0); _hire.Show();
            _fire = new ShellAnim(s.Anims, "ocbhf", 0x10e, 0x17f, 0x21, 0); _fire.Show();
            _exitButton = new ShellAnim(s.Anims, "ocbhe", 0x1fe, 0x1b8, 0x21, 0); _exitButton.Show();
            _regions = new ShellRegions(s.Surface, s.Font7, false, Spots, () => true);
            _regions.Disable(0);
            _regions.Disable(1);
            Headers.Clear();
            int fh = s.Font11.Height;
            Header("Current Mechwarriors: ", 0x12e, 0x5f);
            Header("Mechwarriors for hire: ", 0x206, 0x5f);
            Header("Current AeroTech Pilots: ", 0x12e, 0xc3);
            Header("AeroTech Pilots for hire: ", 0x206, 0xc3);
            _pilots = PilotList(0xe6, 0x5f + fh, s.Career.UnitPilots, 0);
            _pilotsForHire = PilotList(0x1bd, 0x5f + fh, s.Career.HirePilots, 1);
            _aero = TechList(0xe6, 0xc3 + fh, s.Career.UnitTechs, 0);
            _aeroForHire = TechList(0x1bd, 0xc3 + fh, s.Career.HireTechs, 1);
            _funds = FundsList(s);
            foreach (var l in new[] { _funds, _pilots, _pilotsForHire, _aero, _aeroForHire }) l.Init();
            s.FlushKeys();
            s.Register(Tick);
        }

        static ShellSound Sound(int record, int volume)
        {
            var snd = new ShellSound(_s.gameObject, _s.Db, record);
            snd.SetVolume(volume);
            return snd;
        }

        static ShellText Draw(ShellFont font, string text, int x, int y, byte[] remap)
        {
            var t = new ShellText(_s.Surface, font, text, x, y, remap);
            t.Show();
            return t;
        }

        /// <summary>The headers are centred on x (FUN_100496b0).</summary>
        static void Header(string text, int centre, int y) =>
            Headers.Add(Draw(_s.Font11, text, centre - _s.Font11.Width(text) / 2, y, _normal));

        /// <summary>DAT_100a5008: "Funds:" at (484, 20) and the funds right-aligned at x 624, font 7, outside novice and
        /// instant-action careers.</summary>
        public static ShellItemList FundsList(Mw2Shell s)
        {
            var list = new ShellItemList();
            var remap = ShellSurface.MakeRemap(0x11);
            // Drawn on the caller's shell: the Duty Roster (DutyRosterScreen.Extras) uses this list before this screen
            // has ever been opened, when the static _s is still null.
            ShellText Draw(ShellFont font, string text, int x, int y, byte[] map)
            {
                var t = new ShellText(s.Surface, font, text, x, y, map);
                t.Show();
                return t;
            }
            list.Entries.Add(new ShellItemList.Entry
            {
                X = 484, Y = 20,
                Draw = e => s.Career.Mode == 0 || s.Career.Mode == 2 ? null : Draw(s.Font7, "Funds:", e.X, e.Y, remap)
            });
            list.Entries.Add(new ShellItemList.Entry
            {
                X = 624, Y = 20,
                Draw = e =>
                {
                    if (s.Career.Mode == 0 || s.Career.Mode == 2) return null;
                    string money = Debriefing.Money(s.Career.Funds);
                    return Draw(s.Font7, money, e.X - s.Font7.Width(money), e.Y, remap);
                }
            });
            return list;
        }

        /// <summary>FUN_1002c50e 0x1002c50e: a header entry, then for every pilot but the player a name entry (FUN_1002c8a4)
        /// and a salary entry (+0x3c, FUN_1002c7e8).</summary>
        static ShellItemList PilotList(int x, int y, List<PilotRecord> pilots, int kind)
        {
            var list = new ShellItemList();
            int fh = _s.Font11.Height;
            list.Entries.Add(new ShellItemList.Entry { X = x, Y = y, W = -1, H = fh, Remap = _normal, Data = -1, Data2 = -1, State = -1 });
            int row = 1;
            for (int i = 0; i < pilots.Count; i++)
            {
                if (pilots[i].Player) continue;
                int index = i;
                list.Entries.Add(new ShellItemList.Entry
                {
                    X = x, Y = y + row * fh, W = 0x96, H = fh, Remap = _normal, Data = index, Data2 = kind,
                    Draw = e => Draw(_s.Font11, pilots[e.Data].Name, e.X, e.Y, e.State == 1 ? _selected : e.Remap),
                    Click = e => e.State = 2
                });
                list.Entries.Add(new ShellItemList.Entry
                {
                    X = x + 0xa5, Y = y + row * fh, W = -1, H = fh, Remap = _normal, Data = pilots[i].I32(0x3c), Data2 = -1,
                    Draw = Salary
                });
                row++;
            }
            return list;
        }

        /// <summary>FUN_1002c9a6 0x1002c9a6: the same for technicians (all of them; salary +0x24).</summary>
        static ShellItemList TechList(int x, int y, List<TechRecord> techs, int kind)
        {
            var list = new ShellItemList();
            int fh = _s.Font11.Height;
            list.Entries.Add(new ShellItemList.Entry { X = x, Y = y, W = 100, H = fh, Remap = _normal, Data = -1, Data2 = -1, State = -1 });
            for (int i = 0; i < techs.Count; i++)
            {
                list.Entries.Add(new ShellItemList.Entry
                {
                    X = x, Y = y + (i + 1) * fh, W = 0x96, H = fh, Remap = _normal, Data = i, Data2 = kind,
                    Draw = e => Draw(_s.Font11, techs[e.Data].Name, e.X, e.Y, e.State == 1 ? _selected : e.Remap),
                    Click = e => e.State = 2
                });
                list.Entries.Add(new ShellItemList.Entry
                {
                    X = x + 0xa5, Y = y + (i + 1) * fh, W = -1, H = fh, Remap = _normal, Data = techs[i].I32(0x24),
                    Data2 = kind, Draw = Salary
                });
            }
            return list;
        }

        /// <summary>FUN_1002c7e8 / FUN_1002cd39: the salary right-aligned at the entry's x.</summary>
        static ShellText Salary(ShellItemList.Entry e)
        {
            string money = Debriefing.Money(e.Data);
            return Draw(_s.Font11, money, e.X - _s.Font11.Width(money), e.Y, e.State == 1 ? _selected : e.Remap);
        }

        static void ShowBio(string text)
        {
            _bio?.Hide();
            _bio?.TypeSound?.Release();
            _bio = new ShellTextPage(_s.Font11, _s.Font7, 0x23, 0x60, 0x90, 0x103);
            _bio.Layout(text);
            _bio.Create(_s.Surface, _normal, _selected);
            _bio.TypeSound = new ShellSound(_s.gameObject, _s.Db, 0x32);
            _bio.TypeSound.LoopForever();
            _bio.StartTyping();
        }

        /// <summary>A click on a name: select it, clear the other lists and offer hire (0) or fire (1).</summary>
        static void Select(ShellItemList list, ShellItemList.Entry entry, bool forHire, string bio)
        {
            entry.Click(entry);
            list.Refresh();
            foreach (var other in new[] { _pilots, _pilotsForHire, _aero, _aeroForHire })
                if (other != list) { other.ClearStates(); other.Refresh(); }
            if (forHire) { _regions.Enable(0); _regions.Disable(1); }
            else { _regions.Disable(0); _regions.Enable(1); }
            ShowBio(bio);
        }

        static void Rebuild()
        {
            var c = _s.Career;
            int fh = _s.Font11.Height;
            foreach (var l in new[] { _pilots, _pilotsForHire, _aero, _aeroForHire }) l.Free();
            _pilots = PilotList(0xe6, 0x5f + fh, c.UnitPilots, 0);
            _pilotsForHire = PilotList(0x1bd, 0x5f + fh, c.HirePilots, 1);
            _aero = TechList(0xe6, 0xc3 + fh, c.UnitTechs, 0);
            _aeroForHire = TechList(0x1bd, 0xc3 + fh, c.HireTechs, 1);
            foreach (var l in new[] { _pilots, _pilotsForHire, _aero, _aeroForHire }) l.Init();
        }

        static void Press(ShellAnim button)
        {
            button.SetFrame(1);
            _s.Anims.Step();
            _s.BlockUntilEnd(_click);
            _buttonsDown = true;
        }

        /// <summary>LAB_1002d7f1: hire the selected MechWarrior, else the selected AeroTech pilot.</summary>
        static void Hire()
        {
            var c = _s.Career;
            int pilot = _pilotsForHire.SelectedData();
            if (pilot != -1)
            {
                Press(_hire);
                if (c.HirePilot(pilot)) Rebuild();
                else _s.Dialog("Unable to hire more pilots#Ok", null);
                return;
            }
            int tech = _aeroForHire.SelectedData();
            if (tech == -1) return;
            Press(_hire);
            if (c.HireTech(tech)) Rebuild();
            else _s.Dialog("The dropship can only transport|one Aerotech#Ok", null);
        }

        /// <summary>LAB_1002dc64: fire the selected MechWarrior (his mech loses its pilot), else the AeroTech pilot.</summary>
        static void Fire()
        {
            var c = _s.Career;
            int pilot = _pilots.SelectedData();
            if (pilot != -1)
            {
                Press(_fire);
                c.FirePilot(pilot);
                Rebuild();
                return;
            }
            int tech = _aero.SelectedData();
            if (tech == -1) return;
            Press(_fire);
            c.FireTech(tech);
            Rebuild();
        }

        static void Tick(uint ev)
        {
            var s = _s;
            var c = s.Career;
            if (ev == 0xc004)
            {
                _bio?.Advance();
                if (_buttonsDown)
                {
                    _buttonsDown = false;
                    _hire.SetFrame(0);
                    _fire.SetFrame(0);
                }
                s.Anims.Step();
                bool down = s.MouseClicked;
                bool handled = false;
                if (down)
                {
                    var lists = new (ShellItemList list, bool hire)[] { (_pilots, false), (_pilotsForHire, true), (_aero, false), (_aeroForHire, true) };
                    foreach (var (list, hire) in lists)
                    {
                        var e = list.Hit(s.MouseX, s.MouseY);
                        if (e == null) continue;
                        if (e.State == 1)
                        {
                            if (!s.MouseDouble) continue;
                            e.Click(e);
                            list.Refresh();
                            if (hire) Hire(); else Fire();
                            handled = true;
                            break;
                        }
                        string bio = list == _pilots ? c.UnitPilots[e.Data].Str(100, 0x2000)
                                   : list == _pilotsForHire ? c.HirePilots[e.Data].Str(100, 0x2000)
                                   : list == _aero ? c.UnitTechs[e.Data].Str(0x5e, 0x2000)
                                   : c.HireTechs[e.Data].Str(0x5e, 0x2000);
                        Select(list, e, hire, bio);
                        handled = true;
                        break;
                    }
                }
                if (!handled)
                {
                    int spot = _regions.Hit(s.MouseX, s.MouseY);
                    if (down && spot == 0) Hire();
                    else if (down && spot == 1) Fire();
                    else if (down && spot == 2)
                    {
                        _exitButton.SetFrame(1);
                        s.Anims.Step();
                        s.BlockUntilEnd(_exit);
                        ev = 0xc021;
                    }
                }
            }
            if (ev == 0xc004) return;
            _regions.Dispose(); _regions = null;
            _bio?.Hide(); _bio?.TypeSound?.Release(); _bio = null;
            _funds.Free();
            foreach (var l in new[] { _pilots, _pilotsForHire, _aero, _aeroForHire }) l.Free();
            foreach (var t in Headers) t.Hide();
            Headers.Clear();
            s.Anims.Clear();
            s.Surface.DrawAfter.Clear(); s.Surface.DrawBefore.Clear();
            _click?.Release(); _open?.Release(); _ambient?.Release(); _exit?.Release();
            s.Post(ev, 0xc022);
            s.Unregister();
        }
    }
}
