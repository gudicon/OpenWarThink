using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace MW2.Formats
{
    /// <summary>
    /// Replacement for anet2.dll with the calls the game uses: dpSend (flag 1 = reliable, in order), dpReceive (from, buffer; "d1" / "d2" /
    /// "d9" system messages), dpNumPlayers, dpEnumPlayers, dpGetPlayerName, player ids. Adaptation: the wire format is this class's own UDP
    /// protocol, not the ActiveNet packets of winets2.dll, so a Player cannot join the original game and the ActiveNet servers, IPX,
    /// modem and serial links are not carried over. Peers form a full mesh after the host hands out ids.
    /// Datagram: 'M' '2' kind, u16 from, u16 to, u32 sequence (0 = unreliable), then the payload. All timing is the caller's Poll.
    /// </summary>
    public sealed class NetTransport : IDisposable
    {
        public const ushort NoId = 0xfa01;
        public const int DefaultPort = 21157;
        public const float PeerTimeout = 15f, ResendSeconds = 0.25f, HelloSeconds = 0.5f;
        const byte KindData = 0, KindAck = 1, KindHello = 2, KindWelcome = 3, KindPlayers = 4, KindBye = 5, KindRefuse = 6, KindEnum = 7, KindSession = 8, KindKeep = 9;

        public sealed class Session { public IPEndPoint Host; public string Name; public int Players, MaxPlayers; }

        sealed class Peer
        {
            public ushort Id;
            public string Name;
            public IPEndPoint End;
            public float LastHeard;
            public uint NextSend = 1, NextReceive = 1;
            public readonly Dictionary<uint, byte[]> Early = new Dictionary<uint, byte[]>();
            public readonly List<Pending> Unacked = new List<Pending>();
        }
        sealed class Pending { public uint Sequence; public byte[] Datagram; public float Sent; }

        readonly UdpClient _udp;
        readonly Dictionary<ushort, Peer> _peers = new Dictionary<ushort, Peer>();
        readonly Queue<(ushort from, byte[] data)> _inbox = new Queue<(ushort, byte[])>();
        readonly List<Session> _sessions = new List<Session>();
        ushort _nextId = 1;
        float _now, _lastHello, _lastKeep;
        IPEndPoint _hostEnd;
        bool _joining;

        public ushort MyId { get; private set; } = NoId;
        public ushort HostId { get; private set; } = NoId;
        public string MyName { get; }
        public bool IsHost => MyId != NoId && MyId == HostId;
        public string SessionName = "";
        public int MaxPlayers = 8;
        public string Password = "";
        public string RefuseReason { get; private set; }
        public int Port => ((IPEndPoint)_udp.Client.LocalEndPoint).Port;
        public bool AcceptNewPlayers = true;
        public IReadOnlyList<Session> Sessions => _sessions;

        public NetTransport(string name, int port = 0)
        {
            MyName = name;
            _udp = new UdpClient(AddressFamily.InterNetwork);
            _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, false);
            _udp.Client.Bind(new IPEndPoint(IPAddress.Any, port));
            _udp.EnableBroadcast = true;
            try { _udp.Client.IOControl(-1744830452, new byte[] { 0 }, null); } catch (Exception) { }   // SIO_UDP_CONNRESET off
        }

        /// <summary>dpOpen with a new session: this machine is the host and the first player.</summary>
        public void Host(string sessionName, int maxPlayers, string password = "")
        {
            SessionName = sessionName;
            MaxPlayers = maxPlayers;
            Password = password ?? "";
            MyId = HostId = _nextId++;
            _peers[MyId] = new Peer { Id = MyId, Name = MyName };
        }

        /// <summary>dpOpen of an existing session; MyId is set when the host's welcome arrives.</summary>
        public void Join(IPEndPoint host, string password = "")
        {
            Password = password ?? "";
            _hostEnd = host;
            _joining = true;
            _lastHello = -HelloSeconds;
            RefuseReason = null;
        }

        /// <summary>dpEnumSessions over IP: asks the broadcast address and any listed machines; answers land in Sessions.</summary>
        public void Enumerate(IEnumerable<IPEndPoint> direct = null)
        {
            _sessions.Clear();
            var d = Header(KindEnum, 0, 0, 0, 0);
            try { _udp.Send(d, d.Length, new IPEndPoint(IPAddress.Broadcast, DefaultPort)); } catch (SocketException) { }
            if (direct != null) foreach (var end in direct) Raw(d, end);
        }

        public int NumPlayers => _peers.Count;
        public IEnumerable<ushort> PlayerIds => _peers.Keys;
        public string PlayerName(ushort id) => _peers.TryGetValue(id, out var p) ? p.Name : null;

        /// <summary>dpSend. to = 0 sends to every other player.</summary>
        public void Send(ushort to, byte[] data, int size, bool reliable)
        {
            if (MyId == NoId) return;
            foreach (var peer in _peers.Values)
            {
                if (peer.Id == MyId || (to != 0 && to != peer.Id) || peer.End == null) continue;
                uint seq = reliable ? peer.NextSend++ : 0;
                var d = Header(KindData, MyId, peer.Id, seq, size);
                Buffer.BlockCopy(data, 0, d, HeaderSize, size);
                Raw(d, peer.End);
                if (reliable) peer.Unacked.Add(new Pending { Sequence = seq, Datagram = d, Sent = _now });
            }
        }

        /// <summary>dpReceive: false when nothing is waiting (dp_RES_EMPTY).</summary>
        public bool Receive(out ushort from, out byte[] data)
        {
            if (_inbox.Count > 0)
            {
                (from, data) = _inbox.Dequeue();
                return true;
            }
            from = NoId; data = null;
            return false;
        }

        /// <summary>The periodic work anet2 does inside dpReceive. time is any monotonic seconds value.</summary>
        public void Poll(float time)
        {
            _now = time;
            while (_udp.Available > 0)
            {
                IPEndPoint end = new IPEndPoint(IPAddress.Any, 0);
                byte[] d;
                try { d = _udp.Receive(ref end); }
                catch (SocketException) { break; }
                if (d.Length >= HeaderSize && d[0] == (byte)'M' && d[1] == (byte)'2') Handle(d, end);
            }
            if (_joining && _now - _lastHello >= HelloSeconds)
            {
                _lastHello = _now;
                var name = Encoding.ASCII.GetBytes(MyName ?? "");
                var pass = Encoding.ASCII.GetBytes(Password);
                var hello = Header(KindHello, 0, 0, 0, 2 + name.Length + pass.Length);
                hello[HeaderSize] = (byte)name.Length;
                hello[HeaderSize + 1] = (byte)pass.Length;
                Buffer.BlockCopy(name, 0, hello, HeaderSize + 2, name.Length);
                Buffer.BlockCopy(pass, 0, hello, HeaderSize + 2 + name.Length, pass.Length);
                Raw(hello, _hostEnd);
            }
            if (MyId == NoId) return;
            bool keep = _now - _lastKeep >= 1f;
            if (keep) _lastKeep = _now;
            List<ushort> lost = null;
            foreach (var peer in _peers.Values)
            {
                if (peer.Id == MyId || peer.End == null) continue;
                if (keep) Raw(Header(KindKeep, MyId, peer.Id, 0, 0), peer.End);
                foreach (var pending in peer.Unacked)
                    if (_now - pending.Sent >= ResendSeconds)
                    {
                        pending.Sent = _now;
                        Raw(pending.Datagram, peer.End);
                    }
                if (_now - peer.LastHeard > PeerTimeout) (lost ??= new List<ushort>()).Add(peer.Id);
            }
            if (lost != null) foreach (var id in lost) Remove(id);
        }

        void Handle(byte[] d, IPEndPoint end)
        {
            byte kind = d[2];
            ushort from = BitConverter.ToUInt16(d, 3), to = BitConverter.ToUInt16(d, 5);
            uint seq = BitConverter.ToUInt32(d, 7);
            if (from != 0 && _peers.TryGetValue(from, out var known)) known.LastHeard = _now;
            switch (kind)
            {
                case KindData:
                    if (to != MyId || !_peers.TryGetValue(from, out var peer)) return;
                    var body = new byte[d.Length - HeaderSize];
                    Buffer.BlockCopy(d, HeaderSize, body, 0, body.Length);
                    if (seq == 0) { _inbox.Enqueue((from, body)); return; }
                    Raw(Header(KindAck, MyId, from, seq, 0), end);
                    if (seq < peer.NextReceive) return;
                    peer.Early[seq] = body;
                    while (peer.Early.TryGetValue(peer.NextReceive, out var next))
                    {
                        peer.Early.Remove(peer.NextReceive);
                        peer.NextReceive++;
                        _inbox.Enqueue((from, next));
                    }
                    return;
                case KindAck:
                    if (_peers.TryGetValue(from, out var acked)) acked.Unacked.RemoveAll(p => p.Sequence == seq);
                    return;
                case KindHello:
                    if (IsHost) Welcome(d, end);
                    return;
                case KindWelcome:
                    if (!_joining) return;
                    _joining = false;
                    MyId = to;
                    HostId = from;
                    _peers[MyId] = new Peer { Id = MyId, Name = MyName };
                    ApplyPlayers(d);
                    return;
                case KindPlayers:
                    if (from == HostId) ApplyPlayers(d);
                    return;
                case KindRefuse:
                    _joining = false;
                    RefuseReason = Encoding.ASCII.GetString(d, HeaderSize, d.Length - HeaderSize);
                    return;
                case KindBye:
                    Remove(from);
                    return;
                case KindEnum:
                    if (IsHost) Raw(Packet(KindSession, MyId, 0, Encoding.ASCII.GetBytes($"{_peers.Count}|{MaxPlayers}|{SessionName}")), end);
                    return;
                case KindSession:
                {
                    var parts = Encoding.ASCII.GetString(d, HeaderSize, d.Length - HeaderSize).Split(new[] { '|' }, 3);
                    if (parts.Length != 3) return;
                    _sessions.RemoveAll(s => s.Host.Equals(end));
                    _sessions.Add(new Session { Host = end, Players = int.Parse(parts[0]), MaxPlayers = int.Parse(parts[1]), Name = parts[2] });
                    return;
                }
            }
        }

        void Welcome(byte[] d, IPEndPoint end)
        {
            int nameLength = d[HeaderSize], passLength = d[HeaderSize + 1];
            string name = Encoding.ASCII.GetString(d, HeaderSize + 2, nameLength);
            string pass = Encoding.ASCII.GetString(d, HeaderSize + 2 + nameLength, passLength);
            Peer existing = null;
            foreach (var p in _peers.Values) if (p.End != null && p.End.Equals(end)) existing = p;
            if (existing == null)
            {
                // Texts: 0x10006330 reports these two refusals on the joining machine.
                if (pass != Password) { Raw(Packet(KindRefuse, MyId, 0, Encoding.ASCII.GetBytes("Incorrect password!")), end); return; }
                if (_peers.Count >= MaxPlayers || !AcceptNewPlayers) { Raw(Packet(KindRefuse, MyId, 0, Encoding.ASCII.GetBytes("Sorry, that game is full.")), end); return; }
                existing = new Peer { Id = _nextId++, Name = name, End = end, LastHeard = _now };
                _peers[existing.Id] = existing;
                _inbox.Enqueue((0, System(NetLobbyTags.AddPlayer, existing.Id)));
            }
            Raw(Packet(KindWelcome, MyId, existing.Id, PlayersBody()), end);
            var list = PlayersBody();
            foreach (var p in _peers.Values)
                if (p.Id != MyId && p.End != null && p.Id != existing.Id) Raw(Packet(KindPlayers, MyId, p.Id, list), p.End);
        }

        byte[] PlayersBody()
        {
            var b = new StringBuilder();
            foreach (var p in _peers.Values)
                b.Append(p.Id).Append('\t').Append(p.Name).Append('\t').Append(p.End != null ? p.End.ToString() : "").Append('\n');
            return Encoding.ASCII.GetBytes(b.ToString());
        }

        void ApplyPlayers(byte[] d)
        {
            var seen = new HashSet<ushort>();
            foreach (var line in Encoding.ASCII.GetString(d, HeaderSize, d.Length - HeaderSize).Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var v = line.Split('\t');
                ushort id = ushort.Parse(v[0]);
                seen.Add(id);
                if (id == MyId) continue;
                if (!_peers.TryGetValue(id, out var peer))
                {
                    peer = new Peer { Id = id, Name = v[1], LastHeard = _now };
                    _peers[id] = peer;
                    _inbox.Enqueue((0, System(NetLobbyTags.AddPlayer, id)));
                }
                if (id == HostId) peer.End = _hostEnd;
                else if (v[2].Length > 0)
                {
                    int colon = v[2].LastIndexOf(':');
                    var address = IPAddress.Parse(v[2].Substring(0, colon));
                    // The host saw a machine on its own loopback: reach it the way the host is reached.
                    if (IPAddress.IsLoopback(address) && _hostEnd != null) address = _hostEnd.Address;
                    peer.End = new IPEndPoint(address, int.Parse(v[2].Substring(colon + 1)));
                }
            }
            var gone = new List<ushort>();
            foreach (var id in _peers.Keys) if (!seen.Contains(id)) gone.Add(id);
            foreach (var id in gone) Remove(id);
        }

        void Remove(ushort id)
        {
            if (!_peers.Remove(id)) return;
            _inbox.Enqueue((0, System(NetLobbyTags.DeletePlayer, id)));
            // The smallest id left takes over, as Net_Update's election FUN_10022a33 does for the game.
            if (id == HostId)
            {
                ushort lowest = NoId;
                foreach (var key in _peers.Keys) if (key < lowest) lowest = key;
                HostId = lowest;
                if (IsHost) foreach (var key in _peers.Keys) if (key >= _nextId) _nextId = (ushort)(key + 1);
            }
            else if (IsHost)
            {
                var list = PlayersBody();
                foreach (var p in _peers.Values)
                    if (p.Id != MyId && p.End != null) Raw(Packet(KindPlayers, MyId, p.Id, list), p.End);
            }
        }

        /// <summary>dpClose: tells the others and leaves.</summary>
        public void Close()
        {
            if (MyId == NoId) return;
            foreach (var peer in _peers.Values)
                if (peer.Id != MyId && peer.End != null) Raw(Header(KindBye, MyId, peer.Id, 0, 0), peer.End);
            _peers.Clear();
            MyId = NoId;
        }

        public void Dispose()
        {
            Close();
            _udp.Close();
        }

        const int HeaderSize = 11;

        static byte[] System(ushort tag, ushort id)
        {
            var b = new byte[4];
            Buffer.BlockCopy(BitConverter.GetBytes(tag), 0, b, 0, 2);
            Buffer.BlockCopy(BitConverter.GetBytes(id), 0, b, 2, 2);
            return b;
        }

        static byte[] Header(byte kind, ushort from, ushort to, uint seq, int payload)
        {
            var d = new byte[HeaderSize + payload];
            d[0] = (byte)'M'; d[1] = (byte)'2'; d[2] = kind;
            d[3] = (byte)from; d[4] = (byte)(from >> 8);
            d[5] = (byte)to; d[6] = (byte)(to >> 8);
            Buffer.BlockCopy(BitConverter.GetBytes(seq), 0, d, 7, 4);
            return d;
        }

        static byte[] Packet(byte kind, ushort from, ushort to, byte[] payload)
        {
            var d = Header(kind, from, to, 0, payload.Length);
            Buffer.BlockCopy(payload, 0, d, HeaderSize, payload.Length);
            return d;
        }

        void Raw(byte[] d, IPEndPoint end)
        {
            try { _udp.Send(d, d.Length, end); }
            catch (SocketException) { }
        }
    }
}
