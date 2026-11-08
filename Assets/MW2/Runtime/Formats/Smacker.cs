using System;
using System.Collections.Generic;

namespace MW2.Formats
{
    /// <summary>
    /// Smacker video (SMK2) as the Mercenaries shell plays it through SMACKW32.DLL: screen animations, briefings, news and
    /// end flics in the game's SMK folder. All 185 files are SMK2 without flags; briefings carry one 16-bit Huffman-DPCM audio
    /// track at 22050 Hz (MEND stereo). The format is RAD Game Tools' public Smacker layout (the shell only calls the library):
    /// 0x68-byte header, frame sizes and types, four Huffman trees (MMAP, MCLR, FULL, TYPE), then per frame an optional
    /// palette, audio chunks and 4x4 block video.
    /// </summary>
    public sealed class Smacker
    {
        public int Width, Height, FrameCount, Flags;
        /// <summary>Header +0x10: positive milliseconds per frame, negative in 10 microsecond units, 0 means 10 frames a second.</summary>
        public int FrameRate;
        public readonly uint[] AudioRate = new uint[7];
        public double MillisecondsPerFrame => FrameRate > 0 ? FrameRate : FrameRate < 0 ? -FrameRate / 100.0 : 100.0;

        readonly byte[] _d;
        readonly int[] _frameOffset, _frameSize;
        readonly byte[] _frameType;
        readonly Tree _mmap, _mclr, _full, _type;
        int _next;

        /// <summary>Current frame as palette indices, rows top-down, and the current palette (RGB, 256 entries).</summary>
        public readonly byte[] Pixels;
        public readonly byte[] Palette = new byte[768];
        /// <summary>Decoded audio of track 0 for the frames decoded so far (16-bit samples, interleaved when stereo).</summary>
        public readonly List<short> Audio = new List<short>();
        public int AudioChannels => (AudioRate[0] & 0x10000000) != 0 ? 2 : 1;
        public int AudioHz => (int)(AudioRate[0] & 0xffffff);

        public Smacker(byte[] data)
        {
            _d = data;
            if (data[0] != 'S' || data[1] != 'M' || data[2] != 'K' || (data[3] != '2' && data[3] != '4'))
                throw new FormatException("Not a Smacker file");
            Width = I(4); Height = I(8); FrameCount = I(0xc); FrameRate = I(0x10); Flags = I(0x14);
            for (int k = 0; k < 7; k++) AudioRate[k] = (uint)I(0x48 + k * 4);
            int treesSize = I(0x34);
            int frames = FrameCount + ((Flags & 1) != 0 ? 1 : 0);
            _frameSize = new int[frames];
            _frameType = new byte[frames];
            _frameOffset = new int[frames];
            int p = 0x68;
            for (int k = 0; k < frames; k++, p += 4) _frameSize[k] = I(p) & ~3;
            for (int k = 0; k < frames; k++) _frameType[k] = data[p++];
            var bits = new BitReader(data, p, treesSize);
            _mmap = Tree.ReadHeaderTree(bits, I(0x38));
            _mclr = Tree.ReadHeaderTree(bits, I(0x3c));
            _full = Tree.ReadHeaderTree(bits, I(0x40));
            _type = Tree.ReadHeaderTree(bits, I(0x44));
            p += treesSize;
            for (int k = 0; k < frames; k++) { _frameOffset[k] = p; p += _frameSize[k]; }
            Pixels = new byte[Width * Height];
        }

        int I(int o) => BitConverter.ToInt32(_d, o);

        public int FrameIndex => _next;

        /// <summary>Decodes the next frame into Pixels / Palette; false after the last one.</summary>
        public bool NextFrame()
        {
            if (_next >= FrameCount) return false;
            int p = _frameOffset[_next], end = p + _frameSize[_next];
            byte type = _frameType[_next];
            _next++;
            if ((type & 1) != 0)
            {
                int size = _d[p] * 4;
                DecodePalette(p + 1, p + size);
                p += size;
            }
            for (int k = 0; k < 7; k++)
            {
                if ((type & (2 << k)) == 0) continue;
                int len = I(p);
                if (k == 0 && (AudioRate[0] & 0x80000000) != 0) DecodeAudio(p + 4, len - 4);
                else if (k == 0) for (int i = p + 4; i + 1 < p + len; i += 2) Audio.Add(BitConverter.ToInt16(_d, i));
                p += len;
            }
            DecodeVideo(new BitReader(_d, p, end - p));
            return true;
        }

        static readonly byte[] Pal6 = BuildPal6();
        static byte[] BuildPal6()
        {
            var t = new byte[64];
            for (int i = 0; i < 64; i++) t[i] = (byte)((i * 255 + 31) / 63);
            return t;
        }

        void DecodePalette(int p, int end)
        {
            var old = (byte[])Palette.Clone();
            int sz = 0;
            while (sz < 256 && p < end)
            {
                int t = _d[p++];
                if ((t & 0x80) != 0) sz += (t & 0x7f) + 1;
                else if ((t & 0x40) != 0)
                {
                    int off = _d[p++];
                    int j = (t & 0x3f) + 1;
                    while (j-- > 0 && sz < 256)
                    {
                        Palette[sz * 3] = old[off * 3]; Palette[sz * 3 + 1] = old[off * 3 + 1]; Palette[sz * 3 + 2] = old[off * 3 + 2];
                        sz++; off++;
                    }
                }
                else
                {
                    Palette[sz * 3] = Pal6[t & 0x3f];
                    Palette[sz * 3 + 1] = Pal6[_d[p++] & 0x3f];
                    Palette[sz * 3 + 2] = Pal6[_d[p++] & 0x3f];
                    sz++;
                }
            }
        }

        static readonly int[] BlockRuns = BuildRuns();
        static int[] BuildRuns()
        {
            var r = new int[64];
            for (int i = 0; i < 59; i++) r[i] = i + 1;
            r[59] = 128; r[60] = 256; r[61] = 512; r[62] = 1024; r[63] = 2048;
            return r;
        }

        void DecodeVideo(BitReader bits)
        {
            _mmap.ResetLast(); _mclr.ResetLast(); _full.ResetLast(); _type.ResetLast();
            int bw = Width / 4, blocks = bw * (Height / 4), blk = 0, stride = Width;
            bool v4 = _d[3] == '4';
            while (blk < blocks)
            {
                int type = _type.Get(bits);
                int run = BlockRuns[(type >> 2) & 0x3f];
                switch (type & 3)
                {
                    case 0:
                        while (run-- > 0 && blk < blocks)
                        {
                            int clr = _mclr.Get(bits), map = _mmap.Get(bits);
                            byte hi = (byte)(clr >> 8), lo = (byte)clr;
                            int o = (blk / bw) * 4 * stride + (blk % bw) * 4;
                            for (int i = 0; i < 4; i++, o += stride, map >>= 4)
                            {
                                Pixels[o] = (map & 1) != 0 ? hi : lo;
                                Pixels[o + 1] = (map & 2) != 0 ? hi : lo;
                                Pixels[o + 2] = (map & 4) != 0 ? hi : lo;
                                Pixels[o + 3] = (map & 8) != 0 ? hi : lo;
                            }
                            blk++;
                        }
                        break;
                    case 1:
                    {
                        int mode = 0;
                        if (v4)
                        {
                            if (bits.Bit() != 0) mode = 1;
                            else if (bits.Bit() != 0) mode = 2;
                        }
                        while (run-- > 0 && blk < blocks)
                        {
                            int o = (blk / bw) * 4 * stride + (blk % bw) * 4;
                            if (mode == 0)
                                for (int i = 0; i < 4; i++, o += stride)
                                {
                                    int p1 = _full.Get(bits);
                                    Pixels[o + 2] = (byte)p1; Pixels[o + 3] = (byte)(p1 >> 8);
                                    int p2 = _full.Get(bits);
                                    Pixels[o] = (byte)p2; Pixels[o + 1] = (byte)(p2 >> 8);
                                }
                            else if (mode == 1)
                                for (int i = 0; i < 2; i++)
                                {
                                    int p1 = _full.Get(bits);
                                    for (int r = 0; r < 2; r++, o += stride)
                                    {
                                        Pixels[o] = Pixels[o + 1] = (byte)p1;
                                        Pixels[o + 2] = Pixels[o + 3] = (byte)(p1 >> 8);
                                    }
                                }
                            else
                                for (int i = 0; i < 2; i++)
                                {
                                    int p1 = _full.Get(bits), p2 = _full.Get(bits);
                                    for (int r = 0; r < 2; r++, o += stride)
                                    {
                                        Pixels[o + 2] = (byte)p1; Pixels[o + 3] = (byte)(p1 >> 8);
                                        Pixels[o] = (byte)p2; Pixels[o + 1] = (byte)(p2 >> 8);
                                    }
                                }
                            blk++;
                        }
                        break;
                    }
                    case 2:
                        blk += run;
                        break;
                    case 3:
                    {
                        byte c = (byte)(type >> 8);
                        while (run-- > 0 && blk < blocks)
                        {
                            int o = (blk / bw) * 4 * stride + (blk % bw) * 4;
                            for (int i = 0; i < 4; i++, o += stride)
                                Pixels[o] = Pixels[o + 1] = Pixels[o + 2] = Pixels[o + 3] = c;
                            blk++;
                        }
                        break;
                    }
                }
            }
        }

        /// <summary>Huffman DPCM audio chunk: u32 unpacked size, flag bits, 1/2/4 byte trees, start values, then deltas.</summary>
        void DecodeAudio(int p, int length)
        {
            if (length < 4) return;
            int unpacked = I(p);
            var bits = new BitReader(_d, p + 4, length - 4);
            if (bits.Bit() == 0) return;
            int stereo = bits.Bit();
            int sixteen = bits.Bit();
            var trees = new SmallTree[1 << (stereo + sixteen)];
            for (int k = 0; k < trees.Length; k++) trees[k] = SmallTree.Read(bits);
            var pred = new int[2];
            if (sixteen != 0)
            {
                for (int i = stereo; i >= 0; i--)
                {
                    int v = bits.Bits(16);
                    pred[i] = (short)(((v & 0xff) << 8) | (v >> 8));
                }
                for (int i = 0; i <= stereo; i++) Audio.Add((short)pred[i]);
                int samples = unpacked / 2 - (stereo + 1);
                for (int n = 0; n < samples; n++)
                {
                    int ch = stereo != 0 ? n & 1 : 0;
                    int lo = trees[ch * 2].Get(bits), hi = trees[ch * 2 + 1].Get(bits);
                    pred[ch] = (short)(pred[ch] + (short)(lo | (hi << 8)));
                    Audio.Add((short)pred[ch]);
                }
            }
            else
            {
                for (int i = stereo; i >= 0; i--) pred[i] = bits.Bits(8);
                for (int i = 0; i <= stereo; i++) Audio.Add((short)((pred[i] - 128) << 8));
                int samples = unpacked - (stereo + 1);
                for (int n = 0; n < samples; n++)
                {
                    int ch = stereo != 0 ? n & 1 : 0;
                    pred[ch] = (pred[ch] + (sbyte)trees[ch].Get(bits)) & 0xff;
                    Audio.Add((short)((pred[ch] - 128) << 8));
                }
            }
        }

        /// <summary>Little-endian bit reader, least significant bit first.</summary>
        sealed class BitReader
        {
            readonly byte[] _d;
            readonly int _end;
            int _bit;

            public BitReader(byte[] d, int start, int length)
            {
                _d = d;
                _bit = start * 8;
                _end = Math.Min(d.Length, start + Math.Max(0, length)) * 8;
            }

            public int Bit()
            {
                if (_bit >= _end) return 0;
                int v = (_d[_bit >> 3] >> (_bit & 7)) & 1;
                _bit++;
                return v;
            }

            public int Bits(int n)
            {
                int v = 0;
                for (int i = 0; i < n; i++) v |= Bit() << i;
                return v;
            }
        }

        /// <summary>8-bit Huffman tree: presence bit, then 1 = node (0 branch, 1 branch), 0 = leaf with an 8-bit value; a closing bit.</summary>
        sealed class SmallTree
        {
            readonly List<int> _nodes = new List<int>();   // >= 0x100: node, value - 0x100 = index of the "1" branch; else leaf value
            bool _empty = true;

            public static SmallTree Read(BitReader bits)
            {
                var t = new SmallTree();
                if (bits.Bit() == 0) return t;
                t._empty = false;
                t.ReadNode(bits, 0);
                bits.Bit();
                return t;
            }

            void ReadNode(BitReader bits, int depth)
            {
                if (depth > 32) throw new FormatException("Smacker tree too deep");
                if (bits.Bit() == 0) { _nodes.Add(bits.Bits(8)); return; }
                int at = _nodes.Count;
                _nodes.Add(0);
                ReadNode(bits, depth + 1);
                _nodes[at] = 0x100 + _nodes.Count;
                ReadNode(bits, depth + 1);
            }

            public int Get(BitReader bits)
            {
                if (_empty) return 0;
                int i = 0;
                while (_nodes[i] >= 0x100) i = bits.Bit() != 0 ? _nodes[i] - 0x100 : i + 1;
                return _nodes[i];
            }
        }

        /// <summary>16-bit header tree built from two 8-bit trees and three escape codes whose leaves cache the last values.</summary>
        sealed class Tree
        {
            const int Node = unchecked((int)0x80000000);
            int[] _values;
            readonly int[] _last = { -1, -1, -1 };

            public static Tree ReadHeaderTree(BitReader bits, int size)
            {
                var t = new Tree();
                if (bits.Bit() == 0)
                {
                    t._values = new int[2];
                    t._last[0] = t._last[1] = t._last[2] = 1;
                    return t;
                }
                var lo = SmallTree.Read(bits);
                var hi = SmallTree.Read(bits);
                int e0 = bits.Bits(16), e1 = bits.Bits(16), e2 = bits.Bits(16);
                var values = new List<int>(((size + 3) >> 2) + 4);
                t.ReadBig(bits, lo, hi, e0, e1, e2, values, 0);
                bits.Bit();
                for (int k = 0; k < 3; k++)
                    if (t._last[k] < 0) { t._last[k] = values.Count; values.Add(0); }
                t._values = values.ToArray();
                return t;
            }

            int ReadBig(BitReader bits, SmallTree lo, SmallTree hi, int e0, int e1, int e2, List<int> values, int depth)
            {
                if (depth > 64) throw new FormatException("Smacker big tree too deep");
                if (bits.Bit() == 0)
                {
                    int val = lo.Get(bits) | (hi.Get(bits) << 8);
                    if (val == e0) { _last[0] = values.Count; val = 0; }
                    else if (val == e1) { _last[1] = values.Count; val = 0; }
                    else if (val == e2) { _last[2] = values.Count; val = 0; }
                    values.Add(val);
                    return 1;
                }
                int at = values.Count;
                values.Add(0);
                int r = ReadBig(bits, lo, hi, e0, e1, e2, values, depth + 1);
                values[at] = Node | r;
                r++;
                return r + ReadBig(bits, lo, hi, e0, e1, e2, values, depth + 1);
            }

            public void ResetLast()
            {
                _values[_last[0]] = 0; _values[_last[1]] = 0; _values[_last[2]] = 0;
            }

            public int Get(BitReader bits)
            {
                int i = 0;
                while ((_values[i] & Node) != 0)
                {
                    if (bits.Bit() != 0) i += _values[i] & ~Node;
                    i++;
                }
                int v = _values[i];
                if (v != _values[_last[0]])
                {
                    _values[_last[2]] = _values[_last[1]];
                    _values[_last[1]] = _values[_last[0]];
                    _values[_last[0]] = v;
                }
                return v;
            }
        }
    }
}
