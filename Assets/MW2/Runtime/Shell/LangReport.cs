using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MW2.Formats;

namespace MW2.Shell
{
    /// <summary>
    /// The check of a language mod's lang folder, written to lang-report.txt beside it by LANGUAGE TEMPLATE (Options ->
    /// Mods). Each translated whole text is laid out on the page of the screen that shows it, with the game's fonts and
    /// the mod's letters, and compared with the game's own text on the same page:
    /// - briefings and debriefings (BriefingScreen page 0x22, 0x1d, 0x12d x 0x172): no more pages than the game's text;
    /// - credit biographies (*BIO, CreditsBioScreen 47, 65, 549 x 286, first page only): no more pages either;
    /// - contracts (CAM / NOVICE, ContractsScreen 0x2f, 0x41, 0x225 x 0x11e, one page): fits when the game's text does;
    /// - dossiers (PersonnelScreen 0x23, 0x60, 0x90 x 0x103, one page): fits when the game's text does;
    /// - ComStar news (NewsTerminalScreen 0x22, 0x43, 0x1ad x 0x17a): no line wider than the page;
    /// - letters: every letter of the translations the shell font shell11 and the cockpit font BASE6X76 draw only as a
    ///   plain letter, or not at all.
    /// The strings of lang/*.json have no page of their own and are checked only for letters. Port addition with no
    /// counterpart in the original program.
    /// </summary>
    public static class LangReport
    {
        public const string FileName = "lang-report.txt";

        /// <summary>The names put in for \m and \p (FUN_100073f0): as long as a usual player's, so that a text which fits
        /// only with short names shows up.</summary>
        public const string UnitName = "UNIT NAME", PilotName = "PILOT NAME";

        public sealed class Result
        {
            public readonly List<string> Problems = new List<string>();
            public readonly List<string> Letters = new List<string>();
            public int Briefings, Contracts, Dossiers, News;
        }

        /// <summary>The lang folder <paramref name="lang"/> as one language: strings, briefings, news and letters.</summary>
        public static LangPack Load(string lang)
        {
            var pack = new LangPack();
            foreach (string f in Directory.GetFiles(lang, "*.json"))
                try { pack.AddStrings(File.ReadAllText(f), Path.GetFileName(f)); }
                catch (MiniJson.Error e) { pack.Warnings.Add(Path.GetFileName(f) + ": " + e.Message); }
            string fonts = Path.Combine(lang, "fonts");
            if (Directory.Exists(fonts))
                foreach (string f in Directory.GetFiles(fonts, "*.json"))
                    try { pack.AddFont(Path.GetFileNameWithoutExtension(f), File.ReadAllText(f), "fonts/" + Path.GetFileName(f)); }
                    catch (MiniJson.Error e) { pack.Warnings.Add("fonts/" + Path.GetFileName(f) + ": " + e.Message); }
            foreach (var (sub, target) in new[] { ("briefings", pack.Briefings), ("news", pack.News) })
            {
                string folder = Path.Combine(lang, sub);
                if (!Directory.Exists(folder)) continue;
                foreach (string f in Directory.GetFiles(folder, "*.txt"))
                    target[Path.GetFileNameWithoutExtension(f)] = File.ReadAllText(f).Replace("\r\n", "\n");
            }
            return pack;
        }

        /// <summary>Checks <paramref name="pack"/> against the game in <paramref name="data"/> (fonts from
        /// <paramref name="database"/>). The shell's language hooks are set for the check and put back after it.</summary>
        public static Result Check(LangPack pack, ShellData data, ShellDatabase database)
        {
            var r = new Result();
            r.Problems.AddRange(pack.Warnings);
            var career = new Career { UnitName = UnitName };
            var pilot = new PilotRecord();
            pilot.SetStr(0, PilotName);
            pilot.Data[0x2064] = 1;
            career.UnitPilots.Add(pilot);

            var oldTranslate = ShellFont.Translate;
            var oldExtra = ShellFont.ExtraGlyphs;
            var oldBriefing = ShellBriefing.Translated;
            var oldNews = ShellBriefing.TranslatedNews;
            try
            {
                // The game's own text first: no language at all.
                SetLanguage(null);
                var font = new ShellFont(database.Raw(11), "shell11");
                var large = new ShellFont(database.Raw(7), "shell7");
                var english = new Dictionary<string, (int Pages, bool Fits, string Lines)>(StringComparer.OrdinalIgnoreCase);
                foreach (string resource in pack.Briefings.Keys)
                {
                    var page = Page(resource);
                    if (page.Contract)
                    {
                        string text = GameText(data, resource);
                        if (text == null) continue;
                        var p = new ShellTextPage(font, large, page.X, page.Y, page.Width, page.Height);
                        english[resource] = (1, p.Layout(ShellBriefing.ExpandNames(text, career)) < 0, "");
                    }
                    else
                    {
                        var pages = ShellBriefing.BuildPages(data, resource, career, "", font, large, page.X, page.Y, page.Width, page.Height);
                        english[resource] = (pages.Count, true, string.Join("/", pages.Select(pg => pg.Lines.Count)));
                    }
                }
                var dossiers = new List<(string Resource, string Text)>();
                foreach (var entry in data.Prj.Entries("BWD").OrderBy(e => e.Name, StringComparer.Ordinal))
                {
                    string text = LangTemplate.Dossier(data.Prj.Read(entry));
                    if (text != null) dossiers.Add((entry.Name, text));
                }
                var dossierFits = dossiers.ToDictionary(d => d.Resource, d =>
                    new ShellTextPage(font, large, 0x23, 0x60, 0x90, 0x103).Layout(d.Text) < 0);

                // Then the language, laid out as the screens do it: the translation is the text, nothing is looked up again.
                SetLanguage(pack);
                ShellFont.Translate = s => null;
                font = new ShellFont(database.Raw(11), "shell11");
                large = new ShellFont(database.Raw(7), "shell7");
                foreach (var pair in pack.Briefings.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
                {
                    string resource = pair.Key.ToUpperInvariant();
                    if (!english.TryGetValue(resource, out var en)) { r.Problems.Add($"briefings/{pair.Key}.txt: the game has no such text"); continue; }
                    string game = GameText(data, resource);
                    if (game != null && game == pair.Value) continue;     // not translated yet
                    var page = Page(resource);
                    if (page.Contract)
                    {
                        var p = new ShellTextPage(font, large, page.X, page.Y, page.Width, page.Height);
                        bool fits = p.Layout(ShellBriefing.ExpandNames(pair.Value, career)) < 0;
                        if (en.Fits && !fits) r.Problems.Add($"briefings/{pair.Key}.txt: the contract does not fit its page ({p.Lines.Count} lines fit)");
                        Widths(r, $"briefings/{pair.Key}.txt", new[] { p }, font, page.Width);
                        r.Contracts++;
                        continue;
                    }
                    var pages = ShellBriefing.BuildPages(data, resource, career, "", font, large, page.X, page.Y, page.Width, page.Height);
                    if (pages.Count > en.Pages)
                        r.Problems.Add($"briefings/{pair.Key}.txt: {pages.Count} pages, the game's text {en.Pages} (lines per page " +
                                       $"{string.Join("/", pages.Select(pg => pg.Lines.Count))}, the game's {en.Lines}, at most " +
                                       $"{page.Height / (font.Height + 2)} a page)" + (page.Bio ? "; the screen shows the first page only" : ""));
                    Widths(r, $"briefings/{pair.Key}.txt", pages, font, page.Width);
                    r.Briefings++;
                }
                foreach (var (resource, text) in dossiers)
                {
                    string own = pack.Translate(text);
                    if (own == null) continue;
                    var p = new ShellTextPage(font, large, 0x23, 0x60, 0x90, 0x103);
                    if (dossierFits[resource] && p.Layout(own) >= 0)
                        r.Problems.Add($"dossier of {resource}: does not fit the personnel page ({p.Lines.Count} lines fit)");
                    r.Dossiers++;
                }
                foreach (var pair in pack.News.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
                {
                    string text = ShellBriefing.ExpandNames(pair.Value, career);
                    var pages = new List<ShellTextPage>();
                    int at = 0;
                    do
                    {
                        var page = new ShellTextPage(font, large, 0x22, 0x43, 0x1ad, 0x17a);
                        int next = page.Layout(text, at);
                        pages.Add(page);
                        if (next == at || pages.Count > 20) break;
                        at = next;
                    } while (at >= 0);
                    Widths(r, $"news/{pair.Key}.txt", pages, font, 0x1ad);
                    r.News++;
                }
                Letters(r, pack, font);
            }
            finally
            {
                ShellFont.Translate = oldTranslate;
                ShellFont.ExtraGlyphs = oldExtra;
                ShellBriefing.Translated = oldBriefing;
                ShellBriefing.TranslatedNews = oldNews;
                ShellFont.ExtraVersion++;
            }
            return r;
        }

        /// <summary>Writes the result to <paramref name="path"/>.</summary>
        public static void Write(string path, string folder, Result r)
        {
            var b = new StringBuilder();
            b.Append("Language check of ").Append(folder).Append('\n');
            b.Append($"Checked: {r.Briefings} briefings, {r.Contracts} contracts, {r.Dossiers} dossiers, {r.News} news.\n");
            b.Append($"Texts are laid out with the unit name \"{UnitName}\" for \\m and the pilot name \"{PilotName}\" for \\p; " +
                     "texts still the same as the game's are not checked.\n");
            b.Append($"Problems: {r.Problems.Count}\n");
            foreach (string p in r.Problems) b.Append("  ").Append(p).Append('\n');
            if (r.Letters.Count > 0)
            {
                b.Append("Letters the game's fonts lack (draw them in lang/fonts/<font>.json; samples in font-samples):\n");
                foreach (string l in r.Letters) b.Append("  ").Append(l).Append('\n');
            }
            File.WriteAllText(path, b.ToString(), new UTF8Encoding(false));
        }

        static void SetLanguage(LangPack pack)
        {
            ShellFont.Translate = pack == null ? (Func<string, string>)null : pack.Translate;
            ShellFont.ExtraGlyphs = pack == null ? (Func<string, LangPack.GlyphSet>)null : name =>
                pack.Fonts.TryGetValue(name, out var set) ? set
                : (name == "shell9" || name == "shell10") && pack.Fonts.TryGetValue("shell6", out set) ? set : null;
            ShellBriefing.Translated = pack == null ? (Func<string, string>)null
                : resource => pack.Briefings.TryGetValue(resource, out string t) ? t : null;
            ShellBriefing.TranslatedNews = null;
            ShellFont.ExtraVersion++;
        }

        /// <summary>The page a whole text is shown on, by its resource name.</summary>
        static (int X, int Y, int Width, int Height, bool Contract, bool Bio) Page(string resource)
        {
            string r = resource.ToUpperInvariant();
            if (r.Length == 8 && r.Substring(4, 3) == "CAM" || r.StartsWith("NOVICE", StringComparison.Ordinal))
                return (0x2f, 0x41, 0x225, 0x11e, true, false);
            if (r.EndsWith("BIO", StringComparison.Ordinal)) return (47, 65, 549, 286, false, true);
            return (0x22, 0x1d, 0x12d, 0x172, false, false);
        }

        /// <summary>The game's whole text of a resource as LangTemplate writes it, or null.</summary>
        static string GameText(ShellData data, string resource)
        {
            byte[] d = data.ReadBwd(resource);
            return d == null ? null : LangTemplate.WholeText(d).Order;
        }

        static void Widths(Result r, string file, IEnumerable<ShellTextPage> pages, ShellFont font, int width)
        {
            foreach (var page in pages)
                foreach (var line in page.Lines)
                    if (!line.Large && font.RawWidth(line.Text.TrimEnd()) > width)
                    {
                        r.Problems.Add($"{file}: a line wider than the page: {line.Text.Trim()}");
                        return;
                    }
        }

        /// <summary>Every letter from U+00A0 the translations use that shell11 or BASE6X76 has no drawing of.</summary>
        static void Letters(Result r, LangPack pack, ShellFont shell)
        {
            var used = new SortedSet<char>();
            foreach (string text in pack.Values().Concat(pack.Briefings.Values).Concat(pack.News.Values))
                foreach (char c in text) if (c >= 0xa0) used.Add(c);
            pack.Fonts.TryGetValue("BASE6X76", out var hud);
            foreach (char c in used)
            {
                char plain = LangPack.BaseLetter(c);
                string drawn = plain != 0 ? $"drawn as '{plain}'" : "not drawn";
                if (!shell.Has(c)) r.Letters.Add($"'{c}' (U+{(int)c:X4}) in the shell font shell11: {drawn}");
                if (hud == null || !hud.Glyphs.ContainsKey(c)) r.Letters.Add($"'{c}' (U+{(int)c:X4}) in the cockpit font BASE6X76: {drawn}");
            }
        }
    }
}
