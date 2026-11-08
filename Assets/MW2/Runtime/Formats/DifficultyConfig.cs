using System;
using System.IO;

namespace MW2.Formats
{
    /// <summary>
    /// mw2dif.cfg in the game folder (LoadCfgDifficulty 0x10024cf0 into DAT_100aea44, a zeroed 0x17-byte block that keeps zeros
    /// when the file is missing). In single play the loader then forces +2 = 1 (explosions damage units, 0x1001280e),
    /// +3 = 1 (hard landings damage the legs, 0x1003880d), +9 = 1 (radar range keys, 0x10050be5) and clears +0xb, +0xf, +0x13
    /// (the network overrides). Bytes: +0 unlimited ammunition for the player, +1 the player cannot be destroyed (Mech_Destroy
    /// 0x10035ef6 / Mech_ApplyDamage 0x10037b59 skip the player), +4 heat for the player (Mech_UpdateHeat 0x10035a9b keeps the
    /// player's heat at 0 without it), +5 difficulty 0..2 (armour factors 0x10099fbc / 0x10099fb8, heat sinks, Mech_LoadMek).
    /// </summary>
    public sealed class DifficultyConfig
    {
        /// <summary>The user-data folder (Application.persistentDataPath), read before the game folder.</summary>
        public static string UserFolder;

        public const int Size = 0x17;
        public readonly byte[] Bytes = new byte[Size];

        public bool UnlimitedAmmo => Bytes[0] != 0;
        public bool Invulnerable => Bytes[1] != 0;
        public bool AreaDamageToUnits => Bytes[2] != 0;
        public bool LandingDamage => Bytes[3] != 0;
        public bool PlayerHeat => Bytes[4] != 0;
        public int Difficulty => (sbyte)Bytes[5];
        public bool RadarKeys => Bytes[9] != 0;

        /// <summary>LoadCfgDifficulty for single play: the file's bytes (zeros without it) with the forced values.</summary>
        public static DifficultyConfig Load(string gameFolder)
        {
            var c = new DifficultyConfig();
            string path = string.IsNullOrEmpty(gameFolder) ? null : Path.Combine(gameFolder, "mw2dif.cfg");
            // The shell's Combat Variables write their copy into the user-data folder (the game folder stays untouched).
            if (!string.IsNullOrEmpty(UserFolder) && File.Exists(Path.Combine(UserFolder, "mw2dif.cfg"))) path = Path.Combine(UserFolder, "mw2dif.cfg");
            if (path != null && File.Exists(path))
            {
                byte[] d = File.ReadAllBytes(path);
                Array.Copy(d, c.Bytes, Math.Min(d.Length, Size));
            }
            for (int k = 0xb; k < 0xb + 4 && k < Size; k++) c.Bytes[k] = 0;
            for (int k = 0xf; k < 0xf + 4 && k < Size; k++) c.Bytes[k] = 0;
            for (int k = 0x13; k < 0x13 + 4 && k < Size; k++) c.Bytes[k] = 0;
            c.Bytes[9] = 1;
            c.Bytes[2] = 1;
            c.Bytes[3] = 1;
            return c;
        }
    }
}
