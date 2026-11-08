using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace MW2.Formats
{
    /// <summary>
    /// Sound tables read from the game's mercsw.dll.
    /// Sample properties (0x1008b900, 0x8c records of four i16: SNDS id, priority, copies, playback rate; copied into
    /// DAT_10265040 by 0x1000fe10). Miles uses them in 0x10006687 / 0x10006839: the priority replaces the caller's
    /// (low byte of the sample flags), a sample already playing "copies" times is refused, and the rate replaces the
    /// base 11025 Hz (DAT_1008921c). -1 = not set.
    /// Radio messages are pairs of {SNDS id, text, 0} records (0xc bytes): the cockpit computer ("Betty",
    /// 0x10010ab9: first 0x1008cba0, second 0x1008cb78), critical hits (0x10010b01: "Critical hit : " 0x1008cc24 +
    /// 0x1008cd78), lance points (0x100107cc: 0x1008ca70) and lance replies (0x10010985: 0x1008ca98).
    /// </summary>
    public sealed class SoundTables
    {
        /// <summary>Mech_Destroy 0x10035ef6: computer line selected by gamepiece +0; -1 has no line.</summary>
        public static int DestroyedComputerLine(int gamepieceType) => gamepieceType switch
        {
            1 => 0x15,
            2 or 5 or 8 => 0x17,
            3 => 0x19,
            4 => 0x16,
            _ => -1
        };

        public struct SampleInfo
        {
            public short Priority, Copies, Rate;
        }

        public struct RadioPart
        {
            public int Sound;
            public string Text;
        }

        const uint SampleTableVa = 0x1008b900;
        const int SampleCount = 0x8c;

        public readonly Dictionary<int, SampleInfo> Samples = new Dictionary<int, SampleInfo>();
        public RadioPart[] Computer = new RadioPart[0];     // 0x1008cba0, 0x27 messages
        public RadioPart[] ComputerState = new RadioPart[0]; // 0x1008cb78: 0 / 2 "disengaged", 1 "engaged"
        public RadioPart CriticalHit;                        // 0x1008cc24 "Critical hit : "
        public RadioPart[] CriticalParts = new RadioPart[0]; // 0x1008cd78
        public RadioPart[] LancePoints = new RadioPart[0];   // 0x1008ca70 "All points", "Point 2", "Point 3"
        public RadioPart[] LanceReplies = new RadioPart[0];  // 0x1008ca98
        public RadioPart[][] PilotVoices = new RadioPart[0][]; // pointer table 0x1008c9c0, 33 pilots × 8 replies

        /// <summary>
        /// Gait change sounds of the player's mech (0x10090618, used by 0x10028c64): entry 1 + (current + 1) * 4 + target
        /// for the animation states -1 stop, 0 walk, 1 run, 2 reverse; -1 = none.
        /// </summary>
        public int[] GaitSounds = new int[0];

        /// <summary>Name of the extracted file in a <see cref="TableStore"/>.</summary>
        public const string TableName = "sound";

        /// <summary>
        /// The tables as the engine uses them: the extracted sound.json, written from mercsw.dll on first use.
        /// </summary>
        public static SoundTables Get(string gameFolder, TableStore store = null)
        {
            if (store == null) store = TableStore.Default;
            var file = store.Extract(TableName, () => SoundTableFile.From(Load(Path.Combine(gameFolder, "mercsw.dll"))));
            if (file.Format != SoundTableFile.CurrentFormat)
                throw new InvalidDataException($"{store.PathFor(TableName)} has format {file.Format}, this build reads " +
                    $"{SoundTableFile.CurrentFormat}: move it aside (keeping your edits) and run MW2 -> Tables -> Extract missing.");
            return file.ToTables();
        }

        /// <summary>The 0x8c raw sample records of 0x1008b900 (Load keeps those with an id in 1..0x4af, as 0x1000fe10).</summary>
        public readonly List<(short id, short priority, short copies, short rate)> SampleRecords = new List<(short, short, short, short)>();

        internal void AddSampleRecord(int id, int priority, int copies, int rate) =>
            AddSample((short)id, (short)priority, (short)copies, (short)rate);

        void AddSample(short id, short priority, short copies, short rate)
        {
            SampleRecords.Add((id, priority, copies, rate));
            if (id <= 0 || id >= 0x4b0) return;
            Samples[id] = new SampleInfo { Priority = priority, Copies = copies, Rate = rate };
        }

        public static SoundTables Load(string dllPath)
        {
            byte[] d = File.ReadAllBytes(dllPath);
            var t = new SoundTables();
            int o = PeImage.FileOffset(d, SampleTableVa);
            for (int i = 0; i < SampleCount; i++, o += 8)
                t.AddSample(BitConverter.ToInt16(d, o), BitConverter.ToInt16(d, o + 2), BitConverter.ToInt16(d, o + 4),
                    BitConverter.ToInt16(d, o + 6));
            t.Computer = ReadParts(d, 0x1008cba0, 0x27);
            t.ComputerState = ReadParts(d, 0x1008cb78, 3);
            t.CriticalHit = ReadParts(d, 0x1008cc24, 1)[0];
            t.CriticalParts = ReadParts(d, 0x1008cd78, 0x26);
            t.LancePoints = ReadParts(d, 0x1008ca70, 3);
            t.LanceReplies = ReadParts(d, 0x1008ca98, 11);
            t.PilotVoices = new RadioPart[33][];
            for (int i = 0; i < t.PilotVoices.Length; i++)
                t.PilotVoices[i] = ReadParts(d, BitConverter.ToUInt32(d, PeImage.FileOffset(d, 0x1008c9c0u + (uint)(i * 4))), 8);
            t.GaitSounds = new int[16];
            int g = PeImage.FileOffset(d, 0x10090618);
            for (int i = 0; i < 16; i++) t.GaitSounds[i] = BitConverter.ToInt32(d, g + i * 4);
            return t;
        }

        static RadioPart[] ReadParts(byte[] d, uint va, int count)
        {
            var parts = new RadioPart[count];
            for (int i = 0; i < count; i++)
            {
                int o = PeImage.FileOffset(d, va + (uint)(i * 0xc));
                uint text = BitConverter.ToUInt32(d, o + 4);
                parts[i] = new RadioPart { Sound = BitConverter.ToInt32(d, o), Text = text == 0 ? null : CString(d, text) };
            }
            return parts;
        }

        static string CString(byte[] d, uint va)
        {
            int o = PeImage.FileOffset(d, va);
            int end = o;
            while (end < d.Length && d[end] != 0) end++;
            return Encoding.ASCII.GetString(d, o, end - o);
        }
    }

    /// <summary>A sample record of 0x1008b900: SNDS id, priority, copies, playback rate (-1 not set).</summary>
    [Serializable]
    public sealed class SoundSampleDef
    {
        public int Id, Priority, Copies, Rate;
    }

    /// <summary>A radio message part: SNDS id and text ("" for none; no retail part has a null or empty text).</summary>
    [Serializable]
    public sealed class RadioPartDef
    {
        public int Sound;
        public string Text;
    }

    /// <summary>One pilot's eight replies (pointer table 0x1008c9c0).</summary>
    [Serializable]
    public sealed class PilotVoiceDef
    {
        public List<RadioPartDef> Replies = new List<RadioPartDef>();
    }

    /// <summary>The sound tables of mercsw.dll as the extracted sound.json; the radio tables keep their order.</summary>
    [Serializable]
    public sealed class SoundTableFile
    {
        public const int CurrentFormat = 1;
        public int Format = CurrentFormat;
        public List<SoundSampleDef> Samples = new List<SoundSampleDef>();
        public List<RadioPartDef> Computer = new List<RadioPartDef>();
        public List<RadioPartDef> ComputerState = new List<RadioPartDef>();
        public RadioPartDef CriticalHit = new RadioPartDef();
        public List<RadioPartDef> CriticalParts = new List<RadioPartDef>();
        public List<RadioPartDef> LancePoints = new List<RadioPartDef>();
        public List<RadioPartDef> LanceReplies = new List<RadioPartDef>();
        public List<PilotVoiceDef> PilotVoices = new List<PilotVoiceDef>();
        public int[] GaitSounds = new int[16];

        static RadioPartDef Def(SoundTables.RadioPart p) => new RadioPartDef { Sound = p.Sound, Text = p.Text ?? "" };
        static List<RadioPartDef> Defs(SoundTables.RadioPart[] parts)
        {
            var list = new List<RadioPartDef>();
            foreach (var p in parts) list.Add(Def(p));
            return list;
        }
        static SoundTables.RadioPart Part(RadioPartDef p) =>
            new SoundTables.RadioPart { Sound = p.Sound, Text = string.IsNullOrEmpty(p.Text) ? null : p.Text };
        static SoundTables.RadioPart[] Parts(List<RadioPartDef> defs) => defs.ConvertAll(Part).ToArray();

        public static SoundTableFile From(SoundTables t)
        {
            var f = new SoundTableFile();
            foreach (var s in t.SampleRecords) f.Samples.Add(new SoundSampleDef { Id = s.id, Priority = s.priority, Copies = s.copies, Rate = s.rate });
            f.Computer = Defs(t.Computer);
            f.ComputerState = Defs(t.ComputerState);
            f.CriticalHit = Def(t.CriticalHit);
            f.CriticalParts = Defs(t.CriticalParts);
            f.LancePoints = Defs(t.LancePoints);
            f.LanceReplies = Defs(t.LanceReplies);
            foreach (var v in t.PilotVoices) f.PilotVoices.Add(new PilotVoiceDef { Replies = Defs(v) });
            f.GaitSounds = (int[])t.GaitSounds.Clone();
            return f;
        }

        public SoundTables ToTables()
        {
            var t = new SoundTables();
            foreach (var s in Samples) t.AddSampleRecord(s.Id, s.Priority, s.Copies, s.Rate);
            t.Computer = Parts(Computer);
            t.ComputerState = Parts(ComputerState);
            t.CriticalHit = Part(CriticalHit);
            t.CriticalParts = Parts(CriticalParts);
            t.LancePoints = Parts(LancePoints);
            t.LanceReplies = Parts(LanceReplies);
            t.PilotVoices = PilotVoices.ConvertAll(v => Parts(v.Replies)).ToArray();
            t.GaitSounds = (int[])GaitSounds.Clone();
            return t;
        }
    }
}
