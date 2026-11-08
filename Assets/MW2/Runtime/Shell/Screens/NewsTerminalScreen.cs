using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace MW2.Shell
{
    /// <summary>ComStar news terminal 0xc026 (MW2SHELL.DLL FUN_1005129d 0x1005129d,
    /// FUN_10051c0b 0x10051c0b). Text pages use FUN_100765d9 and font 11 from DAT_100cd0dc.</summary>
    public static class NewsTerminalScreen
    {
        const string MissingNews = "The Comstar Newsfeed is temporarily unavailable. Thank you for your interest in this quality news service, but we are currently experiencing difficulties. Please try again later.";
        static readonly ShellRegions.Entry[] Spots =
        {
            new ShellRegions.Entry(510, 423, 621, 474, 0, 0, ""),
            new ShellRegions.Entry(493, 3, 623, 402, 0, 0, ""),
            new ShellRegions.Entry(400, 35, 424, 57, 0, 0, ""),
            new ShellRegions.Entry(427, 35, 451, 57, 0, 0, "")
        };
        static Mw2Shell _shell;
        static ShellRegions _regions;
        static ShellSound _exitSound, _pageSound, _typing, _ambient;
        static ShellAnim _letter;
        static bool _letterShown;
        static List<ShellTextPage> _pages;
        static int _page;

        public static void Open(Mw2Shell shell)
        {
            _shell = shell;
            _page = 0;
            _letter = null; _letterShown = false;
            shell.Anims.Clear();
            shell.Surface.LoadBackground(ShellPcx.Decode(shell.Db.Unpack(0x24)));
            _regions = new ShellRegions(shell.Surface, shell.Font7, false, Spots, () => true);
            new ShellAnim(shell.Anims, "scrollu", 400, 35, 0x21, 0).Show();
            new ShellAnim(shell.Anims, "scrolld", 427, 35, 0x21, 0).Show();
            new ShellAnim(shell.Anims, "nsexit", 523, 438, 0x21, 0).Show();
            _exitSound = Sound(0x39, 50);
            _pageSound = Sound(0x34, 50);
            // FUN_1005129d: the ordinary terminal loops record 0x3f with Sound_Create's default volume 1.
            // The special story-complete path creates a 0x32 text sample after FUN_100523dd instead.
            bool storyLetter = shell.Career.StepType == 3 && shell.Career.Mode == 1;
            _ambient = Sound(0x3f, 1);
            _ambient.LoopForever();
            if (!storyLetter) _ambient.Play();
            _typing = storyLetter ? Sound(0x32, 10) : null;
            _typing?.LoopForever();
            // FUN_1005129d: for a completed regular story, mletter runs as a holding Smacker and
            // FUN_10051c0b waits for its end before restarting record 0x3f.
            if (storyLetter) _letter = new ShellAnim(shell.Anims, "mletter", 0, 0, 4, 1000);
            string news = shell.Career.News;
            if (news.StartsWith("None, why are you reading this?! Oops! Bug! Report me! Bzzzzzzzzz.")) news = MissingNews;
            byte[] expanded = ShellBriefing.ExpandNames(Encoding.ASCII.GetBytes(news), shell.Career);
            _pages = BuildPages(ShellBriefing.Latin1(expanded), shell.Font11, shell.Font7);
            foreach (var page in _pages)
            {
                page.Create(shell.Surface, ShellSurface.MakeRemap(0x11), ShellSurface.MakeRemap(0x44));
                page.TypeSound = _typing;
            }
            if (_pages.Count > 0) _pages[0].StartTyping();
            shell.FlushKeys();
            shell.Register(Tick);
        }

        static ShellSound Sound(int record, int volume)
        {
            var sound = new ShellSound(_shell.gameObject, _shell.Db, record);
            sound.SetVolume(volume);
            return sound;
        }

        /// <summary>FUN_1005129d: repeat FUN_10075e50/FUN_100765d9 until the whole news string is laid out.</summary>
        public static List<ShellTextPage> BuildPages(string text, ShellFont font, ShellFont large)
        {
            var pages = new List<ShellTextPage>();
            if (text == null) return pages;
            int at = 0;
            do
            {
                var page = new ShellTextPage(font, large, 0x22, 0x43, 0x1ad, 0x17a);
                int next = page.Layout(text, at);
                pages.Add(page);
                if (next == at) break;
                at = next;
            } while (at >= 0);
            return pages;
        }

        static void ChangePage(int direction)
        {
            int next = _page + direction;
            if (next < 0 || next >= _pages.Count) return;
            _pages[_page].Hide();
            _page = next;
            _pageSound?.Play();
            _pages[_page].StartTyping();
        }

        static void Tick(uint ev)
        {
            var shell = _shell;
            if (ev == 0xc004)
            {
                shell.TickCounter++;
                shell.Anims.Step();
                bool typed = _pages.Count == 0 || _pages[_page].Advance();
                if (_letter != null)
                {
                    if (typed && !_letterShown) { _letterShown = true; _letter.Show(); }
                    if (!_letter.Running)
                    {
                        _letter.Destroy(); _letter = null;
                        _ambient?.Play();
                    }
                }
                int spot = _regions.Hit(shell.MouseX, shell.MouseY);
                if (Input.GetKey(KeyCode.PageUp)) ChangePage(-1);
                else if (Input.GetKey(KeyCode.PageDown)) ChangePage(1);
                if (shell.MouseClicked)
                {
                    if (spot == 0) { shell.BlockUntilEnd(_exitSound); ev = 0xc01c; }
                    else if (spot == 2) ChangePage(-1);
                    else if (spot == 3) ChangePage(1);
                }
            }
            if (ev == 0xc004) return;
            foreach (var page in _pages) page.Hide();
            _typing?.Release(); _typing = null;
            _ambient?.Release(); _ambient = null;
            _regions.Dispose();
            _regions = null;
            shell.Anims.Clear();
            shell.Surface.DrawAfter.Clear(); shell.Surface.DrawBefore.Clear();
            _exitSound?.Release(); _pageSound?.Release();
            shell.Unregister();
            shell.Post(ev, 0xc026);
        }
    }
}
