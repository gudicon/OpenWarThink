using System;
using System.IO;
using System.Linq;
using MW2.Formats;
using UnityEngine;

namespace MW2
{
    /// <summary>
    /// The game's mods (ModCatalog): the Mods folder next to the built game (next to MW2Mercs.exe, or next to
    /// MW2Mercs.app on macOS; in the Unity Editor the project folder), the order and switches in mods.json in the
    /// user-data folder, and the file lookup the mod-aware parts of the game use. --mods-folder &lt;path&gt; points at
    /// another folder for one run. Port addition with no counterpart in the original program.
    /// </summary>
    public static class Mw2Mods
    {
        public const string FolderName = "Mods";
        public const string SettingsFile = "mods.json";

        static ModCatalog _catalog;

        /// <summary>Raised after the mods screen saved a new choice.</summary>
        public static event Action Changed;

        public static ModCatalog Catalog
        {
            get
            {
                if (_catalog == null) Reload();
                return _catalog;
            }
        }

        public static string SettingsPath => Path.Combine(Application.persistentDataPath, SettingsFile);

        /// <summary>The Mods folder for this run.</summary>
        public static string Folder
        {
            get
            {
                var args = Environment.GetCommandLineArgs();
                int at = Array.IndexOf(args, "--mods-folder");
                if (at >= 0 && at + 1 < args.Length) return Path.GetFullPath(args[at + 1]);
                // dataPath: <game>/MW2Mercs_Data, <game>/MW2Mercs.app/Contents, or <project>/Assets in the Editor.
                var data = new DirectoryInfo(Application.dataPath);
                var game = Application.platform == RuntimePlatform.OSXPlayer ? data.Parent?.Parent : data.Parent;
                return Path.Combine((game ?? data).FullName, FolderName);
            }
        }

        /// <summary>macOS runs a downloaded app that still has the quarantine flag from a hidden read-only copy
        /// (App Translocation), so the Mods folder next to it is not the one the player sees.</summary>
        public static bool Translocated => Application.dataPath.Contains("/AppTranslocation/");

        /// <summary>Reads the Mods folder and mods.json again.</summary>
        public static void Reload()
        {
            string settings = null;
            try { if (File.Exists(SettingsPath)) settings = File.ReadAllText(SettingsPath); }
            catch (Exception e) { Debug.LogWarning("MW2 mods: cannot read " + SettingsPath + ": " + e.Message); }
            try { _catalog = ModCatalog.Load(Folder, settings); }
            catch (Exception e)
            {
                Debug.LogWarning("MW2 mods: cannot read " + Folder + ": " + e.Message);
                _catalog = ModCatalog.Load(null, null);
            }
            var active = _catalog.Active.ToList();
            Debug.Log(active.Count == 0
                ? "MW2 mods: none active (" + Folder + ")"
                : "MW2 mods: " + string.Join(", ", active.Select(m => m.Title)) + " (" + Folder + ")");
            foreach (var m in _catalog.Mods)
                if (m.Problem != null) Debug.LogWarning($"MW2 mods: {Path.GetFileName(m.Folder)}: {m.Problem}");
                else if (m.Incompatible != null) Debug.LogWarning($"MW2 mods: {m.Name}: {m.Incompatible}" + (m.Forced ? " Loaded anyway." : " Turned off."));
        }

        /// <summary>A fresh copy of the catalog for the mods screen to edit (Apply keeps it, dropping it discards).</summary>
        public static ModCatalog Edit()
        {
            Reload();
            string settings = Catalog.SaveSettings();
            return ModCatalog.Load(Catalog.Folder, settings);
        }

        /// <summary>Makes an edited catalog the game's choice and saves it.</summary>
        public static void Apply(ModCatalog edited)
        {
            _catalog = edited;
            Save();
        }

        /// <summary>Saves the order and switches and tells the game the choice changed.</summary>
        public static void Save()
        {
            try { File.WriteAllText(SettingsPath, Catalog.SaveSettings()); }
            catch (Exception e) { Debug.LogWarning("MW2 mods: cannot write " + SettingsPath + ": " + e.Message); }
            Changed?.Invoke();
        }

        /// <summary>The full path of the file an active mod puts at <paramref name="relative"/> ('/' separated), or null.</summary>
        public static string Resolve(string relative) => Catalog.Resolve(relative);

        /// <summary>Opens the Mods folder in the file manager, creating it when it is missing.</summary>
        public static string OpenFolder()
        {
            string folder = Folder;
            try { Directory.CreateDirectory(folder); }
            catch (Exception e) { return "Cannot create " + folder + ": " + e.Message; }
            Application.OpenURL(new Uri(folder.EndsWith(Path.DirectorySeparatorChar.ToString()) ? folder : folder + Path.DirectorySeparatorChar).AbsoluteUri);
            return null;
        }
    }
}
