using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using MW2.Formats;

namespace MW2.Shell
{
    /// <summary>A default high score, 0x45 bytes: pilot 0x20, unit 0x20, score i32, new-record byte.</summary>
    [Serializable]
    public sealed class HighScoreDef
    {
        public string Pilot;
        public string Unit;
        public int Score;
        public int NewRecord;
    }

    /// <summary>A popup of the shell window's menu and its items (MENU resource 104).</summary>
    [Serializable]
    public sealed class MenuPopupDef
    {
        public string Text;
        public bool Grayed;
        public List<MenuItemDef> Items = new List<MenuItemDef>();
    }

    [Serializable]
    public sealed class MenuItemDef
    {
        public int Id;             // command id (ShellMenu constants); 0 with an empty text is a separator
        public string Text;        // '&' marks the access letter, '\t' the key text
        public bool Grayed;
    }

    /// <summary>
    /// The shell's default data blocks and texts from MW2SHELL.DLL as one extracted file: the ten default high scores
    /// (0x100a51c8, 0x2b6 bytes: count then the records), the defaults of mw2snd.cfg (DAT_100a4300, 0x15 dwords, laid
    /// out as Formats/SoundConfig) and mw2dif.cfg (DAT_100a4358, 0x17 bytes, as Formats/DifficultyConfig), the 0x21e
    /// lines of The First Circuit (pointers at 0x1009ca88; a null pointer is "" and draws nothing), and the window menu.
    /// </summary>
    [Serializable]
    public sealed class ShellDefaultTables
    {
        public const string TableName = "shell-defaults";
        public const int CurrentFormat = 1;
        public const uint HighScoreVa = 0x100a51c8, SoundVa = 0x100a4300, DifficultyVa = 0x100a4358, CreditVa = 0x1009ca88;
        public const int CreditLines = 0x21e, MenuResource = 104;

        public int Format = CurrentFormat;
        public List<HighScoreDef> HighScores = new List<HighScoreDef>();
        public int[] SoundWords = new int[SoundConfig.Words];
        public int[] DifficultyBytes = new int[DifficultyConfig.Size];
        public List<string> Credits = new List<string>();
        public List<MenuPopupDef> Menu = new List<MenuPopupDef>();

        /// <summary>The 0x2b6-byte block HighScores keeps (FUN_1002e9c2 copies it from 0x100a51c8).</summary>
        public byte[] HighScoreBlock()
        {
            var b = new byte[MW2.Shell.HighScores.Size];
            Buffer.BlockCopy(BitConverter.GetBytes(HighScores.Count), 0, b, 0, 4);
            for (int r = 0; r < HighScores.Count && r < MW2.Shell.HighScores.Count; r++)
            {
                int o = 4 + r * MW2.Shell.HighScores.RecordSize;
                var a = Encoding.ASCII.GetBytes(HighScores[r].Pilot ?? "");
                Buffer.BlockCopy(a, 0, b, o, Math.Min(a.Length, 0x20));
                a = Encoding.ASCII.GetBytes(HighScores[r].Unit ?? "");
                Buffer.BlockCopy(a, 0, b, o + 0x20, Math.Min(a.Length, 0x20));
                Buffer.BlockCopy(BitConverter.GetBytes(HighScores[r].Score), 0, b, o + 0x40, 4);
                b[o + 0x44] = (byte)HighScores[r].NewRecord;
            }
            return b;
        }

        public byte[] SoundBlock()
        {
            var b = new byte[SoundConfig.Words * 4];
            for (int k = 0; k < SoundConfig.Words; k++) Buffer.BlockCopy(BitConverter.GetBytes(SoundWords[k]), 0, b, k * 4, 4);
            return b;
        }

        public byte[] DifficultyBlock()
        {
            var b = new byte[DifficultyConfig.Size];
            for (int k = 0; k < b.Length; k++) b[k] = (byte)DifficultyBytes[k];
            return b;
        }


        public static ShellDefaultTables Get(string gameFolder, TableStore store = null)
        {
            if (store == null) store = TableStore.Default;
            var t = store.Extract(TableName, () => Load(Path.Combine(gameFolder, "MW2SHELL.DLL")));
            if (t.Format != CurrentFormat)
                throw new InvalidDataException($"{store.PathFor(TableName)} has format {t.Format}, this build reads " +
                    $"{CurrentFormat}: move it aside (keeping your edits) and run MW2 -> Tables -> Extract missing.");
            return t;
        }

        public static ShellDefaultTables Load(string shellDll)
        {
            byte[] d = File.ReadAllBytes(shellDll);
            int Off(uint va) => PeImage.FileOffset(d, va);
            string At(int o, int max)
            {
                int e = o;
                while (e < o + max && e < d.Length && d[e] != 0) e++;
                return Encoding.ASCII.GetString(d, o, e - o);
            }

            var t = new ShellDefaultTables();
            int hs = Off(HighScoreVa), n = BitConverter.ToInt32(d, hs);
            for (int r = 0; r < n && r < MW2.Shell.HighScores.Count; r++)
            {
                int o = hs + 4 + r * MW2.Shell.HighScores.RecordSize;
                t.HighScores.Add(new HighScoreDef
                {
                    Pilot = At(o, 0x20), Unit = At(o + 0x20, 0x20), Score = BitConverter.ToInt32(d, o + 0x40), NewRecord = d[o + 0x44],
                });
            }
            for (int k = 0; k < SoundConfig.Words; k++) t.SoundWords[k] = BitConverter.ToInt32(d, Off(SoundVa) + k * 4);
            for (int k = 0; k < DifficultyConfig.Size; k++) t.DifficultyBytes[k] = d[Off(DifficultyVa) + k];
            for (uint k = 0; k < CreditLines; k++)
            {
                uint p = (uint)BitConverter.ToInt32(d, Off(CreditVa + k * 4));
                t.Credits.Add(p == 0 || PeImage.IsZeroFill(d, p) ? "" : At(Off(p), d.Length));
            }
            var menu = ShellMenu.Parse(PeImage.Resource(d, 4, MenuResource));
            foreach (var popup in menu.Popups)
            {
                var pd = new MenuPopupDef { Text = popup.Text, Grayed = popup.Grayed };
                foreach (var item in popup.Items) pd.Items.Add(new MenuItemDef { Id = item.Id, Text = item.Text, Grayed = item.Grayed });
                t.Menu.Add(pd);
            }
            return t;
        }
    }
}
