using System;
using System.Collections.Generic;
using System.Text;

namespace MW2.Shell
{
    /// <summary>FUN_1002e9c2 0x1002e9c2 / FUN_1002ead2 0x1002ead2: mw2high.cfg has a
    /// four-byte count and ten 0x45-byte entries (pilot, unit, score, new-record flag).
    /// The defaults are embedded at MW2SHELL.DLL 0x100a51c8.</summary>
    public sealed class HighScores
    {
        public const int Size = 0x2b6, RecordSize = 0x45, Count = 10;
        readonly byte[] _data;

        HighScores(byte[] data) { _data = data; }

        public static HighScores Load(byte[] saved, ShellTables tables)
        {
            if (tables == null) throw new ArgumentNullException(nameof(tables));
            if (saved != null && saved.Length == Size && BitConverter.ToInt32(saved, 0) == Count)
                return new HighScores((byte[])saved.Clone());
            return new HighScores(tables.Defaults.HighScoreBlock()); // 0x100a51c8
        }

        public byte[] Save() => (byte[])_data.Clone();
        public int Score(int row) => BitConverter.ToInt32(_data, 4 + row * RecordSize + 0x40);
        public string Pilot(int row) => ReadName(4 + row * RecordSize);
        public string Unit(int row) => ReadName(4 + row * RecordSize + 0x20);
        public bool NewRecord(int row) => _data[4 + row * RecordSize + 0x44] != 0;

        string ReadName(int offset)
        {
            int end = offset;
            while (end < offset + 0x20 && _data[end] != 0) end++;
            return Encoding.ASCII.GetString(_data, offset, end - offset);
        }

        /// <summary>FUN_1002eba2 0x1002eba2: the player pilot, unit name and current net worth form
        /// a candidate. FUN_1002ead2 replaces the lowest score only when the new one is greater.</summary>
        public bool Offer(Career career, ShellTables tables)
        {
            if (career == null) throw new ArgumentNullException(nameof(career));
            if (tables == null) throw new ArgumentNullException(nameof(tables));
            PilotRecord player = career.UnitPilots.Find(p => p.Player);
            if (player == null) return false;
            int lowest = 500000000, slot = -1;
            for (int i = 0; i < Count; i++)
            {
                _data[4 + i * RecordSize + 0x44] = 0;
                if (Score(i) < lowest) { lowest = Score(i); slot = i; }
            }
            int score = career.Funds + ShellEconomy.UnitAssets(career, tables);
            if (slot < 0 || score <= lowest) return false;
            int start = 4 + slot * RecordSize;
            Array.Clear(_data, start, RecordSize);
            WriteName(start, player.Name);
            WriteName(start + 0x20, career.UnitName);
            Buffer.BlockCopy(BitConverter.GetBytes(score), 0, _data, start + 0x40, 4);
            _data[start + 0x44] = 1;
            var rows = new List<byte[]>(Count);
            for (int i = 0; i < Count; i++)
            {
                var row = new byte[RecordSize];
                Buffer.BlockCopy(_data, 4 + i * RecordSize, row, 0, RecordSize);
                rows.Add(row);
            }
            rows.Sort((a, b) => BitConverter.ToInt32(b, 0x40).CompareTo(BitConverter.ToInt32(a, 0x40)));
            for (int i = 0; i < Count; i++) Buffer.BlockCopy(rows[i], 0, _data, 4 + i * RecordSize, RecordSize);
            return true;
        }

        void WriteName(int offset, string name)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(name ?? "");
            Buffer.BlockCopy(bytes, 0, _data, offset, Math.Min(bytes.Length, 0x1f));
        }
    }
}
