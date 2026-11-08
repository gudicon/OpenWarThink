using System;
using System.Collections.Generic;
using System.Linq;
using MW2.Formats;
using UnityEngine;

namespace MW2
{
    /// <summary>
    /// The network half of the simulation: FirstNetwork 0x10020376 and Net_Update 0x10020760. Every player sends his own mech's state each
    /// _DAT_100901f8 ticks and every player's shots are simulated everywhere; the owner alone applies their damage. Host and clients
    /// start together with LA / GO (0x10022370, 0x1002239d, 0x100223f8). A player unheard for 0x38e ticks is hidden (bits 0x4800) until his
    /// next DA; the smallest id left hosts (FUN_10022a33).
    /// Units are in room order: player index is the sorted room index and also the order of the en??star files.
    /// </summary>
    public sealed class Mw2NetGame : MonoBehaviour
    {
        public static Mw2NetGame Instance;

        public NetTransport Net;
        public Mw2MechCombat[] Units;
        public ushort[] Ids;
        public int Me;
        public int Period = 0x24;           // _DAT_100901f8; 0x3c with session flag 1 (FUN_100227a2)
        public bool Regenerate;             // DAT_100aea44 +8, FUN_1001c490 option 1
        public readonly short[,] Kills = new short[8, 8];  // DAT_101fd732
        public readonly short[] Scores = new short[8];     // DAT_10250e60
        public readonly List<string> Messages = new List<string>();
        public int WinnerIndex = -1;        // _DAT_101fd7b2
        public bool Started { get; private set; }           // DAT_100aeaa0 & 1
        public int SentStates { get; private set; }
        public int ReceivedStates { get; private set; }

        readonly int[] _lastHeard = new int[8];     // DAT_10250e80
        readonly int[] _lastTime = new int[8];      // DAT_10250ea0
        readonly int[] _lastSnapshot = new int[8];  // DAT_10250f60
        readonly bool[] _clientReady = new bool[8]; // DAT_10250ee0
        readonly bool[] _left = new bool[8];        // DAT_10250f00
        readonly bool[] _wasDead = new bool[8];     // DAT_10250f40
        ushort _fired;              // DAT_100b6620
        double _clock;
        int _now, _nextSend, _sequence;
        bool _exitSent, _successSent, _everHost;
        int _deadAt = -1;
        public bool WaitingToRegenerate { get; private set; }

        bool IsHost => Net.IsHost;

        /// <summary>FirstNetwork: the session and the units in room order.</summary>
        public static Mw2NetGame Begin(GameObject root, NetTransport net, NetRoom room, Mw2MechCombat[] units)
        {
            var game = root.AddComponent<Mw2NetGame>();
            game.Net = net;
            game.Units = units;
            game.Ids = room.Players.Select(p => p.Id).ToArray();
            game.Me = Array.IndexOf(game.Ids, net.MyId);
            game.Regenerate = (room.Options & 1) != 0;
            for (int i = 0; i < units.Length; i++)
            {
                if (units[i] == null) continue;
                units[i].NetRemote = i != game.Me;
                units[i].NetRemember();
            }
            // AfterWorldLoader 0x100739f9: this machine's team is side 0, every other side 1.
            int myTeam = game.Me >= 0 && game.Me < units.Length && units[game.Me] != null ? units[game.Me].Team : 0;
            foreach (var unit in units)
                if (unit != null) unit.Side = unit.Team == myTeam ? 0 : 1;
            if (game.Me >= 0 && units[game.Me] != null && units[game.Me].TryGetComponent<Mw2MechController>(out var mine)) mine.PowerEnabled = false;
            Instance = game;
            return game;
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
            Mw2MechCombat.NetGame = false;
        }

        void Say(string text, int ticks = 0x712)
        {
            Messages.Add(text);
            Debug.Log("MW2 net: " + text);
            if (Me >= 0 && Me < Units.Length && Units[Me] != null && Units[Me].TryGetComponent<Mw2Cockpit>(out var cockpit))
                cockpit.ShowMessage(text, ticks);
        }

        string NameOf(int index) => index >= 0 && index < Ids.Length ? Net.PlayerName(Ids[index]) ?? _names(index) : "";
        string _names(int index) => Units[index] != null ? Units[index].DisplayName : "";

        void Update()
        {
            _clock += Time.deltaTime * MechMotion.TicksPerSecond;
            _now = (int)_clock;
            Net.Poll(Time.realtimeSinceStartup);
            while (Net.Receive(out ushort from, out byte[] d)) Dispatch(from, d);

            if (!Started)
            {
                // Net_Update before DAT_100a6c5c: clients repeat GO to the host; the host's LA keeps them hearing it.
                if (IsHost)
                {
                    _clientReady[Me] = true;
                    Send(0, NetPackets.EncodeTag("LA"), false);
                    if (AllClientsReady()) BeginPlay();
                }
                else Send(Net.HostId, NetPackets.EncodeTag("GO"), false);
                return;
            }

            if (_now >= _nextSend)
            {
                for (int i = 0; i < Units.Length; i++)
                {
                    var unit = Units[i];
                    if (i == Me || unit == null || _left[i] || !unit.gameObject.activeSelf) continue;
                    if (_now - _lastHeard[i] > 0x38e)
                    {
                        unit.gameObject.SetActive(false);
                        Messages.Add($"hidden {i}");
                    }
                }
                _nextSend = _now + Period;
                SendWeapons();
                SendState();
                _sequence++;
                if ((_sequence & 1) != 0) SendSnapshot();
            }
            UpdateRegeneration();
        }

        bool AllClientsReady()
        {
            for (int i = 0; i < Ids.Length; i++)
                if (!_left[i] && !_clientReady[i]) return false;
            return true;
        }

        void BeginPlay()
        {
            if (IsHost) Send(0, NetPackets.EncodeTag("GO"), false);
            Started = true;
            _nextSend = _now;
            for (int i = 0; i < 8; i++) _lastHeard[i] = _now;
            if (Me >= 0 && Units[Me] != null && Units[Me].TryGetComponent<Mw2MechController>(out var mine)) mine.PowerEnabled = true;
            Say("Battle begins", 0x16a);
        }

        void Send(ushort to, byte[] message, bool reliable) => Net.Send(to, message, message.Length, reliable);

        /// <summary>The fired-slot flag of Weapons_Update 0x1006bc57 (DAT_100b6620).</summary>
        public void MarkFired(int slot) { if (slot >= 0 && slot < 14) _fired |= (ushort)(1 << slot); }

        void SendWeapons()
        {
            if (_fired == 0) return;
            Send(0, NetPackets.EncodeWeapons((byte)Me, _fired), false);
            _fired = 0;
        }

        /// <summary>FUN_10020ed3.</summary>
        void SendState()
        {
            var unit = Units[Me];
            if (unit == null) return;
            var t = unit.transform;
            unit.TryGetComponent<Mw2MechController>(out var c);
            unit.TryGetComponent<Mw2TorsoTwist>(out var twist);
            var s = new NetPackets.UnitState
            {
                Index = (byte)Me,
                Target = (short)(unit.Target != null ? 0x200 | Array.IndexOf(Units, unit.Target) & 0xff : 0x1000),
                Attacker = (short)unit.NetLastAttacker,
                Time = _now,
                X = (int)(t.position.x / PolyModel.UnitsToMeters), Y = (int)(t.position.y / PolyModel.UnitsToMeters), Z = (int)(t.position.z / PolyModel.UnitsToMeters),
                RotY = NetPackets.Fixed(t.eulerAngles.y),
                Torso = twist != null ? NetPackets.Fixed(twist.Current) : 0,
                TorsoPitch = twist != null ? NetPackets.Fixed(twist.Pitch) : 0,
                Throttle = c != null ? NetPackets.Fixed(c.Reverse ? -c.Throttle : c.Throttle) : 0,
                Turn = c != null ? NetPackets.Fixed(c.Turn) : 0
            };
            for (int i = 0; i < 8; i++)
            {
                s.Front[i] = unit.Front[i];
                s.Internal[i] = unit.Internal[i];
                if (i >= 1 && i <= 3) s.Rear[i - 1] = unit.Rear[i];
            }
            s.Flags = NetPackets.ComposeFlags(unit.PowerState, c != null && c.Jump, new bool[5], false, false, 0, 0);
            Send(0, NetPackets.EncodeState(s), false);
            SentStates++;
        }

        public int ReceivedCollisions { get; private set; }

        /// <summary>FUN_10022122: the contact normal and this mech's velocity, to the player it touched.</summary>
        public void SendCollision(Mw2MechCombat other, Vector3 normal, Vector3 velocity)
        {
            int index = Array.IndexOf(Units, other);
            if (index < 0 || index == Me) return;
            Send(Ids[index], NetPackets.EncodeCollision(normal.x, normal.y, normal.z, velocity.x, velocity.y, velocity.z), false);
        }

        /// <summary>FUN_1002223a: the sound and FUN_100350fb on this machine's own mech; the vector arrives negated, so it points away from the sender.</summary>
        void ApplyCollision(int index, byte[] d)
        {
            float N(int at) => NetPackets.FromFixed(BitConverter.ToInt32(d, at));
            Vector3 normal = new Vector3(N(2), N(6), N(10)), theirs = new Vector3(N(14), N(18), N(22));
            ReceivedCollisions++;
            var mine = Units[Me];
            if (mine == null) return;
            mine.TryGetComponent<Mw2MechController>(out var c);
            Vector3 velocity = c != null ? c.ImpactVelocity : Vector3.zero;
            float speed = (float)MechSphereContact.Length(theirs.x, theirs.y, theirs.z);
            Mw2Audio.Instance?.Play(0x105, mine.transform.position, true, (int)(100f * Mathf.Min(speed, 150f) / 150f));
            mine.CollisionImpact(normal, Units[index], null, velocity, theirs);
        }

        /// <summary>FUN_10022495. The GT list is Mw2Destructible.All in load order.</summary>
        void SendSnapshot()
        {
            var bits = new bool[NetPackets.ScenerySlots];
            int n = 0;
            foreach (var gt in Mw2Destructible.All)
            {
                if (n >= bits.Length) break;
                bits[n++] = gt != null && gt.Destroyed;
            }
            Send(0, NetPackets.EncodeSnapshot(_sequence, bits), false);
        }

        void Dispatch(ushort from, byte[] d)
        {
            if (d.Length < 2) return;
            ushort tag = BitConverter.ToUInt16(d, 0);
            if (from == 0)
            {
                if (tag == NetLobbyTags.DeletePlayer && d.Length >= 4) PlayerGone(BitConverter.ToUInt16(d, 2));
                return;
            }
            int index = Array.IndexOf(Ids, from);
            if (index < 0 || index == Me || Units[index] == null) return;
            _lastHeard[index] = _now;
            string kind = ((char)d[0]).ToString() + (char)d[1];
            switch (kind)
            {
                case "DA": if (d.Length == NetPackets.StateSize) ApplyState(index, NetPackets.DecodeState(d)); break;
                case "WE":
                    if (d.Length == NetPackets.WeaponsSize) Units[index].NetFire(BitConverter.ToUInt16(d, 3));
                    break;
                case "KI":
                    if (d.Length == NetPackets.KillsSize)
                    {
                        // FUN_100220c1: the sender's column of the table.
                        for (int killer = 0; killer < Ids.Length && killer < 8; killer++)
                            Kills[killer, index] = BitConverter.ToInt16(d, 2 + killer * 2);
                        RecountScores();
                    }
                    break;
                case "CO":
                    if (d.Length == NetPackets.CollisionSize) ApplyCollision(index, d);
                    break;
                case "GO":
                    if (IsHost) _clientReady[index] = true;
                    else if (!Started && from == Net.HostId) BeginPlay();
                    break;
                case "SN":
                    if (d.Length == NetPackets.SnapshotSize) ApplySnapshot(index, d);
                    break;
                case "SU":
                    // FUN_1002268e.
                    Say($"'{NameOf(index)}' successful.");
                    WinnerIndex = index;
                    _successSent = true;
                    break;
                case "SS":
                    // Net_Update: "'%s' exited the game."
                    if (!_left[index])
                    {
                        _left[index] = true;
                        Units[index].NetApply(new NetPackets.UnitState { Flags = 4 }, null);
                        Say($"'{NameOf(index)}' exited the game.");
                    }
                    break;
                case "CH":
                    // FUN_10021d0a.
                    Say($"{NameOf(index)}: {NetPackets.DecodeChat(d, d.Length)}", 0x43e);
                    Mw2Audio.Instance?.Play(0xf0, Units[index].transform.position, true);
                    break;
            }
        }

        void PlayerGone(ushort id)
        {
            int index = Array.IndexOf(Ids, id);
            if (index < 0 || _left[index]) return;
            _left[index] = true;
            if (Units[index] != null && !Units[index].Destroyed) Units[index].NetApply(new NetPackets.UnitState { Flags = 4 }, null);
            Say($"'{NameOf(index)}' exited the game.");
        }

        /// <summary>FUN_10021376.</summary>
        void ApplyState(int index, NetPackets.UnitState s)
        {
            if (s.Time < _lastTime[index]) return;
            _lastTime[index] = s.Time;
            ReceivedStates++;
            var unit = Units[index];
            if (!unit.gameObject.activeSelf)
            {
                unit.gameObject.SetActive(true);
                Messages.Add($"shown {index}");
            }
            if (unit.TryGetComponent<Mw2MechController>(out var c))
            {
                c.NetSetPose(s.X, s.Y, s.Z, NetPackets.FromFixed(s.RotY));
                float throttle = NetPackets.FromFixed(s.Throttle);
                c.Throttle = Mathf.Abs(throttle);
                c.Reverse = throttle < 0f;
                c.Turn = NetPackets.FromFixed(s.Turn);
                c.Jump = (s.Flags & 0x10) != 0;
            }
            if (unit.TryGetComponent<Mw2TorsoTwist>(out var twist))
            {
                twist.SetTwistImmediate(NetPackets.FromFixed(s.Torso));
                twist.PitchTarget = NetPackets.FromFixed(s.TorsoPitch);
            }
            int target = s.Target;
            unit.Target = (target & 0xf00) == 0x200 && (target & 0xff) < Units.Length ? Units[target & 0xff] : null;
            Mw2MechCombat attacker = s.Attacker >= 0 && s.Attacker < Units.Length ? Units[s.Attacker] : null;
            bool dead = (s.Flags & 0xf) == 4 || (s.Flags & 0xf) == 5;
            bool wasDestroyed = unit.Destroyed;
            unit.NetApply(s, attacker);
            if (dead && !_wasDead[index])
            {
                _wasDead[index] = true;
                if (s.Attacker < 0 || s.Attacker > 7 || s.Attacker == index) Say($"'{NameOf(index)}' destroyed.");
                else Say($"'{NameOf(index)}' destroyed by '{NameOf(s.Attacker)}'.");
            }
            else if (!dead && _wasDead[index])
            {
                // FUN_10051da0 and "'%s' resurrected."
                _wasDead[index] = false;
                unit.NetRegenerate();
                Say($"'{NameOf(index)}' resurrected.");
            }
        }

        /// <summary>FUN_1002257b: a GT another player reports destroyed falls here too.</summary>
        void ApplySnapshot(int index, byte[] d)
        {
            var bits = NetPackets.DecodeSnapshot(d, out int sequence);
            if (sequence <= _lastSnapshot[index]) return;
            _lastSnapshot[index] = sequence;
            int n = 0;
            foreach (var gt in Mw2Destructible.All)
            {
                if (n >= bits.Length) break;
                if (bits[n++] && gt != null && !gt.Destroyed) gt.ApplyDamage(float.MaxValue / 4f, null);
            }
        }

        /// <summary>Mech_Destroy 0x10035ef6 on this machine's mech: the killer's kill in DAT_101fd732, FUN_10021ef2.</summary>
        public void LocalDied(Mw2MechCombat source)
        {
            int killer = source != null ? Array.IndexOf(Units, source) : -1;
            var unit = Units[Me];
            unit.NetLastAttacker = killer;
            if (killer >= 0) Kills[killer, Me]++;
            var row = new short[8];
            for (int i = 0; i < Ids.Length && i < 8; i++) row[i] = Kills[i, Me];
            Send(0, NetPackets.EncodeKills(row, Ids.Length), true);
            RecountScores();
            if (!_wasDead[Me])
            {
                _wasDead[Me] = true;
                if (killer < 0 || killer == Me) Say($"'{NameOf(Me)}' destroyed.");
                else Say($"'{NameOf(Me)}' destroyed by '{NameOf(killer)}'.");
            }
            _deadAt = _now;
        }

        /// <summary>A hit on this machine's mech: gp +0x13c, the attacker sent in DA.</summary>
        public void LocalHit(Mw2MechCombat source)
        {
            int attacker = source != null ? Array.IndexOf(Units, source) : -1;
            if (attacker >= 0) Units[Me].NetLastAttacker = attacker;
        }

        void RecountScores()
        {
            var teams = new int[8];
            for (int i = 0; i < Units.Length; i++) teams[i] = Units[i] != null ? Units[i].Team : i;
            NetPackets.Scores(Kills, teams, Units.Length, Scores);
        }

        /// <summary>
        /// Input_HandleCommands 0x10026391: 0x43e ticks after death, with option 1 the player may regenerate (command 0x67, the fire key)
        /// through FUN_10051da0; "Press SPACEBAR to regenerate, or ESC" is drawn by FUN_10068f04.
        /// </summary>
        void UpdateRegeneration()
        {
            if (_deadAt < 0 || !Regenerate) return;
            if (!WaitingToRegenerate && _now >= _deadAt + 0x43e) WaitingToRegenerate = true;
            if (WaitingToRegenerate && Mw2Input.Command(0x67)) RegenerateNow();
        }

        public void RegenerateNow()
        {
            WaitingToRegenerate = false;
            _deadAt = -1;
            _wasDead[Me] = false;
            Units[Me].NetRegenerate();
            Say($"'{NameOf(Me)}' resurrected.");
        }

        /// <summary>Mission_Announce 0x1004127f, success: _DAT_101fd7b2 and FUN_1002264e once.</summary>
        public void LocalSuccess()
        {
            WinnerIndex = Me;
            if (_successSent) return;
            _successSent = true;
            Send(0, NetPackets.EncodeTag("SU"), true);
        }

        /// <summary>FUN_10021b76 with mode -1 / FUN_1002614c: to everyone, and the sender's own line.</summary>
        public bool Chat(string text)
        {
            var message = NetPackets.EncodeChat(text);
            if (message == null) return false;
            Send(0, message, true);
            Say($"{NameOf(Me)}: {text}", 0x2d4);
            return true;
        }

        /// <summary>FUN_10022883: "SS" once when the player leaves.</summary>
        public void Leave()
        {
            if (_exitSent) return;
            _exitSent = true;
            Send(0, NetPackets.EncodeTag("SS"), true);
        }

        bool _chatOpen;
        string _chatText = "";
        void OnGUI()
        {
            if (WaitingToRegenerate) GUI.Label(new Rect(20, 60, 600, 24), "Press SPACEBAR to regenerate, or ESC to exit");
            if (!Started) GUI.Label(new Rect(20, 60, 600, 24), "Waiting for remote players...");
            if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Return && !_chatOpen) { _chatOpen = true; Event.current.Use(); }
            if (!_chatOpen) return;
            GUI.Label(new Rect(20, 84, 600, 24), "Communication  [Enter] Send to all, [Esc] to abort");
            GUI.SetNextControlName("mw2chat");
            _chatText = GUI.TextField(new Rect(20, 108, 400, 24), _chatText, 0x27);
            GUI.FocusControl("mw2chat");
            if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Return) { if (_chatText.Length > 0) Chat(_chatText); _chatText = ""; _chatOpen = false; }
            if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Escape) { _chatText = ""; _chatOpen = false; }
        }
    }
}
