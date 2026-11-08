using System;

namespace MW2.Shell
{
    /// <summary>
    /// Merc Lords, the high-score table of the Mercenary menu (FUN_1002e570 0x1002e570, closed by FUN_1002e892
    /// 0x1002e892). A modal hook over the current screen (DAT_100cf3bc: the screen function is not called meanwhile):
    /// the palette is kept, record 26 of DATABASE.MW2 is put straight on the screen with its palette (FUN_10078507), the
    /// table is read (FUN_1002e9c2) and drawn on it: "Pilot" at (91, 150), "Mercenary Group" at (270, 150) and "Funds at
    /// Cash Out" right-aligned on 570 in font 7, colour 1 → 0x11; the ten entries from y 182, 16 apart, in font 11, their
    /// funds with thousands commas (FUN_100496d6) right-aligned on 570, a new record in colour 0xdc. Escape or a right
    /// click (mouse +0x14) closes it: Merc Lords and Save/Load on again, the screen restored from the background and the palette put back.
    /// </summary>
    public static class MercLordsScreen
    {
        static Mw2Shell _s;
        static readonly byte[] _palette = new byte[768];

        public static void Open(Mw2Shell s)
        {
            _s = s;
            Buffer.BlockCopy(s.Surface.Palette6, 0, _palette, 0, 768);
            var mark = ShellSurface.MakeRemap(0xdc);
            var normal = ShellSurface.MakeRemap(0x11);
            s.FlushKeys();
            s.Surface.ShowPicture(ShellPcx.Decode(s.Db.Unpack(0x1a)));
            s.Surface.RestoreKey = 0;
            var scores = s.LoadHighScores();
            s.Surface.DrawString(s.Font7, 0x5b, 0x96, "Pilot", normal);
            s.Surface.DrawString(s.Font7, 0x10e, 0x96, "Mercenary Group", normal);
            s.Surface.DrawString(s.Font7, 0x23a - s.Font7.Width("Funds at Cash Out"), 0x96, "Funds at Cash Out", normal);
            int y = 0xb6;
            for (int i = 0; i < HighScores.Count; i++)
            {
                var remap = scores.NewRecord(i) ? mark : normal;
                s.Surface.DrawString(s.Font11, 0x5b, y, scores.Pilot(i), remap);
                s.Surface.DrawString(s.Font11, 0x10e, y, scores.Unit(i), remap);
                string funds = Debriefing.Money(scores.Score(i));
                s.Surface.DrawString(s.Font11, 0x23a - s.Font11.Width(funds), y, funds, remap);
                y += 0x10;
            }
            s.Surface.MarkAll();
            s.Hook = Close;
        }

        /// <summary>FUN_1002e892: with 1 (each pass) only on Escape or a right click; with 0 (FUN_100440bc) at once.</summary>
        static void Close(int pass)
        {
            var s = _s;
            if (pass != 0 && s.ReadKey(out _) != 3 && !s.MouseRightClicked) return;
            s.Hook = null;
            s.Menu.Enable(ShellMenu.MercLords, true);
            s.Menu.Enable(ShellMenu.SaveLoad, true);
            s.Surface.RestoreKey = -1;
            s.Surface.Restore(0, 0, ShellSurface.Width, ShellSurface.Height);
            Buffer.BlockCopy(_palette, 0, s.Surface.Palette6, 0, 768);
            s.Surface.PaletteDirty = true;
            if (pass != 0) s.Anims.Step();
        }
    }
}
