namespace MW2.Shell
{
    /// <summary>Office mech bay 0xc01b (MW2SHELL.DLL FUN_10035b2b 0x10035b2b,
    /// FUN_10035f4b 0x10035f4b). Three of its six hotspots are disabled by the source.</summary>
    public static class MechBayScreen
    {
        // MW2SHELL.DLL 0x100a7318, indexed by rand() % 23 in FUN_10036317.
        static readonly int[] Chatter =
            { 106, 107, 108, 109, 110, 111, 112, 113, 114, 115,
              97, 98, 99, 118, 119, 119, 119, 119, 119, 119, 119, 119, 119 };

        static readonly ShellRegions.Entry[] Spots =
        {
            new ShellRegions.Entry(0, 191, 98, 431, 12, 214, "MECH OPS"),
            new ShellRegions.Entry(72, 107, 153, 200, 82, 104, "MECH FACTORY"),
            new ShellRegions.Entry(0, 432, 639, 479, 321, 443, "OFFICE"),
            new ShellRegions.Entry(75, 203, 163, 288, 150, 244, "CUSTOMIZE"),
            new ShellRegions.Entry(164, 198, 258, 278, 164, 178, "ARMS DEALER"),
            new ShellRegions.Entry(391, 116, 456, 169, 0, 0, "")
        };
        static Mw2Shell _shell;
        static ShellRegions _regions;
        static ShellSound _ambient, _officeSound, _opsSound, _chat;

        public static void Open(Mw2Shell shell)
        {
            _shell = shell;
            _chat = null;
            shell.Anims.Clear();
            shell.Surface.LoadBackground(ShellPcx.Decode(shell.Db.Unpack(14)));
            _ambient = Sound(0x3a, 10); _ambient.LoopForever(); _ambient.Play();
            _officeSound = Sound(0x43, 25);
            _opsSound = Sound(0x3e, 40);
            new ShellAnim(shell.Anims, "mmon", 0, 234, 0x28, 0).Show();
            new ShellAnim(shell.Anims, "mlighta", 0, 192, 0x28, 0).Show();
            new ShellAnim(shell.Anims, "mlightb", 322, 220, 0x28, 0).Show();
            new ShellAnim(shell.Anims, "mlightc", 455, 246, 0x28, 0).Show();
            _regions = new ShellRegions(shell.Surface, shell.Font7, false, Spots, () => true);
            _regions.Disable(1);
            _regions.Disable(3);
            _regions.Disable(4);
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
                Chat();
                int spot = _regions.Hit(shell.MouseX, shell.MouseY);
                if (shell.MouseClicked)
                {
                    if (spot == 0) { shell.BlockUntilEnd(_opsSound); ev = 0xc032; }
                    else if (spot == 2) { shell.BlockUntilEnd(_officeSound); ev = 0xc01a; }
                    else if (spot == 5) ev = 0xc018;
                }
            }
            if (ev == 0xc004) return;
            _regions.Dispose(); _regions = null;
            shell.Anims.Clear();
            shell.Surface.DrawAfter.Clear(); shell.Surface.DrawBefore.Clear();
            _ambient?.Release(); _officeSound?.Release(); _opsSound?.Release(); _chat?.Release();
            shell.Unregister();
            shell.Post(ev, 0xc01b);
        }

        static void Chat()
        {
            if (_chat != null)
            {
                if (_chat.Playing) return;
                _chat.Release();
                _chat = null;
            }
            int record = Chatter[_shell.Rand() % Chatter.Length];
            if (record == 1) return;
            _chat = Sound(record, record < 10 ? 5 : 15);
            _chat.Play();
        }
    }
}
