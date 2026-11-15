using System;
using MW2.Formats;
using UnityEngine;

namespace MW2
{
    /// <summary>
    /// Installation adapter, not part of the original program: the player's own CD image (a CUE sheet with its BIN)
    /// that holds the mission music as Red Book audio tracks. The original plays these tracks from the drive over MCI
    /// (0x10052b83); a mounted image usually exposes only its data track, so the port reads the audio from the BIN.
    /// The image comes from --music-disc for one run, otherwise from the choice saved on the game folder screen.
    /// </summary>
    public static class Mw2MusicDisc
    {
        public const string PreferenceKey = "MW2.MusicDisc";
        public const string Argument = "--music-disc";

        static string _loadedPath;
        static CueSheet _sheet;

        /// <summary>The image chosen for this run, or an empty string.</summary>
        public static string ImagePath
        {
            get
            {
                var args = Environment.GetCommandLineArgs();
                int at = Array.IndexOf(args, Argument);
                if (at >= 0 && at + 1 < args.Length) return args[at + 1];
                return PlayerPrefs.GetString(PreferenceKey, "");
            }
        }

        public static void Save(string path)
        {
            PlayerPrefs.SetString(PreferenceKey, path ?? "");
            PlayerPrefs.Save();
            _loadedPath = null;
            _sheet = null;
        }

        /// <summary>Checks an image; returns null when it can be used, otherwise the reason.</summary>
        public static string Check(string path)
        {
            try
            {
                var sheet = CueSheet.Load(path);
                return sheet.AudioTracks == 0 ? "The CD image has no audio tracks: " + path : null;
            }
            catch (Exception e) { return e.Message; }
        }

        /// <summary>The audio track of the chosen image, or null when no image is chosen or it has no such track.</summary>
        public static CueSheet.Track Track(int number)
        {
            string path = ImagePath;
            if (string.IsNullOrEmpty(path)) return null;
            if (_loadedPath != path)
            {
                _loadedPath = path;
                try { _sheet = CueSheet.Load(path); }
                catch (Exception e)
                {
                    _sheet = null;
                    Debug.LogWarning("MW2: music disc image: " + e.Message);
                }
            }
            return _sheet?.Find(number);
        }

        /// <summary>A streamed clip that reads the track from the image as it plays.</summary>
        public static AudioClip Clip(CueSheet.Track track, out IDisposable owner)
        {
            var reader = new CueSheet.Reader(track);
            owner = reader;
            return AudioClip.Create($"CD track {track.Number}", Math.Max(1, track.Frames), 2, CueSheet.SampleRate, true,
                reader.Read, reader.Seek);
        }
    }
}
