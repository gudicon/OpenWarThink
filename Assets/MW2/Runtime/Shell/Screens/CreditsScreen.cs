namespace MW2.Shell
{
    /// <summary>Mech Bay credits map 0xc018 (MW2SHELL.DLL FUN_100264c5 0x100264c5,
    /// FUN_100266c1 0x100266c1): sixteen biographies and an exit to Mech Bay.</summary>
    public static class CreditsScreen
    {
        static readonly ShellRegions.Entry[] Spots =
        {
            new ShellRegions.Entry(50, 300, 70, 320, 274, 125, "DUSTIN BROWDER"),
            new ShellRegions.Entry(110, 270, 130, 290, 274, 125, "DAN KEGEL"),
            new ShellRegions.Entry(155, 267, 170, 280, 274, 125, "SMITH SURASMITH"),
            new ShellRegions.Entry(175, 260, 190, 275, 274, 125, "MATT CANDLER"),
            new ShellRegions.Entry(195, 250, 206, 260, 274, 125, "HENRY YU"),
            new ShellRegions.Entry(212, 235, 225, 250, 274, 125, "KEN HULLETT"),
            new ShellRegions.Entry(236, 230, 255, 250, 274, 125, "MASON DEMING"),
            new ShellRegions.Entry(262, 230, 283, 250, 274, 125, "TIM MORTEN"),
            new ShellRegions.Entry(305, 220, 330, 250, 274, 125, "JERRETT FARMER"),
            new ShellRegions.Entry(375, 215, 400, 240, 274, 125, "JOHN KEATING"),
            new ShellRegions.Entry(410, 215, 430, 240, 274, 125, "JACK MAMAIS"),
            new ShellRegions.Entry(480, 250, 500, 275, 274, 125, "DAIVA VENCKUS"),
            new ShellRegions.Entry(580, 200, 610, 230, 274, 125, "JOHN PECK"),
            new ShellRegions.Entry(200, 335, 225, 360, 274, 125, "BILL FERRER"),
            new ShellRegions.Entry(240, 365, 270, 400, 274, 125, "SEAN KINNEAR"),
            new ShellRegions.Entry(190, 400, 210, 425, 274, 125, "JJ FRANZEN"),
            new ShellRegions.Entry(0, 0, 640, 30, 315, 20, "EXIT")
        };

        public static int SelectedIndex { get; private set; }
        static Mw2Shell _shell;
        static ShellRegions _regions;
        static ShellSound _select, _arrival;

        public static void Open(Mw2Shell shell)
        {
            _shell = shell;
            _arrival = null;
            shell.Anims.Clear();
            if (shell.Career.Msg23C3 == 0xc01b)
            {
                _arrival = Sound(0x79, 50);
                _arrival.Play();
            }
            shell.Surface.LoadBackground(ShellPcx.Decode(shell.Db.Unpack(48)));
            _select = Sound(0x36, 50);
            _regions = new ShellRegions(shell.Surface, shell.Font6, false, Spots, () => true);
            shell.FlushKeys();
            shell.Register(Tick);
        }

        static ShellSound Sound(int record, int volume)
        {
            var sound = new ShellSound(_shell.gameObject, _shell.Db, record);
            sound.SetVolume(volume);
            return sound;
        }

        static void Tick(uint ev)
        {
            var shell = _shell;
            if (ev == 0xc004)
            {
                shell.TickCounter++;
                shell.Anims.Step();
                int spot = _regions.Hit(shell.MouseX, shell.MouseY);
                if (shell.MouseClicked)
                {
                    if (spot == 16) ev = 0xc01b;
                    else if (spot >= 0)
                    {
                        shell.BlockUntilEnd(_select);
                        SelectedIndex = spot;
                        ev = 0xc038;
                    }
                }
            }
            if (ev == 0xc004) return;
            _regions.Dispose(); _regions = null;
            shell.Anims.Clear();
            shell.Surface.DrawAfter.Clear(); shell.Surface.DrawBefore.Clear();
            _select?.Release(); _arrival?.Release();
            shell.Unregister();
            shell.Post(ev, 0xc018);
        }
    }
}
