using System;

namespace MW2.Shell
{
    /// <summary>
    /// The First Circuit, the credits of the Help menu (FUN_1000fe40 0x1000fe40, hook FUN_1000ff45 0x1000ff45). A modal
    /// hook like Merc Lords over picture 29 (with its palette; the logo flic "amwlogo1" at (120, 4) is not in the
    /// Titanium installation). The 0x21e lines of the table 0x1009ca88 roll up a pixel a pass, 20 pixels apart, from
    /// y 460: between y 105 and 460 each is drawn centred on 320 — "&lt;" font 6 and "&gt;" font 7 in colour 0x11, "~" font 6
    /// in colour 0xef, others font 11 in colour 0x11; null lines are blank — after the band (0, 125) 640 × 335 is restored
    /// from the background, and the rows at 105 and 460 are restored again over them. A right click, any key or a menu
    /// command closes it: The First Circuit and Save/Load on again, the screen and the palette back.
    /// </summary>
    public static class FirstCircuitScreen
    {
        static Mw2Shell _s;
        static readonly byte[] _palette = new byte[768];
        static int _first;      // DAT_100c73f8
        static int _y;          // DAT_100c7500
        static byte[] _normal, _tilde;

        public static void Open(Mw2Shell s)
        {
            _s = s;
            _first = 0;
            Buffer.BlockCopy(s.Surface.Palette6, 0, _palette, 0, 768);
            s.FlushKeys();
            s.Surface.ShowPicture(ShellPcx.Decode(s.Db.Unpack(0x1d)));
            s.Surface.RestoreKey = 0;
            _normal = ShellSurface.MakeRemap(0x11);
            _tilde = ShellSurface.MakeRemap(0xef);
            _y = 0x1cc;
            s.Hook = Hook;
        }

        static void Hook(int pass)
        {
            var s = _s;
            if (pass != 0)
            {
                s.Surface.Restore(0, 0x7d, 0x280, 0x14f);
                int line = _first, y = _y + _first * 0x14;
                _y--;
                for (; line < 0x21e && y < 0x1cc; line++, y += 0x14)
                {
                    if (y < 0x69)
                    {
                        _first = line;
                        continue;
                    }
                    // 0x1009ca88 + line x 4; a null pointer is "" and draws nothing.
                    string text = s.Data.Tables.Defaults.Credits[line];
                    if (text.Length == 0) continue;
                    ShellFont font;
                    byte[] remap = _normal;
                    if (text.StartsWith("<")) { font = s.Font6; text = text.Substring(1); }
                    else if (text.StartsWith(">")) { font = s.Font7; text = text.Substring(1); }
                    else if (text.StartsWith("~")) { font = s.Font6; text = text.Substring(1); remap = _tilde; }
                    else font = s.Font11;
                    s.Surface.DrawString(font, 0x140 - font.Width(text) / 2, y, text, remap);
                }
                s.Surface.Restore(0, 0x69, 0x280, 0x14);
                s.Surface.Restore(0, 0x1cc, 0x280, 0x14);
            }
            if (pass != 0 && !s.MouseRightClicked && s.ReadKey(out _) == 0) return;
            s.Hook = null;
            s.Menu.Enable(ShellMenu.FirstCircuit, true);
            s.Menu.Enable(ShellMenu.SaveLoad, true);
            s.Surface.RestoreKey = -1;
            s.Surface.Restore(0, 0, ShellSurface.Width, ShellSurface.Height);
            Buffer.BlockCopy(_palette, 0, s.Surface.Palette6, 0, 768);
            s.Surface.PaletteDirty = true;
            if (pass != 0) s.Anims.Step();
        }
    }
}
