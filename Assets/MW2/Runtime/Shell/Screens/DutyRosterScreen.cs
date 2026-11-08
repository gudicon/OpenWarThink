using System.Collections.Generic;
using System.Linq;

namespace MW2.Shell
{
    /// <summary>
    /// Duty roster 0xc025 (duty.cpp: FUN_100251f4 0x100251f4, screen function FUN_10025798 0x10025798). Background 35,
    /// "MechWarrior Assignment" centred on x 210 in font 7, then one row per pilot (FUN_100247f0: the name at x 37,
    /// "piloting %s" / "no mech assigned" at x 150, FUN_100245e7); clicking a row selects the pilot and shows his mech's
    /// "a&lt;code&gt;" animation at (440, 114) (FUN_10024428, FUN_100244bf). Previous / next (spots 2 / 3, FUN_10024efb /
    /// FUN_10024da9) give the selected pilot the previous / next free mech, passing an empty "no mech" entry (the
    /// placeholder record DAT_100cb510 added to the unit's mechs while the screen is open). With a drop ship of three or
    /// more places the AeroTech pilot can be assigned (FUN_1002426a / FUN_1002439f). Exit (spot 0) needs the player's mech
    /// and at most the drop ship's crew (FUN_1002504d), then writes the launch MEKs (FUN_10024a97) and goes to the drop
    /// ship 0xc036.
    /// </summary>
    public static class DutyRosterScreen
    {
        static readonly ShellRegions.Entry[] Spots =
        {
            new ShellRegions.Entry(500, 430, 602, 475, 0, 0, ""),
            new ShellRegions.Entry(394, 430, 499, 475, 0, 0, ""),
            new ShellRegions.Entry(427, 289, 523, 340, 0, 0, ""),
            new ShellRegions.Entry(524, 289, 615, 340, 0, 0, "")
        };
        static Mw2Shell _s;
        static ShellRegions _regions;
        static ShellSound _select, _cycle, _ambient, _exit;     // DAT_100cb528, 52c, 544, 548
        static ShellAnim _cash, _exitButton, _prev, _next, _picture;
        static ShellItemList _roster, _extras;
        static byte[] _normal, _selected;

        public static void Open(Mw2Shell s, uint previous)
        {
            _s = s;
            var c = s.Career;
            _normal = ShellSurface.MakeRemap(0x16);
            _selected = ShellSurface.MakeRemap(0x11);
            _picture = null;
            s.Anims.Clear();
            s.Surface.LoadBackground(ShellPcx.Decode(s.Db.Unpack(0x23)));
            _select = Sound(0x34, 0x14);
            _cycle = Sound(0x35, 0x14);
            _ambient = Sound(0x60, 0x1e); _ambient.LoopForever(); _ambient.Play();
            _exit = Sound(0x39, 0x1e);
            if (c.UnitMechs.Count == 0)
            {
                s.Dialog("There are no mechs to assign!#Ok", null);
                Release();
                s.Post(c.Msg23C3, 0xc025);
                return;
            }
            _regions = new ShellRegions(s.Surface, s.Font7, false, Spots, () => true);
            _cash = new ShellAnim(s.Anims, "cashboxd", 0x1d6, 0, 0x21, 0);
            if (c.Mode == 1) _cash.Show();
            _exitButton = new ShellAnim(s.Anims, "bdrexit", 0x1fd, 0x1b8, 0x21, 0); _exitButton.Show();
            _prev = new ShellAnim(s.Anims, "bdrprev", 0x1b7, 0x12a, 0x21, 0); _prev.Show();
            _next = new ShellAnim(s.Anims, "bdrnext", 0x20f, 299, 0x21, 0); _next.Show();
            _roster = Roster(0x25, 0x59);
            _roster.Init();
            // FUN_100251f4: the first pilot's mech row starts selected and its picture is shown.
            if (_roster.Entries.Count > 2)
            {
                _roster.Entries[2].State = 1;
                ClickMech(_roster.Entries[2]);
            }
            _roster.Refresh();
            _extras = Extras();
            _extras.Init();
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

        /// <summary>FUN_100247f0 0x100247f0.</summary>
        static ShellItemList Roster(int x, int y)
        {
            var s = _s;
            var list = new ShellItemList();
            int f7 = s.Font7.Height, f11 = s.Font11.Height;
            const string title = "MechWarrior Assignment";
            list.Entries.Add(new ShellItemList.Entry
            {
                X = 0xd2 - s.Font7.Width(title) / 2, Y = y, H = f7 + 5, Remap = _normal, Data = -1, Data2 = -1, State = -1,
                Draw = e => Draw(s.Font7, title, e.X, e.Y, e.Remap)
            });
            for (int i = 0; i < s.Career.UnitPilots.Count; i++)
            {
                int row = y + f7 + (i + 1) * f11;
                list.Entries.Add(new ShellItemList.Entry
                {
                    X = x, Y = row, W = 0, H = f11 + 5, Remap = _normal, Data = i, Data2 = -1, State = -1,
                    Draw = e => Draw(s.Font11, s.Career.UnitPilots[e.Data].Name, e.X, e.Y, e.Remap)
                });
                list.Entries.Add(new ShellItemList.Entry
                {
                    X = 0x96, Y = row, W = 0xe6, H = f11 + 5, Remap = _normal, Data = i, Data2 = 0, State = -1,
                    Draw = DrawMech, Click = ClickMech
                });
            }
            return list;
        }

        /// <summary>FUN_100245e7 0x100245e7.</summary>
        static ShellText DrawMech(ShellItemList.Entry e)
        {
            var c = _s.Career;
            var pilot = c.UnitPilots[e.Data];
            var mech = c.UnitMechs.Find(m => m.PilotId == pilot.Id);
            string text;
            if (mech == null) text = "no mech assigned";
            else if (mech.Str(0x100, 0x3a).Length == 0)
            {
                int i = _s.Data.Tables.FindMech(mech.MekName);
                text = $"piloting {(i >= 0 ? _s.Data.Tables.MechName(i) : "")}";
            }
            else text = $"piloting {mech.Str(0x100, 0x3a)}";
            return Draw(_s.Font11, text, e.X, e.Y, e.State == 1 ? _selected : e.Remap);
        }

        /// <summary>FUN_10024428 0x10024428: select the row and show the pilot's mech.</summary>
        static void ClickMech(ShellItemList.Entry e)
        {
            if (e.Data < 0) return;
            _select.Play();
            var c = _s.Career;
            var mech = c.UnitMechs.Find(m => m.PilotId == c.UnitPilots[e.Data].Id);
            ShowPicture(mech != null ? mech.Code : "");
            e.State = 2;
        }

        /// <summary>FUN_100244bf 0x100244bf: the "a&lt;code&gt;" animation at (440, 114); an empty code hides it.</summary>
        static void ShowPicture(string code)
        {
            if (code.Length == 0)
            {
                _picture?.Hide();
                return;
            }
            if (_picture != null) { _picture.Hide(); _picture.Destroy(); }
            _picture = new ShellAnim(_s.Anims, "a" + code, 0x1b8, 0x72, 0x68, 0);
            _picture.Show();
        }

        /// <summary>DAT_100a3120: the funds line, "Aerotech:" at (37, 314) and the AeroTech pilot two rows below it.</summary>
        static ShellItemList Extras()
        {
            var s = _s;
            var c = s.Career;
            var list = PersonnelScreen.FundsList(s);
            var label = new ShellItemList.Entry
            {
                X = 37, Y = 314, Remap = _normal,
                Draw = e => c.DropShipSlots < 3 || c.UnitTechs.Count < 1 ? null : Draw(s.Font11, "Aerotech:", e.X, e.Y, e.Remap)
            };
            list.Entries.Add(label);
            var tech = new ShellItemList.Entry { X = 37, Remap = _normal, Data = 0, Draw = DrawTech, Click = ClickTech };
            list.Entries.Add(tech);
            // Y 0x80000022: below the previous entry, two of its heights and 2 pixels (FUN_1004b400).
            label.H = s.Font11.Height;
            tech.Y = label.Y + label.H * 2 + 2;
            return list;
        }

        /// <summary>FUN_1002426a 0x1002426a: with fewer than three places the AeroTech pilot is unassigned and not shown.</summary>
        static ShellText DrawTech(ShellItemList.Entry e)
        {
            var c = _s.Career;
            if (c.DropShipSlots < 3)
            {
                if (c.UnitTechs.Count > 0) c.UnitTechs[e.Data].Data[0x2c] = 0;
                return null;
            }
            if (c.UnitTechs.Count < 1) return null;
            var t = c.UnitTechs[e.Data];
            string text = $"{t.Name.ToUpperInvariant()} {(t.Data[0x2c] == 1 ? "assigned" : "not assigned")}";
            return Draw(_s.Font11, text, e.X, e.Y, e.State == 1 ? _selected : e.Remap);
        }

        /// <summary>FUN_1002439f 0x1002439f: toggle the AeroTech pilot's assignment (+0x2c).</summary>
        static void ClickTech(ShellItemList.Entry e)
        {
            var c = _s.Career;
            if (c.DropShipSlots <= 2) return;
            c.Started = true;
            if (c.UnitTechs.Count > 0)
            {
                var t = c.UnitTechs[e.Data];
                if (t.Data[0x2c] == 1) { e.State = 0; t.Data[0x2c] = 0; }
                else { e.State = 2; t.Data[0x2c] = 1; }
            }
            _select.Play();
        }

        /// <summary>FUN_10024da9 / FUN_10024efb: the next (+1) or previous (-1) free mech for the pilot.</summary>
        static void Cycle(PilotRecord pilot, int step)
        {
            if (!_s.Career.CycleMech(pilot, step, out var chosen))
            {
                _s.Dialog("There are no more mechs in inventory#Ok", null);
                return;
            }
            ShowPicture(chosen != null ? chosen.Code : "");
        }

        /// <summary>FUN_1002504d with its warning: at most two wingmen.</summary>
        static int Crew()
        {
            var c = _s.Career;
            int wingmen = c.UnitPilots.Count(p => p.MechId != -1 && !p.Player);
            if (wingmen >= 3) _s.Dialog("You may assign a max of 2 starmates!", null);
            return c.CrewCountForLaunch();
        }

        /// <summary>The checks before leaving: the player has a mech and the crew fits the drop ship.</summary>
        static bool MayLeave(bool sound)
        {
            var c = _s.Career;
            if (!c.PlayerHasMech())
            {
                if (sound) _exit.Play();
                _s.Dialog("You must assign yourself a mech!#Ok", null);
                return false;
            }
            if (c.DropShipSlots < Crew())
            {
                if (sound) _exit.Play();
                _s.Dialog($"You can assign only {c.DropShipSlots} starmates|for this mission#Ok", null);
                return false;
            }
            return true;
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
                    var e = _roster.Hit(s.MouseX, s.MouseY);
                    if (e != null) { e.Click(e); _roster.Refresh(); }
                    e = _extras.Hit(s.MouseX, s.MouseY);
                    if (e != null) { e.Click(e); _extras.Refresh(); }
                }
                int spot = _regions.Hit(s.MouseX, s.MouseY);
                if (spot == 0 && down)
                {
                    _exitButton.SetFrame(1);
                    s.Anims.Step();
                    if (MayLeave(true)) { s.BlockUntilEnd(_exit); ev = 0xc036; }
                    else _exitButton.SetFrame(0);
                }
                else if ((spot == 2 || spot == 3) && down)
                {
                    _cycle.Play();
                    var button = spot == 2 ? _prev : _next;
                    button.SetFrame(1);
                    s.Anims.Step();
                    int selected = _roster.SelectedData();
                    if (selected == -1) s.Dialog("Select pilot first#Ok", null);
                    else Cycle(c.UnitPilots[selected], spot == 2 ? -1 : 1);
                    _roster.Refresh();
                    button.SetFrame(0);
                }
            }
            // +0x23ba: a hot key wants to leave; it may only when the same checks pass.
            if (c.Byte23BA)
            {
                if (!MayLeave(false)) ev = 0xc004;
                c.Byte23BA = false;
            }
            if (ev == 0xc004) return;
            _regions.Dispose(); _regions = null;
            _extras.Free();
            _roster.Free();
            s.Anims.Clear();
            s.Surface.DrawAfter.Clear(); s.Surface.DrawBefore.Clear();
            Release();
            c.BuildLaunchRoster(s.Data);      // FUN_10024a97: the launch MEKs
            s.Post(ev, 0xc025);
            s.Unregister();
        }

        static void Release()
        {
            _select?.Release(); _cycle?.Release(); _ambient?.Release(); _exit?.Release();
        }
    }
}
