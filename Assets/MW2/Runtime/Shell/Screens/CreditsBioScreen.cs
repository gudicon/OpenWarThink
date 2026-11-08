namespace MW2.Shell
{
    /// <summary>Credits biography 0xc038 (MW2SHELL.DLL FUN_10025f50 0x10025f50,
    /// FUN_100262cd 0x100262cd): ORDR text from the selected biography BWD.</summary>
    public static class CreditsBioScreen
    {
        // MW2SHELL.DLL 0x100a3698: index and BWD name pairs.
        static readonly string[] Biographies =
        {
            "DUSTBBIO", "KEGELBIO", "SMITHBIO", "MATTBIO", "HENRYBIO", "KENBIO", "MASONBIO", "TIMBIO",
            "JERETBIO", "KEATBIO", "JACKMBIO", "DAIVABIO", "PECKBIO", "BILLFBIO", "KBIO", "JJBIO"
        };
        static readonly ShellRegions.Entry[] Spots =
        {
            new ShellRegions.Entry(501, 431, 602, 473, 0, 0, "")
        };
        static Mw2Shell _shell;
        static ShellRegions _regions;
        static ShellAnim _exitButton;
        static ShellSound _exitSound, _typing;
        static ShellTextPage _page;

        public static void Open(Mw2Shell shell)
        {
            _shell = shell;
            shell.Anims.Clear();
            shell.Surface.LoadBackground(ShellPcx.Decode(shell.Db.Unpack(33)));
            _regions = new ShellRegions(shell.Surface, shell.Font6, false, Spots, () => true);
            _exitButton = new ShellAnim(shell.Anims, "ocbce", 510, 440, 0x21, 0);
            _exitButton.Show();
            _exitSound = Sound(0x39, 50);
            _typing = Sound(0x32, 10); _typing.LoopForever();
            int index = CreditsScreen.SelectedIndex;
            var pages = ShellBriefing.BuildPages(shell.Data, Biographies[index], shell.Career, "",
                shell.Font11, shell.Font7, 47, 65, 549, 286);
            _page = pages.Count > 0 ? pages[0] : new ShellTextPage(shell.Font11, shell.Font7, 47, 65, 549, 286);
            _page.Create(shell.Surface, ShellSurface.MakeRemap(0x16), ShellSurface.MakeRemap(0x11));
            _page.TypeSound = _typing;
            _page.StartTyping();
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
                _page.Advance();
                int spot = _regions.Hit(shell.MouseX, shell.MouseY);
                if (shell.MouseClicked && spot == 0)
                {
                    _exitButton.SetFrame(1);
                    shell.Anims.Step();
                    shell.BlockUntilEnd(_exitSound);
                    ev = 0xc018;
                }
            }
            if (ev == 0xc004) return;
            _page.Hide(); _page = null;
            _regions.Dispose(); _regions = null;
            shell.Anims.Clear();
            shell.Surface.DrawAfter.Clear(); shell.Surface.DrawBefore.Clear();
            _exitSound?.Release(); _typing?.Release();
            shell.Unregister();
            shell.Post(ev, 0xc038);
        }
    }
}
