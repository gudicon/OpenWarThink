using System;

namespace MW2.Formats
{
    /// <summary>
    /// ANIM resource (loader mercsw.dll 0x1002439f, player 0x1000d2c0):
    ///   u32 trackCount, u32 frameCount;
    ///   per track: i32 channel, i32 type, frameCount * i32 delta;
    ///   frameCount * 8-byte frame records: u32 flags, i8 state[4].
    /// type 0/1/2 translate x/y/z by delta (model units); 3/4/5 rotate about x/y/z by delta/65536 degrees.
    /// Deltas are relative: each frame adds its value to what the joint still has to travel.
    /// </summary>
    public sealed class Anim
    {
        public sealed class Track
        {
            public int Channel;
            public int Type;
            public float[] Deltas;      // degrees for rotations, model units for translations
        }

        public struct Frame
        {
            public uint Flags;
            public sbyte S0, S1, S2, S3;

            public sbyte State(int i) => i switch { 0 => S0, 1 => S1, 2 => S2, _ => S3 };
        }

        public Track[] Tracks;
        public Frame[] Frames;

        public static Anim Decode(byte[] d)
        {
            int trackCount = BitConverter.ToInt32(d, 0);
            int frameCount = BitConverter.ToInt32(d, 4);
            var anim = new Anim { Tracks = new Track[trackCount], Frames = new Frame[frameCount] };
            int pos = 8;
            for (int t = 0; t < trackCount; t++)
            {
                var track = new Track
                {
                    Channel = BitConverter.ToInt32(d, pos),
                    Type = BitConverter.ToInt32(d, pos + 4),
                    Deltas = new float[frameCount]
                };
                for (int f = 0; f < frameCount; f++)
                {
                    int v = BitConverter.ToInt32(d, pos + 8 + f * 4);
                    track.Deltas[f] = track.Type >= 3 ? v / 65536f : v;
                }
                anim.Tracks[t] = track;
                pos += 8 + frameCount * 4;
            }
            for (int f = 0; f < frameCount; f++, pos += 8)
            {
                anim.Frames[f] = new Frame
                {
                    Flags = BitConverter.ToUInt32(d, pos),
                    S0 = (sbyte)d[pos + 4], S1 = (sbyte)d[pos + 5], S2 = (sbyte)d[pos + 6], S3 = (sbyte)d[pos + 7]
                };
            }
            return anim;
        }
    }
}
