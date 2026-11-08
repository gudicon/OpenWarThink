using System;
using System.Collections.Generic;
using System.Text;

namespace MW2.Shell
{
    /// <summary>MSVCRT rand() / srand() as the shell uses them: seed = seed × 214013 + 2531011, result bits 16..30.</summary>
    public sealed class ShellRandom
    {
        public uint State = 1;
        public int Rand()
        {
            State = State * 214013u + 2531011u;
            return (int)((State >> 16) & 0x7fff);
        }
        public void Srand(uint seed) => State = seed;
    }

    /// <summary>
    /// The parameter block of the random mission generator (0xf3 bytes; FUN_1006adab 0x1006adab fills it, mw2rmg.cfg
    /// holds one): +0 mission number (the n of ran%d), +2 success pay, +6 failure pay, +0xa date (-1: instant action,
    /// a random year 3044..3054), +0xe planet name (empty: random), +0x1e mission type (0 random 1..3: 1 Search and
    /// Destroy, 2 Defend, 3 Recon), +0x20 seed (logged only), +0x24 the first enemy star's difficulty row, +0x28 its
    /// MEKs (0x10 each), +0x58 its unit name, +0x9a its pilot, +0xe1 difficulty (0 → 2).
    /// </summary>
    public sealed class RandomMissionParams
    {
        public const int Size = 0xf3;
        public readonly byte[] Data = new byte[Size];

        public int Number { get => BitConverter.ToInt16(Data, 0); set => Put16(0, value); }
        public int SuccessPay { get => BitConverter.ToInt32(Data, 2); set => Put32(2, value); }
        public int FailPay { get => BitConverter.ToInt32(Data, 6); set => Put32(6, value); }
        public int Date { get => BitConverter.ToInt32(Data, 0xa); set => Put32(0xa, value); }
        public string Planet { get => Str(0xe, 0x10); set => SetStr(0xe, value); }
        public int Type { get => BitConverter.ToInt16(Data, 0x1e); set => Put16(0x1e, value); }
        public uint Seed { get => BitConverter.ToUInt32(Data, 0x20); set => Put32(0x20, (int)value); }
        public int Difficulty { get => Data[0xe1]; set => Data[0xe1] = (byte)value; }

        void Put16(int o, int v) { Data[o] = (byte)v; Data[o + 1] = (byte)(v >> 8); }
        public void Put32(int o, int v) { for (int k = 0; k < 4; k++) Data[o + k] = (byte)(v >> (8 * k)); }
        public string Str(int o, int max)
        {
            int e = o;
            while (e < o + max && e < Data.Length && Data[e] != 0) e++;
            return Encoding.ASCII.GetString(Data, o, e - o);
        }
        public void SetStr(int o, string s)
        {
            var b = Encoding.ASCII.GetBytes(s ?? "");
            Buffer.BlockCopy(b, 0, Data, o, b.Length);
            Data[o + b.Length] = 0;
        }
    }

    /// <summary>
    /// The random mission generator of MW2SHELL.DLL (FUN_1004f490 0x1004f490 and its helpers 0x1005061a..0x100511bb,
    /// the BWD writer of 0x10060d50..0x1006162d), run for the RAN steps of the campaign (FUN_1006adab) and instant action.
    /// From the mission template 0x100b5410 (49 areas: 26 buildings, 23 pieces of terrain, 0x17 bytes each: name, width
    /// +0xb, flag +0xf, depth +0x13) it makes thirty records mis0scn1..mis9scn3, picks one of the mission type with fewer
    /// than three objectives, picks its objective buildings and lays them out with the terrain in rows inside ±125000
    /// (25000 apart, never over the centre), and writes: ran%dwld1.bwd (planet, enemy stars, one block per area), the
    /// objective areas copied from RMG\&lt;name&gt;.bwd to mis?are%d.bwd with their randnav%d.bwd nav points, en%02dstar.bwd
    /// (one to three enemy 'Mechs a star by date and difficulty), ran%dscn1.bwd (copied from RMG\mis?scn?.bwd), the
    /// briefing ran%dbrf1.bwd, the debriefing pages ran%ddbff.bwd / ran%ddbfs.bwd and the contract rnd%dcam1.bwd. The
    /// files are kept in memory (the original writes them beside the game); rmglog.txt is <see cref="Log"/>. The
    /// MW2LOGO%d.CEL faction logos (FUN_100511bb) are not made: the mission engine of the port does not use them yet.
    /// rand() and srand() are the shell's (MSVCRT); every random number is FUN_100507b4: ftol(rand() / 32767.0 × (b + 1 −
    /// a) + a), which gives b + 1 when rand() returns 32767.
    /// </summary>
    public sealed class RandomMissionGenerator
    {
        readonly ShellTables _t;
        readonly Func<int> _rand;
        readonly Func<string, byte[]> _rmgFile;
        /// <summary>The files written, by their lower-case names.</summary>
        public readonly Dictionary<string, byte[]> Files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        public readonly StringBuilder Log = new StringBuilder();
        /// <summary>DAT_100fb1cc: the largest chunk written so far, the header's third dword; it is never reset.</summary>
        public static int LargestChunk;
        /// <summary>local_8c of FUN_1004f490: set for instant action (date -1), otherwise never initialised; the log's
        /// " Instant Action" depends on it.</summary>
        public bool InstantActionLabel;

        public RandomMissionGenerator(ShellTables tables, Func<int> rand, Func<string, byte[]> rmgFile)
        {
            _t = tables;
            _rand = rand;
            _rmgFile = rmgFile;
        }

        /// <summary>FUN_100507b4 0x100507b4.</summary>
        int Random(int a, int b) => (int)(_rand() / 32767.0 * (b + 1 - a) + a);

        /// <summary>The generator's tables (shell-rmg.json); each list ends with the entry rand() = 32767 reaches.</summary>
        ShellRmgTables R => _t.Rmg;
        RmgText X => _t.Rmg.Text;

        // ---- the mission records (DAT_100fb882, 0x532 bytes each) ----

        sealed class Area
        {
            public string Name;
            public float Width, Flag, Depth;
        }

        sealed class Record
        {
            public string Name;
            public int Stars, Type, Objectives, Count, Buildings;
            public Area[] Areas;
        }

        Record[] Records()
        {
            var list = new Record[0x1e];
            for (int i = 0; i < 0x1e; i++)
            {
                var r = new Record
                {
                    Name = $"mis{i / 3}scn{i % 3 + 1}",
                    Stars = i % 3 + 1,
                    Objectives = i % 3 + 1,
                    Type = i / 3 < 7 ? (i / 3 + 2) % 3 : (i / 3 + 1) % 3,
                    Count = R.TemplateCount,
                    Buildings = R.TemplateBuildings
                };
                // All the slots the 0x532-byte template 0x100b5410 has room for: a random index one past the end
                // (rand() = 32767) reads the next, empty, slot as the original does.
                r.Areas = new Area[R.TemplateAreas.Count];
                for (int k = 0; k < r.Areas.Length; k++)
                {
                    var a = R.TemplateAreas[k];
                    r.Areas[k] = new Area { Name = a.Name, Width = a.Width, Flag = a.Flag, Depth = a.Depth };
                }
                list[i] = r;
            }
            return list;
        }

        // ---- layout (FUN_1005061a 0x1005061a) ----

        /// <summary>A rectangle as the original keeps it: top, left, bottom, right.</summary>
        sealed class Rect4 { public int Top, Left, Bottom, Right; }

        static void Shift(Rect4 r, int dx, int dy)        // FUN_100507ff
        {
            r.Left += dx;
            r.Right += dx;
            r.Top += dy;
            r.Bottom += dy;
        }

        static bool Inside(Rect4 r, int x, int y) =>       // FUN_1005076a
            r.Left < x && x < r.Right && r.Bottom < y && y < r.Top;

        /// <summary>
        /// FUN_1005061a: the next place in the row (a new row 25000 above the highest one when the right edge passes
        /// the bounds), the area's centre out, the cursor 25000 past it; a place over the centre (0, 0) is skipped.
        /// False once the row starts above the bounds.
        /// </summary>
        static bool Place(Area a, Rect4 bounds, Rect4 c, out int x, out int z)
        {
            c.Right = (int)(c.Left + (double)a.Depth);
            if (c.Right > bounds.Right)
            {
                Shift(c, bounds.Left - c.Left, c.Top - c.Bottom + 25000);
                c.Top = c.Bottom;
            }
            var spot = new Rect4 { Top = (int)(c.Bottom + (double)a.Depth), Left = c.Left, Bottom = c.Bottom, Right = c.Right };
            c.Top = spot.Top < c.Top ? c.Top : spot.Top;
            x = (int)(spot.Left + a.Width / 2.0);
            z = (int)(spot.Bottom + a.Depth / 2.0);
            Shift(c, (int)(a.Width + 25000.0), 0);
            if (Inside(spot, 0, 0)) return Place(a, bounds, c, out x, out z);
            return c.Bottom < bounds.Top;
        }

        // ---- the BWD writer (bwdshit.cpp: FUN_10061368 begins, FUN_100613f2 writes) ----

        List<byte[]> _chunks;

        void Begin() => _chunks = new List<byte[]>();

        byte[] Chunk(string tag, int size)
        {
            var c = new byte[size];
            Encoding.ASCII.GetBytes(tag.PadRight(4, '\0'), 0, 4, c, 0);
            Put32(c, 4, size);
            if (size > LargestChunk) LargestChunk = size;
            _chunks.Add(c);
            return c;
        }

        /// <summary>FUN_10060d50 0x10060d50: INCL, id 0xffff (from MW2.PRJ) or 0xfffe (a file).</summary>
        void Incl(string name, int id)
        {
            var c = Chunk("INCL", (name.Length + 0xf) & ~3);
            Put16(c, 8, id);
            Ascii(c, 10, name, name.Length);
        }

        /// <summary>FUN_10060df4 0x10060df4: BLKX (scale, rotation × 0x10000, position).</summary>
        void Blkx(int sx, int sy, int sz, int rx, int ry, int rz, int x, int y, int z)
        {
            var c = Chunk("BLKX", 0x2c);
            int[] v = { sx, sy, sz, rx << 16, ry << 16, rz << 16, x, y, z };
            for (int k = 0; k < 9; k++) Put32(c, 8 + k * 4, v[k]);
        }

        /// <summary>FUN_10061189 0x10061189 / FUN_1006127b 0x1006127b: BLK and ENDB.</summary>
        void Blk(params int[] v)
        {
            var c = Chunk("BLK", 0x20);
            for (int k = 0; k < 6; k++) Put32(c, 8 + k * 4, v[k]);
        }

        void Endb() => Chunk("ENDB", 8);

        /// <summary>FUN_10061087 0x10061087: NAVP (position as floats, nine shorts, a name of up to 0x15).</summary>
        void Navp(string name)
        {
            var c = Chunk("NAVP", 0x50);
            Put16(c, 0x18, 1);
            Put16(c, 0x20, 100);
            Put16(c, 0x22, 0x100);
            Ascii(c, 0x24, name, 0x15);
        }

        /// <summary>FUN_10061514 0x10061514: ORDR text.</summary>
        void Ordr(string text)
        {
            var c = Chunk("ORDR", (text.Length + 0xd) & ~3);
            Ascii(c, 8, text, text.Length);
        }

        /// <summary>
        /// FUN_10061599 0x10061599 + FUN_1006162d 0x1006162d: CPGN whose size takes in the CTNT that follows (0xa0 bytes
        /// of its own: +0x14 the scene file, +0x90 the employer).
        /// </summary>
        void Contract(string scene, string employer, string text)
        {
            int ctnt = (text.Length + 0xd) & ~3;
            var c = new byte[0xa0 + ctnt];
            Encoding.ASCII.GetBytes("CPGN", 0, 4, c, 0);
            Put32(c, 4, (ctnt + 0xa3) & ~3);
            Ascii(c, 0x14, scene, scene.Length);
            Ascii(c, 0x90, employer, employer.Length);
            Encoding.ASCII.GetBytes("CTNT", 0, 4, c, 0xa0);
            Put32(c, 0xa4, ctnt);
            Ascii(c, 0xa8, text, text.Length);
            int size = (ctnt + 0xa3) & ~3;
            if (size > LargestChunk) LargestChunk = size;
            if (ctnt > LargestChunk) LargestChunk = ctnt;
            if (c.Length != size) Array.Resize(ref c, Math.Max(c.Length, size));
            _chunks.Add(c);
        }

        /// <summary>
        /// FUN_10060eb5 0x10060eb5: GPS (100 bytes): +8 0xffff, +0xa 0xffff, +0xc the star's team (DAT_100fb590), +0xd
        /// leader (the first GPS of the file), +0xe 2 (computer), +0x10 the difficulty row, +0x20 6, +0x22 0x1000,
        /// +0x24 model, +0x2d MEK, +0x36 unit name, +0x4c pilot.
        /// </summary>
        void Gps(int team, bool leader, string unit, string pilot, string model, string mek, short[] row)
        {
            var c = Chunk("GPS", 100);
            Put16(c, 8, 0xffff);
            Put16(c, 0xa, 0xffff);
            c[0xc] = (byte)team;
            c[0xd] = (byte)(leader ? 1 : 0);
            c[0xe] = 2;
            int a = row[0] < 1 || row[0] > 5 ? 5 : row[0];
            int e = row[4] < 1 || row[4] > 5 ? 5 : row[4];
            Put16(c, 0x10, a);
            Put16(c, 0x12, row[1]);
            Put16(c, 0x14, row[2]);
            Put16(c, 0x16, row[3]);
            Put16(c, 0x18, e);
            Put16(c, 0x20, 6);
            Put16(c, 0x22, 0x1000);
            Ascii(c, 0x24, model, 8);
            Ascii(c, 0x2d, mek, 8);
            Ascii(c, 0x36, unit, 0x15);
            Ascii(c, 0x4c, pilot, 0x15);
        }

        /// <summary>FUN_100613f2 0x100613f2 with FUN_10008250 0x10008250: header (BWD, size, largest chunk), REV "1.22",
        /// DTBL of zeros, then the chunks.</summary>
        void Write(string file)
        {
            int size = 12 + 12 + 0x1c;
            foreach (var c in _chunks) size += c.Length;
            if (LargestChunk < 0x1c) LargestChunk = 0x1c;
            var d = new byte[size];
            Encoding.ASCII.GetBytes("BWD\0", 0, 4, d, 0);
            Put32(d, 4, size);
            Put32(d, 8, LargestChunk);
            Encoding.ASCII.GetBytes("REV\0", 0, 4, d, 12);
            Put32(d, 16, 12);
            Encoding.ASCII.GetBytes("1.22", 0, 4, d, 20);
            Encoding.ASCII.GetBytes("DTBL", 0, 4, d, 24);
            Put32(d, 28, 0x1c);
            int o = 24 + 0x1c;
            foreach (var c in _chunks) { Buffer.BlockCopy(c, 0, d, o, c.Length); o += c.Length; }
            Files[file] = d;
            _chunks = null;
        }

        static void Put16(byte[] d, int o, int v) { d[o] = (byte)v; d[o + 1] = (byte)(v >> 8); }
        static void Put32(byte[] d, int o, int v) { for (int k = 0; k < 4; k++) d[o + k] = (byte)(v >> (8 * k)); }
        static void Ascii(byte[] d, int o, string s, int max)
        {
            var b = Encoding.ASCII.GetBytes(s ?? "");
            Buffer.BlockCopy(b, 0, d, o, Math.Min(b.Length, max));
        }

        /// <summary>FUN_10050876 0x10050876: a file of the game's RMG folder copied.</summary>
        int Copy(string from, string to)
        {
            byte[] d = _rmgFile(from);
            if (d == null) return 3;
            Files[to] = (byte[])d.Clone();
            return 0;
        }

        /// <summary>FUN_1006ac42 0x1006ac42: the message box for a failed run (null when it succeeded).</summary>
        public static string ErrorText(int result, ShellTables t)
        {
            switch (result)
            {
                case 1: return t.Rmg.Text.ErrorCreate;
                case 2: return t.Rmg.Text.ErrorParameter;
                case 3: return t.Rmg.Text.ErrorOpen;
                case 4: return t.Rmg.Text.ErrorMemory;
            }
            return null;
        }

        /// <summary>FUN_1005108f 0x1005108f: thousands separated by commas.</summary>
        public static string Money(int v)
        {
            string digits = v.ToString(System.Globalization.CultureInfo.InvariantCulture);
            bool negative = digits.StartsWith("-");
            if (negative) digits = digits.Substring(1);
            var sb = new StringBuilder();
            for (int i = 0; i < digits.Length; i++)
            {
                if (i > 0 && (digits.Length - i) % 3 == 0) sb.Append(',');
                sb.Append(digits[i]);
            }
            return negative ? "-" + sb : sb.ToString();
        }

        /// <summary>sprintf for the formats of the generator: %s, %d, %ld, %02d, %.Ns.</summary>
        public static string Printf(string f, params object[] a)
        {
            var sb = new StringBuilder();
            int n = 0;
            for (int i = 0; i < f.Length; i++)
            {
                if (f[i] != '%') { sb.Append(f[i]); continue; }
                i++;
                if (f[i] == '%') { sb.Append('%'); continue; }
                bool zero = false;
                int width = 0, prec = -1;
                if (f[i] == '0') { zero = true; i++; }
                while (char.IsDigit(f[i])) width = width * 10 + (f[i++] - '0');
                if (f[i] == '.') { i++; prec = 0; while (char.IsDigit(f[i])) prec = prec * 10 + (f[i++] - '0'); }
                if (f[i] == 'l') i++;
                object v = a[n++];
                string s = f[i] == 's' ? v as string ?? "" : Convert.ToInt64(v).ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (f[i] == 's' && prec >= 0 && s.Length > prec) s = s.Substring(0, prec);
                if (s.Length < width) s = zero ? s.PadLeft(width, '0') : s.PadLeft(width);
                sb.Append(s);
            }
            return sb.ToString();
        }

        // ---- the generator ----

        /// <summary>FUN_1004f490: 0 when every file was made; 1 (cannot create), 2 (bad parameter), 3 (cannot open a
        /// file) or 4 (out of memory) as FUN_1006ac42 reports them.</summary>
        public int Generate(RandomMissionParams p)
        {
            var records = Records();
            bool instant = false;
            if (p.Date == -1)
            {
                instant = true;
                p.Date = Random(0xbe4, 0xbee);
            }
            Log.Append(Printf(X.LogHeader, p.Date, instant || InstantActionLabel ? X.InstantActionLabel : "", p.Seed));
            Log.Append(Printf(X.LogPay, p.SuccessPay, p.FailPay));
            bool bad = p.Date < 0xbe4;
            if (bad) Log.Append(X.LogBadDate);
            if (!bad && p.Planet.Length > 0x10) { bad = true; Log.Append(X.LogPlanetTooLong); }
            if (!bad && (p.Type < 0 || p.Type > 3)) { bad = true; Log.Append(X.LogBadType); }
            if (!bad && p.Difficulty > 3) { bad = true; Log.Append(X.LogBadDifficulty); }
            int planet = 0x1e;
            if (p.Planet.Length != 0)
            {
                planet = 0;
                while (planet < 0xf && R.Planets[planet].Name != p.Planet) planet++;
            }
            if (!bad && planet == 0xf) { bad = true; Log.Append(Printf(X.LogBadPlanet, p.Planet)); }
            if (bad) return 2;
            Log.Append(Printf(X.LogTypeDifficulty, p.Type, p.Difficulty));
            if (p.Type == 0) p.Type = Random(1, 3);
            // A type of 4 (rand() = 32767) matches no record and the original loops for ever; kept at 3 here.
            if (p.Type > 3) p.Type = 3;
            if (p.Difficulty == 0) p.Difficulty = 2;
            Record rec;
            while (true)
            {
                int r = Random(0, 0x1d);
                // Record 30 lies past the table (rand() = 32767): taken as one that does not fit.
                if (r >= records.Length) continue;
                rec = records[r];
                if (rec.Objectives < 3 && rec.Type == p.Type - 1) break;
            }

            // The objective buildings, then the terrain in a random order.
            var used = new bool[rec.Areas.Length];
            var list = new List<Area>();
            do
            {
                int e = Random(0, rec.Buildings - 1);
                if (!used[e])
                {
                    rec.Areas[e].Flag = 1f;
                    list.Add(rec.Areas[e]);
                    used[e] = true;
                }
            } while (list.Count != rec.Objectives);
            do
            {
                int e = Random(rec.Buildings, rec.Count - 1);
                if (!used[e])
                {
                    list.Add(rec.Areas[e]);
                    used[e] = true;
                }
            } while (list.Count <= rec.Count - rec.Buildings);
            var bounds = new Rect4 { Top = 0x1e848, Left = -0x1e848, Bottom = -0x1e848, Right = 0x1e848 };
            Rect4 Cursor() => new Rect4 { Top = -0x1e848, Left = -0x1e848, Bottom = -0x1e848, Right = -0x1e848 };
            // Shuffled into the first places until every objective fits in the bounds.
            for (bool fits = false; !fits; )
            {
                for (int i = 0; i < rec.Objectives; i++)
                {
                    int j = Random(0, list.Count - 1);
                    // j = list.Count (rand() = 32767) swaps in an uninitialised stack slot in the original; skipped here.
                    if (i != j && j < list.Count) (list[i], list[j]) = (list[j], list[i]);
                }
                var c = Cursor();
                int placed = 0;
                fits = true;
                for (int i = 0; i < list.Count; i++)
                {
                    if (!Place(list[i], bounds, c, out _, out _))
                    {
                        if (placed < rec.Objectives) { fits = false; break; }
                    }
                    else if (list[i].Flag != 0f) placed++;
                }
            }

            string wld = Printf(X.WorldFile, p.Number);
            Log.Append(Printf(X.LogCreating, wld));
            Begin();
            string typeName = R.MissionTypes[p.Type];
            Factions(typeName, p, out int employer, out string plt, out int enemy);
            string enemyName = R.Factions[enemy].Name, employerName = R.Factions[employer].Name;
            Log.Append(Printf(X.LogFactions, employerName, typeName, enemyName));
            Incl(plt, 0xffff);
            Log.Append(Printf(X.LogPlanetFile, plt, p.Planet));
            for (int i = 0; i < rec.Stars; i++) Incl(Printf(X.EnemyStarInclude, i + 1), 0xfffe);
            var cur = Cursor();
            int n = 1;
            string stp = null, nav = null;
            for (int i = 0; i < list.Count; i++)
            {
                var a = list[i];
                if (a.Flag != 0f)
                {
                    string from = Printf(X.AreaSource, a.Name), to = Printf(X.AreaFile, rec.Name, n);
                    Log.Append(Printf(X.LogCopy, from, to));
                    int failed = Copy(from, to);
                    if (failed != 0) return failed;
                    a.Name = Printf(X.AreaName, to);
                    stp = Printf(X.StepName, rec.Name, n);
                    nav = Printf(X.NavName, n);
                    n++;
                }
                if (!Place(a, bounds, cur, out int x, out int z)) break;
                Blkx(1, 1, 1, 0, 0, 0, x, 0, z);
                Blk(0, 0, 0, 1, 1, 1);
                if (a.Flag == 0f) Incl(a.Name, 0xffff);
                else
                {
                    Incl(stp, 0xffff);
                    Incl(a.Name, 0xfffe);
                    Incl(nav, 0xfffe);
                }
                Endb();
            }
            Write(wld);
            n = 1;
            string[] navNames = { null, X.NavAlpha, X.NavBeta, X.NavGamma };
            foreach (var a in list)
            {
                if (a.Flag == 0f) continue;
                string file = Printf(X.NavFile, n);
                Log.Append(Printf(X.LogCreatingNav, file));
                Begin();
                Navp(Printf(X.NavLabel, n < navNames.Length ? navNames[n] : ""));
                Write(file);
                n++;
            }
            int mechs = EnemyStars(rec.Stars, p, R.Factions[enemy].Short, p.Type - 1);
            string scnFrom = Printf(X.SceneSource, rec.Name), scn = Printf(X.SceneFile, p.Number);
            Log.Append(Printf(X.LogCopyScene, scnFrom, scn));
            int copied = Copy(scnFrom, scn);
            if (copied != 0) return copied;
            string pay = Money(p.SuccessPay);
            Begin();
            Ordr(Printf(X.Briefing, employerName, typeName, enemyName, p.Planet, p.Planet, pay, mechs, typeName, typeName));
            Write(Printf(X.BriefingFile, p.Number));
            Begin();
            Ordr(Printf(X.DebriefingFailed));
            Write(Printf(X.DebriefingFailedFile, p.Number));
            Begin();
            Ordr(Printf(X.DebriefingSucceeded, pay));
            Write(Printf(X.DebriefingSucceededFile, p.Number));
            Begin();
            string strength = mechs < 4 ? X.StrengthLight : mechs < 6 ? X.StrengthMedium : X.StrengthHeavy;
            string contract = Printf(X.Contract, employerName, employerName, typeName, p.Planet, enemyName, strength,
                employerName, p.Planet, typeName, pay, mechs);
            Contract(scn, R.Factions[employer].Code, contract);
            Write(Printf(X.ContractFile, p.Number));
            return 0;
        }

        /// <summary>
        /// FUN_10050f32 0x10050f32: the planet (its owner and planet file; a random one with a random prefix and owner
        /// when the name is empty), the employer and the enemy among the first three factions (the owner is the
        /// employer in a Defend mission, the enemy otherwise), from 3050 on a Clan enemy (3..6).
        /// </summary>
        void Factions(string typeName, RandomMissionParams p, out int employer, out string plt, out int enemy)
        {
            int prefix, owner;
            if (p.Planet.Length == 0)
            {
                prefix = Random(0, 0x36);
                owner = Random(0, 2);
                p.Planet = R.Planets[Random(0, 0xe)].Name;
            }
            else
            {
                int k = 0;
                while (k < 0xf && R.Planets[k].Name != p.Planet) k++;
                owner = R.Planets[k].Owner;
                prefix = R.Planets[k].Prefix;
            }
            int other;
            do other = (short)Random(0, 2);
            while (other == (short)owner);
            if (typeName == R.MissionTypes[2])
            {
                employer = owner;
                enemy = other;
            }
            else
            {
                enemy = owner;
                employer = other;
            }
            if (p.Date > 0xbe9) enemy = Random(3, 6);
            plt = Printf(X.PlanetFile, R.PlanetPrefixes[prefix]);
        }

        /// <summary>
        /// FUN_10050e40 0x10050e40: a chassis entry (8 dwords: codes, model, name, tons...) of the light, medium, heavy
        /// or Clan table: before 3050 the class of the date ((date − 3044) / 2) three times in four, else a random
        /// lower one; from 3050 Clan.
        /// </summary>
        RmgChassis Chassis(int date, out int index)
        {
            int[] counts = R.ClassCount;
            int cls;
            if (date < 0xbea)
            {
                int dateClass = (short)((date - 0xbe4) / 2);
                cls = Random(0, 1000) < 0x2ee ? dateClass : (short)Random(0, dateClass);
            }
            else cls = 3;
            index = Random(0, counts[cls] * 10 - 1) / 10;
            // One past a list (rand() = 32767) is the next list's first chassis, as the lists lie back to back.
            return R.Chassis[R.ClassStart[cls] + index];
        }

        /// <summary>
        /// FUN_100509ff 0x100509ff: each enemy star en%02dstar.bwd holds one to three 'Mechs (Defend: one or two), each a
        /// chassis by date, a pilot of the table 0x100b5b24 not yet in the star, "&lt;faction&gt; &lt;chassis&gt;" and
        /// "&lt;code&gt;00std", with the difficulty row 0x100b5b88 (difficulty 1 → 6, 2 → 5, 3 → 1, rising a row per
        /// star, 1..8); the first star is noted in the parameters. Returns how many 'Mechs.
        /// </summary>
        int EnemyStars(int stars, RandomMissionParams p, string faction, int type)
        {
            int difficulty = p.Difficulty;
            int row = difficulty == 1 ? 6 : difficulty == 2 ? 5 : difficulty == 3 ? 1 : R.DifficultyBase - 2;
            int first = row;
            int mechs = 0;
            for (int star = 1; star <= stars; star++)
            {
                var pilots = new[] { -1, -1, -1, 0, 0 };
                if (row < 1) row = 1;
                if (row > 8) row = 8;
                Begin();
                int count = (type == 1 ? Random(0, 0x13) : Random(0, 0x1d)) / 10 + 1;
                mechs += count;
                bool leader = true;
                for (int k = 0; k < count; k++)
                {
                    var chassis = Chassis(p.Date, out int index);
                    int pick;
                    while (true)
                    {
                        pick = Random(0, 0x81) / 10;
                        int j = 0;
                        while (j < k && pilots[j] != pick) j++;
                        if (pilots[j] != pick) break;
                    }
                    pilots[k] = pick;
                    string unit = Printf(X.UnitName, faction, chassis.Name);
                    string pilot = R.Pilots[pick];
                    string mek = Printf(X.StockMek, chassis.Code);
                    string model = chassis.Model;
                    if (index < 0) continue;
                    Log.Append(Printf(X.LogGps, model, star));
                    var values = new short[7];
                    for (int v = 0; v < 7; v++) values[v] = (short)R.DifficultyRows[row].Values[v];
                    Gps(star, leader, unit, pilot, model, mek, values);
                    leader = false;
                    if (star == 1)
                    {
                        p.SetStr(0x28 + k * 0x10, mek);
                        p.SetStr(0x58, unit);
                        p.SetStr(0x9a, pilot);
                        p.Put32(0x24, first);
                    }
                }
                Write(Printf(X.EnemyStarFile, star));
                row++;
            }
            return mechs;
        }
    }
}
