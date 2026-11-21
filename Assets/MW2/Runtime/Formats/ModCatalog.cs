using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MW2.Formats
{
    /// <summary>
    /// The mods in the Mods folder next to the game and the player's choice of them. Port addition with no counterpart in
    /// the original program; without mods (or with all of them off) the game reads only the original data.
    ///
    /// A mod is a folder in Mods with a mod.json manifest: id (defaults to the folder name), name, version, author,
    /// description and api, the mod format it was made for. Its files in subfolders (any depth) replace files of the
    /// same relative path; files at the top of the mod folder (mod.json, a readme, a picture) belong to the mod itself.
    /// Order: a mod lower in the list wins when two mods have the same file, and the list shows such conflicts. A mod
    /// made for another mod format is turned off with a message; the player may turn it on again, and it is then loaded
    /// as it is. The order and on/off state are kept in a settings file (mods.json in the user-data folder); new mods
    /// are added at the end, switched on.
    /// </summary>
    public sealed class ModCatalog
    {
        /// <summary>The mod format this version of OpenWarThink reads (the "api" of mod.json).</summary>
        public const int Api = 1;
        public const string Manifest = "mod.json";
        public const int SettingsFormat = 1;

        public sealed class Mod
        {
            public string Id, Name, Version = "", Author = "", Description = "";
            public string Folder;          // full path; for a problem entry the file or folder that caused it
            public int ModApi;
            /// <summary>Why the mod cannot be used at all (no or broken mod.json, a packed zip...). Such a mod stays off.</summary>
            public string Problem;
            /// <summary>Why the mod may not work with this version (another mod format). It can still be turned on.</summary>
            public string Incompatible;
            public bool Enabled;
            /// <summary>The player turned an incompatible mod on anyway.</summary>
            public bool Forced;
            /// <summary>Relative paths ('/' separated) of the files the mod replaces.</summary>
            public readonly List<string> Files = new List<string>();

            public bool Active => Problem == null && Enabled && (Incompatible == null || Forced);

            /// <summary>
            /// A language mod: it has lang/ files, and all its files are text and letters (lang/), pictures of the shell
            /// (shell/) or font samples the game never reads (font-samples/). It changes what the player sees, not the
            /// game, so MercNet does not compare it (<see cref="Fingerprint"/>).
            /// </summary>
            public bool LanguageOnly =>
                Files.Any(f => f.StartsWith(LangFolder, StringComparison.OrdinalIgnoreCase)) &&
                Files.All(f => f.StartsWith(LangFolder, StringComparison.OrdinalIgnoreCase) ||
                               f.StartsWith("shell/", StringComparison.OrdinalIgnoreCase) ||
                               f.StartsWith("font-samples/", StringComparison.OrdinalIgnoreCase));

            const string LangFolder = "lang/";
            public string Title => string.IsNullOrEmpty(Version) ? Name : $"{Name} {Version}";
        }

        public sealed class Conflict
        {
            public string Path;
            public Mod Winner;
            public List<Mod> Losers = new List<Mod>();
        }

        public string Folder { get; private set; }
        public bool FolderExists { get; private set; }
        public readonly List<Mod> Mods = new List<Mod>();

        public IEnumerable<Mod> Active => Mods.Where(m => m.Active);

        // ---- loading ----

        /// <summary>Reads the Mods folder and applies the saved settings (the text of mods.json, or null).</summary>
        public static ModCatalog Load(string modsFolder, string settingsText)
        {
            var c = new ModCatalog { Folder = modsFolder };
            var found = new List<Mod>();
            if (!string.IsNullOrEmpty(modsFolder) && Directory.Exists(modsFolder))
            {
                c.FolderExists = true;
                foreach (string dir in Directory.GetDirectories(modsFolder).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
                    if (!Ignored(Path.GetFileName(dir))) found.Add(ReadMod(dir));
                foreach (string file in Directory.GetFiles(modsFolder).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    string name = Path.GetFileName(file);
                    if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && !name.EndsWith(".7z", StringComparison.OrdinalIgnoreCase)
                        && !name.EndsWith(".rar", StringComparison.OrdinalIgnoreCase)) continue;
                    found.Add(new Mod
                    {
                        Id = name, Name = name, Folder = file,
                        Problem = "A packed mod: unpack it into its own folder in Mods.",
                    });
                }
            }

            // The same id twice: the second folder is not used.
            var ids = new Dictionary<string, Mod>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in found)
            {
                if (m.Problem != null) continue;
                if (ids.TryGetValue(m.Id, out var first))
                    m.Problem = $"Same id \"{m.Id}\" as the mod in folder {Path.GetFileName(first.Folder)}.";
                else ids[m.Id] = m;
            }

            // Saved order first, then the new mods in folder order, switched on.
            var saved = ReadSettings(settingsText);
            foreach (var entry in saved)
            {
                var m = found.FirstOrDefault(x => x.Problem == null && string.Equals(x.Id, entry.Id, StringComparison.OrdinalIgnoreCase));
                if (m == null || c.Mods.Contains(m)) continue;
                m.Enabled = entry.Enabled;
                m.Forced = entry.Forced && m.Incompatible != null;
                c.Mods.Add(m);
            }
            foreach (var m in found)
            {
                if (c.Mods.Contains(m)) continue;
                m.Enabled = m.Problem == null && m.Incompatible == null;
                c.Mods.Add(m);
            }
            return c;
        }

        static bool Ignored(string name) => name.StartsWith(".", StringComparison.Ordinal) || name.StartsWith("__MACOSX", StringComparison.OrdinalIgnoreCase);

        static Mod ReadMod(string dir)
        {
            string folderName = Path.GetFileName(dir);
            var m = new Mod { Id = folderName, Name = folderName, Folder = dir };
            string manifest = Path.Combine(dir, Manifest);
            if (!File.Exists(manifest))
            {
                // A zip unpacked with its own top folder: Mods/X/X/mod.json.
                var inner = Directory.GetDirectories(dir).Where(d => File.Exists(Path.Combine(d, Manifest))).ToList();
                m.Problem = inner.Count == 1
                    ? $"mod.json is one folder deeper: move the folder {Path.GetFileName(inner[0])} straight into Mods."
                    : "No mod.json in this folder.";
                return m;
            }
            Dictionary<string, object> o;
            try
            {
                o = MiniJson.Parse(File.ReadAllText(manifest)) as Dictionary<string, object>;
                if (o == null) { m.Problem = "mod.json must hold an object { ... }."; return m; }
            }
            catch (Exception e) { m.Problem = "mod.json: " + e.Message; return m; }

            string id = MiniJson.Text(o, "id");
            if (!string.IsNullOrWhiteSpace(id))
            {
                if (id.IndexOfAny(new[] { ';', '@', '|', '"', '\n', '\r' }) >= 0) { m.Problem = "mod.json: the id may not contain ; @ | or quotes."; return m; }
                m.Id = id.Trim();
            }
            m.Name = MiniJson.Text(o, "name")?.Trim() is string n && n.Length > 0 ? n : m.Id;
            m.Version = MiniJson.Text(o, "version")?.Trim() ?? (MiniJson.Number(o, "version") is double v ? v.ToString(System.Globalization.CultureInfo.InvariantCulture) : "");
            m.Author = MiniJson.Text(o, "author")?.Trim() ?? "";
            m.Description = MiniJson.Text(o, "description")?.Trim() ?? "";
            double? api = MiniJson.Number(o, "api");
            m.ModApi = api.HasValue ? (int)api.Value : 0;
            if (!api.HasValue) m.Incompatible = $"mod.json does not say which mod format it was made for (\"api\": {Api}).";
            else if (m.ModApi != Api) m.Incompatible = $"Made for mod format {m.ModApi}; this OpenWarThink reads format {Api}.";

            foreach (string sub in Directory.GetDirectories(dir))
            {
                if (Ignored(Path.GetFileName(sub))) continue;
                foreach (string file in Directory.GetFiles(sub, "*", SearchOption.AllDirectories))
                {
                    string rel = file.Substring(dir.Length).TrimStart('\\', '/').Replace('\\', '/');
                    if (rel.Split('/').Any(Ignored) || IgnoredFile(Path.GetFileName(file))) continue;
                    m.Files.Add(rel);
                }
            }
            m.Files.Sort(StringComparer.OrdinalIgnoreCase);
            return m;
        }

        static bool IgnoredFile(string name) =>
            string.Equals(name, "Thumbs.db", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "desktop.ini", StringComparison.OrdinalIgnoreCase);

        // ---- settings ----

        struct Entry { public string Id; public bool Enabled, Forced; }

        static List<Entry> ReadSettings(string text)
        {
            var list = new List<Entry>();
            if (string.IsNullOrWhiteSpace(text)) return list;
            try
            {
                var o = MiniJson.Parse(text) as Dictionary<string, object>;
                foreach (var item in MiniJson.List(o, "mods") ?? new List<object>())
                    if (item is Dictionary<string, object> e && MiniJson.Text(e, "id") is string id)
                        list.Add(new Entry { Id = id, Enabled = MiniJson.Flag(e, "enabled") ?? true, Forced = MiniJson.Flag(e, "force") ?? false });
            }
            catch (MiniJson.Error) { list.Clear(); }
            return list;
        }

        /// <summary>The text of mods.json for the current order and switches.</summary>
        public string SaveSettings()
        {
            var mods = new List<object>();
            foreach (var m in Mods)
            {
                if (m.Problem != null) continue;
                var e = new Dictionary<string, object> { ["id"] = m.Id, ["enabled"] = m.Enabled };
                if (m.Forced) e["force"] = true;
                mods.Add(e);
            }
            return MiniJson.Write(new Dictionary<string, object> { ["format"] = SettingsFormat, ["mods"] = mods }) + "\n";
        }

        // ---- changes from the mods screen ----

        /// <summary>Turns a mod on or off; turning on an incompatible mod marks it as forced.</summary>
        public void SetEnabled(Mod m, bool on)
        {
            if (m.Problem != null) return;
            m.Enabled = on;
            m.Forced = on && m.Incompatible != null;
        }

        /// <summary>Moves a mod up (-1) or down (+1) in the load order.</summary>
        public void Move(Mod m, int delta)
        {
            int i = Mods.IndexOf(m), j = i + delta;
            if (i < 0 || j < 0 || j >= Mods.Count) return;
            Mods.RemoveAt(i);
            Mods.Insert(j, m);
        }

        // ---- files ----

        /// <summary>The full path of the file a later (winning) active mod has at <paramref name="relative"/>, or null.</summary>
        public string Resolve(string relative)
        {
            string key = Normalize(relative);
            for (int i = Mods.Count - 1; i >= 0; i--)
            {
                var m = Mods[i];
                if (!m.Active) continue;
                int k = m.Files.FindIndex(f => string.Equals(f, key, StringComparison.OrdinalIgnoreCase));
                if (k >= 0) return Path.Combine(m.Folder, m.Files[k].Replace('/', Path.DirectorySeparatorChar));
            }
            return null;
        }

        /// <summary>
        /// The first of <paramref name="relatives"/> (most specific first) that the winning mod has: mods are tried from the
        /// last to the first, and inside one mod in the given order, so a later mod's general file beats an earlier mod's
        /// specific one. Null when no active mod has any of them.
        /// </summary>
        public string ResolveFirst(IList<string> relatives)
        {
            for (int i = Mods.Count - 1; i >= 0; i--)
            {
                var m = Mods[i];
                if (!m.Active) continue;
                foreach (string relative in relatives)
                {
                    string key = Normalize(relative);
                    int k = m.Files.FindIndex(f => string.Equals(f, key, StringComparison.OrdinalIgnoreCase));
                    if (k >= 0) return Path.Combine(m.Folder, m.Files[k].Replace('/', Path.DirectorySeparatorChar));
                }
            }
            return null;
        }

        /// <summary>The active mod whose folder holds <paramref name="path"/>, or null.</summary>
        public Mod OwnerOf(string path)
        {
            if (path == null) return null;
            foreach (var m in Mods)
                if (m.Active && path.StartsWith(m.Folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return m;
            return null;
        }

        public static string Normalize(string relative) => (relative ?? "").Replace('\\', '/').TrimStart('/');

        /// <summary>Files that more than one active mod has; the last mod in the order wins.</summary>
        public List<Conflict> Conflicts()
        {
            var byPath = new Dictionary<string, Conflict>(StringComparer.OrdinalIgnoreCase);
            var result = new List<Conflict>();
            foreach (var m in Mods)
            {
                if (!m.Active) continue;
                foreach (string f in m.Files)
                {
                    if (!byPath.TryGetValue(f, out var c)) { byPath[f] = new Conflict { Path = f, Winner = m }; continue; }
                    if (c.Losers.Count == 0) result.Add(c);
                    c.Losers.Add(c.Winner);
                    c.Winner = m;
                }
            }
            return result;
        }

        /// <summary>One line per active mod that overrides another one's files, for the warning on the mods screen.</summary>
        public List<string> ConflictSummary()
        {
            var lines = new List<string>();
            foreach (var group in Conflicts().GroupBy(c => (c.Winner, c.Losers[c.Losers.Count - 1])))
            {
                int n = group.Count();
                lines.Add($"{group.Key.Item1.Name} replaces {n} file{(n == 1 ? "" : "s")} of {group.Key.Item2.Name}" +
                          (n == 1 ? $" ({group.First().Path})" : $" ({group.First().Path}, ...)"));
            }
            return lines;
        }

        // ---- multiplayer ----

        /// <summary>The active mods in order, "id@version;id@version": all players of a game must have the same. Language
        /// mods (<see cref="Mod.LanguageOnly"/>) are left out: players with different languages can play together.</summary>
        public string Fingerprint() => string.Join(";", Active.Where(m => !m.LanguageOnly).Select(m => m.Id + "@" + m.Version));

        /// <summary>What differs between two fingerprints, in words, or null when they are the same.</summary>
        public static string Difference(string host, string joining)
        {
            host ??= "";
            joining ??= "";
            if (host == joining) return null;
            List<string> Split(string s) => s.Length == 0 ? new List<string>() : s.Split(';').ToList();
            string Show(IEnumerable<string> items) => string.Join(", ", items.Select(x => x.Replace("@", " ").TrimEnd()));
            var h = Split(host);
            var j = Split(joining);
            var parts = new List<string>();
            var onlyHost = h.Except(j).ToList();
            var onlyJoining = j.Except(h).ToList();
            if (onlyHost.Count > 0) parts.Add("the host has " + Show(onlyHost));
            if (onlyJoining.Count > 0) parts.Add("you have " + Show(onlyJoining));
            if (parts.Count == 0) parts.Add("the same mods in another order");
            return "Different mods: " + string.Join("; ", parts) + ".";
        }
    }
}
