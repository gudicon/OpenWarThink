using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using MW2.Formats;
using UnityEngine;

namespace MW2.Autopilot
{
    /// <summary>
    /// Synthetic integration fixtures for Objective_Evaluate 0x1004154a / Mission_CheckObjectives 0x100421e6.
    /// Uses archived MTBL records, isolated dependencies and controlled targets/clock, never fair playthrough evidence.
    /// Does not assign objective states or call FinishMission. Every transition uses the production director Update.
    /// </summary>
    public sealed class MissionBehaviorChecks : MonoBehaviour
    {
        public MissionTestPlan Plan;
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        readonly List<Case> _cases = new List<Case>();
        readonly List<string> _errors = new List<string>();
        List<Mission> _missions;

        [Serializable] public sealed class Observation
        {
            public string action, expectedState, state, expectedResult, result;
            public int team, order;
            public int currentOrder;
            public ObjectiveRecord[] visibleObjectives;
            public float seconds;
            public bool passed;
        }
        [Serializable] public sealed class Case
        {
            public string name, mission, target, mask, sourceConditions, error;
            public int sourceTeam, sourceOrder, sourceTimeLimit, testTimeLimit, testTeam;
            public bool passed, dependenciesIsolated = true;
            public readonly List<Observation> observations = new List<Observation>();
        }
        [Serializable] sealed class Report
        {
            public int schema = 1;
            public string kind = "SyntheticBehavioral", runtimeAssemblySha256, originalDllSha256, archiveSha256;
            public string note = "Controlled world state/clock. dependenciesIsolated=true cases bypass MTBL dependencies; false cases preserve full archived tables. No combat or career playthrough claim.";
            public bool passed, fairEligible = false;
            public int missionsInventoried, storyMissionsInventoried, casesPassed, casesTotal;
            public OrderInventory[] orderInventory;
            public Case[] cases;
            public string[] errors;
        }
        [Serializable] public sealed class OrderInventory
        {
            public string mask;
            public int records, unitBound, structureBound, navBound;
        }
        sealed class Source
        {
            public Mission mission;
            public int team, index;
            public MissionOrder order;
        }

        // Harness adapter, not an original algorithm. The original expectations are in each scenario below.
        void Start()
        {
            Application.logMessageReceived += Log;
            var report = new Report();
            try
            {
                Directory.CreateDirectory(Plan.output);
                if (File.Exists(Path.Combine(Plan.output, "mission-behavior.json")))
                    throw new IOException("Mission behavior report already exists; choose a new output directory");
                Time.captureFramerate = 60;
                Mw2Input.GameFolder = Plan.gameFolder;
                using (var prj = new PrjArchive(Path.Combine(Plan.gameFolder, "MW2.PRJ")))
                {
                    _missions = MissionCatalog.StoryMissions(prj).Select(p => Mission.Load(prj, p)).ToList();
                    report.storyMissionsInventoried = _missions.Count;
                    _missions.Add(Mission.Load(prj, "TRA1"));
                }
                report.missionsInventoried = _missions.Count;
                report.orderInventory = _missions.SelectMany(m => m.Teams.SelectMany(t => t.Orders.Select(o => (mission: m, order: o))))
                    .GroupBy(x => x.order.Mask).OrderBy(g => g.Key).Select(g => new OrderInventory
                    {
                        mask = g.Key.ToString("x"), records = g.Count(), unitBound = g.Count(x => UnitBound(x.mission, x.order)),
                        structureBound = g.Count(x => StructureBound(x.mission, x.order)),
                        navBound = g.Count(x => x.mission.NavPoints.Any(n => n.File.Equals(x.order.Target, StringComparison.OrdinalIgnoreCase)))
                    }).ToArray();
                report.runtimeAssemblySha256 = Hash(typeof(MissionBehaviorChecks).Assembly.Location);
                report.originalDllSha256 = Hash(Path.Combine(Plan.gameFolder, "mercsw.dll"));
                report.archiveSha256 = Hash(Path.Combine(Plan.gameFolder, "MW2.PRJ"));
                Navigation();
                Objects();
                TimersAndResults();
                OriginalTrainingTables();
                OriginalStoryTables();
            }
            catch (Exception e) { _errors.Add(e.ToString()); }
            report.cases = _cases.ToArray(); report.errors = _errors.ToArray();
            report.casesTotal = _cases.Count; report.casesPassed = _cases.Count(c => c.passed);
            report.passed = _errors.Count == 0 && _cases.Count > 0 && _cases.All(c => c.passed);
            Application.logMessageReceived -= Log;
            try
            {
                string destination = Path.Combine(Plan.output, "mission-behavior.json");
                // Preserve existing evidence even on a rejected repeat invocation.
                if (!File.Exists(destination))
                {
                    string temporary = destination + ".tmp";
                    File.WriteAllText(temporary, ReportJson.Write(report)); File.Move(temporary, destination);
                }
            }
            catch (Exception e) { Debug.LogError(e); report.passed = false; }
            Debug.Log("MW2 MISSION BEHAVIOR: " + report.casesPassed + "/" + report.casesTotal + "; passed=" + report.passed);
            Application.Quit(report.passed ? 0 : 2);
        }

        void Log(string message, string stack, LogType type)
        { if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert) _errors.Add(message + "\n" + stack); }

        static string Hash(string path)
        {
            if (!File.Exists(path)) return "absent"; // e.g. mercsw.dll once its tables are extracted
            using var algorithm = System.Security.Cryptography.SHA256.Create();
            using var stream = File.OpenRead(path);
            return BitConverter.ToString(algorithm.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }

        Source Find(uint mask, Func<Mission, MissionOrder, bool> bound = null)
        {
            foreach (var mission in _missions)
                foreach (var team in mission.Teams)
                    for (int index = 0; index < team.Orders.Count; index++)
                    {
                        var order = team.Orders[index];
                        if (order.Mask == mask && (bound == null || bound(mission, order)))
                            return new Source { mission = mission, team = team.Team, index = index, order = order };
                    }
            throw new InvalidOperationException("No archived source for mask " + mask.ToString("x"));
        }

        void Run(string name, Source source, Action<Fixture> body, int? timeLimit = null)
        {
            var c = new Case { name = name, mission = source.mission.Prefix, sourceTeam = source.team,
                sourceOrder = source.index, target = source.order.Target, mask = source.order.Mask.ToString("x"),
                sourceConditions = source.order.ConditionTypes, sourceTimeLimit = source.order.TimeLimit,
                testTimeLimit = timeLimit ?? source.order.TimeLimit };
            _cases.Add(c);
            try
            {
                using var fixture = new Fixture(c, source.order, c.testTimeLimit);
                body(fixture);
                c.passed = c.observations.Count > 0 && c.observations.All(o => o.passed);
            }
            catch (Exception e) { c.error = e.ToString(); c.passed = false; }
        }

        // Objective_LeaderReached 0x10040a04: 3D distance, inclusive boundary. LeaveArea negates arrival.
        void Navigation()
        {
            var arrival = Find(MissionOrder.GotoNav, (m, o) => m.NavPoints.Any(n => n.File.Equals(o.Target, StringComparison.OrdinalIgnoreCase)));
            Run("goto: outside -> exact radius -> terminal state retained", arrival, f =>
            {
                f.Nav(); f.Leader.position = Vector3.right * 11;
                f.Step(0, "outside radius", Mw2MissionDirector.State.Active, Mw2MissionDirector.Result.Running);
                f.Leader.position = Vector3.right * 10;
                f.Step(0, "exact radius", Mw2MissionDirector.State.Succeeded, Mw2MissionDirector.Result.Success);
                f.Leader.position = Vector3.right * 11;
                f.Step(1, "leave after completion", Mw2MissionDirector.State.Succeeded, Mw2MissionDirector.Result.Success);
            });
            Run("goto: altitude prevents premature arrival", arrival, f =>
            {
                f.Nav(); f.Leader.position = Vector3.up * 11;
                f.Step(0, "same x/z, above radius", Mw2MissionDirector.State.Active, Mw2MissionDirector.Result.Running);
                f.Leader.position = Vector3.up * 10;
                f.Step(0, "vertical boundary", Mw2MissionDirector.State.Succeeded, Mw2MissionDirector.Result.Success);
            });
            Run("goto: strict objective deadline and report failure", arrival, f =>
            {
                f.Nav(); f.Leader.position = Vector3.right * 11;
                f.Step(0, "activate", Mw2MissionDirector.State.Active, Mw2MissionDirector.Result.Running);
                f.Step(2, "equal limit remains active", Mw2MissionDirector.State.Active, Mw2MissionDirector.Result.Running);
                f.Step(3, "past limit", Mw2MissionDirector.State.Failed, Mw2MissionDirector.Result.Failure);
                f.CheckReport(3, false);
            }, 2);
            Run("goto: original empty NAV binding completes without inventing a destination",
                Find(MissionOrder.GotoNav, (m, o) => !m.NavPoints.Any(n => n.File.Equals(o.Target, StringComparison.OrdinalIgnoreCase))),
                f => f.Step(0, "no bound NAVPs", Mw2MissionDirector.State.Succeeded, Mw2MissionDirector.Result.Success));
            var leave = Find(MissionOrder.LeaveArea, (m, o) => m.NavPoints.Any(n => n.File.Equals(o.Target, StringComparison.OrdinalIgnoreCase)));
            Run("leave: inside and boundary wait, outside succeeds", leave, f =>
            {
                f.Nav(); f.Leader.position = Vector3.zero;
                f.Step(0, "inside", Mw2MissionDirector.State.Active, Mw2MissionDirector.Result.Running);
                f.Leader.position = Vector3.right * 10;
                f.Step(0, "boundary is still inside", Mw2MissionDirector.State.Active, Mw2MissionDirector.Result.Running);
                f.Leader.position = Vector3.right * 11;
                f.Step(0, "outside", Mw2MissionDirector.State.Succeeded, Mw2MissionDirector.Result.Success);
                f.CheckReport(2, true);
            });
        }

        static bool UnitBound(Mission m, MissionOrder o) => m.Spawns.Any(s => (s.ObjectiveMask & o.Mask) != 0 && s.File.Equals(o.Target, StringComparison.OrdinalIgnoreCase));
        static bool StructureBound(Mission m, MissionOrder o) => m.Destructibles.Any(s => (s.ObjectiveMask & o.Mask) != 0 && s.File.Equals(o.Target, StringComparison.OrdinalIgnoreCase));

        // Objective_AddTarget 0x1004045b / Objective_Evaluate 0x1004154a / identification flags 0x10067668.
        void Objects()
        {
            foreach (uint mask in new uint[] { MissionOrder.Destroy })
                foreach (bool structure in new[] { false, true })
                {
                    var source = Find(mask, structure ? StructureBound : UnitBound);
                    Run("destroy " + mask + (structure ? " GT" : " GPS") + ": all matching targets required", source, f =>
                    {
                        f.Targets(structure, 2);
                        f.Step(0, "both alive", Mw2MissionDirector.State.Active, Mw2MissionDirector.Result.Running);
                        f.DestroyTarget(0);
                        f.Step(0, "one alive", Mw2MissionDirector.State.Active, Mw2MissionDirector.Result.Running);
                        f.DestroyTarget(1);
                        f.Step(0, "all destroyed", Mw2MissionDirector.State.Succeeded, Mw2MissionDirector.Result.Success);
                    });
                }
            var destroy = Find(MissionOrder.Destroy, UnitBound);
            Run("destroy: surviving target fails after deadline", destroy, f =>
            {
                f.Targets(false, 1);
                f.Step(0, "activate", Mw2MissionDirector.State.Active, Mw2MissionDirector.Result.Running);
                f.Step(2, "equal limit", Mw2MissionDirector.State.Active, Mw2MissionDirector.Result.Running);
                f.Step(3, "past limit", Mw2MissionDirector.State.Failed, Mw2MissionDirector.Result.Failure);
            }, 2);
            var protect = Find(MissionOrder.Protect, StructureBound);
            Run("protect: surviving targets succeed only past deadline", protect, f =>
            {
                f.Targets(true, 2);
                f.Step(0, "activate", Mw2MissionDirector.State.Active, Mw2MissionDirector.Result.Running);
                f.Step(2, "equal limit", Mw2MissionDirector.State.Active, Mw2MissionDirector.Result.Running);
                f.Step(3, "past limit, all stand", Mw2MissionDirector.State.Succeeded, Mw2MissionDirector.Result.Success);
            }, 2);
            Run("protect: one lost target fails the player", protect, f =>
            {
                f.Targets(true, 2);
                f.Step(0, "both stand", Mw2MissionDirector.State.Active, Mw2MissionDirector.Result.Running);
                f.DestroyTarget(0);
                f.Step(0, "one lost", Mw2MissionDirector.State.Failed, Mw2MissionDirector.Result.Failure);
            }, -1);
            Run("protect: AI partial loss stays reevaluable as FailedOther", protect, f =>
            {
                f.AsAiTeam(); f.Targets(true, 2);
                f.Step(0, "both stand", Mw2MissionDirector.State.Active, Mw2MissionDirector.Result.Running);
                f.DestroyTarget(0);
                f.Step(1, "partial loss", Mw2MissionDirector.State.FailedOther, Mw2MissionDirector.Result.Running);
                f.Step(2, "state 8 remains reevaluable", Mw2MissionDirector.State.FailedOther, Mw2MissionDirector.Result.Running);
                f.DestroyTarget(1);
                f.Step(3, "all lost becomes terminal failure", Mw2MissionDirector.State.Failed, Mw2MissionDirector.Result.Running);
            }, -1);
            foreach (bool structure in new[] { false, true })
            {
                var recon = Find(8, structure ? StructureBound : UnitBound);
                Run("recon" + (structure ? " GT" : " GPS") + ": correct team must identify every target", recon, f =>
                {
                    f.Targets(structure, 2);
                    f.Step(0, "unidentified", Mw2MissionDirector.State.Active, Mw2MissionDirector.Result.Running);
                    f.IdentifyTarget(0, 1); f.IdentifyTarget(1, 1);
                    f.Step(0, "other team identified", Mw2MissionDirector.State.Active, Mw2MissionDirector.Result.Running);
                    f.IdentifyTarget(0, 0);
                    f.Step(0, "one identified by player", Mw2MissionDirector.State.Active, Mw2MissionDirector.Result.Running);
                    f.IdentifyTarget(1, 0);
                    f.Step(0, "both identified by player", Mw2MissionDirector.State.Succeeded, Mw2MissionDirector.Result.Success);
                });
            }
            var reconTimed = Find(8, UnitBound);
            Run("recon: timeout cannot be undone by late identification", reconTimed, f =>
            {
                f.Targets(false, 1);
                f.Step(0, "activate", Mw2MissionDirector.State.Active, Mw2MissionDirector.Result.Running);
                f.Step(2, "equal limit", Mw2MissionDirector.State.Active, Mw2MissionDirector.Result.Running);
                f.Step(3, "past limit", Mw2MissionDirector.State.Failed, Mw2MissionDirector.Result.Failure);
                f.IdentifyTarget(0, 0);
                f.Step(4, "too late to identify", Mw2MissionDirector.State.Failed, Mw2MissionDirector.Result.Failure);
            }, 2);
        }

        // Mission_CheckObjectives 0x100421e6: mandatory failure/success precede team clock; optional failure does not end play.
        void TimersAndResults()
        {
            var source = Find(MissionOrder.GotoNav, (m, o) => m.NavPoints.Any(n => n.File.Equals(o.Target, StringComparison.OrdinalIgnoreCase)));
            Run("team deadline: equality ends an unfinished mission", source, f =>
            {
                f.Nav(); f.Leader.position = Vector3.right * 11; f.Team.TimeLimit = 2;
                f.Step(0, "activate", Mw2MissionDirector.State.Active, Mw2MissionDirector.Result.Running);
                f.Step(1, "before team limit", Mw2MissionDirector.State.Active, Mw2MissionDirector.Result.Running);
                f.Step(2, "at team limit", Mw2MissionDirector.State.Active, Mw2MissionDirector.Result.TimeExceeded);
                f.CheckReport(4, false);
            }, -1);
            Run("mandatory success beats simultaneous team deadline", source, f =>
            {
                f.Nav(); f.Leader.position = Vector3.right * 11; f.Team.TimeLimit = 2;
                f.Step(0, "activate", Mw2MissionDirector.State.Active, Mw2MissionDirector.Result.Running);
                f.Leader.position = Vector3.zero;
                f.Step(2, "arrival at team limit", Mw2MissionDirector.State.Succeeded, Mw2MissionDirector.Result.Success);
            }, -1);
            Run("mandatory failure beats simultaneous team deadline", source, f =>
            {
                f.Nav(); f.Leader.position = Vector3.right * 11; f.Team.TimeLimit = 3;
                f.Step(0, "activate", Mw2MissionDirector.State.Active, Mw2MissionDirector.Result.Running);
                f.Step(3, "objective and team expired", Mw2MissionDirector.State.Failed, Mw2MissionDirector.Result.Failure);
            }, 2);
            Run("optional failure does not end unfinished mandatory objective", source, f =>
            {
                f.Nav(); f.Leader.position = Vector3.right * 11;
                f.Team.Orders[0].Mandatory = false;
                f.Team.Orders.Add(new MissionOrder { Mask = MissionOrder.Hold, Mandatory = true, AllConditions = true, TimeLimit = -1 });
                f.Step(0, "activate optional and mandatory", Mw2MissionDirector.State.Active, Mw2MissionDirector.Result.Running);
                f.Step(3, "optional failed, mandatory waiting", Mw2MissionDirector.State.Failed, Mw2MissionDirector.Result.Running);
            }, 2);
        }

        // Original TRA1 MTBL: keep every record, dependency, mandatory flag, target binding and NAVP geometry.
        // Only world state/leader motion/clock are scripted. Groups 0x80000/0x20000 execute in normal table order.
        void OriginalTrainingTables()
        {
            var mission = _missions.Single(m => m.Prefix == "TRA1");
            var orders = mission.Teams.Single(t => t.Team == 0).Orders;
            int first = orders.FindIndex(o => o.Mask == MissionOrder.GotoNav);
            var source = new Source { mission = mission, team = 0, index = first, order = orders[first] };
            Run("TRA1 original full table: complete the five NAV objectives", source, f =>
            {
                f.FullMission(source);
                f.Step(0, "original start activates Alpha", Mw2MissionDirector.State.Active, Mw2MissionDirector.Result.Running);
                var route = orders.Select((o, i) => (order: o, index: i)).Where(x => x.order.Mask == MissionOrder.GotoNav).ToArray();
                if (route.Length != 5) throw new Exception("TRA1 archived route changed");
                for (int i = 0; i < route.Length; i++)
                {
                    f.GoTo(route[i].order.Target);
                    f.Step(i + 1, "visit " + route[i].order.Text, Mw2MissionDirector.State.Succeeded,
                        i == route.Length - 1 ? Mw2MissionDirector.Result.Success : Mw2MissionDirector.Result.Running, route[i].index);
                }
                f.CheckFullReport(2, true);
            });
            Run("TRA1 original full table: leaving the map triggers mission failure", source, f =>
            {
                f.FullMission(source);
                f.Step(0, "original start", Mw2MissionDirector.State.Active, Mw2MissionDirector.Result.Running);
                int leave = orders.FindLastIndex(o => o.Mask == MissionOrder.LeaveArea);
                f.Leader.position = Vector3.one * 100000;
                f.Step(1, "outside original leave NAVs", Mw2MissionDirector.State.Succeeded, Mw2MissionDirector.Result.Failure, leave);
                f.CheckFullReport(3, false);
            });
            Run("TRA1 original full table: loss of protected player triggers failure group", source, f =>
            {
                f.FullMission(source);
                f.Step(0, "original start", Mw2MissionDirector.State.Active, Mw2MissionDirector.Result.Running);
                int protect = orders.FindIndex(o => o.Mask == MissionOrder.Protect);
                f.DestroyBoundTargets(protect);
                f.Step(1, "protected GPS destroyed", Mw2MissionDirector.State.Failed, Mw2MissionDirector.Result.Failure, protect);
                f.CheckFullReport(3, false);
            });
            Run("TRA1 original full table: forbidden structure destruction triggers failure group", source, f =>
            {
                f.FullMission(source);
                f.Step(0, "original start", Mw2MissionDirector.State.Active, Mw2MissionDirector.Result.Running);
                int destroy = orders.FindIndex(o => o.Mask == MissionOrder.Destroy);
                f.DestroyBoundTargets(destroy);
                f.Step(1, "forbidden GTs destroyed", Mw2MissionDirector.State.Succeeded, Mw2MissionDirector.Result.Failure, destroy);
                f.CheckFullReport(3, false);
            });
        }

        Source StorySource(string prefix, int index, uint mask)
        {
            var mission = _missions.Single(m => m.Prefix == prefix);
            var order = mission.Teams.Single(t => t.Team == 0).Orders[index];
            if (order.Mask != mask) throw new Exception(prefix + " archived order identity changed: " + index);
            return new Source { mission = mission, team = 0, index = index, order = order };
        }

        // Original BET1/2/3 tables, Objective_Evaluate 0x1004154a / Mission_CheckObjectives 0x100421e6.
        // These retain all dependencies: fake distant NAV goals complete through original hidden groups.
        void OriginalStoryTables()
        {
            var bank = StorySource("BET1", 2, MissionOrder.Destroy);
            Run("BET1 full table: bank floor then return completes mission", bank, f =>
            {
                f.FullMission(bank);
                f.Step(0, "start bank contract", Mw2MissionDirector.State.Active, Mw2MissionDirector.Result.Running);
                f.DestroyBoundTargets(2);
                f.Step(1, "destroy required bank floor", Mw2MissionDirector.State.Succeeded, Mw2MissionDirector.Result.Running);
                f.Observe("return activates after floor destruction", Mw2MissionDirector.State.Active, Mw2MissionDirector.Result.Running, 3);
                f.GoTo(f.Team.Orders[3].Target);
                f.Step(2, "return to Nav Beta", Mw2MissionDirector.State.Succeeded, Mw2MissionDirector.Result.Success, 3);
                f.Observe("original group completes intact-floor bonus", Mw2MissionDirector.State.Succeeded, Mw2MissionDirector.Result.Success, 4);
                f.CheckFullReport(2, true);
            });
            Run("BET1 full table: losing optional floors does not block mandatory success", bank, f =>
            {
                f.FullMission(bank);
                f.Step(0, "start bank contract", Mw2MissionDirector.State.Active, Mw2MissionDirector.Result.Running);
                f.DestroyBoundTargets(4);
                f.Step(1, "forbidden floors lost", Mw2MissionDirector.State.Failed, Mw2MissionDirector.Result.Running, 4);
                f.DestroyBoundTargets(2);
                f.Step(2, "required floor destroyed", Mw2MissionDirector.State.Succeeded, Mw2MissionDirector.Result.Running);
                f.GoTo(f.Team.Orders[3].Target);
                f.Step(3, "return with failed bonus", Mw2MissionDirector.State.Succeeded, Mw2MissionDirector.Result.Success, 3);
                f.Observe("success group preserves terminal bonus failure", Mw2MissionDirector.State.Failed, Mw2MissionDirector.Result.Success, 4);
                f.CheckFullReport(2, true);
            });
            Run("BET1 full table: enemy group completes bonus without skipping bank goal", bank, f =>
            {
                f.FullMission(bank);
                f.Step(0, "start bank contract", Mw2MissionDirector.State.Active, Mw2MissionDirector.Result.Running);
                for (int i = 6; i <= 9; i++) f.DestroyBoundTargets(i);
                f.Step(1, "all four enemy bindings destroyed", Mw2MissionDirector.State.Succeeded, Mw2MissionDirector.Result.Running, 5);
                f.Observe("bank objective still required", Mw2MissionDirector.State.Active, Mw2MissionDirector.Result.Running, 2);
                f.DestroyBoundTargets(2); f.Step(2, "bank floor destroyed", Mw2MissionDirector.State.Succeeded, Mw2MissionDirector.Result.Running);
                f.GoTo(f.Team.Orders[3].Target);
                f.Step(3, "return after bank objective", Mw2MissionDirector.State.Succeeded, Mw2MissionDirector.Result.Success, 3);
                f.CheckFullReport(2, true);
            });
            var escort = StorySource("BET2", 1, MissionOrder.Protect);
            Run("BET2 full table: visiting Beta alone cannot complete escort; enemy groups can", escort, f =>
            {
                f.FullMission(escort);
                f.Step(0, "start escort contract", Mw2MissionDirector.State.Active, Mw2MissionDirector.Result.Running);
                f.GoTo(f.Team.Orders[15].Target);
                f.Step(1, "player at Beta, protected car still stands", Mw2MissionDirector.State.Active, Mw2MissionDirector.Result.Running);
                f.DestroyBoundTargets(4);
                f.Step(2, "first required enemy group destroyed", Mw2MissionDirector.State.Active, Mw2MissionDirector.Result.Running);
                f.DestroyBoundTargets(6);
                f.Step(3, "both required enemy groups destroyed", Mw2MissionDirector.State.Succeeded, Mw2MissionDirector.Result.Success);
                f.Observe("destroy-all visible goal completed by source group", Mw2MissionDirector.State.Succeeded, Mw2MissionDirector.Result.Success, 3);
                f.CheckFullReport(2, true);
            });
            Run("BET2 full table: governor car loss fails mandatory escort", escort, f =>
            {
                f.FullMission(escort);
                f.Step(0, "start escort contract", Mw2MissionDirector.State.Active, Mw2MissionDirector.Result.Running);
                f.DestroyBoundTargets(1);
                f.Step(1, "governor car destroyed", Mw2MissionDirector.State.Failed, Mw2MissionDirector.Result.Failure);
                f.CheckFullReport(3, false);
            });
            Run("BET2 full table: optional allied loss does not fail surviving escort", escort, f =>
            {
                f.FullMission(escort);
                f.Step(0, "start escort contract", Mw2MissionDirector.State.Active, Mw2MissionDirector.Result.Running);
                f.DestroyBoundTargets(2);
                f.Step(1, "optional ally lost", Mw2MissionDirector.State.Failed, Mw2MissionDirector.Result.Running, 2);
                f.DestroyBoundTargets(4); f.DestroyBoundTargets(6);
                f.Step(2, "required enemy groups destroyed", Mw2MissionDirector.State.Succeeded, Mw2MissionDirector.Result.Success);
                f.Observe("optional loss retained in successful mission", Mw2MissionDirector.State.Failed, Mw2MissionDirector.Result.Success, 2);
                f.CheckFullReport(2, true);
            });
            var rebels = StorySource("BET3", 1, MissionOrder.GotoNav);
            Run("BET3 full table: hidden destroy groups complete visible goals with next-pass dependency", rebels, f =>
            {
                f.FullMission(rebels);
                f.Step(0, "start rebel contract", Mw2MissionDirector.State.Active, Mw2MissionDirector.Result.Running);
                f.DestroyBoundTargets(2);
                f.Step(1, "first leader group destroyed", Mw2MissionDirector.State.Active, Mw2MissionDirector.Result.Running);
                f.DestroyBoundTargets(3);
                f.Step(2, "both leader groups destroyed", Mw2MissionDirector.State.Succeeded, Mw2MissionDirector.Result.Running);
                f.Observe("other forces still required", Mw2MissionDirector.State.Active, Mw2MissionDirector.Result.Running, 4);
                f.DestroyBoundTargets(6);
                f.Step(3, "one remaining force group destroyed", Mw2MissionDirector.State.Active, Mw2MissionDirector.Result.Running, 4);
                f.DestroyBoundTargets(16);
                f.Step(4, "later table record completes", Mw2MissionDirector.State.Succeeded, Mw2MissionDirector.Result.Running, 16);
                f.Observe("earlier group waits until next pass", Mw2MissionDirector.State.Active, Mw2MissionDirector.Result.Running, 4);
                f.Step(4, "next evaluator pass completes group", Mw2MissionDirector.State.Succeeded, Mw2MissionDirector.Result.Success, 4);
                f.CheckFullReport(2, true);
            });
            Run("BET3 full table: identification is not a substitute for destruction", rebels, f =>
            {
                f.FullMission(rebels);
                f.Step(0, "start rebel contract", Mw2MissionDirector.State.Active, Mw2MissionDirector.Result.Running);
                f.IdentifyBoundTargets(7); f.IdentifyBoundTargets(8);
                f.Step(1, "identify both leader groups", Mw2MissionDirector.State.Succeeded, Mw2MissionDirector.Result.Running, 7);
                f.Observe("second recon order completes", Mw2MissionDirector.State.Succeeded, Mw2MissionDirector.Result.Running, 8);
                f.Observe("visible destruction goal remains active", Mw2MissionDirector.State.Active, Mw2MissionDirector.Result.Running, 1);
            });
            foreach (var source in new[] { bank, escort, rebels })
                Run(source.mission.Prefix + " full table: original map exit fails mission", source, f =>
                {
                    f.FullMission(source);
                    f.Step(0, "start original contract", Mw2MissionDirector.State.Active, Mw2MissionDirector.Result.Running);
                    int leave = f.Team.Orders.FindLastIndex(o => o.Mask == MissionOrder.LeaveArea);
                    f.Leader.position = Vector3.one * 100000;
                    f.Step(1, "outside both original map boundaries", Mw2MissionDirector.State.Succeeded, Mw2MissionDirector.Result.Failure, leave);
                    f.CheckFullReport(3, false);
                });
        }

        // Harness-only world adapter. State dictionaries and FinishMission are never written/invoked by the fixture.
        sealed class Fixture : IDisposable
        {
            readonly GameObject _root;
            readonly Case _case;
            readonly Mw2MissionDirector _director;
            readonly List<Mw2MechCombat> _units = new List<Mw2MechCombat>();
            readonly List<Mw2Destructible> _structures = new List<Mw2Destructible>();
            int _observedOrder, _observedTeam;
            public Transform Leader { get; }
            public TeamOrders Team { get; private set; }
            public Fixture(Case c, MissionOrder source, int limit)
            {
                _case = c;
                _root = new GameObject("Isolated mission behavior: " + c.name);
                _director = _root.AddComponent<Mw2MissionDirector>(); _director.enabled = false;
                Leader = Child("Controlled leader").transform;
                _director.Player = Leader; _director.RegisterLeader(0, Leader);
                var order = JsonUtility.FromJson<MissionOrder>(JsonUtility.ToJson(source));
                order.AllConditions = true; order.ConditionTypes = "";
                order.ConditionA = order.ConditionB = Array.Empty<int>();
                order.Mandatory = true; order.Vital = true; order.TimeLimit = limit;
                Team = new TeamOrders { Team = 0, TimeLimit = 0 };
                Team.Orders.Add(order); _director.Teams.Add(Team);
            }
            GameObject Child(string name)
            { var go = new GameObject(name); go.transform.SetParent(_root.transform); return go; }
            public void Nav()
            {
                // Normalize geometry to a representable 10 m radius; retain the archived target filename.
                _director.Navs.Add(new Mw2MissionDirector.NavInfo { File = Team.Orders[0].Target, Position = Vector3.zero, Radius = 10, Type = 1 });
            }
            public void Targets(bool structure, int count)
            {
                for (int i = 0; i < count; i++)
                    if (structure)
                    {
                        var s = Child("Controlled GT " + i).AddComponent<Mw2Destructible>(); s.enabled = false;
                        s.File = Team.Orders[0].Target; s.ObjectiveMask = (int)Team.Orders[0].Mask;
                        _structures.Add(s);
                    }
                    else
                    {
                        var u = Child("Controlled GPS " + i).AddComponent<Mw2MechCombat>(); u.enabled = false;
                        u.SourceFile = Team.Orders[0].Target; u.ObjectiveMask = (int)Team.Orders[0].Mask;
                        _units.Add(u);
                    }
                typeof(Mw2MissionDirector).GetField("_units", Private).SetValue(_director, _units.ToArray());
                typeof(Mw2MissionDirector).GetField("_structures", Private).SetValue(_director, _structures.ToArray());
                if (_director.OrderTargets(_observedTeam, 0).Count != count) throw new Exception("Controlled targets did not bind to original target filename/mask");
            }
            public void AsAiTeam()
            {
                Team.Team = 1; _observedTeam = 1; _case.testTeam = 1;
                _director.RegisterLeader(1, Leader);
                var player = new TeamOrders { Team = 0, TimeLimit = 0 };
                player.Orders.Add(new MissionOrder { Mask = MissionOrder.Hold, Mandatory = true, AllConditions = true, TimeLimit = -1 });
                _director.Teams.Add(player);
            }
            public void DestroyTarget(int index)
            { if (_structures.Count > 0) _structures[index].Destroyed = true; else _units[index].Destroyed = true; }
            public void IdentifyTarget(int index, int team)
            { if (_structures.Count > 0) _structures[index].IdentifiedTeams |= 1 << team; else _units[index].IdentifiedTeams |= 1 << team; }
            public void FullMission(Source source)
            {
                var mission = source.mission;
                _case.dependenciesIsolated = false; _observedOrder = source.index;
                _director.Teams.Clear();
                foreach (var team in mission.Teams)
                    _director.Teams.Add(new TeamOrders { Team = team.Team, TimeLimit = team.TimeLimit,
                        SuccessSound = team.SuccessSound, FailureSound = team.FailureSound,
                        Orders = team.Orders.Select(o => JsonUtility.FromJson<MissionOrder>(JsonUtility.ToJson(o))).ToList() });
                Team = _director.Teams.Single(t => t.Team == 0);
                foreach (var nav in mission.NavPoints)
                    _director.Navs.Add(new Mw2MissionDirector.NavInfo { File = nav.File, Name = nav.Name,
                        Position = PolyMeshBuilder.ToUnity(nav.Position), Radius = nav.Radius, Type = nav.Type, Flags = nav.Flags, Team = nav.Team });
                foreach (var team in mission.Teams)
                {
                    if (!mission.TryGetTeamStart(team.Team, out var start)) throw new Exception("Missing archived team start");
                    var leader = team.Team == 0 ? Leader : Child("Controlled team " + team.Team).transform;
                    leader.position = PolyMeshBuilder.ToUnity(start.Position); _director.RegisterLeader(team.Team, leader);
                }
                foreach (var spawn in mission.Spawns)
                {
                    var unit = Child("Archived GPS " + spawn.File).AddComponent<Mw2MechCombat>(); unit.enabled = false;
                    unit.SourceFile = spawn.File; unit.ObjectiveMask = spawn.ObjectiveMask; unit.Team = spawn.Team; unit.IsPlayer = spawn.Player;
                    _units.Add(unit);
                }
                foreach (var gt in mission.Destructibles)
                {
                    var target = Child("Archived GT " + gt.File).AddComponent<Mw2Destructible>(); target.enabled = false;
                    target.File = gt.File; target.ObjectiveMask = gt.ObjectiveMask; target.Flags = gt.Flags;
                    _structures.Add(target);
                }
                typeof(Mw2MissionDirector).GetField("_units", Private).SetValue(_director, _units.ToArray());
                typeof(Mw2MissionDirector).GetField("_structures", Private).SetValue(_director, _structures.ToArray());
                foreach (var team in _director.Teams)
                    for (int i = 0; i < team.Orders.Count; i++)
                        if (string.Equals(team.Orders[i].Target, "userstar", StringComparison.OrdinalIgnoreCase) &&
                            mission.Spawns.Any(s => s.Player && (s.ObjectiveMask & team.Orders[i].Mask) != 0) &&
                            !_director.OrderTargets(team.Team, i).Any(t => t.Kind == AiTarget.TargetKind.Unit && t.Unit.IsPlayer))
                            throw new Exception("Original userstar order did not bind the controlled player");
            }
            public void GoTo(string target) => Leader.position = _director.Navs.First(n => n.File.Equals(target, StringComparison.OrdinalIgnoreCase)).Position;
            public void DestroyBoundTargets(int order)
            {
                var targets = _director.OrderTargets(0, order);
                if (targets.Count == 0) throw new Exception("Original table has no targets to destroy");
                foreach (var target in targets)
                    if (target.Kind == AiTarget.TargetKind.Unit) target.Unit.Destroyed = true;
                    else if (target.Kind == AiTarget.TargetKind.Structure) target.Structure.Destroyed = true;
                    else throw new Exception("Unexpected NAV in destruction fixture");
            }
            public void IdentifyBoundTargets(int order)
            {
                var targets = _director.OrderTargets(0, order);
                if (targets.Count == 0) throw new Exception("Original recon record has no bound targets");
                foreach (var target in targets)
                    if (target.Kind == AiTarget.TargetKind.Unit) target.Unit.IdentifiedTeams |= 1;
                    else if (target.Kind == AiTarget.TargetKind.Structure) target.Structure.IdentifiedTeams |= 1;
                    else throw new Exception("Unexpected NAV in recon fixture");
            }
            public void Step(float seconds, string action, Mw2MissionDirector.State state, Mw2MissionDirector.Result result, int? order = null)
            {
                typeof(Mw2MissionDirector).GetField("_missionClock", Private).SetValue(_director, seconds);
                typeof(Mw2MissionDirector).GetMethod("Update", Private).Invoke(_director, null);
                Observe(action, state, result, order);
            }
            public void Observe(string action, Mw2MissionDirector.State state, Mw2MissionDirector.Result result, int? order = null)
            {
                var observed = new Observation { action = action, seconds = _director.MissionTime, currentOrder = _director.CurrentOrder(_observedTeam),
                    visibleObjectives = Team.Orders.Select((o, i) => new ObjectiveRecord { index = i, text = o.Text,
                        state = _director.StateOf(Team.Team, i).ToString(), mask = o.Mask.ToString("x"), visible = o.Vital, mandatory = o.Mandatory })
                        .Where(o => o.visible).ToArray(),
                    team = _observedTeam, order = order ?? _observedOrder,
                    expectedState = state.ToString(), state = _director.StateOf(_observedTeam, order ?? _observedOrder).ToString(),
                    expectedResult = result.ToString(), result = _director.MissionResult.ToString() };
                observed.passed = observed.state == observed.expectedState && observed.result == observed.expectedResult;
                _case.observations.Add(observed);
            }
            public void CheckReport(int expectedState, bool complete)
            {
                byte[] report = _director.BuildReport();
                if (BitConverter.ToInt32(report, 0x10) != expectedState || BitConverter.ToInt32(report, 4) != 1 ||
                    BitConverter.ToInt32(report, 0x14) != (complete ? 1 : 0))
                    throw new Exception("Mission report does not match the evaluator outcome");
            }
            public void CheckFullReport(int expectedState, bool mandatoryComplete)
            {
                byte[] report = _director.BuildReport();
                int count = BitConverter.ToInt32(report, 4);
                if (BitConverter.ToInt32(report, 0x10) != expectedState || count != Team.Orders.Count(o => o.Vital))
                    throw new Exception("Full-table report omitted original visible objectives or changed mission result");
                bool allMandatoryComplete = true;
                for (int i = 0; i < count; i++)
                    if (BitConverter.ToInt32(report, 0x14 + i * 0x34 + 0x10) != 0 && BitConverter.ToInt32(report, 0x14 + i * 0x34) == 0)
                        allMandatoryComplete = false;
                if (allMandatoryComplete != mandatoryComplete) throw new Exception("Full-table report mandatory completion mismatch");
            }
            public void Dispose() => DestroyImmediate(_root);
        }
    }
}
