namespace MW2.Shell
{
    /// <summary>Mech Ops hub 0xc032 (MW2SHELL.DLL FUN_100364cf 0x100364cf,
    /// FUN_10036a4a 0x10036a4a): five shops and the exit to Mech Bay.</summary>
    public static class MechOpsScreen
    {
        static readonly ShellRegions.Entry[] Spots =
        {
            new ShellRegions.Entry(13, 7, 238, 97, 0, 0, ""),
            new ShellRegions.Entry(13, 98, 238, 191, 0, 0, ""),
            new ShellRegions.Entry(13, 192, 238, 286, 0, 0, ""),
            new ShellRegions.Entry(13, 287, 238, 378, 0, 0, ""),
            new ShellRegions.Entry(13, 379, 238, 469, 0, 0, ""),
            new ShellRegions.Entry(512, 426, 618, 471, 0, 0, "")
        };
        static readonly uint[] Destinations = { 0xc01e, 0xc01d, 0xc02f, 0xc028, 0xc030, 0xc01b };
        // MW2SHELL.DLL 0x100a7538, indexed by rand() % 29 in FUN_10036f81.
        static readonly int[] Chatter =
            { 106, 107, 108, 109, 110, 111, 112, 113, 114, 115,
              97, 98, 99, 118, 119, 119, 119, 119, 119, 119,
              119, 119, 119, 119, 119, 119, 119, 119, 119 };

        static Mw2Shell _shell;
        static ShellRegions _regions;
        static ShellAnim[] _buttons;
        static ShellSound _select, _exit, _ambient, _chat;
        static int _hover;

        public static void Open(Mw2Shell shell)
        {
            _shell = shell;
            _hover = -1;
            _chat = null;
            shell.Anims.Clear();
            shell.Surface.LoadBackground(ShellPcx.Decode(shell.Db.Unpack(18)));
            new ShellAnim(shell.Anims, "mbay", 352, 113, 0x28, 0).Show();
            _buttons = new[]
            {
                new ShellAnim(shell.Anims, "bmarms", 19, 11, 0x21, 0),
                new ShellAnim(shell.Anims, "bmmech", 19, 104, 0x21, 0),
                new ShellAnim(shell.Anims, "bmcust", 19, 198, 0x21, 0),
                new ShellAnim(shell.Anims, "bmrepr", 19, 292, 0x21, 0),
                new ShellAnim(shell.Anims, "bmwgrp", 19, 385, 0x21, 0),
                new ShellAnim(shell.Anims, "bmexit", 525, 437, 0x21, 0)
            };
            foreach (var button in _buttons) button.Show();
            shell.Anims.Step();
            _regions = new ShellRegions(shell.Surface, shell.Font7, false, Spots, () => true);
            _select = Sound(0x35, 50);
            _exit = Sound(0x39, 50);
            _ambient = Sound(0x3a, 10); _ambient.LoopForever(); _ambient.Play();
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
                if (spot != _hover)
                {
                    if (_hover >= 0 && _hover < 5) _buttons[_hover].SetFrame(0);
                    _hover = spot;
                    if (spot >= 0 && spot < 5) _buttons[spot].SetFrame(1);
                }
                if (shell.MouseClicked && spot >= 0 && spot < Destinations.Length)
                {
                    if (spot == 5)
                    {
                        _buttons[5].SetFrame(1);
                        shell.Anims.Step();
                        shell.BlockUntilEnd(_exit);
                    }
                    else shell.BlockUntilEnd(_select);
                    ev = Destinations[spot];
                }
            }
            if (ev == 0xc004) return;
            _regions.Dispose(); _regions = null;
            shell.Anims.Clear();
            shell.Surface.DrawAfter.Clear(); shell.Surface.DrawBefore.Clear();
            _select?.Release(); _exit?.Release(); _ambient?.Release(); _chat?.Release();
            shell.Unregister();
            shell.Post(ev, 0xc032);
        }

        /// <summary>MW2SHELL.DLL FUN_10036f81 0x10036f81: wait for the last sample, then select another.</summary>
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
