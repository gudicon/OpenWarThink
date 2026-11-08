using System.Collections.Generic;
using UnityEngine;

namespace MW2.Shell
{
    /// <summary>
    /// Briefing 0xc012 (FUN_10059a4a 0x10059a4a, screen function FUN_1005a59a 0x1005a59a). Background 0x25, the ORDR
    /// text of xxxxBRF1 typed into a 301 x 370 page at (34, 29) in font 11 with remap 1 -> 0x11 (two pages at most:
    /// spot 1 toggles them), the news flic of the step as an animation at (377, 26) (spot 3), launch (spot 4, checked
    /// against the drop ship's capacity), back to the drop ship (spot 6) and the "MERCS" key sequence or spot 7 for 0xc02d.
    /// In dock (+0x239d in a regular career) or without the player's mech the page is replaced by the "IN DOCK" notes.
    /// The news flic plays its Smacker sound track while the shell's samples are released (FUN_10002231).
    /// </summary>
    public static class BriefingScreen
    {
        static readonly ShellRegions.Entry[] Spots =
        {
            new ShellRegions.Entry(212, 0, 423, 40, 0, 0, ""),
            new ShellRegions.Entry(104, 416, 275, 470, 0, 0, ""),
            new ShellRegions.Entry(400, 314, 600, 359, 0, 0, ""),
            new ShellRegions.Entry(447, 257, 548, 300, 0, 0, ""),
            new ShellRegions.Entry(338, 423, 503, 471, 0, 0, ""),
            new ShellRegions.Entry(400, 360, 600, 405, 0, 0, ""),
            new ShellRegions.Entry(504, 427, 600, 473, 0, 0, ""),
            new ShellRegions.Entry(20, 20, 35, 35, 0, 0, "")
        };
        static readonly char[] Cheat = { 'M', 'E', 'R', 'C', 'S' };     // DAT_100b86f8
        static Mw2Shell _s;
        static ShellRegions _regions;
        static ShellSound _click, _ambient, _backSound;                 // DAT_100d8670, DAT_100d8650, DAT_100d8654
        static ShellAnim _picture, _viewButton, _pageButton, _launchButton, _backButton, _news;
        static ShellTextPage _page0, _page1, _current;                  // DAT_100d8668, DAT_100d866c, DAT_100d864c
        static readonly List<ShellText> Notes = new List<ShellText>();
        static bool _second, _canLaunch;                                // DAT_100d8628, DAT_100d8624
        static int _cheat;                                              // DAT_100d8664
        static byte[] _remap11, _largeRemap;

        public static void Open(Mw2Shell s)
        {
            _s = s;
            var c = s.Career;
            c.Byte23B7 = false;
            _picture = _viewButton = _pageButton = _launchButton = _backButton = _news = null;
            _page0 = _page1 = _current = null;
            _second = _canLaunch = false;
            _cheat = 0;
            Notes.Clear();
            _remap11 = ShellSurface.MakeRemap(0x11);        // DAT_100cceb0
            _largeRemap = _remap11;
            s.Anims.Clear();
            s.Surface.LoadBackground(ShellPcx.Decode(s.Db.Unpack(0x25)));
            _click = Sound(0x35, -1);
            if (c.Str(0x1b6, 0x10).Length > 0) _picture = new ShellAnim(s.Anims, c.Str(0x1b6, 0x10), 0x163, 0x16, 0x68, 1000);
            _viewButton = new ShellAnim(s.Anims, "lbav", 0x1c8, 0x108, 0x21, 1000);
            _pageButton = new ShellAnim(s.Anims, "lbas", 0x72, 0x1a6, 0x21, 1000);
            var launch = new ShellAnim(s.Anims, "lbal", 0x15f, 0x1b1, 0x21, 1000);
            _backButton = new ShellAnim(s.Anims, "lbbe", 0x1fe, 0x1b8, 0x21, 1000);
            _backButton.Show();
            _regions = new ShellRegions(s.Surface, s.Font7, false, Spots, () => true);
            bool inDock = false;
            if (c.Mode == 0) _regions.Disable(2);
            else if (c.Mode == 1) { inDock = c.Finished239D; _regions.Disable(5); _regions.Disable(2); }
            else if (c.Mode == 2) { _regions.Disable(2); _regions.Disable(6); }
            var pages = ShellBriefing.BuildPages(s.Data, ShellBriefing.ResourceName(c.Mission), c, "",
                s.Font11, s.Font7, 0x22, 0x1d, 0x12d, 0x172);
            _page0 = pages.Count > 0 ? pages[0] : new ShellTextPage(s.Font11, s.Font7, 0, 0, 100, 100);
            if (pages.Count > 1) _page1 = pages[1];
            bool hasMech = c.PlayerHasMech();
            if (inDock || !hasMech)
            {
                _page0 = new ShellTextPage(s.Font11, s.Font7, 0x22, 0x1d, 0x12d, 0x172);
                _page1 = null;
                Note(s.Font7, "IN DOCK", 0x1d0, 0x7e);
                if (inDock)
                    _page0.Layout("You must choose a contract before proceeding to your next destination.  Proceed to the " +
                                  "office, log on to the computer and c");
                if (!hasMech) Note(s.Font7, "PLAYER MUST BE ASSIGNED A MECH", 0x2e, 0x5c);
                if (c.Byte23A2) { _regions.Disable(0); _regions.Disable(4); }
                else { _regions.Disable(4); _regions.Enable(6); }
            }
            else
            {
                _canLaunch = true;
                _launchButton = launch;
                _picture?.Show();
                _pageButton.Show();
                _viewButton.Show();
            }
            foreach (var p in new[] { _page0, _page1 })
                if (p != null)
                {
                    p.Create(s.Surface, _remap11, _largeRemap);
                    p.TypeSound = Sound(0x32, -1);
                    p.TypeSound.LoopForever();
                }
            s.FlushKeys();
            _current = _page0;
            _current.StartTyping();
            _launchButton?.Show();
            _ambient = Sound(0x40, 0x1e); _ambient.LoopForever(); _ambient.Play();
            _backSound = Sound(0x39, 0x32);
            s.Register(Tick);
        }

        static ShellSound Sound(int record, int volume)
        {
            var snd = new ShellSound(_s.gameObject, _s.Db, record);
            if (volume >= 0) snd.SetVolume(volume);
            return snd;
        }

        static void Note(ShellFont font, string text, int x, int y)
        {
            var t = new ShellText(_s.Surface, font, text, x, y, _remap11);
            t.Show();
            Notes.Add(t);
        }

        static void Tick(uint ev)
        {
            var s = _s;
            var c = s.Career;
            if (ev == 0xc004)
            {
                // The news animation has run out: it goes, the step picture comes back (FUN_1005b095 recreates the samples).
                if (_news != null && !_news.Running)
                {
                    _news.Hide();
                    s.Anims.Step();
                    _news.Destroy();
                    _news = null;
                    _picture?.Show();
                }
                s.Anims.Step();
                _current?.Advance();
                foreach (char ch in Input.inputString)
                {
                    if (char.ToUpperInvariant(ch) == Cheat[_cheat])
                    {
                        if (++_cheat > 4) { ev = 0xc02d; _cheat = 0; }
                    }
                    else _cheat = 0;
                }
                int spot = _regions.Hit(s.MouseX, s.MouseY);
                if (s.MouseClicked && ev == 0xc004)
                {
                    switch (spot)
                    {
                        case 1:
                            if (_page1 == null) break;
                            _pageButton.SetFrame(_second ? 0 : 1);
                            s.Anims.Step();
                            _current.Hide();
                            _current = _second ? _page0 : _page1;
                            _current.StartTyping();
                            _second = !_second;
                            break;
                        case 3:
                            if (!_canLaunch) break;
                            _viewButton.SetFrame(1);
                            s.Anims.Step();
                            _viewButton.SetFrame(0);
                            _picture?.Hide();
                            if (c.NewsFlic.Length == 0) c.SetStr(0x176, "nodata");
                            if (_news == null || !_news.Running)
                            {
                                _ambient?.Stop();
                                _page0.TypeSound?.Stop();
                                _page1?.TypeSound?.Stop();
                                _news = new ShellAnim(s.Anims, c.NewsFlic, 0x179, 0x1a, 0x24, 1000);
                                _news.Show();
                            }
                            break;
                        case 4:
                            _launchButton?.SetFrame(1);
                            s.Anims.Step();
                            if (c.CrewCountForLaunch() > c.DropShipSlots)
                            {
                                s.Dialog($"You can assign only {c.DropShipSlots} starmates|for this mission#Ok", null);
                                _launchButton?.SetFrame(0);
                            }
                            else
                            {
                                s.BlockUntilEnd(_click);
                                if (_canLaunch) ev = 0xc011;
                            }
                            break;
                        case 6:
                            _backButton.SetFrame(1);
                            s.Anims.Step();
                            s.BlockUntilEnd(_backSound);
                            ev = 0xc036;
                            break;
                        case 7:
                            ev = 0xc02d;
                            _cheat = 0;
                            break;
                    }
                }
                if (_news == null && _ambient != null && !_ambient.Playing) _ambient.Play();
            }
            if (ev == 0xc004) return;
            _regions.Dispose(); _regions = null;
            _page0?.Hide(); _page1?.Hide();
            _page0?.TypeSound?.Release(); _page1?.TypeSound?.Release();
            foreach (var t in Notes) t.Hide();
            Notes.Clear();
            _backSound?.Release();
            s.Anims.Clear();
            _ambient?.Release(); _click?.Release();
            s.Surface.DrawAfter.Clear(); s.Surface.DrawBefore.Clear();
            s.Post(ev, 0xc012);
            s.Unregister();
        }
    }
}
