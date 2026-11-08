using System;
using System.IO;

namespace MW2.Formats
{
    /// <summary>
    /// mw2snd.cfg in the game folder (LoadCfgSnd 0x10024e60, read at start by SimMain 0x100465ae into DAT_100a6308 and copied
    /// word for word to DAT_1008b898; written back by FUN_10024f7d from FUN_1000fd09 when the sim ends): 0x15 dwords.
    /// Words: 0 Betty volume (DAT_1008b898), 1 sound effects (DAT_1008b89c), 2 voice (DAT_1008b8a0), 3 music (DAT_1008b8a4),
    /// all 16.16 with 0x10000 = full; 4 sound flags (DAT_1008b8a8: 2 voice on, 4 MIDI music, 8 CD music); 5 object textmaps
    /// (+0x14), 6 terrain textmaps (+0x18), 7 (+0x1c), 8 (+0x20), 9 explosion chunks (+0x24), 10 particles (+0x28),
    /// 11 multiple lights (+0x2c), 12 shadows (+0x30) as set by the COMBAT VARIABLES page (0x1004e9a1);
    /// 13 (DAT_100905fc), 14.. a DLL name ("vesa768.dll").
    /// Without the file LoadCfgSnd uses its defaults: volumes 0x10000, flags 0xb, words 5..9 = 1, 10..12 = 0, 13 = 8.
    /// </summary>
    public sealed class SoundConfig
    {
        /// <summary>The user-data folder (Application.persistentDataPath), read before the game folder.</summary>
        public static string UserFolder;

        public const int Words = 0x15;
        public readonly int[] Word = new int[Words];

        public int Betty { get => Word[0]; set => Word[0] = value; }
        public int Effects { get => Word[1]; set => Word[1] = value; }
        public int Voice { get => Word[2]; set => Word[2] = value; }
        public int Music { get => Word[3]; set => Word[3] = value; }
        public int Flags { get => Word[4]; set => Word[4] = value; }

        /// <summary>LoadCfgSnd's values when mw2snd.cfg is missing (0x10024e60).</summary>
        public static SoundConfig Defaults()
        {
            var c = new SoundConfig();
            c.Word[0] = c.Word[1] = c.Word[2] = c.Word[3] = 0x10000;
            c.Word[4] = 0xb;
            for (int i = 5; i <= 9; i++) c.Word[i] = 1;
            c.Word[13] = 8;
            return c;
        }

        public static SoundConfig Parse(byte[] data)
        {
            var c = new SoundConfig();
            for (int i = 0; i < Words && i * 4 + 4 <= data.Length; i++) c.Word[i] = BitConverter.ToInt32(data, i * 4);
            return c;
        }

        /// <summary>mw2snd.cfg from the game folder, or the defaults when it is missing or short.</summary>
        public static SoundConfig Load(string gameFolder)
        {
            string path = string.IsNullOrEmpty(gameFolder) ? null : Path.Combine(gameFolder, "mw2snd.cfg");
            // The shell's Combat Variables write their copy into the user-data folder (the game folder stays untouched).
            if (!string.IsNullOrEmpty(UserFolder) && File.Exists(Path.Combine(UserFolder, "mw2snd.cfg"))) path = Path.Combine(UserFolder, "mw2snd.cfg");
            if (path == null || !File.Exists(path)) return Defaults();
            var data = File.ReadAllBytes(path);
            return data.Length < Words * 4 ? Defaults() : Parse(data);
        }
    }
}
