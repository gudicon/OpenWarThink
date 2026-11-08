using System;
using System.IO;

namespace MW2.Formats
{
    /// <summary>
    /// Maps a virtual address of one of the game's libraries to an offset in the file, through the PE section table.
    ///
    /// Several tables the engine needs were compiled into mercsw.dll and MW2SHELL.DLL rather than stored in MW2.PRJ,
    /// so they are read out of the user's own copies at fixed addresses. That ties those reads to one build of the
    /// game; TableStore exists so the result is extracted once and the libraries are not needed afterwards.
    /// </summary>
    public static class PeImage
    {
        public static int FileOffset(byte[] d, uint va)
        {
            int pe = BitConverter.ToInt32(d, 0x3C);
            int sections = BitConverter.ToUInt16(d, pe + 6);
            int optional = BitConverter.ToUInt16(d, pe + 20);
            uint imageBase = BitConverter.ToUInt32(d, pe + 24 + 28);
            for (int s = 0; s < sections; s++)
            {
                int o = pe + 24 + optional + s * 40;
                uint vsize = BitConverter.ToUInt32(d, o + 8), rva = BitConverter.ToUInt32(d, o + 12);
                uint rawSize = BitConverter.ToUInt32(d, o + 16), raw = BitConverter.ToUInt32(d, o + 20);
                uint start = imageBase + rva;
                if (va >= start && va < start + Math.Max(vsize, rawSize) && va - start < rawSize)
                    return (int)(raw + (va - start));
            }
            throw new InvalidDataException($"VA {va:x} not in file");
        }

        /// <summary>A resource of the image (type and numeric id, first language), null if there is none.</summary>
        public static byte[] Resource(byte[] d, int type, int id)
        {
            int pe = BitConverter.ToInt32(d, 0x3C);
            uint imageBase = BitConverter.ToUInt32(d, pe + 24 + 28);
            uint rva = BitConverter.ToUInt32(d, pe + 24 + 96 + 2 * 8);
            if (rva == 0) return null;
            int root = FileOffset(d, imageBase + rva);
            // The entry's OffsetToData (top bit: a subdirectory), or -1 when the key is not there; keyless takes the first.
            long Find(int dir, int key)
            {
                int named = BitConverter.ToUInt16(d, dir + 12), ids = BitConverter.ToUInt16(d, dir + 14);
                for (int k = 0; k < named + ids; k++)
                {
                    int e = dir + 16 + k * 8;
                    uint name = BitConverter.ToUInt32(d, e);
                    if (key < 0 || (name & 0x80000000) == 0 && name == key) return BitConverter.ToUInt32(d, e + 4);
                }
                return -1;
            }
            long t = Find(root, type);
            if (t < 0 || (t & 0x80000000) == 0) return null;
            long n = Find(root + (int)(t & 0x7fffffff), id);
            if (n < 0 || (n & 0x80000000) == 0) return null;
            long l = Find(root + (int)(n & 0x7fffffff), -1);
            if (l < 0 || (l & 0x80000000) != 0) return null;
            int leaf = root + (int)l;
            uint dataRva = BitConverter.ToUInt32(d, leaf), size = BitConverter.ToUInt32(d, leaf + 4);
            var result = new byte[size];
            Buffer.BlockCopy(d, FileOffset(d, imageBase + dataRva), result, 0, (int)size);
            return result;
        }

        /// <summary>
        /// Whether <paramref name="va"/> lies in a section's zero-filled tail: inside its virtual size but past the
        /// bytes stored in the file (uninitialised data, all zeros once loaded). MW2SHELL.DLL's mech table points
        /// some short codes there (0x100d3d60), which at run time are empty strings.
        /// </summary>
        public static bool IsZeroFill(byte[] d, uint va)
        {
            int pe = BitConverter.ToInt32(d, 0x3C);
            int sections = BitConverter.ToUInt16(d, pe + 6);
            int optional = BitConverter.ToUInt16(d, pe + 20);
            uint imageBase = BitConverter.ToUInt32(d, pe + 24 + 28);
            for (int s = 0; s < sections; s++)
            {
                int o = pe + 24 + optional + s * 40;
                uint vsize = BitConverter.ToUInt32(d, o + 8), rva = BitConverter.ToUInt32(d, o + 12);
                uint rawSize = BitConverter.ToUInt32(d, o + 16);
                uint start = imageBase + rva;
                if (va >= start && va - start < vsize && va - start >= rawSize) return true;
            }
            return false;
        }
    }
}
