using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace MW2.Shell
{
    /// <summary>Office contracts 0xc024 (FUN_1000b06b / FUN_1000b5e5); CTNT text uses
    /// FUN_1006b2e3 -> FUN_100765d9 in a single text page per offer.</summary>
    public static class ContractsScreen
    {
        static readonly ShellRegions.Entry[] Spots =
        {
            new ShellRegions.Entry(401, 431, 500, 473, 0, 0, ""),
            new ShellRegions.Entry(501, 431, 602, 473, 0, 0, ""),
            new ShellRegions.Entry(36, 379, 129, 426, 0, 0, ""),
            new ShellRegions.Entry(130, 379, 227, 426, 0, 0, "")
        };
        static Mw2Shell _shell;
        static ShellRegions _regions;
        static ShellSound _click, _exit;
        static List<ContractOffers.Offer> _offers;
        static List<ShellAnim> _employerMarks;
        static ShellTextPage _offerPage;
        static ShellAnim _previous, _next, _accept;
        static int _selected;

        public static void Open(Mw2Shell shell)
        {
            _shell = shell;
            if (shell.Career.Finished239D && shell.Career.StepType == 3)
                shell.Career.Outcome239C = true;
            _offers = ContractOffers.Read(shell.Career, shell.Data, shell.TickCounter, message => shell.Dialog(message, null));
            _selected = shell.Career.Finished239D ? 0 : shell.Career.NextChoice;
            if (_offers.Count == 0) _selected = 0;
            else _selected = Mathf.Clamp(_selected, 0, _offers.Count - 1);
            shell.Anims.Clear();
            shell.Surface.LoadBackground(ShellPcx.Decode(shell.Db.Unpack(33)));
            _employerMarks = new List<ShellAnim>();
            foreach (var offer in _offers)
            {
                string employer = offer.Employer.ToUpperInvariant();
                int x = employer == "COMSTAR" ? 492 : employer == "DRACONIS" ? 402 :
                    employer == "FEDCOM" ? 383 : employer == "FREERASS" ? 424 : 0;
                int y = employer == "COMSTAR" ? 172 : employer == "DRACONIS" ? 158 :
                    employer == "FEDCOM" ? 146 : employer == "FREERASS" ? 160 : 0;
                _employerMarks.Add(employer.Length > 0 ? new ShellAnim(shell.Anims, employer, x, y, 0x61, 0) : null);
            }
            _regions = new ShellRegions(shell.Surface, shell.Font7, false, Spots, () => true);
            _next = new ShellAnim(shell.Anims, "ocbcn", 138, 391, 0x21, 0); _next.Show();
            _previous = new ShellAnim(shell.Anims, "ocbcp", 47, 389, 0x21, 0); _previous.Show();
            _accept = new ShellAnim(shell.Anims, "ocbca", 409, 440, 0x21, 0); _accept.Show();
            new ShellAnim(shell.Anims, "ocbce", 510, 440, 0x21, 0).Show();
            if (!shell.Career.Finished239D) _regions.Disable(0);
            _click = Sound(0x35); _exit = Sound(0x39);
            ShowOffer();
            shell.FlushKeys();
            shell.Register(Tick);
        }

        static ShellSound Sound(int record)
        {
            var sound = new ShellSound(_shell.gameObject, _shell.Db, record);
            sound.SetVolume(50);
            return sound;
        }

        static void ShowOffer()
        {
            _offerPage?.Hide();
            _offerPage = null;
            foreach (var mark in _employerMarks) mark?.Hide();
            if (_offers.Count == 0) return;
            var offer = _offers[_selected];
            byte[] text = ShellBriefing.ExpandNames(Encoding.ASCII.GetBytes(offer.Text), _shell.Career);
            _offerPage = new ShellTextPage(_shell.Font11, _shell.Font7, 0x2f, 0x41, 0x225, 0x11e);
            _offerPage.Layout(ShellBriefing.Latin1(text));
            _offerPage.Create(_shell.Surface, ShellSurface.MakeRemap(0x16), ShellSurface.MakeRemap(0x9d));
            _offerPage.StartTyping();
            _employerMarks[_selected]?.Show();
            _regions.Disable(2); _regions.Disable(3);
            if (_shell.Career.Finished239D)
            {
                if (_selected > 0) _regions.Enable(2);
                if (_selected + 1 < _offers.Count) _regions.Enable(3);
            }
        }

        static void Tick(uint ev)
        {
            var shell = _shell;
            if (ev == 0xc004)
            {
                shell.TickCounter++;
                shell.Anims.Step();
                _offerPage?.Advance();
                int spot = _regions.Hit(shell.MouseX, shell.MouseY);
                if (shell.MouseClicked)
                {
                    if (spot == 0 && shell.Career.AcceptContract(_selected, _offers))
                    { shell.BlockUntilEnd(_click); ev = 0xc021; }
                    else if (spot == 1) { shell.BlockUntilEnd(_exit); ev = 0xc021; }
                    else if (spot == 2 && _selected > 0) { _selected--; _click.Play(); ShowOffer(); }
                    else if (spot == 3 && _selected + 1 < _offers.Count) { _selected++; _click.Play(); ShowOffer(); }
                }
            }
            if (ev == 0xc004) return;
            shell.Career.Byte23B8 = true;
            _offerPage?.Hide(); _offerPage = null;
            foreach (var mark in _employerMarks) mark?.Hide();
            _employerMarks.Clear();
            _regions.Dispose(); _regions = null;
            shell.Anims.Clear();
            shell.Surface.DrawAfter.Clear(); shell.Surface.DrawBefore.Clear();
            _click?.Release(); _exit?.Release();
            shell.Unregister();
            shell.Post(ev, 0xc024);
        }
    }
}
