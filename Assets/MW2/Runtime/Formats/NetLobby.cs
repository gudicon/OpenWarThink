using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace MW2.Formats
{
    /// <summary>
    /// The room status of netshell.dll (DAT_10044620, 0x3f bytes, sent as TS by FUN_10017000): +2 flags (2 = team game),
    /// +3 mission[4], +7 option bits (FUN_1001c490), +8 max tonnage class, +9 temperature class, +0xa gravity, +0xb time of day,
    /// +0xc / +0xd team logos, +0xe players, +0xf six bytes each: u16 id, u16 flags, u16 mech.
    /// Player flags: 0x3f logo, 0x80 ready, 0x100 launching, 0x200 team, 0x400 custom mech. Mech: 0x7f variant, 0x1f80 chassis.
    /// </summary>
    public sealed class NetRoom
    {
        public const int Size = 0x3f, MaxPlayers = 8;
        public const int FlagReady = 0x80, FlagLaunching = 0x100, FlagTeam = 0x200, FlagCustom = 0x400;

        public struct Player
        {
            public ushort Id, Flags, Mech;
            public int Team => (Flags & FlagTeam) >> 9;
            public int Variant => Mech & 0x7f;
            public int Chassis => (Mech & 0x1f80) >> 7;
        }

        public byte RoomFlags;
        public string Mission = "";
        public byte Options, Tonnage, Temperature, Gravity, TimeOfDay = 1, LogoA, LogoB;
        public readonly List<Player> Players = new List<Player>();

        public bool TeamGame => (RoomFlags & 2) != 0;

        public byte[] Encode()
        {
            var b = new byte[Size];
            b[2] = RoomFlags;
            if (!string.IsNullOrEmpty(Mission)) Encoding.ASCII.GetBytes(Mission, 0, Math.Min(4, Mission.Length), b, 3);
            b[7] = Options; b[8] = Tonnage; b[9] = Temperature; b[0xa] = Gravity; b[0xb] = TimeOfDay; b[0xc] = LogoA; b[0xd] = LogoB;
            b[0xe] = (byte)Players.Count;
            for (int i = 0; i < Players.Count && i < MaxPlayers; i++)
            {
                int at = 0xf + i * 6;
                Put(b, at, Players[i].Id); Put(b, at + 2, Players[i].Flags); Put(b, at + 4, Players[i].Mech);
            }
            return b;
        }

        public static NetRoom Decode(byte[] b, int offset = 0)
        {
            var r = new NetRoom
            {
                RoomFlags = b[offset + 2], Mission = Encoding.ASCII.GetString(b, offset + 3, 4).TrimEnd('\0'), Options = b[offset + 7], Tonnage = b[offset + 8],
                Temperature = b[offset + 9], Gravity = b[offset + 0xa], TimeOfDay = b[offset + 0xb], LogoA = b[offset + 0xc], LogoB = b[offset + 0xd]
            };
            int count = Math.Min((int)b[offset + 0xe], MaxPlayers);
            for (int i = 0; i < count; i++)
            {
                int at = offset + 0xf + i * 6;
                r.Players.Add(new Player { Id = BitConverter.ToUInt16(b, at), Flags = BitConverter.ToUInt16(b, at + 2), Mech = BitConverter.ToUInt16(b, at + 4) });
            }
            return r;
        }

        public int Find(ushort id)
        {
            for (int i = 0; i < Players.Count; i++) if (Players[i].Id == id) return i;
            return -1;
        }

        /// <summary>
        /// FUN_1001c490: mw2dif.cfg bytes of the room: option bits 2 -> +0 unlimited ammunition, 8 -> +2 area damage, 0x10 -> +3 collision damage, 4 -> +4 heat,
        /// 1 -> +8 regeneration, 0x20 -> +9 radar; +0xa team game; +0xb time of day; +0xf gravity &lt;&lt; 12; +0x13 temperature -50 / 10 / 70.
        /// </summary>
        public void ApplyTo(DifficultyConfig config)
        {
            var b = config.Bytes;
            b[0] = (byte)(Options >> 1 & 1);
            b[8] = (byte)(Options & 1);
            b[2] = (byte)(Options >> 3 & 1);
            b[9] = (byte)(Options >> 5 & 1);
            b[3] = (byte)(Options >> 4 & 1);
            b[4] = (byte)(Options >> 2 & 1);
            b[10] = (byte)(RoomFlags >> 1 & 1);
        }

        /// <summary>World_LoadChunks 0x10070e92 with DAT_100a6c3c: PLNT values give way to the room's when set.</summary>
        public void ApplyTo(Mission mission)
        {
            // +0xf = class << 12 is 16.16 gravity in units of the PLNT value.
            if (Gravity != 0) mission.Gravity = 0.0296f * (Gravity << 12) / 65536f;
            if (Temperature == 1) mission.Temperature = -50;
            else if (Temperature == 2) mission.Temperature = 10;
            else if (Temperature == 3) mission.Temperature = 70;
            if (TimeOfDay <= 3) mission.StartPeriod = TimeOfDay;
        }

        /// <summary>FUN_1002d980: every player is ready.</summary>
        public bool AllReady { get { foreach (var p in Players) if ((p.Flags & FlagReady) == 0) return false; return true; } }
        /// <summary>FUN_1002d940: every player has reported that he is launching.</summary>
        public bool AllLaunching { get { foreach (var p in Players) if ((p.Flags & FlagLaunching) == 0) return false; return true; } }

        /// <summary>FUN_1002da00 with FUN_1002d9c0: by team in a team game, then by id. The sorted order is the unit order in the mission.</summary>
        public void SortForLaunch()
        {
            bool team = TeamGame;
            Players.Sort((x, y) =>
            {
                int d = x.Team - y.Team;
                return d != 0 && team ? d : x.Id - y.Id;
            });
        }

        void ClearReady()
        {
            for (int i = 0; i < Players.Count; i++)
            {
                var p = Players[i];
                p.Flags = (ushort)(p.Flags & 0xff7f);
                Players[i] = p;
            }
        }

        int TeamCount(int team)
        {
            int n = 0;
            foreach (var p in Players) if (p.Team == team) n++;
            return n;
        }

        /// <summary>
        /// FUN_10016c10, the host's DS command (tag 0x4453): body +0 u32 command, +4 keep-ready flag, +5.. arguments. Returns true when
        /// the room changed. Commands: 0 team (the last member of a team stays while there are two or more players), 1 ready, 2 launching,
        /// 4 mech, 5 mission, 6 options (bits set from +5 under mask +6), 8 logo. Command 3 counts launched clients (handled by the host loop).
        /// </summary>
        public bool ApplyCommand(ushort from, byte[] body, int at)
        {
            int index = Find(from);
            if (index < 0) return false;
            int command = BitConverter.ToInt32(body, at);
            byte keep = body[at + 4];
            var p = Players[index];
            switch (command)
            {
                case 0:
                    if (TeamCount(p.Team) == 1 && Players.Count >= 2) return false;
                    p.Flags = (ushort)((p.Flags & 0xfdff) | ((body[at + 5] & 1) << 9));
                    Players[index] = p;
                    if ((keep & 1) == 0) ClearReady();
                    return true;
                case 1:
                    p.Flags = (ushort)((p.Flags & 0xff7f) | ((body[at + 5] & 1) << 7));
                    break;
                case 2:
                    p.Flags |= FlagLaunching;
                    break;
                case 3:
                    return false;
                case 4:
                {
                    ushort mech = BitConverter.ToUInt16(body, at + 9), flags = BitConverter.ToUInt16(body, at + 7);
                    p.Mech = (ushort)((p.Mech & 0xe000) | (mech & 0x1fff));
                    p.Flags = (ushort)((p.Flags & 0xfbff) | (flags & 0x400));
                    if ((keep & 1) == 0) p.Flags = (ushort)(p.Flags & 0xff7f);
                    break;
                }
                case 5:
                    Mission = Encoding.ASCII.GetString(body, at + 5, 4).TrimEnd('\0');
                    if ((keep & 1) == 0) ClearReady();
                    return true;
                case 6:
                    Options = (byte)((~body[at + 6] & Options) | body[at + 5]);
                    if ((keep & 1) == 0) ClearReady();
                    return true;
                case 8:
                    p.Flags = (ushort)((p.Flags & 0xffc0) | (body[at + 5] & 0x3f));
                    break;
                default:
                    return false;
            }
            Players[index] = p;
            return true;
        }

        /// <summary>FUN_10016ec0: the DS message, 0xd bytes: tag, u32 command, keep byte, six argument bytes.</summary>
        public static byte[] Command(int command, byte keep, params byte[] arguments)
        {
            var b = new byte[0xd];
            b[0] = 0x53; b[1] = 0x44;
            Buffer.BlockCopy(BitConverter.GetBytes(command), 0, b, 2, 4);
            b[6] = keep;
            if (arguments != null) Buffer.BlockCopy(arguments, 0, b, 7, Math.Min(arguments.Length, 6));
            return b;
        }

        static void Put(byte[] b, int at, ushort v) { b[at] = (byte)v; b[at + 1] = (byte)(v >> 8); }
    }

    /// <summary>Lobby message tags of netshell.dll, dispatched by FUN_1000cd00.</summary>
    public static class NetLobbyTags
    {
        public const ushort AddPlayer = 0x3164, DeletePlayer = 0x3264, SessionLost = 0x3964;
        public const ushort Command = 0x4453, Chat = 0x4843, MechInfo = 0x494d, Go = 0x4f47, Ping = 0x5041, MechQuery = 0x514d, Status = 0x5453;
    }

    /// <summary>
    /// A MISSIONS\*.MWM file: an uncompressed tar archive (FUN_1001fb20; "ustar" checked by FUN_1001f940) with the mission's BWDs and
    /// texts. FUN_1001c850 lists one entry for each archive holding a "????scn1..." name: *name.txt up to "&amp;X", limits *lim0/1/2.txt.
    /// </summary>
    public sealed class NetMission
    {
        public readonly Dictionary<string, byte[]> Files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        public string Prefix, Name;
        public readonly Limits[] Modes = new Limits[3];

        /// <summary>FUN_1001c600: side (0 clan, 1 inner sphere, 3 any), players (MAXPLAYERS), team flag.</summary>
        public struct Limits { public int Side, MaxPlayers, Team; }

        public static Limits ParseLimits(string text)
        {
            var l = new Limits { Side = 3, MaxPlayers = 1, Team = 0 };
            foreach (var raw in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string line = raw.Trim();
                if (line.Equals("INNER_SPHERE", StringComparison.OrdinalIgnoreCase) || line.Equals("IS", StringComparison.OrdinalIgnoreCase)) l.Side = 1;
                else if (line.Equals("CLAN", StringComparison.OrdinalIgnoreCase)) l.Side = 0;
                else if (line.Equals("TEAM", StringComparison.OrdinalIgnoreCase)) l.Team = 1;
                else if (line.Equals("FREE", StringComparison.OrdinalIgnoreCase)) l.Team = 0;
                else if (line.StartsWith("MAXPLAYERS", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 2 && int.TryParse(parts[1], out int n)) l.MaxPlayers = n;
                }
            }
            return l;
        }

        public static NetMission Load(string path) => Parse(File.ReadAllBytes(path));

        public static NetMission Parse(byte[] tar)
        {
            var m = new NetMission();
            for (int at = 0; at + 512 <= tar.Length; )
            {
                if (tar[at] == 0) break;
                string name = Text(tar, at, 100);
                int size = Convert.ToInt32(Text(tar, at + 124, 12).Trim(' ', '\0'), 8);
                var data = new byte[size];
                Buffer.BlockCopy(tar, at + 512, data, 0, size);
                m.Files[name] = data;
                at += 512 + (size + 511) / 512 * 512;
            }
            foreach (var name in m.Files.Keys)
                if (name.Length > 7 && string.Compare(name, 4, "scn1", 0, 4, StringComparison.OrdinalIgnoreCase) == 0)
                    m.Prefix = name.Substring(0, 4);
            if (m.Prefix == null) return null;
            if (!m.Files.TryGetValue(m.Prefix + "name.txt", out var title)) return null;
            string nameText = Encoding.ASCII.GetString(title);
            int end = nameText.IndexOf("&X", StringComparison.Ordinal);
            m.Name = (end >= 0 ? nameText.Substring(0, end) : nameText).Trim();
            for (int i = 0; i < 3; i++)
                if (m.Files.TryGetValue(m.Prefix + "lim" + i + ".txt", out var limits)) m.Modes[i] = ParseLimits(Encoding.ASCII.GetString(limits));
                else if (i == 2) m.Modes[2] = m.Modes[1];
            return m;
        }

        /// <summary>The mission's own BWD by name, for Mission.LooseBwd.</summary>
        public byte[] Bwd(string name) => Files.TryGetValue(name + ".bwd", out var b) || Files.TryGetValue(name, out b) ? b : null;

        static string Text(byte[] b, int at, int max)
        {
            int n = 0;
            while (n < max && b[at + n] != 0) n++;
            return Encoding.ASCII.GetString(b, at, n);
        }
    }

    /// <summary>
    /// The team files of a network mission (FUN_1001b7b0): en00star.bwd .. en07star.bwd. Free for all: one file per player, team = sorted
    /// index. Team game: en00star and en01star for teams 0 and 1; the first member of each is leader. The remaining files are empty BWDs.
    /// The file body comes from FUN_1000813a / FUN_10001150 / FUN_10001250, the same layout as the shell's userstar.
    /// </summary>
    public static class NetRoster
    {
        public struct Unit { public string Model, Mek, Pilot; public int MekId, ModelId; public bool Local; public int Team; public bool Leader; }

        /// <summary>FUN_1001c0c0: one GPS chunk, 0x64 bytes. +8 MEK id (0xfffe for a custom MEK), +0xa model id, +0xc team, +0xd leader,
        /// +0xe 0 for this machine's player and 1 for the others, +0x20 6, +0x22 (options &amp; 0x20) &lt;&lt; 5, +0x24 model, +0x2d MEK, +0x36 pilot.</summary>
        public static byte[] Gps(Unit u, int options)
        {
            var c = new byte[0x64];
            Encoding.ASCII.GetBytes("GPS\0", 0, 4, c, 0);
            Buffer.BlockCopy(BitConverter.GetBytes(0x64), 0, c, 4, 4);
            Buffer.BlockCopy(BitConverter.GetBytes((ushort)u.MekId), 0, c, 8, 2);
            Buffer.BlockCopy(BitConverter.GetBytes((ushort)u.ModelId), 0, c, 0xa, 2);
            c[0xc] = (byte)u.Team;
            c[0xd] = (byte)(u.Leader ? 1 : 0);
            c[0xe] = (byte)(u.Local ? 0 : 1);
            c[0x20] = 6;
            Buffer.BlockCopy(BitConverter.GetBytes((ushort)((options & 0x20) << 5)), 0, c, 0x22, 2);
            Ascii(c, 0x24, u.Model, 8);
            Ascii(c, 0x2d, u.Mek, 8);
            Ascii(c, 0x36, u.Pilot, 0x15);
            return c;
        }

        /// <summary>Files for a sorted room; unit order is the player index order FirstNetwork 0x10020376 uses.</summary>
        public static Dictionary<string, byte[]> Files(IList<Unit> units, bool teamGame, int options)
        {
            var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            int used = 0;
            if (teamGame)
                for (int team = 0; team < 2; team++)
                {
                    var chunks = new List<byte[]>();
                    bool first = true;
                    foreach (var unit in units)
                    {
                        if (unit.Team != team) continue;
                        var u = unit;
                        u.Leader = first;
                        first = false;
                        chunks.Add(Gps(u, options));
                    }
                    files[$"en{used++:00}star"] = File(chunks);
                }
            else
                foreach (var unit in units)
                {
                    var u = unit;
                    u.Team = used;
                    u.Leader = true;
                    files[$"en{used++:00}star"] = File(new List<byte[]> { Gps(u, options) });
                }
            for (; used < 8; used++) files[$"en{used:00}star"] = File(new List<byte[]>());
            return files;
        }

        static byte[] File(List<byte[]> chunks)
        {
            int size = 12 + 12 + 0x1c, largest = 0x1c;
            foreach (var c in chunks) { size += c.Length; largest = Math.Max(largest, c.Length); }
            var d = new byte[size];
            Encoding.ASCII.GetBytes("BWD\0", 0, 4, d, 0);
            Buffer.BlockCopy(BitConverter.GetBytes(size), 0, d, 4, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(largest), 0, d, 8, 4);
            Encoding.ASCII.GetBytes("REV\0", 0, 4, d, 12);
            Buffer.BlockCopy(BitConverter.GetBytes(12), 0, d, 16, 4);
            Encoding.ASCII.GetBytes("1.22", 0, 4, d, 20);
            Encoding.ASCII.GetBytes("DTBL", 0, 4, d, 24);
            Buffer.BlockCopy(BitConverter.GetBytes(0x1c), 0, d, 28, 4);
            int o = 24 + 0x1c;
            foreach (var c in chunks) { Buffer.BlockCopy(c, 0, d, o, c.Length); o += c.Length; }
            return d;
        }

        static void Ascii(byte[] d, int o, string s, int max)
        {
            var b = Encoding.ASCII.GetBytes(s ?? "");
            Buffer.BlockCopy(b, 0, d, o, Math.Min(b.Length, max));
        }
    }
}
