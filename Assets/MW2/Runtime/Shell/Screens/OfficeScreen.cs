namespace MW2.Shell
{
    /// <summary>Office 0xc01a (MW2SHELL.DLL FUN_10055b5f 0x10055b5f and FUN_10056065
    /// 0x10056065): five DLL regions; duty roster and star chart are disabled here.</summary>
    public static class OfficeScreen
    {
        static readonly ShellRegions.Entry[] Spots =
        {
            new ShellRegions.Entry(538, 102, 639, 297, 532, 83, "DUTY ROSTER"),
            new ShellRegions.Entry(417, 183, 500, 385, 449, 149, "MECH BAY"),
            new ShellRegions.Entry(89, 160, 210, 354, 112, 147, "STARPORT"),
            new ShellRegions.Entry(138, 355, 319, 463, 278, 339, "COMPUTER"),
            new ShellRegions.Entry(230, 164, 436, 311, 292, 122, "STAR CHART")
        };
        static Mw2Shell _shell;
        static ShellRegions _regions;
        static ShellSound _doorSound, _computerSound, _arrivalSound;

        public static void Open(Mw2Shell shell)
        {
            _shell = shell;
            _arrivalSound = null;
            shell.Anims.Clear();
            shell.Surface.LoadBackground(ShellPcx.Decode(shell.Db.Unpack(15)));
            if (shell.Career.Msg23C3 == 0xc01c)
            {
                new ShellAnim(shell.Anims, "offdoorc", 103, 194, 0x44, 1000).Show();
                _arrivalSound = Sound(0x43, 25);
                _arrivalSound.Play();
            }
            new ShellAnim(shell.Anims, "omon", 156, 363, 0x28, 1000).Show();
            new ShellAnim(shell.Anims, "starmap", 263, 296, 0x28, 1000).Show();
            _doorSound = Sound(0x43, 40);
            _computerSound = Sound(0x3e, 55);
            _regions = new ShellRegions(shell.Surface, shell.Font7, false, Spots, () => true);
            _regions.Disable(0);
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
                int spot = _regions.Hit(shell.MouseX, shell.MouseY);
                if (shell.MouseClicked)
                {
                    if (spot == 1)
                    {
                        var door = new ShellAnim(shell.Anims, "offdoorb", 427, 157, 0x44, 1000);
                        door.Show();
                        _doorSound.Play();
                        shell.HoldUntilEnd(door, 0xc01b);
                        return;
                    }
                    if (spot == 2)
                    {
                        var door = new ShellAnim(shell.Anims, "offdoora", 103, 194, 0x44, 1000);
                        door.Show();
                        _doorSound.Play();
                        shell.HoldUntilEnd(door, 0xc01c);
                        return;
                    }
                    if (spot == 3) { shell.BlockUntilEnd(_computerSound); ev = 0xc021; }
                }
            }
            if (ev == 0xc004) return;
            _regions.Dispose(); _regions = null;
            shell.Anims.Clear();
            shell.Surface.DrawAfter.Clear(); shell.Surface.DrawBefore.Clear();
            _doorSound?.Release(); _computerSound?.Release(); _arrivalSound?.Release();
            shell.Unregister();
            shell.Post(ev, 0xc01a);
        }
    }
}
