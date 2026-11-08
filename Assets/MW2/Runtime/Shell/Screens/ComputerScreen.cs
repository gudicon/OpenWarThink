namespace MW2.Shell
{
    /// <summary>Office computer hub 0xc021 (MW2SHELL.DLL FUN_10009bcf 0x10009bcf,
    /// FUN_1000a064 0x1000a064): contracts, personnel, inventory, finance and exit.</summary>
    public static class ComputerScreen
    {
        static readonly ShellRegions.Entry[] Spots =
        {
            new ShellRegions.Entry(13, 10, 296, 125, 0, 0, ""),
            new ShellRegions.Entry(13, 126, 296, 240, 0, 0, ""),
            new ShellRegions.Entry(13, 241, 296, 356, 0, 0, ""),
            new ShellRegions.Entry(13, 357, 296, 471, 0, 0, ""),
            new ShellRegions.Entry(511, 425, 617, 473, 0, 0, "")
        };
        static readonly uint[] Destinations = { 0xc024, 0xc022, 0xc01f, 0xc020, 0xc01a };
        static Mw2Shell _shell;
        static ShellRegions _regions;
        static ShellSound _ambient, _exitSound, _selectSound;

        public static void Open(Mw2Shell shell)
        {
            _shell = shell;
            shell.Anims.Clear();
            shell.Surface.LoadBackground(ShellPcx.Decode(shell.Db.Unpack(34)));
            new ShellAnim(shell.Anims, "offlogo", 352, 111, 0x28, 0).Show();
            new ShellAnim(shell.Anims, "ooexit", 525, 436, 0x21, 0).Show();
            new ShellAnim(shell.Anims, "oocont", 19, 18, 0x21, 0).Show();
            new ShellAnim(shell.Anims, "oopers", 19, 135, 0x21, 0).Show();
            new ShellAnim(shell.Anims, "ooinvt", 19, 251, 0x21, 0).Show();
            new ShellAnim(shell.Anims, "oofina", 19, 366, 0x21, 0).Show();
            _ambient = Sound(0x3d, 30); _ambient.LoopForever(); _ambient.Play();
            _exitSound = Sound(0x39, 50);
            _selectSound = Sound(0x35, 50);
            _regions = new ShellRegions(shell.Surface, shell.Font7, false, Spots, () => true);
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
                if (shell.MouseClicked && spot >= 0 && spot < Destinations.Length)
                {
                    shell.BlockUntilEnd(spot == 4 ? _exitSound : _selectSound);
                    ev = Destinations[spot];
                }
            }
            if (ev == 0xc004) return;
            _regions.Dispose(); _regions = null;
            shell.Anims.Clear();
            shell.Surface.DrawAfter.Clear(); shell.Surface.DrawBefore.Clear();
            _ambient?.Release(); _exitSound?.Release(); _selectSound?.Release();
            shell.Unregister();
            shell.Post(ev, 0xc021);
        }
    }
}
