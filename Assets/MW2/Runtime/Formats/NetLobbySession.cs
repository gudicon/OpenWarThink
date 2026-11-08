using System;
using System.Collections.Generic;
using System.Text;

namespace MW2.Formats
{
    /// <summary>
    /// The 'Mech list of netshell.dll: the PRJ MTAB resource "MECH" (FUN_1001eae0, u32 count then 0x2e-byte records: +3 code, +7 model,
    /// +0x10 name, +0x29 tons, +0x2d technology) and the standard variants %3.3s%02dSTD (FUN_100105e0).
    /// </summary>
    public sealed class NetMechTable
    {
        public struct Chassis { public string Code, Model, Name; public int Tons, Tech; public List<int> Variants; }
        public readonly List<Chassis> Items = new List<Chassis>();

        public NetMechTable(PrjArchive prj)
        {
            var d = prj.Read("MTAB", "MECH");
            int count = BitConverter.ToInt32(d, 0);
            for (int i = 0; i < count; i++)
            {
                int at = 4 + i * 0x2e;
                var c = new Chassis
                {
                    Code = Text(d, at + 3, 3), Model = Text(d, at + 7, 9), Name = Text(d, at + 0x10, 0x19),
                    Tons = d[at + 0x29], Tech = d[at + 0x2d], Variants = new List<int>()
                };
                for (int v = 0; v < 100; v++)
                    if (prj.TryFind("MEK", Mek(c.Code, v), out _)) c.Variants.Add(v);
                Items.Add(c);
            }
        }

        public static string Mek(string code, int variant) => $"{code}{variant:00}STD";

        public int Find(string model)
        {
            for (int i = 0; i < Items.Count; i++)
                if (string.Equals(Items[i].Model, model, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        static string Text(byte[] b, int at, int max)
        {
            int n = 0;
            while (n < max && b[at + n] != 0) n++;
            return Encoding.ASCII.GetString(b, at, n);
        }
    }

    /// <summary>
    /// One machine's view of a MercNet room: FUN_1000cd00 (message dispatch), FUN_10016c10 on the host, FUN_100164e0 (the client's check of
    /// the status against its own choice, the launch handshake) and FUN_100162a0 (launch). Not carried over: custom 'Mech transfer MQ/MI
    /// (standard variants only), team and clan affiliations with their logos, and /kick and /ping.
    /// </summary>
    public sealed class NetLobbySession
    {
        public readonly NetTransport Net;
        public readonly NetRoom Room = new NetRoom();
        public readonly List<string> ChatLines = new List<string>();
        public bool Launch { get; private set; }
        public bool Lost { get; private set; }
        public string LostReason { get; private set; }
        public ushort WantMech;
        bool _hostOpened;
        float _lastStatus;

        public NetLobbySession(NetTransport net) { Net = net; }

        /// <summary>The host's room starts with the mission and options of its options screen, and holds its own player.</summary>
        public void OpenHost(string mission, byte options, bool team, byte temperature = 0, byte gravity = 0, byte timeOfDay = 1)
        {
            Room.Mission = mission;
            Room.Options = options;
            Room.RoomFlags = (byte)(team ? 2 : 0);
            Room.Temperature = temperature;
            Room.Gravity = gravity;
            Room.TimeOfDay = timeOfDay;
            Room.Players.Clear();
            Room.Players.Add(new NetRoom.Player { Id = Net.MyId, Mech = WantMech });
            _hostOpened = true;
        }

        /// <summary>FUN_10016ec0: to the host, or straight to FUN_10016c10 on the host itself. Reliable, as dpSend flag 1.</summary>
        public void Command(int command, byte keep, params byte[] arguments)
        {
            var message = NetRoom.Command(command, keep, arguments);
            if (Net.IsHost) { if (Room.ApplyCommand(Net.MyId, message, 2)) BroadcastStatus(); }
            else if (Net.HostId != NetTransport.NoId) Net.Send(Net.HostId, message, message.Length, true);
        }

        public void SetReady(bool ready) => Command(1, 0, (byte)(ready ? 1 : 0));
        public void SetTeam(int team) => Command(0, 0, (byte)team);
        public void SetMission(string mission) => Command(5, 0, Encoding.ASCII.GetBytes(mission.PadRight(4, '\0').Substring(0, 4)));
        public void SetOptions(byte bits, byte mask) => Command(6, 0, bits, mask);

        /// <summary>Case 4 of FUN_10016c10: chassis and variant. Changing the 'Mech clears the player's ready flag.</summary>
        public void SetMech(int chassis, int variant)
        {
            WantMech = (ushort)((chassis << 7) | (variant & 0x7f));
            Command(4, 0, 0, 0, 0, 0, (byte)WantMech, (byte)(WantMech >> 8));
        }

        /// <summary>"%s: %s" to everyone (tag CH, 0x4843; FUN_10016160 prints it).</summary>
        public void Chat(string text)
        {
            var body = Encoding.ASCII.GetBytes(text);
            var message = new byte[body.Length + 3];
            message[0] = 0x43; message[1] = 0x48;
            Buffer.BlockCopy(body, 0, message, 2, body.Length);
            Net.Send(0, message, message.Length, true);
            ChatLines.Add($"{Net.MyName}: {text}");
        }

        void BroadcastStatus()
        {
            var body = Room.Encode();
            var message = new byte[body.Length + 2];
            message[0] = 0x53; message[1] = 0x54;
            Buffer.BlockCopy(body, 0, message, 2, body.Length);
            Net.Send(0, message, message.Length, true);
        }

        /// <summary>Call every frame.</summary>
        public void Poll(float time)
        {
            Net.Poll(time);
            while (Net.Receive(out ushort from, out byte[] d))
            {
                if (d.Length < 2) continue;
                ushort tag = BitConverter.ToUInt16(d, 0);
                switch (tag)
                {
                    case NetLobbyTags.AddPlayer:
                        if (Net.IsHost && _hostOpened)
                        {
                            ushort id = BitConverter.ToUInt16(d, 2);
                            if (Room.Find(id) < 0) Room.Players.Add(new NetRoom.Player { Id = id, Mech = 0x1f80 });
                            BroadcastStatus();
                        }
                        break;
                    case NetLobbyTags.DeletePlayer:
                    {
                        ushort id = BitConverter.ToUInt16(d, 2);
                        int index = Room.Find(id);
                        if (index >= 0) Room.Players.RemoveAt(index);
                        if (!Net.IsHost && Net.NumPlayers <= 1) { Lost = true; LostReason = "Session lost."; }
                        if (Net.IsHost) { _hostOpened = true; BroadcastStatus(); }
                        break;
                    }
                    case NetLobbyTags.Status:
                        if (d.Length >= NetRoom.Size + 2 && !Net.IsHost) Apply(NetRoom.Decode(d, 2));
                        break;
                    case NetLobbyTags.Command:
                        if (Net.IsHost && d.Length >= 0xd && Room.ApplyCommand(from, d, 2)) BroadcastStatus();
                        break;
                    case NetLobbyTags.Chat:
                        ChatLines.Add($"{Net.PlayerName(from)}: {Encoding.ASCII.GetString(d, 2, d.Length - 2).TrimEnd('\0')}");
                        break;
                }
            }
            if (Net.IsHost && _hostOpened && time - _lastStatus > 1f)
            {
                _lastStatus = time;
                BroadcastStatus();
            }
            CheckLaunch();
        }

        void Apply(NetRoom incoming)
        {
            Room.RoomFlags = incoming.RoomFlags;
            Room.Mission = incoming.Mission;
            Room.Options = incoming.Options; Room.Tonnage = incoming.Tonnage; Room.Temperature = incoming.Temperature;
            Room.Gravity = incoming.Gravity; Room.TimeOfDay = incoming.TimeOfDay; Room.LogoA = incoming.LogoA; Room.LogoB = incoming.LogoB;
            Room.Players.Clear();
            Room.Players.AddRange(incoming.Players);
            // FUN_100164e0: the host's table disagrees with this player's choice: ask again; the 0x1f80 marker means no 'Mech yet.
            int me = Room.Find(Net.MyId);
            if (me >= 0 && Room.Players[me].Mech != WantMech) Command(4, 0, 0, 0, 0, 0, (byte)WantMech, (byte)(WantMech >> 8));
        }

        /// <summary>FUN_100164e0 / FUN_100162a0: everyone ready -> command 2 once; everyone launching -> launch.</summary>
        void CheckLaunch()
        {
            int me = Room.Find(Net.MyId);
            if (me < 0 || Launch || Room.Players.Count == 0) return;
            if (Room.AllReady && (Room.Players[me].Flags & NetRoom.FlagLaunching) == 0) Command(2, 0);
            if (Room.AllReady && Room.AllLaunching)
            {
                Net.AcceptNewPlayers = false;     // dpEnableNewPlayers(0), FUN_1001b7b0
                Room.SortForLaunch();
                Launch = true;
            }
        }

        /// <summary>FUN_1001b7b0: the team files of this machine, from the sorted room.</summary>
        public List<NetRoster.Unit> Units(NetMechTable table, PrjArchive prj)
        {
            var units = new List<NetRoster.Unit>();
            foreach (var p in Room.Players)
            {
                var c = table.Items[p.Chassis];
                string mek = NetMechTable.Mek(c.Code, p.Variant);
                prj.TryFind("MEK", mek, out var mekEntry);
                prj.TryFind("BWD", c.Model, out var modelEntry);
                units.Add(new NetRoster.Unit
                {
                    Model = c.Model, Mek = mek, Pilot = Net.PlayerName(p.Id) ?? "", MekId = mekEntry.Id, ModelId = modelEntry.Id,
                    Local = p.Id == Net.MyId, Team = p.Team
                });
            }
            return units;
        }
    }
}
