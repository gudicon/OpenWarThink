using System;
using UnityEngine;

namespace MW2.Shell
{
    /// <summary>
    /// Instant action 0xc029 (open FUN_1005375c 0x1005375c, screen function FUN_1005475d 0x1005475d; the state is
    /// InstantAction). Background 3 of SHELLFIX.MW2. salvage.mw2 is dropped; unless coming back from the mission settings
    /// 0xc031 the generator's parameters are cleared (mw2rmg.cfg is read only to be cleared, as in the original); then
    /// srand(time) and a new random mission "ran0" (date -1: a random year, pay 0), its world renamed randwld1; both
    /// rosters get 3 slots and none in use, and the six points are applied (FUN_10053192). Sounds 0x37 (looped, 0x1e),
    /// 0x35 click, 0x39 exit. Buttons iaaprev / iaanext (0x1b5,0x110 / 0x210,0x113: variants; 0x1b5,0xd0 / 0x210,0xd4:
    /// chassis), iaaexit, iaamisp (mission settings), iaalnch (launch), iaamlab (MechLab), "mlbd"; the chassis picture
    /// "a&lt;code&gt;" at (0x1b9, 0x1a). The list of the six points (FUN_10052cd0, font 7 at x 32 from y 54, colour 1 → 0x16,
    /// the selected 0x11): "Point %d: Mech: %s (%d T)", "NO MECH ASSIGNED" or "RANDOM MECH". Spots (table 0x100b7740):
    /// 0 / 1 previous / next variant, 2 / 3 previous / next chassis, 4 exit to the title, 5 launch (the player's mech and an
    /// enemy needed), 6 MechLab, 7 mission settings, 8 delete the selected user variant. Leaving writes mw2rmg.cfg.
    /// </summary>
    public static class InstantActionScreen
    {
        static readonly ShellRegions.Entry[] Spots =
        {
            new ShellRegions.Entry(428, 262, 522, 317, 0, 0, ""), new ShellRegions.Entry(523, 262, 618, 317, 0, 0, ""),
            new ShellRegions.Entry(428, 202, 522, 241, 0, 0, ""), new ShellRegions.Entry(523, 202, 618, 241, 0, 0, ""),
            new ShellRegions.Entry(499, 432, 605, 473, 0, 0, ""), new ShellRegions.Entry(427, 380, 576, 430, 0, 0, ""),
            new ShellRegions.Entry(16, 433, 123, 471, 0, 0, ""), new ShellRegions.Entry(143, 431, 359, 473, 0, 0, ""),
            new ShellRegions.Entry(471, 330, 578, 373, 0, 0, "")
        };
        const int TwoBelow = unchecked((int)0x80000022);

        static Mw2Shell _s;
        static ShellRegions _regions;
        static ShellSound _ambient, _click, _exit;
        static ShellAnim _prevVariant, _nextVariant, _prevChassis, _nextChassis, _exitButton, _settings, _launch, _lab, _picture;
        static ShellItemList _list;
        static byte[] _normal, _bright;
        static bool _closed;

        public static void Open(Mw2Shell s)
        {
            _s = s;
            var c = s.Career;
            _closed = false;
            InstantAction.Init(s.Data.Tables);
            s.SalvageFile = null;
            _normal = ShellSurface.MakeRemap(0x16);
            _bright = ShellSurface.MakeRemap(0x11);
            s.Anims.Clear();
            _picture = null;
            _list = List();
            _list.Init();
            InstantAction.Enter(c.Msg23C3, s.Data, c);
            s.Surface.LoadBackground(ShellPcx.Decode(s.Fix.Unpack(3)));
            if (c.Msg23C3 != 0xc031)
            {
                bool loaded = InstantAction.LoadParams(s.GameFolder);
                if (!loaded && c.Msg23C3 != 0xc02b) s.Dialog("Warning: Could not load RMG config#Ok", null);
                Array.Clear(InstantAction.Params.Data, 0, RandomMissionParams.Size);
            }
            // srand(time(NULL)), then FUN_1004f490 with date -1 and no pay.
            s.Data.Random.Srand((uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            var p = InstantAction.Params;
            p.Date = -1;
            p.FailPay = 0;
            p.SuccessPay = 0;
            var gen = new RandomMissionGenerator(s.Data.Tables, s.Data.Random.Rand, s.Data.ReadRmg);
            int result = gen.Generate(p);
            foreach (var kv in gen.Files) s.Data.Files[kv.Key] = kv.Value;
            s.Data.RmgLog = gen.Log.ToString();
            string message = RandomMissionGenerator.ErrorText(result, s.Data.Tables);
            if (message != null) s.Dialog(message, null);
            Career.RenameWorld("RAN0", s.Data);
            InstantAction.Select(0, -1, 3, 0);
            InstantAction.Select(1, -1, 3, 0);
            _list.ClearStates();
            _list.Entries[InstantAction.Selected].State = 1;
            InstantAction.ApplyAll(s.Data);
            ShowPicture(Code(InstantAction.Chassis[InstantAction.Selected]));
            _ambient = Sound(0x37, 0x1e);
            _ambient.LoopForever();
            _ambient.Play();
            _click = Sound(0x35, 0x32);
            _exit = Sound(0x39, 0x32);
            _prevVariant = Button("iaaprev", 0x1b5, 0x110, 0x21);
            _nextVariant = Button("iaanext", 0x210, 0x113, 0x21);
            _prevChassis = Button("iaaprev", 0x1b5, 0xd0, 0x21);
            _nextChassis = Button("iaanext", 0x210, 0xd4, 0x21);
            _exitButton = Button("iaaexit", 0x1fe, 0x1b8, 0x21);
            _settings = Button("iaamisp", 0x9d, 0x1b8, 0x21);
            _launch = Button("iaalnch", 0x1ac, 0x189, 0x21);
            _lab = Button("iaamlab", 0x19, 0x1b8, 0x21);
            Button("mlbd", 0x1e3, 0x154, 0x61);
            _regions = new ShellRegions(s.Surface, s.Font7, false, Spots, () => true);
            s.Register(Tick);
            _list.Refresh();
        }

        static ShellSound Sound(int record, int volume)
        {
            var snd = new ShellSound(_s.gameObject, _s.Db, record);
            snd.SetVolume(volume);
            return snd;
        }

        static ShellAnim Button(string name, int x, int y, uint flags)
        {
            var a = new ShellAnim(_s.Anims, name, x, y, flags, 0);
            a.Show();
            return a;
        }

        static string Code(int chassis) => chassis >= 0 ? _s.Data.Tables.MechCode(chassis) : "";

        /// <summary>FUN_10053075 0x10053075: the chassis picture "a&lt;code&gt;" at (0x1b9, 0x1a), flags 0x68; none hides it.</summary>
        static void ShowPicture(string code)
        {
            if (code == null) return;
            if (code.Length == 0) { _picture?.Hide(); return; }
            if (_picture != null) { _picture.Hide(); _picture.Destroy(); }
            _picture = new ShellAnim(_s.Anims, "a" + code, 0x1b9, 0x1a, 0x68, 0);
            _picture.Show();
        }

        static ShellItemList List()
        {
            var list = new ShellItemList();
            for (int i = 0; i < InstantAction.Points; i++)
                list.Entries.Add(new ShellItemList.Entry
                {
                    X = 0x20, Y = i == 0 ? 0x36 : i == 3 ? 0x10c : TwoBelow, W = 0xc8, H = 0x15, Remap = ShellSurface.MakeRemap(0x16),
                    Data = i, Draw = DrawPoint, Click = PickPoint
                });
            return list;
        }

        /// <summary>FUN_10052cd0 0x10052cd0 (each drawing of a variant point loads its MEK again).</summary>
        static ShellText DrawPoint(ShellItemList.Entry e)
        {
            var t = _s.Data.Tables;
            int p = e.Data, c = InstantAction.Chassis[p];
            string text;
            if (c < 0) text = c == -1 ? $"Point {p}: NO MECH ASSIGNED" : $"Point {p}: RANDOM MECH";
            else if (InstantAction.VariantOn[p] && InstantAction.Variants[p].Count > 0)
            {
                var m = InstantAction.FromMek(_s.Data, InstantAction.Variants[p][InstantAction.VariantIndex[p]], _s.Career);
                InstantAction.VariantMech[p] = m;
                text = m == null ? $"Point {p}: Mech: {t.MechName(c)} ({t.Mech(c).Tons} T)"
                                 : $"Point {p}: Mech: {m.Str(0x100, 0x3a)} ({m.I32(0x204) / 100} T)";
            }
            else text = $"Point {p}: Mech: {t.MechName(c)} ({t.Mech(c).Tons} T)";
            var shown = new ShellText(_s.Surface, _s.Font7, text, e.X, e.Y, e.State == 1 ? _bright : e.Remap);
            shown.Show();
            return shown;
        }

        /// <summary>FUN_1005302a 0x1005302a: the point becomes the selected one and its chassis is shown.</summary>
        static void PickPoint(ShellItemList.Entry e)
        {
            InstantAction.Selected = e.Data;
            ShowPicture(Code(InstantAction.Chassis[e.Data]));
            e.State = 2;
        }

        static ShellItemList.Entry SelectedEntry()
        {
            foreach (var e in _list.Entries) if (e.State > 0) return e;
            return null;
        }

        static void Press(ShellAnim button, ShellSound sound, bool wait)
        {
            button.SetFrame(1);
            _s.Anims.Step();
            if (wait) _s.BlockUntilEnd(sound); else sound.Play();
            button.SetFrame(0);
        }

        static void Tick(uint ev)
        {
            var s = _s;
            if (ev == 0xc004)
            {
                s.Anims.Step();
                bool clicked = s.MouseClicked;
                if (clicked)
                {
                    var hit = _list.Hit(s.MouseX, s.MouseY);
                    if (hit != null)
                    {
                        hit.Click(hit);
                        _list.Refresh();
                    }
                }
                int spot = clicked ? _regions.Hit(s.MouseX, s.MouseY) : -1;
                ShellItemList.Entry sel;
                switch (spot)
                {
                    case 0:
                    case 1:
                        sel = SelectedEntry();
                        if (sel == null || InstantAction.Chassis[sel.Data] < 0) break;
                        if (spot == 1 && InstantAction.VariantOn[sel.Data] &&
                            InstantAction.VariantIndex[sel.Data] >= InstantAction.Variants[sel.Data].Count - 1) break;
                        ShowPicture(InstantAction.Step(s.Data, sel.Data, 0, spot == 0 ? -1 : 1));
                        _list.Refresh();
                        Press(spot == 0 ? _prevVariant : _nextVariant, _click, false);
                        break;
                    case 2:
                    case 3:
                        sel = SelectedEntry();
                        if (sel != null)
                        {
                            ShowPicture(InstantAction.Step(s.Data, sel.Data, spot == 2 ? -1 : 1, 0));
                            _list.Refresh();
                        }
                        Press(spot == 2 ? _prevChassis : _nextChassis, _click, false);
                        break;
                    case 4:
                        Press(_exitButton, _exit, true);
                        ev = 0xc02b;
                        break;
                    case 5:
                        if (InstantAction.InUse(0) < 0) s.Dialog("You must choose a mech|for yourself#Ok", null);
                        else if (InstantAction.InUse(0) == 0) s.Dialog("You have no mech to pilot#Ok", null);
                        else if (InstantAction.InUse(1) == 0) s.Dialog("You must select at least 1 enemy for combat!#Ok", null);
                        else
                        {
                            Press(_launch, _exit, true);
                            ev = 0xc011;
                        }
                        break;
                    case 6:
                        ev = MechLab();
                        break;
                    case 7:
                        Press(_settings, _click, true);
                        if (!InstantAction.LoadParams(s.GameFolder)) Array.Clear(InstantAction.Params.Data, 0, RandomMissionParams.Size);
                        ev = 0xc031;
                        break;
                    case 8:
                        DeleteVariant();
                        break;
                }
            }
            if (ev != 0xc004) Leave(ev);
        }

        /// <summary>Case 6 of FUN_1005475d: the checks before MechLab, then the lab mech (InstantAction.MakeLabMech) and Customizing 0xc02f.</summary>
        static uint MechLab()
        {
            var s = _s;
            var sel = SelectedEntry();
            if (sel == null) { s.Dialog("Select point first!#Ok", null); return 0xc004; }
            InstantAction.Selected = sel.Data;
            int c = InstantAction.Chassis[sel.Data];
            if (c == -1) { s.Dialog("Select mech type first!#Ok", null); return 0xc004; }
            if (c == -2) { s.Dialog("Random mechs cannot be customized!#Ok", null); return 0xc004; }
            string code = Code(c).ToUpperInvariant();
            if (code == "ELE" || code == "ISB") { s.Dialog("This mech chassis may not be customized#Ok", null); return 0xc004; }
            if (!InstantAction.MakeLabMech(s.Data, s.Career, text => s.Dialog(text, null))) return 0xc004;
            s.Anims.Step();
            InstantAction.Select(0);
            return 0xc02f;
        }

        /// <summary>Case 8 of FUN_1005475d: a user variant's MEK deleted (no confirmation, as in the original), the enemy points
        /// holding it reset, and it is taken out of every point's list.</summary>
        static void DeleteVariant()
        {
            var s = _s;
            var sel = SelectedEntry();
            if (sel == null) { s.Dialog("Select variant to delete first#Ok", null); return; }
            int p = sel.Data;
            InstantAction.Selected = p;
            if (!InstantAction.VariantOn[p]) { s.Dialog("Selected mech cannot be deleted#Ok", null); return; }
            var m = InstantAction.VariantMech[p];
            if (m != null && m.Str(0x9a8, 3).ToUpperInvariant() == "STD") { s.Dialog("Standard variants cannot be deleted#Ok", null); return; }
            string name = m != null ? m.MekName : InstantAction.Variants[p][InstantAction.VariantIndex[p]];
            InstantAction.ForgetEnemyMek(s.Data, name);
            for (int q = 0; q < InstantAction.Points; q++)
            {
                var list = InstantAction.Variants[q];
                int k = list.FindIndex(v => string.Equals(v, name, StringComparison.OrdinalIgnoreCase));
                if (k < 0) continue;
                list.RemoveAt(k);
                if (k <= InstantAction.VariantIndex[q]) InstantAction.VariantIndex[q]--;
                if (InstantAction.VariantIndex[q] >= list.Count) InstantAction.VariantIndex[q] = list.Count - 1;
                if (InstantAction.VariantIndex[q] < 0) InstantAction.VariantIndex[q] = 0;
            }
            try
            {
                string file = System.IO.Path.Combine(InstantAction.UserMekFolder, (name.EndsWith(".mek") ? name : name + ".mek"));
                if (System.IO.File.Exists(file)) System.IO.File.Delete(file);
            }
            catch (System.IO.IOException error) { Debug.LogWarning("MW2 instant action: " + error.Message); }
            _list.Refresh();
        }

        static void Leave(uint ev)
        {
            if (_closed) return;
            _closed = true;
            var s = _s;
            _regions.Dispose();
            _regions = null;
            if (!InstantAction.SaveParams()) s.Dialog("Could not save RMG config#Ok", null);
            _list.Free();
            s.Anims.Clear();
            s.Surface.DrawAfter.Clear(); s.Surface.DrawBefore.Clear();
            _ambient.Release(); _exit.Release(); _click.Release();
            s.Post(ev, 0xc029);
            s.Unregister();
        }
    }
}
