using System;
using System.Collections.Generic;

namespace MW2.Formats
{
    /// <summary>
    /// Effect (explosion) type from the table in mercsw.dll (.data, 36 records of 0x2C bytes at VA 0x1009bb08), used by
    /// 0x10011ea8 (start) and 0x1001280e (update): +0x00 duration in ticks, +0x08 sound, +0x0C sound chance (-1 always),
    /// +0x10 screen flash, +0x14 needs a model slot, +0x18 area damage, +0x1C texture animation mode of the slot's key
    /// (2 = play once), +0x20 area damage per tick, +0x24 area heat per tick, +0x28 flags (1 follow the unit hit,
    /// 2 no limit on nearby explosions of the same type).
    /// </summary>
    [Serializable]
    public sealed class EffectDef
    {
        public int Duration;
        public int Sound;
        public int SoundChance;
        public bool Flash;
        public bool UsesModel;
        public bool AreaDamage;
        public int TextureMode;
        public float DamagePerTick;
        public float HeatPerTick;
        public int Flags;
    }

    /// <summary>The effect table as a <see cref="TableStore"/> file. The wrapper is what lets a list round-trip.</summary>
    [Serializable]
    public sealed class EffectTableFile
    {
        public List<EffectDef> Effects = new List<EffectDef>();

        public EffectTableFile() { }

        public EffectTableFile(List<EffectDef> effects) { Effects = effects; }
    }

    public static class EffectTable
    {
        const uint TableVa = 0x1009bb08;
        const int RecordSize = 0x2C;
        public const int Count = 36;

        /// <summary>Name of this table in a <see cref="TableStore"/>.</summary>
        public const string TableName = "effects";

        /// <summary>
        /// The effect table as it stands: the extracted file if there is one, otherwise read out of the game's
        /// mercsw.dll and written to the store so the library is not needed again. Edits to the file are kept.
        /// </summary>
        public static List<EffectDef> Get(string gameFolder, TableStore store = null)
        {
            if (store == null) store = TableStore.Default;
            return store.Extract(TableName,
                () => new EffectTableFile(Load(System.IO.Path.Combine(gameFolder, "mercsw.dll")))).Effects;
        }

        public static List<EffectDef> Load(string dllPath)
        {
            byte[] d = System.IO.File.ReadAllBytes(dllPath);
            int r0 = PeImage.FileOffset(d, TableVa);
            var list = new List<EffectDef>();
            for (int i = 0; i < Count; i++)
            {
                int r = r0 + i * RecordSize;
                list.Add(new EffectDef
                {
                    Duration = BitConverter.ToInt32(d, r),
                    Sound = BitConverter.ToInt32(d, r + 0x08),
                    SoundChance = BitConverter.ToInt32(d, r + 0x0C),
                    Flash = BitConverter.ToInt32(d, r + 0x10) != 0,
                    UsesModel = BitConverter.ToInt32(d, r + 0x14) != 0,
                    AreaDamage = BitConverter.ToInt32(d, r + 0x18) != 0,
                    TextureMode = BitConverter.ToInt32(d, r + 0x1C),
                    DamagePerTick = BitConverter.ToSingle(d, r + 0x20),
                    HeatPerTick = BitConverter.ToSingle(d, r + 0x24),
                    Flags = BitConverter.ToInt32(d, r + 0x28)
                });
            }
            return list;
        }
    }
}
