using System;
using System.Text;

namespace MW2.Formats
{
    /// <summary>
    /// In-mission network messages of mercsw.dll, byte for byte. Two ASCII tag bytes, then the body; multi-byte values
    /// are little-endian and unaligned. Reliable delivery is the dpSend flag 1 (0x100434b0 / 0x100434e0).
    /// DA 0x8d 0x10020ed3 / 0x10021376 unit state; WE 5 0x10021d7e / 0x10021e47 weapons fired;
    /// KI 0x12 0x10021ef2 / 0x100220c1 killed-by row; CO 0x1a 0x10022122 / 0x1002223a unit collision;
    /// SN 0x27 0x10022495 / 0x1002257b destroyed GT bits; GO, LA, SU, SS 2 bytes; CH text 0x10021b76 / 0x10021d0a.
    /// </summary>
    public static class NetPackets
    {
        public const int StateSize = 0x8d, WeaponsSize = 5, KillsSize = 0x12, CollisionSize = 0x1a, SnapshotSize = 0x27, TagSize = 2;
        public const int ScenerySlots = 0xfe;
        public const int MaxChat = 0x50;

        /// <summary>fld, fmul 65536 (_DAT_10087230), fadd 0.5 (_DAT_10087234), ftol: truncation toward zero after adding one half.</summary>
        public static int Fixed(float value) => (int)((double)value * 65536.0 + 0.5);
        public static float FromFixed(int value) => (float)value * 1.52587890625e-05f;     // _DAT_10087238

        /// <summary>The unit state body of FUN_10020ed3. Armour values keep their float bits.</summary>
        public sealed class UnitState
        {
            public byte Index;
            public short Target;        // gp +0xdc
            public short Attacker;      // gp +0x13c
            public ushort Flags;        // +7, see ComposeFlags
            public readonly float[] Front = new float[8];       // section +0
            public readonly float[] Rear = new float[3];        // section +4 of sections 1, 2, 3
            public readonly float[] Internal = new float[8];    // section +8
            public int Time;            // +0x55 DAT_100a663c
            public int X, Y, Z;         // gp +0x50..+0x58
            public int RotX, RotY, RotZ;    // gp +0x5c..+0x64, 16.16
            public int OriginHeight;    // mech +0xe00
            public int GroundHeight;    // mech +0xe68
            public int Torso, TorsoPitch, Pitch;    // mech +0x2c, +0x4c, +0x38, 16.16
            public int Throttle, Turn;  // mech +0x08, +0x18, 16.16
        }

        /// <summary>
        /// Flag word of FUN_10020ed3: low nibble mech state +0xdd4, 0x10 control +0x30, 0x20..0x200 control +0x1d..+0x21
        /// only while state 2 with a positive +0xdf4, 0x400 a scenery target this team has marked, 0x800 mech +0xe40 bit 0x80,
        /// high nibble mech +0xe42.
        /// </summary>
        public static ushort ComposeFlags(int state, bool control30, bool[] control1d, bool targetMarked, bool flag80, int flagsE42, int timerDf4)
        {
            int flags = state & 0xf;
            if (targetMarked) flags |= 0x400;
            if (flag80) flags |= 0x800;
            if (control30) flags |= 0x10;
            flags |= (flagsE42 << 12) & 0xffff;
            if (timerDf4 > 0 && state == 2)
                for (int i = 0; i < 5; i++)
                    if (control1d[i]) flags |= 0x20 << i;
            return (ushort)flags;
        }

        public static byte[] EncodeState(UnitState s)
        {
            var b = new byte[StateSize];
            Tag(b, "DA");
            b[2] = s.Index;
            PutShort(b, 3, s.Target);
            PutShort(b, 5, s.Attacker);
            PutShort(b, 7, (short)s.Flags);
            PutFloat(b, 0x11, s.Rear[0]);
            PutFloat(b, 0x0d, s.Rear[1]);
            PutFloat(b, 0x09, s.Rear[2]);
            for (int i = 0; i < 8; i++)
            {
                PutFloat(b, 0x15 + i * 4, s.Front[i]);
                PutFloat(b, 0x35 + i * 4, s.Internal[i]);
            }
            PutInt(b, 0x55, s.Time);
            PutInt(b, 0x59, s.X); PutInt(b, 0x5d, s.Y); PutInt(b, 0x61, s.Z);
            PutInt(b, 0x65, s.RotX); PutInt(b, 0x69, s.RotY); PutInt(b, 0x6d, s.RotZ);
            PutInt(b, 0x71, s.OriginHeight); PutInt(b, 0x75, s.GroundHeight);
            PutInt(b, 0x79, s.Torso); PutInt(b, 0x7d, s.TorsoPitch); PutInt(b, 0x81, s.Pitch);
            PutInt(b, 0x85, s.Throttle); PutInt(b, 0x89, s.Turn);
            return b;
        }

        public static UnitState DecodeState(byte[] b)
        {
            var s = new UnitState
            {
                Index = b[2],
                Target = BitConverter.ToInt16(b, 3),
                Attacker = BitConverter.ToInt16(b, 5),
                Flags = BitConverter.ToUInt16(b, 7),
                Time = BitConverter.ToInt32(b, 0x55),
                X = BitConverter.ToInt32(b, 0x59), Y = BitConverter.ToInt32(b, 0x5d), Z = BitConverter.ToInt32(b, 0x61),
                RotX = BitConverter.ToInt32(b, 0x65), RotY = BitConverter.ToInt32(b, 0x69), RotZ = BitConverter.ToInt32(b, 0x6d),
                OriginHeight = BitConverter.ToInt32(b, 0x71), GroundHeight = BitConverter.ToInt32(b, 0x75),
                Torso = BitConverter.ToInt32(b, 0x79), TorsoPitch = BitConverter.ToInt32(b, 0x7d), Pitch = BitConverter.ToInt32(b, 0x81),
                Throttle = BitConverter.ToInt32(b, 0x85), Turn = BitConverter.ToInt32(b, 0x89)
            };
            s.Rear[0] = BitConverter.ToSingle(b, 0x11);
            s.Rear[1] = BitConverter.ToSingle(b, 0x0d);
            s.Rear[2] = BitConverter.ToSingle(b, 0x09);
            for (int i = 0; i < 8; i++)
            {
                s.Front[i] = BitConverter.ToSingle(b, 0x15 + i * 4);
                s.Internal[i] = BitConverter.ToSingle(b, 0x35 + i * 4);
            }
            return s;
        }

        /// <summary>FUN_10021d7e: bit n = weapon slot n fired since the last send; nothing is sent for an empty mask.</summary>
        public static byte[] EncodeWeapons(byte index, ushort mask)
        {
            var b = new byte[WeaponsSize];
            Tag(b, "WE");
            b[2] = index;
            PutShort(b, 3, (short)mask);
            return b;
        }

        /// <summary>FUN_10021ef2: how many times each player killed the sender. The row stops at the first empty player slot.</summary>
        public static byte[] EncodeKills(short[] killedBy, int players)
        {
            var b = new byte[KillsSize];
            Tag(b, "KI");
            for (int i = 0; i < 8 && i < players; i++) PutShort(b, 2 + i * 2, killedBy[i]);
            return b;
        }

        /// <summary>FUN_10022122 to one player: the collision vector negated (fchs at 0x1002213e) and the sender's mech +0xe34.. velocity, 16.16.</summary>
        public static byte[] EncodeCollision(float x, float y, float z, float vx, float vy, float vz)
        {
            var b = new byte[CollisionSize];
            Tag(b, "CO");
            PutInt(b, 2, Fixed(-x)); PutInt(b, 6, Fixed(-y)); PutInt(b, 10, Fixed(-z));
            PutInt(b, 14, Fixed(vx)); PutInt(b, 18, Fixed(vy)); PutInt(b, 22, Fixed(vz));
            return b;
        }

        /// <summary>
        /// FUN_10022495: sequence (+2, DAT_100a6c4c) then one bit per GT, first GT in the top bit. Only whole bytes are stored:
        /// GT 0xf8..0xfd stay in the unfinished byte and are never sent.
        /// </summary>
        public static byte[] EncodeSnapshot(int sequence, bool[] destroyed)
        {
            var b = new byte[SnapshotSize];
            Tag(b, "SN");
            PutInt(b, 2, sequence);
            int bit = 0, pos = 6, current = 0;
            for (int i = 0; i < ScenerySlots; i++)
            {
                current = (current << 1) & 0xff;
                if (destroyed[i]) current |= 1;
                if (++bit == 8)
                {
                    bit = 0;
                    b[pos++] = (byte)current;
                    current = 0;
                }
            }
            return b;
        }

        /// <summary>FUN_1002257b: reads the same bits; true entries are GTs the receiver marks destroyed.</summary>
        public static bool[] DecodeSnapshot(byte[] b, out int sequence)
        {
            sequence = BitConverter.ToInt32(b, 2);
            var result = new bool[ScenerySlots];
            for (int i = 0; i < ScenerySlots; i++)
            {
                int pos = 6 + i / 8;
                if (pos < b.Length) result[i] = (b[pos] & (0x80 >> (i % 8))) != 0;
            }
            return result;
        }

        public static byte[] EncodeTag(string tag)
        {
            var b = new byte[TagSize];
            Tag(b, tag);
            return b;
        }

        /// <summary>FUN_10021b76: tag, NUL-terminated text, size = length + 3; refused from 0x50 characters.</summary>
        public static byte[] EncodeChat(string text)
        {
            if (text == null || text.Length >= MaxChat) return null;
            var b = new byte[text.Length + 3];
            Tag(b, "CH");
            Encoding.ASCII.GetBytes(text, 0, text.Length, b, 2);
            return b;
        }

        /// <summary>FUN_10021d0a cuts the received text at byte +0x51 before formatting it.</summary>
        public static string DecodeChat(byte[] b, int size)
        {
            int end = 2;
            int limit = Math.Min(size, 0x51);
            while (end < limit && b[end] != 0) end++;
            return Encoding.ASCII.GetString(b, 2, end - 2);
        }

        /// <summary>
        /// FUN_10021f74: score of each player from the kill table DAT_101fd732[victim * 2 + killer * 0x10] (Mech_Destroy 0x10035ef6
        /// increments it). A kill adds one to the killer, a kill of the killer's own team (or of himself) takes one away. The table
        /// is read while both slots hold a player, stopping at the first empty one.
        /// </summary>
        public static void Scores(short[,] killedBy, int[] teams, int players, short[] scores)
        {
            for (int i = 0; i < 8; i++) scores[i] = 0;
            for (int victim = 0; victim < 8 && victim < players; victim++)
                for (int killer = 0; killer < 8 && killer < players; killer++)
                    if (teams[victim] == teams[killer]) scores[killer] -= killedBy[killer, victim];
                    else scores[killer] += killedBy[killer, victim];
        }

        static void Tag(byte[] b, string tag) { b[0] = (byte)tag[0]; b[1] = (byte)tag[1]; }
        static void PutShort(byte[] b, int at, short v) { b[at] = (byte)v; b[at + 1] = (byte)(v >> 8); }
        static void PutInt(byte[] b, int at, int v) { b[at] = (byte)v; b[at + 1] = (byte)(v >> 8); b[at + 2] = (byte)(v >> 16); b[at + 3] = (byte)(v >> 24); }
        static void PutFloat(byte[] b, int at, float v) { Buffer.BlockCopy(BitConverter.GetBytes(v), 0, b, at, 4); }
    }
}
