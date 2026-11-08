using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using MW2.Formats;
using MW2.Shell;
using UnityEngine;

namespace MW2.Autopilot
{
    /// <summary>
    /// Opt-in Fair career-segment harness, not an original game algorithm.
    /// Exercises ShellMain 100428d3, launch 10061aca, engine return 100598b1/100424dd,
    /// debrief 1001ede1/1001d1d3 and Save_Read 1006ddf6 with actual simulated outcomes.
    /// No synthesized mission success, direct damage edits or replacement career between sorties.
    /// </summary>
    public sealed class CareerPlaythroughChecks : MonoBehaviour
    {
        public MissionTestPlan Plan;
        Mw2Shell _shell;
        PrjArchive _archive;
        MissionTelemetry _telemetry;
        readonly Report _report = new Report();
        bool _ownsOutput;

        [Serializable] sealed class Leg
        {
            public string mission, model, mek, result, classification, nextMission, failureStep;
            public string assignedModel, assignedMek;
            public bool scriptedPlayer;
            public int sortie, seed, fundsBefore, fundsReturned, fundsAfterDebrief, fundsAfterRepair, repairCost;
            public int playerId, mechId, rosterCount;
            public string launchMekSha256, returnedMekSha256, pendingSaveSha256, nextSaveSha256;
            public bool reportPreserved, damageMerged, pendingSaveRoundTrip, nextSaveRoundTrip, previousUnitsRemoved;
            public bool continuedMissionFailure;
            public int skippedUnsupportedAmmo;
            public bool salvageOffered;
            public int salvageWrecks, recoveredChassis, recoveredItems;
        }
        [Serializable] sealed class Report
        {
            public int schema = 1, requestedSorties = 4, difficulty;
            public string kind = "FairCareerSegment", runtimeAssemblySha256, originalDllSha256, archiveSha256;
            public string stopReason = "Running";
            public string note = "Actual career roster/outcomes. A successful requested segment is not proof of a complete campaign or GUI operation.";
            public bool fairEligible, passed, storyComplete;
            public bool resumed, startingSaveRoundTrip, advancedStartingContract;
            public bool allowMissionFailure, routeCompleted;
            public bool collectSalvage;
            public string startingSaveSha256, normalizedStartingSaveSha256, loadedMission, startingMission;
            public int startingFunds;
            public readonly List<Leg> sorties = new List<Leg>();
            public readonly List<string> errors = new List<string>();
        }

        IEnumerator Start()
        {
            Application.logMessageReceived += Log;
            var run = Run();
            while (true)
            {
                bool more;
                try { more = run.MoveNext(); }
                catch (Exception error) { _report.errors.Add(error.ToString()); _report.stopReason = "HarnessError"; break; }
                if (!more) break;
                yield return run.Current;
            }
            _telemetry?.Dispose(); _telemetry = null;
            if (_shell != null)
            {
                _shell.enabled = false;
                _shell.Anims?.Clear();
                _shell.Prj = null;
            }
            _archive?.Dispose();
            Mission.LooseBwd = null;
            Application.logMessageReceived -= Log;
            _report.passed &= _report.errors.Count == 0;
            if (_ownsOutput) SaveReport();
            Debug.Log("MW2 CAREER SEGMENT: " + _report.stopReason + "; sorties=" + _report.sorties.Count + "; passed=" + _report.passed);
            Application.Quit(_report.errors.Count > 0 ? 2 : _report.passed ? 0 : 1);
        }

        void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); }

        void Log(string message, string stack, LogType type)
        {
            if ((type == LogType.Error || type == LogType.Exception || type == LogType.Assert) && _report.errors.Count < 100)
                _report.errors.Add(message + "\n" + stack);
        }

        IEnumerator Run()
        {
            Require(!Directory.Exists(Plan.output) && !File.Exists(Plan.output), "Output must be new; existing evidence was preserved");
            Directory.CreateDirectory(Plan.output); _ownsOutput = true;
            var args = Environment.GetCommandLineArgs();
            _report.allowMissionFailure = args.Contains("--career-allow-failure");
            _report.collectSalvage = args.Contains("--career-collect-salvage");
            int at = Array.IndexOf(args, "--career-sorties");
            if (at >= 0)
            {
                Require(at + 1 < args.Length && int.TryParse(args[at + 1], out _report.requestedSorties), "Invalid --career-sorties");
                Require(_report.requestedSorties >= 1 && _report.requestedSorties <= 64, "Career sorties must be 1..64");
            }
            Require(Plan.mode == "Fair" && !Plan.invulnerable, "Career segment requires Fair mode");
            Mw2Input.GameFolder = Plan.gameFolder;
            DifficultyConfig.UserFolder = Application.persistentDataPath;
            var options = Mw2Options.GameOptions;
            Require(!options.Invulnerable && !options.UnlimitedAmmo && options.PlayerHeat,
                "Fair career requires invulnerability OFF, unlimited ammo OFF and player heat ON; options were not changed");
            _report.fairEligible = true; _report.difficulty = options.Difficulty;
            _report.runtimeAssemblySha256 = HashFile(typeof(CareerPlaythroughChecks).Assembly.Location);
            _report.originalDllSha256 = HashFile(Path.Combine(Plan.gameFolder, "mercsw.dll"));
            _report.archiveSha256 = HashFile(Path.Combine(Plan.gameFolder, "MW2.PRJ"));
            Time.timeScale = 1; Time.captureFramerate = 60;
            Application.runInBackground = true;
            Application.targetFrameRate = Plan.speed >= 16 ? -1 : Mathf.RoundToInt(60 * Plan.speed);
            if (Camera.main == null) new GameObject("Career test camera") { tag = "MainCamera" }.AddComponent<Camera>();
            _shell = FindAnyObjectByType<Mw2Shell>(FindObjectsInactive.Include);
            Require(_shell != null, "Missing bootstrapped shell");
            _shell.enabled = false; _shell.GameFolder = Plan.gameFolder;
            _archive = new PrjArchive(Path.Combine(Plan.gameFolder, "MW2.PRJ"));
            _shell.Prj = _archive;
            _shell.Data = new ShellData(_archive, ShellTables.FromStore(Plan.gameFolder), Plan.gameFolder);
            _shell.Db = new ShellDatabase(Path.Combine(Plan.gameFolder, "DATABASE.MW2"));
            _shell.Surface = new ShellSurface();
            _shell.Anims = new ShellAnimList(_shell.Surface, Path.Combine(Plan.gameFolder, "SMK"), _shell.gameObject);
            _shell.Font7 = new ShellFont(_shell.Db.Raw(7));
            _shell.Font11 = new ShellFont(_shell.Db.Raw(11));
            _shell.Menu = ShellMenu.From(_shell.Data.Tables.Defaults.Menu);
            int saveAt = Array.IndexOf(args, "--career-save");
            if (saveAt >= 0)
            {
                Require(saveAt + 1 < args.Length && !args[saveAt + 1].StartsWith("--", StringComparison.Ordinal), "Missing --career-save path");
                byte[] saved = File.ReadAllBytes(Path.GetFullPath(args[saveAt + 1]));
                _shell.Career = SaveLoadScreen.Read(_shell, saved); // Actual Save_Read 1006ddf6, same as the campaign UI.
                Require(_shell.Career.Mode == 1 && _shell.Career.Registered && _shell.Career.PlayerHasMech(),
                    "Resume requires a registered regular career with an assigned player mech");
                _report.resumed = true;
                _report.startingSaveSha256 = HashBytes(saved);
                byte[] normalized = _shell.Career.Save();
                _report.normalizedStartingSaveSha256 = HashBytes(normalized);
                _report.startingSaveRoundTrip = saved.SequenceEqual(normalized);
                File.WriteAllBytes(Path.Combine(Plan.output, "initial.sav"), saved);
                _report.loadedMission = _shell.Career.Mission;
                Require(!_shell.Career.Endless, "The loaded save has already completed the story");
                // A finished contract's save still names its last mission. Continue via
                // the normal contract screen model rather than replaying the paid flight.
                if (_shell.Career.Finished239D)
                {
                    var offers = ContractOffers.Read(_shell.Career, _shell.Data, Plan.seed);
                    Require(offers.Count > 0 && _shell.Career.AcceptContract(0, offers) &&
                        _shell.Career.AdvanceAtMissionCommand(_shell.Data, 0xc021), "Cannot continue the saved finished contract");
                    _report.advancedStartingContract = true;
                }
                else if (_shell.Career.StepType == 0)
                {
                    Require(_shell.Career.AdvanceAtMissionCommand(_shell.Data, 0xc033), "Cannot enter the saved CAMP offer");
                    _report.advancedStartingContract = true;
                }
            }
            else
            {
                _shell.Career = Career.New(1, _shell.Data);
                _shell.Career.Register("FAIR SEGMENT", "CAREER CHECK");
                File.WriteAllBytes(Path.Combine(Plan.output, "initial.sav"), _shell.Career.Save());
                Require(_shell.Career.AdvanceAtMissionCommand(_shell.Data, 0xc033), "Cannot enter first original CAMP offer");
            }
            _report.startingMission = _shell.Career.Mission;
            _report.startingFunds = _shell.Career.Funds;
            var launchMethod = typeof(Mw2Shell).GetMethod("LaunchMission", BindingFlags.Instance | BindingFlags.NonPublic);
            Require(launchMethod != null, "Missing shell launch coroutine");
            SaveReport();

            for (int index = 0; index < _report.requestedSorties; index++)
            {
                var career = _shell.Career;
                string folder = Path.Combine(Plan.output, "sortie-" + (index + 1).ToString("D3"));
                Directory.CreateDirectory(folder);
                BriefingScreen.Open(_shell);
                Require(_shell.Handler != null && career.PlayerHasMech(), "Career cannot enter briefing with its assigned mech");
                _shell.Handler(0xc011); // The screen's real exit handler; launch below uses the real coroutine.
                UnityEngine.Random.InitState(unchecked(Plan.seed + index));
                var roster = career.BuildLaunchRoster(_shell.Data);
                Require(roster.Count > 0 && roster[0].MekData != null, "Career has no serialized player loadout");
                var leg = new Leg { sortie = index + 1, seed = unchecked(Plan.seed + index), mission = career.Mission,
                    failureStep = career.FailStep(0),
                    model = roster[0].Model, mek = roster[0].MekName, fundsBefore = career.Funds,
                    playerId = career.UnitPilots.First(p => p.Player).Id,
                    mechId = career.UnitPilots.First(p => p.Player).MechId, rosterCount = roster.Count,
                    launchMekSha256 = HashBytes(roster[0].MekData) };
                _report.sorties.Add(leg);
                File.WriteAllBytes(Path.Combine(folder, "launch.mek"), roster[0].MekData);
                File.WriteAllBytes(Path.Combine(folder, "launch.sav"), career.Save());
                leg.assignedModel = leg.model; leg.assignedMek = leg.mek;
                // Resolve exactly the same userstar/loose inputs as LaunchMission, to retain
                // missing-resource evidence without changing the real shell launch path.
                byte[] userstar = UserStar.Build(roster, career, _shell.Data.Tables, _archive);
                string[] missing;
                Spawn human;
                Mission.LooseBwd = name => name.Equals("userstar", StringComparison.OrdinalIgnoreCase) ? userstar : _shell.Data.ReadLoose(name);
                try
                {
                    var source = Mission.Load(_archive, MissionCatalog.Prefix(leg.mission));
                    missing = source.Missing.ToArray(); human = source.Spawns.Single(s => s.Player);
                }
                finally { Mission.LooseBwd = null; }
                Require(missing.Length == 0, "Missing career mission resources: " + string.Join(", ", missing));
                SaveReport();
                var launch = (IEnumerator)launchMethod.Invoke(_shell, new object[] { roster, roster[0], 0xc012u });
                while (launch.MoveNext()) yield return launch.Current; // Includes the real cosmetic launch wait.
                yield return null; yield return null;
                var director = FindAnyObjectByType<Mw2MissionDirector>();
                Require(_report.errors.Count == 0 && director != null && director.Player != null &&
                    director.MissionId.Equals(MissionCatalog.Prefix(leg.mission), StringComparison.OrdinalIgnoreCase), "Career launch failed");
                var pilot = director.Player.GetComponent<MechAutopilot>();
                bool rosterPlayer = human.Model.Equals(roster[0].Model, StringComparison.OrdinalIgnoreCase) &&
                    human.Loadout.Equals(Path.GetFileNameWithoutExtension(roster[0].MekName), StringComparison.OrdinalIgnoreCase);
                Require(pilot != null && pilot.Combat.CareerUnit == rosterPlayer &&
                    director.PlayerModel.Equals(human.Model, StringComparison.OrdinalIgnoreCase) &&
                    Path.GetFileNameWithoutExtension(director.PlayerMek).Equals(human.Loadout, StringComparison.OrdinalIgnoreCase) &&
                    pilot.Combat.SourceFile.Equals(human.File, StringComparison.OrdinalIgnoreCase),
                    "Career launch does not match its actual human GPS/written-MEK identity");
                leg.scriptedPlayer = !pilot.Combat.CareerUnit;
                leg.model = director.PlayerModel; leg.mek = director.PlayerMek;
                if (leg.scriptedPlayer)
                {
                    File.WriteAllBytes(Path.Combine(folder, "assigned.mek"), roster[0].MekData);
                    File.WriteAllBytes(Path.Combine(folder, "launch.mek"), pilot.Combat.MekBytes);
                    leg.launchMekSha256 = HashBytes(pilot.Combat.MekBytes);
                }
                var flightPlan = new MissionTestPlan { gameFolder = Plan.gameFolder, output = folder,
                    missions = director.MissionId, mech = leg.model, mek = leg.mek, mode = "Fair", tactic = Plan.tactic,
                    seed = leg.seed, attempt = index + 1, timeout = Plan.timeout, speed = Plan.speed };
                _telemetry = new MissionTelemetry(flightPlan, director, pilot, folder);
                _telemetry.Record.missingResources = missing;
                pilot.Tactic = (PilotTactic)Enum.Parse(typeof(PilotTactic), Plan.tactic);
                pilot.Begin();
                // Director.Start restores the normal 60 FPS cap; apply test pacing only
                // after the normal lifecycle, just as MissionTestRunner.Child does.
                Application.targetFrameRate = Plan.speed >= 16 ? -1 : Mathf.RoundToInt(60 * Plan.speed);
                var wall = System.Diagnostics.Stopwatch.StartNew();
                RunOutcome outcome;
                do
                {
                    yield return null;
                    Require(wall.Elapsed.TotalSeconds <= Plan.timeout + 180, "Career sortie exceeded its wall watchdog");
                    _telemetry.Sample();
                    outcome = RunOutcome.Evaluate(_telemetry.HasErrors, pilot.Combat.Destroyed, director.MissionResult.ToString(),
                        director.MissionTime, Plan.timeout, pilot.Sensors.HasTarget, pilot.Navigation.Recoveries, pilot.Combat.DeathSourceName);
                } while (outcome == null);
                var flight = _telemetry.Finish(outcome.result, outcome.classification, outcome.reason);
                _telemetry = null;
                pilot.StopPilot();
                leg.result = flight.result; leg.classification = flight.classification;
                byte[] returnedMek = pilot.Combat.WriteMek(), expectedReport = director.BuildReport();
                if (returnedMek != null)
                {
                    leg.returnedMekSha256 = HashBytes(returnedMek);
                    File.WriteAllBytes(Path.Combine(folder, "returned.mek"), returnedMek);
                }
                Require(leg.scriptedPlayer == (returnedMek == null), "Unexpected scripted/written player return");
                Require(Mw2Cockpit.MissionLeft != null, "Missing real career return callback");
                Mw2Cockpit.MissionLeft(false);
                _shell.enabled = false; // Drive the real screen handlers explicitly; no UI ticks or scripted outcomes.
                leg.fundsReturned = _shell.Career.Funds;
                if (_shell.SalvageFile != null)
                    File.WriteAllBytes(Path.Combine(folder, "salvage.mw2"), _shell.SalvageFile);
                leg.reportPreserved = _shell.MissionReportFile.SequenceEqual(expectedReport);
                File.WriteAllBytes(Path.Combine(folder, "mission-report.cfg"), _shell.MissionReportFile);
                File.WriteAllBytes(Path.Combine(folder, "combat-career.cfg"), _shell.CombatCareerFile);
                var merged = _shell.Career.UnitMechs.Find(m => m.MechId == leg.mechId);
                var expectedMek = MekLoadout.Decode(leg.scriptedPlayer ? roster[0].MekData : returnedMek);
                var mergedMek = merged == null ? null : MekLoadout.Decode(merged.ToMek());
                leg.damageMerged = mergedMek != null && mergedMek.FrontArmor.SequenceEqual(expectedMek.FrontArmor) &&
                    mergedMek.RearArmor.SequenceEqual(expectedMek.RearArmor) && mergedMek.Internal.SequenceEqual(expectedMek.Internal);
                if (leg.scriptedPlayer) leg.damageMerged &= merged.ToMek().SequenceEqual(roster[0].MekData);
                Require(leg.reportPreserved && leg.damageMerged, "Real mission return changed the report or failed to merge damage");
                yield return null; yield return null;
                leg.previousUnitsRemoved = FindAnyObjectByType<Mw2MissionDirector>() == null && Mw2MechCombat.All.Count == 0;
                Require(leg.previousUnitsRemoved, "Prior mission units survived career return");
                bool continueFailure = _report.allowMissionFailure && outcome.classification == "MissionConditionFailure" &&
                    !string.IsNullOrEmpty(leg.failureStep) && !leg.failureStep.Equals("DEATH", StringComparison.OrdinalIgnoreCase);
                if (outcome.result != "Victory" && !continueFailure)
                {
                    _report.stopReason = "Mission" + outcome.result;
                    File.WriteAllBytes(Path.Combine(folder, "stopped.sav"), _shell.Career.Save());
                    SaveReport(); yield break;
                }

                DebriefScreen.Open(_shell);
                Require(_shell.Handler != null && !_shell.FlicRunning, "Successful career report did not enter debrief");
                File.WriteAllBytes(Path.Combine(folder, "before-salvage.sav"), _shell.Career.Save());
                if (_report.collectSalvage) CollectSalvage(leg);
                _shell.Handler(0xc02e); // Preserve pending real report through SaveLoadScreen.Read before payment.
                leg.pendingSaveRoundTrip = RoundTrip(folder, "pending", out leg.pendingSaveSha256);
                Require(leg.pendingSaveRoundTrip, "Pending debrief save did not round-trip byte for byte");
                DebriefScreen.Open(_shell);
                int type = _shell.Career.StepType;
                _shell.Handler(0xc012); // Real Continue handles payment and CPGN progression.
                if (continueFailure && type == 1)
                {
                    var next = Campaign.Decode(leg.failureStep, _shell.Data.ReadBwd(leg.failureStep));
                    Require(next != null && _shell.Career.Mission.Equals(next.Mission, StringComparison.OrdinalIgnoreCase),
                        "Nonfatal failure did not follow its original CPGN failure link");
                }
                leg.fundsAfterDebrief = _shell.Career.Funds;
                if (!_shell.Career.PlayerHasMech()) { _report.stopReason = "NoAssignedMech"; SaveReport(); yield break; }
                var repair = new MechRepair(_shell.Career, _shell.Data.Tables);
                repair.Open(); repair.RepairAll(false);
                int repairBefore = _shell.Career.Funds;
                Require(repair.Answer(0), "Career cannot pay for the available repairs");
                leg.fundsAfterRepair = _shell.Career.Funds; leg.repairCost = repairBefore - leg.fundsAfterRepair;
                if (_shell.Career.Finished239D && !_shell.Career.Endless)
                {
                    var offers = ContractOffers.Read(_shell.Career, _shell.Data, leg.seed);
                    Require(offers.Count > 0 && _shell.Career.AcceptContract(0, offers) &&
                        _shell.Career.AdvanceAtMissionCommand(_shell.Data, 0xc021), "Cannot accept the next original contract offer");
                }
                leg.nextMission = _shell.Career.Mission;
                leg.nextSaveRoundTrip = RoundTrip(folder, "next", out leg.nextSaveSha256);
                Require(leg.nextSaveRoundTrip, "Ready-for-next-mission save did not round-trip byte for byte");
                leg.continuedMissionFailure = continueFailure;
                _report.storyComplete = _shell.Career.Endless;
                SaveReport();
                if (_report.storyComplete) break;
            }
            Require(_report.archiveSha256 == HashFile(Path.Combine(Plan.gameFolder, "MW2.PRJ")) &&
                _report.originalDllSha256 == HashFile(Path.Combine(Plan.gameFolder, "mercsw.dll")), "Original inputs changed during the segment");
            _report.routeCompleted = _report.errors.Count == 0 && _report.sorties.All(l => (l.result == "Victory" || l.continuedMissionFailure) &&
                l.reportPreserved && l.damageMerged && l.pendingSaveRoundTrip && l.nextSaveRoundTrip && l.previousUnitsRemoved) &&
                (_report.sorties.Count == _report.requestedSorties || _report.storyComplete);
            _report.passed = _report.routeCompleted && _report.sorties.All(l => l.result == "Victory");
            _report.stopReason = _report.passed ? "RequestedSegmentComplete" :
                _report.routeCompleted ? "RequestedRouteCompleteWithFailures" : "SegmentIncomplete";
        }

        // Opt-in tester selection, not a game rule. Read the actual Debrief_Open
        // 1001ede1 offer gate, then use SalvageScreen's 100675e8/10069597/
        // 100652b8 model operations and the wreck's recorded rolls unchanged.
        void CollectSalvage(Leg leg)
        {
            var flags = BindingFlags.Static | BindingFlags.NonPublic;
            bool offered = (bool)typeof(DebriefScreen).GetField("_salvageAvailable", flags).GetValue(null);
            int state = (int)typeof(DebriefScreen).GetField("_state", flags).GetValue(null);
            leg.salvageOffered = offered && state == 2;
            if (!leg.salvageOffered) return;
            var career = _shell.Career;
            var file = SalvageFile.Read(_shell.SalvageFile);
            leg.salvageWrecks = file.Wrecks.Count;
            var candidates = new List<SalvageRecovery.Candidate>();
            for (int i = 0; i < file.Wrecks.Count; i++)
            {
                var candidate = SalvageRecovery.Prepare(file.Wrecks[i], _shell.Data,
                    file.Wrecks[i].Rolls[Math.Min(i, 95)]);
                if (candidate != null) candidates.Add(candidate);
            }
            if (career.Mission.StartsWith("out3", StringComparison.OrdinalIgnoreCase))
            {
                var kodiak = SalvageRecovery.PrepareKodiak(_shell.Data);
                if (kodiak != null) candidates.Add(kodiak);
            }
            var skipped = new List<string>();
            foreach (var candidate in candidates)
            {
                if (SalvageRecovery.Commit(candidate, career, _shell.Data.Tables) != null)
                { leg.recoveredChassis++; continue; }
                // Strip only unclaimed wrecks. TakeCritical enforces original
                // part restrictions, multi-slot identity and 200-space limit.
                for (int section = 0; section < 8; section++)
                    for (int slot = 0; slot < (section == 0 || section >= 6 ? 6 : 12); slot++)
                    {
                        int code = candidate.Current.Crit(section, slot);
                        int ammoIndex = (code - 10000) / 100;
                        if (UnsupportedSalvageAmmo(code))
                        {
                            // Tester declines an unsupported selection; do not
                            // normalize it or change the original salvage model.
                            leg.skippedUnsupportedAmmo++;
                            skipped.Add("section=" + section + ";slot=" + slot + ";code=" + code + ";index=" + ammoIndex);
                            continue;
                        }
                        if (SalvageRecovery.TakeCritical(candidate, section, slot,
                            career.UnitWeapons, career.UnitAmmo, _shell.Data.Tables, out _) != null)
                            leg.recoveredItems++;
                    }
            }
            if (skipped.Count > 0) File.WriteAllLines(Path.Combine(Plan.output, "sortie-" + leg.sortie.ToString("000"), "salvage-skipped.txt"), skipped);
            // The ordinary screen also consumes the file on acceptance.
            _shell.SalvageOpened = true;
            _shell.SalvageFile = null;
            career.SalvageFile = null;
        }

        internal static bool UnsupportedSalvageAmmo(int code)
        {
            int index = (code - 10000) / 100;
            return SalvageRecovery.CriticalKind(code) == 4 && (index < 0 || index >= WeaponTable.TypeCount);
        }

        bool RoundTrip(string folder, string name, out string digest)
        {
            byte[] saved = _shell.Career.Save();
            File.WriteAllBytes(Path.Combine(folder, name + ".sav"), saved);
            digest = HashBytes(saved);
            _shell.Career = SaveLoadScreen.Read(_shell, File.ReadAllBytes(Path.Combine(folder, name + ".sav")));
            return saved.SequenceEqual(_shell.Career.Save());
        }

        void SaveReport()
        {
            string temporary = Path.Combine(Plan.output, "career-segment.json.tmp"), destination = Path.Combine(Plan.output, "career-segment.json");
            File.WriteAllText(temporary, ReportJson.Write(_report));
            if (File.Exists(destination)) File.Delete(destination);
            File.Move(temporary, destination);
        }

        static string HashFile(string path)
        {
            if (!File.Exists(path)) return "absent"; // e.g. mercsw.dll once its tables are extracted
            using var hash = System.Security.Cryptography.SHA256.Create();
            using var stream = File.OpenRead(path);
            return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }
        static string HashBytes(byte[] data)
        {
            using var hash = System.Security.Cryptography.SHA256.Create();
            return BitConverter.ToString(hash.ComputeHash(data)).Replace("-", "").ToLowerInvariant();
        }
    }
}
