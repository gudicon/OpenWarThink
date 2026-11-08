using UnityEngine;

namespace MW2.Shell
{
    /// <summary>
    /// Registration, message 0xc013 (FUN_1005b372 0x1005b372, FUN_1005b9bb 0x1005b9bb): record 24,
    /// rcomstar and oraccp, pilot name at (330,312), unit name at (330,350). FUN_10035083 0x10035083 edits
    /// up to 28 printable characters with a 230-pixel width limit. FUN_1005b744 registers the resulting career.
    /// </summary>
    public static class RegistrationScreen
    {
        static Mw2Shell _s;
        static ShellRegions _regions;
        static ShellText _pilotText, _unitText;
        static ShellSound _accepted, _prompt, _ambient;
        static ShellAnim _button, _comstar;
        static bool _unitStage;
        static uint _pendingNext;
        static string _pilot, _unit;
        static readonly byte[] EditingRemap = ShellSurface.MakeRemap(0x11);
        static readonly byte[] FinalRemap = ShellSurface.MakeRemap(0x16);

        // Table PTR_DAT_100b8870, 0x1c per entry. The first two zones occupy the same acceptance button.
        static readonly ShellRegions.Entry[] Spots =
        {
            new ShellRegions.Entry(502, 431, 602, 472, 289, 365, ""),
            new ShellRegions.Entry(502, 431, 602, 472, 289, 365, ""),
            new ShellRegions.Entry(317, 196, 520, 224, 0, 0, "")
        };

        public static void Open(Mw2Shell shell)
        {
            _s = shell;
            _unitStage = false;
            _pendingNext = 0;
            _pilot = "";
            _unit = shell.Career.Mode == 0 ? "HANSON'S ROUGHRIDERS" : "";
            shell.Anims.Clear();
            shell.Surface.LoadBackground(ShellPcx.Decode(shell.Db.Unpack(24)));
            _accepted = Sound(0x39);
            _prompt = Sound(0x50);
            _ambient = Sound(0x3f);
            _ambient.LoopForever(); _ambient.Play();
            _button = new ShellAnim(shell.Anims, "oraccp", 510, 441, 0x21, 0); _button.Show();
            _comstar = new ShellAnim(shell.Anims, "rcomstar", 51, 84, 0x20, 0); _comstar.Show();
            _regions = new ShellRegions(shell.Surface, shell.Font7, false, Spots, () => true);
            _regions.Disable(0); _regions.Disable(1);
            shell.FlushKeys();
            _prompt.Play();
            DrawNames();
            shell.Register(Tick);
        }

        static ShellSound Sound(int record)
        {
            var snd = new ShellSound(_s.gameObject, _s.Db, record);
            snd.SetVolume(0x32);
            return snd;
        }

        static void DrawNames()
        {
            _pilotText?.Hide(); _unitText?.Hide();
            _pilotText = new ShellText(_s.Surface, _s.Font6, _pilot + (_unitStage ? "" : "_"), 330, 312,
                _unitStage ? FinalRemap : EditingRemap);
            _pilotText.Show(0);
            _unitText = new ShellText(_s.Surface, _s.Font6, _unit + (_unitStage ? "_" : ""), 330, 350,
                _unitStage ? EditingRemap : FinalRemap);
            _unitText.Show(0);
        }

        static void Tick(uint ev)
        {
            var s = _s;
            if (_pendingNext != 0) { Leave(_pendingNext); return; }
            if (ev != 0xc004) { Leave(ev); return; }
            s.TickCounter++;
            s.Anims.Step();
            int spot = _regions.Hit(s.MouseX, s.MouseY);
            bool changed = false, accepted = false;
            string typed = Input.inputString;
            foreach (char ch in typed)
            {
                if (ch == '\b')
                {
                    if (_unitStage && _unit.Length > 0) { _unit = _unit.Substring(0, _unit.Length - 1); changed = true; }
                    else if (!_unitStage && _pilot.Length > 0) { _pilot = _pilot.Substring(0, _pilot.Length - 1); changed = true; }
                }
                else if (ch == '\n' || ch == '\r') accepted = true;
                else if (ch > 31 && ch < 128 && ch != '~')
                {
                    string value = _unitStage ? _unit : _pilot;
                    if (value.Length < 28 && s.Font6.Width(value + ch + "_") < 230)
                    {
                        if (_unitStage) _unit += ch; else _pilot += ch;
                        changed = true;
                    }
                }
            }
            if (Input.GetKeyDown(KeyCode.Escape))
            {
                if (_unitStage) _unit = s.Career.Mode == 0 ? "HANSON'S ROUGHRIDERS" : "";
                else _pilot = "";
                changed = true;
            }
            if (s.MouseClicked && spot == 1) accepted = true;
            if (!_unitStage && accepted && _pilot.Length > 0)
            {
                _pilot = _pilot.ToUpperInvariant();
                _unitStage = true;
                _regions.Enable(1);
                changed = true;
            }
            else if (_unitStage && accepted)
            {
                _unit = (_unit.Length == 0 && s.Career.Mode == 0 ? "HANSON'S ROUGHRIDERS" : _unit).ToUpperInvariant();
                _button.SetFrame(1);
                s.Anims.Step();
                _pendingNext = s.Career.Register(_pilot, _unit);
                if (s.Career.Mode == 0 || s.Career.Mode == 1) s.Menu.Enable(ShellMenu.Retire, true);     // FUN_1005b744
                s.BlockUntilEnd(_accepted);
                return;
            }
            if (changed) DrawNames();
        }

        static void Leave(uint msg)
        {
            _pilotText?.Hide(); _unitText?.Hide();
            _pilotText = _unitText = null;
            _regions?.Dispose(); _regions = null;
            _s.Anims.Clear();
            _s.Surface.DrawAfter.Clear(); _s.Surface.DrawBefore.Clear();
            _accepted?.Release(); _prompt?.Release(); _ambient?.Release();
            _s.Post(msg, 0xc013);
            _s.Unregister();
        }
    }
}
