namespace MW2.Shell
{
    /// <summary>Office finance page 0xc020 (FUN_100284c0 / FUN_1002941e): one source text page.</summary>
    public static class FinanceScreen
    {
        static readonly ShellRegions.Entry[] Spots =
        {
            new ShellRegions.Entry(500, 432, 602, 472, 0, 0, "")
        };
        static Mw2Shell _shell;
        static ShellRegions _regions;
        static ShellSound _exit;
        static ShellTextPage _page;

        public static void Open(Mw2Shell shell)
        {
            _shell = shell;
            shell.Anims.Clear();
            shell.Surface.LoadBackground(ShellPcx.Decode(shell.Db.Unpack(shell.Career.Byte23B9 ? 46 : 30)));
            new ShellAnim(shell.Anims, shell.Career.Byte23B9 ? "occfe" : "ocbfe", 510, 440, 0x21, 0).Show();
            _regions = new ShellRegions(shell.Surface, shell.Font7, false, Spots, () => true);
            _exit = new ShellSound(shell.gameObject, shell.Db, 0x39);
            _exit.SetVolume(30);
            ShowReport(FinanceReport.Read(shell.Career, shell.Data.Tables));
            shell.FlushKeys();
            shell.Register(Tick);
        }

        static void ShowReport(FinanceReport r)
        {
            _page = new ShellTextPage(_shell.Font11, _shell.Font7, 0xb4, 0x6d, 0x197, 0x123);
            string body = FinanceReport.PageText(r, _shell.Career, _shell.Font11, _shell.Font7);
            _page.Layout(body);
            _page.Create(_shell.Surface, ShellSurface.MakeRemap(0x11), ShellSurface.MakeRemap(0x44));
            _page.StartTyping();
        }

        static void Tick(uint ev)
        {
            var shell = _shell;
            if (ev == 0xc004)
            {
                shell.TickCounter++;
                shell.Anims.Step();
                _page?.Advance();
                if (shell.MouseClicked && _regions.Hit(shell.MouseX, shell.MouseY) == 0 && shell.Career.Mode == 1)
                {
                    shell.BlockUntilEnd(_exit);
                    ev = shell.Career.Byte23B9 ? 0xc036u : 0xc021u;
                }
            }
            if (ev == 0xc004) return;
            _page?.Hide(); _page = null;
            _regions.Dispose(); _regions = null;
            shell.Anims.Clear();
            shell.Surface.DrawAfter.Clear(); shell.Surface.DrawBefore.Clear();
            _exit?.Release();
            shell.Unregister();
            shell.Post(ev, 0xc020);
        }
    }
}
