using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using MW2.Formats;
using UnityEngine;

namespace MW2.Shell
{
    /// <summary>
    /// MercNet, the lobby of netshell.dll: NetShellMain2 0x1002c550. Callsign and connection, room list (games.c), ready room, options
    /// and mission (hostopts.c), 'Mech (mechs.c), launch (network.c, shellsim.c) and scores (FUN_1001ad60). Adaptation: drawn with Unity's
    /// IMGUI with the original texts, not the NET* screens of MW2.PRJ; only the TCP/IP connection exists. Start: --mw2-net [--mw2-net-test].
    /// </summary>
    public sealed class Mw2NetShell : MonoBehaviour
    {
        public string GameFolder = Mw2GameFolder.Default;
        public static Mw2NetShell Instance;

        PrjArchive _prj;
        NetMechTable _mechs;
        readonly List<NetMission> _missions = new List<NetMission>();
        NetTransport _net;
        NetLobbySession _session;
        enum Screen { Crosslink, Rooms, Ready, Loading, Battle, Scores }
        Screen _screen = Screen.Crosslink;
        string _callsign = "MechWarrior", _address = "127.0.0.1", _password = "", _chat = "", _roomName = "";
        int _chassis, _variant;
        int _mission;
        byte _hostOptions = 0x3d, _hostTemperature, _hostGravity;
        byte _hostTimeOfDay = 1; // netshell1002d860 default; spinner1000a580: Std(0), Custom(1).
        bool _teamGame;
        GameObject _root;
        string _status = "";
        Vector2 _scroll;

        public IReadOnlyList<NetMission> Missions => _missions;
        public NetLobbySession Session => _session;
        public NetMechTable MechTable => _mechs;
        public Mw2NetGame Game { get; private set; }
        public string LastScores { get; private set; } = "";
        public bool ScoresVisible => _screen == Screen.Scores;
        public byte HostTimeOfDay => _hostTimeOfDay;

        /// <summary>Host options1000a580/1000b6e0: original two-entry time spinner, copied to room+0xb at creation.</summary>
        public void SelectTimeOfDay(byte value)
        {
            if (value > 1) throw new ArgumentOutOfRangeException(nameof(value), "Time spinner contains Std and Custom");
            _hostTimeOfDay = value;
        }

        public static bool Bootstrap()
        {
            var args = Environment.GetCommandLineArgs();
            if (!args.Contains("--mw2-net")) return false;
            var go = new GameObject("MercNet");
            DontDestroyOnLoad(go);
            var shell = go.AddComponent<Mw2NetShell>();
            int folder = Array.IndexOf(args, "--game-folder");
            if (folder >= 0 && folder + 1 < args.Length) shell.GameFolder = args[folder + 1];
            if (args.Contains("--mw2-net-test")) go.AddComponent<Mw2NetSelfTest>();
            return true;
        }

        void Awake()
        {
            Instance = this;
            // Host adaptation: Update/Poll must keep servicing peers when the window loses focus.
            // This belongs to normal MercNet startup, not only to the automated network test.
            Application.runInBackground = true;
        }

        void Start() { LoadData(); }

        /// <summary>FUN_1001c850 and FUN_1001eae0: MISSIONS\*.mwm and the 'Mech table.</summary>
        public void LoadData()
        {
            if (_prj != null) return;
            _prj = new PrjArchive(Path.Combine(GameFolder, "MW2.PRJ"));
            _mechs = new NetMechTable(_prj);
            string folder = Path.Combine(GameFolder, "MISSIONS");
            if (Directory.Exists(folder))
                foreach (var file in Directory.GetFiles(folder, "*.mwm").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    var mission = NetMission.Load(file);
                    if (mission != null) _missions.Add(mission);
                }
            _chassis = Math.Max(0, _mechs.Find("commando"));
            Debug.Log($"MercNet: {_missions.Count} missions, {_mechs.Items.Count} 'Mechs");
        }

        public void HostRoom(string callsign, int port, int mission, bool team, int maxPlayers = 8, string password = "", byte options = 0)
        {
            Close();
            _callsign = callsign;
            _net = new NetTransport(callsign, port);
            _net.Host($"{callsign}'s Ready Room", maxPlayers, password);     // "%s's Ready Room"
            _session = new NetLobbySession(_net) { WantMech = (ushort)((_chassis << 7) | _variant) };
            _session.OpenHost(_missions[mission].Prefix, options, team, timeOfDay: _hostTimeOfDay);
            _screen = Screen.Ready;
        }

        public void JoinRoom(string callsign, IPEndPoint host, string password = "")
        {
            Close();
            _callsign = callsign;
            _net = new NetTransport(callsign);
            _net.Join(host, password);
            _session = new NetLobbySession(_net) { WantMech = (ushort)((_chassis << 7) | _variant) };
            _screen = Screen.Ready;
        }

        public void SelectMech(int chassis, int variant)
        {
            _chassis = chassis;
            _variant = variant;
            _session?.SetMech(chassis, variant);
        }

        void Close()
        {
            _net?.Dispose();
            _net = null;
            _session = null;
        }

        void OnDestroy()
        {
            Close();
            _prj?.Dispose();
        }

        void Update()
        {
            if (_session == null) return;
            if (_screen == Screen.Ready)
            {
                _session.Poll(Time.realtimeSinceStartup);
                if (_net.RefuseReason != null) { _status = _net.RefuseReason; Close(); _screen = Screen.Rooms; return; }
                if (_session.Lost) { _status = _session.LostReason; Close(); _screen = Screen.Rooms; return; }
                if (_session.Launch) { _screen = Screen.Loading; StartCoroutine(Launch()); }
            }
        }

        /// <summary>FUN_1001b7b0: en00star..en07star, then the mission with the sorted room; SimMain continues in Mw2NetGame.</summary>
        IEnumerator Launch()
        {
            yield return null;
            var mission = _missions.FirstOrDefault(m => string.Equals(m.Prefix, _session.Room.Mission, StringComparison.OrdinalIgnoreCase));
            if (mission == null)
            {
                // FUN_100163f0.
                _status = "Sorry, that mission is not available on your machine.";
                Close();
                _screen = Screen.Rooms;
                yield break;
            }
            var room = _session.Room;
            var units = _session.Units(_mechs, _prj);
            var files = NetRoster.Files(units, room.TeamGame, room.Options);
            int me = room.Find(_net.MyId);
            var local = units[me];
            Mission.LooseBwd = name => files.TryGetValue(name, out var roster) ? roster : mission.Bwd(name);
            // Load the selected folder before applying FUN_1001c490's room bytes.
            // Otherwise GameOptions can create a folderless fallback while _loaded is
            // false, then replace it with single-play forced bytes once the director exists.
            Mw2Input.GameFolder = GameFolder;
            Mw2MechCombat.NetGame = true;
            room.ApplyTo(Mw2Options.GameOptions);
            Mw2MissionBuilder.NetOverride = room.ApplyTo;
            // en??star order: a team game has teams 0 and 1, free for all one team for each sorted player.
            Mw2MissionDirector.PlayerTeam = room.TeamGame ? local.Team : me;
            try { _root = Mw2MissionBuilder.Build(_prj, GameFolder, mission.Prefix, local.Model.ToLowerInvariant(), 1, local.Mek); }
            catch (Exception error) { Debug.LogException(error); }
            finally
            {
                Mission.LooseBwd = null;
                Mw2MissionBuilder.NetOverride = null;
            }
            if (_root == null)
            {
                Mw2MechCombat.NetGame = false;
                _status = "The mission could not be loaded.";
                Close();
                _screen = Screen.Rooms;
                yield break;
            }
            // Player index = room index: the same order as the GPS records of the en??star files.
            var sorted = Mw2MechCombat.All
                .Where(c => c.IsPlayer || (c.SourceFile != null && c.SourceFile.StartsWith("en", StringComparison.OrdinalIgnoreCase) && c.SourceFile.EndsWith("star", StringComparison.OrdinalIgnoreCase)))
                .OrderBy(c => c.GamepieceIndex).ToArray();
            if (sorted.Length != room.Players.Count)
                Debug.LogError($"MercNet: {sorted.Length} units for {room.Players.Count} players");
            Game = Mw2NetGame.Begin(_root, _net, room, sorted);
            Mw2Cockpit.MissionLeft = OnMissionLeft;
            _screen = Screen.Battle;
        }

        /// <summary>The end of SimMain: FUN_10022883, then the scores screen (FUN_1001a740).</summary>
        public void OnMissionLeft(bool fled)
        {
            Mw2Cockpit.MissionLeft = null;
            if (Game != null)
            {
                Game.Leave();
                LastScores = ScoresText(Game);
            }
            StartCoroutine(FinishLeaving());
        }

        IEnumerator FinishLeaving()
        {
            // Lets the reliable SS reach the others before dpClose.
            float until = Time.realtimeSinceStartup + 1f;
            while (Time.realtimeSinceStartup < until) { _net?.Poll(Time.realtimeSinceStartup); yield return null; }
            if (_root != null)
            {
                // Build deliberately keeps the player's mech outside the mission root.
                // The original simulation returns before netshell builds its separate scores screen.
                var director = _root.GetComponent<Mw2MissionDirector>();
                if (director != null && director.Player != null && !director.Player.transform.IsChildOf(_root.transform))
                {
                    director.Player.gameObject.SetActive(false);
                    Destroy(director.Player.gameObject);
                }
                _root.SetActive(false);
                Destroy(_root);
            }
            _root = null;
            yield return null; // Complete Unity's deferred destruction before showing results.
            Mw2MissionDirector.PlayerTeam = 0;
            Mw2ImageEnhancement.Enabled = false;
            Mw2Pause.ResetForMissionExit();
            Mw2Options.Reset();
            Game = null;
            Close();
            _screen = Screen.Scores;
        }

        /// <summary>FUN_1001ad60: "%s killed %s %d times." and the winner line.</summary>
        public static string ScoresText(Mw2NetGame game)
        {
            var lines = new List<string>();
            string Name(int i) => game.Net.PlayerName(game.Ids[i]) ?? game.Units[i]?.DisplayName ?? "";
            if (game.WinnerIndex >= 0) lines.Add($"{Name(game.WinnerIndex)} successfully completed the mission!");
            else lines.Add("This mission has not yet been completed.");
            for (int killer = 0; killer < game.Ids.Length; killer++)
                for (int victim = 0; victim < game.Ids.Length; victim++)
                {
                    int n = game.Kills[killer, victim];
                    if (killer == victim && n == 0) continue;
                    lines.Add(n == 0 ? $"{Name(killer)} never killed {Name(victim)}."
                        : n == 1 ? $"{Name(killer)} killed {Name(victim)} once."
                        : $"{Name(killer)} killed {Name(victim)} {n} times.");
                }
            for (int i = 0; i < game.Ids.Length; i++) lines.Add($"{Name(i)}: {game.Scores[i]}");
            return string.Join("\n", lines);
        }

        void OnGUI()
        {
            if (_screen == Screen.Battle) return;
            // Scores are a separate netshell page, not an overlay on the cockpit.
            if (_screen == Screen.Scores)
                GUI.DrawTexture(new Rect(0, 0, UnityEngine.Screen.width, UnityEngine.Screen.height), Texture2D.blackTexture);
            float scale = Mathf.Min(Screen_Width / 640f, Screen_Height / 480f);
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1));
            GUILayout.BeginArea(new Rect(16, 16, 608, 448));
            switch (_screen)
            {
                case Screen.Crosslink: DrawCrosslink(); break;
                case Screen.Rooms: DrawRooms(); break;
                case Screen.Ready: DrawReady(); break;
                case Screen.Loading: GUILayout.Label("Waiting for remote players..."); break;
                case Screen.Scores: DrawScores(); break;
            }
            GUILayout.EndArea();
        }

        static float Screen_Width => UnityEngine.Screen.width;
        static float Screen_Height => UnityEngine.Screen.height;

        void DrawCrosslink()
        {
            GUILayout.Label(" Establish System Crosslink");
            GUILayout.Label(" Enter Your Callsign");
            _callsign = GUILayout.TextField(_callsign, 0x14);
            if (GUILayout.Button("Select"))
            {
                // FUN_10003ca0.
                if (string.IsNullOrWhiteSpace(_callsign)) _status = "You must enter a callsign!";
                else { _status = ""; _screen = Screen.Rooms; }
            }
            GUILayout.Label(_status);
        }

        void DrawRooms()
        {
            GUILayout.Label("Pilot's Lounge");
            GUILayout.Label(_status);
            GUILayout.Label("This is the server's IP address field.");
            _address = GUILayout.TextField(_address);
            GUILayout.Label("Input Password");
            _password = GUILayout.TextField(_password);
            if (GUILayout.Button("Join the currently selected room."))
            {
                if (IPAddress.TryParse(_address, out var ip)) JoinRoom(_callsign, new IPEndPoint(ip, NetTransport.DefaultPort), _password);
                else _status = "You must first specify a machine to connect to.";
            }
            GUILayout.Space(8);
            _mission = Mathf.Clamp(_mission, 0, Mathf.Max(0, _missions.Count - 1));
            GUILayout.Label(_missions.Count > 0 ? $"Mission: {_missions[_mission].Name} ({_missions[_mission].Prefix})" : "No missions");
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Click to choose previous available mission.")) _mission = (_mission + _missions.Count - 1) % Math.Max(1, _missions.Count);
            if (GUILayout.Button("Click to choose next available mission.")) _mission = (_mission + 1) % Math.Max(1, _missions.Count);
            GUILayout.EndHorizontal();
            if (_missions.Count > 0) _teamGame = _missions[_mission].Modes[0].Team != 0;
            GUILayout.Label(_teamGame ? "Team Missions" : "Free for all");
            // FUN_1001c490: the host's choices (the screens of hostopts.c).
            bool Toggle(byte bit, string text)
            {
                bool on = GUILayout.Toggle((_hostOptions & bit) != 0, text);
                _hostOptions = (byte)(on ? _hostOptions | bit : _hostOptions & ~bit);
                return on;
            }
            GUILayout.BeginHorizontal();
            Toggle(1, "Regenerate");
            Toggle(4, "Heat");
            Toggle(0x10, "Collision damage");
            Toggle(8, "Splash damage");
            Toggle(0x20, "Radar");
            Toggle(2, "Unlimited ammo");
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Air temperature: " + new[] { "mission", "-50", "10", "70" }[_hostTemperature])) _hostTemperature = (byte)((_hostTemperature + 1) % 4);
            if (GUILayout.Button("Gravity: " + (_hostGravity == 0 ? "mission" : (_hostGravity / 16f).ToString("0.00") + " G"))) _hostGravity = (byte)((_hostGravity + 8) % 40);
            if (GUILayout.Button("Time: " + (_hostTimeOfDay == 0 ? "Std" : "Custom"))) SelectTimeOfDay((byte)(_hostTimeOfDay ^ 1));
            GUILayout.EndHorizontal();
            if (_missions.Count > 0 && GUILayout.Button("Host a game."))
            {
                HostRoom(_callsign, NetTransport.DefaultPort, _mission, _teamGame, _missions[_mission].Modes[0].MaxPlayers, _password, _hostOptions);
                _session.Room.Temperature = _hostTemperature;
                _session.Room.Gravity = _hostGravity;
            }
        }

        void DrawReady()
        {
            if (_session == null) return;
            GUILayout.Label(_net.MyId == NetTransport.NoId ? "Connecting..." : _net.SessionName);
            var room = _session.Room;
            var mission = _missions.FirstOrDefault(m => string.Equals(m.Prefix, room.Mission, StringComparison.OrdinalIgnoreCase));
            GUILayout.Label($"Mission: {(mission != null ? mission.Name : room.Mission)}  {(room.TeamGame ? "Team Missions" : "Free for all")}");
            foreach (var p in room.Players)
            {
                string mech = p.Chassis < _mechs.Items.Count ? NetMechTable.Mek(_mechs.Items[p.Chassis].Code, p.Variant) : "?";
                GUILayout.Label($"{_net.PlayerName(p.Id)}  {mech}  {(room.TeamGame ? "Team " + (char)('A' + p.Team) : "")}  {((p.Flags & NetRoom.FlagReady) != 0 ? "Ready" : "")}");
            }
            var chassis = _mechs.Items[Mathf.Clamp(_chassis, 0, _mechs.Items.Count - 1)];
            GUILayout.Label($"'Mech: {chassis.Name} ({chassis.Tons} T)  {NetMechTable.Mek(chassis.Code, _variant)}");
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Click here to go to the previous 'Mech chassis.")) { int c = (_chassis + _mechs.Items.Count - 1) % _mechs.Items.Count; SelectMech(c, FirstVariant(c)); }
            if (GUILayout.Button("Click here to go to the next 'Mech chassis.")) { int c = (_chassis + 1) % _mechs.Items.Count; SelectMech(c, FirstVariant(c)); }
            if (GUILayout.Button("next configuration")) { var v = chassis.Variants; if (v.Count > 0) SelectMech(_chassis, v[(v.IndexOf(_variant) + 1) % v.Count]); }
            GUILayout.EndHorizontal();
            int me = room.Find(_net.MyId);
            if (me >= 0)
            {
                bool ready = (room.Players[me].Flags & NetRoom.FlagReady) != 0;
                if (room.TeamGame && GUILayout.Button("Click here to change your clan allegiance")) _session.SetTeam(room.Players[me].Team ^ 1);
                if (GUILayout.Button(ready ? "Launch (waiting)" : "Click here when you are ready to launch.")) _session.SetReady(!ready);
            }
            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(110));
            foreach (var line in _session.ChatLines) GUILayout.Label(line);
            GUILayout.EndScrollView();
            GUILayout.BeginHorizontal();
            _chat = GUILayout.TextField(_chat, 0x4f);
            if (GUILayout.Button("Send", GUILayout.Width(60)) && _chat.Length > 0) { _session.Chat(_chat); _chat = ""; }
            GUILayout.EndHorizontal();
            if (GUILayout.Button("Close connection and restart.")) { Close(); _screen = Screen.Rooms; }
            // FUN_10018e00.
            if (GUILayout.Button("Click here to exit MercNet.")) Application.Quit();
        }

        int FirstVariant(int chassis)
        {
            var v = _mechs.Items[chassis].Variants;
            return v.Count > 0 ? v[0] : 0;
        }

        void DrawScores()
        {
            GUILayout.Label(LastScores);
            if (GUILayout.Button("Leave")) _screen = Screen.Rooms;
        }
    }
}
