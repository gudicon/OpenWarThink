using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using MW2.Formats;
using MW2.Shell;
using UnityEngine;

namespace MW2
{
    /// <summary>
    /// The language of the active mods (LangPack, their "lang" folders): read at start, after the mods screen saved a
    /// choice and when a file of a lang folder changes while the game runs (Mw2WeaponLooks polls). It hands the shell
    /// fonts their translation and extra letters (ShellFont.Translate / ExtraGlyphs), gives the cockpit its extra letters
    /// as a second atlas, and writes every shown text it has no translation for to lang-missing.txt in the user-data
    /// folder, for the translator. Without a language mod nothing is set and the game shows its own text.
    /// Port addition with no counterpart in the original program.
    /// </summary>
    public static class Mw2Lang
    {
        public const string MissingFile = "lang-missing.txt";
        const int CacheLimit = 20000;

        public static LangPack Current { get; private set; } = new LangPack();
        public static bool Active => !Current.Empty;

        static readonly Dictionary<string, string> s_cache = new Dictionary<string, string>(StringComparer.Ordinal);
        static readonly HashSet<string> s_missing = new HashSet<string>(StringComparer.Ordinal);
        static readonly Dictionary<string, HudLetters> s_hud = new Dictionary<string, HudLetters>(StringComparer.OrdinalIgnoreCase);

        public static string MissingPath => Path.Combine(Application.persistentDataPath, MissingFile);

        /// <summary>Reads the lang folders of the active mods again and sets the hooks.</summary>
        public static void Apply()
        {
            var pack = new LangPack();
            var sources = new List<string>();
            try
            {
                foreach (var m in Mw2Mods.Catalog.Active)
                    foreach (string rel in m.Files)
                    {
                        if (!rel.StartsWith(LangPack.Folder + "/", StringComparison.OrdinalIgnoreCase)) continue;
                        string path = Path.Combine(m.Folder, rel.Replace('/', Path.DirectorySeparatorChar));
                        string shown = Mw2WeaponLooks.Shown(path);
                        string inner = rel.Substring(LangPack.Folder.Length + 1);
                        try
                        {
                            if (inner.StartsWith("briefings/", StringComparison.OrdinalIgnoreCase) && inner.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                                // The game's ORDR texts end their lines with LF alone (beside the \n escapes): keep
                                // that, whatever the translator's editor saved.
                                pack.Briefings[Path.GetFileNameWithoutExtension(inner)] =
                                    File.ReadAllText(path).Replace("\r\n", "\n");
                            else if (inner.StartsWith("news/", StringComparison.OrdinalIgnoreCase) && inner.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                                pack.News[Path.GetFileNameWithoutExtension(inner)] = File.ReadAllText(path).Replace("\r\n", "\n");
                            else if (inner.StartsWith("fonts/", StringComparison.OrdinalIgnoreCase) && inner.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                                pack.AddFont(Path.GetFileNameWithoutExtension(inner), File.ReadAllText(path), shown);
                            else if (inner.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                                pack.AddStrings(File.ReadAllText(path), shown);
                            else continue;
                            if (!sources.Contains(m.Id)) sources.Add(m.Id);
                        }
                        catch (Exception e) when (e is MiniJson.Error || e is IOException || e is UnauthorizedAccessException)
                        {
                            Mw2WeaponLooks.Say(shown + ": " + e.Message, true);
                        }
                    }
            }
            catch (Exception e) { Debug.LogWarning("MW2 mods: " + e.Message); }
            foreach (var w in pack.Warnings) Mw2WeaponLooks.Say(w, true);

            bool was = Active;
            Current = pack;
            s_cache.Clear();
            s_missing.Clear();
            foreach (var h in s_hud.Values) if (h.Atlas != null) UnityEngine.Object.Destroy(h.Atlas);
            s_hud.Clear();
            ShellFont.ExtraVersion++;
            if (Active)
            {
                ShellFont.Translate = Translate;
                ShellBriefing.Translated = Briefing;
                ShellBriefing.TranslatedNews = pack.News.Count > 0 ? pack.News : null;
                ShellFont.ExtraGlyphs = name => Current.Fonts.TryGetValue(name, out var set) ? set
                    // Records 9 and 10 are record 6 in other inks.
                    : (name == "shell9" || name == "shell10") && Current.Fonts.TryGetValue("shell6", out set) ? set : null;
                try { File.WriteAllText(MissingPath, ""); } catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
                Debug.Log($"MW2 mods: language from {string.Join(", ", sources)}: {pack.Count} texts, {pack.Briefings.Count} briefings, {pack.News.Count} news, " +
                          $"{pack.Fonts.Count} fonts");
            }
            else
            {
                ShellFont.Translate = null;
                ShellBriefing.Translated = null;
                ShellBriefing.TranslatedNews = null;
                ShellFont.ExtraGlyphs = null;
            }
            if (was != Active || Active)
                Mw2WeaponLooks.Say(Active ? $"Language: {string.Join(", ", sources)} ({pack.Count} texts)" : "Language: the game's own", false);
        }

        /// <summary>The shown form of <paramref name="text"/>: its translation, or the text itself (then noted as missing).</summary>
        public static string Tr(string text) => Active ? Translate(text) ?? text : text;

        /// <summary>
        /// <paramref name="pattern"/> (with %s / %d markers) translated as a key of its own, then its markers replaced by
        /// <paramref name="args"/> in order: for the port's own screens, whose values (paths, names, counts) would make
        /// every shown text a new one. A translation keeps the markers in the same order (LangPack checks).
        /// </summary>
        public static string Format(string pattern, params object[] args)
        {
            string t = pattern;
            if (Active)
            {
                t = Current.Key(pattern);
                if (t == null) { Note(pattern); t = pattern; }
            }
            var sb = new StringBuilder(t.Length + 32);
            int next = 0;
            for (int i = 0; i < t.Length; i++)
            {
                if (t[i] == '%' && i + 1 < t.Length && (t[i + 1] == 's' || t[i + 1] == 'd') && next < args.Length)
                {
                    sb.Append(args[next++]);
                    i++;
                }
                else sb.Append(t[i]);
            }
            return sb.ToString();
        }

        static string Translate(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            if (s_cache.TryGetValue(text, out var t)) return t;
            t = Current.Translate(text);
            if (t == null)
            {
                Note(text);
                t = text;
            }
            if (s_cache.Count >= CacheLimit) s_cache.Clear();
            s_cache[text] = t;
            return t;
        }

        /// <summary>The whole briefing text of <paramref name="resource"/> from the language, or null.</summary>
        public static string Briefing(string resource) =>
            Active && resource != null && Current.Briefings.TryGetValue(resource, out var text) ? text : null;

        /// <summary>A text worth translating (two Latin letters in a row, no Cyrillic) goes to the missing list once, its
        /// numbers written as %d.</summary>
        static void Note(string text)
        {
            bool latin = false, cyrillic = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c >= 0x400 && c <= 0x4ff) cyrillic = true;
                if (i > 0 && IsLatin(c) && IsLatin(text[i - 1])) latin = true;
            }
            if (!latin || cyrillic) return;
            // Numbers change from frame to frame: the list gets the pattern, ready to be a "%d" key.
            string shape = System.Text.RegularExpressions.Regex.Replace(text, "[0-9]+", "%d");
            if (!s_missing.Add(shape)) return;
            try { File.AppendAllText(MissingPath, Escape(shape) + "\n", Encoding.UTF8); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
        }

        static bool IsLatin(char c) => c >= 'A' && c <= 'Z' || c >= 'a' && c <= 'z';

        /// <summary>One line, as a JSON string body, ready to paste as a key.</summary>
        static string Escape(string s)
        {
            var sb = new StringBuilder(s.Length + 8);
            foreach (char c in s)
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': break;
                    case '\t': sb.Append("\\t"); break;
                    default: sb.Append(c); break;
                }
            return "\"" + sb + "\"";
        }

        // ---- the cockpit font ----

        /// <summary>Extra letters of a cockpit FONT resource packed in one row, white ink like its own atlas.</summary>
        public sealed class HudLetters
        {
            public Texture2D Atlas;
            public readonly Dictionary<char, (int X, int W)> Glyphs = new Dictionary<char, (int, int)>();
        }

        /// <summary>The extra letters for the cockpit font <paramref name="name"/> of height <paramref name="height"/>, or null.</summary>
        public static HudLetters Hud(string name, int height)
        {
            if (!Active) return null;
            if (s_hud.TryGetValue(name, out var h)) return h;
            s_hud[name] = null;
            if (!Current.Fonts.TryGetValue(name, out var set) || set.Height != height || set.Glyphs.Count == 0) return null;
            h = new HudLetters();
            int total = 0;
            foreach (var g in set.Glyphs) { h.Glyphs[g.Key] = (total, g.Value.Width); total += g.Value.Width + 1; }
            var tex = new Texture2D(Math.Max(1, total), height, TextureFormat.RGBA32, false)
            {
                name = name + " letters",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };
            var px = new Color32[tex.width * height];
            foreach (var g in set.Glyphs)
            {
                int x0 = h.Glyphs[g.Key].X;
                for (int y = 0; y < height; y++)
                    for (int x = 0; x < g.Value.Width; x++)
                        if (g.Value.Pixels[y * g.Value.Width + x] == 1)
                            px[(height - 1 - y) * tex.width + x0 + x] = new Color32(255, 255, 255, 255);
            }
            tex.SetPixels32(px);
            tex.Apply();
            h.Atlas = tex;
            s_hud[name] = h;
            return h;
        }
    }
}
