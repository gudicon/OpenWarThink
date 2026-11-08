namespace MW2.Shell
{
    /// <summary>
    /// Title screen, message 0xc02b (FUN_100737ad 0x100737ad, screen function FUN_10073de5 0x10073de5): background record 40,
    /// buttons smercs (new regular career, Career_New(1) -> 0xc035 landing flic), smechw (novice career, Career_New(0) ->
    /// 0xc01c), smrcnt (multiplayer: the shell starts mercnet.exe and quits, 0xc006 -> 0xc002), sinst (instant action,
    /// Career_New(2) -> 0xc029), soptns (Combat Variables, FUN_10027946 → CombatVariablesScreen). Spots 0..4 of table 0x100bb230 are disabled; 5..9 are the
    /// buttons. The hover line (FUN_100735e0 0x100735e0) is drawn in font 6 at 29, 442 with colour 1 -> 0x11.
    /// </summary>
    public static class TitleScreen
    {
        static readonly ShellRegions.Entry[] Spots =
        {
            new ShellRegions.Entry(415, 20, 624, 95, 0, 0, ""), new ShellRegions.Entry(415, 96, 624, 174, 0, 0, ""),
            new ShellRegions.Entry(415, 175, 624, 253, 0, 0, ""), new ShellRegions.Entry(415, 254, 624, 330, 0, 0, ""),
            new ShellRegions.Entry(415, 331, 624, 401, 0, 0, ""),
            new ShellRegions.Entry(415, 20, 624, 95, 0, 0, ""), new ShellRegions.Entry(415, 96, 624, 174, 0, 0, ""),
            new ShellRegions.Entry(415, 175, 624, 253, 0, 0, ""), new ShellRegions.Entry(415, 254, 624, 330, 0, 0, ""),
            new ShellRegions.Entry(415, 331, 624, 401, 0, 0, "")
        };

        static Mw2Shell _s;
        static ShellAnim _mercs, _mechw, _inst, _optns, _mrcnt;      // DAT_100f9f14, f18, f24, f1c, f20
        static ShellSound _music, _s51, _s52, _s4c, _click, _s36;   // DAT_100f9f0c, f08, f04, f00, ef8, f10
        static ShellRegions _regions;      // DAT_100f9ef4
        static ShellText _hover;           // DAT_100f9efc
        static int _hoverSpot = -1;        // DAT_100bb490
        static bool _firstTick;            // DAT_100bb494
        static readonly byte[] HoverRemap = ShellSurface.MakeRemap(0x11);   // 0x100cceb0

        public static void Open(Mw2Shell s)
        {
            _s = s;
            DebriefScreen.CareerEnded = false;      // Title_Open 0x100737ad: DAT_100caeb8 = 0
            s.Anims.Clear();
            _firstTick = true;
            _mercs = new ShellAnim(s.Anims, "smercs", 0x1b5, 0x28, 0x21, 0); _mercs.Show();
            _mechw = new ShellAnim(s.Anims, "smechw", 0x1b5, 0x77, 0x21, 0); _mechw.Show();
            _inst = new ShellAnim(s.Anims, "sinst", 0x1b5, 0x115, 0x21, 0); _inst.Show();
            _optns = new ShellAnim(s.Anims, "soptns", 0x1b5, 0x160, 0x21, 0); _optns.Show();
            _mrcnt = new ShellAnim(s.Anims, "smrcnt", 0x1b5, 200, 0x21, 0); _mrcnt.Show();
            s.Career.Started = false;
            _music = new ShellSound(s.gameObject, s.Db, 0x31);
            _music.SetVolume(0x1e);
            _music.LoopForever();
            _music.Play();
            _s51 = Sound(0x51); _s52 = Sound(0x52); _s4c = Sound(0x4c); _click = Sound(0x35); _s36 = Sound(0x36);
            _hover = null;
            _hoverSpot = -1;
            _regions = new ShellRegions(s.Surface, s.Font7, false, Spots, () => true);
            for (int i = 0; i < 5; i++) _regions.Disable(i);
            s.Register(Tick);
        }

        static ShellSound Sound(int record)
        {
            var snd = new ShellSound(_s.gameObject, _s.Db, record);
            snd.SetVolume(0x32);
            return snd;
        }

        static void Tick(uint ev)
        {
            var s = _s;
            if (ev == 0xc004)
            {
                if (_firstTick)
                {
                    _firstTick = false;
                    s.Surface.LoadBackground(ShellPcx.Decode(s.Db.Unpack(40)));
                }
                s.TickCounter++;
                s.Anims.Step();
                if (s.MouseX < 10) s.MouseX++;
                int spot = _regions.Hit(s.MouseX, s.MouseY);
                ShowHover(spot);
                switch (spot)
                {
                    case 0: case 5:
                        if (s.MouseClicked)
                        {
                            _click.Play();
                            _mercs.SetFrame(1);
                            s.Anims.Step();
                            s.BlockUntilEnd(_s52);
                            ev = 0xc035;
                            s.Career = Career.New(1, s.Data);
                        }
                        break;
                    case 1: case 6:
                        if (s.MouseClicked)
                        {
                            _click.Play();
                            _mechw.SetFrame(1);
                            s.Anims.Step();
                            s.BlockUntilEnd(_s51);
                            s.Career = Career.New(0, s.Data);
                            ev = 0xc01c;
                        }
                        break;
                    case 2: case 7:
                        if (s.MouseClicked)
                        {
                            _mrcnt.SetFrame(1);
                            s.Anims.Step();
                            s.BlockUntilEnd(_click);
                            ev = 0xc006;
                            _mrcnt.SetFrame(0);
                        }
                        break;
                    case 3: case 8:
                        if (s.MouseClicked)
                        {
                            _click.Play();
                            _inst.SetFrame(1);
                            s.Anims.Step();
                            s.BlockUntilEnd(_s4c);
                            s.Career = Career.New(2, s.Data);
                            ev = 0xc029;
                        }
                        break;
                    case 4: case 9:
                        if (s.MouseClicked)
                        {
                            _click.Play();
                            _optns.SetFrame(1);
                            s.Anims.Step();
                            s.BlockUntilEnd(_s36);
                            _optns.SetFrame(0);
                            CombatVariablesScreen.Open(s);
                        }
                        break;
                }
            }
            if (ev == 0xc004) return;
            s.Srand((uint)s.TickCounter);
            _regions.Dispose();
            _regions = null;
            s.Anims.Clear();
            s.Surface.DrawAfter.Clear();
            s.Surface.DrawBefore.Clear();
            foreach (var snd in new[] { _click, _s4c, _s51, _s52, _music, _s36 }) snd?.Release();
            // 0xc006 (multiplayer): the shell shuts Miles down, runs mercnet.exe and then quits (0xc002).
            if (ev == 0xc006)
            {
                // Title_Tick1007438d..1007439b: _spawnlp mode0 waits for the
                // child before PostMessage(0xc002). Poll in Unity instead of
                // blocking its event thread; sounds and title objects are gone.
                try
                {
                    var child = System.Diagnostics.Process.Start(
                        System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName,
                        "--mw2-net --game-folder \"" + s.GameFolder + "\"");
                    if (child != null) { WaitForMercNet(s, child); return; }
                }
                catch (System.Exception error) { UnityEngine.Debug.LogWarning("MercNet: " + error.Message); }
                ev = 0xc002;
            }
            s.Post(ev, 0xc02b);
            s.Unregister();
        }

        // Title_Tick1007439b ->100743ec: the exit message follows child termination,
        // even for a nonzero child exit. Unity keeps pumping frames while it waits.
        internal static void WaitForMercNet(Mw2Shell shell, System.Diagnostics.Process child)
        {
            shell.Register(ev =>
            {
                if (!child.HasExited) return;
                child.Dispose();
                shell.Post(0xc002, 0xc02b);
                shell.Unregister();
            });
        }

        /// <summary>FUN_100735e0: the line under the buttons for the spot under the mouse.</summary>
        static void ShowHover(int spot)
        {
            if (spot == _hoverSpot) return;
            _hoverSpot = spot;
            if (_hover != null) { _hover.Hide(); _hover = null; }
            string text;
            switch (spot)
            {
                case 0: case 5: text = "Command a Mercenary Army (Economy Enabled)"; break;
                case 1: case 6: text = "Join a Mercenary Army (Economy Disabled)"; break;
                case 2: case 7: text = "Multiplayer Mercenaries"; break;
                case 3: case 8: text = "Join Battle right away"; break;
                case 4: case 9: text = "Game settings and options"; break;
                default: text = ""; break;
            }
            _hover = new ShellText(_s.Surface, _s.Font6, text, 0x1d, 0x1ba, HoverRemap);
            _hover.Show(0);
        }
    }
}
