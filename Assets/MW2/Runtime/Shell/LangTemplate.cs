using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using MW2.Formats;

namespace MW2.Shell
{
    /// <summary>
    /// The language template: a mod folder with every text of the game a translator fills in, made from the player's own
    /// copy of the game (Options -> Mods -> LANGUAGE TEMPLATE). Port addition with no counterpart in the original program.
    ///
    /// lang/*.json get the texts with empty translations (LangPack leaves an empty translation out, so the game keeps its
    /// own text until one is written): the interface texts of the port's code (<see cref="LangCatalog"/>), the shown
    /// strings of the shell tables (items.json, menu.json, credits.json, rmg.json), the command names and group headings of
    /// Cockpit Controls (controls.json), the texts the missions hand to the
    /// cockpit (mission.json: MTBL order texts, unit, structure and nav point names, inspection texts), the pilot and
    /// AeroTech pilot names and dossiers (dossiers.json), the keys of the other active language mods (under their file
    /// names) and the texts noted in lang-missing.txt while playing (missing.json).
    /// lang/briefings/NAME.txt and lang/news/NAME.txt get the English text of every briefing, debriefing and contract
    /// (ORDR / CTNT) and every ComStar news text (NEWS), to be translated in place. font-samples/NAME.json show the letters
    /// each font of the game already has, in the lang/fonts format, to copy into lang/fonts/NAME.json and change (outside
    /// lang/, so the game never loads them; rewritten every run).
    ///
    /// Running it again keeps everything written so far: a text already in any strings file of the folder is not added
    /// again, a file that exists is not replaced, and a strings file that is not valid JSON is left alone.
    /// </summary>
    public static class LangTemplate
    {
        public const string DefaultId = "my-language";

        public sealed class Result
        {
            public string Folder;
            public int Texts, Added, Briefings, News, Fonts;
            public readonly List<string> Problems = new List<string>();
        }

        /// <summary>The table fields that reach a font, by path from <see cref="ShellTables"/> (a final * takes every field
        /// starting so), and the file they go to.</summary>
        static readonly (string Path, string File)[] TableFields =
        {
            ("Tables.Economy.Weapons.Name", "items"), ("Tables.Economy.Equipment.Name", "items"),
            ("Tables.Economy.Items.Name", "items"), ("Tables.Lab.Weapons.Name", "items"), ("Tables.Lab.SectionNames", "items"),
            ("Tables.Lab.Messages.*", "items"),
            ("Tables.Defaults.Menu.Items.Text", "menu"), ("Tables.Defaults.Menu.Text", "menu"),
            ("Tables.Defaults.Credits", "credits"),
            ("Tables.Instant.Planets.Name", "rmg"), ("Tables.Rmg.Planets.Name", "rmg"), ("Tables.Rmg.Factions.Name", "rmg"),
            ("Tables.Rmg.MissionTypes", "rmg"), ("Tables.Rmg.Text.Briefing", "rmg"), ("Tables.Rmg.Text.Contract", "rmg"),
            ("Tables.Rmg.Text.DebriefingFailed", "rmg"), ("Tables.Rmg.Text.DebriefingSucceeded", "rmg"),
            ("Tables.Rmg.Text.InstantActionLabel", "rmg"), ("Tables.Rmg.Text.NavAlpha", "rmg"),
            ("Tables.Rmg.Text.NavBeta", "rmg"), ("Tables.Rmg.Text.NavGamma", "rmg"), ("Tables.Rmg.Text.NavLabel", "rmg"),
            ("Tables.Rmg.Text.StrengthLight", "rmg"), ("Tables.Rmg.Text.StrengthMedium", "rmg"),
            ("Tables.Rmg.Text.StrengthHeavy", "rmg"), ("Tables.Rmg.Text.Error*", "rmg"),
        };

        static readonly Dictionary<string, string> Comments = new Dictionary<string, string>
        {
            ["interface"] = "Write each translation between the empty quotes; an empty one keeps the game's text. %d is a number, %s a text: keep them in the same order.",
            ["items"] = "Names of weapons, equipment and items, MechLab sections and MechLab messages.",
            ["menu"] = "The in-mission menu.",
            ["credits"] = "The credits.",
            ["rmg"] = "Random missions and instant action: planets, factions, mission types and the generated briefing, contract and debriefing texts.",
            ["controls"] = "Cockpit Controls: the group headings and the names of the game's commands (gamekey.map).",
            ["mission"] = "Texts of the missions: objectives, unit, structure and nav point names, inspection texts.",
            ["dossiers"] = "Pilot and AeroTech pilot names and dossiers, whole text as the personnel screen shows it.",
            ["missing"] = "Texts the game showed without a translation while playing (lang-missing.txt); numbers are written as %d.",
        };

        /// <summary>
        /// Writes or updates the template in <paramref name="folder"/>. <paramref name="otherKeys"/> are the strings files of
        /// other language mods (file name without .json, keys); <paramref name="missing"/> the lines of lang-missing.txt.
        /// </summary>
        public static Result Write(string folder, ShellData data, ShellDatabase database, IEnumerable<string> weaponNames,
            IEnumerable<(string File, IEnumerable<string> Keys)> otherKeys, IEnumerable<string> missing)
        {
            var r = new Result { Folder = folder };
            string lang = Path.Combine(folder, LangPack.Folder);
            Directory.CreateDirectory(Path.Combine(lang, "briefings"));
            Directory.CreateDirectory(Path.Combine(lang, "news"));
            string modJson = Path.Combine(folder, ModCatalog.Manifest);
            if (!File.Exists(modJson))
                File.WriteAllText(modJson, MiniJson.Write(new Dictionary<string, object>
                {
                    ["id"] = Path.GetFileName(Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                    ["name"] = "My language",
                    ["version"] = "0.1",
                    ["author"] = "",
                    ["description"] = "A translation of the game's text.",
                    ["api"] = ModCatalog.Api,
                }) + "\n", new UTF8Encoding(false));

            // The strings files as they stand; a text already in one is never added to another.
            var files = new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
            var order = new List<string>();
            var locked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var known = new HashSet<string>(StringComparer.Ordinal);
            var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in Directory.GetFiles(lang, "*.json").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                string name = Path.GetFileNameWithoutExtension(path);
                try
                {
                    if (!(MiniJson.Parse(File.ReadAllText(path), true) is Dictionary<string, object> root))
                        throw new MiniJson.Error("not one { ... } object");
                    files[name] = root;
                    order.Add(name);
                    foreach (string key in root.Keys) if (key != "comment") known.Add(key);
                }
                catch (Exception e) when (e is MiniJson.Error || e is IOException || e is UnauthorizedAccessException)
                {
                    locked.Add(name);
                    r.Problems.Add($"{LangPack.Folder}/{name}.json: {e.Message}; nothing added to it");
                }
            }

            void Add(string file, string text)
            {
                if (string.IsNullOrWhiteSpace(text) || text == "comment" || known.Contains(text) || locked.Contains(file)) return;
                if (!Worth(text)) return;
                if (!files.TryGetValue(file, out var root))
                {
                    files[file] = root = new Dictionary<string, object>();
                    order.Add(file);
                    if (Comments.TryGetValue(file, out string c)) root["comment"] = c;
                }
                root[text] = "";
                known.Add(text);
                changed.Add(file);
                r.Added++;
            }

            foreach (var (_, text) in LangCatalog.Texts) Add("interface", text);
            foreach (var (file, text) in TableTexts(data.Tables)) Add(file, text);
            if (weaponNames != null) foreach (string w in weaponNames) Add("items", w);
            foreach (string text in ControlTexts(data.GameFolder, r.Problems)) Add("controls", text);
            foreach (string text in MissionTexts(data.Prj, r.Problems)) Add("mission", text);
            foreach (string text in DossierTexts(data.Prj)) Add("dossiers", text);
            if (otherKeys != null)
                foreach (var (file, keys) in otherKeys)
                    foreach (string key in keys) Add(file, key);
            if (missing != null)
                foreach (string line in missing)
                {
                    string t = line.Trim();
                    if (t.Length == 0) continue;
                    try { if (MiniJson.Parse(t, false) is string s) Add("missing", s); }
                    catch (MiniJson.Error) { }
                }

            foreach (string name in order)
            {
                r.Texts += files[name].Keys.Count(k => k != "comment");
                if (!changed.Contains(name)) continue;
                File.WriteAllText(Path.Combine(lang, name + ".json"), MiniJson.Write(files[name]) + "\n", new UTF8Encoding(false));
            }

            WholeTexts(data.Prj, lang, r);
            FontSamples(folder, database, data.Prj, r);
            return r;
        }

        /// <summary>A text a translator can do something with: two letters in a row somewhere.</summary>
        static bool Worth(string text)
        {
            for (int i = 1; i < text.Length; i++)
                if (char.IsLetter(text[i]) && char.IsLetter(text[i - 1])) return true;
            return false;
        }

        // ---- shell tables ----

        /// <summary>The strings of <see cref="TableFields"/>, found by walking the public fields of the tables.</summary>
        public static IEnumerable<(string File, string Text)> TableTexts(ShellTables tables)
        {
            var found = new List<(string, string)>();
            var seen = new HashSet<object>(new ReferenceComparer());
            void Walk(object o, string path, int depth)
            {
                if (o == null || depth > 7) return;
                if (o is string s)
                {
                    foreach (var (prefix, file) in TableFields)
                        if (prefix.EndsWith("*") ? path.StartsWith(prefix.Substring(0, prefix.Length - 1), StringComparison.Ordinal) : path == prefix)
                        {
                            found.Add((file, s));
                            break;
                        }
                    return;
                }
                var t = o.GetType();
                if (t.IsPrimitive || t.IsEnum || o is byte[] || !seen.Add(o)) return;
                if (o is System.Collections.IEnumerable list)
                {
                    foreach (var item in list) Walk(item, path, depth + 1);
                    return;
                }
                if (t.Namespace == null || !t.Namespace.StartsWith("MW2", StringComparison.Ordinal)) return;
                foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
                    Walk(f.GetValue(o), path + "." + f.Name, depth + 1);
                foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                    if (p.GetIndexParameters().Length == 0 && p.CanRead && p.DeclaringType == t)
                        try { Walk(p.GetValue(o), path + "." + p.Name, depth + 1); }
                        catch (Exception) { }
            }
            Walk(tables, "Tables", 0);
            return found;
        }

        sealed class ReferenceComparer : IEqualityComparer<object>
        {
            bool IEqualityComparer<object>.Equals(object a, object b) => ReferenceEquals(a, b);
            public int GetHashCode(object o) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o);
        }

        // ---- controls ----

        /// <summary>
        /// The texts of the Cockpit Controls screens (ControlsScreen, Mw2CockpitControlsMenu) that come from the game: the
        /// action group headings in capitals, then each command of gamekey.map with its group heading in capitals.
        /// </summary>
        public static IEnumerable<string> ControlTexts(string gameFolder, List<string> problems)
        {
            var list = new List<string>();
            foreach (var a in ControlBindings.Actions) list.Add(a.Group.ToUpperInvariant());
            try
            {
                foreach (string c in ControlBindings.CommandNames(InputSystem.ReadMap(gameFolder, "gamekey.map")))
                {
                    list.Add(ControlBindings.CommandGroup(c).ToUpperInvariant());
                    list.Add(ControlBindings.CommandText(c));
                }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { problems?.Add("gamekey.map: " + e.Message); }
            return list;
        }

        // ---- missions and dossiers ----

        /// <summary>The texts each mission of MW2.PRJ (BWD ...SCN1) hands to the cockpit font, in mission order.</summary>
        public static IEnumerable<string> MissionTexts(PrjArchive prj, List<string> problems)
        {
            var list = new List<string>();
            foreach (var entry in prj.Entries("BWD").Where(e => e.Name.EndsWith("SCN1", StringComparison.OrdinalIgnoreCase))
                                                  .OrderBy(e => e.Name, StringComparer.Ordinal))
            {
                string prefix = entry.Name.Substring(0, entry.Name.Length - 4).ToUpperInvariant();
                try
                {
                    var m = Mission.Load(prj, prefix);
                    foreach (var team in m.Teams)
                        foreach (var o in team.Orders) list.Add(o.Text);
                    foreach (var s in m.Spawns) { list.Add(s.DisplayName); list.Add(s.InspectText); }
                    foreach (var d in m.Destructibles) { list.Add(d.Name); list.Add(d.Contents); }
                    foreach (var n in m.NavPoints) list.Add(n.Name);
                }
                catch (Exception e) { problems?.Add($"{prefix}: {e.Message}"); }
            }
            return list;
        }

        /// <summary>
        /// The name and dossier of every pilot (PILT) and AeroTech pilot (TECH) resource with a DSSR chunk, as the personnel
        /// screen shows them (Career.ReadPilot FUN_1006aecc / ReadTech FUN_1006b0db).
        /// </summary>
        public static IEnumerable<string> DossierTexts(PrjArchive prj)
        {
            var list = new List<string>();
            foreach (var entry in prj.Entries("BWD").OrderBy(e => e.Name, StringComparer.Ordinal))
            {
                var b = prj.Read(entry);
                string name = PersonName(b), dossier = Dossier(b);
                if (dossier == null) continue;
                list.Add(name);
                list.Add(dossier);
            }
            return list;
        }

        /// <summary>The dossier of a pilot or AeroTech pilot resource as the personnel screen shows it, or null.</summary>
        public static string Dossier(byte[] b)
        {
            string kind = PersonKind(b);
            if (kind == "PILT") return Career.ReadPilot(b)?.Str(100, 0x2000);
            if (kind == "TECH") return Career.ReadTech(b)?.Str(0x5e, 0x2000);
            return null;
        }

        static string PersonName(byte[] b)
        {
            string kind = PersonKind(b);
            if (kind == "PILT") return Career.ReadPilot(b)?.Name;
            if (kind == "TECH") return Career.ReadTech(b)?.Name;
            return null;
        }

        /// <summary>"PILT" / "TECH" for a person resource with a dossier, else null (a PILT / TECH chunk is 0x5c bytes).</summary>
        static string PersonKind(byte[] b)
        {
            if (b.Length < 0x14) return null;
            int total = BitConverter.ToInt32(b, 4), pos = 0xc;
            string kind = null;
            bool dossier = false;
            while (pos < total && pos + 8 <= b.Length)
            {
                string tag = Encoding.ASCII.GetString(b, pos, 4);
                int len = BitConverter.ToInt32(b, pos + 4);
                if (tag == "PILT" || tag == "TECH") { kind = kind ?? tag; pos += 0x5c; continue; }
                if (tag == "DSSR") dossier = true;
                if (len < 8) break;
                pos += len;
            }
            return dossier ? kind : null;
        }

        // ---- font samples ----

        /// <summary>
        /// font-samples/shellN.json for the shell records 5, 6, 7, 9, 10, 11 (FUN_100296f0: ink, 0 shadow, 0xff empty) and
        /// font-samples/BASE6X76.json for the cockpit FONT (0x0e ink): every letter with ink, codes 128..255 under the
        /// Latin-1 letter their code page 437 code stands for.
        /// </summary>
        static void FontSamples(string folder, ShellDatabase database, PrjArchive prj, Result r)
        {
            string samples = Path.Combine(folder, "font-samples");
            var unicode = new Dictionary<int, char>();
            for (char c = (char)0xa0; c <= 0xff; c++) if (LangPack.Cp437(c) >= 0) unicode[LangPack.Cp437(c)] = c;
            char? Letter(int code) => code > 32 && code < 127 ? (char)code : unicode.TryGetValue(code, out char u) ? u : (char?)null;

            void Save(string name, int height, int count, Func<int, (int Width, Func<int, int, char> Pixel)> glyph)
            {
                var glyphs = new Dictionary<string, object>();
                for (int code = 0; code < count; code++)
                {
                    char? letter = Letter(code);
                    if (letter == null) continue;
                    var (width, pixel) = glyph(code);
                    if (width <= 0) continue;
                    var rows = new List<object>();
                    bool ink = false;
                    for (int y = 0; y < height; y++)
                    {
                        var row = new StringBuilder(width);
                        for (int x = 0; x < width; x++) { char p = pixel(x, y); row.Append(p); ink |= p == '#'; }
                        rows.Add(row.ToString());
                    }
                    if (ink) glyphs[letter.Value.ToString()] = rows;
                }
                Directory.CreateDirectory(samples);
                File.WriteAllText(Path.Combine(samples, name + ".json"), MiniJson.Write(new Dictionary<string, object>
                {
                    ["comment"] = $"The letters the game's font {name} has (# ink, + shadow, . empty). Copy a letter into " +
                                  $"lang/fonts/{name}.json under its new name and change it; this file is only a sample.",
                    ["height"] = height,
                    ["glyphs"] = glyphs,
                }) + "\n", new UTF8Encoding(false));
                r.Fonts++;
            }

            if (database != null)
                foreach (int record in new[] { 5, 6, 7, 9, 10, 11 })
                {
                    byte[] d = database.Raw(record);
                    if (d == null || d.Length < 0x10) continue;
                    int count = Math.Min(BitConverter.ToInt32(d, 4), 256), height = BitConverter.ToInt32(d, 8);
                    Save("shell" + record, height, count, code =>
                    {
                        int o = BitConverter.ToInt32(d, 0x10 + code * 4);
                        if (o < 0 || o + 4 > d.Length) return (0, null);
                        int w = BitConverter.ToInt32(d, o);
                        if (w <= 0 || o + 4 + w * height > d.Length) return (0, null);
                        return (w, (x, y) => d[o + 4 + y * w + x] == 0xff ? '.' : d[o + 4 + y * w + x] == 0 ? '+' : '#');
                    });
                }
            if (prj != null && prj.TryFind("FONT", "BASE6X76", out var entry))
            {
                var hud = HudFont.Decode(prj.Read(entry));
                Save("BASE6X76", hud.Height, hud.Widths.Length, code =>
                    hud.Pixels[code] == null ? (0, null) : (hud.Widths[code], (Func<int, int, char>)((x, y) => hud.Pixels[code][y * hud.Widths[code] + x] == 0x0e ? '#' : '.')));
            }
        }

        // ---- briefings and news ----

        /// <summary>
        /// The English whole texts of every BWD: the ORDR text (FUN_100076eb: last byte zeroed, up to the first zero) or,
        /// for a contract, the CTNT text (ContractOffers), to lang/briefings/NAME.txt; the NEWS text (Campaign: its last
        /// byte left out, up to the first zero) to lang/news/NAME.txt. A file that exists is kept.
        /// </summary>
        static void WholeTexts(PrjArchive prj, string lang, Result r)
        {
            foreach (var entry in prj.Entries("BWD"))
            {
                var (order, news) = WholeText(prj.Read(entry));
                string name = entry.Name.ToUpperInvariant();
                if (order != null) { Keep(Path.Combine(lang, "briefings", name + ".txt"), order); r.Briefings++; }
                if (news != null) { Keep(Path.Combine(lang, "news", name + ".txt"), news); r.News++; }
            }
        }

        /// <summary>The ORDR (or contract CTNT) and NEWS whole texts of a BWD, each null when it has none.</summary>
        public static (string Order, string News) WholeText(byte[] d)
        {
            string order = null, news = null;
            foreach (var (tag, body, length) in Chunks(d))
            {
                if (news == null && tag == "NEWS") news = Zero(d, body, Math.Max(0, length - 1));
                if (order == null && (tag == "ORDR" || tag == "CTNT"))
                    order = Zero(d, body, tag == "ORDR" ? Math.Max(0, length - 1) : length);
            }
            return (order, news);
        }

        /// <summary>Writes <paramref name="text"/> unless the file is there already (it may hold the translation).</summary>
        static void Keep(string path, string text)
        {
            if (!File.Exists(path)) File.WriteAllText(path, text, new UTF8Encoding(false));
        }

        /// <summary>BWD chunks as Contract_Read FUN_1006b2e3 walks them: a CPGN chunk is 0xa0 bytes whatever its length says.</summary>
        static IEnumerable<(string tag, int body, int length)> Chunks(byte[] d)
        {
            if (d.Length < 0xc) yield break;
            int p = 0xc, end = Math.Min(BitConverter.ToInt32(d, 4), d.Length);
            while (p + 8 <= end)
            {
                string tag = Encoding.ASCII.GetString(d, p, 4).TrimEnd('\0');
                int length = BitConverter.ToInt32(d, p + 4);
                if (tag == "CPGN") { p += 0xa0; continue; }
                yield return (tag, p + 8, Math.Max(0, Math.Min(length - 8, d.Length - p - 8)));
                if (length < 8) yield break;
                p += length;
            }
        }

        /// <summary>The bytes up to the first zero as Latin-1, as the shell draws them (ShellBriefing.Latin1).</summary>
        static string Zero(byte[] d, int start, int max)
        {
            var b = new StringBuilder();
            for (int e = start; e < start + max && e < d.Length && d[e] != 0; e++) b.Append((char)d[e]);
            return b.ToString();
        }
    }
}
