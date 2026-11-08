using System;
using System.IO;

namespace MW2.Shell
{
    /// <summary>
    /// Packed shell database (database.cpp): DATABASE.MW2 and SHELLFIX.MW2 in the game folder (or the smk folder of the CD path).
    /// FUN_1001abc3 0x1001abc3 reads u32 count and count u32 offsets; record sizes run to the next offset (the last to the end of
    /// file). FUN_1001afa0 0x1001afa0 numbers records from 1 (record n is entry n - 1). FUN_1001aff5 0x1001aff5 returns a record as
    /// it is, FUN_1001b0ed 0x1001b0ed unpacks an LZSS record.
    /// </summary>
    public sealed class ShellDatabase
    {
        readonly byte[] _d;
        readonly int[] _offset, _size;

        public int Count => _offset.Length;

        public ShellDatabase(string path)
        {
            _d = File.ReadAllBytes(path);
            int n = BitConverter.ToInt32(_d, 0);
            _offset = new int[n];
            _size = new int[n];
            for (int i = 0; i < n; i++) _offset[i] = BitConverter.ToInt32(_d, 4 + i * 4);
            for (int i = 0; i < n; i++) _size[i] = (i + 1 < n ? _offset[i + 1] : _d.Length) - _offset[i];
        }

        /// <summary>FUN_1001aff5: record n (1-based) as stored; null when n is out of range.</summary>
        public byte[] Raw(int n)
        {
            if (n < 1 || n > Count) return null;
            var r = new byte[_size[n - 1]];
            Buffer.BlockCopy(_d, _offset[n - 1], r, 0, r.Length);
            return r;
        }

        /// <summary>
        /// FUN_1001b0ed: u32 unpacked size, then flag bytes (least significant bit first; 1 = literal byte, 0 = two-byte reference:
        /// ring position in the low 12 bits, length = high 4 bits + 3) over a zeroed 4096-byte ring written from position 0.
        /// </summary>
        public byte[] Unpack(int n)
        {
            if (n < 1 || n > Count) return null;
            int p = _offset[n - 1], end = p + _size[n - 1];
            int size = BitConverter.ToInt32(_d, p);
            p += 4;
            var ring = new byte[4096];
            int w = 0, o = 0, flags = 0;
            var output = new byte[size];
            while (o < size && p < end)
            {
                flags >>= 1;
                if ((flags & 0x100) == 0) flags = _d[p++] | 0xff00;
                if ((flags & 1) != 0)
                {
                    byte b = _d[p++];
                    ring[w] = b; output[o++] = b; w = (w + 1) & 0xfff;
                }
                else
                {
                    int v = _d[p] | (_d[p + 1] << 8);
                    p += 2;
                    int r = v & 0xfff, len = ((v >> 12) & 0xf) + 3;
                    for (int k = 0; k < len && o < size; k++)
                    {
                        byte b = ring[r];
                        ring[w] = b; output[o++] = b;
                        w = (w + 1) & 0xfff; r = (r + 1) & 0xfff;
                    }
                }
            }
            return output;
        }
    }

    /// <summary>
    /// 256-colour PCX of the shell backgrounds. FUN_10080a33 0x10080a33: rows of +0x42 bytes from +0x80, a byte with both top bits
    /// set repeats the next byte (low 6 bits) times, any other byte is a pixel; (ymax - ymin + 1) rows. FUN_10080ab5 0x10080ab5:
    /// the palette is the last 0x300 bytes, each component shifted right by 2 (6-bit VGA values). FUN_10080ae0: size from the
    /// header bounds.
    /// </summary>
    public sealed class ShellPcx
    {
        public int Width, Height;
        public byte[] Pixels;
        /// <summary>6-bit components (0..63), as the shell keeps them at surface +0x92.</summary>
        public readonly byte[] Palette6 = new byte[768];

        public static ShellPcx Decode(byte[] d)
        {
            var pcx = new ShellPcx
            {
                Width = BitConverter.ToInt16(d, 8) - BitConverter.ToInt16(d, 4) + 1,
                Height = BitConverter.ToInt16(d, 10) - BitConverter.ToInt16(d, 6) + 1
            };
            int bytesPerLine = BitConverter.ToUInt16(d, 0x42);
            pcx.Pixels = new byte[pcx.Width * pcx.Height];
            var line = new byte[bytesPerLine];
            int p = 0x80;
            for (int y = 0; y < pcx.Height; y++)
            {
                int x = 0;
                while (x < bytesPerLine && p < d.Length)
                {
                    int b = d[p++];
                    if ((b & 0xc0) == 0xc0)
                    {
                        int count = b & 0x3f;
                        byte v = d[p++];
                        for (int k = 0; k < count && x < bytesPerLine; k++) line[x++] = v;
                    }
                    else line[x++] = (byte)b;
                }
                Buffer.BlockCopy(line, 0, pcx.Pixels, y * pcx.Width, Math.Min(pcx.Width, bytesPerLine));
            }
            for (int i = 0; i < 768; i++) pcx.Palette6[i] = (byte)(d[d.Length - 768 + i] >> 2);
            return pcx;
        }
    }
}
