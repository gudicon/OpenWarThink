using System;
using System.IO;
using MW2.Formats;
using UnityEngine;

namespace MW2.Shell
{
    /// <summary>
    /// Combat Variables, the options page of the shell (Options menu and the title's "soptns": FUN_10027946 0x10027946,
    /// hook FUN_10027ac9 0x10027ac9). A modal hook over the screen like Merc Lords: the palette is kept, record 27 goes
    /// straight on the screen (FUN_10078507), the rectangle (375, 124) 258 × 352 is restored from the background with key
    /// 0 (+0x3a6 = 0), the logo flic "amwlogo1" at (120, 4) (FUN_10070205; SMK\amwlogo1.smk is not in the Titanium
    /// installation, so it does not open in the original either). mw2snd.cfg (0x54 bytes, DAT_100a4300) and mw2dif.cfg
    /// (0x17 bytes, DAT_100a4358) are read over their defaults. The list DAT_100a3908 (font 6, centred in 100 pixels at
    /// x 373, colour 1 → 0x11): difficulty EASY / MEDIUM / HARD (dif +5), heat ON / OFF (dif +4), object and terrain
    /// textures (snd +0x14 / +0x18), display detail and object density HIGH / LOW (+0x1c / +0x20), chunky explosions
    /// (+0x24), "640x480" (+0x38, a click sets "vesa480.dll" or clears it), particles, multiple lights, shadows (+0x28,
    /// +0x2c, +0x30), +0x48, +0x4c; three sliders at (335, 113 / 137 / 161) 285 × 21 for the music (+0xc), sound effects
    /// (+4, the shell's master volume) and voice (+8) drawn with frames 1 (track) and 0 (knob at 8 + volume / 256) of
    /// shape record 3; dragging one plays sound 0x35 at the new volume whenever it moves by 0xa00 or more. A right click,
    /// any key or a menu command closes it: Combat Variables and Save/Load on again, both files written (here into the
    /// user-data folder), the screen restored from the background and the palette put back.
    /// </summary>
    public static class CombatVariablesScreen
    {
        static Mw2Shell _s;
        static readonly byte[] _palette = new byte[768];
        static ShellItemList _list;
        static Shape[] _slider;
        static ShellSound _sound;
        static ShellItemList.Entry _dragging;
        static int _dragFrom;
        static byte[] _remap;

        /// <summary>DAT_100a4300 (mw2snd.cfg) and DAT_100a4358 (mw2dif.cfg) of the shell.</summary>
        public static byte[] Sound, Difficulty;

        /// <summary>FUN_1002782a / FUN_10027871: the blocks over their .data values, from the user-data folder or the game's.</summary>
        public static void Load(Mw2Shell s)
        {
            if (Sound == null) Sound = s.Data.Tables.Defaults.SoundBlock();
            if (Difficulty == null) Difficulty = s.Data.Tables.Defaults.DifficultyBlock();
            Read(s, "MW2SND.CFG", Sound);
            Read(s, "MW2DIF.CFG", Difficulty);
            ShellSound.MasterVolume = BitConverter.ToInt32(Sound, 4);
        }

        static void Read(Mw2Shell s, string name, byte[] block)
        {
            string own = Path.Combine(Application.persistentDataPath, name), game = Path.Combine(s.GameFolder, name);
            string path = File.Exists(own) ? own : File.Exists(game) ? game : null;
            if (path == null) return;
            try
            {
                byte[] d = File.ReadAllBytes(path);
                Buffer.BlockCopy(d, 0, block, 0, Math.Min(d.Length, block.Length));
            }
            catch (IOException error) { Debug.LogWarning("MW2 options: " + error.Message); }
        }

        /// <summary>FUN_100278ff / FUN_100278b8: the blocks written (into the user-data folder; the game folder stays as it is).</summary>
        static void Write(string name, byte[] block)
        {
            try
            {
                Directory.CreateDirectory(Application.persistentDataPath);
                File.WriteAllBytes(Path.Combine(Application.persistentDataPath, name), block);
            }
            catch (IOException error) { Debug.LogWarning("MW2 options: " + error.Message); }
        }

        public static void Open(Mw2Shell s)
        {
            _s = s;
            Buffer.BlockCopy(s.Surface.Palette6, 0, _palette, 0, 768);
            _slider = Shape.DecodeFrames(s.Db.Raw(3));
            _sound = new ShellSound(s.gameObject, s.Db, 0x35);
            _sound.SetVolume(0x32);
            s.Surface.ShowPicture(ShellPcx.Decode(s.Db.Unpack(0x1b)));
            s.Surface.RestoreKey = 0;
            s.Surface.Restore(0x177, 0x7c, 0x102, 0x160);
            s.FlushKeys();
            Load(s);
            _remap = ShellSurface.MakeRemap(0x11);
            _list = List();
            _list.Init();
            _dragging = null;
            s.Hook = Hook;
        }

        static ShellText Centred(ShellItemList.Entry e, string text)
        {
            var t = new ShellText(_s.Surface, _s.Font6, text, e.X + e.W / 2, e.Y, _remap);
            t.Show(0);
            return t;
        }

        static int Int(int o) => BitConverter.ToInt32(Sound, o);
        static void SetInt(int o, int v) { var b = BitConverter.GetBytes(v); Buffer.BlockCopy(b, 0, Sound, o, 4); }

        static ShellItemList List()
        {
            var list = new ShellItemList();
            void Add(int y, Func<ShellItemList.Entry, ShellText> draw, Action<ShellItemList.Entry> click) =>
                list.Entries.Add(new ShellItemList.Entry { X = 0x175, Y = y, W = 100, Draw = draw, Click = click });
            string[] levels = { "~EASY", "~MEDIUM", "~HARD" };
            // 0x100272f6 / 0x100275b1: difficulty 0..2.
            Add(0xc7, e => Centred(e, Difficulty[5] < 3 ? levels[Difficulty[5]] : ""), e => { Difficulty[5]++; if (Difficulty[5] > 2) Difficulty[5] = 0; });
            // 0x1002739c / 0x1002760d: heat.
            Add(0xdf, e => Centred(e, Difficulty[4] == 0 ? "~OFF" : "~ON"), e => Difficulty[4] = (byte)(Difficulty[4] == 0 ? 1 : 0));
            int[] words = { 0x14, 0x18, 0x1c, 0x20, 0x24, 0x38, 0x28, 0x2c, 0x30, 0x48, 0x4c };
            int[] y = { 0xf0, 0x101, 0x112, 0x123, 0x134, 0x145, 0x156, 0x167, 0x178, 0x189, 0x19a };
            for (int k = 0; k < words.Length; k++)
            {
                int o = words[k];
                if (o == 0x38)
                {
                    // 0x10027539 / 0x10027637: the renderer DLL name.
                    Add(y[k], e => Centred(e, "~640x480"), e =>
                    {
                        string dll = Sound[0x38] == 0 ? "vesa480.dll" : "";
                        Array.Clear(Sound, 0x38, 0xf);
                        System.Text.Encoding.ASCII.GetBytes(dll, 0, dll.Length, Sound, 0x38);
                    });
                    continue;
                }
                bool highLow = o == 0x1c || o == 0x20;      // 0x100274e0, else 0x10027343
                Add(y[k], e => Centred(e, Int(o) == 0 ? (highLow ? "~LOW" : "~OFF") : (highLow ? "~HIGH" : "~ON")),
                    e => SetInt(o, Int(o) == 0 ? 1 : 0));   // 0x100275e0
            }
            // 0x1002767c: the slider area restored; 0x100276a9 / 0x1002772d: the three sliders.
            list.Entries.Add(new ShellItemList.Entry
            {
                X = 0x14f, Y = 0x71, W = 0x11d, H = 0x4e,
                Draw = e => { _s.Surface.Restore(e.X, e.Y, e.W, e.H); return null; }
            });
            int[] slider = { 0xc, 4, 8 };
            int[] sy = { 0x71, 0x89, 0xa1 };
            for (int k = 0; k < 3; k++)
            {
                int o = slider[k];
                list.Entries.Add(new ShellItemList.Entry
                {
                    X = 0x14f, Y = sy[k], W = 0x11d, H = 0x15, Data = o,
                    Draw = e =>
                    {
                        int v = Int(o);
                        if (_slider.Length > 1) _s.Surface.DrawShape(_slider[1], e.X, e.Y, e.W, e.H);
                        if (_slider.Length > 0) _s.Surface.DrawShape(_slider[0], e.X + 8 + (v + (v >> 31 & 0xff) >> 8), e.Y - 1, 0xf, 0x1d);
                        return null;
                    },
                    Click = e => { _dragging = e; _dragFrom = Int(o); }
                });
            }
            return list;
        }

        /// <summary>The loop of FUN_1002772d, a pass at a time: while the button is held the volume follows the mouse.</summary>
        static void Drag()
        {
            var s = _s;
            var e = _dragging;
            int v = s.MouseX - (e.X + 0xf);
            if (v < 0) v = 0;
            else if (v > 0x100) v = 0x100;
            v <<= 8;
            SetInt(e.Data, v);
            if (e.Data == 4) ShellSound.MasterVolume = v;       // +4 is DAT_100a4304 itself
            if (Math.Abs(_dragFrom - v) > 0x9ff)
            {
                _dragFrom = v;
                int master = ShellSound.MasterVolume;
                ShellSound.MasterVolume = v;
                _sound.SetVolume(0x32);
                _sound.Play();
                ShellSound.MasterVolume = master;
            }
            _list.Refresh();
            if (!s.MouseLeft) _dragging = null;
        }

        /// <summary>FUN_10027ac9: with 1 each pass a click on the list; a right click, any key or 0 closes it.</summary>
        static void Hook(int pass)
        {
            var s = _s;
            if (pass != 0)
            {
                if (_dragging != null)
                {
                    Drag();
                    return;
                }
                if (s.MouseClicked)
                {
                    var hit = _list.Hit(s.MouseX, s.MouseY);
                    if (hit != null)
                    {
                        hit.Click(hit);
                        _list.Refresh();
                    }
                }
            }
            if (pass != 0 && !s.MouseRightClicked && s.ReadKey(out _) == 0) return;
            s.Hook = null;
            s.Menu.Enable(ShellMenu.CombatVariables, true);
            s.Menu.Enable(ShellMenu.SaveLoad, true);
            _list.Free();
            Write("MW2SND.CFG", Sound);
            Write("MW2DIF.CFG", Difficulty);
            _sound.Release();
            ShellSound.MasterVolume = BitConverter.ToInt32(Sound, 4);
            s.Surface.RestoreKey = -1;
            s.Surface.Restore(0, 0, ShellSurface.Width, ShellSurface.Height);
            Buffer.BlockCopy(_palette, 0, s.Surface.Palette6, 0, 768);
            s.Surface.PaletteDirty = true;
            if (pass != 0) s.Anims.Step();
        }
    }
}
