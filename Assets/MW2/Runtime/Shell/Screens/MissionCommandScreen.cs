using UnityEngine;

namespace MW2.Shell
{
    /// <summary>Drop-ship approach 0xc016: FUN_10023b59 / FUN_10023f12.</summary>
    public static class MissionCommandScreen
    {
        static readonly ShellRegions.Entry[] Spots =
        {
            new ShellRegions.Entry(563, 232, 639, 313, 490, 216, "ABORT CAMPAIGN"),
            new ShellRegions.Entry(208, 209, 351, 297, 230, 201, "MISSION COMMAND")
        };

        static Mw2Shell _s;
        static ShellRegions _regions;
        static ShellSound _ambient, _select;
        static ShellText _question;
        static bool _confirm;

        public static void Open(Mw2Shell s)
        {
            _s = s;
            _confirm = false;
            _question = null;
            s.Career.Byte23B9 = true;
            s.Anims.Clear();
            s.Surface.LoadBackground(ShellPcx.Decode(s.Db.Unpack(43)));
            _ambient = new ShellSound(s.gameObject, s.Db, 0x40);
            _ambient.SetVolume(30); _ambient.LoopForever(); _ambient.Play();
            _select = new ShellSound(s.gameObject, s.Db, 0x3e);
            _select.SetVolume(40);
            new ShellAnim(s.Anims, "ocbed", 283, 48, 0x21, 0);
            new ShellAnim(s.Anims, "dmon", 220, 211, 0x68, 0).Show();
            new ShellAnim(s.Anims, "dthing", 302, 98, 0x68, 0).Show();
            new ShellAnim(s.Anims, "drop", 575, 233, 0x68, 0).Show();
            _regions = new ShellRegions(s.Surface, s.Font7, false, Spots, () => true);
            if (s.Career.Mode == 0) _regions.Disable(0);
            s.Register(Tick);
        }

        static void Tick(uint ev)
        {
            var s = _s;
            if (ev == 0xc004)
            {
                s.TickCounter++;
                s.Anims.Step();
                if (_confirm)
                {
                    if (Input.GetKeyDown(KeyCode.Y))
                    {
                        ev = s.Career.Abort(s.Data, Mw2Shell.MoviesOn == 1);
                        if (ev == 0xc004) _confirm = false;
                    }
                    else if (Input.GetKeyDown(KeyCode.N) || Input.GetKeyDown(KeyCode.Escape)) _confirm = false;
                    if (!_confirm) { _question?.Hide(); _question = null; }
                }
                else
                {
                    int spot = _regions.Hit(s.MouseX, s.MouseY);
                    if (s.MouseClicked && spot == 0)
                    {
                        if (!s.Career.CanAbortCampaign())
                            Debug.Log("You cannot abort this campaign.");
                        else
                        {
                            _confirm = true;
                            _question = new ShellText(s.Surface, s.Font6, "Are you sure? Y/N", 240, 442,
                                ShellSurface.MakeRemap(0x16));
                            _question.Show();
                        }
                    }
                    else if (s.MouseClicked && spot == 1)
                    {
                        s.BlockUntilEnd(_select);
                        ev = 0xc036;
                    }
                }
            }
            if (ev == 0xc004) return;
            _question?.Hide(); _question = null;
            _regions.Dispose(); _regions = null;
            s.Anims.Clear();
            s.Surface.DrawAfter.Clear(); s.Surface.DrawBefore.Clear();
            _ambient?.Release(); _select?.Release();
            s.Post(ev, 0xc016);
            s.Unregister();
        }
    }
}
