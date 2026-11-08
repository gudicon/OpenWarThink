using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using MW2.Formats;
using UnityEngine;

namespace MW2.Shell
{
    /// <summary>
    /// Two real Player processes play a network battle end to end: --mw2-net --mw2-net-test --role host|join --port N --output file
    /// [--mission FRE2]. Each machine records what it itself observed. Unity test logic, not game code: the shots are real, only the aim
    /// (placing the host's mech beside the client's) is scripted, since nobody presses keys in batch mode.
    /// </summary>
    public sealed class Mw2NetSelfTest : MonoBehaviour
    {
        readonly List<string> _results = new List<string>();
        int _failed;
        string _output;
        bool _host, _radarEnabled = true, _areaEnabled = true;
        int _port;
        byte _timeOfDay;
        string _missionPrefix = "FRE2";
        const float FiringDistance = 100f;

        sealed class RigSnapshot
        {
            public Mw2MechCombat Unit;
            public Transform[] Nodes, Parents;
            public int[][] Items;
            public int HeatSinks, JumpMp;
        }

        static RigSnapshot CaptureRig(Mw2MechCombat unit)
        {
            var nodes = unit.GetComponentsInChildren<Transform>(true).Where(t => t != unit.transform).ToArray();
            return new RigSnapshot { Unit = unit, Nodes = nodes, Parents = nodes.Select(t => t.parent).ToArray(),
                Items = unit.SectionItems.Select(items => items.ToArray()).ToArray(), HeatSinks = unit.HeatSinks,
                JumpMp = unit.GetComponent<Mw2MechController>().JumpMp };
        }

        // FUN_10051da0 / FUN_10052148: both owner and replicas rebuild the whole model tree.
        void CheckRegeneratedRig(RigSnapshot rig)
        {
            Check(rig.Nodes.Select((t, i) => t != null && t.parent == rig.Parents[i] && t.IsChildOf(rig.Unit.transform)).All(x => x),
                "regeneration restores every original model node parent on this Player");
            var lods = rig.Unit.GetComponent<Mw2LodGroup>();
            Check(lods.Levels.Where(l => l.Lod == lods.Current).SelectMany(l => l.Renderers)
                .Where(r => r != null && !(lods.ShellHidden && lods.Shell.Contains(r))).All(r => r.enabled),
                "regenerated current LOD shows all original model parts on this Player");
            Check(rig.Unit.SectionItems.Select((items, i) => items.SequenceEqual(rig.Items[i])).All(x => x) &&
                rig.Unit.HeatSinks == rig.HeatSinks && rig.Unit.SpeedFactor == 1f && !rig.Unit.LegLost,
                "regeneration restores original critical inventories cooling and mobility on this Player");
            var movement = rig.Unit.GetComponent<Mw2MechController>();
            Check(movement.JumpMp == rig.JumpMp && !movement.JumpDisabled && !movement.FallenOver &&
                rig.Unit.PoweringUp && rig.Unit.PowerState <= 1,
                "regeneration restores jets upright physics and normal power-up delay on this Player");
        }

        void Check(bool ok, string what)
        {
            _results.Add((ok ? "PASS " : "FAIL ") + what);
            Debug.Log("MW2 net test: " + (ok ? "PASS " : "FAIL ") + what);
            if (!ok) _failed++;
        }

        void LeaveFromPausedWindow()
        {
            Mw2Pause.MainMenuOpen = true;
            Mw2Input.WindowOpen = true;
            Mw2Pause.Update(false);
            Check(Mw2Pause.PauseMode, "exit fixture starts with a paused cockpit window");
            Mw2Cockpit.MissionLeft?.Invoke(false);
        }

        // Offscreen rendering remains valid for a hidden Player, unlike desktop/backbuffer captures.
        void CheckGroundPixels()
        {
            var director = FindAnyObjectByType<Mw2MissionDirector>();
            var ground = director != null ? director.transform.Find("Ground") : null;
            if (ground == null) { Check(false, "mission ground exists for the graphical regression"); return; }
            int layer = ground.gameObject.layer;
            var obj = new GameObject("Network ground render probe");
            var camera = obj.AddComponent<Camera>(); camera.enabled = false;
            var target = new RenderTexture(64, 64, 24);
            var pixels = new Texture2D(64, 64, TextureFormat.RGB24, false);
            var previous = RenderTexture.active;
            try
            {
                ground.gameObject.layer = 31;
                camera.cullingMask = 1 << 31; camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = Color.magenta; camera.orthographic = true; camera.orthographicSize = 30;
                camera.nearClipPlane = 0.1f; camera.farClipPlane = 200;
                obj.transform.position = ground.position + Vector3.up * 100;
                obj.transform.rotation = Quaternion.Euler(90, 0, 0);
                camera.targetTexture = target; camera.Render();
                RenderTexture.active = target;
                pixels.ReadPixels(new Rect(0, 0, 64, 64), 0, 0); pixels.Apply();
                int visible = pixels.GetPixels32().Count(p => (p.r != 0 || p.g != 0 || p.b != 0) &&
                    !(p.r > 240 && p.g < 10 && p.b > 240));
                Check(visible > 2048, "offscreen ground contains lit texture pixels, not black or empty background: " + visible);
                File.WriteAllBytes(Path.ChangeExtension(_output, "ground.png"), pixels.EncodeToPNG());
            }
            finally
            {
                ground.gameObject.layer = layer; RenderTexture.active = previous;
                camera.targetTexture = null; target.Release(); Destroy(target); Destroy(pixels); Destroy(obj);
            }
        }

        static string Arg(string[] args, string name, string fallback)
        {
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
        }

        IEnumerator Start()
        {
            var args = Environment.GetCommandLineArgs();
            _radarEnabled = !args.Contains("--radar-off");
            _areaEnabled = !args.Contains("--area-damage-off");
            _host = Arg(args, "--role", "host") == "host";
            _port = int.Parse(Arg(args, "--port", "21157"));
            _timeOfDay = byte.Parse(Arg(args, "--time-of-day", "1"));
            _output = Arg(args, "--output", "net-test.txt");
            _missionPrefix = Arg(args, "--mission", "FRE2");
            int seed = int.Parse(Arg(args, "--seed", "12345"));
            UnityEngine.Random.InitState(seed);
            int optionBits = (_radarEnabled ? 0x3d : 0x1d) & (_areaEnabled ? 0xff : ~8);
            Debug.Log($"MW2 net test configuration: mission {_missionPrefix}, seed {seed}, COMMANDO/CMD00STD, network options 0x{optionBits:x2}, temperature 70, gravity 0.0148");
            bool backgroundAtStartup = Application.runInBackground;
            Application.runInBackground = false;
            typeof(Mw2NetShell).GetMethod("Awake", System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic).Invoke(Mw2NetShell.Instance, null);
            Check(backgroundAtStartup && Application.runInBackground,
                "normal MercNet startup owns background processing without a test-only override");
            Application.targetFrameRate = 60;
            yield return null;
            var routine = args.Contains("--three-players") ? RunThree() : Run();
            float deadline = Time.realtimeSinceStartup + 420f;
            while (true)
            {
                object current = null;
                bool more;
                try { more = routine.MoveNext(); current = routine.Current; }
                catch (Exception error)
                {
                    Check(false, "exception " + error);
                    break;
                }
                if (!more) break;
                if (Time.realtimeSinceStartup > deadline) { Check(false, "overall deadline"); break; }
                yield return current;
            }
            if (_failed == 0)
            {
                yield return WaitFor(() => Mw2NetShell.Instance.ScoresVisible, 10f, "scores page waits for simulation teardown");
                Check(Mw2Cockpit.Player == null && Mw2MechCombat.All.Count == 0 &&
                    FindAnyObjectByType<Mw2MissionDirector>() == null, "scores page has no detached player, cockpit or mission");
                Check(!Mw2MechCombat.NetGame && !Mw2Input.WindowOpen && !Mw2Input.MenuMode &&
                    !Mw2Pause.PauseMode && !Mw2Pause.MainMenuOpen && Time.timeScale == 1f,
                    "mission exit releases cockpit pause and input ownership");
            }
            Check(_failed == 0, "no failed checks");
            File.WriteAllText(_output, string.Join("\n", _results) + "\n");
            Application.Quit(_failed == 0 ? 0 : 1);
        }

        /// <summary>What stands on the line of fire, for a run in which shots do not land.</summary>
        static void LogLine(Mw2MechCombat from, Mw2MechCombat to, string where)
        {
            Vector3 a = from.transform.position + Vector3.up * 5f, b = to.transform.position + Vector3.up * 5f;
            var hits = Physics.RaycastAll(a, (b - a).normalized, (b - a).magnitude).OrderBy(h => h.distance).Take(4).Select(h => $"{h.collider.name} at {h.distance:0.0}");
            Debug.Log($"MW2 net test: line {where}: {(b - a).magnitude:0.0} m: {string.Join(", ", hits)}");
        }

        IEnumerator WaitFor(Func<bool> condition, float seconds, string what)
        {
            float until = Time.realtimeSinceStartup + seconds;
            while (!condition() && Time.realtimeSinceStartup < until) yield return null;
            Check(condition(), what);
        }

        // Three independent OS Players exercise the mesh through the regular shell/game.
        // Test-only placement and aim; damage, KI, DA, regeneration and departures use game paths.
        IEnumerator RunThree()
        {
            var shell = GetComponent<Mw2NetShell>();
            shell.LoadData();
            string role = Arg(Environment.GetCommandLineArgs(), "--role", "host");
            string name = _host ? "alpha" : role == "join1" ? "bravo" : "charlie";
            int mission = shell.Missions.ToList().FindIndex(m => m.Prefix.Equals("FRE2", StringComparison.OrdinalIgnoreCase));
            int chassis = shell.MechTable.Find("commando");
            Check(mission >= 0 && chassis >= 0, "three-player FRE2 and stock Commando available");
            shell.SelectMech(chassis, 0);
            shell.SelectTimeOfDay(_timeOfDay);
            if (_host) shell.HostRoom(name, _port, mission, false, 3, "", 0x3d);
            else shell.JoinRoom(name, new IPEndPoint(IPAddress.Loopback, _port));
            var session = shell.Session;
            yield return WaitFor(() => session.Net.MyId != NetTransport.NoId && session.Net.NumPlayers == 3 && session.Room.Players.Count == 3,
                40f, "three independent Players joined the same room");
            shell.SelectMech(chassis, 0);
            yield return WaitFor(() => session.Room.Players.All(p => p.Chassis == chassis && p.Variant == 0), 15f, "three stock loadouts reach room status");
            session.Chat("mesh " + name);
            yield return WaitFor(() => new[] { "alpha", "bravo", "charlie" }.All(n => session.ChatLines.Contains(n + ": mesh " + n)),
                15f, "lobby chat reaches both peers including client-to-client");
            session.SetReady(true);
            yield return WaitFor(() => shell.Game != null, 90f, "all three launch via normal ready handshake");
            var game = shell.Game;
            if (game == null) yield break;
            Check(game.Units.Length == 3 && game.Units.All(u => u != null), "three game units created");
            CheckTimeOfDay(session);
            int alpha = Array.FindIndex(game.Ids, id => game.Net.PlayerName(id) == "alpha");
            int bravo = Array.FindIndex(game.Ids, id => game.Net.PlayerName(id) == "bravo");
            int charlie = Array.FindIndex(game.Ids, id => game.Net.PlayerName(id) == "charlie");
            Check(alpha >= 0 && bravo >= 0 && charlie >= 0 && alpha < bravo && bravo < charlie, "room and game preserve ordered identities");
            Check(game.Units.Count(u => u.IsPlayer) == 1 && game.Units.Where((u, i) => i != game.Me).All(u => u.NetRemote && !u.TryGetComponent<Mw2MechAI>(out _)),
                "one local mech and two remote mechs without AI");
            Check(game.Units.Select(u => u.Team).Distinct().Count() == 3, "free-for-all gives all three distinct teams");
            yield return WaitFor(() => game.Started && game.ReceivedStates > 6, 30f, "three-way LA/GO and DA traffic");
            var mine = game.Units[game.Me];
            var bravoRig = CaptureRig(game.Units[bravo]);
            mine.TryGetComponent<Mw2MechController>(out var control);
            control.KeyboardControl = false; control.Throttle = 0; control.Turn = 0;
            yield return WaitFor(() => mine.PowerState == 2, 25f, "own mech powered up normally");
            if (mine.TryGetComponent<Mw2TorsoTwist>(out var twist)) { twist.SetTwistImmediate(0); twist.PitchTarget = 0; }
            StartCoroutine(ObserveDamage(mine));
            if (game.Me == charlie)
            {
                var p = mine.transform.position + new Vector3(700, 0, 700);
                control.NetSetPose((int)(p.x / PolyModel.UnitsToMeters), (int)(p.y / PolyModel.UnitsToMeters), (int)(p.z / PolyModel.UnitsToMeters), 0);
            }
            game.Chat("loaded " + name);
            yield return WaitFor(() => new[] { "alpha", "bravo", "charlie" }.Where(n => n != name).All(n => game.Messages.Contains(n + ": loaded " + n)),
                15f, "battle chat reaches both other Players");
            int health = mine.Front.Sum() + mine.Rear.Sum() + mine.Internal.Sum();
            if (_host)
            {
                var victim = game.Units[bravo];
                var p = victim.transform.position + new Vector3(0, 0, -FiringDistance);
                control.NetSetPose((int)(p.x / PolyModel.UnitsToMeters), (int)(p.y / PolyModel.UnitsToMeters), (int)(p.z / PolyModel.UnitsToMeters), 0);
                yield return new WaitForSecondsRealtime(1f);
                int fired = 0; bool cooling = false;
                float until = Time.realtimeSinceStartup + 200f;
                while (!victim.Destroyed && Time.realtimeSinceStartup < until)
                {
                    Vector3 to = victim.transform.position - mine.transform.position; to.y = 0;
                    p = mine.transform.position;
                    control.NetSetPose((int)(p.x / PolyModel.UnitsToMeters), (int)(p.y / PolyModel.UnitsToMeters), (int)(p.z / PolyModel.UnitsToMeters), Quaternion.LookRotation(to).eulerAngles.y);
                    mine.Target = victim;
                    if (mine.Heat >= 65) cooling = true; else if (mine.Heat <= 45) cooling = false;
                    if (!cooling && mine.PowerState == 2) fired += mine.NetFireAll();
                    yield return null;
                }
                Check(fired > 0 && victim.Destroyed, "real host shots destroy bravo with third Player present");
                Check(!mine.Destroyed && health == mine.Front.Sum() + mine.Rear.Sum() + mine.Internal.Sum(), "host stays undamaged in controlled shooting fixture");
            }
            else yield return WaitFor(() => game.Units[bravo].Destroyed, 230f, "bravo death observed through owner state");
            yield return WaitFor(() => game.Kills[alpha, bravo] == 1, 15f, "owner KI kill column reaches every Player");
            Check(game.Scores[alpha] == 1 && game.Scores[bravo] == 0 && game.Scores[charlie] == 0, "all three see identical scores");
            Check(game.Messages.Contains("'bravo' destroyed by 'alpha'."), "all three see the same killer and victim");
            if (game.Me == charlie) Check(!mine.Destroyed && health == mine.Front.Sum() + mine.Rear.Sum() + mine.Internal.Sum(), "third observer takes no damage");
            if (game.Me == bravo)
            {
                yield return WaitFor(() => game.WaitingToRegenerate, 15f, "normal death delay offers regeneration");
                game.RegenerateNow();
            }
            yield return WaitFor(() => !game.Units[bravo].Destroyed, 25f, "bravo regeneration reaches all three Players");
            CheckRegeneratedRig(bravoRig);
            Check(game.GetComponent<Mw2MissionDirector>().MissionResult == Mw2MissionDirector.Result.Running, "FFA continues after kill and regeneration");
            if (_host)
            {
                yield return new WaitForSecondsRealtime(2f);
                LeaveFromPausedWindow();
                yield return WaitFor(() => shell.LastScores.Contains("alpha killed bravo once."), 10f, "host exits to genuine score page");
                yield return new WaitForSecondsRealtime(4f);
            }
            else
            {
                yield return WaitFor(() => game.Messages.Contains("'alpha' exited the game.") && game.Net.NumPlayers == 2, 30f, "both surviving peers receive host departure");
                yield return WaitFor(() => game.Net.HostId == game.Ids[bravo], 15f, "bravo elected host on both surviving Players");
                game.Chat("survivor " + name);
                string peer = game.Me == bravo ? "charlie" : "bravo";
                yield return WaitFor(() => game.Messages.Contains(peer + ": survivor " + peer), 15f, "client-to-client battle traffic survives host migration");
                if (game.Me == bravo)
                {
                    yield return new WaitForSecondsRealtime(2f);
                    LeaveFromPausedWindow();
                    yield return WaitFor(() => shell.LastScores.Contains("alpha killed bravo once."), 10f, "second Player exits with retained score table");
                    yield return new WaitForSecondsRealtime(4f);
                }
                else
                {
                    yield return WaitFor(() => game.Net.IsHost && game.Net.NumPlayers == 1, 30f, "charlie becomes final host");
                    Check(game.Kills[alpha, bravo] == 1 && game.Scores[alpha] == 1, "departures preserve historical kills and score");
                    LeaveFromPausedWindow();
                    yield return WaitFor(() => shell.LastScores.Contains("alpha killed bravo once."), 10f, "third Player exits with retained score table");
                }
            }
        }

        IEnumerator Run()
        {
            var shell = GetComponent<Mw2NetShell>();
            shell.LoadData();
            Check(shell.Missions.Count == 13, "13 missions listed: " + shell.Missions.Count);
            int mission = shell.Missions.ToList().FindIndex(m => string.Equals(m.Prefix, _missionPrefix, StringComparison.OrdinalIgnoreCase));
            Check(mission >= 0, "mission " + _missionPrefix);
            int chassis = shell.MechTable.Find("commando");
            Check(chassis >= 0 && shell.MechTable.Items[chassis].Variants.Contains(0), "commando in the 'Mech table");
            shell.SelectMech(chassis, 0);
            bool team = shell.Missions[mission].Modes[0].Team != 0;
            shell.SelectTimeOfDay(_timeOfDay);
            NetLobbySession session0 = null;
            if (_host)
            {
                int optionBits = _radarEnabled ? 0x3d : 0x1d;
                if (!_areaEnabled) optionBits &= ~8;
                shell.HostRoom("alpha", _port, mission, team, 8, "", (byte)optionBits);
                session0 = shell.Session;
                session0.Room.Temperature = 3;
                session0.Room.Gravity = 8;
            }
            else shell.JoinRoom("bravo", new IPEndPoint(IPAddress.Loopback, _port));
            var session = shell.Session;
            yield return WaitFor(() => session.Net.MyId != NetTransport.NoId && session.Net.NumPlayers == 2, 30f, "both machines in the room");
            shell.SelectMech(chassis, 0);
            yield return WaitFor(() => session.Room.Players.Count == 2, 10f, "room lists two players");
            session.Chat(_host ? "hello from alpha" : "hello from bravo");
            yield return WaitFor(() => session.ChatLines.Any(l => l == (_host ? "bravo: hello from bravo" : "alpha: hello from alpha")), 10f, "lobby chat arrives");
            yield return WaitFor(() => session.Room.Players.All(p => p.Chassis == chassis && p.Variant == 0), 10f, "both 'Mechs in the status");
            Check(session.Room.Mission.Equals(_missionPrefix, StringComparison.OrdinalIgnoreCase), "client sees the host's mission");
            Check(session.Room.TeamGame == team, "room mode follows the mission's limits");
            if (team && !_host)
            {
                session.SetTeam(1);
                yield return WaitFor(() => session.Room.Players[session.Room.Find(session.Net.MyId)].Team == 1, 10f, "client moves to team B");
            }
            else if (team) yield return WaitFor(() => session.Room.Players.Any(p => p.Team == 1), 15f, "host sees team B");
            session.SetReady(true);
            yield return WaitFor(() => shell.Game != null, 90f, "mission launched");
            var game = shell.Game;
            if (game == null) yield break;
            Check(game.Units.Length == 2 && game.Units.All(u => u != null), "two units in room order");
            CheckTimeOfDay(session);
            Check(Mw2Options.GameOptions.AreaDamageToUnits == _areaEnabled && Mw2Effects.AreaDamageToUnits == _areaEnabled,
                "room area-damage option reaches the live effects gate on this Player: " + _areaEnabled);
            Check(game.Ids[0] < game.Ids[1], "units sorted by id");
            Check(game.Units[game.Me] != null && game.Units[game.Me].IsPlayer, "own unit at own index");
            Check(game.Units[1 - game.Me].NetRemote && !game.Units[1 - game.Me].TryGetComponent<Mw2MechAI>(out _), "the other player has no AI");
            Check(game.Units[0].Side != game.Units[1].Side, "the other player is the enemy side");
            yield return WaitFor(() => game.Started, 30f, "LA / GO start together");
            yield return WaitFor(() => game.ReceivedStates > 3 && game.SentStates > 3, 10f, "DA flows both ways");
            var sky = SkyGround.Load(Path.Combine(shell.GameFolder, "SKYGND.PAR"), _missionPrefix);
            var ambient = Shader.GetGlobalColor("_Mw2Ambient");
            Check(sky != null && (sky.Ambient.r > 0 || sky.Ambient.g > 0 || sky.Ambient.b > 0) &&
                Mathf.Abs(ambient.r - sky.Ambient.r / 255f) < 0.0001f &&
                Mathf.Abs(ambient.g - sky.Ambient.g / 255f) < 0.0001f,
                "network world publishes inherited original SKYGND ambient instead of black defaults");
            if (Environment.GetCommandLineArgs().Contains("--capture-visuals"))
            {
                yield return WaitFor(() => Mw2PaletteFade.Instance != null && Mw2PaletteFade.Instance.Fade.Mode != 16,
                    15f, "initial world palette has faded in before the graphical regression");
                CheckGroundPixels();
            }
            var mine = game.Units[game.Me];
            var other = game.Units[1 - game.Me];
            var victimRig = CaptureRig(_host ? other : mine);
            StartCoroutine(ObserveDamage(mine));
            mine.TryGetComponent<Mw2MechController>(out var myController);
            Check(mine.PlanetTemperature == 70 && other.PlanetTemperature == 70, "the room's temperature: " + mine.PlanetTemperature);
            Check(Mathf.Abs(myController.PlanetGravity - 0.0148f) < 1e-4f, "the room's gravity: " + myController.PlanetGravity);
            Check(Mw2Options.GameOptions.PlayerHeat && Mw2Options.GameOptions.LandingDamage && !Mw2Options.GameOptions.UnlimitedAmmo &&
                Mw2Options.GameOptions.RadarKeys == _radarEnabled, "the room's options reach this machine");
            var cockpit = mine.GetComponent<Mw2Cockpit>();
            Check(cockpit != null && cockpit.RadarAvailable == _radarEnabled, "the host radar option controls this player's HUD");
            // The power-up of Mech_InitMovement takes 6 to 8 seconds.
            yield return WaitFor(() => mine.PowerState == 2, 25f, "own mech powered up");
            // This fixture scripts placement/aim. Keep the real physics and power code,
            // but prevent the desktop cursor from overwriting the scripted torso pitch.
            // A dotnet assembly without ENABLE_LEGACY_INPUT_MANAGER masked this input.
            myController.KeyboardControl = false;
            if (mine.TryGetComponent<Mw2TorsoTwist>(out var aimTwist))
            {
                aimTwist.SetTwistImmediate(0);
                aimTwist.PitchTarget = 0;
            }

            if (_host)
            {
                // Beside the client, facing it: from here the shots are real. In a team game the client walks into the open at team A's start.
                Vector3 start = other.transform.position;
                Check((start - mine.transform.position).sqrMagnitude > 1f, "players start apart");
                LogLine(mine, other, "from start to start");
                if (team) Check((start - mine.transform.position).magnitude > 500f, "team starts are far apart: " + (start - mine.transform.position).magnitude);
                // A move only the owner knows: the client must see it through DA.
                Vector3 far = mine.transform.position + new Vector3(250f, 0, 130f);
                myController.NetSetPose((int)(far.x / PolyModel.UnitsToMeters), (int)(far.y / PolyModel.UnitsToMeters), (int)(far.z / PolyModel.UnitsToMeters), 37f);
                yield return new WaitForSecondsRealtime(1f);
                {
                    Vector3 off = mine.transform.position - far; off.y = 0;
                    Check(off.magnitude < 1f, $"the move stays in the owner's physics: {off.magnitude:0.00} m across, height {mine.transform.position.y - far.y:0.00}");
                }
                game.Chat($"at {mine.transform.position.x.ToString(System.Globalization.CultureInfo.InvariantCulture)} {mine.transform.position.z.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
                yield return new WaitForSecondsRealtime(3f);
                start = other.transform.position;
                if (team)
                {
                    yield return WaitFor(() => (other.transform.position - mine.transform.position).magnitude < FiringDistance + 20f, 20f, "client walked over to the host");
                }
                else
                {
                    // Stand clear of the target's missile/death splash. Sixteen metres made
                    // the fixture's unopposed shooter take its own explosion damage.
                    Vector3 place = start + new Vector3(0, 0, -FiringDistance);
                    myController.NetSetPose((int)(place.x / PolyModel.UnitsToMeters), (int)(place.y / PolyModel.UnitsToMeters), (int)(place.z / PolyModel.UnitsToMeters), 0f);
                }
                if (mine.TryGetComponent<Mw2TorsoTwist>(out var twist)) { twist.SetTwistImmediate(0); }
                // CO: a contact only the other machine can turn into damage.
                game.SendCollision(other, new Vector3(0, 0, 1), new Vector3(0, 0, 60f));
                // A building only this machine destroys: the client must see it through SN (FUN_10022495).
                var building = Mw2Destructible.All.FindIndex(d => d != null && !d.Destroyed && d.name.IndexOf("Satellite", StringComparison.OrdinalIgnoreCase) < 0);
                if (building >= 0)
                {
                    Mw2Destructible.All[building].ApplyDamage(float.MaxValue / 4f, mine);
                    Check(Mw2Destructible.All[building].Destroyed, "a building falls on the host");
                    game.Chat("gt " + building);
                }
                int fired = 0;
                var firingVector = other.transform.position - mine.transform.position;
                firingVector.y = 0;
                var firingPosition = mine.transform.position / PolyModel.UnitsToMeters;
                myController.NetSetPose((int)firingPosition.x, (int)firingPosition.y, (int)firingPosition.z,
                    Quaternion.LookRotation(firingVector).eulerAngles.y);
                int shooterHealth = mine.Front.Sum() + mine.Rear.Sum() + mine.Internal.Sum();
                yield return new WaitForSecondsRealtime(0.5f);
                mine.WeaponAimRay(aimTwist, out var aimOrigin, out var aimDirection);
                Check(mine.FirstHit(aimOrigin, aimDirection, 1500f, out var aimHit) &&
                    aimHit.collider.GetComponentInParent<Mw2MechCombat>() == other,
                    $"scripted aim ray reaches the opponent: {aimDirection}");
                float until = Time.realtimeSinceStartup + 240f;
                bool cooling = false;
                int coolingFrames = 0;
                float peakHeat = 0f;
                while (!other.Destroyed && Time.realtimeSinceStartup < until)
                {
                    Vector3 to = other.transform.position - mine.transform.position; to.y = 0;
                    if (to.sqrMagnitude > 1f)
                    {
                        var p = mine.transform.position;
                        myController.NetSetPose((int)(p.x / PolyModel.UnitsToMeters), (int)(p.y / PolyModel.UnitsToMeters), (int)(p.z / PolyModel.UnitsToMeters), Quaternion.LookRotation(to).eulerAngles.y);
                    }
                    mine.Target = other;
                    // Test pilot policy, not a game rule. NetFireAll bypasses the player's
                    // power/heat gate; firing it every frame can kill the unopposed host by heat.
                    peakHeat = Mathf.Max(peakHeat, mine.Heat);
                    if (mine.Heat >= 65f) cooling = true;
                    else if (mine.Heat <= 45f) cooling = false;
                    if (cooling) coolingFrames++;
                    if (!cooling && mine.PowerState == 2) fired += mine.NetFireAll();
                    yield return null;
                }
                Check(fired > 0, "host fired bursts: " + fired);
                Check(peakHeat < 100f, $"test gunner stays below destructive heat: {peakHeat:0.0}, cooling frames {coolingFrames}");
                Check(!mine.Destroyed && mine.Front.Sum() + mine.Rear.Sum() + mine.Internal.Sum() == shooterHealth,
                    "the unopposed shooter takes no damage during the firing phase");
                Check(other.Destroyed, "client's mech destroyed, seen on the host via DA");
                Check(game.Messages.Any(m => m == "'bravo' destroyed by 'alpha'."), "kill message: " + string.Join(" | ", game.Messages));
                yield return WaitFor(() => game.Kills[game.Me, 1 - game.Me] == 1, 10f, "KI reached the host");
                Check(game.Scores[game.Me] == 1 && game.Scores[1 - game.Me] == 0, $"host's score {game.Scores[game.Me]}, client's {game.Scores[1 - game.Me]}");
                var director = FindAnyObjectByType<Mw2MissionDirector>();
                if (team)
                {
                    // BAS1: the destroy order on en01star opens the mission-success order (0x10000); the kill itself wins.
                    yield return WaitFor(() => director.MissionResult == Mw2MissionDirector.Result.Success, 15f, "killing the enemy team wins");
                    Check(game.WinnerIndex == game.Me, "the host is recorded as winner");
                    yield return WaitFor(() => shell.LastScores.Length > 0, 60f, "the exit sequence ends in the scores");
                    Check(shell.LastScores.Contains("alpha successfully completed the mission!"), "winner line: " + shell.LastScores);
                }
                else
                {
                    // FRE2: the second order waits on itself and no order ends the mission; players fight until they leave.
                    Check(director != null && director.MissionResult == Mw2MissionDirector.Result.Running, "a kill does not end a free-for-all");
                    yield return WaitFor(() => !other.Destroyed, 30f, "client regenerated, seen on the host");
                    CheckRegeneratedRig(victimRig);
                    Check(game.Messages.Any(m => m == "'bravo' resurrected."), "resurrected message");
                    Check(other.Internal.Sum() > 0, "the host sees the regenerated structure through DA");
                    Check(!mine.Destroyed, "host's own mech untouched");
                    LeaveFromPausedWindow();
                    yield return WaitFor(() => shell.LastScores.Length > 0, 10f, "scores written");
                    Check(shell.LastScores.Contains("This mission has not yet been completed."), "no winner line: " + shell.LastScores);
                }
                Check(shell.LastScores.Contains("alpha killed bravo once."), "score text: " + shell.LastScores);
                yield return new WaitForSecondsRealtime(2f);
            }
            else
            {
                int startFront = mine.Front.Sum() + mine.Internal.Sum();
                // The host's announced position, compared with what DA has made of its mech here.
                yield return WaitFor(() => game.Messages.Any(m => m.StartsWith("alpha: at ")), 40f, "host announces its position");
                var announced = game.Messages.First(m => m.StartsWith("alpha: at ")).Split(' ');
                float ax = float.Parse(announced[2], System.Globalization.CultureInfo.InvariantCulture), az = float.Parse(announced[3], System.Globalization.CultureInfo.InvariantCulture);
                yield return WaitFor(() => Mathf.Abs(other.transform.position.x - ax) < 2f && Mathf.Abs(other.transform.position.z - az) < 2f, 5f, "the host's position arrives through DA and survives this machine's physics");
                yield return new WaitForSecondsRealtime(1f);
                Check(Mathf.Abs(other.transform.position.x - ax) < 2f && Mathf.Abs(other.transform.position.z - az) < 2f, $"still there a second later: {other.transform.position} vs {ax} {az}");
                Check(Mathf.Abs(Mathf.DeltaAngle(other.transform.eulerAngles.y, 37f)) < 1f, "heading arrives: " + other.transform.eulerAngles.y);
                if (team)
                {
                    Vector3 place = other.transform.position + new Vector3(0, 0, FiringDistance);
                    myController.NetSetPose((int)(place.x / PolyModel.UnitsToMeters), (int)(place.y / PolyModel.UnitsToMeters), (int)(place.z / PolyModel.UnitsToMeters), 0f);
                }
                float armourLow = float.MaxValue;
                float until = Time.realtimeSinceStartup + 270f;
                float nextLog = 0f;
                while (!mine.Destroyed && Time.realtimeSinceStartup < until)
                {
                    armourLow = Mathf.Min(armourLow, mine.Front.Sum() + mine.Internal.Sum());
                    if (Time.realtimeSinceStartup > nextLog)
                    {
                        nextLog = Time.realtimeSinceStartup + 4f;
                        Debug.Log($"MW2 net test: client view: distance {(other.transform.position - mine.transform.position).magnitude:0.0}, other power {other.PowerState}, shutdown {other.ShutDown}, fire orders {other.NetFireOrders}, weapons {string.Join("", other.Weapons.Select(w => w.State))}, my side {mine.Side} other {other.Side}, target {(other.Target != null ? other.Target.DisplayName : "none")}, active {other.gameObject.activeSelf}");
                    }
                    yield return null;
                }
                Check(game.ReceivedCollisions == 1, "CO received: " + game.ReceivedCollisions);
                var gtLine = game.Messages.FirstOrDefault(m => m.StartsWith("alpha: gt "));
                Check(gtLine != null || Mw2Destructible.All.Count == 0, "host names the building it destroyed");
                if (gtLine != null)
                {
                    int building = int.Parse(gtLine.Substring("alpha: gt ".Length));
                    yield return WaitFor(() => Mw2Destructible.All[building].Destroyed, 10f, "the building falls here through SN");
                }
                Check(armourLow < startFront, $"host's shots damaged the client's mech: {startFront} -> {armourLow}");
                Check(mine.Destroyed, "own mech destroyed");
                Check(game.Kills[1 - game.Me, game.Me] == 1, "kill counted against the killer");
                Check(game.Scores[1 - game.Me] == 1, "killer's score");
                Check(game.Messages.Any(m => m == "'bravo' destroyed by 'alpha'."), "own death message: " + string.Join(" | ", game.Messages));
                var director = FindAnyObjectByType<Mw2MissionDirector>();
                if (team)
                {
                    yield return WaitFor(() => game.Messages.Any(m => m.StartsWith("'alpha' successful")), 30f, "SU received");
                    yield return WaitFor(() => director.MissionResult == Mw2MissionDirector.Result.Failure, 15f, "this team's mission fails");
                    yield return WaitFor(() => shell.LastScores.Length > 0, 60f, "the exit sequence ends in the scores");
                    Check(shell.LastScores.Contains("alpha successfully completed the mission!"), "winner line: " + shell.LastScores);
                    Check(shell.LastScores.Contains("alpha killed bravo once."), "score text: " + shell.LastScores);
                }
                else
                {
                    yield return WaitFor(() => game.WaitingToRegenerate, 15f, "regeneration offered after 0x43e ticks");
                    game.RegenerateNow();
                    Check(!mine.Destroyed && mine.Internal.Sum() > 0, "regenerated");
                    CheckRegeneratedRig(victimRig);
                    yield return WaitFor(() => game.Messages.Any(m => m == "'alpha' exited the game."), 80f, "SS received");
                    Check(other.Destroyed, "the host's mech is gone after SS");
                    yield return WaitFor(() => game.Net.IsHost, 25f, "client takes over as host");
                    LeaveFromPausedWindow();
                    yield return WaitFor(() => shell.LastScores.Length > 0, 10f, "scores written");
                }
                yield return new WaitForSecondsRealtime(1.5f);
            }
        }

        // TS room byte and World_LoadChunks10070e92's INIT override must reach actual built palette state.
        void CheckTimeOfDay(NetLobbySession session)
        {
            Check(session.Room.TimeOfDay == _timeOfDay, "host time selection reaches room status on this Player: " + _timeOfDay);
            var palette = FindAnyObjectByType<Mw2PaletteFade>();
            var start = typeof(Mw2PaletteFade).GetField("_startPeriod", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Check(palette != null && (int)start.GetValue(palette) == _timeOfDay,
                "host time selection reaches actual mission palette initialization on this Player");
        }

        // Test telemetry only: retain the cause of an unexpected fixture death.
        IEnumerator ObserveDamage(Mw2MechCombat unit)
        {
            int previous = unit.Front.Sum() + unit.Rear.Sum() + unit.Internal.Sum();
            bool destroyed = unit.Destroyed;
            while (unit != null)
            {
                int health = unit.Front.Sum() + unit.Rear.Sum() + unit.Internal.Sum();
                if (health != previous || unit.Destroyed != destroyed)
                {
                    unit.TryGetComponent<Mw2MechController>(out var controller);
                    Debug.Log($"MW2 net damage trace: {previous}->{health}, position {unit.transform.position}, velocity {controller.ImpactVelocity}, heat {unit.Heat:0.0}, power {unit.PowerState}, dead {unit.Destroyed}");
                    previous = health;
                    destroyed = unit.Destroyed;
                }
                yield return null;
            }
        }
    }
}
