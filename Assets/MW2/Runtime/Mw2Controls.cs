using System;
using System.IO;
using MW2.Formats;
using UnityEngine;

namespace MW2
{
    /// <summary>
    /// Where the player's bindings live: controls.json in the user-data folder (Application.persistentDataPath), next
    /// to the shell's MW2SND.CFG / MW2DIF.CFG copies. The game folder's inputw.map and gamekey.map are never changed;
    /// without the file the shipped maps apply unchanged. Port extension (see ControlBindings).
    /// </summary>
    public static class Mw2Controls
    {
        public const string FileName = "controls.json";
        static ControlProfile _profile;
        static bool _loaded;

        public static string FilePath => Path.Combine(Application.persistentDataPath, FileName);

        /// <summary>The saved profile, or null when the player has changed nothing.</summary>
        public static ControlProfile Profile
        {
            get
            {
                if (_loaded) return _profile;
                _loaded = true;
                _profile = null;
                try
                {
                    if (File.Exists(FilePath))
                    {
                        var p = JsonUtility.FromJson<ControlProfile>(File.ReadAllText(FilePath));
                        if (p != null && p.Format == ControlProfile.CurrentFormat) _profile = p;
                        else Debug.LogWarning($"MW2 controls: {FilePath} has an unknown format and is ignored");
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"MW2 controls: {FilePath} could not be read ({e.Message}); using the game's maps");
                }
                return _profile;
            }
        }

        /// <summary>Saves the profile (deletes the file when it changes nothing) and reloads the input layer.</summary>
        public static void Save(ControlProfile profile)
        {
            bool empty = profile == null || profile.Actions.Count == 0 && profile.Commands.Count == 0;
            if (empty)
            {
                if (File.Exists(FilePath)) File.Delete(FilePath);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllText(FilePath, JsonUtility.ToJson(profile, true));
            }
            _profile = empty ? null : profile.Clone();
            _loaded = true;
            Mw2Input.Reload();
        }
    }
}
