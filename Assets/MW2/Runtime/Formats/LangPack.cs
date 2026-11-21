using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace MW2.Formats
{
    /// <summary>
    /// A language of the active mods, from their "lang" folders (a later mod in the list overrides an earlier one):
    /// - lang/*.json: { "English text": "translation", ... }. The key is the text exactly as the game shows it; "%d" and
    ///   "%s" in a key stand for a number and for any text, and the translation takes them in the same order. Text the
    ///   game puts together from parts ("line|line#Ok" windows, "~" centred lines, padding spaces) is tried whole first,
    ///   then by its parts.
    /// - lang/briefings/NAME.txt: the whole text of a briefing or debriefing resource (BWD ORDR), escapes included.
    /// - lang/news/NAME.txt: the whole ComStar news text (NEWS chunk) of the campaign step resource NAME, escapes included.
    /// - lang/fonts/NAME.json: { "height": H, "glyphs": { "letter": [ "rows of '#' ink, '+' shadow, '.' empty" ], ... } }, the
    ///   letters a font of the game lacks (any letter from U+00A0; a drawing here wins over the font's own); NAME is shell5,
    ///   shell6, shell7, shell9, shell10, shell11 (DATABASE.MW2 records) or BASE6X76 (the cockpit font). A Latin-1 letter
    ///   with no drawing takes the shell font's own code page 437 glyph (<see cref="Cp437"/>), else its plain letter
    ///   (<see cref="BaseLetter"/>).
    /// Without such files the game shows its own text. Port addition with no counterpart in the original program.
    /// </summary>
    public sealed class LangPack
    {
        public const string Folder = "lang";

        public sealed class Glyph
        {
            public int Width;
            /// <summary>Row by row from the top: 0 empty, 1 ink, 2 shadow.</summary>
            public byte[] Pixels;
        }

        public sealed class GlyphSet
        {
            public int Height;
            public readonly Dictionary<char, Glyph> Glyphs = new Dictionary<char, Glyph>();
        }

        sealed class Pattern
        {
            public Regex Match;
            public string Key, Value;
            public string[] Parts;  // the translation cut at its markers
            public bool[] Text;      // marker i is %s (its text is translated too when the pack has it)
        }

        readonly Dictionary<string, string> _exact = new Dictionary<string, string>(StringComparer.Ordinal);
        readonly List<Pattern> _patterns = new List<Pattern>();
        public readonly Dictionary<string, string> Briefings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, string> News = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, GlyphSet> Fonts = new Dictionary<string, GlyphSet>(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> Warnings = new List<string>();

        public int Count => _exact.Count + _patterns.Count;
        public bool Empty => Count == 0 && Briefings.Count == 0 && News.Count == 0 && Fonts.Count == 0;

        static readonly Regex Marker = new Regex("%[ds]", RegexOptions.CultureInvariant);

        /// <summary>Adds the pairs of a strings file; a file that is not a JSON object throws <see cref="MiniJson.Error"/>.</summary>
        public void AddStrings(string json, string source)
        {
            if (!(MiniJson.Parse(json, true) is Dictionary<string, object> root))
                throw new MiniJson.Error("the file must hold one { \"English\": \"translation\", ... } object");
            foreach (var pair in root)
            {
                if (pair.Key == "comment") continue;
                if (!(pair.Value is string value)) { Warnings.Add($"{source}: \"{Short(pair.Key)}\" needs a text in quotes"); continue; }
                if (pair.Key.Length == 0 || value.Length == 0) continue;
                var keyMarks = Marker.Matches(pair.Key);
                if (keyMarks.Count == 0) { _exact[pair.Key] = value; continue; }
                if (Marker.Replace(pair.Key, "").Trim().Length == 0) { Warnings.Add($"{source}: \"{pair.Key}\" has no text of its own"); continue; }
                var valueMarks = Marker.Matches(value);
                bool same = valueMarks.Count == keyMarks.Count;
                for (int i = 0; same && i < keyMarks.Count; i++) same = keyMarks[i].Value == valueMarks[i].Value;
                if (!same) { Warnings.Add($"{source}: \"{Short(pair.Key)}\" must keep its %d / %s in the same order"); continue; }
                var re = new StringBuilder("^");
                int at = 0;
                foreach (Match m in keyMarks)
                {
                    re.Append(Regex.Escape(pair.Key.Substring(at, m.Index - at)));
                    re.Append(m.Value == "%d" ? "(-?\\d+)" : "(.*?)");
                    at = m.Index + m.Length;
                }
                re.Append(Regex.Escape(pair.Key.Substring(at))).Append('$');
                _patterns.RemoveAll(p => p.Key == pair.Key);
                var kinds = new bool[keyMarks.Count];
                for (int i = 0; i < kinds.Length; i++) kinds[i] = keyMarks[i].Value == "%s";
                _patterns.Add(new Pattern
                {
                    Key = pair.Key,
                    Value = value,
                    Match = new Regex(re.ToString(), RegexOptions.CultureInvariant | RegexOptions.Singleline),
                    Parts = Marker.Split(value),
                    Text = kinds
                });
            }
            // The most specific pattern first: the one with the most text of its own.
            _patterns.Sort((a, b) => Marker.Replace(b.Key, "").Length.CompareTo(Marker.Replace(a.Key, "").Length));
        }

        /// <summary>Adds the glyphs of a font file over those already read for that font.</summary>
        public void AddFont(string name, string json, string source)
        {
            if (!(MiniJson.Parse(json, true) is Dictionary<string, object> root))
                throw new MiniJson.Error("the file must hold one { \"height\": ..., \"glyphs\": { ... } } object");
            int height = root.TryGetValue("height", out var hv) && hv is double hd ? (int)hd : 0;
            if (height <= 0) { Warnings.Add($"{source}: \"height\" is missing"); return; }
            if (!Fonts.TryGetValue(name, out var set) || set.Height != height) Fonts[name] = set = new GlyphSet { Height = height };
            if (!(root.TryGetValue("glyphs", out var g) && g is Dictionary<string, object> glyphs)) { Warnings.Add($"{source}: \"glyphs\" is missing"); return; }
            foreach (var pair in glyphs)
            {
                if (pair.Key.Length != 1) { Warnings.Add($"{source}: \"{pair.Key}\" must be one letter"); continue; }
                if (!(pair.Value is List<object> rows) || rows.Count != height) { Warnings.Add($"{source}: \"{pair.Key}\" needs {height} rows"); continue; }
                int width = rows[0] is string first ? first.Length : 0;
                var px = new byte[width * height];
                bool ok = width > 0;
                for (int y = 0; ok && y < height; y++)
                {
                    if (!(rows[y] is string row) || row.Length != width) { ok = false; break; }
                    for (int x = 0; x < width; x++)
                        px[y * width + x] = row[x] == '#' ? (byte)1 : row[x] == '+' ? (byte)2 : (byte)0;
                }
                if (!ok) { Warnings.Add($"{source}: the rows of \"{pair.Key}\" must all be {width} characters long"); continue; }
                set.Glyphs[pair.Key[0]] = new Glyph { Width = width, Pixels = px };
            }
        }

        /// <summary>The translation written for exactly this key, its %d / %s markers included, or null: for a text the
        /// program fills in after translating (Mw2Lang.Format).</summary>
        public string Key(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            if (_exact.TryGetValue(key, out var t)) return t;
            return _patterns.Find(p => p.Key == key)?.Value;
        }

        /// <summary>The translation of <paramref name="text"/>, or null when the pack has none.</summary>
        public string Translate(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            string whole = Whole(text);
            if (whole != null) return whole;
            // Parts of a window text: "line|line#Button|Button".
            if (text.IndexOf('|') >= 0 || text.IndexOf('#') >= 0)
            {
                var parts = Regex.Split(text, "([|#])");
                bool any = false;
                for (int i = 0; i < parts.Length; i += 2)
                {
                    string t = Whole(parts[i]);
                    if (t != null) { parts[i] = t; any = true; }
                }
                if (any) return string.Concat(parts);
            }
            return null;
        }

        /// <summary>Exact, then without a leading '~' and the spaces around, then by pattern.</summary>
        string Whole(string text)
        {
            if (_exact.TryGetValue(text, out var t)) return t;
            int a = 0, b = text.Length;
            while (a < b && (text[a] == '~' || text[a] == ' ')) a++;
            while (b > a && text[b - 1] == ' ') b--;
            if (b <= a) return null;
            string core = a == 0 && b == text.Length ? text : text.Substring(a, b - a);
            if (!ReferenceEquals(core, text) && _exact.TryGetValue(core, out t)) return text.Substring(0, a) + t + text.Substring(b);
            foreach (var p in _patterns)
            {
                var m = p.Match.Match(core);
                if (!m.Success) continue;
                var sb = new StringBuilder(text, 0, a, text.Length + 16);
                for (int i = 0; i < p.Parts.Length; i++)
                {
                    sb.Append(p.Parts[i]);
                    if (i + 1 >= p.Parts.Length) continue;
                    string v = m.Groups[i + 1].Value;
                    sb.Append(p.Text[i] && v.Length > 0 && !ReferenceEquals(v, text) ? Whole(v) ?? v : v);
                }
                sb.Append(text, b, text.Length - b);
                return sb.ToString();
            }
            return null;
        }

        /// <summary>Every translation of the strings files, exact and pattern ones.</summary>
        public IEnumerable<string> Values() => _exact.Values.Concat(_patterns.Select(p => p.Value));

        // ---- letters ----

        /// <summary>
        /// The game's fonts hold letters 128..255 in the DOS code page 437 layout (the shell records 5..11 have ink at
        /// 0x80 C cedilla ... 0xe1 sharp s), while a translation is Unicode: the code page 437 code of a Latin-1 letter
        /// U+00A0..U+00FF, or -1 when the code page has none.
        /// </summary>
        public static int Cp437(char c) => c >= 0xa0 && c <= 0xff ? Cp437Codes[c - 0xa0] : -1;

        static readonly short[] Cp437Codes =
        {
            255, 173, 155, 156, -1, 157, -1, -1, -1, -1, 166, 174, 170, -1, -1, -1,
            248, 241, 253, -1, -1, 230, -1, 250, -1, -1, 167, 175, 172, 171, -1, 168,
            -1, -1, -1, -1, 142, 143, 146, 128, -1, 144, -1, -1, -1, -1, -1, -1,
            -1, 165, -1, -1, -1, -1, 153, -1, -1, -1, -1, -1, 154, -1, -1, 225,
            133, 160, 131, -1, 132, 134, 145, 135, 138, 130, 136, 137, 141, 161, 140, 139,
            -1, 164, 149, 162, 147, -1, 148, 246, -1, 151, 163, 150, 129, -1, -1, 152,
        };

        /// <summary>
        /// The plain letter under an accented one (e with acute -> e, n with tilde -> n, l with stroke -> l), for a
        /// font that has neither the letter nor a language mod's drawing of it; 0 when there is none.
        /// </summary>
        public static char BaseLetter(char c)
        {
            if (c < 0x80) return c;
            int i = "\u0142\u0141\u00f8\u00d8\u0111\u0110\u0131\u0153\u0152\u00e6\u00c6\u00df".IndexOf(c);
            if (i >= 0) return "lLoOdDioOaAs"[i];
            try
            {
                string d = c.ToString().Normalize(NormalizationForm.FormD);
                if (d.Length > 0 && d[0] < 0x80 && char.IsLetter(d[0])) return d[0];
            }
            catch (ArgumentException) { }
            return (char)0;
        }

        static string Short(string s) => s.Length <= 40 ? s : s.Substring(0, 37) + "...";
    }
}
