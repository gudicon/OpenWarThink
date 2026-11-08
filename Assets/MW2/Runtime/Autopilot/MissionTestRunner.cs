using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using MW2.Formats;
using UnityEngine;
using Process = System.Diagnostics.Process;
using ProcessStartInfo = System.Diagnostics.ProcessStartInfo;

namespace MW2.Autopilot
{
    /// <summary>Every attempt runs in a fresh Player process, isolating all game singletons, timers and damage.</summary>
    public sealed class MissionTestRunner : MonoBehaviour
    {
        static MissionTestRunner s_instance;
        public static bool IsTestChild { get; private set; }
        // Normal play keeps the original unscaled clock. A fixed-step test uses one clock for input, objectives and movement.
        public static float ClockDelta => IsTestChild ? Time.deltaTime : Time.unscaledDeltaTime;
        MissionTestPlan _plan;
        Process _process;
        MissionTelemetry _telemetry;
        readonly List<string> _startupErrors = new List<string>();
        string _status = "";

        public static bool Bootstrap()
        {
            var args = Environment.GetCommandLineArgs();
            // Launch-only adaptation: leave audio/voice clocks and saved options
            // unchanged, while silencing the Unity mixer (including shell sounds).
            if (args.Contains("--mw2-mute"))
            {
                AudioListener.volume = 0f;
                Debug.Log("MW2: launch audio muted; saved sound options unchanged");
            }
            if (args.Contains("--mw2-career-playthrough"))
            {
                IsTestChild = true;
                new GameObject("Career playthrough checks").AddComponent<CareerPlaythroughChecks>().Plan = MissionTestPlan.Parse(args);
                return true;
            }
            if (args.Contains("--mw2-mission-checks"))
            {
                IsTestChild = true;
                new GameObject("Mission behavior checks").AddComponent<MissionBehaviorChecks>().Plan = MissionTestPlan.Parse(args);
                return true;
            }
            if (args.Contains("--mw2-selftest"))
            {
                IsTestChild = true;
                new GameObject("Autopilot integration checks").AddComponent<AutopilotRuntimeChecks>().Plan = MissionTestPlan.Parse(args);
                return true;
            }
            if (!args.Contains("--mw2-tests") && !args.Contains("--mw2-child")) return false;
            try
            {
                var runner = new GameObject("Mission test runner").AddComponent<MissionTestRunner>();
                DontDestroyOnLoad(runner.gameObject);
                runner._plan = MissionTestPlan.Parse(args); s_instance = runner;
                IsTestChild = runner._plan.child;
                Application.runInBackground = true;
            }
            catch (Exception e) { Debug.LogError("MW2 test startup: " + e); Application.Quit(2); }
            return true;
        }

        public static void StartInteractive(Mw2MissionDirector director)
        {
            if (s_instance != null) return;
            if (string.IsNullOrEmpty(director.MissionId) || string.IsNullOrEmpty(director.PlayerModel))
            { Debug.LogError("Reload the mission to populate mission/MEK metadata before running tests"); return; }
            var runner = new GameObject("Mission test series").AddComponent<MissionTestRunner>();
            runner._plan = new MissionTestPlan { gameFolder = director.GameFolder, missions = director.MissionId,
                mech = director.PlayerModel, mek = director.PlayerMek, attempts = 10, headless = true,
                output = Path.Combine(Application.persistentDataPath, "MissionTests", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff")) };
            runner._plan.Validate();
            Directory.CreateDirectory(runner._plan.output);
            if (director.Player != null && director.Player.TryGetComponent<Mw2MechCombat>(out var p) && p.MekBytes != null)
            {
                runner._plan.mekFile = Path.Combine(runner._plan.output, "player-loadout.mek");
                File.WriteAllBytes(runner._plan.mekFile, p.MekBytes);
            }
            s_instance = runner;
            DontDestroyOnLoad(runner.gameObject);
        }

        IEnumerator Start()
        {
            if (_plan == null) yield break;
            if (_plan.child) yield return Child(); else yield return Batch();
        }

        IEnumerator Child()
        {
            Application.logMessageReceived += LoadingLog;
            Mw2MissionDirector director = null; MechAutopilot pilot = null; Mission mission = null;
            Exception failure = null;
            try
            {
                Directory.CreateDirectory(_plan.output);
                if (File.Exists(Path.Combine(_plan.output, "result.json"))) throw new IOException("Attempt already exists; choose a new output directory");
                Time.timeScale = 1f; Time.captureFramerate = 60;
                DifficultyConfig.UserFolder = Application.persistentDataPath;
                Mw2Input.GameFolder = _plan.gameFolder;
                var options = Mw2Options.GameOptions;
                if (_plan.mode == "Fair" && (options.Invulnerable || options.UnlimitedAmmo || !options.PlayerHeat))
                    throw new InvalidOperationException("Fair mode requires invulnerability OFF, unlimited ammo OFF and player heat ON in Combat Variables. Configuration was not changed.");
                if (_plan.mode == "Validation" && _plan.invulnerable) options.Bytes[1] = 1; // Explicit in-memory validation option only.
                if (Camera.main == null)
                {
                    var go = new GameObject("Test main camera") { tag = "MainCamera" };
                    go.AddComponent<Camera>().farClipPlane = 8000f;
                }
                UnityEngine.Random.InitState(_plan.seed);
                using (var prj = new PrjArchive(Path.Combine(_plan.gameFolder, "MW2.PRJ")))
                {
                    string prefix = MissionCatalog.Prefix(_plan.missions);
                    if (!prj.TryFind("BWD", prefix + "SCN1", out _)) throw new InvalidOperationException("Missing original mission " + prefix);
                    mission = Mission.Load(prj, prefix);
                    byte[] mek = string.IsNullOrEmpty(_plan.mekFile) ? null : File.ReadAllBytes(_plan.mekFile);
                    var root = Mw2MissionBuilder.Build(prj, _plan.gameFolder, prefix, _plan.mech, 1, _plan.mek, playerMekData: mek);
                    if (root == null || !root.TryGetComponent(out director) || director.Player == null)
                        throw new InvalidOperationException("Mission builder did not create a player/director");
                    pilot = director.Player.GetComponent<MechAutopilot>();
                    if (pilot == null) throw new InvalidOperationException("Missing player test pilot");
                }
            }
            catch (Exception e) { failure = e; }
            // Let controller, weapons, objectives and AI initialize in their normal Unity lifecycle.
            yield return null; yield return null;
            if (failure != null || _startupErrors.Count > 0)
            {
                WriteError(_plan.output, _plan, "LoadError", failure != null ? failure.ToString() : string.Join("\n", _startupErrors));
                Application.Quit(2); yield break;
            }
            try
            {
                _telemetry = new MissionTelemetry(_plan, director, pilot, _plan.output);
                _telemetry.Record.missingResources = mission.Missing.ToArray();
                pilot.Tactic = (PilotTactic)Enum.Parse(typeof(PilotTactic), _plan.tactic);
                pilot.Begin();
                Application.targetFrameRate = _plan.speed >= 16 ? -1 : Mathf.RoundToInt(60 * _plan.speed);
            }
            catch (Exception e) { failure = e; }
            Application.logMessageReceived -= LoadingLog;
            if (failure != null)
            { WriteError(_plan.output, _plan, "InitializationError", failure.ToString()); Application.Quit(2); yield break; }
            string result = null, classification = null, reason = null;
            while (result == null)
            {
                yield return null;
                _telemetry.Sample();
                _status = director.MissionId + " " + director.MissionTime.ToString("0.0") + "s " + pilot.State;
                var outcome = RunOutcome.Evaluate(_telemetry.HasErrors, pilot.Combat.Destroyed, director.MissionResult.ToString(),
                    director.MissionTime, _plan.timeout, pilot.Sensors.HasTarget, pilot.Navigation.Recoveries, pilot.Combat.DeathSourceName);
                if (outcome != null) { result = outcome.result; classification = outcome.classification; reason = outcome.reason; }
            }
            try { _telemetry.Finish(result, classification, reason); _telemetry = null; }
            catch (Exception e) { Debug.LogError("Cannot save mission telemetry: " + e); Application.Quit(2); yield break; }
            pilot.StopPilot();
            Debug.Log("MW2 TEST RESULT: " + result + " " + classification + " -> " + _plan.output);
            Application.Quit(result == "Error" ? 2 : 0);
        }

        void LoadingLog(string message, string trace, LogType type)
        {
            if ((type == LogType.Error || type == LogType.Exception || type == LogType.Assert) && _startupErrors.Count < 100)
                _startupErrors.Add(message + "\n" + trace);
        }

        IEnumerator Batch()
        {
            List<string> missions = null; Exception error = null;
            string folder = Path.Combine(_plan.output, _plan.mode);
            try
            {
                Directory.CreateDirectory(folder);
                if (File.Exists(Path.Combine(folder, "runs.jsonl")) || File.Exists(Path.Combine(folder, "plan.json")))
                    throw new IOException("Series already exists; choose a new output directory");
                using (var prj = new PrjArchive(Path.Combine(_plan.gameFolder, "MW2.PRJ")))
                {
                    missions = _plan.MissionList(prj);
                    if (!string.IsNullOrEmpty(_plan.campaign))
                    {
                        var references = MissionCatalog.ReferencedStoryMissions(prj);
                        File.WriteAllText(Path.Combine(folder, "campaign-references.json"), ReportJson.Write(references));
                        File.WriteAllText(Path.Combine(folder, "unreachable-campaign-references.json"),
                            ReportJson.Write(references.Except(missions, StringComparer.OrdinalIgnoreCase).ToList()));
                    }
                }
                if (missions.Count == 0) throw new InvalidOperationException("No supported story missions in archive");
                File.WriteAllText(Path.Combine(folder, "plan.json"), ReportJson.Write(_plan));
                File.WriteAllText(Path.Combine(folder, "missions.json"), ReportJson.Write(missions));
            }
            catch (Exception e) { error = e; }
            if (error != null)
            { Debug.LogError("MW2 test series: " + error); if (_plan.quit) Application.Quit(2); Destroy(gameObject); yield break; }
            var records = new List<MissionRunRecord>();
            int serial = 0;
            foreach (string mission in missions)
                for (int attempt = 1; attempt <= _plan.attempts; attempt++)
                {
                    int seed = unchecked(_plan.seed + serial++);
                    string attemptFolder = Path.Combine(folder, mission, "attempt-" + attempt.ToString("D4") + "-seed-" + seed);
                    Directory.CreateDirectory(attemptFolder);
                    _status = $"{mission}: attempt {attempt}/{_plan.attempts}, seed {seed}";
                    error = null;
                    try { _process = Process.Start(StartInfo(mission, attempt, seed, attemptFolder)); if (_process == null) throw new IOException("Player did not start"); }
                    catch (Exception e) { error = e; }
                    float started = Time.realtimeSinceStartup;
                    bool watchdog = false;
                    if (error == null)
                    {
                        while (!_process.HasExited)
                        {
                            if (Time.realtimeSinceStartup - started > _plan.timeout + 180f)
                            { _process.Kill(); watchdog = true; break; }
                            yield return null;
                        }
                    }
                    MissionRunRecord record = null;
                    try
                    {
                        string report = Path.Combine(attemptFolder, "result.json");
                        if (!watchdog && error == null && File.Exists(report)) record = JsonUtility.FromJson<MissionRunRecord>(File.ReadAllText(report));
                        if (record != null && (record.mission != mission || record.seed != seed || record.attempt != attempt || record.mode != _plan.mode ||
                            (_process.ExitCode != 0 && record.result != "Error")))
                            record = null;
                        if (record == null) record = WriteError(attemptFolder, _plan, watchdog ? "ProcessWatchdog" : "ProcessError",
                            error != null ? error.ToString() : "Child exited without a complete report; inspect Player.log", mission, attempt, seed);
                        File.AppendAllText(Path.Combine(folder, "runs.jsonl"), record.ToJson() + "\n");
                        records.Add(record);
                        File.WriteAllText(Path.Combine(folder, "summary.json"), ReportJson.Write(SeriesSummary.Build(records, _plan.mode)));
                    }
                    catch (Exception e) { Debug.LogError("MW2 series reporting: " + e); if (_plan.quit) Application.Quit(2); yield break; }
                    if (_process != null) { _process.Dispose(); _process = null; }
                    Debug.Log("MW2 TEST: " + _status + " -> " + record.result + " / " + record.classification);
                }
            _status = "Finished: " + folder;
            Debug.Log("MW2 TEST SERIES: " + ReportJson.Write(SeriesSummary.Build(records, _plan.mode)));
            if (_plan.quit) Application.Quit(records.Any(r => r.result == "Error") ? 2 : 0);
            else Destroy(gameObject);
        }

        ProcessStartInfo StartInfo(string mission, int attempt, int seed, string folder)
        {
            string exe = Application.isEditor ? Path.GetFullPath(Path.Combine(Application.dataPath, "../Build/Windows/MW2Mercs.exe")) :
                Path.Combine(Directory.GetParent(Application.dataPath).FullName, Path.GetFileName(Application.dataPath).Replace("_Data", "") + ".exe");
            if (!File.Exists(exe)) throw new FileNotFoundException("Build the Windows Player first", exe);
            var args = new List<string> { "--mw2-child", "--mission", mission, "--mech", _plan.mech, "--mek", _plan.mek,
                "--game-folder", _plan.gameFolder, "--output", folder, "--attempt", attempt.ToString(), "--seed", seed.ToString(),
                "--timeout", _plan.timeout.ToString(CultureInfo.InvariantCulture), "--speed", _plan.speed.ToString(CultureInfo.InvariantCulture),
                "--mode", _plan.mode, "--tactic", _plan.tactic, "-logFile", Path.Combine(folder, "Player.log") };
            if (!string.IsNullOrEmpty(_plan.mekFile)) { args.Add("--mek-file"); args.Add(_plan.mekFile); }
            if (_plan.invulnerable) args.Add("--invulnerable");
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "--mw2-mute") >= 0) args.Add("--mw2-mute");
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "--pilot-close-nav") >= 0) args.Add("--pilot-close-nav");
            if (_plan.headless) { args.Add("-batchmode"); args.Add("-nographics"); }
            return new ProcessStartInfo { FileName = exe, Arguments = string.Join(" ", args.Select(Quote)), UseShellExecute = false,
                CreateNoWindow = _plan.headless, WindowStyle = _plan.headless ? System.Diagnostics.ProcessWindowStyle.Hidden : System.Diagnostics.ProcessWindowStyle.Normal };
        }
        // Windows argv quoting, including embedded quotes and trailing directory separators.
        public static string Quote(string value)
        {
            var b = new StringBuilder("\""); int slashes = 0;
            foreach (char c in value)
            {
                if (c == '\\') { slashes++; continue; }
                b.Append('\\', c == '"' ? slashes * 2 + 1 : slashes); b.Append(c); slashes = 0;
            }
            return b.Append('\\', slashes * 2).Append('"').ToString();
        }
        static MissionRunRecord WriteError(string folder, MissionTestPlan p, string classification, string reason,
                                          string mission = null, int attempt = 0, int seed = 0)
        {
            var r = new MissionRunRecord { mission = mission ?? p.missions, mode = p.mode, tactic = p.tactic,
                mech = p.mech, mek = p.mek, attempt = attempt > 0 ? attempt : p.attempt, seed = attempt > 0 ? seed : p.seed,
                invulnerable = p.invulnerable, result = "Error", classification = classification, reason = reason, errors = new[] { reason } };
            if (IsTestChild)
            {
                r.processId = Process.GetCurrentProcess().Id;
                var options = Mw2Options.GameOptions;
                r.difficulty = options.Difficulty; r.invulnerable = options.Invulnerable;
                r.unlimitedAmmo = options.UnlimitedAmmo; r.playerHeat = options.PlayerHeat;
                r.fairEligible = p.mode == "Fair" && !options.Invulnerable && !options.UnlimitedAmmo && options.PlayerHeat;
            }
            Directory.CreateDirectory(folder);
            string destination = Path.Combine(folder, "result.json");
            if (!File.Exists(destination)) File.WriteAllText(destination, r.ToJson());
            return r;
        }
        void OnGUI()
        { if (!_plan.child && !Application.isBatchMode) GUI.Box(new Rect(20, Screen.height - 60, 850, 40), "Mission tests: " + _status); }
        void OnDestroy()
        {
            Application.logMessageReceived -= LoadingLog;
            if (_process != null) { if (!_process.HasExited) _process.Kill(); _process.Dispose(); }
            if (_telemetry != null) _telemetry.Dispose();
            if (s_instance == this) s_instance = null;
        }
    }
}
