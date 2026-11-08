using System;
using System.Collections.Generic;
using System.Text;
using MW2.Formats;

namespace MW2.Shell
{
    /// <summary>FUN_10059a4a 0x10059a4a and FUN_100076eb 0x100076eb: choose a BRF1 BWD from
    /// the first four mission-name characters and read its ORDR text chunk.</summary>
    public static class ShellBriefing
    {
        public sealed class Line
        {
            public readonly string Text;
            public readonly bool Center;
            public Line(string text, bool center) { Text = text; Center = center; }
        }

        public static string ResourceName(string mission)
        {
            var prefix = new char[4];
            int i = 0;
            while (i < 4 && i < mission.Length && mission[i] != '.')
            {
                prefix[i] = char.ToUpperInvariant(mission[i]);
                i++;
            }
            while (i < 4) prefix[i++] = '_';
            return new string(prefix) + "BRF1";
        }

        /// <summary>ORDR bytes, excluding the null padding; the shell's page formatter FUN_100765d9 consumes these bytes.</summary>
        public static byte[] ReadOrders(ShellData data, string mission)
        {
            string resource = ResourceName(mission);
            byte[] bwd = resource.StartsWith("RAN", StringComparison.Ordinal) ? data.ReadLoose(resource) : data.ReadBwd(resource);
            if (bwd == null) return null;
            foreach (var (tag, body, length) in BwdChunks.Enumerate(bwd))
                if (tag == "ORDR")
                {
                    int n = length;
                    while (n > 0 && bwd[body + n - 1] == 0) n--;
                    var result = new byte[n];
                    Buffer.BlockCopy(bwd, body, result, 0, n);
                    return result;
                }
            return null;
        }

        /// <summary>FUN_1007602a 0x1007602a returns token 3 for \s; FUN_100765d9 then starts another
        /// text page at the first byte after that escape. Automatic height-based page breaks are separate.</summary>
        public static List<byte[]> SplitExplicitPages(byte[] orders)
        {
            var pages = new List<byte[]>();
            if (orders == null) return pages;
            int start = 0;
            for (int i = 0; i + 1 < orders.Length; i++)
                if (orders[i] == '\\' && (orders[i + 1] == 's' || orders[i + 1] == 'S'))
                {
                    var part = new byte[i - start];
                    Buffer.BlockCopy(orders, start, part, 0, part.Length);
                    pages.Add(part);
                    start = i + 2;
                    i++;
                }
            var last = new byte[orders.Length - start];
            Buffer.BlockCopy(orders, start, last, 0, last.Length);
            pages.Add(last);
            return pages;
        }

        /// <summary>
        /// FUN_100073f0 0x100073f0: \m becomes the unit name (+0x102), \p the player pilot's name (FUN_1004a254), \q the
        /// inserted text, \r nothing; every other escape is kept with its character. The text ends at its first zero.
        /// </summary>
        public static byte[] ExpandNames(byte[] orders, Career career, string insert = null)
        {
            if (orders == null) return null;
            var result = new List<byte>(orders.Length);
            string playerName = "";
            foreach (var pilot in career.UnitPilots)
                if (pilot.Player) { playerName = pilot.Name; break; }
            int n = 0;
            while (n < orders.Length && orders[n] != 0) n++;
            for (int i = 0; i < n; i++)
            {
                if (orders[i] == '\\')
                {
                    byte c = i + 1 < n ? orders[i + 1] : (byte)0;
                    if (c == 'm' || c == 'M') result.AddRange(Encoding.ASCII.GetBytes(career.UnitName));
                    else if (c == 'p' || c == 'P') result.AddRange(Encoding.ASCII.GetBytes(playerName));
                    else if (c == 'q' || c == 'Q') result.AddRange(Encoding.ASCII.GetBytes(insert ?? ""));
                    else if (c != 'r' && c != 'R') { result.Add(orders[i]); result.Add(c); }
                    i++;
                    continue;
                }
                result.Add(orders[i]);
            }
            while (result.Count > 0 && result[result.Count - 1] == 0) result.RemoveAt(result.Count - 1);
            return result.ToArray();
        }

        /// <summary>
        /// FUN_100076eb 0x100076eb / FUN_10007609 0x10007609: the ORDR text of a briefing or debriefing BWD (its last byte
        /// forced to zero), expanded by FUN_100073f0 with <paramref name="insert"/> for \q, laid out page after page
        /// (FUN_10075e50 + FUN_100765d9) until the text is used up. RMG names ("RAN...") read the BWD the random mission
        /// generator left beside the game (FUN_10007d89 with -2).
        /// </summary>
        public static List<ShellTextPage> BuildPages(ShellData data, string resource, Career career, string insert,
            ShellFont font, ShellFont large, int x, int y, int width, int height)
        {
            var pages = new List<ShellTextPage>();
            byte[] bwd = resource.StartsWith("RAN", StringComparison.Ordinal) ? data.ReadLoose(resource) : data.ReadBwd(resource);
            if (bwd == null) return pages;
            foreach (var (tag, body, length) in BwdChunks.Enumerate(bwd))
                if (tag == "ORDR")
                {
                    var raw = new byte[length];
                    Buffer.BlockCopy(bwd, body, raw, 0, length);
                    if (length > 0) raw[length - 1] = 0;
                    string text = Latin1(ExpandNames(raw, career, insert));
                    int at = 0;
                    do
                    {
                        var page = new ShellTextPage(font, large, x, y, width, height);
                        at = page.Layout(text, at);
                        pages.Add(page);
                    } while (at >= 0);
                    break;
                }
            return pages;
        }

        public static string Latin1(byte[] b)
        {
            var c = new char[b.Length];
            for (int i = 0; i < b.Length; i++) c[i] = (char)b[i];
            return new string(c);
        }

        /// <summary>Neutral display layout still used by the news, contracts and finance screens until they move to
        /// <see cref="ShellTextPage"/> (FUN_100765d9). A neutral display layout for ORDR. It keeps explicit
        /// newlines, centering and pages, and wraps text to the original 301-pixel box. The original
        /// text-page object's word, tab and height calculations still require a direct screen comparison.</summary>
        public static List<List<Line>> Layout(byte[] orders, ShellFont font, int width = 0x12d, int height = 0x172)
        {
            var pages = new List<List<Line>> { new List<Line>() };
            if (orders == null) return pages;
            var line = new StringBuilder();
            var word = new StringBuilder();
            bool centered = false;
            int maxLines = height / (font.Height + 2);
            void NewPage()
            {
                pages.Add(new List<Line>());
                centered = false;
            }
            void NewLine()
            {
                pages[pages.Count - 1].Add(new Line(line.ToString(), centered));
                line.Clear();
                centered = false;
                if (pages[pages.Count - 1].Count >= maxLines) NewPage();
            }
            void Word()
            {
                if (word.Length == 0) return;
                string part = word.ToString();
                if (line.Length > 0 && font.Width(line + part) > width) NewLine();
                line.Append(part);
                word.Clear();
            }
            for (int i = 0; i < orders.Length; i++)
            {
                byte b = orders[i];
                if (b == '\\' && i + 1 < orders.Length)
                {
                    char cmd = char.ToLowerInvariant((char)orders[++i]);
                    Word();
                    if (cmd == 'n') { NewLine(); continue; }
                    if (cmd == 's') { if (line.Length > 0) NewLine(); NewPage(); continue; }
                    if (cmd == 'c') { centered = true; continue; }
                    if (cmd == 't')
                    {
                        int spaceWidth = font.Width(" ");
                        if (spaceWidth <= 0) spaceWidth = 4;
                        int next = (font.Width(line.ToString()) / 36 + 1) * 36;
                        while (font.Width(line.ToString()) < next) line.Append(' ');
                        continue;
                    }
                    word.Append(cmd);
                    continue;
                }
                if (b == '\r' || b == '\n') continue;
                if (b == ' ')
                {
                    word.Append(' ');
                    Word();
                }
                else word.Append((char)(b & 0x7f));
            }
            Word();
            if (line.Length > 0) NewLine();
            return pages;
        }
    }
}
