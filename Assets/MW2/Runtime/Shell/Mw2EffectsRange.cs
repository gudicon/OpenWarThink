using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using MW2.Formats;
using UnityEngine;

namespace MW2.Shell
{
    /// <summary>
    /// The weapon effects test range, opened from Options -> Mods (TEST RANGE): a mission where the other units stand still
    /// as targets (up to three of them placed 100 to 220 metres in front of the player), the player cannot be hurt and every weapon of the player's mech is one test weapon that needs no ammunition.
    /// Ctrl+Shift+] and Ctrl+Shift+[ step through all weapon types; firing works as usual. Effect files of the mods reload
    /// while the range runs (Mw2WeaponLooks), so a modder edits a file, saves it and fires again. Leaving the mission from the
    /// cockpit menu returns to the shell. Ctrl+Shift+F fires the selected weapon on its own, again and again. Ctrl+Shift+K
    /// destroys the nearest standing target at once, for looking at unit destruction looks (effects/mechs).
    /// Command line: --effects-range opens the range as soon as the game files are open; --effects-range-weapon NAME
    /// starts with that weapon; --effects-range-capture FOLDER also fires on its own, saves a few pictures of the game
    /// into FOLDER and quits (for checking a look without a person at the keyboard).
    /// Port addition with no counterpart in the original program.
    /// </summary>
    public class Mw2EffectsRange : MonoBehaviour
    {
        /// <summary>The range's mission and mech (the same as the automatic mission tests use).</summary>
        const string Mission = "BET3", Mech = "COMMANDO", Mek = "CMD00STD";

        Mw2Shell _shell;
        uint _backMsg, _backParam;     // the shell screen the range was opened over (+0x23c7 / +0x23c3)
        Mw2MechCombat _player;
        List<WeaponDef> _weapons;
        int _index;
        byte[] _savedOptions;
        GUIStyle _style, _shadow;
        bool _autoFire;
        float _nextShot;
        int _shots;

        public static bool Running { get; private set; }

        /// <summary>Builds the range mission and hands the screen to it.</summary>
        public static void Open(Mw2Shell shell)
        {
            if (Running) return;
            shell.StartCoroutine(Launch(shell));
        }

        static IEnumerator Launch(Mw2Shell shell)
        {
            Running = true;
            Mw2WeaponLooks.Say("Building the test range ...", false);
            yield return null;
            yield return null;
            GameObject root = null;
            string problem = null;
            List<WeaponDef> weapons = null;
            try
            {
                weapons = WeaponTable.Get(shell.GameFolder).Where(w => !string.IsNullOrEmpty(w.Name) && !w.IsAms).ToList();
                if (shell.Prj == null) problem = "the game files are not open";
                else if (!shell.Prj.TryFind("BWD", Mission + "SCN1", out _)) problem = "the mission " + Mission + " is missing from MW2.PRJ";
                else root = Mw2MissionBuilder.Build(shell.Prj, shell.GameFolder, Mission, Mech.ToLowerInvariant(), 1, Mek);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                problem = e.Message;
            }
            Mw2MissionDirector director = null;
            if (problem == null && (root == null || !root.TryGetComponent(out director) || director.Player == null))
                problem = "the mission did not build";
            if (problem != null || weapons == null || weapons.Count == 0)
            {
                Mw2WeaponLooks.Say("The test range could not start: " + (problem ?? "no weapon table"), true);
                if (root != null) Destroy(root);
                Running = false;
                yield break;
            }
            var range = root.AddComponent<Mw2EffectsRange>();
            range._shell = shell;
            range._backMsg = shell.Career.Msg23C7 != 0 ? shell.Career.Msg23C7 : 0xc02b;
            range._backParam = shell.Career.Msg23C3;
            range._weapons = weapons;
            range._player = director.Player.GetComponent<Mw2MechCombat>();
            range.Begin();
            Mw2Cockpit.MissionLeft = range.Leave;
            shell.enabled = false;
        }

        void Begin()
        {
            // In memory only, put back on leaving: invulnerable (+1) and unlimited ammunition (+0).
            var options = Mw2Options.GameOptions;
            _savedOptions = (byte[])options.Bytes.Clone();
            options.Bytes[0] = 1;
            options.Bytes[1] = 1;
            foreach (var unit in Mw2MechCombat.All)
            {
                if (unit == null || unit == _player) continue;
                foreach (var ai in unit.GetComponents<Mw2MechAI>()) ai.enabled = false;
            }
            PlaceTargets();
            var start = _player != null && _player.Weapons.Count > 0 ? _player.Weapons[0].Def : null;
            _index = Math.Max(0, start != null ? _weapons.FindIndex(w => w.Type == start.Type) : _weapons.FindIndex(w => w.Name == "MLAS"));
            string wanted = Arg("--effects-range-weapon");
            int k = wanted == null ? -1 : _weapons.FindIndex(w => string.Equals(w.Name, wanted, StringComparison.OrdinalIgnoreCase));
            if (k >= 0) _index = k;
            Arm();
            string capture = Arg("--effects-range-capture");
            if (capture != null) StartCoroutine(Capture(capture));
        }

        /// <summary>The three units nearest the player, still and facing it, 100, 160 and 220 metres ahead.</summary>
        void PlaceTargets()
        {
            if (_player == null) return;
            var from = _player.transform.position;
            var ahead = _player.transform.forward;
            ahead.y = 0f;
            if (ahead.sqrMagnitude < 1e-6f) ahead = Vector3.forward;
            ahead.Normalize();
            var side = new Vector3(ahead.z, 0f, -ahead.x);
            var targets = Mw2MechCombat.All
                .Where(u => u != null && u != _player && !u.Destroyed && u.GetComponent<Mw2MechController>() != null)
                .OrderBy(u => (u.transform.position - from).sqrMagnitude).Take(3).ToList();
            for (int i = 0; i < targets.Count; i++)
            {
                var p = from + ahead * (100f + 60f * i) + side * (i == 1 ? 0f : i == 0 ? -12f : 12f);
                p.y = Mw2Ground.Height(p);
                var face = from - p;
                float heading = Mathf.Atan2(face.x, face.z) * Mathf.Rad2Deg;
                var units = p / PolyModel.UnitsToMeters;
                targets[i].GetComponent<Mw2MechController>().NetSetPose(Mathf.RoundToInt(units.x), Mathf.RoundToInt(units.y), Mathf.RoundToInt(units.z), heading);
            }
        }

        static string Arg(string name)
        {
            var args = Environment.GetCommandLineArgs();
            int at = Array.IndexOf(args, name);
            return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void FromCommandLine()
        {
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "--effects-range") < 0 && Arg("--effects-range-capture") == null) return;
            var go = new GameObject("MW2 effects range start") { hideFlags = HideFlags.HideInHierarchy };
            go.AddComponent<Starter>();
        }

        /// <summary>Waits for the shell to open the game files, then opens the range.</summary>
        sealed class Starter : MonoBehaviour
        {
            IEnumerator Start()
            {
                float until = Time.realtimeSinceStartup + 120f;
                Mw2Shell shell = null;
                while (Time.realtimeSinceStartup < until && (shell == null || shell.Prj == null || shell.FolderError != null ||
                       shell.FlicRunning || shell.Career.Msg23C7 != 0xc02b))
                {
                    shell = FindAnyObjectByType<Mw2Shell>();
                    yield return null;
                }
                yield return new WaitForSecondsRealtime(1f);
                if (shell != null && shell.Prj != null) Open(shell);
                else Debug.LogWarning("MW2 effects range: the game files did not open");
                Destroy(gameObject);
            }
        }

        /// <summary>
        /// The capture of --effects-range-capture: for each step of --effects-range-shots (weapon:view:metres, view cockpit or
        /// outside; by default MLAS, MPLAS, SLAS and LLAS from the cockpit and from outside at 40 and 220 metres) the nearest
        /// unit is put on the aim line at that distance, the weapon fires once and four pictures are taken 0.02 to 0.4 s
        /// after the shot leaves, and it fires once more after reloading (logged); then the range is left and the shell screen taken. The mouse is left out meanwhile
        /// (Mw2Input.UseMouse), so it does not turn the torso.
        /// </summary>
        IEnumerator Capture(string folder)
        {
            System.IO.Directory.CreateDirectory(folder);
            // The mouse is left out, so it does not turn the torso (a locked cursor still reports where the pointer is).
            Mw2Input.UseMouse = false;
            // The mech powers up 6 to 8 seconds after the start (Mech_UpdateControl 0x1003a449).
            float until = Time.realtimeSinceStartup + 40f;
            while (_player.PowerState != 2 && Time.realtimeSinceStartup < until) yield return null;
            yield return new WaitForSecondsRealtime(2f);
            string plan = Arg("--effects-range-shots") ?? string.Join(",", new[] { "MLAS", "MPLAS", "SLAS", "LLAS" }
                .SelectMany(w => new[] { $"{w}:cockpit:40", $"{w}:outside:40", $"{w}:cockpit:220", $"{w}:outside:220" }));
            var cockpit = _player.GetComponent<Mw2Cockpit>();
            float[] after = { 0.02f, 0.1f, 0.22f, 0.4f };
            foreach (string step in plan.Split(','))
            {
                var part = step.Split(':');
                if (part.Length == 4 && part[0] == "pair" && float.TryParse(part[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float pairAt))
                {
                    // pair:WEAPON:view:metres - the same weapon fires twice at a target that stands still on the aim line;
                    // the log gives each shot's flight time (in the original the second one, through the slot where the
                    // first stopped at the target, hits at once; with the rules fix it flies as long as the first).
                    int w = _weapons.FindIndex(x => string.Equals(x.Name, part[1], StringComparison.OrdinalIgnoreCase));
                    if (w < 0) { Debug.LogWarning("MW2 effects range: no weapon " + part[1]); continue; }
                    _index = w;
                    Arm();
                    if (cockpit != null) cockpit.CockpitView = part[2] != "outside";
                    yield return new WaitForSecondsRealtime(1.2f);
                    PlaceOnAim(pairAt);
                    yield return null;
                    PlaceOnAim(pairAt);
                    var flights = new List<string>();
                    for (int n = 0; n < 2; n++)
                    {
                        var def = _weapons[w];
                        float give = Time.realtimeSinceStartup + 4f + (float)((def.Burst * def.IntervalTicks + def.RecycleTicks) / MechMotion.TicksPerSecond);
                        bool shotFired = false;
                        while (!shotFired && Time.realtimeSinceStartup < give) { shotFired = _player.FireSelected(); if (!shotFired) yield return null; }
                        float until2 = Time.realtimeSinceStartup + 2f;
                        while (Mw2Projectile.Active.Count == 0 && Time.realtimeSinceStartup < until2) yield return null;
                        var shot = Mw2Projectile.Active.Count > 0 ? Mw2Projectile.Active[0] : null;
                        float left = Time.realtimeSinceStartup;
                        int frames = 0;
                        while (shot != null && Mw2Projectile.Active.Contains(shot) && Time.realtimeSinceStartup - left < 5f) { frames++; yield return null; }
                        flights.Add($"shot {n + 1}: fired {shotFired}, flew {Time.realtimeSinceStartup - left:0.000} s ({frames} frames)");
                        yield return new WaitForSecondsRealtime(0.3f);
                    }
                    Debug.Log($"MW2 effects range: pair {part[1]} {pairAt:0} m, fix instant shots {ShotSlotPool.FreshStart}: " + string.Join("; ", flights));
                    continue;
                }
                if (part.Length == 3 && part[0] == "kill" && float.TryParse(part[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float killAt))
                {
                    // kill:view:metres - the nearest target stands on the aim line and is destroyed; pictures of its end.
                    if (cockpit != null) cockpit.CockpitView = part[1] != "outside";
                    yield return new WaitForSecondsRealtime(1.2f);
                    PlaceOnAim(killAt);
                    yield return null;
                    PlaceOnAim(killAt);
                    var victim = Targets().FirstOrDefault();
                    if (victim == null) { Debug.LogWarning("MW2 effects range: no target left for " + step); continue; }
                    var unitLook = Mw2WeaponLooks.Get(victim);
                    victim.Kill(_player);
                    float killed = Time.realtimeSinceStartup;
                    float[] times = { 0.05f, 0.4f, 1.5f, 4f };
                    for (int i = 0; i < times.Length; i++)
                    {
                        while (Time.realtimeSinceStartup - killed < times[i]) yield return null;
                        string name = $"kill_{part[1]}_{killAt:0}m_{i}.png";
                        ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(folder, name));
                        Debug.Log($"MW2 effects range: {name}: {UnitName(victim)} destroyed {victim.Destroyed}, look " +
                                  (unitLook != null ? Mw2WeaponLooks.Shown(unitLook.Source) : "original") +
                                  $", mod lights {FindObjectsByType<Mw2ModLight>(FindObjectsSortMode.None).Length}, " +
                                  $"mod prefabs {FindObjectsByType<Mw2ModPrefab>(FindObjectsSortMode.None).Length}");
                    }
                    // A picture is written at the end of its frame: let it be written before the range moves on or is left.
                    yield return null;
                    yield return null;
                    continue;
                }
                int k = _weapons.FindIndex(w => string.Equals(w.Name, part[0], StringComparison.OrdinalIgnoreCase));
                if (part.Length != 3 && !(part.Length == 4 && part[3] == "hit") || k < 0 || !float.TryParse(part[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float metres))
                {
                    Debug.LogWarning("MW2 effects range: cannot read the step " + step);
                    continue;
                }
                _index = k;
                Arm();
                if (cockpit != null) cockpit.CockpitView = part[1] != "outside";
                yield return new WaitForSecondsRealtime(1.2f);
                PlaceOnAim(metres);
                yield return null;
                PlaceOnAim(metres);
                bool started = _player.FireSelected();
                // The first shot of a burst leaves after the weapon's interval (+0x58, 60 ticks for most lasers): time
                // the pictures from the moment it is out.
                float wait = Time.realtimeSinceStartup + 1.5f;
                while (Mw2Projectile.Active.Count == 0 && Time.realtimeSinceStartup < wait) yield return null;
                float fired = Time.realtimeSinceStartup;
                // WEAPON:view:metres:hit - the pictures are timed from the moment the first shot ends (slow missiles).
                if (part.Length == 4 && part[3] == "hit")
                {
                    var first = Mw2Projectile.Active.Count > 0 ? Mw2Projectile.Active[0] : null;
                    while (first != null && Mw2Projectile.Active.Contains(first) && Time.realtimeSinceStartup - fired < 8f) yield return null;
                    fired = Time.realtimeSinceStartup;
                }
                for (int i = 0; i < after.Length; i++)
                {
                    while (Time.realtimeSinceStartup - fired < after[i]) yield return null;
                    string name = $"{part[0]}_{part[1]}_{metres:0}m_{i}.png";
                    ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(folder, name));
                    Debug.Log($"MW2 effects range: {name}: fired {started}, {Mw2Projectile.Active.Count} shot(s) in flight, " +
                              $"lasers {FindObjectsByType<Mw2ModLaser>(FindObjectsSortMode.None).Length}, " +
                              $"mod lights {FindObjectsByType<Mw2ModLight>(FindObjectsSortMode.None).Length}, " +
                              $"mod prefabs {FindObjectsByType<Mw2ModPrefab>(FindObjectsSortMode.None).Length}, " +
                              $"mouse used {Mw2Input.UseMouse}");
                }
                yield return new WaitForSecondsRealtime(0.6f);
                // The same weapon fires again once its burst and reload are over (1006bc57: +0x2C shots every +0x58
                // ticks, then +0x54 ticks), without being chosen anew.
                var fired1 = _weapons[k];
                float reload = Time.realtimeSinceStartup + 2f + (float)((fired1.Burst * fired1.IntervalTicks + fired1.RecycleTicks) / MechMotion.TicksPerSecond);
                bool again = false;
                while (!again && Time.realtimeSinceStartup < reload)
                {
                    again = _player.FireSelected();
                    if (!again) yield return null;
                }
                Debug.Log($"MW2 effects range: {part[0]} fired again without re-arming: {again}");
                yield return new WaitForSecondsRealtime(0.6f);
            }
            Mw2Input.UseMouse = true;
            if (cockpit != null) cockpit.CockpitView = true;
            // Leave as the cockpit menu does and take the shell screen it returns to; the range object goes, so the shell
            // runs the rest.
            var shell = _shell;
            shell.StartCoroutine(AfterLeave(shell, folder));
            Leave(false);
        }

        /// <summary>For the capture: the nearest unit stands on the aim line <paramref name="metres"/> ahead, the others aside.</summary>
        void PlaceOnAim(float metres)
        {
            _player.WeaponAimRay(_player.GetComponent<Mw2TorsoTwist>(), out var eye, out var aim);
            var others = Mw2MechCombat.All.Where(u => u != null && u != _player && !u.Destroyed && u.GetComponent<Mw2MechController>() != null)
                .OrderBy(u => (u.transform.position - _player.transform.position).sqrMagnitude).ToList();
            for (int i = 0; i < others.Count; i++)
            {
                var p = i == 0 ? eye + aim.normalized * metres - Vector3.up * 4f
                               : _player.transform.position + _player.transform.right * (300f + 30f * i);
                if (i > 0) p.y = Mw2Ground.Height(p);
                var face = _player.transform.position - p;
                var units = p / PolyModel.UnitsToMeters;
                others[i].GetComponent<Mw2MechController>().NetSetPose(Mathf.RoundToInt(units.x), Mathf.RoundToInt(units.y), Mathf.RoundToInt(units.z),
                    Mathf.Atan2(face.x, face.z) * Mathf.Rad2Deg);
            }
        }

        static IEnumerator AfterLeave(Mw2Shell shell, string folder)
        {
            yield return new WaitForSecondsRealtime(3f);
            ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(folder, "shell.png"));
            yield return new WaitForSecondsRealtime(1f);
            Debug.Log($"MW2 effects range: pictures saved to {folder}; back in the shell on screen 0x{shell.Career.Msg23C7:x}");
            Application.Quit();
        }

        /// <summary>
        /// Every weapon of the player's mech becomes the chosen test weapon. One that needs ammunition draws on a pool of
        /// its own whose count is -1, which Weapon_FireProjectile 0x1006c554 never takes rounds from (AmmoStore.Consume);
        /// with no pool at all it would show 0 rounds after its first shot and not be ready again.
        /// </summary>
        void Arm()
        {
            if (_player == null) return;
            var def = _weapons[_index];
            var store = _player.Ammunition;
            int endless = store.Pools.FindIndex(p => p.Type == -1 && p.Total == -1);
            if (endless < 0)
            {
                endless = store.Pools.Count;
                store.Pools.Add(new AmmoStore.Pool { Type = -1, Total = -1 });
            }
            foreach (var w in _player.Weapons)
            {
                w.Def = def;
                w.Ammo = -1;
                w.Pool = def.UsesAmmo ? endless : AmmoStore.NoPool;
                w.Broken = false;
                w.State = 1;
                w.Repeat = false;
            }
            var look = Mw2WeaponLooks.Get(def);
            Mw2WeaponLooks.Say($"Test weapon: {def.Name} — " + (look != null ? "mod look " + Shown(look.Source) : "original look"), false);
        }

        static string Shown(string path) => path == null ? "" : System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(
            System.IO.Path.GetDirectoryName(path)))) + "/" + WeaponLook.Folder + "/" + System.IO.Path.GetFileName(path);

        void Update()
        {
#if ENABLE_LEGACY_INPUT_MANAGER
            bool chord = (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)) &&
                         (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift));
            if (_autoFire && _player != null && Time.time >= _nextShot)
            {
                if (_player.FireSelected()) { _nextShot = Time.time + 0.6f; _shots++; }
            }
            if (!chord) return;
            if (Input.GetKeyDown(KeyCode.F))
            {
                _autoFire = !_autoFire;
                Mw2WeaponLooks.Say(_autoFire ? "Firing on its own (Ctrl+Shift+F stops)" : "Stopped firing on its own", false);
            }
            if (Input.GetKeyDown(KeyCode.K)) KillNearest();
            int step = Input.GetKeyDown(KeyCode.RightBracket) ? 1 : Input.GetKeyDown(KeyCode.LeftBracket) ? -1 : 0;
            if (step == 0) return;
            _index = (_index + step + _weapons.Count) % _weapons.Count;
            Arm();
#endif
        }

        /// <summary>The standing targets, nearest first.</summary>
        List<Mw2MechCombat> Targets() => _player == null ? new List<Mw2MechCombat>() :
            Mw2MechCombat.All.Where(u => u != null && u != _player && !u.Destroyed)
                .OrderBy(u => (u.transform.position - _player.transform.position).sqrMagnitude).ToList();

        /// <summary>Ctrl+Shift+K: the nearest standing target is destroyed by the player, as if shot.</summary>
        void KillNearest()
        {
            var targets = Targets();
            if (targets.Count == 0)
            {
                Mw2WeaponLooks.Say("No targets left: leave through the cockpit menu and open the test range again", true);
                return;
            }
            var unit = targets[0];
            var look = Mw2WeaponLooks.Get(unit);
            targets[0].Kill(_player);
            Mw2WeaponLooks.Say($"Destroyed {UnitName(unit)}: " + (look != null ? "mod look " + Mw2WeaponLooks.Shown(look.Source) : "original look")
                               + $"; {targets.Count - 1} target{(targets.Count == 2 ? "" : "s")} left", false);
        }

        static string UnitName(Mw2MechCombat u) => string.IsNullOrEmpty(u.ModelName) ? u.DisplayName : u.ModelName;

        void OnGUI()
        {
            if (Event.current.type != EventType.Repaint || _weapons == null) return;
            int size = Mathf.Clamp(Screen.height / 50, 11, 24);
            if (_style == null || _style.fontSize != size)
            {
                _style = new GUIStyle(GUI.skin.label) { fontSize = size, alignment = TextAnchor.UpperRight, normal = { textColor = new Color32(236, 236, 236, 255) } };
                _shadow = new GUIStyle(_style) { normal = { textColor = Color.black } };
            }
            var def = _weapons[_index];
            var look = Mw2WeaponLooks.Get(def);
            var target = Targets().FirstOrDefault();
            var unitLook = target != null ? Mw2WeaponLooks.Get(target) : null;
            string text = $"TEST RANGE   {def.Name}   ({string.Join(" / ", WeaponLook.Keys(def.Name, def.ProjectileType).Skip(1))})\n" +
                          (look != null ? "mod look: " + Shown(look.Source) : "original look (no effect file in the active mods)") +
                          (target != null ? $"\nnearest target {UnitName(target)} ({string.Join(" / ", MechLook.Keys(target.ModelName, target.Tons, target.GamepieceType))}): " +
                                            (unitLook != null ? "mod look " + Mw2WeaponLooks.Shown(unitLook.Source) : "original destruction") : "\nno targets left") +
                          (Mw2ModRules.Current.FixInstantShots ? "\nfix: instant second shots fixed by " + Mw2WeaponLooks.Shown(Mw2ModRules.Current.Source) : "") +
                          "\nCtrl+Shift+[ / ] other weapon   ·   Ctrl+Shift+F fire on its own   ·   Ctrl+Shift+K destroy the nearest target" +
                          "\nleave through the cockpit menu";
            var content = new GUIContent(text);
            float width = Screen.width * 0.6f, h = _style.CalcHeight(content, width);
            // Bottom right, above the cockpit's own weapon list at the top right.
            var r = new Rect(Screen.width - width - 10f, Screen.height * 0.62f - h, width, h);
            GUI.depth = -900;
            _shadow.Draw(new Rect(r.x + 2f, r.y + 2f, r.width, r.height), content, false, false, false, false);
            _style.Draw(r, content, false, false, false, false);
        }

        /// <summary>Mw2Cockpit.MissionLeft: back to the shell screen it was opened over (Flee to Windows still quits the game).</summary>
        void Leave(bool fled)
        {
            Mw2Cockpit.MissionLeft = null;
            if (_savedOptions != null) Array.Copy(_savedOptions, Mw2Options.GameOptions.Bytes, _savedOptions.Length);
            Running = false;
            if (fled)
            {
#if UNITY_EDITOR
                UnityEditor.EditorApplication.isPlaying = false;
#else
                Application.Quit();
#endif
                return;
            }
            if (TryGetComponent<Mw2MissionDirector>(out var director) && director.Player != null) Destroy(director.Player.gameObject);
            Destroy(gameObject);
            var cam = Camera.main;
            if (cam != null)
            {
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = Color.black;
            }
            _shell.enabled = true;
            _shell.FlushKeys();
            // Back to the screen the range was opened over, set up afresh: its animations are dropped first, as
            // the hangar (0xc01c) does not clear the list and would draw over another screen's pictures.
            _shell.Anims.Clear();
            _shell.Post(_backMsg, _backParam);
        }
    }
}
