using System;
using System.Collections.Generic;
using System.IO;
using MW2.Formats;
using UnityEngine;

namespace MW2.Autopilot
{
    /// <summary>Opt-in Unity testing pilot. Actions enter the existing player input block; enemies are unchanged.</summary>
    [DefaultExecutionOrder(-1000)]
    public sealed class MechAutopilot : MonoBehaviour
    {
        public bool Active, ShowDebug;
        public bool GroupFire; // Explicit testing policy; original player fire controls1006bc57 remain unchanged.
        public bool HeadAim; // Explicit player-style targeting of visible head geometry; no enemy health input.
        public bool ObjectivesFirst; // Explicit player-style prioritization of visible mandatory installations.
        public float MinimumCombatDistance; // Optional ordinary reverse-throttle spacing from an observed close target.
        public bool FaceTarget; // Explicit tester gunnery: stop and track an exposed target inside own firing range.
        public bool LanceStandOff; // Ordinary reform command when a friendly approaches a visible hostile within120m.
        float _nextStandOff;
        public bool LanceFocusTarget; // Opt-in ordinary select-all/attack-selected-target commands only.
        public bool LanceEngageAtWill; // Ordinary select-all / engage command, sent once after power-up.
        public string HoldNav; // Explicit player order: hold the interior of a public NAV with ordinary controls.
        public float TravelBearing = float.NaN; // Explicit tester travel order; ordinary steering, no outcome or position writes.
        public PilotTactic Tactic;
        public PilotState State;
        public string Reason = "Manual control";
        public float ReticleError => _combat.LastAimError;
        public Mw2MechCombat Combat { get; private set; }
        public Mw2MechController Controller { get; private set; }
        public Mw2MissionDirector Director { get; private set; }
        public readonly AutopilotObjectives Objectives = new AutopilotObjectives();
        public readonly AutopilotSensors Sensors = new AutopilotSensors();
        public readonly AutopilotNavigation Navigation = new AutopilotNavigation();
        readonly AutopilotCombat _combat = new AutopilotCombat();
        Mw2TorsoTwist _twist;
        bool _chainBefore, _begun;
        [Serializable] public sealed class PlayerOrders
        {
            public string tactic = "Guard";
            public int preferGp = -1, aimSection = -1, travelBearing = -1, reform;
            public int[] avoidGps = Array.Empty<int>();
            public float aimTolerance = 3f, minimumCombatDistance;
            public bool groupFire, headAim = true, faceTarget = true, lanceFocus = true, standoff = true;
        }
        string _ordersFile, _lastOrders;
        float _nextOrders;
        int _reform, _pendingReform;
        bool _lanceCommandSent;
        Mw2MechCombat _lastFocusTarget;
        int _weaponBefore;
        float _now, _nextScan, _nextEvade;
        Vector3 _evadePoint;
        static MechAutopilot s_control;
        public static bool HasControl => s_control != null && s_control.Active;

        void Awake()
        {
            Combat = GetComponent<Mw2MechCombat>(); Controller = GetComponent<Mw2MechController>();
            _twist = GetComponent<Mw2TorsoTwist>();
        }
        void Update()
        {
            if (Combat == null || !Combat.IsPlayer) return;
            if (Director == null) Director = FindAnyObjectByType<Mw2MissionDirector>();
#if ENABLE_LEGACY_INPUT_MANAGER
            bool chord = (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)) &&
                         (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift));
            if (chord && Input.GetKeyDown(KeyCode.F8)) { if (Active) StopPilot(); else Begin(); }
            if (chord && Input.GetKeyDown(KeyCode.F9)) ShowDebug = !ShowDebug;
            if (chord && Input.GetKeyDown(KeyCode.F10) && Director != null) MissionTestRunner.StartInteractive(Director);
#endif
            if (Active && !_begun) Begin();
            if (Active) Mw2Input.Poll();
        }
        public void Begin()
        {
            if (_begun || Combat == null || !Combat.IsPlayer || Controller == null) return;
            Director = FindAnyObjectByType<Mw2MissionDirector>();
            if (Director == null) return;
            if (s_control != null && s_control != this) s_control.StopPilot();
            GroupFire = GroupFire || Array.IndexOf(Environment.GetCommandLineArgs(), "--pilot-group-fire") >= 0;
            HeadAim = HeadAim || Array.IndexOf(Environment.GetCommandLineArgs(), "--pilot-head-aim") >= 0;
            ObjectivesFirst = ObjectivesFirst || Array.IndexOf(Environment.GetCommandLineArgs(), "--pilot-objectives-first") >= 0;
            Objectives.CloseNav = Array.IndexOf(Environment.GetCommandLineArgs(), "--pilot-close-nav") >= 0;
            var arguments = Environment.GetCommandLineArgs();
            int ordersAt = Array.IndexOf(arguments, "--pilot-orders-file");
            if (ordersAt >= 0 && ordersAt + 1 < arguments.Length) _ordersFile = arguments[ordersAt + 1];
            int holdAt = Array.IndexOf(arguments, "--pilot-hold-nav");
            if (holdAt >= 0 && holdAt + 1 < arguments.Length) HoldNav = arguments[holdAt + 1];
            Navigation.UseMapRoute = Array.IndexOf(arguments, "--pilot-map-route") >= 0;
            FaceTarget = FaceTarget || Array.IndexOf(arguments, "--pilot-face-target") >= 0;
            LanceStandOff = LanceStandOff || Array.IndexOf(arguments, "--pilot-lance-standoff") >= 0;
            LanceFocusTarget = LanceFocusTarget || Array.IndexOf(arguments, "--pilot-lance-focus") >= 0;
            LanceEngageAtWill = LanceEngageAtWill || Array.IndexOf(arguments, "--pilot-lance-engage-at-will") >= 0;
            _combat.AvoidAutocannons = Array.IndexOf(arguments, "--pilot-no-autocannon") >= 0;
            int preferAt = Array.IndexOf(arguments, "--pilot-prefer-target-name");
            if (preferAt >= 0 && preferAt + 1 < arguments.Length) Sensors.PreferredTargetName = arguments[preferAt + 1];
            int manualAt = Array.IndexOf(arguments, "--pilot-manual-structure-name");
            if (manualAt >= 0 && manualAt + 1 < arguments.Length)
                Sensors.ManualStructureNames = arguments[manualAt + 1].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            int searchAt = Array.IndexOf(arguments, "--pilot-search-nav");
            if (searchAt >= 0 && searchAt + 1 < arguments.Length) Objectives.SearchNav = arguments[searchAt + 1];
            int routeAt = Array.IndexOf(arguments, "--pilot-route-nav");
            if (routeAt >= 0 && routeAt + 1 < arguments.Length)
                Objectives.RouteNavs = arguments[routeAt + 1].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            int avoidAt = Array.IndexOf(arguments, "--pilot-avoid-unit-name");
            if (avoidAt >= 0 && avoidAt + 1 < arguments.Length) Sensors.AvoidUnitName = arguments[avoidAt + 1];
            int bearingAt = Array.IndexOf(arguments, "--pilot-travel-bearing");
            if (bearingAt >= 0)
            {
                if (bearingAt + 1 >= arguments.Length || !float.TryParse(arguments[bearingAt + 1],
                    System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture,
                    out var bearing) || float.IsNaN(bearing) || float.IsInfinity(bearing))
                    throw new ArgumentException("--pilot-travel-bearing requires a finite heading in degrees");
                TravelBearing = Mathf.Repeat(bearing, 360f);
            }
            Active = _begun = true; s_control = this;
            _chainBefore = Mw2MechCombat.ChainFire; _weaponBefore = Combat.SelectedWeapon;
            Controller.PrepareTestPilot(); Navigation.Reset();
            _now = _nextScan = _nextEvade = 0;
            _lanceCommandSent = false;
            _lastFocusTarget = null;
            _nextStandOff = 0;
            MissionTelemetry.Note("pilot_enabled", Tactic + (GroupFire ? "; group fire" : "; single fire") +
                (Objectives.CloseNav ? "; close public NAV" : ""));
        }
        public void StopPilot()
        {
            bool owned = s_control == this;
            Active = _begun = false;
            if (owned)
            {
                s_control = null;
                Mw2MechCombat.ChainFire = _chainBefore;
                if (Combat != null) Combat.SelectedWeapon = _weaponBefore;
                if (Controller != null) Controller.PrepareTestPilot();
                if (Mw2Input.System != null)
                {
                    var input = Mw2Input.System;
                    input.SetFloat(8, 0); input.SetFloat(0x10, 0);
                    foreach (int b in new[] { 0x1d, 0x1e, 0x1f, 0x20, 0x21, 0x25, 0x27, 0x28, 0x29, 0x2a, 0x2c, 0x30, 0x37, 0x3b }) input.Block[b] = 0;
                    input.Block[0x24] = input.Block[0x18] = input.Block[0x1c] = 1;
                }
                MissionTelemetry.Note("pilot_disabled", "Manual control restored");
            }
            Reason = "Manual control";
        }
        void OnDisable() { if (_begun) StopPilot(); }

        /// <summary>Called once by Poll after normal input, before controllers/weapons consume it.</summary>
        public static void ApplyInput(InputSystem input, List<int> commands)
        {
            if (!HasControl) return;
            var pilot = s_control;
            if (pilot.Director == null || pilot.Combat == null) { pilot.StopPilot(); return; }
            commands.RemoveAll(id => id != 0x13 && id != 0x14 && id != 0x2a && id != 0x25 && !(id >= 2 && id <= 0x12));
            if (Mw2Pause.PauseMode || Mw2Input.WindowOpen) return;
            var c = pilot.Step();
            if (pilot.LanceEngageAtWill && !pilot._lanceCommandSent && pilot.Combat.PowerState == 2 &&
                !pilot.Combat.Destroyed && pilot.Director.MissionResult == Mw2MissionDirector.Result.Running)
            {
                // Input_Command100267c0: select all first, then toggle autonomous
                // engagement through the cockpit's normal command consumer.
                commands.Add(0x19); commands.Add(0x1f);
                pilot._lanceCommandSent = true;
                MissionTelemetry.Note("lance_command", "All: Engage at Will (ordinary input)");
            }
            Array.Clear(input.Block, 0, input.Block.Length);
            input.SetFloat(8, Mathf.Clamp01(c.throttle) / 64f);
            input.SetFloat(0x10, Mathf.Clamp(c.turn, -1, 1) * 1024f);
            input.SetFloat(0, c.pitch); input.SetFloat(4, c.pan);
            input.Block[0x30] = c.reverse ? (byte)1 : (byte)0;
            input.Block[0x1d] = input.Block[0x20] = c.jump ? (byte)1 : (byte)0;
            if (pilot.Sensors.HasTarget && (pilot.Sensors.Distance < 200f || pilot.Sensors.InspectTarget)) input.Block[0x3b] = 1;
            // Ordinary Q request, not a forced target: 10067b80 retains all ray,
            // hidden/range checks and the original reticle-selection state.
            if (pilot.Sensors.InspectTarget &&
                (pilot.Sensors.Structure != null && pilot.Combat.TargetStructure != pilot.Sensors.Structure ||
                 pilot.Sensors.Unit != null && pilot.Combat.Target != pilot.Sensors.Unit)) input.Block[0x37] = 1;
            pilot.ApplyLanceFocus(input, commands);
            if (c.weapon >= 0 && c.weapon < pilot.Combat.Weapons.Count) pilot.Combat.SelectedWeapon = c.weapon;
            if (Mw2MechCombat.ChainFire == pilot.GroupFire) input.Block[0x2c] = 1;
            else input.Block[0x25] = c.fire ? (byte)1 : (byte)0;
        }

        // A normal cockpit target must first be selected by Q through the actual reticle.
        // No direct Target assignment, LanceCommand call, hidden-unit or health query.
        internal void ApplyLanceFocus(InputSystem input, List<int> commands)
        {
            if (_pendingReform != _reform && Combat.PowerState == 2 && !Combat.Destroyed)
            {
                commands.Add(0x19); commands.Add(0x1e); _reform = _pendingReform;
                _lastFocusTarget = null;
                MissionTelemetry.Note("lance_command", "All: Reform (explicit ordinary player order)");
                return;
            }
            if (ApplyLanceStandOff(commands)) return;
            var target = Sensors.Unit;
            if (!LanceFocusTarget || Combat.PowerState != 2 || Combat.Destroyed ||
                Director.MissionResult != Mw2MissionDirector.Result.Running || target == null ||
                target.Destroyed || !Sensors.Attackable || !Sensors.Clear || !Combat.PilotCanSelect(target)) return;
            if (Combat.Target != target) { input.Block[0x37] = 1; return; }
            if (_lastFocusTarget == target) return;
            commands.Add(0x19); commands.Add(0x1c);
            _lastFocusTarget = target;
            MissionTelemetry.Note("lance_command", "All: Attack selected " + target.DisplayName + " (ordinary input)");
        }

        internal bool ApplyLanceStandOff(List<int> commands)
        {
            if (!LanceStandOff || Combat.Destroyed || Combat.PowerState != 2 ||
                Director.MissionResult != Mw2MissionDirector.Result.Running || Director.MissionTime < _nextStandOff) return false;
            var mates = Mw2MechAI.LanceOf(0);
            for (int i = 0; i < mates.Count && i < 2; i++)
            {
                var mate = mates[i].GetComponent<Mw2MechCombat>();
                var enemy = mate != null ? mate.Target : null;
                if (mate == null || mate.Destroyed || enemy == null || enemy.Side != 1 || enemy.Destroyed ||
                    !Combat.PilotCanSelect(enemy) || Vector3.Distance(mate.transform.position, enemy.transform.position) >= 120f) continue;
                commands.Add(i == 0 ? 0x1a : 0x1b); commands.Add(0x1e);
                _nextStandOff = Director.MissionTime + 2f;
                MissionTelemetry.Note("lance_command", mate.DisplayName + ": Reform before close contact with " + enemy.DisplayName + " (ordinary input)");
                return true;
            }
            return false;
        }

        void ReadPlayerOrders()
        {
            if (string.IsNullOrEmpty(_ordersFile) || _now < _nextOrders) return;
            _nextOrders = _now + 0.25f;
            try
            {
                var text = File.ReadAllText(_ordersFile);
                if (text == _lastOrders) return;
                var order = JsonUtility.FromJson<PlayerOrders>(text);
                if (order == null || !Enum.TryParse<PilotTactic>(order.tactic, out var tactic) ||
                    order.aimSection < -1 || order.aimSection > 7) return;
                TravelBearing = order.travelBearing >= 0 ? Mathf.Repeat(order.travelBearing, 360f) : float.NaN;
                _combat.AimTolerance = order.aimTolerance > 0 ? Mathf.Clamp(order.aimTolerance, 0.05f, 3f) : 3f;
                MinimumCombatDistance = Mathf.Max(0, order.minimumCombatDistance);
                Tactic = tactic; GroupFire = order.groupFire; HeadAim = order.headAim; FaceTarget = order.faceTarget;
                LanceFocusTarget = order.lanceFocus; LanceStandOff = order.standoff;
                Sensors.PreferredGamepiece = order.preferGp; Sensors.AvoidGamepieces = order.avoidGps ?? Array.Empty<int>();
                Sensors.AimSection = order.aimSection; _pendingReform = order.reform;
                _lastOrders = text;
                MissionTelemetry.Note("player_orders", text);
            }
            catch (IOException) { } // External controller replaces a small order file atomically.
            catch (ArgumentException) { } // Incomplete/invalid orders never enter the controls.
        }

        internal bool ApplyCombatSpacing(ref PilotControls controls)
        {
            if (MinimumCombatDistance <= 0 || Sensors.Unit == null || !Sensors.Clear) return false;
            var toward = Sensors.Point - Combat.Origin; toward.y = 0;
            if (toward.magnitude >= MinimumCombatDistance) return false;
            controls.reverse = true; controls.throttle = 1f; controls.jump = false;
            controls.turn = Mathf.Clamp(Mathf.DeltaAngle(Controller.HeadingDeg,
                Mathf.Atan2(toward.x, toward.z) * Mathf.Rad2Deg) / 45f, -1, 1) * 0.8f;
            Navigation.Reset(); Reason = "Reverse away from observed close threat";
            return true;
        }

        bool HoldingNav => Director != null && !string.IsNullOrEmpty(HoldNav) &&
            Director.PlayerNavs.Exists(n => string.Equals(n.File, HoldNav, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(n.Name, HoldNav, StringComparison.OrdinalIgnoreCase));

        internal bool ApplyHoldNav(ref PilotControls controls)
        {
            if (!HoldingNav) return false;
            var nav = Director.PlayerNavs.Find(n => string.Equals(n.File, HoldNav, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(n.Name, HoldNav, StringComparison.OrdinalIgnoreCase));
            float radius = Mathf.Max(20f, (nav.Radius > 0 ? nav.Radius : 100f) * 0.75f);
            var delta = nav.Position - transform.position; delta.y = 0;
            if (delta.magnitude > radius)
            {
                Navigation.Drive(Controller, Combat, nav.Position, 15f, _now, ref controls);
                State = PilotState.Navigation;
            }
            else
            {
                // Standing here is intentional, not failed progress toward an enemy.
                Navigation.Reset(); controls.throttle = 0; controls.jump = false;
                if (Sensors.HasTarget && Sensors.Clear)
                {
                    var toward = Sensors.Point - Combat.Origin;
                    controls.turn = Mathf.Clamp(Mathf.DeltaAngle(Controller.HeadingDeg,
                        Mathf.Atan2(toward.x, toward.z) * Mathf.Rad2Deg) / 45f, -1, 1) * 0.8f;
                }
            }
            Reason = "Hold public NAV " + nav.Name;
            return true;
        }

        PilotControls Step()
        {
            var controls = new PilotControls { weapon = -1 };
            _now += Time.deltaTime;
            ReadPlayerOrders();
            bool finished = Combat.Destroyed || Director.MissionResult != Mw2MissionDirector.Result.Running;
            if (!finished && _now >= _nextScan)
            {
                _nextScan = _now + 0.1f;
                Objectives.Update(Director, Combat, _now, Tactic);
                var previous = Sensors.TargetName;
                Physics.SyncTransforms();
                Sensors.PreferFiringAngles = AutopilotPolicy.MobileTactic(Tactic);
                Sensors.PreferVisibleHead = HeadAim;
                Sensors.PreferMandatoryStructures = ObjectivesFirst;
                Sensors.PrioritizeDefense = Tactic == PilotTactic.Guard;
                Sensors.AllowVisualUnits = Tactic == PilotTactic.Guard;
                Sensors.Scan(Combat, Director, Objectives, _now,
                    Tactic != PilotTactic.CombatFirst && Tactic != PilotTactic.UnitFirst && Tactic != PilotTactic.Guard,
                    Tactic == PilotTactic.UnitFirst);
                if (previous != Sensors.TargetName) MissionTelemetry.Note("target", Sensors.TargetName);
            }
            float integrity = 1f;
            for (int i = 0; i < Combat.Internal.Length; i++)
                if ((i == 0 || i == 2 || i == 6 || i == 7) && Combat.MaxInternal[i] > 0)
                    integrity = Mathf.Min(integrity, (float)Combat.Internal[i] / Combat.MaxInternal[i]);
            var old = State;
            State = AutopilotPolicy.Decide(finished, Sensors.HasTarget, Sensors.Clear, Navigation.Recovering,
                Objectives.HasDestination, Combat.Heat, integrity, Sensors.Distance, _combat.PreferredRange);
            bool followNav = !finished && Objectives.HasDestination && Objectives.PreferDestination;
            if (followNav) State = Navigation.Recovering ? PilotState.Reposition : PilotState.Navigation;
            if (!Sensors.HasTarget && State == PilotState.Navigation &&
                Vector3.Distance(transform.position, Objectives.Destination) <= Objectives.ArrivalRadius) State = PilotState.CompleteObjective;
            Reason = State == PilotState.Retreat ? "Critical structure; seek cover" :
                     State == PilotState.Evade ? "Heat/range; cool while moving" : State == PilotState.Reposition ? "Recover path or line of fire" :
                     State == PilotState.Engage ? "Sensed target; aim and fire" : State == PilotState.MissionFinished ? "Director result or player death" : Objectives.Description;
            if (finished || Combat.PowerState != 2 || Combat.ShutDown || Combat.PoweredDown)
            {
                if (old != State) MissionTelemetry.Note("state", State + ": " + Reason);
                return controls;
            }
            if (Tactic == PilotTactic.Guard && !Sensors.HasTarget && _twist != null)
                controls.pan = Mathf.Sin(_now * 0.8f) * _twist.Limit; // Ordinary look sweep without radar.
            if (!float.IsNaN(TravelBearing) && !float.IsInfinity(TravelBearing))
            {
                float angle = TravelBearing * Mathf.Deg2Rad;
                var destination = transform.position + new Vector3(Mathf.Sin(angle), 0, Mathf.Cos(angle)) * 1000f;
                Navigation.Drive(Controller, Combat, destination, 10f, _now, ref controls);
                State = PilotState.Navigation; Reason = "Explicit travel bearing; no discretionary combat";
                if (old != State) MissionTelemetry.Note("state", State + ": " + Reason);
                return controls;
            }
            Vector3 goal = Objectives.Destination;
            float radius = Objectives.ArrivalRadius;
            if (Sensors.HasTarget)
            {
                _combat.Aim(Combat, _twist, Sensors, Tactic, ref controls, GroupFire);
            }
            if (Sensors.HasTarget && !followNav)
            {
                var toward = Sensors.Point - transform.position; toward.y = 0;
                if (State == PilotState.Evade || State == PilotState.Retreat || State == PilotState.Reposition)
                {
                    if (State == PilotState.Reposition && Sensors.Distance > _combat.PreferredRange * 1.15f && !Navigation.Recovering)
                    {
                        // Radar acquisition at 1.75 km is not a reason to circle at the starting point.
                        goal = Sensors.Point - toward.normalized * (_combat.PreferredRange * 0.8f); radius = 15;
                    }
                    else if (State == PilotState.Reposition && !Sensors.Clear && AutopilotPolicy.MobileTactic(Tactic))
                    {
                        goal = Navigation.RepositionWaypoint(Combat, Sensors, _now); radius = 10;
                    }
                    else
                    {
                        if (_now > _nextEvade)
                        {
                            _nextEvade = _now + 3f;
                            _evadePoint = Navigation.CoverOrFlank(Combat, Sensors, State != PilotState.Reposition, ((int)(_now / 8) & 1) == 0 ? 1 : -1);
                        }
                        goal = _evadePoint; radius = 8;
                    }
                }
                else
                {
                    float orbit = Tactic == PilotTactic.Flank ? 130 : 95;
                    var lateral = Vector3.Cross(Vector3.up, toward.normalized) * (((int)(_now / 12) & 1) == 0 ? 1 : -1);
                    goal = Sensors.Point - toward.normalized * _combat.PreferredRange + lateral * orbit;
                    radius = 8;
                    // Opt-in tester tactic: keep a steady circling direction instead
                    // of reversing every twelve seconds under incoming fire.
                    // Uses only the selected visible target and own weapon range.
                    if (AutopilotPolicy.MobileTactic(Tactic))
                    {
                        lateral = Vector3.Cross(Vector3.up, toward.normalized);
                        float radial = Mathf.Clamp((toward.magnitude - _combat.PreferredRange) * 0.5f, -100f, 100f);
                        goal = transform.position + lateral * 250f + toward.normalized * radial;
                        if (Tactic == PilotTactic.Guard)
                        {
                            // A distant attacker must be intercepted; circling the
                            // protected building cannot bring it into weapon range.
                            if (Sensors.Distance > _combat.PreferredRange * 1.5f)
                                goal = Sensors.Point - toward.normalized * (_combat.PreferredRange * 0.8f);
                            else if (Objectives.Defending)
                            {
                                var offset = goal - Objectives.DefensePoint; offset.y = 0;
                                if (offset.magnitude > 300f)
                                    goal = Objectives.DefensePoint + offset.normalized * 300f;
                            }
                        }
                    }
                }
            }
            if (!followNav && Sensors.HasTarget && Sensors.InspectTarget && !Sensors.Attackable)
            {
                controls.fire = false;
                goal = Sensors.InspectionPoint; radius = 100f;
                State = PilotState.CompleteObjective;
                Reason = "Inspect selected target before firing";
            }
            if (!HoldingNav && (Sensors.HasTarget || Objectives.HasDestination)) Navigation.Drive(Controller, Combat, goal, radius, _now, ref controls);
            if (Sensors.HasTarget && State == PilotState.Engage)
            {
                if (AutopilotPolicy.MobileTactic(Tactic) && Mathf.Abs(controls.turn) < 0.65f)
                    controls.throttle = 1f;
                else if (!AutopilotPolicy.MobileTactic(Tactic) && Sensors.Distance < _combat.PreferredRange * 1.4f)
                    controls.throttle = Mathf.Min(controls.throttle, 0.55f);
            }
            // Shoot a static mandatory structure from within the selected
            // weapon's range instead of walking toward its distant bounds centre.
            if (Tactic == PilotTactic.Guard && Sensors.Structure != null && Sensors.Clear && Sensors.Attackable &&
                controls.weapon >= 0 && controls.weapon < Combat.Weapons.Count)
            {
                var weapon = Combat.Weapons[controls.weapon].Def;
                if (Sensors.Distance >= weapon.MinRangeMeters && Sensors.Distance <= AutopilotCombat.ShotRange(weapon, true))
                    controls.throttle = 0f;
            }
            if (FaceTarget && !followNav && State == PilotState.Engage && Sensors.Clear &&
                Sensors.Distance <= _combat.PreferredRange * 1.5f)
            {
                var toward = Sensors.Point - Combat.Origin;
                controls.throttle = 0;
                controls.turn = Mathf.Clamp(Mathf.DeltaAngle(Controller.HeadingDeg,
                    Mathf.Atan2(toward.x, toward.z) * Mathf.Rad2Deg) / 45f, -1, 1) * 0.8f;
                Reason = "Track visible target with chassis and turret";
            }
            if (!followNav) ApplyCombatSpacing(ref controls);
            ApplyHoldNav(ref controls);
            if (Navigation.Recovering) controls.fire = false;
            // Log the final action state. Logging before the inspection override emitted
            // Engage every frame and exhausted bounded traces without an actual transition.
            if (old != State) MissionTelemetry.Note("state", State + ": " + Reason);
            return controls;
        }
        void OnGUI()
        {
            if (!ShowDebug || Combat == null || !Combat.IsPlayer) return;
            string text = $"Test pilot: {(Active ? State.ToString() : "OFF")} / {Tactic}\nTarget: {Sensors.TargetName}\nNAV: {Objectives.LastNav}\n" +
                $"Decision: {Reason}\nHeat: {Combat.Heat:0.0}; speed: {Controller.SpeedKph:0.0} kph\nWeapon: {_combat.WeaponName}\n" +
                $"Objective: {Objectives.Description}\nMission: {(Director != null ? Director.MissionResult.ToString() : "none")}\n" +
                "Ctrl+Shift+F8 pilot; F9 debug; F10 test series";
            GUI.Box(new Rect(15, 150, 510, 215), ""); GUI.Label(new Rect(25, 160, 490, 205), text);
        }
    }
}
