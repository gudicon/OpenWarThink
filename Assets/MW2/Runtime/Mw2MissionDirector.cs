using System;
using System.Collections.Generic;
using System.Linq;
using MW2.Formats;
using UnityEngine;

namespace MW2
{
    /// <summary>
    /// Runs the MTBL team orders and the player's objectives. Order state mirrors the engine's per-entry status
    /// (DAT_102007ba: 5 succeeded, 6 failed, 8 failed protection of another team). Conditions (0x1003fca0): I at start, C order a of team b finished,
    /// S succeeded, F failed; combined with OR, or with AND when the entry's "all" flag is set.
    /// Mission result as in Mission_CheckObjectives (0x100421e6): all mandatory ('M') entries of the player's team
    /// succeeded -> success, one failed -> failure, then the team time limit -> time exceeded.
    /// Per-entry evaluation (0x1004154a) is only partly ported:
    /// go to nav, destroy structures/units, protect structures, leave area; identification 0x8
    /// currently resolves only the source's empty-target case (discovery flags are pending).
    /// </summary>
    public class Mw2MissionDirector : MonoBehaviour
    {
        public enum State { Waiting, Active, Succeeded, Failed, FailedOther }
        public enum Goal { None, Hold, Move, Attack }
        public enum Result { Running, Success, Failure, TimeExceeded }

        [Serializable]
        public struct NavInfo
        {
            public string File;
            public string Name;
            public Vector3 Position;    // Unity world space
            public float Radius;        // metres
            public int Type, Flags, Team;   // nav record +0, +0x24, +0xc
        }

        public List<TeamOrders> Teams = new List<TeamOrders>();
        public List<NavInfo> Navs = new List<NavInfo>();

        /// <summary>FTBL formation (DAT_1024e7c0, 0x70 bytes each): name and slot offsets in MW2 local metres.</summary>
        [Serializable]
        public struct FormationInfo
        {
            public string Name;
            public Vector3[] Slots;
        }
        public List<FormationInfo> Formations = new List<FormationInfo>();
        public string[] InitialFormationNames = new string[16];

        /// <summary>
        /// Nav points the player sees and selects (Map_DrawNavs 0x100504b4): those of the player's team (+0xc), not start
        /// points (+0 != 0) and not hidden (+0x24 bit 1). The objectives still find any nav point by its file.
        /// </summary>
        public List<NavInfo> PlayerNavs
        {
            get
            {
                if (_playerNavs == null) _playerNavs = Navs.FindAll(n => n.Team == PlayerTeam && n.Type != 0 && (n.Flags & 1) == 0);
                return _playerNavs;
            }
        }
        List<NavInfo> _playerNavs;
        [Tooltip("The player's mech; team 0 and the 'userstar' target.")]
        public Transform Player;

        [Tooltip("AIT rule tables 1..9 (index 0 unused); read from the game's MW2.PRJ when the scene was built without them.")]
        public AiTable[] AiTables = new AiTable[0];
        public string GameFolder = Mw2GameFolder.Default;
        public string MissionId, PlayerModel, PlayerMek;

        /// <summary>The mission statistics DAT_101fd6e0 (MW2CAR.CFG) of the running mission.</summary>
        public static CombatCareer Stats = new CombatCareer();
        /// <summary>The wrecks of the running mission (DAT_100b00d4, salvage.mw2).</summary>
        public static SalvageFile Salvage = new SalvageFile();

        [Header("Read-only")]
        public Result MissionResult;
        /// <summary>DAT_10090698: this machine's team. 0 outside a network game (FUN_1002a7c1); in one, the player's en??star team.</summary>
        public static int PlayerTeam;
        [Tooltip("DAT_100a70b0: the result is decided and the radio fell silent; the cockpit leaves the mission.")]
        public bool ExitRequested;
        [Tooltip("Seconds after the start when the result was decided (team +0x8, DAT_10200788); -1 while running.")]
        public float FinishTime = -1f;

        /// <summary>Seconds since the mission started (the start second +0x4 is 0 here; clock DAT_100a663c / 181).</summary>
        public float MissionTime => _missionClock;
        float _missionClock;

        /// <summary>Team time limit, MTBL header +0xC in seconds (team +0xC, DAT_1020078c); below 1 = none.</summary>
        public int TimeLimit(int team) => Teams.Find(t => t.Team == team)?.TimeLimit ?? 0;

        /// <summary>
        /// Formation of each team (DAT_1024f5d8 + team * 0x3c): FUN_1002a180 initializes index 0,
        /// then STAR/GRP uses FUN_1002a1ee to select the matching FTBL name.
        /// </summary>
        readonly Dictionary<int, int> _formation = new Dictionary<int, int>();

        public int FormationOf(int team)
        {
            if (_formation.TryGetValue(team, out var f)) return f;
            string name = InitialFormationNames != null && team >= 0 && team < InitialFormationNames.Length
                ? InitialFormationNames[team] : null;
            int index = Formations.FindIndex(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            return index >= 0 ? index : 0;
        }

        /// <summary>
        /// 0x1002a29e: the team takes formation n of FTBL (copied into the team's slot table); the player's team also
        /// hears "Formation change to" (0x10010aa2 -> 0x10010985(2, 0)). Members move to the new slots by lance position.
        /// </summary>
        public void SetFormation(int team, int formation)
        {
            if (formation < 0 || formation >= 0x20) return;
            _formation[team] = formation;
            if (formation >= Formations.Count || Formations[formation].Slots == null) return;
            var slots = Formations[formation].Slots;
            foreach (var mate in Mw2MechAI.LanceOf(team))
                if (mate.LancePosition > 0 && mate.LancePosition < slots.Length) mate.FormationOffset = slots[mate.LancePosition];
        }

        readonly Dictionary<int, int> _currentOrder = new Dictionary<int, int>();
        Func<int, int, int> _conditionState;

        public AiTable[] Rules
        {
            get
            {
                if (AiTables == null || AiTables.Length < 10) AiTables = Formats.AiTables.FromGame(GameFolder) ?? new AiTable[10];
                return AiTables;
            }
        }

        /// <summary>
        /// The order a team works on (DAT_10200660, chosen in Mission_CheckObjectives 0x100421e6): the first order in progress
        /// whose mask has no bits above 0xFFFF (fail triggers and condition groups never lead). -1 = none.
        /// </summary>
        public int CurrentOrder(int team) => _currentOrder.TryGetValue(team, out var o) ? o : -1;

        public MissionOrder Order(int team, int index)
        {
            var t = Teams.Find(x => x.Team == team);
            return t != null && index >= 0 && index < t.Orders.Count ? t.Orders[index] : null;
        }

        /// <summary>
        /// Objects attached to an order (Objective_AddTarget 0x1004045b): nav points, units and structures loaded from the
        /// order's target file; "userstar" is the player.
        /// </summary>
        public List<AiTarget> OrderTargets(int team, int index)
        {
            var list = new List<AiTarget>();
            var o = Order(team, index);
            if (o == null || string.IsNullOrEmpty(o.Target)) return list;
            foreach (var n in Navs)
                if (string.Equals(n.File, o.Target, StringComparison.OrdinalIgnoreCase)) list.Add(AiTarget.ForNav(n));
            foreach (var u in _units)
                if (u != null && (u.ObjectiveMask & o.Mask) != 0 &&
                    (string.Equals(u.SourceFile, o.Target, StringComparison.OrdinalIgnoreCase) ||
                                  u.IsPlayer && string.Equals(o.Target, "userstar", StringComparison.OrdinalIgnoreCase)))
                    list.Add(AiTarget.ForUnit(u));
            foreach (var s in StructuresFor(o)) list.Add(AiTarget.ForStructure(s));
            // Objective_AddTarget1004045b appends only while count <0x28.
            // Preserve the existing nonoverflow adapter; LUT4's actual overflow
            // consists solely of GTs, whose Index is their source load order.
            if (list.Count > 40)
                return list.OrderBy(t => t.Kind == AiTarget.TargetKind.Nav ? 0 : t.Kind == AiTarget.TargetKind.Unit ? 1 : 2)
                    .ThenBy(t => t.Kind == AiTarget.TargetKind.Unit ? t.Unit.GamepieceIndex :
                                 t.Kind == AiTarget.TargetKind.Structure ? t.Structure.Index : 0)
                    .Take(40).ToList();
            return list;
        }

        // Order bits seen in the player's tables (beyond those in MissionOrder): mission failure trigger.
        const uint FailMission = 0x20000;

        readonly Dictionary<(int team, int order), State> _states = new Dictionary<(int, int), State>();
        readonly Dictionary<(int team, int order), int> _started = new Dictionary<(int, int), int>();
        /// <summary>Objective state +0x6b: the second the entry left state 3 (re-set on every pass while it stays 8).</summary>
        readonly Dictionary<(int team, int order), int> _finished = new Dictionary<(int, int), int>();
        readonly HashSet<int> _voicedOrders = new HashSet<int>();
        readonly Dictionary<int, Transform> _leaders = new Dictionary<int, Transform>();
        Mw2Destructible[] _structures = new Mw2Destructible[0];
        Mw2MechCombat[] _units = new Mw2MechCombat[0];

        public void RegisterLeader(int team, Transform leader) => _leaders[team] = leader;

        public State StateOf(int team, int order) =>
            _states.TryGetValue((team, order), out var s) ? s : State.Waiting;

        void Awake()
        {
            _conditionState = (team, order) => SourceState(StateOf(team, order));
            // Frame rate capped at 60 (vsync off so the cap applies).
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = 60;
        }

        void Start()
        {
            if (Player != null) _leaders[PlayerTeam] = Player;
            _structures = FindObjectsByType<Mw2Destructible>(FindObjectsSortMode.None);
            _units = FindObjectsByType<Mw2MechCombat>(FindObjectsSortMode.None);
            // FirstShots 0x10010c60 zeroes the statistics, SimMain 0x100465ae sets the outcome to 1 and
            // AfterWorldLoader 0x100739f9 counts every gamepiece and structure by side.
            Stats = new CombatCareer();
            Salvage = new SalvageFile();
            foreach (var unit in _units)
                Stats.AddBySide(unit.Side, CombatCareer.TotalFriendlyMechs, CombatCareer.TotalEnemyMechs, CombatCareer.TotalNeutralMechs);
            foreach (var gt in _structures)
                Stats.AddBySide(gt.Side, CombatCareer.TotalFriendlyBldgs, CombatCareer.TotalEnemyBldgs, CombatCareer.TotalNeutralBldgs);
            if (Player != null && Player.TryGetComponent<Mw2MechCombat>(out var player) && string.IsNullOrEmpty(player.SourceFile))
                player.SourceFile = "userstar";
        }

        void UpdateCurrentOrders()
        {
            foreach (var team in Teams)
            {
                int current = -1;
                for (int i = 0; i < team.Orders.Count && current < 0; i++)
                    if (CanEvaluate(team.Team, i, team.Orders[i]) && (team.Orders[i].Mask & 0xFFFF0000u) == 0) current = i;
                if (CurrentOrder(team.Team) == current && _currentOrder.ContainsKey(team.Team)) continue;
                _currentOrder[team.Team] = current;
                Mw2MechAI.TeamOrderChanged(this, team.Team);       // 0x1000ba3f
            }
        }

        void Update()
        {
            // FUN_10015f3d 0x10015f3d: the clock keeps running while a window holds the sim and stops only in the pause
            // mode (PauseTimer), so it is kept on unscaled time.
            if (!Mw2Pause.PauseMode) _missionClock += Autopilot.MissionTestRunner.ClockDelta;
            // Mission_CheckObjectives 0x100421e6 calls Objective_Evaluate 0x1004154a once per
            // record in team/order order each frame. Backward dependencies wait for the next frame.
            foreach (var team in Teams)
            {
                for (int i = 0; i < team.Orders.Count; i++)
                {
                    var key = (team.Team, i);
                    var o = team.Orders[i];
                    var state = StateOf(team.Team, i);
                    // FUN_10041469 gates every pass, including already active/state-8 entries.
                    // Losing a condition pauses evaluation without discarding the original start second.
                    if (!CanEvaluate(team.Team, i, o)) continue;
                    if (state == State.Waiting)
                    {
                        _states[key] = State.Active;
                        _started[key] = Mathf.FloorToInt(MissionTime); // DAT_10200650 = ticks / 181
                        state = State.Active;
                    }
                    // Objective_Evaluate 0x1004154a returns early only for source states 5/6;
                    // state 8 (another team's failed protection) is evaluated again each tick.
                    if (state == State.Active || state == State.FailedOther)
                    {
                        var next = Evaluate(team.Team, i, o);
                        // Objective_Evaluate 0x1004154a: any state but 3 stores the second in +0x6b.
                        if (next != State.Active) _finished[key] = Mathf.FloorToInt(MissionTime);
                        if (next != state)
                        {
                            _states[key] = next;
                            if (next != State.Active) OrderFinished(team.Team, i, o, next);
                        }
                    }
                }
            }
            UpdateResult();
            UpdateCurrentOrders();
        }

        /// <summary>
        /// FUN_10040fbe (0x10040fbe): once per order of the player's team, queue its MTBL success/failure sound with
        /// "%s successful" / "%s failed" (whitespace runs collapsed by FUN_10040640 0x10040640); the text is shown
        /// only for a 'V' order whose text does not start with a space.
        /// </summary>
        void OrderFinished(int team, int index, MissionOrder order, State state)
        {
            if (team != 0 || Mw2Audio.Instance == null || !_voicedOrders.Add(index)) return;
            bool ok = state == State.Succeeded;
            string text = (order.Text ?? "") + (ok ? " successful" : " failed");
            text = order.Vital && text[0] != ' ' ? System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ") : null;
            Mw2Audio.Instance.PlayVoice(ok ? order.StartSound : order.FinishSound, text);
        }

        /// <summary>FUN_100426d3 at a regeneration: the team's orders become active again; a toggled order 0x40000 that had run is toggled back.</summary>
        public void NetResetTeam(int team)
        {
            var orders = Teams.Find(t => t.Team == team);
            if (orders == null) return;
            for (int i = 0; i < orders.Orders.Count; i++)
            {
                var o = orders.Orders[i];
                if (o.Mask == 0x40000 && StateOf(team, i) == State.Succeeded && TryGetOrder(o.TargetTeam, o.TargetOrder, out var vital)) vital.Vital = !vital.Vital;
                _states.Remove((team, i));
                _started.Remove((team, i));
                _finished.Remove((team, i));
            }
            if (team == PlayerTeam) MissionResult = Result.Running;
        }

        /// <summary>Mission_Announce 0x1004127f: team result 2/3/4, with BET68 for time exceeded.</summary>
        void FinishMission(Result result)
        {
            if (MissionResult != Result.Running) return;
            MissionResult = result;
            FinishTime = MissionTime;
            // Mission_Announce 0x1004127f: success goes out as SU (FUN_1002264e).
            if (result == Result.Success && Mw2MechCombat.NetGame && Mw2NetGame.Instance != null) Mw2NetGame.Instance.LocalSuccess();
            var team = Teams.Find(t => t.Team == PlayerTeam);
            string text = result == Result.Success ? "Mission successful" :
                          result == Result.Failure ? "Mission failed" : "Mission time exceeded";
            if (team != null && Mw2Audio.Instance != null)
                Mw2Audio.Instance.PlayVoice(result == Result.TimeExceeded ? "BET68" :
                                            result == Result.Success ? team.SuccessSound : team.FailureSound, text);
            Debug.Log($"MW2: {text}");
        }

        /// <summary>FUN_10041469 0x10041469: source-state and dependency gate shared by evaluation and current-order selection.</summary>
        bool CanEvaluate(int team, int index, MissionOrder order) =>
            order.CanEvaluate(SourceState(StateOf(team, index)), _conditionState);

        /// <summary>MTBL source states at DAT_102007ba (Mtbl_Load 0x1003fca0 / Objective_Evaluate 0x1004154a).</summary>
        static int SourceState(State state)
        {
            return state switch { State.Active => 3, State.Succeeded => 5, State.Failed => 6, State.FailedOther => 8, _ => 0 };
        }

        /// <summary>
        /// Objective_Evaluate (0x1004154a) for an active entry. timedOut = a time limit (+0x2A, seconds since activation)
        /// has passed. Destroy (1, 2): all targets destroyed -> succeeded, timed out -> failed. Protect (4): timed out ->
        /// succeeded if all targets stand, else failed; before that any target destroyed -> failed (all destroyed also
        /// failed); otherwise it stays active, so without a limit only a 0x100000 entry can complete it. Go to nav (0x100):
        /// the team leader within the nav radius (0x10040a04; other teams only while it is their current order) ->
        /// succeeded, timed out -> failed. Leave area (0x2000): leader outside -> succeeded. Start (0x10), hold (0x200,
        /// 0x400), 0x800: succeeded when timed out (a start point has no limit and succeeds at once). Group types act on
        /// entry +0x55 of team +0x53 and succeed themselves: 0x100000 sets it succeeded, 0x80000 failed (unless already
        /// done), 0x400000 back to waiting, 0x200000 resets the whole team, 0x40000 toggles its 'V', 0x10000 / 0x20000 end
        /// the team's game as won / lost. 0x20: targets identified and the team's other mandatory entries succeeded. Any
        /// other type succeeds at once.
        /// Multiplayer branches are left out.
        /// </summary>
        State Evaluate(int team, int index, MissionOrder o)
        {
            uint type = o.Mask;
            bool timedOut = _started.TryGetValue((team, index), out int t0) &&
                            MissionOrder.TimedOut(o.TimeLimit, t0, Mathf.FloorToInt(MissionTime));
            switch (type)
            {
                case MissionOrder.StartPoint:
                    return State.Succeeded;
                case MissionOrder.Hold:
                case MissionOrder.HoldWith:
                case 0x800:
                case 0:
                    return timedOut ? State.Succeeded : State.Active;
                case MissionOrder.Protect:
                {
                    var targets = OrderTargets(team, index).Where(t => t.Kind != AiTarget.TargetKind.Nav).ToList();
                    bool allStand = targets.All(t => !t.Destroyed), allDown = targets.All(t => t.Destroyed);
                    return ObjectiveProtection.State(timedOut, allStand, allDown, team == PlayerTeam) switch
                    {
                        5 => State.Succeeded, 6 => State.Failed, 8 => State.FailedOther, _ => State.Active
                    };
                }
                case 0x8:
                    // Objective_Evaluate 0x1004154a ANDs FUN_100408e6 over bound targets,
                    // starting at true. DOM1ENS6 has mask 0x7, so this order has no targets.
                    // The flags are set by 0x10067668 when the player inspects the target with I (Mw2MechCombatTargets).
                    int bit = 1 << (team & 0x1f);
                    var identified = OrderTargets(team, index).Where(t => t.Kind != AiTarget.TargetKind.Nav)
                        .All(t => t.Kind == AiTarget.TargetKind.Unit ? t.Unit != null && (t.Unit.IdentifiedTeams & bit) != 0
                                : t.Structure != null && (t.Structure.IdentifiedTeams & bit) != 0);
                    return identified ? State.Succeeded : timedOut ? State.Failed : State.Active;
                case 0x10000:
                case FailMission:
                    if (o.TargetTeam == PlayerTeam && MissionResult == Result.Running)
                        FinishMission(type == FailMission ? Result.Failure : Result.Success);
                    return State.Succeeded;
                case 0x40000:
                    if (TryGetOrder(o.TargetTeam, o.TargetOrder, out var vital)) vital.Vital = !vital.Vital;
                    return State.Succeeded;
                case 0x100000:
                case 0x80000:
                {
                    var key = (o.TargetTeam, o.TargetOrder);
                    var s = StateOf(o.TargetTeam, o.TargetOrder);
                    if (TryGetOrder(o.TargetTeam, o.TargetOrder, out _) && s != State.Succeeded && s != State.Failed)
                    {
                        var next = type == 0x100000 ? State.Succeeded : State.Failed;
                        _states[key] = next;
                        _finished[key] = Mathf.FloorToInt(MissionTime);
                        OrderFinished(o.TargetTeam, o.TargetOrder, Order(o.TargetTeam, o.TargetOrder), next);
                    }
                    return State.Succeeded;
                }
                case 0x400000:
                    _states.Remove((o.TargetTeam, o.TargetOrder));
                    _started.Remove((o.TargetTeam, o.TargetOrder));
                    _finished.Remove((o.TargetTeam, o.TargetOrder));
                    return State.Succeeded;
                case 0x200000:
                    foreach (var k in _states.Keys.Where(k => k.team == o.TargetTeam).ToList()) { _states.Remove(k); _started.Remove(k); _finished.Remove(k); }
                    return State.Succeeded;
            }

            bool done;
            if ((type & MissionOrder.GotoNav) != 0)
                // Objective_Evaluate 0x1004154a starts bVar5=true and ANDs every bound NAVP.
                // An empty target list therefore succeeds (for an AI team only on its current order).
                done = (team == PlayerTeam || CurrentOrder(team) == index) &&
                       NavIndices(o.Target).ToList().All(n => LeaderReached(team, n));
            else if ((type & MissionOrder.LeaveArea) != 0)
                // The same source loop ANDs the negated 3D arrival test; zero NAVPs also succeeds.
                done = NavIndices(o.Target).ToList().All(n => !LeaderReached(team, n));
            else if ((type & 3u) != 0) // Objective_Evaluate 0x1004154a: both types 1 and 2 destroy targets
            {
                var targets = OrderTargets(team, index).Where(t => t.Kind != AiTarget.TargetKind.Nav).ToList();
                done = targets.All(t => t.Destroyed);
            }
            else if (type == 0x20)
            {
                // Every bound target identified by the team (FUN_100408e6) and every other mandatory entry of the team
                // succeeded (state 5). No MTBL record of MW2.PRJ uses this type.
                int bit = 1 << (team & 0x1f);
                done = OrderTargets(team, index).Where(t => t.Kind != AiTarget.TargetKind.Nav)
                           .All(t => t.Kind == AiTarget.TargetKind.Unit ? t.Unit != null && (t.Unit.IdentifiedTeams & bit) != 0
                                   : t.Structure != null && (t.Structure.IdentifiedTeams & bit) != 0);
                var orders = Teams.Find(x => x.Team == team);
                for (int i = 0; orders != null && i < orders.Orders.Count; i++)
                    if (i != index && orders.Orders[i].Mandatory && StateOf(team, i) != State.Succeeded) done = false;
            }
            // Any other type (0x1000, the nuke order of LUT1 / SIG2 / SPA3 / WOL1 / MED1) matches no branch: bVar5 stays
            // true and the entry succeeds at once, so it never becomes the team's current order and the AI's nuke
            // (FUN_10013407) is never reached.
            else done = true;
            if (done) return State.Succeeded;
            return timedOut ? State.Failed : State.Active;
        }

        bool TryGetOrder(int team, int index, out MissionOrder order)
        {
            order = null;
            var t = Teams.Find(x => x.Team == team);
            if (t == null || index < 0 || index >= t.Orders.Count) return false;
            order = t.Orders[index];
            return true;
        }

        IEnumerable<Mw2Destructible> StructuresFor(MissionOrder o) =>
            _structures.Where(s => s != null && (s.ObjectiveMask & o.Mask) != 0 &&
                                   string.Equals(s.File, o.Target, StringComparison.OrdinalIgnoreCase));

        IEnumerable<int> NavIndices(string file)
        {
            for (int i = 0; i < Navs.Count; i++)
                if (string.Equals(Navs[i].File, file, StringComparison.OrdinalIgnoreCase)) yield return i;
        }

        /// <summary>
        /// Objective_LeaderReached 0x10040a04 for a NAVP: FUN_10015760 sums the three squared coordinate deltas against
        /// the radius squared (boundary inside; a nonpositive radius becomes 20000 model units). As a side effect, when
        /// the team leader is the player with power on (GP flag 0x2000) and the nav point is not a start point (+0 != 0),
        /// its first arrival sets the visited flag 0x20 and team bit and plays MECNAVPT (0xfc).
        /// </summary>
        bool LeaderReached(int team, int navIndex)
        {
            if (!_leaders.TryGetValue(team, out var leader) || leader == null) return false;
            var nav = Navs[navIndex];
            Vector3 delta = leader.position - nav.Position;
            float radius = nav.Radius > 0f ? nav.Radius : 20000f * PolyModel.UnitsToMeters;
            if (delta.sqrMagnitude > radius * radius) return false;
            if (leader == Player && nav.Type != 0 && Player.TryGetComponent<Mw2MechCombat>(out var p) && p.PowerState == 2 &&
                !p.ShutDown && MarkNavVisited(navIndex, p.Team) && Mw2Audio.Instance != null)
                Mw2Audio.Instance.Play(0xfc, Player.position, true);
            return true;
        }

        /// <summary>Nav record +0x24 flag 0x20 / +0x26 team bits at run time (0x10040a04, 0x10067668, 0x100355e0).</summary>
        [NonSerialized] public int[] NavVisitedTeams;

        public bool NavVisited(int navIndex) =>
            NavVisitedTeams != null && navIndex >= 0 && navIndex < NavVisitedTeams.Length && NavVisitedTeams[navIndex] != 0;

        /// <summary>Sets the visited flag and the team bit; true when the flag was clear (the callers then play 0xfc).</summary>
        /// <summary>
        /// FUN_100424dd 0x100424dd: MW2MSN.CFG of the player's team. The team's start second +4 is 0 here (the clock starts
        /// with the mission), its end second +8 is the second of the result or -1, the state byte +0x38 is 0 / 2 / 3 / 4.
        /// </summary>
        public byte[] BuildReport()
        {
            var team = Teams.Find(t => t.Team == PlayerTeam);
            var records = new List<MissionObjectiveResult>();
            if (team != null)
                for (int i = 0; i < team.Orders.Count; i++)
                {
                    var o = team.Orders[i];
                    records.Add(new MissionObjectiveResult
                    {
                        Visible = o.Vital, Complete = StateOf(PlayerTeam, i) == State.Succeeded, Mandatory = o.Mandatory,
                        Group = o.Kind, Text = o.Text,
                        Started = _started.TryGetValue((PlayerTeam, i), out int t0) ? t0 : -1,
                        Finished = _finished.TryGetValue((PlayerTeam, i), out int t1) ? t1 : -1
                    });
                }
            int state = MissionResult == Result.Success ? 2 : MissionResult == Result.Failure ? 3 :
                        MissionResult == Result.TimeExceeded ? 4 : 0;
            return MissionReportFile.Write(0, FinishTime >= 0 ? Mathf.FloorToInt(FinishTime) : -1, state, records);
        }

        public bool MarkNavVisited(int navIndex, int team)
        {
            if (navIndex < 0 || navIndex >= Navs.Count) return false;
            if (NavVisitedTeams == null || NavVisitedTeams.Length != Navs.Count) NavVisitedTeams = new int[Navs.Count];
            bool first = NavVisitedTeams[navIndex] == 0;
            NavVisitedTeams[navIndex] |= 1 << (team & 0x1f);
            return first;
        }

        void UpdateResult()
        {
            // Mission_CheckObjectives 0x100421e6 (0x100423f9): once the player's team is decided, the debug flag /D
            // (DAT_100a709c) is off and the radio is silent (DAT_100a65b0 = 0), DAT_100a70b0 = 1 starts the exit
            // sequence of Input_HandleCommands 0x10026391 and clears its timers.
            if (MissionResult != Result.Running)
            {
                if (!ExitRequested && (Mw2Audio.Instance == null || Mw2Audio.Instance.RadioIdle)) ExitRequested = true;
                return;
            }
            var player = Teams.Find(t => t.Team == PlayerTeam);
            if (player == null) return;
            bool allDone = true, failed = false;
            for (int i = 0; i < player.Orders.Count; i++)
            {
                var o = player.Orders[i];
                var s = StateOf(PlayerTeam, i);
                if (!o.Mandatory) continue;
                if (s == State.Failed || s == State.FailedOther) failed = true;
                if (s != State.Succeeded) allDone = false;
            }
            if (failed) FinishMission(Result.Failure);
            else if (allDone) FinishMission(Result.Success);
            // Mission_CheckObjectives 0x100422e8: DAT_10200650 is whole seconds (ticks / 181), and
            // team header +0xC is checked only after mandatory success/failure has been decided.
            else if (player.TimeLimit > 0 && Mathf.FloorToInt(MissionTime) >= player.TimeLimit)
                FinishMission(Result.TimeExceeded);
        }

        public bool TryGetNav(string file, out NavInfo nav)
        {
            foreach (var n in Navs)
                if (string.Equals(n.File, file, StringComparison.OrdinalIgnoreCase)) { nav = n; return true; }
            nav = default;
            return false;
        }

        /// <summary>Approximate movement goal for the team's current order (selection at 0x100421e6).</summary>
        public Goal GetGoal(int team, out Vector3 point, out string description)
        {
            point = default;
            description = "no orders";
            var orders = Teams.Find(t => t.Team == team);
            if (orders == null) return Goal.None;
            int i = CurrentOrder(team);
            if (i < 0 || i >= orders.Orders.Count) return Goal.None;
            var o = orders.Orders[i];
            if ((o.Mask & 3u) != 0 && string.Equals(o.Target, "userstar", StringComparison.OrdinalIgnoreCase) && Player != null)
            {
                point = Player.position;
                description = $"#{i} Attack {o.Target}";
                return Goal.Attack;
            }
            if ((o.Mask & MissionOrder.GotoNav) != 0 && TryGetNav(o.Target, out var nav))
            {
                point = nav.Position;
                description = $"#{i} Move {o.Target}";
                return Goal.Move;
            }
            if ((o.Mask & (MissionOrder.Hold | MissionOrder.HoldWith)) != 0 && TryGetNav(o.Target, out var hold))
            {
                point = hold.Position;
                description = $"#{i} Hold {o.Target}";
                return Goal.Hold;
            }
            return Goal.None;
        }

        void OnGUI()
        {
            // The result is announced on the top strip (Mission_Announce 0x1004127f); only the debug panel repeats it.
            if (!Mw2MechCombat.ShowDebug) return;
            var player = Teams.Find(t => t.Team == PlayerTeam);
            if (player == null) return;
            var lines = new List<string> { MissionResult == Result.Running ? "Objectives" : $"Objectives ({MissionResult})" };
            for (int i = 0; i < player.Orders.Count; i++)
            {
                var o = player.Orders[i];
                if (string.IsNullOrEmpty(o.Text)) continue;
                var s = StateOf(PlayerTeam, i);
                string mark = s == State.Succeeded ? "[x]" : s == State.Failed || s == State.FailedOther ? "[!]" : s == State.Active ? "[ ]" : "[-]";
                lines.Add($"{mark} {o.Text}{(o.Mandatory ? "" : " (optional)")}");
            }
            var text = new GUIContent(string.Join("\n", lines));
            float w = 330, h = GUI.skin.label.CalcHeight(text, w);
            GUI.Box(new Rect(Screen.width - w - 20, 10, w + 12, h + 12), GUIContent.none);
            GUI.Label(new Rect(Screen.width - w - 14, 16, w, h), text);
        }
    }

    /// <summary>One object bound to an MTBL order by Objective_AddTarget (0x1004045b).</summary>
    public sealed class AiTarget
    {
        public enum TargetKind { Nav, Unit, Structure }
        public TargetKind Kind;
        public Mw2MissionDirector.NavInfo Nav;
        public Mw2MechCombat Unit;
        public Mw2Destructible Structure;

        public Vector3 Position => Kind == TargetKind.Nav ? Nav.Position :
            Kind == TargetKind.Unit && Unit != null ? Unit.transform.position :
            Structure != null ? Structure.transform.position : default;
        public bool Destroyed => Kind == TargetKind.Unit ? Unit == null || Unit.Destroyed :
            Kind == TargetKind.Structure && (Structure == null || Structure.Destroyed);

        public static AiTarget ForNav(Mw2MissionDirector.NavInfo nav) => new AiTarget { Kind = TargetKind.Nav, Nav = nav };
        public static AiTarget ForUnit(Mw2MechCombat unit) => new AiTarget { Kind = TargetKind.Unit, Unit = unit };
        public static AiTarget ForStructure(Mw2Destructible structure) => new AiTarget { Kind = TargetKind.Structure, Structure = structure };
    }
}
