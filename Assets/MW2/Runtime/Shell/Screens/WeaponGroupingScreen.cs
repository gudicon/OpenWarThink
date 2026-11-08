namespace MW2.Shell
{
    /// <summary>
    /// Weapons Grouping 0xc030 (grouping.cpp: open FUN_10029f4e 0x10029f4e, screen function FUN_1002ac31 0x1002ac31).
    /// Background 42 (DAT_100a4bb8), colour 1 → 0x16 / 0x11 / 0x9d. Without mechs: "You have no mechs in inventory!" and
    /// back. The unit's first mech: its name centred on 151 at y 86 in font 6 (FUN_10029db0, cut to 186), its
    /// "a&lt;code&gt;" picture at (71, 170) (FUN_1002a412) and the weapon list (FUN_1002a511): "Weapon:" at (370, 89) and
    /// "Group" right-aligned at 557 — or "No weapons" when the unit's stored weapon list (DAT_100cd134, not the mech's)
    /// is empty — then a row per weapon: "%s:" (200 wide; a click gives it the next group, FUN_1002aa21) and its group
    /// right-aligned at 557. Spots: 0 exit to the drop ship 0xc032, 1 / 2 previous / next mech (with more than one),
    /// 4 all groups cleared ("bwdefl"); spots 3 and 5..9 do nothing.
    /// </summary>
    public static class WeaponGroupingScreen
    {
        static readonly ShellRegions.Entry[] Spots =
        {
            new ShellRegions.Entry(500, 430, 602, 474, 0, 0, ""),
            new ShellRegions.Entry(60, 347, 152, 395, 0, 0, ""),
            new ShellRegions.Entry(153, 347, 247, 395, 0, 0, ""),
            new ShellRegions.Entry(258, 430, 364, 474, 0, 0, ""),
            new ShellRegions.Entry(380, 430, 483, 474, 0, 0, ""),
            new ShellRegions.Entry(286, 135, 306, 155, 0, 0, ""),
            new ShellRegions.Entry(286, 156, 306, 176, 0, 0, ""),
            new ShellRegions.Entry(286, 177, 306, 197, 0, 0, ""),
            new ShellRegions.Entry(286, 198, 306, 218, 0, 0, ""),
            new ShellRegions.Entry(286, 219, 306, 239, 0, 0, "")
        };

        static Mw2Shell _s;
        static ShellRegions _regions;
        static ShellSound _click, _exit, _ambient;          // DAT_100cd4f8, 4e0, 4e4
        static ShellAnim _exitButton, _default, _prev, _next, _picture;
        static ShellItemList _name, _weapons;
        static MechRecord _mech;                            // DAT_100cd4a8
        static int _index;                                  // DAT_100cd4b8
        static bool _buttonsDown;                           // DAT_100cd484
        static byte[] _normal, _selected;

        public static void Open(Mw2Shell s)
        {
            _s = s;
            var c = s.Career;
            _normal = ShellSurface.MakeRemap(0x16);
            _selected = ShellSurface.MakeRemap(0x11);
            _picture = null;
            _buttonsDown = false;
            s.Surface.LoadBackground(ShellPcx.Decode(s.Db.Unpack(0x2a)));
            if (c.UnitMechs.Count == 0)
            {
                s.Dialog("You have no mechs in inventory!#Ok", null);
                s.Post(c.Msg23C3, 0xc030);
                return;
            }
            s.Anims.Clear();
            _click = new ShellSound(s.gameObject, s.Db, 0x35);
            _exitButton = Button("bwexit", 0x1fe, 0x1b8);
            _default = Button("bwdefl", 0x186, 0x1b8);
            _prev = Button("bwprev", 0x47, 0x163);
            _next = Button("bwnext", 0x9f, 0x164);
            _exit = new ShellSound(s.gameObject, s.Db, 0x39);
            _exit.SetVolume(0x32);
            _ambient = new ShellSound(s.gameObject, s.Db, 0x37);
            _ambient.SetVolume(0x32);
            _ambient.LoopForever();
            _ambient.Play();
            _index = 0;
            _mech = c.UnitMechs[0];
            ShowPicture();
            _name = new ShellItemList();
            _name.Entries.Add(new ShellItemList.Entry { X = 0x97, Y = 0x56, Remap = _normal, Draw = DrawName });
            _name.Init();
            _weapons = Weapons();
            _weapons.Init();
            _regions = new ShellRegions(s.Surface, s.Font7, false, Spots, () => true);
            s.Register(Tick);
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

        /// <summary>FUN_1002a412 0x1002a412.</summary>
        static void ShowPicture()
        {
            if (_picture != null) { _picture.Hide(); _picture.Destroy(); }
            _picture = new ShellAnim(_s.Anims, "a" + _mech.Code, 0x47, 0xaa, 0x28, 0);
            _picture.Show();
        }

        /// <summary>FUN_10029db0 0x10029db0.</summary>
        static ShellText DrawName(ShellItemList.Entry e)
        {
            string text = _mech.Str(0x100, 0x3a);
            if (text.Length == 0)
            {
                int i = _s.Data.Tables.FindMech(_mech.MekName);
                text = i >= 0 ? _s.Data.Tables.MechName(i) : "";
            }
            text = SkipFirstWord(text, _s.Font6, 0xba);
            return Draw(_s.Font6, text, e.X - _s.Font6.Width(text) / 2, e.Y, _selected);
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

        /// <summary>FUN_1002a511 0x1002a511.</summary>
        static ShellItemList Weapons()
        {
            var list = new ShellItemList();
            var m = _mech;
            int h = _s.Font11.Height;
            int head = 1;
            if (_s.Career.UnitWeapons.Count == 0)
                list.Entries.Add(new ShellItemList.Entry { X = 0x172, Y = 0x59, Remap = _selected, Data = -1, Draw = e => Draw(_s.Font11, "No weapons", e.X, e.Y, e.Remap) });
            else
            {
                head = 2;
                list.Entries.Add(new ShellItemList.Entry { X = 0x172, Y = 0x59, Remap = _selected, Data = -1, Draw = e => Draw(_s.Font11, "Weapon:", e.X, e.Y, e.Remap) });
                list.Entries.Add(new ShellItemList.Entry
                {
                    X = 0x22d, Y = 0x59, Remap = _selected, Data = -1,
                    Draw = e => Draw(_s.Font11, "Group", e.X - _s.Font11.Width("Group"), e.Y, e.Remap)
                });
            }
            int row = head - 1;
            for (int k = 0; k < 0xe; k++)
            {
                if (m.I32(0x2f0 + k * 8) < 0) continue;
                int y = (head + row) * h + 0x59;
                WeaponGroups.Clamp(m, k);
                int slot = k;
                var group = new ShellItemList.Entry { X = 0x22d, Y = y, Remap = _normal, Data = -1, Data2 = slot };
                group.Draw = e =>
                {
                    string text = WeaponGroups.Group(m, slot).ToString();
                    return Draw(_s.Font11, text, e.X - _s.Font11.Width(text), e.Y, e.State == 1 ? _selected : e.Remap);
                };
                var name = new ShellItemList.Entry
                {
                    X = 0x172, Y = y, W = 200, Remap = _normal, Data = m.I32(0x2f0 + k * 8) / 100, Data2 = slot,
                    Draw = e => Draw(_s.Font11, $"{_s.Data.Tables.WeaponName(e.Data)}:", e.X, e.Y, e.State == 1 ? _selected : e.Remap),
                    Click = e =>
                    {
                        _s.Career.Started = true;
                        e.State = 2;
                        group.State = 2;
                        WeaponGroups.Next(m, slot);
                        _weapons.Refresh();
                    }
                };
                list.Entries.Add(name);
                list.Entries.Add(group);
                row++;
            }
            return list;
        }

        static void Rebuild()
        {
            _weapons.Free();
            _weapons = Weapons();
            _weapons.Init();
        }

        /// <summary>Spots 1 / 2: the previous / next mech (FUN_1002b3a2 / FUN_1002b375).</summary>
        static void Cycle(ShellAnim button, int step)
        {
            var c = _s.Career;
            button.SetFrame(1);
            _s.Anims.Step();
            _buttonsDown = true;
            _click.Play();
            _index += step;
            if (_index >= c.UnitMechs.Count) _index = 0;
            if (_index < 0) _index = c.UnitMechs.Count - 1;
            _mech = c.UnitMechs[_index];
            ShowPicture();
            _name.Refresh();
            Rebuild();
        }

        static void Tick(uint ev)
        {
            var s = _s;
            var c = s.Career;
            if (ev == 0xc004)
            {
                if (_buttonsDown)
                {
                    _buttonsDown = false;
                    _default.SetFrame(0); _prev.SetFrame(0); _next.SetFrame(0);
                }
                s.Anims.Step();
                bool down = s.MouseClicked;
                if (down)
                {
                    var e = _weapons.Hit(s.MouseX, s.MouseY);
                    if (e != null)
                    {
                        e.Click(e);
                        _weapons.Refresh();
                    }
                }
                switch (down ? _regions.Hit(s.MouseX, s.MouseY) : -1)
                {
                    case 0:
                        _exitButton.SetFrame(1);
                        s.Anims.Step();
                        s.BlockUntilEnd(_exit);
                        ev = 0xc032;
                        _buttonsDown = true;
                        break;
                    case 1:
                        if (c.UnitMechs.Count != 1) Cycle(_prev, -1);
                        break;
                    case 2:
                        if (c.UnitMechs.Count != 1) Cycle(_next, 1);
                        break;
                    case 4:
                        _default.SetFrame(1);
                        s.Anims.Step();
                        s.BlockUntilEnd(_click);
                        WeaponGroups.Clear(_mech);
                        c.Started = true;
                        Rebuild();
                        _buttonsDown = true;
                        break;
                }
            }
            if (ev == 0xc004) return;
            _regions.Dispose(); _regions = null;
            _weapons.Free();
            _name.Free();
            s.Anims.Clear();
            s.Surface.DrawAfter.Clear(); s.Surface.DrawBefore.Clear();
            _exit.Release(); _click.Release(); _ambient.Release();
            s.Post(ev, 0xc030);
            s.Unregister();
        }
    }
}
