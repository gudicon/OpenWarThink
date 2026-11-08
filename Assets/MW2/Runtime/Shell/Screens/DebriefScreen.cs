using UnityEngine;

namespace MW2.Shell
{
    /// <summary>
    /// Debriefing 0xc00a (Debrief_Open 0x1001ede1, screen function Debrief_Tick 0x100206bb). The statistics of MW2CAR.CFG
    /// go to state +0x2e0f, MW2MSN.CFG to +0x243b; an ejection first plays "meject" (a regular career then loses the mech,
    /// FUN_1001dd8d), a death "mdeath" (novice: then the screen, regular: the title). The page at (46, 38) 558 x 355 in
    /// font 11, colour 1 -> 0x16, carries the xxxxDBFS (won) or xxxxDBFF text with the objectives table at \q and, in a
    /// regular career, the funds, deaths and salvage notes after it (Debrief_Regular 0x1001e89a); novice careers use
    /// FUN_1001e6ba 0x1001e6ba. Background 38 is loaded on the first tick. Spots: 2 the other page, 3 continue (lbcsp:
    /// hangar 0xc01c, lbbn: briefing 0xc012), 4 replay (briefing), 6 salvage 0xc039. Leaving runs the bankruptcy check
    /// FUN_100203df, the end of the career FUN_100205d0, Unit_TooDamaged 0x100202e9, the landing flic FUN_1002045f and the
    /// campaign advance FUN_1001d1d3. Career endings record high scores via FUN_1002eba2; the units' written MEKs and
    /// salvage.mw2 do not exist yet (the engine does not write them), so no deaths, salvage notes or salvage screen follow.
    /// </summary>
    public static class DebriefScreen
    {
        static readonly ShellRegions.Entry[] Spots =
        {
            new ShellRegions.Entry(400, 314, 600, 359, 0, 0, ""),
            new ShellRegions.Entry(400, 360, 600, 405, 0, 0, ""),
            new ShellRegions.Entry(50, 416, 220, 470, 0, 0, ""),
            new ShellRegions.Entry(451, 423, 616, 471, 0, 0, ""),
            new ShellRegions.Entry(451, 423, 597, 471, 0, 0, ""),
            new ShellRegions.Entry(504, 423, 600, 471, 0, 0, ""),
            new ShellRegions.Entry(251, 423, 421, 471, 0, 0, "")
        };
        static readonly string[] Planets =      // PTR_s_mcc1SCN1_100ab918
        {
            "mcc1SCN1", "bet1SCN1", "spa1SCN1", "iva1SCN1", "gra1SCN1", "sol1scn1", "sig1SCN1", "wol1scn1", "Out1SCN1",
            "lut1scn1", "big1scn1", "buz1scn1", "dom1scn1", "dou1scn1", "dow2scn1", "myb1scn1", "rec1scn1", "rev1scn1",
            "rog1scn1", "sur1scn1", "wou1scn1", "med1scn1", "mou1scn1"
        };

        static Mw2Shell _s;
        static Debriefing _d;
        static ShellRegions _regions;
        static ShellSound _click, _ambient;                                 // DAT_100caeac, DAT_100caeb0
        static ShellAnim _firstButton, _secondButton, _hangar, _briefing, _replay, _back, _salvage, _picture, _continue;
        static ShellTextPage _page0, _page1;                                // DAT_100caea0, DAT_100cae9c
        static bool _second, _firstTick, _salvageAvailable, _unregister;    // DAT_100cae98, DAT_100a1c68, DAT_100caeb4, DAT_100a1c54
        static int _state;                                                  // DAT_100c8f88: report +0x10
        /// <summary>DAT_100caebc: the ejection / death flic of this debriefing was shown (the screen opens again after it).</summary>
        static bool _flicShown;
        /// <summary>DAT_100caeb8: set by the end-of-career path (Debrief_Open with param 3), cleared by Title_Open.</summary>
        public static bool CareerEnded;

        public static void Open(Mw2Shell s)
        {
            _s = s;
            var c = s.Career;
            s.Menu.Enable(ShellMenu.Retire, true);      // Debrief_Open 0x1001ede1 with 0
            _unregister = true;
            _second = false;
            _salvageAvailable = false;
            _firstButton = _secondButton = _hangar = _briefing = _replay = _back = _salvage = _picture = _continue = null;
            _page0 = _page1 = null;
            s.Anims.Clear();
            s.FlushKeys();
            byte[] combat = s.CombatCareerFile;
            if (combat != null) c.LoadCombatCareer(combat);
            else combat = new byte[0x50];
            int salvageCount = 0;           // DAT_100c99d0: salvage.mw2 +0
            bool noOpportunity = true;      // DAT_100c994c
            if (s.SalvageFile != null && s.SalvageFile.Length >= 4 && System.BitConverter.ToInt32(s.SalvageFile, 0) > 0)
            {
                salvageCount = System.BitConverter.ToInt32(s.SalvageFile, 0);
                noOpportunity = false;
                _salvageAvailable = true;
            }
            c.SetI32(0x156, 0);
            _d = new Debriefing(c, s.Data.Tables, s.Font11);
            if (!c.Byte23B7)
            {
                int outcome = combat[0x1d];
                if (outcome == 2)
                {
                    // FUN_1001e5ae 0x1001e5ae
                    if (!_flicShown)
                    {
                        _flicShown = true;
                        s.PlayFlic("meject", 0xc00a, 0xc00a);
                        return;
                    }
                    _flicShown = false;
                    if (c.Mode == 1) _d.LoseEjectedMech();
                }
                else if (outcome == 4)
                {
                    // FUN_1001e60f 0x1001e60f
                    if (c.Mode == 0)
                    {
                        if (!_flicShown)
                        {
                            _flicShown = true;
                            s.PlayFlic("mdeath", 0xc00a, 0xc00a);
                            return;
                        }
                        _flicShown = false;
                    }
                    else if (c.Mode == 1)
                    {
                        s.PlayFlic("mdeath", 0xc02b, 0xc02b);
                        return;
                    }
                }
            }
            byte[] reportBytes = s.MissionReportFile;
            ShellMissionReport report = null;
            if (reportBytes != null)
            {
                report = new ShellMissionReport(reportBytes);
                c.LoadMissionReport(report);
            }
            else
            {
                // No report from a mission: the original keeps the static buffer DAT_100c8f78 (zero at start).
                var blank = new byte[ShellMissionReport.Size];
                System.Text.Encoding.ASCII.GetBytes("MW2M", 0, 4, blank, 0);
                report = new ShellMissionReport(blank);
            }
            _state = report.TeamByte38;
            string objectives = _d.Objectives(report, combat);
            string mission = c.Mission;
            string resource = ShellBriefing.ResourceName(mission).Substring(0, 4) + (_state == 2 ? "DBFS" : "DBFF");
            var pages = ShellBriefing.BuildPages(s.Data, resource, c, objectives, s.Font11, s.Font7, 0x2e, 0x26, 0x22e, 0x163);
            _page0 = pages.Count > 0 ? pages[0] : new ShellTextPage(s.Font11, s.Font7, 0x2e, 0x26, 0x22e, 0x163);
            _page1 = pages.Count > 1 ? pages[1] : new ShellTextPage(s.Font11, s.Font7, 0x2e, 0x26, 0x22e, 0x163);
            _click = Sound(0x35, 0x32);
            _ambient = Sound(0x40, 0x1e); _ambient.LoopForever(); _ambient.Play();
            _secondButton = new ShellAnim(s.Anims, "lbbdb", 0x2d, 0x1a6, 0x21, 1000);
            _firstButton = new ShellAnim(s.Anims, "lbba", 0x2d, 0x1a6, 0x21, 1000);
            if (c.Mode == 1 && c.StepType >= 2 && c.StepType <= 4)
            {
                string key = (mission.Length > 3 ? mission.Substring(0, 3) : mission) + "1scn1";
                for (int i = 0; i < Planets.Length; i++)
                {
                    if (!string.Equals(Planets[i], key, System.StringComparison.OrdinalIgnoreCase)) continue;
                    c.SetI32(0x23d3 + i * 4, 1);
                    if (string.Equals(key, "lut1scn1", System.StringComparison.OrdinalIgnoreCase))
                    {
                        c.SetI32(0x242f, 1);
                        if (_state == 2)
                        {
                            c.Funds += c.SuccessPay;
                            c.UnitExpenses(true);
                            _d.RemoveDead();
                        }
                    }
                }
            }
            if (c.Mode == 1)
            {
                bool allowed = Debriefing.SalvageAllowed(mission);
                noOpportunity = !allowed;
                if (!allowed && _salvageAvailable && !CareerEnded) _salvageAvailable = false;
                if (c.I32(0x2433) != 0 && _state == 2) _salvageAvailable = true;
                if (salvageCount == 0) _salvageAvailable = false;
                if (mission.Length > 3 && string.Equals(mission.Substring(0, 3), "out", System.StringComparison.OrdinalIgnoreCase) &&
                    mission[3] == '3' && !s.SalvageOpened) _salvageAvailable = true;
                if (_salvageAvailable && _state == 2) _salvage = new ShellAnim(s.Anims, "salv", 0xfa, 0x1a6, 1, 1000);
            }
            else _salvageAvailable = false;
            _hangar = new ShellAnim(s.Anims, "lbcsp", 0x1c3, 0x1b1, 0x21, 1000);
            _briefing = new ShellAnim(s.Anims, "lbbn", 0x1c3, 0x1a7, 0x21, 1000);
            _replay = new ShellAnim(s.Anims, "lbrp", 0x1c3, 0x1b1, 0x21, 1000);
            _back = new ShellAnim(s.Anims, "lbbe", 0x1fe, 0x1b8, 0x21, 1000);
            _regions = new ShellRegions(s.Surface, s.Font7, false, Spots, () => true);
            _regions.Disable(5);
            if (c.Mode == 0) Novice();
            else if (c.Mode == 1) Regular(salvageCount, noOpportunity);
            // Debrief_Open: T, O, B and Y held together add 50,000,000 C-bills.
            if (Input.GetKey(KeyCode.T) && Input.GetKey(KeyCode.O) && Input.GetKey(KeyCode.B) && Input.GetKey(KeyCode.Y))
                c.Funds += 50000000;
            if (!_salvageAvailable || _state != 2)
            {
                _regions.Disable(6);
                _salvage?.Hide();
            }
            var remap = ShellSurface.MakeRemap(0x16);      // DAT_100ccfb0
            var large = ShellSurface.MakeRemap(0x11);      // DAT_100cceb0
            foreach (var p in new[] { _page0, _page1 })
            {
                p.Create(s.Surface, remap, large);
                p.TypeSound = Sound(0x32, -1);
                p.TypeSound.LoopForever();
            }
            _firstTick = true;
            s.Register(Tick);
        }

        static ShellSound Sound(int record, int volume)
        {
            var snd = new ShellSound(_s.gameObject, _s.Db, record);
            if (volume >= 0) snd.SetVolume(volume);
            return snd;
        }

        /// <summary>FUN_1001e6ba 0x1001e6ba (novice career).</summary>
        static void Novice()
        {
            var c = _s.Career;
            if (!c.Byte23B7) _d.RestoreFromCopies();
            if (_state == 2)
            {
                c.Outcome239C = true;
                _regions.Disable(4);
                _replay.Hide();
            }
            else
            {
                c.Outcome239C = false;
                _regions.Enable(4);
                _replay.Show();
                _continue = _replay;
            }
            switch (c.StepType)
            {
                case 1:
                    if (!c.Byte23B7) c.Byte23A2 = true;
                    if (_continue == null) _continue = _briefing;
                    break;
                case 2: case 3:
                    if (!c.Byte23B7) { c.Finished239D = false; c.Byte23A2 = false; }
                    if (_continue == null) _continue = _hangar;
                    break;
                case 4:
                    _continue = _hangar;
                    break;
                default:
                    _s.Dialog($"Novice Mode: Invalid Campaign Type {c.StepType}#Ok", null);
                    break;
            }
            _regions.Disable(0);
            _continue?.Show();
            _firstButton.Show();
        }

        /// <summary>Debrief_Regular 0x1001e89a (regular career).</summary>
        static void Regular(int salvageCount, bool noOpportunity)
        {
            var s = _s;
            var c = s.Career;
            string text = "";
            if (_state == 2)
            {
                // FUN_1001b550 always answers 0, so a won mission always pays the success compensation.
                c.Outcome239C = true;
                _d.Compensation = c.SuccessPay;
                text += _d.Funds();
                _d.RollSalvage(s.Rand);
                text += _d.SalvageNote(s.SalvageFile != null, noOpportunity, salvageCount);
            }
            else if (_state == 0 || _state == 3 || _state == 4)
            {
                c.Outcome239C = false;
                _d.Compensation = c.FailPay;
                text += _d.Funds();
            }
            else s.Dialog($"Regular mode: Unknown mission outcome {c.StepType}#Ok", null);
            text += _d.Deaths(null);
            _page0.Layout(ShellBriefing.Latin1(ShellBriefing.ExpandNames(System.Text.Encoding.ASCII.GetBytes(text), c)));
            switch (c.StepType)
            {
                case 1:
                    if (!c.Byte23B7) c.Byte23A2 = true;
                    _continue = c.UnitMechs.Count == 0 ? _hangar : _briefing;
                    break;
                case 2:
                    if (!c.Byte23B7) { c.Finished239D = true; c.Byte23A2 = false; }
                    _continue = _hangar;
                    break;
                case 3:
                    if (c.Outcome239C) _continue = _briefing;
                    else
                    {
                        if (!c.Byte23B7) { c.Finished239D = true; c.Byte23A2 = false; }
                        _continue = _hangar;
                    }
                    break;
                case 4:
                    _continue = _hangar;
                    break;
                default:
                    s.Dialog($"Regular Mode: Invalid Campaign Type {c.StepType}#Ok", null);
                    break;
            }
            _regions.Disable(4);
            _continue?.Show();
            _firstButton.Show();
            string picture = c.Str(0x1b6, 0x10);
            if (picture.Length > 0)
            {
                _picture = new ShellAnim(s.Anims, picture, 0x163, 0x16, 0x68, 1000);
                _picture.Show();
            }
        }

        static void Tick(uint ev)
        {
            var s = _s;
            var c = s.Career;
            if (ev == 0xc004)
            {
                if (_firstTick)
                {
                    _firstTick = false;
                    s.Surface.LoadBackground(ShellPcx.Decode(s.Db.Unpack(0x26)));
                    _page0.StartTyping();
                }
                s.Anims.Step();
                if (_second) _page1?.Advance();
                else _page0?.Advance();
                int spot = _regions.Hit(s.MouseX, s.MouseY);
                if (s.MouseClicked)
                    switch (spot)
                    {
                        case 2:
                            if (_page1 == null) break;
                            if (!_second)
                            {
                                _firstButton.SetFrame(1);
                                s.Anims.Step();
                                s.BlockUntilEnd(_click);
                                _firstButton.Hide();
                                _secondButton.SetFrame(0);
                                _secondButton.Show();
                                _page0.Hide();
                                _page1.StartTyping();
                            }
                            else
                            {
                                _secondButton.SetFrame(1);
                                s.Anims.Step();
                                s.BlockUntilEnd(_click);
                                _secondButton.Hide();
                                _firstButton.SetFrame(0);
                                _firstButton.Show();
                                _page1.Hide();
                                _page0.StartTyping();
                            }
                            _second = !_second;
                            break;
                        case 3:
                            _continue?.SetFrame(1);
                            s.Anims.Step();
                            s.BlockUntilEnd(_click);
                            if (_continue == _hangar) ev = 0xc01c;
                            else if (_continue == _briefing) ev = 0xc012;
                            break;
                        case 4:
                            _continue?.SetFrame(1);
                            s.Anims.Step();
                            s.BlockUntilEnd(_click);
                            ev = 0xc012;
                            break;
                        case 6:
                            if (!_salvageAvailable) break;
                            _salvage?.SetFrame(1);
                            s.Anims.Step();
                            ev = 0xc039;
                            break;
                    }
            }
            if (ev != 0xc004) Leave(ev);
        }

        static void Leave(uint ev)
        {
            var s = _s;
            var c = s.Career;
            _regions.Dispose(); _regions = null;
            s.Anims.Clear();
            _page0?.Hide(); _page1?.Hide();
            _page0?.TypeSound?.Release(); _page1?.TypeSound?.Release();
            _click?.Release(); _ambient?.Release();
            if (ev != 0xc039 && ev != 0xc02e) s.SalvageFile = null;      // remove("salvage.mw2")
            s.Surface.DrawAfter.Clear(); s.Surface.DrawBefore.Clear();
            if (ev == 0xc02e || ev == 0xc002 || ev == 0xc039)
            {
                c.Byte23B7 = true;
                c.Byte23B8 = false;
                s.Post(ev, 0xc00a);
            }
            else if (!Bankrupt() && !EndOfCareer(ev) && !TooDamaged())
            {
                Landing(ref ev);
                uint advance = _d.Advance(s.Data);
                if (advance != 0) s.Post(advance, 0xc00a);
                else if (!s.FlicRunning) s.Post(ev, 0xc00a);
            }
            if (_unregister) s.Unregister();
            c.Byte23B7 = true;
            c.Byte23B8 = false;
        }

        /// <summary>FUN_100203df 0x100203df: a regular career whose balance after this mission is negative disbands.</summary>
        static bool Bankrupt()
        {
            var c = _s.Career;
            int outcome = c.Data[0x2e0f + 0x1d];
            if (c.Mode != 1 || outcome == 4 || c.Funds + _d.Income - c.UnitExpenses(false) >= 0) return false;
            _s.Dialog("You don't have enough funds to maintain your lance|Your unit disbands in disgrace!#Ok", null);
            _s.RecordHighScore();
            _unregister = false;
            _s.PlayFlic("mpoorw", 0xc02b, 0xc00a);
            return true;
        }

        /// <summary>FUN_100205d0 0x100205d0: the last step of a career (type 4).</summary>
        static bool EndOfCareer(uint ev)
        {
            var c = _s.Career;
            if (c.Mode == 0)
            {
                if (c.StepType != 4) return false;
                if (c.Outcome239C)
                {
                    _unregister = false;
                    _s.PlayFlic("mend", 0xc02b, 0xc00a);
                    return true;
                }
                _s.Post(ev, 0xc00a);
                return false;
            }
            if (c.Mode == 1)
            {
                if (c.StepType != 4) return false;
                _s.RecordHighScore();
                _unregister = false;
                _s.PlayFlic("mend", 0xc03a, 0xc00a);
                return true;
            }
            _s.Post(ev, 0xc00a);
            return false;
        }

        /// <summary>Unit_TooDamaged 0x100202e9.</summary>
        static bool TooDamaged()
        {
            var s = _s;
            var c = s.Career;
            if (c.Mode != 1 || c.Data[0x2e0f + 0x1d] == 4 || c.StepType == 4 || c.UnitMechs.Count != 0) return false;
            uint next = c.ResolveDestroyedUnit(c.Data[0x2e0f + 0x1d], s.Data);
            if (next == 0xc01c) s.Dialog("Your unit is too damaged to continue|Contract terminated!#Ok", null);
            s.Post(next == 0 ? 0xc01cu : next, 0xc00a);
            return true;
        }

        /// <summary>FUN_1002045f 0x1002045f: the landing flic after the last mission of a contract.</summary>
        static void Landing(ref uint ev)
        {
            var c = _s.Career;
            bool lastMission = c.StepType > 1 && c.StepType < 4;
            if (c.Mode == 1)
            {
                if (!lastMission) return;
                if (string.Equals(c.FailStep(0), "DEATH", System.StringComparison.OrdinalIgnoreCase) && !c.Outcome239C)
                {
                    ev = 0xc02b;
                    return;
                }
                if (Mw2Shell.MoviesOn != 1) { _s.Post(ev, 0xc00a); return; }
                _unregister = false;
                if (ev != 0xc03b) _s.PlayFlic("mland", ev, 0xc00a);
                return;
            }
            if (c.Mode != 0) return;
            if (lastMission && c.Outcome239C)
            {
                if (Mw2Shell.MoviesOn == 1)
                {
                    _unregister = false;
                    if (ev != 0xc03b) _s.PlayFlic("mland", ev, 0xc00a);
                }
                else _s.Post(ev, 0xc00a);
            }
        }
    }
}
