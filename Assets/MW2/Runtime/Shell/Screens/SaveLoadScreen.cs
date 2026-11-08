using System;
using System.IO;
using UnityEngine;

namespace MW2.Shell
{
    /// <summary>
    /// Save / load 0xc02e (savegame.cpp: open FUN_1006ee78 0x1006ee78, screen function FUN_1006f614 0x1006f614).
    /// Background 20 of DATABASE.MW2; ten slots mersave0..9.sav, their names (state +2) in font 6 at (42, 92 + 35 n)
    /// (colour 1 → 0x16, the selected one 0x11, drawn before the anims); spots (table 0x100baa60): 0..9 the slots,
    /// 10 load (saveload), 11 save (savesave), 12 exit (saveexit); 10 and 11 come on with a selection. Coming from the
    /// title, instant action (0xc029 / 0xc031) or the instant-action Customizing the screen only loads: slots without a
    /// file are off, save is off and its button hidden, and with no save at all it says so and goes back to the title.
    /// A second click on the selected slot edits its name (FUN_10035083, 28 characters narrower than 240 pixels,
    /// upper-cased, an empty name keeps the old one). Saves are written to Unity's user-data folder (the game folder is
    /// never changed); a save is read from there, else from the game folder. Sounds 0x4e on opening, 0x69 looped, 0x4f
    /// after loading, 0x53 after saving.
    /// </summary>
    public static class SaveLoadScreen
    {
        static readonly ShellRegions.Entry[] Spots =
        {
            new ShellRegions.Entry(32, 83, 297, 116, 41, 91, ""), new ShellRegions.Entry(32, 117, 297, 151, 41, 125, ""),
            new ShellRegions.Entry(32, 152, 297, 186, 41, 160, ""), new ShellRegions.Entry(32, 187, 297, 221, 41, 195, ""),
            new ShellRegions.Entry(32, 222, 297, 256, 41, 230, ""), new ShellRegions.Entry(32, 257, 297, 291, 41, 265, ""),
            new ShellRegions.Entry(32, 292, 297, 326, 41, 300, ""), new ShellRegions.Entry(32, 327, 297, 361, 41, 335, ""),
            new ShellRegions.Entry(32, 362, 297, 397, 41, 370, ""), new ShellRegions.Entry(32, 398, 297, 432, 41, 406, ""),
            new ShellRegions.Entry(392, 387, 507, 429, 41, 406, ""), new ShellRegions.Entry(508, 387, 618, 429, 41, 406, ""),
            new ShellRegions.Entry(462, 435, 556, 474, 41, 406, "")
        };

        /// <summary>A slot DAT_100f5138 + n × 0x211: name, file, +0x200 / +0x204 (month and year of the save, set but not
        /// shown), +0x208 a save exists, +0x209 its text.</summary>
        sealed class Slot
        {
            public string Name = "", File;
            public int Month, Year;
            public bool Exists;
            public ShellText Text;
        }

        static Mw2Shell _s;
        static uint _from;                                  // DAT_100f65e8
        static readonly Slot[] _slots = new Slot[10];
        static int _selected = -1;                          // DAT_100f65e4 / DAT_100f9458
        static ShellRegions _regions;                       // DAT_100f9454
        static ShellSound _saved, _loaded, _enter, _ambient;   // DAT_100f9460, 9464, 9468, 946c
        static ShellAnim _loadButton, _saveButton, _exitButton;
        static byte[] _normal, _bright;
        static bool _closed;
        static bool _editing;
        static string _edited, _before;
        static ShellText _editText;

        /// <summary>DAT_100cf3d8: the save was asked for before a new career (New Mercenary, "Save game?" Yes); leaving
        /// goes to the title.</summary>
        public static bool BeforeNewCareer;

        /// <summary>Where a slot is written: the user-data folder.</summary>
        public static string WritePath(int n) => Path.Combine(Application.persistentDataPath, $"mersave{n}.sav");

        /// <summary>Where a slot is read: the user-data folder, else the game folder (fopen "rb" of the original).</summary>
        public static string ReadPath(Mw2Shell s, int n)
        {
            string own = WritePath(n);
            if (File.Exists(own)) return own;
            string game = Path.Combine(s.GameFolder ?? "", $"mersave{n}.sav");
            return File.Exists(game) ? game : null;
        }

        static bool LoadOnly(Mw2Shell s) =>
            _from == 0xc02b || _from == 0xc029 || _from == 0xc031 || _from == 0xc02f && s.Career.Mode == 2;

        public static void Open(Mw2Shell s, uint from)
        {
            _s = s;
            _from = from;
            _closed = false;
            _editing = false;
            _selected = -1;
            _normal = ShellSurface.MakeRemap(0x16);
            _bright = ShellSurface.MakeRemap(0x11);
            s.Surface.LoadBackground(ShellPcx.Decode(s.Db.Unpack(20)));
            _saved = Sound(0x53);
            _loaded = Sound(0x4f);
            _enter = Sound(0x4e);
            _ambient = Sound(0x69);
            _ambient.LoopForever();
            _ambient.Play();
            s.Anims.Clear();
            _loadButton = Button("saveload", 0x192, 0x18d);
            _saveButton = Button("savesave", 0x211, 0x18d);
            _exitButton = Button("saveexit", 0x1d3, 0x1b8);
            _regions = new ShellRegions(s.Surface, s.Font7, false, Spots, () => true);
            int found = 0;
            for (int n = 0; n < 10; n++)
            {
                var slot = _slots[n] = new Slot { Month = n, Year = n, File = $"mersave{n}.sav" };
                string path = ReadPath(s, n);
                byte[] head = null;
                if (path != null)
                    try
                    {
                        using (var f = File.OpenRead(path))
                        {
                            head = new byte[Career.Size];
                            if (f.Read(head, 0, head.Length) != head.Length) head = null;
                        }
                    }
                    catch (IOException) { head = null; }
                if (head == null)
                {
                    if (LoadOnly(s)) _regions.Disable(n);
                    continue;
                }
                found++;
                int e = 2;
                while (e < head.Length && head[e] != 0) e++;
                slot.Name = System.Text.Encoding.ASCII.GetString(head, 2, e - 2);
                slot.Month = BitConverter.ToInt32(head, 0x126);
                slot.Year = BitConverter.ToInt32(head, 0x122);
                slot.Exists = true;
            }
            for (int n = 0; n < 10; n++) DrawSlot(n, _normal);
            _regions.Disable(10);
            _regions.Disable(11);
            if (found == 0 && LoadOnly(s))
            {
                s.Dialog("There are no saved games to load#Ok", null);
                _regions.Dispose();
                _regions = null;
                Release();
                s.Post(0xc02b, 0xc02e);
                return;
            }
            if (LoadOnly(s))
            {
                _regions.Disable(11);
                _saveButton.Hide();
            }
            if (s.Career.Data[0x239a] == 1 || BeforeNewCareer)
            {
                _regions.Disable(10);
                _loadButton.Hide();
            }
            s.Register(Tick);
            _enter.Play();
        }

        static ShellSound Sound(int record)
        {
            var snd = new ShellSound(_s.gameObject, _s.Db, record);
            snd.SetVolume(0x32);
            return snd;
        }

        static ShellAnim Button(string name, int x, int y)
        {
            var a = new ShellAnim(_s.Anims, name, x, y, 0x21, 0);
            a.Show();
            return a;
        }

        /// <summary>FUN_1006ead6 0x1006ead6 / FUN_1006ebb2 0x1006ebb2 / FUN_1006ece6 0x1006ece6: a slot's name drawn before
        /// the anims, 0x16 or 0x11 when selected; an empty name draws nothing.</summary>
        static void DrawSlot(int n, byte[] remap)
        {
            var slot = _slots[n];
            slot.Text?.Hide();
            slot.Text = null;
            if (slot.Name.Length == 0) return;
            slot.Text = new ShellText(_s.Surface, _s.Font6, slot.Name, 0x2a, n * 0x23 + 0x5c, remap);
            slot.Text.Show(0);
        }

        /// <summary>FUN_1006ed9f 0x1006ed9f: load comes on unless only saving, save unless only loading.</summary>
        static void EnableButtons()
        {
            if (_s.Career.Data[0x239a] == 0 && !BeforeNewCareer) _regions.Enable(10);
            if (!LoadOnly(_s)) _regions.Enable(11);
        }

        /// <summary>Where leaving goes: 0xc002 when quitting (+0x239b), the title when a new career follows (+0x239a or
        /// DAT_100cf3d8, which is cleared), else back.</summary>
        static uint Next()
        {
            var c = _s.Career;
            if (c.Data[0x239b] == 1) return 0xc002;
            if (c.Data[0x239a] == 1 || BeforeNewCareer)
            {
                BeforeNewCareer = false;
                return 0xc02b;
            }
            return _from;
        }

        /// <summary>
        /// Save_Read 0x1006ddf6 with 1: the save becomes the career; its mission report (+0x243b) and combat record
        /// (+0x2e0f) are what MW2MSN.CFG / MW2CAR.CFG would hold, its salvage file comes back, launch MEK flags are
        /// cleared. In career mode 1 before the end of a step the random mission of the accepted offer is generated again:
        /// srand(+0x2342[+0x239e]), FUN_1006adab with the mission number +0x23cf, the pay +0x1e6 / +0x1ea and no new seed.
        /// </summary>
        public static Career Read(Mw2Shell s, byte[] bytes)
        {
            var c = Career.Load(bytes);
            if (c.Mode == 1 && !c.Finished239D)
            {
                s.Data.Random.Srand((uint)c.OfferSeed(c.NextChoice));
                c.RunRmg(s.Data, c.RanNumber, c.SuccessPay, c.FailPay, false, false, s.TickCounter, out int failed);
                string message = RandomMissionGenerator.ErrorText(failed, s.Data.Tables);
                if (message != null) s.Dialog(message, null);
                // Original1006ad2f's randwld1.bwd survives on disk across Save_Read.
                // ShellData.Files is session memory: recreate that launch alias after
                // regeneration so a fresh process does not lose the saved mission world.
                if (failed == 0) Career.RenameWorld(c.Mission, s.Data);
            }
            s.MissionReportFile = new byte[ShellMissionReport.Size];
            Buffer.BlockCopy(c.Data, 0x243b, s.MissionReportFile, 0, ShellMissionReport.Size);
            s.CombatCareerFile = new byte[0x50];
            Buffer.BlockCopy(c.Data, 0x2e0f, s.CombatCareerFile, 0, 0x50);
            s.SalvageFile = c.SalvageFile;
            s.SalvageOpened = false;
            foreach (var m in c.UnitMechs) m.Custom = false;
            return c;
        }

        static void Tick(uint ev)
        {
            var s = _s;
            var c = s.Career;
            if (_editing)
            {
                s.Anims.Step();
                EditTick();
                return;
            }
            if (ev == 0xc004)
            {
                s.Anims.Step();
                int spot = _regions.Hit(s.MouseX, s.MouseY);
                if (s.MouseClicked)
                    switch (spot)
                    {
                        case 0: case 1: case 2: case 3: case 4: case 5: case 6: case 7: case 8: case 9:
                            EnableButtons();
                            int previous = _selected;
                            if (previous >= 0) DrawSlot(previous, _normal);
                            _selected = spot;
                            DrawSlot(spot, _bright);
                            if (previous == spot && !LoadOnly(s)) StartEdit();
                            break;
                        case 10:
                            _loadButton.SetFrame(1);
                            s.Anims.Step();
                            if (!_slots[_selected].Exists)
                            {
                                s.Dialog("Saved game does not exist!#Ok", null);
                                _loadButton.SetFrame(0);
                                break;
                            }
                            if (c.Started)
                                s.Dialog("Lose current game?#Yes|No", a =>
                                {
                                    if (a == 1) _loadButton.SetFrame(0);
                                    else Load();
                                });
                            else Load();
                            break;
                        case 11:
                            _saveButton.SetFrame(1);
                            s.Anims.Step();
                            if (_slots[_selected].Name.Length == 0)
                            {
                                _saveButton.SetFrame(0);
                                break;
                            }
                            if (_slots[_selected].Exists)
                                s.Dialog("Replace Saved Game?#Yes|No", a =>
                                {
                                    if (a == 1) _saveButton.SetFrame(0);
                                    else Save();
                                });
                            else Save();
                            break;
                        case 12:
                            _exitButton.SetFrame(1);
                            s.Anims.Step();
                            Leave(Next());
                            break;
                    }
                return;
            }
            Leave(ev);
        }

        static void Load()
        {
            var s = _s;
            byte[] bytes;
            try { bytes = File.ReadAllBytes(ReadPath(s, _selected)); }
            catch (Exception error)
            {
                Debug.LogWarning("MW2 load: " + error.Message);
                _loadButton.SetFrame(0);
                return;
            }
            s.Career = Read(s, bytes);
            if (s.Career.Mode != 2) s.Menu.Enable(ShellMenu.Retire, true);
            s.Career.Data[0x239b] = 0;
            s.Career.Data[0x239a] = 0;
            s.BlockUntilEnd(_loaded);
            Leave(s.Career.Msg23C7);
        }

        static void Save()
        {
            var s = _s;
            var c = s.Career;
            var slot = _slots[_selected];
            slot.Exists = true;
            c.SaveName = slot.Name;
            try
            {
                Directory.CreateDirectory(Application.persistentDataPath);
                File.WriteAllBytes(WritePath(_selected), c.SaveGame(s.SalvageFile));
                c.Started = false;
                s.BlockUntilEnd(_saved);
            }
            catch (IOException error)
            {
                s.Dialog($"Error Saving Game: {error.HResult & 0xffff}#Ok", null);
            }
            slot.Month = c.Month;
            slot.Year = c.Year;
            Leave(Next());
        }

        static void StartEdit()
        {
            _editing = true;
            _before = _slots[_selected].Name;
            _edited = _before;
            _slots[_selected].Text?.Hide();
            _slots[_selected].Text = null;
            ShowEdit();
        }

        static void ShowEdit()
        {
            _editText?.Hide();
            _editText = new ShellText(_s.Surface, _s.Font6, _edited + "_", 0x2a, _selected * 0x23 + 0x5c, _bright);
            _editText.Show();
        }

        /// <summary>FUN_10035083 for the slot name: Enter, Esc or a click end it; whatever was typed stays (the original
        /// copies the buffer back on every exit), an empty name takes the old one back.</summary>
        static void EditTick()
        {
            var s = _s;
            bool done = false;
            foreach (char ch in Input.inputString)
            {
                if (ch == '\b')
                {
                    if (_edited.Length > 0) _edited = _edited.Substring(0, _edited.Length - 1);
                }
                else if (ch == '\n' || ch == '\r') done = true;
                else if (ch > 31 && ch < 128 && ch != '~' && _edited.Length != 0x1c && s.Font6.Width(_edited + ch + "_") < 0xf0)
                    _edited += ch;
            }
            if (Input.GetKeyDown(KeyCode.Escape) || s.MouseClicked) done = true;
            if (!done)
            {
                ShowEdit();
                return;
            }
            _editing = false;
            _editText?.Hide();
            _editText = null;
            if (_edited.Length == 0) _edited = _before;
            _slots[_selected].Name = _edited.ToUpperInvariant();      // FUN_1000739d
            DrawSlot(_selected, _bright);
        }

        static void Release()
        {
            _saved.Release(); _loaded.Release(); _enter.Release(); _ambient.Release();
        }

        static void Leave(uint ev)
        {
            if (_closed) return;
            _closed = true;
            var s = _s;
            _regions?.Dispose();
            _regions = null;
            foreach (var slot in _slots) if (slot != null) { slot.Text?.Hide(); slot.Text = null; }
            s.Anims.Clear();
            s.Surface.DrawAfter.Clear(); s.Surface.DrawBefore.Clear();
            Release();
            s.Post(ev, 0xc02e);
            s.Unregister();
        }
    }
}
