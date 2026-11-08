namespace MW2.Shell
{
    /// <summary>
    /// Instant action mission settings 0xc031 (open FUN_1005824f 0x1005824f, screen function FUN_100589df 0x100589df).
    /// Background 22; sounds 0x37 (looped, 0x1e), 0x39 exit (0x1e), 0x35 click (0x32); buttons iabexit, difficulty
    /// laaeasy / laamed / laahard (0xb7 / 0xf7 / 0x137, 0x3e), mission type laadest / laadefd / laarecn (0xb7 / 0x110 /
    /// 0x169, 0x62), the loops solarsys (0x58, 0xc1) and ships (0x164, 0xc1). The planet of the generator's parameters
    /// (+0xe, one of the 15 of 0x100b2888; unknown: the first) in font 7 at (0xcd, 0x86), colour 1 → 0x16; the buttons of
    /// the difficulty (+0xe1) and type (+0x1e) shown pressed (FUN_10058945). Spots (table 0x100b83b8, 1 disabled): 0 back
    /// to 0xc029, 3..5 difficulty 1..3, 6..8 Search and Destroy / Defend / Recon, 9 the next planet.
    /// </summary>
    public static class MissionSettingsScreen
    {
        static readonly ShellRegions.Entry[] Spots =
        {
            new ShellRegions.Entry(500, 430, 602, 474, 0, 0, ""), new ShellRegions.Entry(148, 430, 354, 474, 0, 0, ""),
            new ShellRegions.Entry(178, 71, 241, 105, 0, 0, ""), new ShellRegions.Entry(178, 55, 243, 91, 0, 0, ""),
            new ShellRegions.Entry(244, 55, 307, 91, 0, 0, ""), new ShellRegions.Entry(308, 55, 372, 91, 0, 0, ""),
            new ShellRegions.Entry(178, 92, 268, 126, 0, 0, ""), new ShellRegions.Entry(269, 92, 357, 126, 0, 0, ""),
            new ShellRegions.Entry(358, 92, 447, 126, 0, 0, ""), new ShellRegions.Entry(188, 127, 280, 162, 0, 0, "")
        };

        static Mw2Shell _s;
        static ShellRegions _regions;
        static ShellSound _ambient, _exit, _click;
        static ShellAnim _exitButton;
        static readonly ShellAnim[] _difficulty = new ShellAnim[3], _type = new ShellAnim[3];
        static ShellText _planet;
        static int _index;          // DAT_100d8598
        static bool _closed;

        static string Planet(int i) => _s.Data.Tables.Instant.Planets[i].Name; // 0x100b2888 + i x 12

        public static void Open(Mw2Shell s)
        {
            _s = s;
            _closed = false;
            s.Anims.Clear();
            _ambient = Sound(0x37, 0x1e);
            _ambient.LoopForever();
            _ambient.Play();
            _exit = Sound(0x39, 0x1e);
            s.Surface.LoadBackground(ShellPcx.Decode(s.Db.Unpack(22)));
            _click = Sound(0x35, 0x32);
            _exitButton = Button("iabexit", 0x1fe, 0x1b8, 0x21);
            _difficulty[0] = Button("laaeasy", 0xb7, 0x3e, 0x21);
            _difficulty[1] = Button("laamed", 0xf7, 0x3e, 0x21);
            _difficulty[2] = Button("laahard", 0x137, 0x3e, 0x21);
            _type[0] = Button("laadest", 0xb7, 0x62, 0x21);
            _type[1] = Button("laadefd", 0x110, 0x62, 0x21);
            _type[2] = Button("laarecn", 0x169, 0x62, 0x21);
            Button("solarsys", 0x58, 0xc1, 0x28);
            Button("ships", 0x164, 0xc1, 0x28);
            _regions = new ShellRegions(s.Surface, s.Font7, false, Spots, () => true);
            _regions.Disable(1);
            _index = 0;
            string planet = InstantAction.Params.Planet;
            for (int i = 0; i <= 0xe; i++) if (Planet(i) == planet) { _index = i; break; }
            ShowPlanet();
            var p = InstantAction.Params;
            if (p.Difficulty >= 1 && p.Difficulty <= 3) _difficulty[p.Difficulty - 1].SetFrame(1);
            if (p.Type >= 1 && p.Type <= 3) _type[p.Type - 1].SetFrame(1);
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

        static void ShowPlanet()
        {
            _planet?.Hide();
            _planet = new ShellText(_s.Surface, _s.Font7, Planet(_index), 0xcd, 0x86, ShellSurface.MakeRemap(0x16));
            _planet.Show();
        }

        static void Tick(uint ev)
        {
            var s = _s;
            var p = InstantAction.Params;
            if (ev == 0xc004)
            {
                s.Anims.Step();
                int spot = s.MouseClicked ? _regions.Hit(s.MouseX, s.MouseY) : -1;
                switch (spot)
                {
                    case 0:
                        _exitButton.SetFrame(1);
                        s.Anims.Step();
                        s.BlockUntilEnd(_exit);
                        ev = 0xc029;
                        break;
                    case 3: case 4: case 5:
                        for (int i = 0; i < 3; i++) _difficulty[i].SetFrame(i == spot - 3 ? 1 : 0);
                        p.Difficulty = spot - 2;
                        _click.Play();
                        break;
                    case 6: case 7: case 8:
                        for (int i = 0; i < 3; i++) _type[i].SetFrame(i == spot - 6 ? 1 : 0);
                        p.Type = spot - 5;
                        _click.Play();
                        break;
                    case 9:
                        if (++_index > 0xe) _index = 0;
                        p.Planet = Planet(_index);
                        _click.Play();
                        ShowPlanet();
                        break;
                }
            }
            if (ev != 0xc004) Leave(ev);
        }

        static void Leave(uint ev)
        {
            if (_closed) return;
            _closed = true;
            var s = _s;
            _regions.Dispose();
            _regions = null;
            _planet?.Hide();
            _planet = null;
            s.Anims.Clear();
            s.Surface.DrawAfter.Clear(); s.Surface.DrawBefore.Clear();
            _ambient.Release(); _exit.Release(); _click.Release();
            s.Post(ev, 0xc031);
            s.Unregister();
        }
    }
}
