using System;
using System.IO;
using System.Text;

namespace MW2.Formats
{
    /// <summary>SNDS: SFLX blocks (0x1005b838) or RIFF PCM. Samples are normalized for AudioClip.SetData.</summary>
    public sealed class SoundSample
    {
        public int Rate;
        public int Channels;
        public float[] Samples;

        public static SoundSample Decode(byte[] data)
        {
            if (data == null || data.Length < 14) throw new InvalidDataException("Short SNDS resource");
            string magic = Encoding.ASCII.GetString(data, 0, 4);
            if (magic == "SFLX") return DecodeSflx(data);
            if (magic == "RIFF") return DecodeWave(data);
            throw new InvalidDataException("Unknown SNDS format: " + magic);
        }

        static SoundSample DecodeSflx(byte[] data)
        {
            int blocks = BitConverter.ToInt32(data, 8);
            int width = BitConverter.ToUInt16(data, 12);
            if (blocks < 1 || width < 2 || width > 4096 || (long)blocks * width > 20000000)
                throw new InvalidDataException("Invalid SFLX block dimensions");
            var samples = new float[blocks * width];
            var work = new byte[width];
            int pos = 14, previous = 0;
            for (int block = 0; block < blocks; block++)
            {
                if (pos >= data.Length) throw new InvalidDataException("Truncated SFLX block");
                int control = data[pos++], scale = control >> 6, mode = control & 15;
                if (scale > 2) throw new InvalidDataException("Invalid SFLX scale");
                int count = width >> scale;
                if (mode == 0)
                {
                    for (int i = 0; i < count; i++) work[i] = 128;
                    previous = 0;
                }
                else if (mode == 5)
                {
                    Require(data, pos, count);
                    Buffer.BlockCopy(data, pos, work, 0, count);
                    pos += count;
                    previous = work[count - 1] - 128;
                }
                else if (mode >= 2 && mode <= 4)
                {
                    int bits = mode == 2 ? 1 : mode == 3 ? 2 : 4;
                    int choices = 1 << bits;
                    Require(data, pos, choices + (count * bits + 7) / 8);
                    var delta = new int[choices];
                    for (int i = 0; i < choices; i++) delta[i] = data[pos++] * 2 - 128;
                    int packed = pos;
                    for (int i = 0; i < count; i++)
                    {
                        int shift = (i * bits) & 7;
                        int code = (data[packed + (i * bits >> 3)] >> shift) & (choices - 1);
                        previous = Math.Max(-127, Math.Min(127, previous + delta[code]));
                        work[i] = (byte)(previous + 128);
                    }
                    pos += (count * bits + 7) / 8;
                }
                else if (mode != 1) throw new InvalidDataException("Unknown SFLX block mode " + mode);

                // 0x1005bacf / 0x1005ba77 interpolate the half/quarter-rate blocks.
                if (scale == 1)
                    for (int i = count - 1; i >= 0; i--)
                    {
                        int a = work[i], b = work[Math.Min(i + 1, width - 1)];
                        work[i * 2] = (byte)a;
                        work[i * 2 + 1] = (byte)((a + b) >> 1);
                    }
                else if (scale == 2)
                    for (int i = count - 1; i >= 0; i--)
                    {
                        int a = work[i], b = work[Math.Min(i + 1, width - 1)];
                        int middle = (a + b) >> 1;
                        work[i * 4] = (byte)a;
                        work[i * 4 + 1] = (byte)((middle + a) >> 1);
                        work[i * 4 + 2] = (byte)middle;
                        work[i * 4 + 3] = (byte)((middle + b) >> 1);
                    }
                for (int i = 0; i < width; i++) samples[block * width + i] = (work[i] - 128) / 128f;
            }
            return new SoundSample { Rate = 11025, Channels = 1, Samples = samples };
        }

        static SoundSample DecodeWave(byte[] data)
        {
            if (Encoding.ASCII.GetString(data, 8, 4) != "WAVE") throw new InvalidDataException("Not a WAV resource");
            int rate = 0, channels = 0, bits = 0, payload = -1, length = 0;
            for (int pos = 12; pos + 8 <= data.Length;)
            {
                string tag = Encoding.ASCII.GetString(data, pos, 4);
                int size = BitConverter.ToInt32(data, pos + 4);
                pos += 8;
                if (size < 0 || size > data.Length - pos) throw new InvalidDataException("Invalid WAV chunk");
                if (tag == "fmt " && size >= 16)
                {
                    if (BitConverter.ToUInt16(data, pos) != 1) throw new InvalidDataException("WAV is not PCM");
                    channels = BitConverter.ToUInt16(data, pos + 2);
                    rate = BitConverter.ToInt32(data, pos + 4);
                    bits = BitConverter.ToUInt16(data, pos + 14);
                }
                else if (tag == "data") { payload = pos; length = size; }
                pos += size + (size & 1);
            }
            if (payload < 0 || rate < 1000 || channels < 1 || channels > 2 || (bits != 8 && bits != 16))
                throw new InvalidDataException("Unsupported WAV sample");
            int count = length / (bits / 8);
            var samples = new float[count];
            for (int i = 0; i < count; i++) samples[i] = bits == 8
                ? (data[payload + i] - 128) / 128f
                : BitConverter.ToInt16(data, payload + i * 2) / 32768f;
            return new SoundSample { Rate = rate, Channels = channels, Samples = samples };
        }

        static void Require(byte[] data, int start, int count)
        {
            if (start < 0 || count < 0 || start > data.Length - count) throw new InvalidDataException("Truncated SFLX data");
        }
    }
}
