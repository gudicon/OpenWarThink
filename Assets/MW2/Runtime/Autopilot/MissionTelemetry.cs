using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;

namespace MW2.Autopilot
{
    /// <summary>Read-only observer; full-world telemetry is never fed back into the pilot's sensors.</summary>
    public sealed class MissionTelemetry : IDisposable
    {
        static MissionTelemetry s_current;
        public readonly MissionRunRecord Record;
        readonly Mw2MissionDirector _director;
        readonly MechAutopilot _pilot;
        readonly StreamWriter _events;
        readonly StreamWriter _tactical;
        readonly Dictionary<int, string> _tacticalStates = new Dictionary<int, string>();
        readonly Dictionary<(int, int), string> _tacticalOrders = new Dictionary<(int, int), string>();
        float _nextTactical;
        readonly string _folder;
        readonly MissionValidationMode _validation;
        readonly List<string> _errors = new List<string>();
        readonly List<TimelineEvent> _timeline = new List<TimelineEvent>();
        readonly Dictionary<int, string> _objectiveStates = new Dictionary<int, string>();
        readonly Dictionary<(int team, int order), List<string>> _teamStates = new Dictionary<(int, int), List<string>>();
        readonly Dictionary<Mw2MechCombat, bool> _deaths = new Dictionary<Mw2MechCombat, bool>();
        readonly Dictionary<Mw2MechCombat, bool> _airborne = new Dictionary<Mw2MechCombat, bool>();
        readonly List<WeaponRecord> _weapons = new List<WeaponRecord>();
        int[] _armor, _rear, _internal;
        float _nextSample, _nextPosition;
        bool _finished;
        int _lastCritical, _criticalFrame = -1;
        readonly System.Diagnostics.Stopwatch _watch = System.Diagnostics.Stopwatch.StartNew();
        public bool HasErrors => _errors.Count > 0;

        public MissionTelemetry(MissionTestPlan plan, Mw2MissionDirector director, MechAutopilot pilot, string folder)
        {
            _director = director; _pilot = pilot; _folder = folder;
            Directory.CreateDirectory(folder);
            if (File.Exists(Path.Combine(folder, "result.json"))) throw new IOException("Attempt already exists: " + folder);
            _events = new StreamWriter(Path.Combine(folder, "events.jsonl"), false) { AutoFlush = true };
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "--pilot-tactical-log") >= 0)
                _tactical = new StreamWriter(Path.Combine(folder, "tactical.jsonl"), false);
            var options = Mw2Options.GameOptions;
            Record = new MissionRunRecord { mission = director.MissionId, mech = director.PlayerModel, mek = director.PlayerMek,
                mode = plan.mode, tactic = plan.tactic, seed = plan.seed, attempt = plan.attempt,
                difficulty = options.Difficulty, invulnerable = options.Invulnerable, unlimitedAmmo = options.UnlimitedAmmo,
                playerHeat = options.PlayerHeat, fairEligible = plan.mode == "Fair" && !options.Invulnerable && !options.UnlimitedAmmo && options.PlayerHeat,
                processId = System.Diagnostics.Process.GetCurrentProcess().Id,
                runtimeAssemblySha256 = Hash(typeof(MissionTelemetry).Assembly.Location),
                originalDllSha256 = Hash(Path.Combine(plan.gameFolder, "mercsw.dll")) };
            _armor = (int[])pilot.Combat.Front.Clone(); _rear = (int[])pilot.Combat.Rear.Clone(); _internal = (int[])pilot.Combat.Internal.Clone();
            foreach (var w in pilot.Combat.Weapons) _weapons.Add(new WeaponRecord { name = w.Def.Name, group = w.Group, ammo = w.Ammo });
            s_current = this;
            if (plan.mode == "Validation") _validation = new MissionValidationMode(director, plan.invulnerable);
            Application.logMessageReceived += OnLog;
            Event("start", plan.mode + "; explicit loadout " + plan.mech + "/" + plan.mek);
        }
        public static void Note(string kind, string detail)
        {
            if (s_current == null) return;
            s_current.Event(kind, detail);
            if (kind == "player_orders" || kind == "lance_command") s_current.TacticalEvent(kind, null, detail);
        }
        static string Hash(string file)
        {
            if (!File.Exists(file)) return "absent"; // e.g. mercsw.dll once its tables are extracted
            using (var algorithm = System.Security.Cryptography.SHA256.Create())
            using (var stream = File.OpenRead(file)) return BitConverter.ToString(algorithm.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }
        public static void Damage(Mw2MechCombat unit, int location, float amount, bool rear, Mw2MechCombat source)
        {
            var t = s_current;
            if (t != null) t.TacticalEvent("damage", unit, "section=" + location + ";amount=" + amount.ToString(CultureInfo.InvariantCulture) + ";rear=" + rear + ";sourceId=" + (source != null ? source.GetInstanceID() : 0));
            if (t != null && unit == t._pilot.Combat)
            {
                t.Record.rawDamageTaken += amount;
                t.Event("damage", string.Format(CultureInfo.InvariantCulture, "section={0};amount={1:0.###};rear={2};source={3}",
                    location, amount, rear, source != null ? source.DisplayName : "unknown"));
            }
        }
        public static void Critical(Mw2MechCombat unit, int location, int item, bool silent, Mw2MechCombat source)
        {
            var t = s_current;
            if (t != null) t.TacticalEvent("critical", unit, "section=" + location + ";item=" + item + ";silent=" + silent + ";sourceId=" + (source != null ? source.GetInstanceID() : 0));
            if (t != null && unit == t._pilot.Combat)
            {
                t._lastCritical = item; t._criticalFrame = Time.frameCount;
                t.Event("critical", "section=" + location + ";item=" + item + ";silent=" + silent + ";source=" + (source != null ? source.DisplayName : "unknown"));
            }
        }
        public static void Death(Mw2MechCombat unit, Mw2MechCombat source)
        {
            var t = s_current;
            if (t == null) return;
            if (unit == t._pilot.Combat)
                t.Record.deathMechanism = t._criticalFrame == Time.frameCount && t._lastCritical >= 10000 ? "AmmoCriticalChain" :
                    t._criticalFrame == Time.frameCount && t._lastCritical == 7750 ? "EngineCritical" :
                    unit.Heat > 100 && source == unit ? "OverheatDestruction" :
                    source == unit ? "SelfAttributedDestructionTraceRequired" : "DamageDestruction";
            t.TacticalEvent("death", unit, "sourceId=" + (source != null ? source.GetInstanceID() : 0) + ";source=" + (source != null ? source.DisplayName : "unknown"));
            t.Event("death", unit.DisplayName + ";source=" + (source != null ? source.DisplayName : "unknown") + ";heat=" + unit.Heat.ToString(CultureInfo.InvariantCulture));
            if (!unit.IsPlayer) t._deaths[unit] = true;
        }
        public static void Shot(Mw2MechCombat owner, Mw2MechCombat.MountedWeapon weapon)
        {
            var t = s_current;
            if (t == null) return;
            t.TacticalEvent("shot", owner, weapon.Def.Name + ";group=" + weapon.Group + ";heat=" + owner.Heat.ToString(CultureInfo.InvariantCulture) + ";targetId=" + (owner.Target != null ? owner.Target.GetInstanceID() : 0));
            if (owner != t._pilot.Combat) return;
            int index = owner.Weapons.IndexOf(weapon);
            if (index < 0 || index >= t._weapons.Count) return;
            t._weapons[index].shots++; t._weapons[index].damagePotential += weapon.Def.Damage;
            t.Event("shot", weapon.Def.Name + "; target=" + t._pilot.Sensors.TargetName);
        }
        public void Event(string kind, string detail)
        {
            if (_finished) return;
            if (_timeline.Count >= 10000) { Record.timelineTruncated = true; return; }
            var e = new TimelineEvent { seconds = _director != null ? _director.MissionTime : 0, kind = kind, detail = detail };
            _timeline.Add(e);
            try { _events.WriteLine(ReportJson.Write(e)); }
            catch (IOException failure) { if (_errors.Count < 100) _errors.Add("Telemetry write failed: " + failure.Message); }
        }
        void OnLog(string message, string trace, LogType type)
        {
            if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
            { if (_errors.Count < 100) _errors.Add(message + "\n" + trace); Event("unity_error", message); }
        }
        public void Sample()
        {
            if (_finished || _director == null || _pilot.Combat == null) return;
            SampleTactical();
            if (_director.MissionTime < _nextSample) return;
            _nextSample = _director.MissionTime + 0.1f;
            if (_validation != null) _validation.Sample();
            var p = _pilot.Combat;
            for (int i = 0; i < _internal.Length; i++)
            {
                Record.damageTaken += Mathf.Max(0, _armor[i] - p.Front[i]) + Mathf.Max(0, _rear[i] - p.Rear[i]) + Mathf.Max(0, _internal[i] - p.Internal[i]);
                if (_internal[i] > 0 && p.Internal[i] <= 0) Event("component_lost", "section=" + i);
                _armor[i] = p.Front[i]; _rear[i] = p.Rear[i]; _internal[i] = p.Internal[i];
            }
            Record.peakHeat = Mathf.Max(Record.peakHeat, p.Heat);
            foreach (var orders in _director.Teams)
                for (int i = 0; i < orders.Orders.Count; i++)
                {
                    var key = (orders.Team, i);
                    string state = _director.StateOf(orders.Team, i).ToString();
                    if (!_teamStates.TryGetValue(key, out var history)) _teamStates[key] = history = new List<string>();
                    if (history.Count == 0 || history[history.Count - 1] != state) history.Add(state);
                }
            var team = _director.Teams.Find(t => t.Team == 0);
            for (int i = 0; team != null && i < team.Orders.Count; i++)
            {
                string state = _director.StateOf(0, i).ToString();
                if (!_objectiveStates.TryGetValue(i, out string old) || old != state)
                { _objectiveStates[i] = state; Event("objective", i + ": " + team.Orders[i].Text + " -> " + state); }
            }
            foreach (var unit in Mw2MechCombat.All)
            {
                if (unit == null || unit.IsPlayer) continue;
                if (!_deaths.TryGetValue(unit, out bool dead)) { dead = false; Event("unit_registered", unit.DisplayName + ";flags=" + unit.GamepieceFlags); }
                if (unit.Destroyed && !dead) Event("unit_destroyed", unit.DisplayName + "; side=" + unit.Side);
                _deaths[unit] = unit.Destroyed;
                if (unit.TryGetComponent<Mw2MechController>(out var c))
                {
                    bool old = _airborne.TryGetValue(unit, out bool a) && a;
                    if (old != c.Airborne) Event(c.Airborne ? "airborne" : "landed", unit.DisplayName);
                    _airborne[unit] = c.Airborne;
                }
            }
            if (_director.MissionTime >= _nextPosition)
            {
                _nextPosition = _director.MissionTime + 5f;
                var v = p.transform.position;
                Event("position", string.Format(CultureInfo.InvariantCulture, "x={0:0.0};z={1:0.0};heat={2:0.0};state={3};target={4};recoveries={5};heading={6:0.0};speed={7:0.0};range={8:0.0}",
                    v.x, v.z, p.Heat, _pilot.State, _pilot.Sensors.TargetName, _pilot.Navigation.Recoveries,
                    _pilot.Controller.HeadingDeg, _pilot.Controller.SpeedKph, _pilot.Sensors.Distance));
                if (_pilot.Sensors.InspectTarget)
                    Event("inspection", "requested=" + _pilot.Sensors.TargetName +
                        ";selected=" + (p.Target != null ? p.Target.DisplayName : p.TargetStructure != null ? p.TargetStructure.DisplayName : "none") +
                        ";status=" + p.InspectStatus + ";positionRange=" + Vector3.Distance(v, _pilot.Sensors.InspectionPoint).ToString("0.0", CultureInfo.InvariantCulture));
                if (_pilot.Sensors.Structure != null && _pilot.Sensors.Attackable)
                    Event("structure_target", _pilot.Sensors.TargetName + ";strength=" +
                        _pilot.Sensors.Structure.Strength.ToString("0.0", CultureInfo.InvariantCulture));
            }
        }
        public MissionRunRecord Finish(string result, string classification, string reason)
        {
            _nextSample = 0;
            Sample(); Event("finish", result + ": " + reason);
            var p = _pilot.Combat;
            Record.durationSeconds = _director.MissionTime; Record.wallSeconds = (float)_watch.Elapsed.TotalSeconds;
            Record.result = result; Record.classification = classification; Record.reason = reason;
            Record.directorResult = _director.MissionResult.ToString();
            Record.lastNav = _pilot.Objectives.LastNav; Record.finalPilotState = _pilot.State.ToString(); Record.lastDecision = _pilot.Reason;
            Record.deathSource = p.Destroyed ? p.DeathSourceName : "";
            Record.armor = (int[])p.Front.Clone(); Record.rearArmor = (int[])p.Rear.Clone(); Record.structure = (int[])p.Internal.Clone();
            Record.enemiesDestroyed = _deaths.Count(x => x.Key != null && x.Key.Side == 1 && x.Value);
            Record.enemiesRemaining = Mw2MechCombat.All.Count(x => x != null && x.Side == 1 && !x.Destroyed);
            Record.enemiesDestroyedByPlayer = Mw2MissionDirector.Stats[Formats.CombatCareer.EnemyMechKills] + Mw2MissionDirector.Stats[Formats.CombatCareer.EnemyOtherKills];
            var team = _director.Teams.Find(t => t.Team == 0);
            var objectives = new List<ObjectiveRecord>();
            for (int i = 0; team != null && i < team.Orders.Count; i++)
            { var o = team.Orders[i]; objectives.Add(new ObjectiveRecord { index = i, text = o.Text, mask = o.Mask.ToString("x"),
                state = _director.StateOf(0, i).ToString(), visible = o.Vital, mandatory = o.Mandatory }); }
            Record.objectives = objectives.ToArray();
            Record.allTeamObjectives = _director.Teams.SelectMany(orders => orders.Orders.Select((o, i) => new TeamObjectiveRecord
            {
                team = orders.Team, index = i, target = o.Target, mask = o.Mask.ToString("x"),
                state = _director.StateOf(orders.Team, i).ToString(), visible = o.Vital, mandatory = o.Mandatory,
                boundTargets = _director.OrderTargets(orders.Team, i).Count,
                observedStates = _teamStates.TryGetValue((orders.Team, i), out var history) ? history.ToArray() : Array.Empty<string>()
            })).ToArray();
            for (int i = 0; i < _weapons.Count; i++) _weapons[i].ammo = p.Weapons[i].Ammo;
            Record.weapons = _weapons.ToArray(); Record.errors = _errors.ToArray(); Record.timeline = _timeline.ToArray();
            if (_validation != null) { _validation.Finish(); Record.validation = _validation.Coverage; }
            TacticalSnapshot("finish");
            _finished = true;
            string temporary = Path.Combine(_folder, "result.json.tmp");
            File.WriteAllText(temporary, Record.ToJson()); File.Move(temporary, Path.Combine(_folder, "result.json"));
            Dispose(); return Record;
        }
        // Optional read-only diagnostic stream. Never read by sensors, AI, controls or the evaluator.
        // Every rendered simulation frame is observed; the old bounded report remains unchanged.
        [Serializable] sealed class UnitObservation
        {
            public int id, gp, team, side, flags, armorFactor, power, targetId;
            public string name, file, aiState, aiStatus, deathSource;
            public bool destroyed, poweredDown, shutdown, legLost, trigger;
            public float x, y, z, heat, speedFactor;
            public int[] front, rear, structure;
        }
        [Serializable] sealed class TacticalObservation
        {
            public float seconds; public string kind, detail; public int unitId;
            public UnitObservation[] units;
            public float reticleError, sensorRange; public bool clear, attackable; public int sensorGp; public string sensorTarget;
        }
        void TacticalEvent(string kind, Mw2MechCombat unit, string detail)
        {
            if (_tactical == null || _finished) return;
            _tactical.WriteLine(ReportJson.Write(new TacticalObservation { seconds = _director.MissionTime,
                kind = kind, unitId = unit != null ? unit.GetInstanceID() : 0, detail = detail }));
        }
        UnitObservation Observe(Mw2MechCombat u)
        {
            u.TryGetComponent<Mw2MechAI>(out var ai);
            var v = u.transform.position;
            return new UnitObservation { id = u.GetInstanceID(), gp = u.GamepieceIndex, name = u.DisplayName,
                file = u.SourceFile, team = u.Team, side = u.Side, flags = u.GamepieceFlags, armorFactor = u.ArmorFactor,
                power = u.PowerState, poweredDown = u.PoweredDown, shutdown = u.ShutDown, destroyed = u.Destroyed,
                legLost = u.LegLost, speedFactor = u.SpeedFactor, heat = u.Heat, trigger = u.Trigger,
                targetId = u.Target != null ? u.Target.GetInstanceID() : 0,
                aiState = ai != null ? ai.PilotState.ToString() : _pilot.State.ToString(),
                aiStatus = ai != null ? ai.Status : _pilot.Reason, deathSource = u.DeathSourceName,
                x = v.x, y = v.y, z = v.z, front = u.Front, rear = u.Rear, structure = u.Internal };
        }
        void TacticalSnapshot(string kind)
        {
            if (_tactical == null) return;
            _tactical.WriteLine(ReportJson.Write(new TacticalObservation { seconds = _director.MissionTime,
                kind = kind, reticleError = _pilot.ReticleError, sensorRange = _pilot.Sensors.Distance, clear = _pilot.Sensors.Clear, attackable = _pilot.Sensors.Attackable, sensorGp = _pilot.Sensors.Unit != null ? _pilot.Sensors.Unit.GamepieceIndex : -1, sensorTarget = _pilot.Sensors.TargetName, units = Mw2MechCombat.All.Where(u => u != null).Select(Observe).ToArray() }));
            _tactical.Flush();
        }
        void SampleTactical()
        {
            if (_tactical == null) return;
            bool changed = false;
            foreach (var u in Mw2MechCombat.All)
            {
                if (u == null) continue;
                string state = u.GamepieceFlags + ";power=" + u.PowerState + ";leg=" + u.LegLost + ";speedFactor=" + u.SpeedFactor.ToString(CultureInfo.InvariantCulture);
                int id = u.GetInstanceID();
                if (!_tacticalStates.TryGetValue(id, out var old) || old != state)
                { TacticalEvent("unit_state", u, state); _tacticalStates[id] = state; changed = true; }
            }
            foreach (var team in _director.Teams)
                for (int i = 0; i < team.Orders.Count; i++)
                {
                    string state = _director.StateOf(team.Team, i).ToString();
                    var key = (team.Team, i);
                    if (!_tacticalOrders.TryGetValue(key, out var old) || old != state)
                    {
                        TacticalEvent("order", null, "team=" + team.Team + ";order=" + i + ";state=" + state + ";target=" + team.Orders[i].Target);
                        _tacticalOrders[key] = state; changed = true;
                    }
                }
            if (changed || _director.MissionTime >= _nextTactical)
            { TacticalSnapshot(changed ? "transition" : "sample"); _nextTactical = _director.MissionTime + 1f; }
        }
        public void Dispose()
        {
            Application.logMessageReceived -= OnLog;
            if (s_current == this) s_current = null;
            _events.Dispose();
            _tactical?.Dispose();
        }
    }
}
