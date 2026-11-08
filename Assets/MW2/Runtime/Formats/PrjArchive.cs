using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace MW2.Formats
{
    /// <summary>
    /// Reader for the Titanium MW2 ".PRJ" resource archive.
    /// Layout recovered from mercsw.dll: Prj_Open 0x10057b1d, Prj_LoadIndices 0x10057fa9, Prj_ReadEntry 0x1005838f.
    /// </summary>
    public sealed class PrjArchive : IDisposable
    {
        public readonly struct Entry
        {
            public readonly string Tag;
            public readonly int Id;
            public readonly string Name;
            public readonly long Offset;
            public readonly int Size;

            public Entry(string tag, int id, string name, long offset, int size)
            {
                Tag = tag; Id = id; Name = name; Offset = offset; Size = size;
            }

            public override string ToString() => $"{Tag}#{Id} {Name} ({Size} bytes)";
        }

        const int EntryHeaderSize = 0x3E;   // 'DATA' header in front of every resource, holds the name at +0x1E

        readonly FileStream _stream;
        readonly BinaryReader _reader;
        readonly Dictionary<string, List<Entry>> _byTag = new Dictionary<string, List<Entry>>();
        readonly Dictionary<string, Entry> _byName = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyCollection<string> Tags => _byTag.Keys;

        /// <summary>Full path of the archive file; the game's loose files (SKYGND.PAR) lie next to it.</summary>
        public string FilePath => _stream.Name;

        public PrjArchive(string path)
        {
            _stream = File.OpenRead(path);
            _reader = new BinaryReader(_stream);
            if (Encoding.ASCII.GetString(_reader.ReadBytes(4)) != "PROJ")
                throw new InvalidDataException($"{path} is not a PROJ archive");

            _stream.Position = 0x10;
            int dirSize = _reader.ReadInt32() + 8;
            _stream.Position = 0xC;
            byte[] dir = _reader.ReadBytes(dirSize);

            int typeCount = BitConverter.ToUInt16(dir, 0xC);
            for (int t = 0; t < typeCount; t++)
            {
                int e = 0xE + t * 0x18;
                string tag = Encoding.ASCII.GetString(dir, e, 4).TrimEnd('\0');
                uint indexOffset = BitConverter.ToUInt32(dir, e + 4);
                int indexSize = (int)BitConverter.ToUInt32(dir, e + 8);
                int skip = BitConverter.ToUInt16(dir, e + 0x14);
                var list = new List<Entry>();
                _byTag[tag] = list;
                if (indexOffset == 0) continue;

                _stream.Position = indexOffset;
                byte[] index = _reader.ReadBytes(indexSize);
                int count = (index.Length - 0x16) / 8;
                for (int i = 1; i < count; i++)
                {
                    uint off = BitConverter.ToUInt32(index, 0x16 + i * 8);
                    int size = (int)BitConverter.ToUInt32(index, 0x16 + i * 8 + 4);
                    if (off == 0 || size <= skip) continue;
                    var entry = new Entry(tag, i, ReadName(off), off + skip, size - skip);
                    list.Add(entry);
                    _byName[tag + ":" + entry.Name] = entry;
                }
            }
        }

        string ReadName(long headerOffset)
        {
            _stream.Position = headerOffset + 0x1E;
            byte[] raw = _reader.ReadBytes(EntryHeaderSize - 0x1E);
            int len = Array.IndexOf(raw, (byte)0);
            return Encoding.ASCII.GetString(raw, 0, len < 0 ? raw.Length : len);
        }

        public IReadOnlyList<Entry> Entries(string tag) =>
            _byTag.TryGetValue(tag, out var list) ? list : (IReadOnlyList<Entry>)Array.Empty<Entry>();

        public bool TryFind(string tag, string name, out Entry entry) => _byName.TryGetValue(tag + ":" + name, out entry);

        public byte[] Read(in Entry entry)
        {
            _stream.Position = entry.Offset;
            return _reader.ReadBytes(entry.Size);
        }

        public byte[] Read(string tag, string name) =>
            TryFind(tag, name, out var e) ? Read(e) : throw new KeyNotFoundException($"{tag}:{name}");

        public void Dispose() => _reader.Dispose();
    }
}
