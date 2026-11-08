using UnityEngine;

namespace MW2.Shell
{
    /// <summary>Mission command aboard the drop ship, 0xc036 (FUN_100229fa / FUN_10023415).</summary>
    public static class DropShipScreen
    {
        static readonly ShellRegions.Entry[] Spots =
        {
            new ShellRegions.Entry(13, 7, 238, 97, 0, 0, ""),
            new ShellRegions.Entry(13, 98, 238, 191, 0, 0, ""),
            new ShellRegions.Entry(13, 192, 238, 286, 0, 0, ""),
            new ShellRegions.Entry(13, 287, 238, 378, 0, 0, ""),
            new ShellRegions.Entry(13, 379, 238, 469, 0, 0, ""),
            new ShellRegions.Entry(13, 98, 238, 191, 0, 0, ""),
            new ShellRegions.Entry(13, 192, 238, 286, 0, 0, ""),
            new ShellRegions.Entry(13, 287, 238, 378, 0, 0, ""),
            new ShellRegions.Entry(512, 426, 618, 471, 0, 0, "")
        };
        static Mw2Shell _s;
        static ShellRegions _regions;
        static ShellSound _exit, _select, _ambient;
        static ShellAnim[] _buttons;
        static ShellAnim _back;
        static int _hover;

        public static void Open(Mw2Shell s)
        {
            _s = s;
            _hover = -1;
            s.Anims.Clear();
            s.Surface.LoadBackground(ShellPcx.Decode(s.Db.Unpack(44)));
            _exit = Sound(0x39); _select = Sound(0x35); _ambient = Sound(0x40);
            _ambient.LoopForever(); _ambient.Play();
            new ShellAnim(s.Anims, "lship", 350, 115, 0x28, 0).Show();
            _back = new ShellAnim(s.Anims, "dsbe", 525, 437, 0x21, 0); _back.Show();
            _buttons = new[]
            {
                new ShellAnim(s.Anims, "dsbf", 19, 11, 0x21, 0),
                new ShellAnim(s.Anims, "dsbi", 19, 104, 0x21, 0),
                new ShellAnim(s.Anims, "dsbd", 19, 198, 0x21, 0),
                new ShellAnim(s.Anims, "dsbr", 19, 292, 0x21, 0),
                new ShellAnim(s.Anims, "dsbm", 19, 385, 0x21, 0)
            };
            foreach (var button in _buttons) button.Show();
            if (s.Career.Mode == 1)
                new ShellAnim(s.Anims, "cashboxc", 474, 0, 0x21, 0).Show();
            else
            {
                new ShellAnim(s.Anims, "dsnmlab", 19, 104, 0x21, 0).Show();
                new ShellAnim(s.Anims, "dsnduty", 19, 198, 0x21, 0).Show();
                new ShellAnim(s.Anims, "dsnlnch", 19, 292, 0x21, 0).Show();
            }
            _regions = new ShellRegions(s.Surface, s.Font7, false, Spots, () => true);
            if (s.Career.Mode == 0)
            {
                foreach (int spot in new[] { 0, 2, 3, 4 }) _regions.Disable(spot);
            }
            else
            {
                foreach (int spot in new[] { 5, 6, 7 }) _regions.Disable(spot);
            }
            s.Career.LoadBriefingCapacity(s.Data);
            s.Career.TrimDropShipCrew();
            s.Register(Tick);
        }

        static ShellSound Sound(int record)
        {
            var snd = new ShellSound(_s.gameObject, _s.Db, record);
            snd.SetVolume(30);
            return snd;
        }

        static void Tick(uint ev)
        {
            var s = _s;
            if (ev == 0xc004)
            {
                s.TickCounter++;
                s.Anims.Step();
                int spot = _regions.Hit(s.MouseX, s.MouseY);
                if (spot != _hover)
                {
                    if (_hover >= 0 && _hover < 5) _buttons[_hover].SetFrame(0);
                    _hover = spot;
                    if (spot >= 0 && spot < 5) _buttons[spot].SetFrame(1);
                }
                if (s.MouseClicked)
                {
                    switch (spot)
                    {
                        case 0: ev = 0xc020; break;
                        case 1: ev = 0xc01f; break;
                        case 2: case 6: ev = 0xc025; break;
                        case 3: ev = 0xc028; break;
                        case 4:
                            if (!s.Career.PlayerHasMech()) Debug.Log("Assign yourself a mech on the duty roster.");
                            else if (s.Career.CrewCountForLaunch() > s.Career.DropShipSlots)
                                Debug.Log($"You can assign only {s.Career.DropShipSlots} starmates.");
                            else ev = 0xc012;
                            break;
                        case 5:
                            if (!s.Career.PlayerHasMech()) Debug.Log("Assign yourself a mech on the duty roster.");
                            else ev = 0xc02f;
                            break;
                        case 7: ev = 0xc012; break;
                        case 8: _back.SetFrame(1); s.Anims.Step(); ev = 0xc016; break;
                    }
                    if (ev != 0xc004) s.BlockUntilEnd(ev == 0xc016 ? _exit : _select);
                }
            }
            if (ev == 0xc004) return;
            _regions.Dispose(); _regions = null;
            s.Anims.Clear();
            s.Surface.DrawAfter.Clear(); s.Surface.DrawBefore.Clear();
            _exit?.Release(); _select?.Release(); _ambient?.Release();
            s.Post(ev, 0xc036);
            s.Unregister();
        }
    }
}
