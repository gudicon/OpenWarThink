using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace MW2.Formats
{
    /// <summary>
    /// Red Book audio tracks of a CD image described by a CUE sheet (FILE "x.bin" BINARY / TRACK nn AUDIO /
    /// INDEX 01 mm:ss:ff). Not part of the original program, which plays the tracks from the drive over MCI
    /// (0x10052b83); this lets the port play them from the player's own disc image. Raw images keep 2352-byte sectors;
    /// an audio sector is 588 frames of 16-bit little-endian stereo PCM at 44100 Hz. A track runs from its INDEX 01 to
    /// the next track's INDEX 00 (or INDEX 01 when there is none) in the same file, or to the end of its file.
    /// PREGAP is silence that the image does not store.
    /// </summary>
    public sealed class CueSheet
    {
        public const int SectorBytes = 2352;
        public const int SampleRate = 44100;

        public sealed class Track
        {
            public int Number;
            public bool Audio;
            public string File;
            /// <summary>Byte offset of INDEX 01 in File.</summary>
            public long Offset;
            /// <summary>Length of the track in bytes.</summary>
            public long Length;
            /// <summary>Stereo frames (one left and one right sample each).</summary>
            public int Frames => (int)(Length / 4);
        }

        public readonly string Path;
        public readonly List<Track> Tracks = new List<Track>();

        static readonly Regex FileLine = new Regex("^\\s*FILE\\s+\"?(.+?)\"?\\s+(BINARY|MOTOROLA|WAVE|MP3|AIFF)\\s*$", RegexOptions.IgnoreCase);
        static readonly Regex TrackLine = new Regex("^\\s*TRACK\\s+(\\d+)\\s+(\\S+)", RegexOptions.IgnoreCase);
        static readonly Regex IndexLine = new Regex("^\\s*INDEX\\s+(\\d+)\\s+(\\d+):(\\d+):(\\d+)", RegexOptions.IgnoreCase);

        CueSheet(string path) { Path = path; }

        public Track Find(int number) => Tracks.Find(t => t.Number == number && t.Audio);

        public int AudioTracks => Tracks.FindAll(t => t.Audio).Count;

        /// <summary>Reads a CUE sheet. Throws when it is not one or when its BIN files are missing.</summary>
        public static CueSheet Load(string path)
        {
            path = System.IO.Path.GetFullPath(path);
            var sheet = new CueSheet(path);
            string dir = System.IO.Path.GetDirectoryName(path) ?? "";
            string file = null;
            bool binary = false;
            Track current = null;
            var index0 = new Dictionary<Track, long>();
            var index1 = new Dictionary<Track, long>();
            // Latin-1 by hand: code page encodings are not always present in a Unity Player.
            byte[] raw = File.ReadAllBytes(path);
            var chars = new char[raw.Length];
            for (int i = 0; i < raw.Length; i++) chars[i] = (char)raw[i];
            foreach (string line in new string(chars).Split('\n'))
            {
                var m = FileLine.Match(line);
                if (m.Success)
                {
                    file = Resolve(dir, m.Groups[1].Value);
                    binary = m.Groups[2].Value.Equals("BINARY", StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                m = TrackLine.Match(line);
                if (m.Success)
                {
                    if (file == null) throw new InvalidDataException("TRACK before FILE in " + path);
                    current = new Track
                    {
                        Number = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
                        // Only raw little-endian images can be read directly.
                        Audio = binary && m.Groups[2].Value.Equals("AUDIO", StringComparison.OrdinalIgnoreCase),
                        File = file,
                    };
                    sheet.Tracks.Add(current);
                    continue;
                }
                m = IndexLine.Match(line);
                if (m.Success && current != null)
                {
                    int n = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                    long sector = (long.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) * 60
                        + long.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture)) * 75
                        + long.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture);
                    if (n == 0) index0[current] = sector * SectorBytes;
                    else if (n == 1) index1[current] = sector * SectorBytes;
                }
            }
            if (sheet.Tracks.Count == 0) throw new InvalidDataException("No tracks in " + path);
            for (int i = 0; i < sheet.Tracks.Count; i++)
            {
                var t = sheet.Tracks[i];
                if (!index1.TryGetValue(t, out t.Offset)) throw new InvalidDataException($"Track {t.Number} has no INDEX 01");
                if (!File.Exists(t.File)) throw new FileNotFoundException("CD image file is missing: " + t.File);
                long end = new FileInfo(t.File).Length;
                if (i + 1 < sheet.Tracks.Count && sheet.Tracks[i + 1].File == t.File)
                {
                    var next = sheet.Tracks[i + 1];
                    end = index0.TryGetValue(next, out long gap) ? gap : index1.TryGetValue(next, out long start) ? start : end;
                }
                t.Length = Math.Max(0, end - t.Offset) / 4 * 4;
            }
            return sheet;
        }

        /// <summary>The image names its BIN as it was on the author's disk; match the name without regard to case.</summary>
        static string Resolve(string dir, string name)
        {
            string direct = System.IO.Path.Combine(dir, name);
            if (File.Exists(direct)) return direct;
            string bare = System.IO.Path.GetFileName(name);
            if (Directory.Exists(dir))
                foreach (string f in Directory.GetFiles(dir))
                    if (string.Equals(System.IO.Path.GetFileName(f), bare, StringComparison.OrdinalIgnoreCase)) return f;
            return direct;
        }

        /// <summary>Reads PCM of a track as floats, interleaved left/right, from a stereo frame position.</summary>
        public sealed class Reader : IDisposable
        {
            readonly FileStream _file;
            readonly Track _track;
            readonly byte[] _bytes = new byte[1 << 16];
            readonly object _lock = new object();
            long _frame;

            public Reader(Track track)
            {
                _track = track;
                _file = new FileStream(track.File, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            }

            public void Seek(int frame)
            {
                lock (_lock) _frame = Math.Max(0, Math.Min(frame, _track.Frames));
            }

            /// <summary>Fills data with interleaved samples; past the end of the track it writes silence.</summary>
            public void Read(float[] data)
            {
                lock (_lock)
                {
                    int filled = 0;
                    while (filled < data.Length)
                    {
                        long left = (_track.Frames - _frame) * 2;
                        if (left <= 0) break;
                        int samples = (int)Math.Min(Math.Min(data.Length - filled, _bytes.Length / 2), left) & ~1;
                        if (samples == 0) break;
                        _file.Seek(_track.Offset + _frame * 4, SeekOrigin.Begin);
                        int got = 0, want = samples * 2;
                        while (got < want)
                        {
                            int n = _file.Read(_bytes, got, want - got);
                            if (n <= 0) break;
                            got += n;
                        }
                        samples = got / 4 * 2;
                        if (samples == 0) break;
                        for (int i = 0; i < samples; i++)
                            data[filled + i] = (short)(_bytes[2 * i] | (_bytes[2 * i + 1] << 8)) / 32768f;
                        filled += samples;
                        _frame += samples / 2;
                    }
                    Array.Clear(data, filled, data.Length - filled);
                }
            }

            public void Dispose()
            {
                lock (_lock) _file.Dispose();
            }
        }
    }
}
