namespace MW2.Shell
{
    /// <summary>
    /// The hangar, message 0xc01c (FUN_10072619 0x10072619, screen function FUN_10072d05 0x10072d05): background record 16,
    /// animations smon (monitor) and slightb (light), sdoorab when returning from the office (0xc01a). Spots (table 0x100bb230,
    /// font 7 labels kept while hovered): 0 BOARD DROP SHIP (sdoorb, then 0xc033: the "mto" flic and the launch screen 0xc016),
    /// 1 TERMINAL (0xc026 when registered, else the registration 0xc013), 2 OFFICE (sdoora, then 0xc01a; disabled for a novice),
    /// 3 disabled. Unregistered clicks on the door or office play the "register first" sample (record 0x46). Random hangar
    /// chatter (FUN_10073465 0x10073465) plays from table 0x100bb2b0 while nothing else talks.
    /// </summary>
    public static class MainMenuScreen
    {
        static readonly ShellRegions.Entry[] Spots =
        {
            new ShellRegions.Entry(413, 256, 525, 414, 419, 265, "BOARD DROP SHIP"),
            new ShellRegions.Entry(209, 267, 319, 371, 243, 375, "TERMINAL"),
            new ShellRegions.Entry(72, 247, 165, 422, 125, 249, "OFFICE"),
            new ShellRegions.Entry(511, 105, 602, 162, 144, 133, null)
        };

        static readonly int[] Chatter =
            { 106, 107, 108, 109, 110, 111, 112, 113, 114, 115, 86, 87, 88, 89, 90, 91, 92, 93, 118, 119, 119, 119, 119, 119, 119 };

        static Mw2Shell _s;
        static ShellRegions _regions;                 // DAT_100f9ec0
        static ShellAnim _doorFromOffice, _door;      // DAT_100f9ef0, DAT_100f9ee8 / ee4
        static ShellSound _office, _ambient, _noMech, _registerLaunch, _finished, _terminal, _register, _notRegistered, _chat;
        // DAT_100f9ec8 (0x43), ec4 (0x3f), edc (0x48), ecc (0x44), ed4 (0x47), ed0 (0x54), ebc (0x45), ed8 (0x46), eb8

        public static void Open(Mw2Shell s)
        {
            _s = s;
            var c = s.Career;
            _regions = null; _doorFromOffice = null; _door = null;
            _office = _ambient = _noMech = _registerLaunch = _finished = _terminal = _register = _notRegistered = _chat = null;
            c.Byte23A2 = false;
            s.Surface.LoadBackground(ShellPcx.Decode(s.Db.Unpack(0x10)));
            c.Byte23B9 = false;
            // FUN_1004ac2c 0x1004ac2c / FUN_1004acf2 0x1004acf2: an insolvent regular career returns to title.
            if (c.Mode == 1 && !ShellEconomy.CanAffordAnyMech(c, s.Data.Tables))
            {
                UnityEngine.Debug.Log("You cannot afford even the cheapest BattleMech.");
                s.PlayFlic("mpoorw", 0xc02b, 0xc01c);
                return;
            }
            _office = Sound(0x43, 0x1e);
            if (c.Msg23C3 == 0xc01a)
            {
                _doorFromOffice = new ShellAnim(s.Anims, "sdoorab", 0x5f, 0x103, 4, 1000);
                _doorFromOffice.Show();
            }
            new ShellAnim(s.Anims, "smon", 0xef, 0x123, 8, 1000).Show();
            new ShellAnim(s.Anims, "slightb", 0x201, 0x67, 8, 1000).Show();
            s.Anims.Step();
            if (c.Registered && !c.Finished239D && c.UnitMechs.Count == 0) _noMech = Sound(0x48, 0x32);
            if (c.Registered && !c.Finished239D) _registerLaunch = Sound(0x44, 0x28);
            if (c.Finished239D) _finished = Sound(0x47, 0x28);
            _terminal = Sound(0x54, 0x14);
            if (!c.Registered)
            {
                _register = Sound(0x45, 0x32);
                _register.Play();
                _notRegistered = Sound(0x46, 0x32);
            }
            _ambient = new ShellSound(s.gameObject, s.Db, 0x3f);
            _ambient.LoopForever();
            _ambient.Play();
            s.FlushKeys();
            _regions = new ShellRegions(s.Surface, s.Font7, false, Spots, () => true);
            _regions.Disable(3);
            if (c.Mode == 0) _regions.Disable(2);
            s.Register(Tick);
            if (_doorFromOffice != null) _office.Play();
        }

        static ShellSound Sound(int record, int volume)
        {
            var snd = new ShellSound(_s.gameObject, _s.Db, record);
            snd.SetVolume(volume);
            return snd;
        }

        static void Tick(uint ev)
        {
            var s = _s;
            var c = s.Career;
            if (ev == 0xc004)
            {
                s.TickCounter++;
                s.Anims.Step();
                int spot = _regions.Hit(s.MouseX, s.MouseY);
                Chat();
                if (spot == 0 && s.MouseClicked)
                {
                    if (!c.Registered) _notRegistered?.Play();
                    else if (c.Mode != 1)
                    {
                        OpenDoor("sdoorb", 0x1b6, 0x11e, 0x44, _registerLaunch, 0xc033);
                        return;
                    }
                    else if (!c.Finished239D)
                    {
                        if (c.UnitMechs.Count != 0)
                        {
                            OpenDoor("sdoorb", 0x1b6, 0x11e, 4, _registerLaunch, 0xc033);
                            return;
                        }
                        _noMech?.Play();
                    }
                    else if (_finished != null && !_finished.Playing) _finished.Play();
                }
                else if (spot == 1 && s.MouseClicked)
                {
                    s.BlockUntilEnd(_terminal);
                    ev = c.Registered ? 0xc026u : 0xc013u;
                }
                else if (spot == 2 && s.MouseClicked)
                {
                    if (c.Registered)
                    {
                        var door = new ShellAnim(s.Anims, "sdoora", 0x5f, 0x103, 0x44, 1000);
                        _office.Play();
                        door.Show();
                        s.HoldUntilEnd(door, 0xc01a);
                        return;
                    }
                    _notRegistered?.Play();
                }
            }
            if (ev == 0xc004) return;
            _regions.Dispose();
            _regions = null;
            s.Anims.Clear();
            s.Surface.DrawAfter.Clear();
            s.Surface.DrawBefore.Clear();
            foreach (var snd in new[] { _ambient, _office, _registerLaunch, _terminal, _finished, _notRegistered, _noMech, _chat, _register })
                snd?.Release();
            s.Post(ev, 0xc01c);
            s.Unregister();
        }

        static void OpenDoor(string name, int x, int y, uint flags, ShellSound sound, uint msg)
        {
            var door = new ShellAnim(_s.Anims, name, x, y, flags, 1000);
            door.Show();
            sound?.Play();
            _s.HoldUntilEnd(door, msg);
        }

        /// <summary>FUN_10073465: while the register prompt and the last chatter are silent, start another random chatter
        /// sample (volume 5 for records below 10, else 15).</summary>
        static void Chat()
        {
            if (_register != null && _register.Playing) return;
            if (_chat != null)
            {
                if (_chat.Playing) return;
                _chat.Release();
                _chat = null;
            }
            int record = Chatter[_s.Rand() % 25];
            if (record == 1) return;
            _chat = Sound(record, record < 10 ? 5 : 15);
            _chat.Play();
        }
    }
}
