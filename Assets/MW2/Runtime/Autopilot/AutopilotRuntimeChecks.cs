using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MW2.Formats;
using UnityEngine;

namespace MW2.Autopilot
{
    /// <summary>Separate synthetic integration check process; never counted as a fair mission attempt.</summary>
    public sealed class AutopilotRuntimeChecks : MonoBehaviour
    {
        public MissionTestPlan Plan;
        readonly List<string> _checks = new List<string>();
        readonly List<string> _errors = new List<string>();
        void Check(bool condition, string name) { if (!condition) throw new Exception(name); _checks.Add(name); }
        void Log(string message, string stack, LogType type)
        { if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert) _errors.Add(message); }
        IEnumerator Start()
        {
            Application.logMessageReceived += Log;
            Application.runInBackground = true;
            Time.captureFramerate = 60; Application.targetFrameRate = -1;
            Mw2MissionDirector director = null;
            Exception error = null;
            try
            {
                Mw2Input.GameFolder = Plan.gameFolder;
                if (Camera.main == null) { var c = new GameObject("Self-test camera") { tag = "MainCamera" }; c.AddComponent<Camera>(); }
                using (var prj = new PrjArchive(Path.Combine(Plan.gameFolder, "MW2.PRJ")))
                    director = Mw2MissionBuilder.Build(prj, Plan.gameFolder, "BET3", "COMMANDO", 1, "CMD00STD").GetComponent<Mw2MissionDirector>();
            }
            catch (Exception e) { error = e; }
            yield return null; yield return null;
            // Capture a fresh real mission, before any structural, singleton or campaign teardown fixtures run.
            if (error == null && Environment.GetCommandLineArgs().Contains("--systems-capture"))
            {
                var capture = CaptureSystemsStatus();
                while (true)
                {
                    bool more;
                    try { more = capture.MoveNext(); }
                    catch (Exception e) { _errors.Add(e.ToString()); break; }
                    if (!more) break;
                    yield return capture.Current;
                }
            }
            try
            {
                if (error != null) throw error;
                if (Environment.GetCommandLineArgs().Contains("--mw2-mute"))
                    Check(AudioListener.volume == 0f && !AudioListener.pause,
                        "Muted launch silences the mixer without pausing voice playback clocks");
                var pilot = director.Player.GetComponent<MechAutopilot>();
                Mw2Input.Poll(); var input = Mw2Input.System;
                var commands = new List<int> { 0x64 };
                input.SetFloat(8, 0.01f); input.Block[0x25] = 1;
                MechAutopilot.ApplyInput(input, commands);
                Check(input.Float(8) == 0.01f && input.Block[0x25] == 1 && commands.Count == 1, "Disabled pilot leaves human input unchanged");
                var objectives = new AutopilotObjectives(); objectives.Update(director, pilot.Combat, 0);
                Check(objectives.HasDestination && director.PlayerNavs.Any(n => (n.Position - objectives.Destination).sqrMagnitude < 1),
                    "BET3 uses a public NAV instead of 40 km dummy objectives");
                var closeNav = new AutopilotObjectives { CloseNav = true };
                closeNav.Update(director, pilot.Combat, 0);
                Check(closeNav.HasDestination && closeNav.Destination == objectives.Destination &&
                    closeNav.ArrivalRadius <= 60f && closeNav.ArrivalRadius <= objectives.ArrivalRadius,
                    "Close-NAV tester retains the public destination and only narrows its steering arrival radius");
                var enemy = Mw2MechCombat.All.First(u => u.Side == 1);
                CheckWaitingPublicNavSearch(pilot.Combat);
                CheckMapRoute(pilot.Controller);
                var route = new AutopilotNavigation();
                var routeSensor = new AutopilotSensors { Unit = enemy, Point = pilot.transform.position + Vector3.forward * 300f };
                var routeStart = pilot.transform.position;
                try
                {
                    var waypoint = route.RepositionWaypoint(pilot.Combat, routeSensor, 1f);
                    pilot.transform.position += Vector3.right * 30f;
                    Check(route.RepositionWaypoint(pilot.Combat, routeSensor, 4f) == waypoint,
                        "Mobile obstruction flank stays fixed across the former three-second moving-goal interval");
                    pilot.transform.position = waypoint;
                    Check((route.RepositionWaypoint(pilot.Combat, routeSensor, 5f) - waypoint).sqrMagnitude > 100f,
                        "Mobile obstruction flank advances only after reaching its retained waypoint");
                }
                finally { pilot.transform.position = routeStart; Physics.SyncTransforms(); }
                var inspectionSensor = new AutopilotSensors { Unit = enemy };
                Check(inspectionSensor.InspectionPoint == enemy.Origin,
                    "Inspection steering uses the same GP origin as native target validation");
                int flags = enemy.UnitFlags;
                try
                {
                    enemy.UnitFlags |= 0x800;
                    var sensor = new AutopilotSensors(); sensor.Scan(pilot.Combat, director, objectives, 1);
                    Check(sensor.Unit != enemy, "Hidden target is unavailable to pilot sensors");
                }
                finally { enemy.UnitFlags = flags; }
                CheckPilotHostileStructures(director);
                CheckManualStructures(director);
                CheckHollowStructureAim(director.Player.GetComponent<Mw2MechCombat>());
                CheckChildGtCascade(director.Player.GetComponent<Mw2MechCombat>());
                CheckStructureEyeVisibility(pilot.Combat);
                CheckExposedUnitAim(director, pilot.Combat);
                var airborne = new AutopilotSensors { Unit = enemy, Point = pilot.Combat.Origin + Vector3.forward * 120 + Vector3.up * 15,
                    ObservedVelocity = Vector3.up * 10, Clear = true, Attackable = true, Distance = 121 };
                var gunner = new AutopilotCombat(); var control = new PilotControls();
                gunner.Aim(pilot.Combat, pilot.GetComponent<Mw2TorsoTwist>(), airborne, PilotTactic.Balanced, ref control);
                Check(control.weapon >= 0 && control.pitch > 0 && control.pitch < 60 && !float.IsNaN(control.pan),
                    "Airborne moving target produces finite upward weapon aim without changing chassis controls");
                bool chain = Mw2MechCombat.ChainFire;
                int weapon = pilot.Combat.SelectedWeapon;
                pilot.Begin();
                Check(MechAutopilot.HasControl && pilot.Controller.KeyboardControl, "Bot owns input through original keyboard controller");
                CheckHoldNav(pilot, director);
                input.SetFloat(8, 0.01f); MechAutopilot.ApplyInput(input, commands);
                Check(input.Float(8) != 0.01f && !commands.Contains(0x64), "Active pilot replaces only player control requests");
                var travelPosition = pilot.transform.position;
                var travelPowerField = typeof(Mw2MechCombat).GetField("_toneState",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                var travelPower = travelPowerField.GetValue(pilot.Combat);
                try
                {
                    travelPowerField.SetValue(pilot.Combat, 2);
                    Check(AutopilotCombat.SteadyAim(0.2f, 0.35f) && !AutopilotCombat.SteadyAim(0.4f, 0.35f),
                        "Precise tester fire waits for reticle alignment without changing the native fire gate");
                    var spacingPoint = pilot.Sensors.Point; var spacingUnit = pilot.Sensors.Unit; bool spacingClear = pilot.Sensors.Clear;
                    var spacingPosition = pilot.transform.position;
                    try
                    {
                        pilot.Sensors.Unit = enemy; pilot.Sensors.Point = pilot.Combat.Origin + Vector3.forward * 100; pilot.Sensors.Clear = true;
                        var spacingControl = new PilotControls();
                        Check(!pilot.ApplyCombatSpacing(ref spacingControl), "Combat spacing is opt-in");
                        pilot.MinimumCombatDistance = 200;
                        Check(pilot.ApplyCombatSpacing(ref spacingControl) && spacingControl.reverse && spacingControl.throttle > 0 &&
                            pilot.transform.position == spacingPosition, "Close-threat spacing requests ordinary reverse/turn controls without moving the mech directly");
                    }
                    finally { pilot.MinimumCombatDistance = 0; pilot.Sensors.Point = spacingPoint; pilot.Sensors.Unit = spacingUnit; pilot.Sensors.Clear = spacingClear; }

                    var focusUnit = pilot.Sensors.Unit; bool focusClear = pilot.Sensors.Clear, focusAttack = pilot.Sensors.Attackable;
                    var selectedTarget = pilot.Combat.Target;
                    try
                    {
                        pilot.Sensors.Unit = enemy; pilot.Sensors.Clear = pilot.Sensors.Attackable = true;
                        pilot.Combat.Target = enemy; commands.Clear();
                        pilot.ApplyLanceFocus(input, commands);
                        Check(!commands.Contains(0x1c), "Lance focus is opt-in and leaves normal commands unchanged");
                        Check(!pilot.ApplyLanceStandOff(commands), "Standoff is opt-in and does not issue unsolicited ally commands");
                        pilot.LanceStandOff = true;
                        Check(!pilot.ApplyLanceStandOff(commands), "Standoff without a close hostile ally target issues no reform command");
                        pilot.LanceStandOff = false;
                        pilot.LanceFocusTarget = true; pilot.Combat.Target = null; input.Block[0x37] = 0;
                        pilot.ApplyLanceFocus(input, commands);
                        Check(input.Block[0x37] == 1 && !commands.Contains(0x1c) && pilot.Combat.Target == null,
                            "Focus requests ordinary reticle selection without forcing a combat target");
                        pilot.Combat.Target = enemy; pilot.ApplyLanceFocus(input, commands);
                        Check(commands.Contains(0x19) && commands.Contains(0x1c), "Focus uses normal select-all and attack-selected commands");
                        commands.Clear(); pilot.ApplyLanceFocus(input, commands);
                        Check(!commands.Contains(0x1c), "Focus sends once per actual selected target, preserving subsequent AI execution");
                    }
                    finally
                    {
                        pilot.LanceFocusTarget = false; pilot.Combat.Target = selectedTarget;
                        pilot.Sensors.Unit = focusUnit; pilot.Sensors.Clear = focusClear; pilot.Sensors.Attackable = focusAttack;
                        commands.Clear();
                    }

                    pilot.LanceEngageAtWill = true;
                    MechAutopilot.ApplyInput(input, commands);
                    Check(commands.Contains(0x19) && commands.Contains(0x1f),
                        "Tester requests select-all and free engagement through ordinary command input");
                    MechAutopilot.ApplyInput(input, commands);
                    Check(!commands.Contains(0x19) && !commands.Contains(0x1f),
                        "Free engagement toggle is sent once rather than toggled off every frame");
                    pilot.LanceEngageAtWill = false;
                    pilot.TravelBearing = pilot.Controller.HeadingDeg;
                    MechAutopilot.ApplyInput(input, commands);
                    Check(input.Float(8) > 0 && input.Block[0x25] == 0 && pilot.State == PilotState.Navigation,
                        "Explicit bearing travel requests ordinary throttle without discretionary fire");
                    Check(pilot.transform.position == travelPosition && director.MissionResult == Mw2MissionDirector.Result.Running,
                        "Travel input does not teleport the player or write a mission result");
                }
                finally { pilot.TravelBearing = float.NaN; travelPowerField.SetValue(pilot.Combat, travelPower); }
                pilot.StopPilot();
                Check(!MechAutopilot.HasControl && Mw2MechCombat.ChainFire == chain && pilot.Combat.SelectedWeapon == weapon,
                    "Disabling restores fire mode, selection and input ownership");
                input.SetFloat(8, 0.012f); commands.Add(0x64); MechAutopilot.ApplyInput(input, commands);
                Check(input.Float(8) == 0.012f && commands.Contains(0x64), "Human control resumes without reloading mission");
                Check(director.MissionResult == Mw2MissionDirector.Result.Running, "Checks do not force mission success");
                CheckPilotGroups(pilot, input);
                CheckConditionReevaluation();
                CheckStructuralCriticals();
                CheckPointCollisionMeshes();
                CheckNetworkRadarPolicy();
                CheckNetworkAreaDamage();
                CheckSystemsStatus();
                CheckRadioExit();
                CheckUnitSpheres();
                CheckDetectionAssignment(director);
                CheckPilotRulePhase();
                CheckPilotPopContinuation();
                CheckNativeLead(director);
                CheckNativeWeaponCycle();
                CheckNativeAiLock();
                CheckAiShotConvergence();
                CheckPlayerShotConvergence(director);
                CheckProjectileSegments();
                CheckPartSpheres();
                CheckPartTieOrder();
                CheckMixedTieOrder();
                CheckNativeBoxSegments();
                CheckPathWorldBoxes();
                CheckGroundCandidateCapacity();
                CheckPartRepresentations(director);
                CheckNetworkRegeneration(director);
                CheckNetworkEquipmentRegeneration(director);
                var jetControl = director.Player.GetComponent<Mw2MechController>();
                int originalJumpMp = jetControl.JumpMp, originalFuel = jetControl.JumpFuel; bool originalDisabled = jetControl.JumpDisabled;
                try
                {
                    jetControl.JumpMp = 4; jetControl.JumpDisabled = false; jetControl.JumpFuel = 0;
                    Check(jetControl.HudJetFuel == 0, "Empty jump fuel keeps the JET indicator present");
                    jetControl.JumpDisabled = true;
                    Check(jetControl.HudJetFuel < 0, "Destroyed jump system suppresses the JET label and bar");
                    jetControl.JumpDisabled = false; jetControl.JumpMp = 0;
                    Check(jetControl.HudJetFuel < 0, "A mech without jump jets suppresses the JET label and bar");
                }
                finally { jetControl.JumpMp = originalJumpMp; jetControl.JumpFuel = originalFuel; jetControl.JumpDisabled = originalDisabled; }
            }
            catch (Exception e) { _errors.Add(e.ToString()); }
            if (_errors.Count == 0)
            {
                var childCheck = CheckTitleChildWait();
                while (true)
                {
                    bool more;
                    try { more = childCheck.MoveNext(); }
                    catch (Exception e) { _errors.Add(e.ToString()); break; }
                    if (!more) break;
                    yield return childCheck.Current;
                }
            }
            if (_errors.Count == 0)
            {
                var careerCheck = CheckCareerRoundTrip();
                while (true)
                {
                    bool more;
                    try { more = careerCheck.MoveNext(); }
                    catch (Exception e) { _errors.Add(e.ToString()); break; }
                    if (!more) break;
                    yield return careerCheck.Current;
                }
            }
            Application.logMessageReceived -= Log;
            Directory.CreateDirectory(Plan.output);
            var report = new SelfTestReport { passed = _errors.Count == 0, checks = _checks.ToArray(), errors = _errors.ToArray() };
            File.WriteAllText(Path.Combine(Plan.output, "selftest.json"), ReportJson.Write(report));
            Debug.Log("MW2 AUTOPILOT SELFTEST: " + ReportJson.Write(report));
            Application.Quit(report.passed ? 0 : 2);
        }
        [Serializable] sealed class SelfTestReport { public bool passed; public string[] checks, errors; }

        // Title_Tick1007439b waits for a real child before posting0xc002.
        // Use a mission-check child rather than opening a network session in this fixture.
        IEnumerator CheckTitleChildWait()
        {
            var shell = FindAnyObjectByType<MW2.Shell.Mw2Shell>(FindObjectsInactive.Include);
            bool enabled = shell.enabled; shell.enabled = false;
            var savedHandler = shell.Handler;
            const System.Reflection.BindingFlags fields = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var queue = (Queue<(uint msg, uint wParam)>)typeof(MW2.Shell.Mw2Shell).GetField("_posted", fields).GetValue(shell);
            var savedPosts = queue.ToArray(); queue.Clear();
            string output = Path.GetFullPath(Path.Combine(Plan.output, "title-child"));
            Directory.CreateDirectory(output);
            System.Diagnostics.Process child = null;
            bool disposed = false;
            try
            {
                child = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName,
                    Arguments = "-batchmode -nographics --mw2-mission-checks --game-folder \"" + Plan.gameFolder +
                        "\" --output \"" + output + "\" -logFile \"" + Path.Combine(output, "Player.log") + "\"" +
                        (Environment.GetCommandLineArgs().Contains("--mw2-mute") ? " --mw2-mute" : ""),
                    UseShellExecute = false, CreateNoWindow = true
                });
                MW2.Shell.TitleScreen.WaitForMercNet(shell, child);
                shell.Handler(0xc004);
                Check(shell.Handler != null && queue.Count == 0,
                    "Title retains the parent handler and posts no quit while its real child runs");
                float deadline = Time.realtimeSinceStartup + 45;
                while (!child.HasExited)
                {
                    if (Time.realtimeSinceStartup > deadline) throw new Exception("Title child check exceeded45 seconds");
                    yield return null;
                }
                int exit = child.ExitCode;
                shell.Handler(0xc004); disposed = true;
                Check(exit == 0 && File.Exists(Path.Combine(output, "mission-behavior.json")),
                    "Title lifecycle child actually executes its mission checks and exits normally");
                Check(shell.Handler == null && queue.Count == 1 && queue.Peek() == (0xc002u, 0xc02bu),
                    "Title posts exactly the original quit message after child termination and releases its handler");
            }
            finally
            {
                if (child != null && !disposed) { if (!child.HasExited) child.Kill(); child.Dispose(); }
                queue.Clear(); foreach (var entry in savedPosts) queue.Enqueue(entry);
                shell.Handler = savedHandler; shell.enabled = enabled;
            }
        }

        void CheckPilotGroups(MechAutopilot pilot, InputSystem input)
        {
            var self = pilot.Combat;
            var groups = self.Weapons.Select(w => w.Group).ToArray();
            var states = self.Weapons.Select(w => w.State).ToArray();
            float heat = self.Heat; bool chain = Mw2MechCombat.ChainFire;
            bool old = pilot.GroupFire;
            var originalDef = self.Weapons[0].Def; int originalAmmo = self.Weapons[0].Ammo;
            try
            {
                for (int i = 0; i < self.Weapons.Count; i++) { self.Weapons[i].Group = 1; self.Weapons[i].State = 1; }
                self.Heat = 0;
                Check(AutopilotCombat.GroupReady(self, 0, 100), "Grouped pilot accepts a cool ready short-range salvo");
                self.Weapons[1].State = 2;
                Check(!AutopilotCombat.GroupReady(self, 0, 100), "Grouped pilot waits for a reloading companion weapon");
                self.Weapons[1].State = 1; self.Heat = 64;
                Check(!AutopilotCombat.GroupReady(self, 0, 100), "Grouped pilot budgets heat of all working members");
                self.Heat = 0;
                Check(!AutopilotCombat.GroupReady(self, 0, 5000), "Grouped pilot rejects a salvo outside its members' range");
                var sensor = new AutopilotSensors { Unit = self, Clear = true, Attackable = true, Distance = 100 };
                var gunnery = new AutopilotCombat(); var controls = new PilotControls();
                self.Weapons[0].Def = new WeaponDef { Name = "cAC20", Type = originalDef.Type, Damage = 1000,
                    Heat = 0, Burst = 1, RecycleTicks = 1, RangeMeters = 500 };
                self.Weapons[0].Ammo = -1;
                gunnery.Aim(self, null, sensor, PilotTactic.Balanced, ref controls);
                Check(controls.weapon == 0, "Default tester weapon scoring retains a ready powerful weapon");
                gunnery.AvoidAutocannons = true;
                gunnery.Aim(self, null, sensor, PilotTactic.Balanced, ref controls);
                Check(controls.weapon > 0, "Explicit no-autocannon tester selects an allowed installed weapon");
                gunnery.Aim(self, null, sensor, PilotTactic.Balanced, ref controls, true);
                Check(controls.weapon == -1, "No-autocannon tester rejects a mixed synchronous group containing an AC");
                self.Weapons[0].Group = 2;
                gunnery.Aim(self, null, sensor, PilotTactic.Balanced, ref controls, true);
                Check(controls.weapon > 0, "No-autocannon tester permits a separate safe synchronous group");
                self.Weapons[0].Def = originalDef; self.Weapons[0].Ammo = originalAmmo; self.Weapons[0].Group = 1;
                pilot.GroupFire = true; pilot.Begin(); Mw2MechCombat.ChainFire = true;
                MechAutopilot.ApplyInput(input, new List<int>());
                Check(input.Block[0x2c] == 1, "Grouped pilot requests the original group-fire toggle");
                Mw2MechCombat.ChainFire = false; MechAutopilot.ApplyInput(input, new List<int>());
                Check(input.Block[0x2c] == 0, "Grouped pilot retains an already active group-fire mode");
                input.Block[0x37] = input.Block[0x3b] = 1;
                pilot.StopPilot();
                Check(input.Block[0x37] == 0 && input.Block[0x3b] == 0,
                    "Stopping the pilot clears its pending reticle-pick and inspect requests");
            }
            finally
            {
                if (pilot.Active) pilot.StopPilot();
                pilot.GroupFire = old; Mw2MechCombat.ChainFire = chain; self.Heat = heat;
                self.Weapons[0].Def = originalDef; self.Weapons[0].Ammo = originalAmmo;
                for (int i = 0; i < groups.Length; i++) { self.Weapons[i].Group = groups[i]; self.Weapons[i].State = states[i]; }
            }
        }

        IEnumerator CaptureSystemsStatus()
        {
            Directory.CreateDirectory(Plan.output);
            var cockpit = Mw2Cockpit.Player;
            RequireCapture(cockpit != null, "Capture has no player cockpit");
            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            const System.Reflection.BindingFlags statics = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;
            var commands = (List<int>)typeof(Mw2Input).GetField("_commands", statics).GetValue(null);
            void Toggle()
            {
                typeof(Mw2Input).GetField("_frame", statics).SetValue(null, Time.frameCount);
                commands.Clear(); commands.Add(0x4b);
                typeof(Mw2Cockpit).GetMethod("UpdateMainMenu", flags).Invoke(cockpit, null);
                commands.Clear();
            }
            // Give the real palette/camera their normal startup frames before the synthetic screenshot scene is frozen.
            yield return new WaitForSecondsRealtime(3);
            Toggle();
            yield return null; yield return new WaitForEndOfFrame();
            ScreenCapture.CaptureScreenshot(Path.Combine(Plan.output, "systems-status.png"));
            yield return new WaitForEndOfFrame();
            cockpit.SystemOptionSet(0x2a, 0);
            yield return new WaitForEndOfFrame();
            ScreenCapture.CaptureScreenshot(Path.Combine(Plan.output, "systems-status-hud-off.png"));
            yield return new WaitForEndOfFrame();
            cockpit.SystemOptionSet(0x2a, 1); cockpit.SystemOptionSet(0xc0, 1);
            yield return new WaitForEndOfFrame();
            ScreenCapture.CaptureScreenshot(Path.Combine(Plan.output, "systems-status-enhanced.png"));
            yield return new WaitForEndOfFrame();
            Toggle(); cockpit.SystemOptionSet(0xc0, 0);
        }
        static void RequireCapture(bool condition, string message) { if (!condition) throw new Exception(message); }

        void CheckSystemsStatus()
        {
            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            const System.Reflection.BindingFlags statics = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;
            var obj = new GameObject("Original window 5 fixture"); var cockpit = obj.AddComponent<Mw2Cockpit>();
            var type = typeof(Mw2Cockpit); var update = type.GetMethod("UpdateMainMenu", flags);
            var commands = (List<int>)typeof(Mw2Input).GetField("_commands", statics).GetValue(null);
            var keys = (List<int>)typeof(Mw2Input).GetField("_menuKeys", statics).GetValue(null);
            var oldCommands = commands.ToArray(); var oldKeys = keys.ToArray();
            bool oldEject = Mw2MechCombat.AutomaticEjection, oldEnhanced = Mw2ImageEnhancement.Enabled;
            bool oldWindow = Mw2Input.WindowOpen, oldArrows = Mw2Input.WindowTakesArrows, oldMode = Mw2Input.MenuMode;
            bool oldMain = Mw2Pause.MainMenuOpen;
            void Frame(int command = 0, int key = 0)
            {
                typeof(Mw2Input).GetField("_frame", statics).SetValue(null, Time.frameCount);
                commands.Clear(); keys.Clear(); if (command != 0) commands.Add(command); if (key != 0) keys.Add(key);
                update.Invoke(cockpit, null);
            }
            try
            {
                Mw2Pause.MainMenuOpen = false; Mw2Input.MenuMode = false;
                Frame(0x4b); Frame();
                Check(cockpit.SystemsStatusOpen && Mw2Input.WindowOpen && !Mw2Pause.MainMenuOpen && !Mw2Input.MenuMode &&
                    !Mw2Input.WindowTakesArrows, "USER_MENU opens original window5 without arrow capture or pause mode");
                var stack = (System.Collections.IList)type.GetField("_mainStack", flags).GetValue(cockpit);
                var page = stack[0]; var pageType = page.GetType();
                var items = (System.Collections.IList)pageType.GetField("Items").GetValue(page);
                var labels = items.Cast<object>().Select(i => (string)i.GetType().GetField("Text").GetValue(i)).ToArray();
                Check((string)pageType.GetField("Title").GetValue(page) == "SYSTEMS STATUS" && labels.SequenceEqual(new[] {
                    "Light Amplification", "Image Emhancement", "HUD", "Auto Eject", "(Esc to exit)" }),
                    "SYSTEMS STATUS uses all original labels including Image Emhancement typo");
                cockpit.SystemOptionSet(0x2a, 0); Frame(key: 0x33);
                Check(cockpit.HudState == 1 && cockpit.SystemsStatusOpen, "Window5 digit3 immediately toggles HUD back on while HUD was off");
                Frame(key: 0xc8);
                Check(cockpit.HudState == 1, "Window5 flags2 rejects arrow input");
                Frame(key: 0x34);
                Check(Mw2MechCombat.AutomaticEjection != oldEject, "Window5 digit4 directly writes original Auto Eject option53");
                Frame(key: 0x32);
                Check(Mw2ImageEnhancement.Enabled != oldEnhanced, "Window5 digit2 activates image enhancement rendering state");
                Frame(key: 0x1b);
                Check(!cockpit.SystemsStatusOpen && !Mw2Input.WindowOpen && Mw2MechCombat.AutomaticEjection != oldEject,
                    "Esc closes SYSTEMS STATUS without rolling back immediate options");
                Frame(0x4b); Frame(); Frame(0x4b);
                Check(!cockpit.SystemsStatusOpen, "USER_MENU toggles original window5 closed");
                Check(Shader.Find("MW2/EnhancedBoundary") != null, "Enhanced polygon-boundary shader is retained in the Player");
                var mesh = new Mesh();
                try
                {
                    mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.right + Vector3.up, Vector3.up };
                    mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
                    mesh.SetUVs(4, new List<Vector2> { new Vector2(1, 256), new Vector2(1, 256), new Vector2(1, 256), new Vector2(1, 256) });
                    var geo = typeof(Mw2ImageEnhancement).GetMethod("Build", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
                        .Invoke(null, new object[] { mesh });
                    var edges = (System.Array)geo.GetType().GetField("edges").GetValue(geo);
                    Check(edges.Length == 4, "Enhanced polygon boundary excludes the quad triangulation diagonal");
                }
                finally { Destroy(mesh); }
                var source = new PolyObject { Name = "Enhanced source triangle", Flags = 0,
                    Vertices = new[] { Vector3.zero, Vector3.up, Vector3.right },
                    VertexA = new short[] { 0, 0, 16 }, VertexB = new short[] { 0, 16, 0 } };
                source.Polygons.Add((0x3000, new[] { 0, 1, 2 }));
                var sprite = PolyMeshBuilder.Build(source, new Color32[256], null,
                    key => new PolyMeshBuilder.TextureInfo(16, 16, 0), new List<int>());
                try
                {
                    var original = new List<Vector3>(); sprite.GetUVs(5, original);
                    Check(original.Count == 4 && original[0] == Vector3.zero && original[1] == Vector3.up &&
                        original[2] == Vector3.right && original[3] == Vector3.zero,
                        "Enhanced type3 dispatch retains its original POLY triangle instead of the expanded billboard");
                }
                finally { Destroy(sprite); }
                var nodeRenderer = obj.AddComponent<MeshRenderer>();
                var imageNode = obj.AddComponent<Mw2ImageNode>(); imageNode.Flags = 0x200; imageNode.Apply();
                var metadata = new MaterialPropertyBlock(); nodeRenderer.GetPropertyBlock(metadata);
                Check(metadata.GetFloat("_Mw2EnhancedFlags") == 0x200 && metadata.GetFloat("_Mw2EnhancedOverride") == 1,
                    "World OBJ model flags feed enhanced rendering without altering shared mesh metadata");
            }
            finally
            {
                Mw2MechCombat.AutomaticEjection = oldEject; Mw2ImageEnhancement.Enabled = oldEnhanced;
                Mw2Pause.MainMenuOpen = oldMain; Mw2Input.WindowOpen = oldWindow; Mw2Input.WindowTakesArrows = oldArrows;
                Mw2Input.MenuMode = oldMode; commands.Clear(); commands.AddRange(oldCommands); keys.Clear(); keys.AddRange(oldKeys);
                Destroy(obj);
            }
        }

        // Hud_UpdatePlayer 100176cb / Hud_CreateGauges 10016ed7 / Map_StepRange 10050be5.
        // Effects_Update1001280e reads DAT_100aea44+2 after NetRoom1001c490 changes it.
        void CheckNetworkAreaDamage()
        {
            var options = Mw2Options.GameOptions;
            var before = (byte[])options.Bytes.Clone();
            try
            {
                var room = new NetRoom { Options = 0x35 };
                room.ApplyTo(options);
                Check(!options.AreaDamageToUnits && !Mw2Effects.AreaDamageToUnits,
                    "Room area-damage OFF reaches the live effects gate after options were loaded");
                room.Options = 0x3d; room.ApplyTo(options);
                Check(options.AreaDamageToUnits && Mw2Effects.AreaDamageToUnits,
                    "Room area-damage ON restores the live effects gate in the same session");
                room.Options = 0x35; room.ApplyTo(options);
                Check(!Mw2Effects.AreaDamageToUnits,
                    "A second room cannot inherit the prior room's area-damage cache");
            }
            finally { Buffer.BlockCopy(before, 0, options.Bytes, 0, before.Length); }
        }

        void CheckNetworkRadarPolicy()
        {
            var obj = new GameObject("Network radar option fixture");
            var cockpit = obj.AddComponent<Mw2Cockpit>();
            var type = typeof(Mw2Cockpit);
            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var mode = type.GetMethod("UpdateRadarMode", flags);
            var step = type.GetMethod("StepRadarRange", flags);
            var request = type.GetField("_radarRequest", flags);
            var ranges = (int[])type.GetField("_radarRanges", flags).GetValue(cockpit);
            bool net = Mw2MechCombat.NetGame;
            byte option = Mw2Options.GameOptions.Bytes[9];
            try
            {
                foreach (var sample in new[] { (net: true, option: 0), (net: true, option: 1), (net: false, option: 0), (net: false, option: 1) })
                {
                    Mw2MechCombat.NetGame = sample.net;
                    Mw2Options.GameOptions.Bytes[9] = (byte)sample.option;
                    bool available = !sample.net || sample.option != 0;
                    cockpit.RadarMode = 0; request.SetValue(cockpit, 1);
                    mode.Invoke(cockpit, null);
                    Check(cockpit.RadarMode == (available ? 1 : 0), "Native radar mode gate net=" + sample.net + ", option=" + sample.option);
                    cockpit.RadarMode = 1; ranges[1] = 200000;
                    step.Invoke(cockpit, new object[] { 1 });
                    Check(cockpit.RadarRange == (sample.option != 0 ? 100000 : 200000),
                        "Native radar range-key gate net=" + sample.net + ", option=" + sample.option);
                    var availability = type.GetProperty("RadarAvailable");
                    Check(availability != null && (bool)availability.GetValue(cockpit) == available,
                        "HUD radar availability follows original net/option condition");
                }
                Mw2MechCombat.NetGame = true; Mw2Options.GameOptions.Bytes[9] = 0;
                type.GetMethod("DrawRadar", flags).Invoke(cockpit, new object[] { new Rect(0, 0, 100, 100) });
                Check(type.GetField("_lineMaterial", flags).GetValue(cockpit) == null,
                    "Disabled network radar submits no radar geometry/material");
                type.GetField("_mapView", flags).SetValue(cockpit, true); cockpit.RadarMode = 4;
                type.GetMethod("RenderMapScreen", flags).Invoke(cockpit, null);
                Check(type.GetField("_mapScreen", flags).GetValue(cockpit) == null,
                    "Disabled network radar creates no fullscreen map render target");
                type.GetMethod("DrawTargetWindow", flags, null, new[] { typeof(int), typeof(bool), typeof(bool) }, null)
                    .Invoke(cockpit, new object[] { 17, true, false });
                Check(true, "Disabled network radar skips the original target-view gauge before GUI drawing");
            }
            finally { Mw2MechCombat.NetGame = net; Mw2Options.GameOptions.Bytes[9] = option; Destroy(obj); }
        }

        // Test-pilot coverage, not native AI. Original TRA2 Fighter GT flags/AFFL are
        // read from PRJ; player selection reuses Target_CheckStructure 10067406.
        void CheckPilotHostileStructures(Mw2MissionDirector director)
        {
            var units = Mw2MechCombat.All.Where(u => u != null && !u.IsPlayer).Select(u => (u, u.UnitFlags)).ToArray();
            var structures = Mw2Destructible.All.Where(s => s != null).Select(s => (s, s.Flags)).ToArray();
            var obj = new GameObject("Original TRA2 parked-aircraft sensor fixture");
            var self = director.Player.GetComponent<Mw2MechCombat>();
            var previousUnit = self.Target; var previousStructure = self.TargetStructure; int previousNav = self.TargetNav;
            var fighter = obj.AddComponent<Mw2Destructible>();
            try
            {
                foreach (var entry in units) entry.u.UnitFlags |= 0x800;
                foreach (var entry in structures) entry.s.Flags |= 0x800;
                using var prj = new PrjArchive(Path.Combine(Plan.gameFolder, "MW2.PRJ"));
                var mission = Mission.Load(prj, "TRA2");
                var original = mission.Destructibles.First(s => s.Name == "Fighter");
                fighter.File = original.File; fighter.DisplayName = original.Name;
                fighter.Flags = original.Flags; fighter.Side = mission.SideOfAffiliation(original.Affiliation);
                fighter.ObjectiveMask = original.ObjectiveMask;
                // Place it above nearby scenery to isolate eligibility from scene occlusion.
                obj.transform.position = self.Origin + Vector3.up * 100;
                var objectives = new AutopilotObjectives();
                var sensors = new AutopilotSensors();
                Check(fighter.Side == 1 && fighter.Flags == 0x400 && self.PilotCanSelect(fighter) &&
                    !objectives.IsDestroyTarget(director, fighter.File, fighter.ObjectiveMask),
                    "Archived TRA2 Fighter is an enemy selectable GT without a visible direct destroy binding");
                sensors.Scan(self, director, objectives, 1);
                Check(sensors.Structure == fighter && !sensors.Attackable && sensors.InspectTarget,
                    "Test pilot identifies selectable enemy GT before discretionary destruction");
                fighter.IdentifiedTeams |= 1 << self.Team;
                sensors.Scan(self, director, objectives, 1.5f);
                Check(sensors.Structure == fighter && sensors.Attackable && !sensors.InspectTarget,
                    "Test pilot attacks identified enemy GT without reading hidden aircraft objectives");
                sensors.Scan(self, director, objectives, 1.6f, false);
                Check(!sensors.HasTarget,
                    "CombatFirst omits discretionary GT without reading health or hidden mission binding");
                sensors.Scan(self, director, objectives, 1.7f);
                Check(sensors.Structure == fighter && sensors.Attackable,
                    "Default structure policy still selects TRA2 fighter after an opt-in scan");
                fighter.Flags |= 0x800; sensors.Scan(self, director, objectives, 2);
                Check(sensors.Structure != fighter, "Test pilot cannot target hidden hostile GT");
                fighter.Flags = 0; sensors.Scan(self, director, objectives, 3);
                Check(sensors.Structure != fighter, "Unbound hostile GT must pass the normal player target filter");
                fighter.Flags = original.Flags; fighter.Side = 0; sensors.Scan(self, director, objectives, 4);
                Check(sensors.Structure != fighter, "Friendly selectable GT is not an unbound attack target");
                fighter.Side = 2; sensors.Scan(self, director, objectives, 5);
                Check(sensors.Structure != fighter, "Neutral selectable GT is not an unbound attack target");
                fighter.Side = 1; fighter.Destroyed = true; sensors.Scan(self, director, objectives, 6);
                Check(sensors.Structure != fighter, "Destroyed hostile GT is excluded from test-pilot targets");
                fighter.Destroyed = false;
                obj.transform.position = self.Origin + Vector3.up * 2000;
                sensors.Scan(self, director, objectives, 7);
                Check(sensors.Structure != fighter, "Out-of-range hostile GT remains unavailable to the test pilot");
                obj.transform.position = self.Origin + Vector3.up * 100;
                var neighbourObject = new GameObject("Unidentified hostile neighbour");
                neighbourObject.transform.SetParent(obj.transform, false);
                neighbourObject.transform.position = self.Origin + Vector3.up * 120;
                var neighbour = neighbourObject.AddComponent<Mw2Destructible>();
                neighbour.File = original.File + "-neighbour"; neighbour.DisplayName = original.Name;
                neighbour.Flags = original.Flags; neighbour.Side = 1; neighbour.ObjectiveMask = original.ObjectiveMask;
                sensors.Scan(self, director, objectives, 8);
                Check(sensors.Structure == neighbour && sensors.InspectTarget && !sensors.Attackable,
                    "Survey an unknown hostile GT before discretionary fire on its identified neighbour");
                neighbour.Flags |= 0x800;
                sensors.PreferredTargetName = fighter.DisplayName.ToUpperInvariant();
                sensors.Scan(self, director, objectives, 8.1f, false);
                Check(sensors.Structure == fighter && sensors.Attackable,
                    "Explicit caption preference permits an eligible hostile GT in Guard");
                fighter.Flags |= 0x800;
                sensors.Scan(self, director, objectives, 8.2f, false);
                Check(!sensors.HasTarget, "Caption preference cannot reveal a hidden target");
                fighter.Flags &= ~0x800;
                sensors.PreferredTargetName = null;
                var fixture = obj.AddComponent<Mw2MissionDirector>(); fixture.enabled = false; fixture.Player = self.transform;
                fixture.RegisterLeader(0, self.transform);
                var bonus = new MissionOrder { Mask = MissionOrder.Destroy, Target = fighter.File,
                    Vital = true, Mandatory = false, AllConditions = true, TimeLimit = -1,
                    ConditionTypes = "", ConditionA = Array.Empty<int>(), ConditionB = Array.Empty<int>() };
                var team = new TeamOrders { Team = 0 };
                team.Orders.Add(bonus);
                team.Orders.Add(new MissionOrder { Mask = MissionOrder.Hold, Mandatory = true,
                    AllConditions = true, TimeLimit = -1, ConditionTypes = "",
                    ConditionA = Array.Empty<int>(), ConditionB = Array.Empty<int>() });
                fixture.Teams.Add(team);
                const System.Reflection.BindingFlags privateFields = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                typeof(Mw2MissionDirector).GetField("_units", privateFields).SetValue(fixture, Array.Empty<Mw2MechCombat>());
                typeof(Mw2MissionDirector).GetField("_structures", privateFields).SetValue(fixture, new[] { fighter });
                typeof(Mw2MissionDirector).GetMethod("Update", privateFields).Invoke(fixture, null);
                Check(objectives.IsDestroyTarget(fixture, fighter.File, fighter.ObjectiveMask) &&
                    !objectives.IsDestroyTarget(fixture, fighter.File, fighter.ObjectiveMask, true),
                    "Public optional destruction binding is distinct from a mandatory objective");
                sensors.Scan(self, fixture, objectives, 9, false);
                Check(!sensors.HasTarget, "CombatFirst skips active public bonus structures");
                bonus.Mandatory = true;
                sensors.Scan(self, fixture, objectives, 10, false);
                Check(sensors.Structure == fighter && sensors.Attackable,
                    "CombatFirst retains the same structure when its public objective is mandatory");
                var enemy = units.First(entry => entry.u.Side == 1).u;
                var enemyPosition = enemy.transform.position;
                int enemyFlags = enemy.UnitFlags;
                try
                {
                    // Separate sight lines: the mech must not physically occlude the GT.
                    obj.transform.position = self.Origin + Vector3.right * 100 + Vector3.up * 100;
                    enemy.UnitFlags = (enemy.UnitFlags & ~0x816) | 0x400;
                    enemy.transform.position += self.Origin + Vector3.up * 100 - enemy.ModelCentre;
                    typeof(Mw2MechCombat).GetField("_centreFrame", privateFields).SetValue(enemy, -1);
                    Physics.SyncTransforms();
                    sensors.Unit = null; sensors.Structure = null;
                    sensors.Scan(self, fixture, objectives, 11, false);
                    Check(sensors.Structure == fighter,
                        "Existing CombatFirst still ranks public mandatory GT above a non-objective unit");
                    sensors.Scan(self, fixture, objectives, 12, false, true);
                    Check(sensors.Unit == enemy && sensors.Attackable,
                        "UnitFirst chooses exposed selectable unit within own working weapon range over mandatory GT" +
                        $" (selected={sensors.TargetName}, eligible={self.PilotCanSelect(enemy)}, line={sensors.Line(self, enemy.ModelCentre, enemy)}, distance={Vector3.Distance(self.Origin, enemy.ModelCentre)})");
                    sensors.PreferMandatoryStructures = true;
                    sensors.Scan(self, fixture, objectives, 12.5f, false, true);
                    Check(sensors.Structure == fighter && sensors.Attackable,
                        "Explicit installation-first policy retains a reachable public mandatory GT over an exposed combat unit");
                    sensors.PreferMandatoryStructures = false;
                    sensors.AvoidUnitName = enemy.DisplayName.ToUpperInvariant();
                    sensors.Scan(self, fixture, objectives, 12.6f, false, true);
                    Check(sensors.Structure == fighter,
                        "Explicit tester name avoidance skips a selectable discretionary unit case-insensitively");
                    sensors.AvoidUnitName = null;
                    sensors.Scan(self, fixture, objectives, 12.7f, false, true);
                    Check(sensors.Unit == enemy,
                        "Disabling name avoidance restores the existing unit selection policy");
                    enemy.UnitFlags |= 0x800;
                    sensors.Scan(self, fixture, objectives, 13, false, true);
                    Check(sensors.Structure == fighter,
                        "UnitFirst cannot use hidden enemy to suppress a mandatory structure");
                    enemy.UnitFlags &= ~0x800;
                    enemy.transform.position += Vector3.up * 1000;
                    typeof(Mw2MechCombat).GetField("_centreFrame", privateFields).SetValue(enemy, -1);
                    Physics.SyncTransforms();
                    sensors.Scan(self, fixture, objectives, 14, false, true);
                    Check(sensors.Structure == fighter,
                        "UnitFirst does not abandon reachable mandatory GT for a distant unit");
                }
                finally
                {
                    enemy.transform.position = enemyPosition; enemy.UnitFlags = enemyFlags;
                    typeof(Mw2MechCombat).GetField("_centreFrame", privateFields).SetValue(enemy, -1);
                    Physics.SyncTransforms();
                }
                // Recon of a public area is not complete when only its first
                // member was identified. Reproduce the sticky Dropship in RAN8.
                bonus.Mask = 8;
                fighter.ObjectiveMask = neighbour.ObjectiveMask = 8;
                fighter.Side = neighbour.Side = 0;
                neighbour.File = fighter.File; neighbour.Flags &= ~0x800;
                fighter.IdentifiedTeams = 1 << self.Team; neighbour.IdentifiedTeams = 0;
                typeof(Mw2MissionDirector).GetField("_structures", privateFields).SetValue(fixture, new[] { fighter, neighbour });
                typeof(Mw2MissionDirector).GetMethod("Update", privateFields).Invoke(fixture, null);
                sensors.Structure = fighter; sensors.Unit = null;
                sensors.Scan(self, fixture, objectives, 15, false);
                Check(sensors.Structure == neighbour && sensors.InspectTarget && !sensors.Attackable,
                    "Public recon skips its already-identified sticky GT and selects the next member");
                neighbour.IdentifiedTeams = 1 << self.Team;
                sensors.Scan(self, fixture, objectives, 16, false);
                Check(!sensors.HasTarget, "Public recon has no remaining member once both GTs are identified");
                neighbour.IdentifiedTeams = 0; neighbour.Flags |= 0x800;
                sensors.Scan(self, fixture, objectives, 17, false);
                Check(!sensors.HasTarget, "Recon cannot reveal a hidden remaining group member");
                bonus.Mask = MissionOrder.Destroy; fighter.ObjectiveMask = (int)MissionOrder.Destroy;
                sensors.Scan(self, fixture, objectives, 18, false);
                Check(sensors.Structure == fighter && sensors.Attackable,
                    "Identification does not suppress a separate active public destruction task");
            }
            finally
            {
                foreach (var entry in units) entry.u.UnitFlags = entry.UnitFlags;
                foreach (var entry in structures) entry.s.Flags = entry.Flags;
                self.Target = previousUnit; self.TargetStructure = previousStructure; self.TargetNav = previousNav;
                foreach (var target in obj.GetComponentsInChildren<Mw2Destructible>(true)) target.Flags |= 0x800;
                obj.SetActive(false); Destroy(obj);
            }
        }

        void CheckHoldNav(MechAutopilot pilot, Mw2MissionDirector director)
        {
            var saved = pilot.transform.position; var requested = pilot.HoldNav;
            try
            {
                var controls = new PilotControls { throttle = 1f };
                pilot.HoldNav = null;
                Check(!pilot.ApplyHoldNav(ref controls) && controls.throttle == 1f,
                    "Public NAV hold is disabled by default and leaves ordinary driving untouched");
                var nav = director.PlayerNavs[0]; pilot.HoldNav = nav.File.ToUpperInvariant();
                pilot.transform.position = nav.Position;
                pilot.ApplyHoldNav(ref controls);
                Check(controls.throttle == 0 && pilot.transform.position == nav.Position &&
                    director.MissionResult == Mw2MissionDirector.Result.Running,
                    "Explicit public NAV hold brakes inside its zone without moving the player or completing the mission");
                pilot.transform.position = nav.Position + Vector3.right * (Mathf.Max(nav.Radius, 100f) + 200f);
                var outside = pilot.transform.position; controls = new PilotControls();
                pilot.ApplyHoldNav(ref controls);
                Check((Mathf.Abs(controls.turn) > 0 || controls.throttle > 0) && pilot.transform.position == outside,
                    "Returning to a public hold zone requests steering instead of teleportation");
                pilot.HoldNav = "__not_a_public_nav__"; controls.throttle = 0.7f;
                Check(!pilot.ApplyHoldNav(ref controls) && controls.throttle == 0.7f,
                    "Unavailable NAV cannot create a hold destination or expose script coordinates");
            }
            finally { pilot.transform.position = saved; pilot.HoldNav = requested; pilot.Navigation.Reset(); Physics.SyncTransforms(); }
        }

        void CheckManualStructures(Mw2MissionDirector director)
        {
            var self = director.Player.GetComponent<Mw2MechCombat>();
            var saved = self.transform.position;
            var previousUnit = self.Target; var previousStructure = self.TargetStructure; int previousNav = self.TargetNav;
            var target = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var cache = typeof(Mw2MechCombat).GetField("_centreFrame",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            try
            {
                self.transform.position = new Vector3(200000, 2000, 200000); cache.SetValue(self, -1);
                self.WeaponAimRay(self.GetComponent<Mw2TorsoTwist>(), out var eye, out var look);
                target.transform.position = eye + look * 100; target.transform.localScale = Vector3.one * 10;
                var gt = target.AddComponent<Mw2Destructible>();
                gt.DisplayName = "Manual fixture building"; gt.Side = 2; gt.Flags = 0;
                wall.SetActive(false); Physics.SyncTransforms();
                var sensors = new AutopilotSensors(); var objectives = new AutopilotObjectives();
                sensors.Scan(self, director, objectives, 1, false);
                Check(!sensors.HasTarget && !self.PilotCanSelect(gt), "Non-selectable neutral building stays unavailable without explicit free aim");
                sensors.ManualStructureNames = new[] { "FIXTURE BUILDING" };
                sensors.Scan(self, director, objectives, 2, false);
                Check(sensors.Structure == gt && sensors.Clear && sensors.Attackable && sensors.ManualVisual &&
                    !sensors.InspectTarget && self.TargetStructure == null,
                    "Explicit visible building free aim does not grant radar target or inspection");
                gt.Flags = 0x800; sensors.Scan(self, director, objectives, 3, false);
                Check(!sensors.HasTarget, "Manual building caption cannot reveal hidden geometry");
                gt.Flags = 0; wall.SetActive(true); wall.transform.position = eye + look * 50;
                wall.transform.localScale = Vector3.one * 30; Physics.SyncTransforms();
                sensors.Scan(self, director, objectives, 4, false);
                Check(!sensors.HasTarget, "Manual building free aim rejects a physically occluded target");
                wall.SetActive(false); target.transform.position = eye - look * 100; Physics.SyncTransforms();
                sensors.Scan(self, director, objectives, 5, false);
                Check(!sensors.HasTarget, "Manual building observation is restricted to the actual eye cone");
                target.transform.position = eye + look * 100; target.GetComponent<Renderer>().enabled = false;
                Physics.SyncTransforms(); sensors.Scan(self, director, objectives, 6, false);
                Check(!sensors.HasTarget, "Manual building observation requires enabled rendered geometry");
            }
            finally
            {
                target.SetActive(false); wall.SetActive(false); Destroy(target); Destroy(wall);
                self.transform.position = saved; cache.SetValue(self, -1); Physics.SyncTransforms();
                self.Target = previousUnit; self.TargetStructure = previousStructure; self.TargetNav = previousNav;
            }
        }

        void CheckMapRoute(Mw2MechController controller)
        {
            var saved = controller.transform.position;
            var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
            try
            {
                var start = new Vector3(200000, 0, 200000);
                controller.transform.position = start;
                wall.transform.position = start + Vector3.forward * 80 + Vector3.up * 20;
                wall.transform.localScale = new Vector3(120, 40, 10);
                Physics.SyncTransforms();
                var goal = start + Vector3.forward * 200;
                Check(!AutopilotNavigation.MapSegment(controller, start, goal),
                    "Map-route tester detects a wall across the direct public route");
                var route = new AutopilotNavigation();
                var waypoint = route.MapWaypoint(controller, goal, 1);
                Check(Mathf.Abs(waypoint.x - start.x) > 60 && AutopilotNavigation.MapSegment(controller, start, waypoint) &&
                    controller.transform.position == start,
                    "Map-route tester finds a clear lateral detour without moving the player directly");
                controller.transform.position = start + new Vector3(75, 0, 60);
                var corner = new AutopilotNavigation { UseMapRoute = true };
                var controls = new PilotControls();
                corner.Drive(controller, controller.GetComponent<Mw2MechCombat>(), goal, 60f, 2f, ref controls);
                Check(controls.throttle > 0,
                    "Map-route tester approaches a short intermediate corner inside the public NAV's wide arrival circle");
                controller.transform.position = start;
                wall.GetComponent<Collider>().enabled = false; Physics.SyncTransforms();
                Check(new AutopilotNavigation().MapWaypoint(controller, goal, 2) == goal,
                    "Map-route tester uses the direct destination when the physical wall is disabled");
                wall.transform.position = start + Vector3.forward * 100 + Vector3.up * ((controller.ProbeHeight + 0.25f) / 2f);
                wall.transform.localScale = new Vector3(120, controller.ProbeHeight + 0.25f, 10);
                wall.GetComponent<Collider>().enabled = true; Physics.SyncTransforms();
                Check(!AutopilotNavigation.MapSegment(controller, start, goal),
                    "Map-route clearance uses the actual movement probe height and detects a low blocking rail");
                var own = controller.GetComponent<Mw2MechCombat>();
                int type = own.GamepieceType;
                try
                {
                    own.GamepieceType = 2;
                    wall.transform.position = start + Vector3.forward * 100 + Vector3.up * 10;
                    wall.transform.localScale = new Vector3(120, 20, 40);
                    wall.AddComponent<Mw2Surface>().CollisionType = 0;
                    wall.GetComponent<Collider>().enabled = true; Physics.SyncTransforms();
                    Check(!AutopilotNavigation.MapSegment(controller, start, goal),
                        "Hovercraft map route rejects raised ground rather than planning a climb above its fixed travel plane");
                }
                finally { own.GamepieceType = type; }
            }
            finally { controller.transform.position = saved; wall.SetActive(false); Destroy(wall); Physics.SyncTransforms(); }
        }

        void CheckWaitingPublicNavSearch(Mw2MechCombat self)
        {
            var root = new GameObject("Waiting public NAV search fixture");
            var saved = self.transform.position;
            try
            {
                var fixture = root.AddComponent<Mw2MissionDirector>(); fixture.enabled = false;
                var alpha = saved + Vector3.right * 1000;
                var beta = saved + Vector3.right * 2000;
                fixture.Navs.Add(new Mw2MissionDirector.NavInfo { File = "alpha", Name = "Alpha", Type = 1, Team = 0, Position = alpha, Radius = 100 });
                fixture.Navs.Add(new Mw2MissionDirector.NavInfo { File = "beta", Name = "Beta", Type = 1, Team = 0, Position = beta, Radius = 100 });
                fixture.Navs.Add(new Mw2MissionDirector.NavInfo { File = "hidden", Type = 1, Team = 0, Flags = 1, Position = saved + Vector3.right * 40000 });
                fixture.NavVisitedTeams = new[] { 1, 0, 0 };
                self.transform.position = beta;
                var explicitRoute = new AutopilotObjectives { RouteNavs = new[] { "hidden", "alpha", "beta" } };
                explicitRoute.Update(fixture, self, 0);
                Check(explicitRoute.Destination == alpha && explicitRoute.PreferDestination,
                    "Explicit public route skips hidden NAV and steers to its first public waypoint");
                self.transform.position = alpha;
                explicitRoute.Update(fixture, self, 0.1f);
                Check(explicitRoute.Destination == beta && explicitRoute.PreferDestination && !fixture.NavVisited(1),
                    "Reaching a route waypoint advances tester travel without writing native NAV completion");
                self.transform.position = beta;
                var search = new AutopilotObjectives(); search.Update(fixture, self, 1);
                Check(search.HasDestination && (search.Destination - (alpha + Vector3.forward * 220)).sqrMagnitude < 1 &&
                    !fixture.NavVisited(1) && !fixture.NavVisited(2),
                    "Arriving at a waiting public NAV resumes search near Alpha without completing native NAVs or revealing hidden ones");
                var first = search.Destination;
                self.transform.position = Vector3.MoveTowards(self.transform.position, first, 100f);
                search.Update(fixture, self, 30); search.Update(fixture, self, 47);
                Check(search.Destination == first,
                    "Public-route search retains a distant waypoint while the tester makes progress beyond its original deadline");
                search.Update(fixture, self, 100);
                Check((search.Destination - first).sqrMagnitude > 100,
                    "An unreachable public-route search point expires instead of holding the tester forever");
                self.transform.position = saved;
                var anchored = new AutopilotObjectives { SearchNav = "alpha" }; anchored.Update(fixture, self, 1);
                Check((anchored.Destination - (alpha + Vector3.forward * 220)).sqrMagnitude < 1,
                    "Explicit public Alpha search avoids leaving for a waiting Beta before finding the installation");
                var hidden = new AutopilotObjectives { SearchNav = "hidden" }; hidden.Update(fixture, self, 1);
                Check(hidden.Destination == beta && !fixture.NavVisited(2),
                    "A requested hidden search anchor never becomes selectable or overrides the public route");
            }
            finally { self.transform.position = saved; root.SetActive(false); Destroy(root); Physics.SyncTransforms(); }
        }

        void CheckExposedUnitAim(Mw2MissionDirector director, Mw2MechCombat self)
        {
            var root = new GameObject("Partly occluded selectable unit fixture");
            var saved = self.transform.position;
            var units = Mw2MechCombat.All.Where(u => u != null && u != self).Select(u => (u, u.UnitFlags)).ToArray();
            try
            {
                foreach (var entry in units) entry.u.UnitFlags |= 0x800;
                self.transform.position = new Vector3(200000, 10000, 200000);
                self.WeaponAimRay(self.GetComponent<Mw2TorsoTwist>(), out _, out var look);
                var unitObject = new GameObject("Exposed upper/lower parts"); unitObject.transform.SetParent(root.transform);
                unitObject.transform.position = self.Origin + look * 100;
                var target = unitObject.AddComponent<Mw2MechCombat>(); target.Side = 1; target.UnitFlags = 0x400;
                var map = unitObject.AddComponent<Mw2HitMap>();
                foreach (int side in new[] { 1, -1 })
                {
                    var part = GameObject.CreatePrimitive(PrimitiveType.Cube); part.transform.SetParent(unitObject.transform, false);
                    part.transform.localPosition = Vector3.up * (20 * side); part.transform.localScale = Vector3.one * 8;
                    map.Nodes.Add(part.transform); map.Sections.Add(side == 1 ? 0 : 2);
                }
                var wall = GameObject.CreatePrimitive(PrimitiveType.Cube); wall.transform.SetParent(root.transform);
                wall.transform.position = self.Origin + look * 50; wall.transform.localScale = Vector3.one * 10;
                Physics.SyncTransforms();
                var objectives = new AutopilotObjectives();
                var normal = new AutopilotSensors(); normal.Scan(self, director, objectives, 1, false);
                Check(self.PilotCanSelect(target) && normal.Unit == target && !normal.Clear,
                    "Selectable unit fixture reproduces a blocked aggregate-centre ray");
                var mobile = new AutopilotSensors { PreferFiringAngles = true }; mobile.Scan(self, director, objectives, 1, false);
                Check(mobile.Unit == target && mobile.Clear && !mobile.ManualVisual &&
                    (mobile.Point - target.ModelCentre).sqrMagnitude > 1,
                    "Mobile tester retains an eye-visible part aim point without changing radar eligibility");
                Check(AutopilotSensors.TryHeadAim(self, target, out var headPoint) && headPoint.y > target.ModelCentre.y,
                    "Explicit head-aim tester requires a first physical hit on the visible mapped head");
                Check(AutopilotSensors.TrySectionAim(self, target, 0, out var orderedHead) && orderedHead == headPoint,
                    "Explicit section aim uses the same visible first-hit geometry as head aiming");
                Check(!AutopilotSensors.TrySectionAim(self, target, 6, out _) && !AutopilotSensors.TrySectionAim(self, target, 8, out _),
                    "Section aim refuses an absent leg and invalid section instead of firing at the torso");
                mobile.AvoidGamepieces = new[] { target.GamepieceIndex }; mobile.Scan(self, director, objectives, 1.5f, false);
                Check(mobile.Unit != target, "Explicit observed target avoidance cannot select the spared unit");
                mobile.AvoidGamepieces = Array.Empty<int>();
                target.transform.GetChild(0).GetComponent<Renderer>().enabled = false;
                Check(!AutopilotSensors.TryHeadAim(self, target, out _),
                    "Head aim cannot substitute a visible torso when the head renderer is disabled");
                foreach (var renderer in target.GetComponentsInChildren<Renderer>()) renderer.enabled = false;
                mobile.Scan(self, director, objectives, 2, false);
                Check(!mobile.Clear, "Disabled rendered parts cannot supply the mobile eye-visible aim point");
            }
            finally
            {
                foreach (var entry in units) entry.u.UnitFlags = entry.UnitFlags;
                self.transform.position = saved; root.SetActive(false); Destroy(root); Physics.SyncTransforms();
            }
        }

        void CheckStructureEyeVisibility(Mw2MechCombat self)
        {
            var root = new GameObject("Weapon-eye GT visibility fixture");
            var saved = self.transform.position;
            var cockpit = self.GetComponent<Mw2Cockpit>(); var savedEye = cockpit.Eye;
            try
            {
                self.transform.position = new Vector3(200000, 10000, 200000);
                var eye = new GameObject("Raised weapon eye"); eye.transform.SetParent(root.transform);
                eye.transform.position = self.Origin + Vector3.up * 10; cockpit.Eye = eye.transform;
                var cube = GameObject.CreatePrimitive(PrimitiveType.Cube); cube.transform.SetParent(root.transform);
                cube.transform.position = self.Origin + Vector3.forward * 100; cube.transform.localScale = Vector3.one * 6;
                var target = cube.AddComponent<Mw2Destructible>(); target.File = "eye-fixture"; target.ObjectiveMask = 2;
                var wall = GameObject.CreatePrimitive(PrimitiveType.Cube); wall.transform.SetParent(root.transform);
                wall.transform.position = self.Origin + Vector3.forward * 50; wall.transform.localScale = new Vector3(20, 4, 6);
                Physics.SyncTransforms();
                Check(!AutopilotSensors.TryStructureAim(self, target, out _) &&
                    AutopilotSensors.TryStructureAim(self, target, out _, true),
                    "Guard sees a real GT surface from the weapon eye when GP Origin is blocked by a low wall");
                var fixture = root.AddComponent<Mw2MissionDirector>(); fixture.enabled = false;
                var team = new TeamOrders { Team = 0 };
                team.Orders.Add(new MissionOrder { Vital = true, Mandatory = true, Mask = 2, Target = target.File,
                    AllConditions = true, TimeLimit = -1, ConditionTypes = "", ConditionA = Array.Empty<int>(), ConditionB = Array.Empty<int>() });
                fixture.Teams.Add(team);
                const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                typeof(Mw2MissionDirector).GetField("_units", flags).SetValue(fixture, Array.Empty<Mw2MechCombat>());
                typeof(Mw2MissionDirector).GetField("_structures", flags).SetValue(fixture, new[] { target });
                typeof(Mw2MissionDirector).GetMethod("Update", flags).Invoke(fixture, null);
                var sensor = new AutopilotSensors { PrioritizeDefense = true };
                sensor.Scan(self, fixture, new AutopilotObjectives(), 1, false);
                Check(sensor.Structure == target && sensor.Clear && sensor.Attackable && !self.PilotCanSelect(target),
                    "Eye-visible public mandatory GT remains a manual target without acquiring radar eligibility");
                target.Flags |= 0x800;
                Check(!AutopilotSensors.TryStructureAim(self, target, out _, true),
                    "Weapon-eye surface aiming still rejects hidden GTs");
            }
            finally { cockpit.Eye = savedEye; self.transform.position = saved; root.SetActive(false); Destroy(root); Physics.SyncTransforms(); }
        }

        void CheckHollowStructureAim(Mw2MechCombat self)
        {
            var root = new GameObject("Hollow GT aim fixture");
            var saved = self.transform.position;
            try
            {
                self.transform.position = new Vector3(200000, 10000, 200000);
                root.transform.position = self.Origin + Vector3.forward * 100;
                var target = root.AddComponent<Mw2Destructible>(); target.Flags = 0x400;
                foreach (var p in new[] { Vector3.right, -Vector3.right, Vector3.up, -Vector3.up })
                {
                    var bar = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    bar.transform.SetParent(root.transform, false); bar.transform.localPosition = p;
                    bar.transform.localScale = Mathf.Abs(p.x) > 0 ? new Vector3(0.2f, 2.2f, 0.2f) : new Vector3(2.2f, 0.2f, 0.2f);
                }
                Physics.SyncTransforms();
                var center = Mw2MechCombat.StructureCentre(target); var ray = center - self.Origin;
                Check(!self.FirstHit(self.Origin, ray.normalized, ray.magnitude + 0.5f, out _),
                    "Hollow GT reproduces an unobstructed centre ray that hits no structure");
                bool found = AutopilotSensors.TryStructureAim(self, target, out var point); ray = point - self.Origin;
                Check(found && (point - center).sqrMagnitude > 0.01f &&
                    self.FirstHit(self.Origin, ray.normalized, ray.magnitude + 0.5f, out var hit) &&
                    hit.collider.GetComponentInParent<Mw2Destructible>() == target,
                    "Test pilot chooses a visible solid GT surface instead of the empty centre");
                target.Flags |= 0x800;
                Check(!AutopilotSensors.TryStructureAim(self, target, out _), "Surface aiming cannot expose a hidden GT");
            }
            finally { root.SetActive(false); Destroy(root); self.transform.position = saved; Physics.SyncTransforms(); }
        }

        // FUN_1005f481 0x1005f481 -> FUN_1005f52b 0x1005f52b: a GT node under a destroyed GT node is destroyed with it.
        // Shape of SIG1 sig1are1: s-1apgls GT nodes under Control Tower node 10, replacements at the area root.
        void CheckChildGtCascade(Mw2MechCombat self)
        {
            var root = new GameObject("Child GT cascade fixture");
            try
            {
                root.transform.position = new Vector3(-200000, 10000, -200000);
                GameObject Node(string name, Transform parent)
                {
                    var go = GameObject.CreatePrimitive(PrimitiveType.Cube); go.name = name;
                    go.transform.SetParent(parent, false); go.transform.localPosition = Vector3.right * 3; return go;
                }
                var tower = Node("s-1aptwr", root.transform).AddComponent<Mw2Destructible>();
                tower.File = "cascade"; tower.ObjectiveMask = 2; tower.Strength = 10;
                var glass = Node("s-1apgls", tower.transform).AddComponent<Mw2Destructible>();
                glass.File = "cascade"; glass.ObjectiveMask = 2; glass.Strength = 25;
                var wreck = Node("sa1apgls", root.transform); wreck.GetComponent<Renderer>().enabled = false;
                glass.Replacement = wreck.transform; glass.Debris = new[] { wreck.GetComponent<Renderer>() };
                var grand = Node("grandchild", glass.transform).AddComponent<Mw2Destructible>();
                var sibling = Node("sibling", root.transform).AddComponent<Mw2Destructible>();
                var stats = Mw2MissionDirector.Stats; int kills = stats[Formats.CombatCareer.NeutralBldgKills];
                tower.ApplyDamage(5, self);
                Check(!tower.Destroyed && !glass.Destroyed, "Damage below GT strength destroys neither the parent nor its child GT");
                tower.ApplyDamage(5, self);
                Check(tower.Destroyed && glass.Destroyed && grand.Destroyed && !sibling.Destroyed &&
                    wreck.GetComponent<Renderer>().enabled && !glass.GetComponent<Collider>().enabled,
                    "Destroying a parent GT destroys GT nodes under it and shows their replacements, not sibling GTs");
                Check(stats[Formats.CombatCareer.NeutralBldgKills] == kills + 1,
                    "Only the directly destroyed GT counts a building kill (FUN_10012e9d); the cascade adds none");
                // Building_Damage 0x10013010: _ftol of the damage, and a GT with strength 0 is never damaged.
                var bunker = Node("bunker", root.transform).AddComponent<Mw2Destructible>(); bunker.Strength = 50;
                for (int i = 0; i < 100; i++) bunker.ApplyDamage(0.9f, self);
                bunker.ApplyDamage(2.7f, self);
                Check(!bunker.Destroyed && bunker.Strength == 48,
                    "Building damage truncates each call to whole points: fractional area damage leaves a GT intact");
                var shell = Node("zero-strength", root.transform).AddComponent<Mw2Destructible>(); shell.Strength = 0;
                shell.ApplyDamage(500, self);
                Check(!shell.Destroyed, "A GT with strength 0 takes no damage and is never destroyed");
            }
            finally { root.SetActive(false); Destroy(root); }
        }

        // ShellMain 100428d3 / launch 10061aca / Save_Read 1006ddf6 / Units_WriteMeks 100598b1.
        // Synthetic aborts exercise the real shell handoff twice in one Player; they are not a playthrough.
        IEnumerator CheckCareerRoundTrip()
        {
            var shell = FindAnyObjectByType<MW2.Shell.Mw2Shell>(FindObjectsInactive.Include);
            Check(shell != null, "Career integration uses the bootstrapped shell component");
            shell.enabled = false;
            shell.GameFolder = Plan.gameFolder;
            using var prj = new PrjArchive(Path.Combine(Plan.gameFolder, "MW2.PRJ"));
            var tables = MW2.Shell.ShellTables.FromStore(Plan.gameFolder);
            shell.Prj = prj;
            shell.Data = new MW2.Shell.ShellData(prj, tables, Plan.gameFolder);
            shell.Career = MW2.Shell.Career.New(1, shell.Data);
            shell.Career.Register("ROUNDTRIP", "CAREER CHECK");
            Check(shell.Career.AdvanceAtMissionCommand(shell.Data, 0xc033) &&
                shell.Career.Mission.Equals("tra1scn1", StringComparison.OrdinalIgnoreCase),
                "Registered CAMP career advances to the original TRA1 contract");
            int funds = shell.Career.Funds;
            int pilotId = shell.Career.UnitPilots[0].Id;
            int mechId = shell.Career.UnitMechs[0].MechId;
            byte[] returnedMek = null;
            var launchMethod = typeof(MW2.Shell.Mw2Shell).GetMethod("LaunchMission",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            try
            {
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    UnityEngine.Random.InitState(Plan.seed + attempt);
                    var roster = shell.Career.BuildLaunchRoster(shell.Data);
                    Check(roster.Count == 1 && roster[0].MekData != null,
                        "Career launch " + attempt + " carries the assigned player's serialized MEK");
                    if (returnedMek != null)
                    {
                        var expected = MekLoadout.Decode(returnedMek);
                        var next = MekLoadout.Decode(roster[0].MekData);
                        Check(next.FrontArmor.SequenceEqual(expected.FrontArmor) && next.Internal.SequenceEqual(expected.Internal),
                            "Second launch preserves returned damage across career serialization");
                    }
                    // Skip only the cosmetic two-second launch-screen wait; execute the original coroutine body.
                    var launch = (IEnumerator)launchMethod.Invoke(shell, new object[] { roster, roster[0], 0xc012u });
                    Check(launch.MoveNext() && !launch.MoveNext(), "Real shell launch coroutine completes " + attempt);
                    yield return null; yield return null;
                    var scene = FindAnyObjectByType<Mw2MissionDirector>();
                    Check(scene != null && scene.MissionId == "TRA1" && scene.Player != null &&
                        scene.MissionResult == Mw2MissionDirector.Result.Running,
                        "Real career launch builds TRA1 and its player " + attempt);
                    var player = scene.Player.GetComponent<Mw2MechCombat>();
                    var loadout = MekLoadout.Decode(roster[0].MekData);
                    Check(player.CareerUnit && player.MekName.Equals(roster[0].MekName, StringComparison.OrdinalIgnoreCase) &&
                        player.Front.SequenceEqual(loadout.FrontArmor.Select(a => a * player.ArmorFactor)),
                        "Career player uses the roster MEK and source armour multiplier " + attempt);
                    player.Front[3] = Math.Max(0, player.Front[3] - 4 * player.ArmorFactor);
                    returnedMek = player.WriteMek();
                    var expectedReport = scene.BuildReport();
                    var expectedStats = (byte[])Mw2MissionDirector.Stats.Data.Clone();
                    Check(returnedMek != null && Mw2Cockpit.MissionLeft != null,
                        "Career unit writes damage and installs the real return callback " + attempt);
                    Mw2Cockpit.MissionLeft(false);
                    shell.enabled = false; // Keep posted debriefing pending; no synthetic outcome/payment is applied.
                    Check(shell.Career.Msg23C7 == 0xc00a && !shell.Career.UnitMechs[0].Custom &&
                        shell.MissionReportFile.SequenceEqual(expectedReport) && shell.CombatCareerFile.SequenceEqual(expectedStats),
                        "Real return callback posts debriefing with the runtime report and statistics " + attempt);
                    var merged = MekLoadout.Decode(shell.Career.UnitMechs[0].ToMek());
                    Check(merged.FrontArmor.SequenceEqual(MekLoadout.Decode(returnedMek).FrontArmor),
                        "Real return callback merges the written player damage " + attempt);
                    shell.Career.LoadMissionReport(new MW2.Shell.ShellMissionReport(shell.MissionReportFile));
                    shell.Career.LoadCombatCareer(shell.CombatCareerFile);
                    byte[] saved = shell.Career.Save();
                    shell.Career = MW2.Shell.Career.Load(saved);
                    Check(shell.Career.Save().SequenceEqual(saved) && shell.Career.Funds == funds &&
                        shell.Career.UnitPilots[0].Id == pilotId && shell.Career.UnitMechs[0].MechId == mechId &&
                        shell.Career.UnitPilots[0].MechId == mechId,
                        "Returned career round-trips without changing funds or assignments " + attempt);
                    yield return null; yield return null;
                    Check(FindAnyObjectByType<Mw2MissionDirector>() == null && Mw2MechCombat.All.Count == 0,
                        "Returning removes all prior mission units before another career launch " + attempt);
                }
                CheckCareerDebrief(shell);
                var scripted = CheckScriptedCareerPlayer(shell);
                while (scripted.MoveNext()) yield return scripted.Current;
            }
            finally { shell.enabled = false; shell.Prj = null; Mission.LooseBwd = null; }
        }

        // World_LoadChunks10073313/10073491 uses the authored human GPS, not an unrelated shell MEK.
        IEnumerator CheckScriptedCareerPlayer(MW2.Shell.Mw2Shell shell)
        {
            shell.Career.Mission = "sig2scn1"; // Synthetic launch fixture only; no campaign outcome/payment.
            var roster = shell.Career.BuildLaunchRoster(shell.Data);
            var before = MekLoadout.Decode(roster[0].MekData);
            int funds = shell.Career.Funds;
            var launchMethod = typeof(MW2.Shell.Mw2Shell).GetMethod("LaunchMission",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var launch = (IEnumerator)launchMethod.Invoke(shell, new object[] { roster, roster[0], 0xc012u });
            Check(launch.MoveNext() && !launch.MoveNext(), "Scripted SIG2 shell launch completes");
            yield return null; yield return null;
            var scene = FindAnyObjectByType<Mw2MissionDirector>();
            var player = scene.Player.GetComponent<Mw2MechCombat>();
            Check(scene.PlayerModel.Equals("pegasus", StringComparison.OrdinalIgnoreCase) &&
                scene.PlayerMek.Equals("peg00std", StringComparison.OrdinalIgnoreCase) && !player.CareerUnit,
                "SIG2 uses its authored Pegasus/PEG00STD rather than the assigned career mech");
            Check(player.SourceFile.Equals("sig2star", StringComparison.OrdinalIgnoreCase) && player.WriteMek() == null,
                "Scripted player retains its GPS objective binding and writes no unrelated career MEK");
            player.Front[3] = Math.Max(0, player.Front[3] - 1); // Controlled fixture damage, never selected career play.
            Mw2Cockpit.MissionLeft(false); shell.enabled = false;
            var after = MekLoadout.Decode(shell.Career.UnitMechs.First(m =>
                m.MekName.Equals(roster[0].MekName, StringComparison.OrdinalIgnoreCase)).ToMek());
            Check(after.FrontArmor.SequenceEqual(before.FrontArmor) && after.Internal.SequenceEqual(before.Internal) &&
                after.WeaponIds.SequenceEqual(before.WeaponIds) && shell.Career.Funds == funds,
                "Returning the scripted vehicle preserves the owned career mech and funds");
            yield return null; yield return null;
            Check(FindAnyObjectByType<Mw2MissionDirector>() == null && Mw2MechCombat.All.Count == 0,
                "Scripted player return removes its units through the normal shell callback");
        }

        // MW2SHELL.DLL 1001ede1 / 100206bb / 1001d1d3 / 1006ddf6.
        // Real screen transitions with synthetic outcomes, independently of any combat victory.
        void CheckCareerDebrief(MW2.Shell.Mw2Shell shell)
        {
            // Price the actual returned armour loss (the second edit can clamp at zero).
            var repair = new MW2.Shell.MechRepair(shell.Career, shell.Data.Tables);
            repair.Open();
            int repairFunds = shell.Career.Funds;
            var beforeRepair = MekLoadout.Decode(repair.Mech.ToMek());
            var pristine = MekLoadout.Decode(repair.Pristine.ToMek());
            int armorLost = pristine.FrontArmor.Sum() + pristine.RearArmor.Sum() -
                beforeRepair.FrontArmor.Sum() - beforeRepair.RearArmor.Sum();
            Check(armorLost > 0 && repair.RepairAll(true) == armorLost * 625,
                "Returned runtime damage has the source repair price of 625 per armour point");
            repair.RepairAll(false);
            Check(repair.Pending() == armorLost * 625 && shell.Career.Funds == repairFunds && repair.Answer(0) &&
                shell.Career.Funds == repairFunds - armorLost * 625,
                "Repairing returned runtime damage charges only on acceptance");
            var repaired = MekLoadout.Decode(shell.Career.BuildLaunchRoster(shell.Data)[0].MekData);
            Check(repaired.FrontArmor.SequenceEqual(MekLoadout.Decode(repair.Pristine.ToMek()).FrontArmor),
                "The next career roster contains repaired armour");
            shell.Db = new MW2.Shell.ShellDatabase(Path.Combine(Plan.gameFolder, "DATABASE.MW2"));
            shell.Surface = new MW2.Shell.ShellSurface();
            shell.Anims = new MW2.Shell.ShellAnimList(shell.Surface, Path.Combine(Plan.gameFolder, "SMK"), shell.gameObject);
            shell.Font7 = new MW2.Shell.ShellFont(shell.Db.Raw(7));
            shell.Font11 = new MW2.Shell.ShellFont(shell.Db.Raw(11));
            shell.Menu = MW2.Shell.ShellMenu.From(shell.Data.Tables.Defaults.Menu);
            foreach (int outcome in new[] { 2, 3, 4 })
            {
                shell.Career = MW2.Shell.Career.New(1, shell.Data);
                shell.Career.Register("DEBRIEF", "CAREER CHECK");
                Check(shell.Career.LoadStep("BET1CAM1", shell.Data), "Debrief fixture loads original BET1 campaign step " + outcome);
                var career = shell.Career;
                string currentMission = career.Mission;
                var mission = Mission.Load(shell.Prj, "BET1");
                var objectives = mission.Teams.Find(t => t.Team == 0).Orders.Where(o => o.Vital)
                    .Select(o => new MissionObjectiveResult { Visible = true, Complete = outcome == 2,
                        Mandatory = o.Mandatory, Group = o.Kind, Text = o.Text, Started = 0, Finished = 100 }).ToArray();
                shell.MissionReportFile = MissionReportFile.Write(0, 100, outcome, objectives);
                var stats = new CombatCareer(); stats.Set16(CombatCareer.StarEnemyMechKills, 3);
                shell.CombatCareerFile = stats.Data;
                shell.SalvageFile = null;
                int funds = career.Funds, expenses = career.UnitExpenses(false);
                int income = (outcome == 2 ? career.SuccessPay : career.FailPay) + 3 * career.BountyPay;
                if (outcome == 2)
                    foreach (var objective in objectives)
                        income += objective.Group == 1 ? career.TargetPay : objective.Group == 2 ? career.BonusPay :
                            objective.Group == 4 ? career.BountyPay : 0;
                string nextStep = outcome == 2 ? career.SuccessStep(0) : career.FailStep(0);
                string nextMission = nextStep.Equals("DEATH", StringComparison.OrdinalIgnoreCase) ? career.Mission :
                    Campaign.Decode(nextStep, shell.Data.ReadBwd(nextStep)).Mission;
                MW2.Shell.DebriefScreen.Open(shell);
                Check(career.Funds == funds && career.Outcome239C == (outcome == 2) && shell.Handler != null,
                    "Real debrief opens without paying before Continue " + outcome);
                shell.Handler(0xc02e); // Real save-screen exit path: payment must remain pending.
                Check(career.Byte23B7 && career.Funds == funds && career.Mission == currentMission,
                    "Leaving debrief for Save/Load preserves the pending mission and funds " + outcome);
                byte[] saved = career.Save();
                shell.Career = MW2.Shell.SaveLoadScreen.Read(shell, saved);
                Check(shell.Career.Save().SequenceEqual(saved) && shell.MissionReportFile.SequenceEqual(
                    saved.Skip(0x243b).Take(MissionReportFile.Size)),
                    "Real SaveLoadScreen restores pending debrief and mission report " + outcome);
                MW2.Shell.DebriefScreen.Open(shell);
                Check(shell.Career.Funds == funds, "Reopening saved debrief does not pay twice " + outcome);
                shell.Handler(0xc012); // Actual screen Leave -> Advance, with no fixture call to Advance.
                Check(shell.Career.Funds == funds + income - expenses &&
                    shell.Career.Mission.Equals(nextMission, StringComparison.OrdinalIgnoreCase),
                    "Continue pays source compensation/objectives/bounty once and follows the CPGN link " + outcome);
            }
            CheckCareerRoute(shell);
            CheckRandomCareerReload(shell);
            CheckLut4ObjectiveCapacity(shell);
            shell.Anims.Clear(); shell.Handler = null; shell.Surface = null;
        }

        // Objective_AddTarget1004045b retains at most40 registrations. Actual LUT4
        // contains46 protected GTs; simulate losses on either side of the boundary.
        void CheckLut4ObjectiveCapacity(MW2.Shell.Mw2Shell shell)
        {
            const System.Reflection.BindingFlags fields = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var previousUnits = new HashSet<Mw2MechCombat>(Mw2MechCombat.All);
            GameObject scene = null, fixtureRoot = null;
            try
            {
                scene = Mw2MissionBuilder.Build(shell.Prj, Plan.gameFolder, "LUT4", "COMMANDO", 1, "CMD00STD");
                var actual = scene.GetComponent<Mw2MissionDirector>(); actual.enabled = false;
                typeof(Mw2MissionDirector).GetMethod("Start", fields).Invoke(actual, null);
                var source = actual.Teams.Find(t => t.Team == 0).Orders[5];
                var targets = scene.GetComponentsInChildren<Mw2Destructible>(true)
                    .Where(s => s.File.Equals(source.Target, StringComparison.OrdinalIgnoreCase) && (s.ObjectiveMask & source.Mask) != 0)
                    .OrderBy(s => s.Index).ToArray();
                Check(source.Mask == MissionOrder.Protect && source.Mandatory && targets.Length == 46,
                    "Actual LUT4 world builds46 GTs for the mandatory protection record5");
                Check(actual.OrderTargets(0, 5).Select(t => t.Structure).SequenceEqual(targets.Take(40)),
                    "LUT4 protection retains the first40 original GT indices after actual scene construction");
                fixtureRoot = new GameObject("LUT4 controlled protection boundary");
                var director = fixtureRoot.AddComponent<Mw2MissionDirector>(); director.enabled = false;
                director.Player = fixtureRoot.transform; director.RegisterLeader(0, fixtureRoot.transform);
                var order = JsonUtility.FromJson<MissionOrder>(JsonUtility.ToJson(source));
                order.AllConditions = true; order.ConditionTypes = "";
                order.ConditionA = order.ConditionB = Array.Empty<int>(); order.TimeLimit = -1;
                var team = new TeamOrders { Team = 0 }; team.Orders.Add(order); director.Teams.Add(team);
                typeof(Mw2MissionDirector).GetField("_units", fields).SetValue(director, Array.Empty<Mw2MechCombat>());
                // Unity's discovery order is unspecified. Reverse it to prove the source indices select the prefix.
                typeof(Mw2MissionDirector).GetField("_structures", fields).SetValue(director, targets.Reverse().ToArray());
                targets[40].Destroyed = true;
                typeof(Mw2MissionDirector).GetMethod("Update", fields).Invoke(director, null);
                Check(director.StateOf(0, 0) == Mw2MissionDirector.State.Active && director.MissionResult == Mw2MissionDirector.Result.Running,
                    "LUT4 loss of registration41 does not fail the original capped protection");
                targets[39].Destroyed = true;
                typeof(Mw2MissionDirector).GetMethod("Update", fields).Invoke(director, null);
                Check(director.StateOf(0, 0) == Mw2MissionDirector.State.Failed && director.MissionResult == Mw2MissionDirector.Result.Failure,
                    "LUT4 loss of registration40 fails the mandatory protection as in the original");
            }
            finally
            {
                if (fixtureRoot != null) DestroyImmediate(fixtureRoot);
                if (scene != null) DestroyImmediate(scene);
                foreach (var unit in Mw2MechCombat.All.ToArray())
                    if (unit != null && !previousUnits.Contains(unit)) DestroyImmediate(unit.gameObject);
            }
        }

        // Save_Read1006ddf6 regenerates RMG files; the port must also restore the
        // persistent randwld1 alias created by shell1006ad2f before initial launch.
        void CheckRandomCareerReload(MW2.Shell.Mw2Shell shell)
        {
            var previousData = shell.Data; var previousCareer = shell.Career;
            try
            {
                var data = new MW2.Shell.ShellData(shell.Prj, previousData.Tables, Plan.gameFolder);
                var career = MW2.Shell.Career.New(1, data);
                career.Register("RMG RELOAD", "SYNTHETIC CHECK");
                var offers = MW2.Shell.ContractOffers.Read(career, data, 777);
                int index = offers.FindIndex(o => o.Step.StartsWith("RAN", StringComparison.OrdinalIgnoreCase));
                career.Finished239D = true; career.Outcome239C = true;
                Check(index >= 0 && career.AcceptContract(index, offers) && career.AdvanceAtMissionCommand(data, 0xc021),
                    "Original CAMP offer accepts and generates a random contract for reload regression");
                var world = data.Files["randwld1.bwd"];
                byte[] saved = career.Save();
                for (int pass = 0; pass < 2; pass++)
                {
                    shell.Data = new MW2.Shell.ShellData(shell.Prj, previousData.Tables, Plan.gameFolder);
                    shell.Career = MW2.Shell.SaveLoadScreen.Read(shell, saved);
                    Check(shell.Career.Save().SequenceEqual(saved), "Random contract save is unchanged on fresh reload " + pass);
                    Check(shell.Data.Files.TryGetValue("randwld1.bwd", out var restored) && restored.SequenceEqual(world),
                        "Fresh random career reload restores the exact launch-world alias " + pass);
                    Mission.LooseBwd = shell.Data.ReadLoose;
                    var mission = Mission.Load(shell.Prj, MissionCatalog.Prefix(shell.Career.Mission));
                    Check(mission.Missing.Count == 0 && mission.Areas.Count > 0 && mission.NavPoints.Count > 0,
                        "Reloaded random scene resolves its world and NAV inputs " + pass);
                }
            }
            finally { Mission.LooseBwd = null; shell.Data = previousData; shell.Career = previousCareer; }
        }

        // ShellMain 100428d3, briefing 100229fa, debrief 1001ede1 / 1001d1d3,
        // command advance 100229fa and save reader 1006ddf6. Synthetic successful
        // reports test one continuous career's screens/state, never combat playthrough.
        void CheckCareerRoute(MW2.Shell.Mw2Shell shell)
        {
            shell.Career = MW2.Shell.Career.New(1, shell.Data);
            shell.Career.Register("ROUTE", "CONTINUOUS CHECK");
            var career = shell.Career;
            int playerId = career.UnitPilots[0].Id, mechId = career.UnitMechs[0].MechId;
            string step = career.SuccessStep(0);
            Check(career.AdvanceAtMissionCommand(shell.Data, 0xc033), "Continuous career enters its first original contract");
            var rows = new List<string> { "step\tmission\tseed\tmode\tmodel\tmek\tfunds_before\tincome\texpenses\tfunds_after" };
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (career.StepType != 4 && visited.Add(step) && rows.Count < 100)
            {
                string missionName = career.Mission;
                var source = Campaign.Decode(step, shell.Data.ReadBwd(step));
                Check(source != null && source.Mission.Equals(missionName, StringComparison.OrdinalIgnoreCase),
                    "Continuous career follows original CPGN " + step);
                MW2.Shell.BriefingScreen.Open(shell);
                Check(!career.Byte23B7 && career.PlayerHasMech(), "Next real briefing resets debrief visit and retains assigned mech " + step);
                shell.Handler(0xc011); // Screen exit only; no synthetic combat result is presented as a flight.
                var roster = career.BuildLaunchRoster(shell.Data);
                Check(roster.Count == 1 && roster[0].MekData != null && career.UnitPilots[0].Id == playerId &&
                    career.UnitPilots[0].MechId == mechId, "Continuous career preserves its original roster " + step);
                var mission = Mission.Load(shell.Prj, missionName.Substring(0, 4));
                var objectives = mission.Teams.Find(t => t.Team == 0).Orders.Where(o => o.Vital)
                    .Select(o => new MissionObjectiveResult { Visible = true, Complete = true, Mandatory = o.Mandatory,
                        Group = o.Kind, Text = o.Text, Started = 0, Finished = 100 }).ToArray();
                shell.MissionReportFile = MissionReportFile.Write(0, 100, 2, objectives);
                shell.CombatCareerFile = new CombatCareer().Data;
                shell.SalvageFile = null;
                career.ReturnFromMission(_ => null, shell.Data.Tables);
                int funds = career.Funds, expenses = career.UnitExpenses(false);
                int income = career.SuccessPay;
                foreach (var o in objectives) income += o.Group == 1 ? career.TargetPay : o.Group == 2 ? career.BonusPay : o.Group == 4 ? career.BountyPay : 0;
                int type = career.StepType;
                string next = career.SuccessStep(0);
                MW2.Shell.DebriefScreen.Open(shell);
                shell.Handler(0xc02e);
                byte[] checkpoint = career.Save();
                shell.Career = MW2.Shell.SaveLoadScreen.Read(shell, checkpoint);
                career = shell.Career;
                Check(career.Save().SequenceEqual(checkpoint) && career.Funds == funds,
                    "Continuous pending debrief survives SaveLoadScreen byte for byte " + step);
                MW2.Shell.DebriefScreen.Open(shell);
                shell.Handler(0xc012);
                Check(career.Funds == funds + income - expenses,
                    "Continuous real debrief pays once and preserves economy " + step);
                rows.Add($"{step}\t{missionName}\t{Plan.seed}\tSyntheticSuccess\t{roster[0].Model}\t{roster[0].MekName}\t{funds}\t{income}\t{expenses}\t{career.Funds}");
                if (type == 2)
                {
                    var offers = MW2.Shell.ContractOffers.Read(career, shell.Data, Plan.seed);
                    Check(career.Finished239D && offers.Count > 0 && career.AcceptContract(0, offers) &&
                        career.AdvanceAtMissionCommand(shell.Data, 0xc021), "Continuous career accepts next listed contract " + step);
                }
                step = next;
            }
            Check(career.StepType == 4 && career.Mission.Equals("lut4scn1", StringComparison.OrdinalIgnoreCase),
                "Continuous saved career reaches the original LUT4 final mission without replacing career state");
            MW2.Shell.BriefingScreen.Open(shell);
            shell.Handler(0xc011);
            int finalFunds = career.Funds, finalPay = career.SuccessPay, finalExpenses = career.UnitExpenses(false);
            shell.MissionReportFile = MissionReportFile.Write(0, 100, 2, Array.Empty<MissionObjectiveResult>());
            shell.CombatCareerFile = new CombatCareer().Data;
            career.ReturnFromMission(_ => null, shell.Data.Tables);
            MW2.Shell.DebriefScreen.Open(shell);
            Check(career.Endless && career.Funds == finalFunds + finalPay - finalExpenses,
                "Original LUT4 final debrief pays on Open and marks the story complete");
            shell.Handler(0xc02e);
            byte[] finalSave = career.Save();
            shell.Career = MW2.Shell.SaveLoadScreen.Read(shell, finalSave);
            career = shell.Career;
            Check(career.Save().SequenceEqual(finalSave) && career.Endless,
                "Final story-complete state survives the actual saved-debrief reader");
            // Native 1001f61d..1001f773 has no +23b7 guard on the special final payment.
            // Preserve this original repeat-on-open quirk instead of treating it as a port bug.
            MW2.Shell.DebriefScreen.Open(shell);
            Check(career.Funds == finalFunds + 2 * (finalPay - finalExpenses),
                "Reopening the final report preserves the original unguarded LUT payment quirk");
            Check(career.EndingFlic(shell.Data.Tables) == "mrich",
                "The continuous synthetic career selects the rich ending from its accumulated assets");
            shell.Handler(0xc02e); // Avoid recording a fixture score or writing a real user high-score file.
            rows.Add($"{step}\t{career.Mission}\t{Plan.seed}\tSyntheticFinalOpenTwice\tcommando\tply00MER.MEK\t{finalFunds}\t{2 * finalPay}\t{2 * finalExpenses}\t{career.Funds}");
            Directory.CreateDirectory(Plan.output);
            File.WriteAllLines(Path.Combine(Plan.output, "career-route.tsv"), rows);
        }

        // Lod_SetForGamepiece 1001e9df / Lod_SwapModel 1001eb15: current-model collision,
        // using real Atlas representations on the same source skeleton.
        void CheckPartRepresentations(Mw2MissionDirector scene)
        {
            var root = new GameObject("Part representation fixture");
            try
            {
                using var prj = new PrjArchive(Path.Combine(Plan.gameFolder, "MW2.PRJ"));
                var material = scene.Player.GetComponentInChildren<Renderer>().sharedMaterial;
                var atlas = MechAssembler.Build(prj, "ATLAS", new Color32[256], material, 1, allLods: true);
                atlas.transform.SetParent(root.transform); atlas.transform.position = new Vector3(200000, 1000, 200000);
                var group = atlas.GetComponent<Mw2LodGroup>();
                var parts = atlas.GetComponentsInChildren<Mw2HitPart>();
                Check(group.Levels.All(level => level.Renderers.Where(r => r != null && r.TryGetComponent<MeshFilter>(out var mf) &&
                    PolyMeshBuilder.HasCollisionTriangles(mf.sharedMesh)).All(r => r.GetComponent<Mw2HitPart>() != null)),
                    "All real Atlas representations carry source-sphere hit metadata");
                var first = parts.First(p => p.Representation == 1 && p.name.Contains("RARM"));
                var fourth = parts.First(p => p.Representation == 4 && p.name.Contains("RARM"));
                foreach (var p in parts) p.HitCollider.enabled = p == first || p == fourth;
                var shooter = new GameObject("LOD shooter"); shooter.transform.SetParent(root.transform);
                var combat = shooter.AddComponent<Mw2MechCombat>();
                bool Hits(Mw2HitPart part)
                {
                    var center = part.transform.TransformPoint(part.LocalCentre);
                    return combat.FirstHit(center - Vector3.forward * part.ModelRadius * 5, Vector3.forward,
                        part.ModelRadius * 10, out var hit) && hit.collider == part.HitCollider;
                }
                group.Show(1);
                Check(first.CurrentModel && !fourth.CurrentModel && Hits(first), "Attached part collision selects actual LOD1 model");
                group.Show(4);
                Check(!first.CurrentModel && fourth.CurrentModel && Hits(fourth), "Attached part collision follows the switch to actual LOD4 model");
                fourth.transform.SetParent(root.transform, true); first.HitCollider.enabled = false;
                group.Show(1);
                Check(fourth.CurrentModel && Hits(fourth), "Detached part retains its selected collision model when its former unit changes LOD");
            }
            finally { DestroyImmediate(root); }
        }

        // FUN_10051da0 -> FUN_10052148 restores saved model nodes and their hierarchy/pose.
        void CheckNetworkRegeneration(Mw2MissionDirector scene)
        {
            var source = Mw2MechCombat.All.First(u => u != null && !u.IsPlayer && u.GetComponent<Mw2HitMap>() != null);
            var root = Instantiate(source.gameObject);
            Transform[] nodes = null;
            var random = UnityEngine.Random.state;
            try
            {
                root.name = "Real model network regeneration fixture";
                root.transform.position = new Vector3(250000, 1000, 250000);
                foreach (var behaviour in root.GetComponents<Behaviour>()) behaviour.enabled = false;
                var combat = root.GetComponent<Mw2MechCombat>();
                var map = root.GetComponent<Mw2HitMap>();
                var lods = root.GetComponent<Mw2LodGroup>();
                nodes = root.GetComponentsInChildren<Transform>(true).Where(t => t != root.transform).ToArray();
                var parents = nodes.Select(t => t.parent).ToArray();
                var positions = nodes.Select(t => t.localPosition).ToArray();
                var rotations = nodes.Select(t => t.localRotation).ToArray();
                combat.NetRemember();
                var detach = typeof(Mw2MechCombat).GetMethod("BlowOffSection", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                map.RaiseDamage(12, 4);
                detach.Invoke(combat, new object[] { 5 });
                Check(nodes.Any(t => !t.IsChildOf(root.transform)), "Real arm detaches through existing section-loss effects path");
                combat.NetRegenerate();
                Check(nodes.Select((t, i) => t.parent == parents[i]).All(x => x), "Network regeneration restores every original node parent including detached descendants");
                Check(nodes.Select((t, i) => (t.localPosition - positions[i]).sqrMagnitude < 0.000001f && Quaternion.Angle(t.localRotation, rotations[i]) < 0.001f).All(x => x),
                    "Network regeneration restores original node local positions and rotations");
                Check(map.Nodes.All(t => map.LevelOf(t) == 0), "Regeneration resets inherited node damage levels");
                var visible = lods.Levels.First(l => l.Lod == lods.Current).Renderers.First(r => r != null && !lods.Shell.Contains(r));
                lods.Hide(visible);
                combat.NetRegenerate();
                Check(visible.enabled, "Regeneration re-shows a hidden part without requiring an LOD change");
                if (Mw2Effects.Instance != null)
                {
                    typeof(Mw2Effects).GetMethod("UpdateParts", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                        .Invoke(Mw2Effects.Instance, new object[] { 181 });
                    Check(nodes.Select((t, i) => t.parent == parents[i] && (t.localPosition - positions[i]).sqrMagnitude < 0.000001f).All(x => x),
                        "Old debris records cannot move restored parts after regeneration");
                }
                detach.Invoke(combat, new object[] { 5 }); combat.NetRegenerate();
                Check(nodes.All(t => t.IsChildOf(root.transform)), "Repeated detach/regenerate restores the same node tree");
            }
            finally
            {
                UnityEngine.Random.state = random;
                if (nodes != null) foreach (var node in nodes) if (node != null && !node.IsChildOf(root.transform)) DestroyImmediate(node.gameObject);
                DestroyImmediate(root);
            }
        }

        // FUN_10051da0 reloads the original MEK via1002f9e0, not only its armour.
        void CheckNetworkEquipmentRegeneration(Mw2MissionDirector scene)
        {
            var source = Mw2MechCombat.All.First(u => u != null && u != scene.Player.GetComponent<Mw2MechCombat>() && u.SectionItems != null);
            var obj = new GameObject("Network equipment regeneration fixture");
            var combat = obj.AddComponent<Mw2MechCombat>();
            var movement = obj.AddComponent<Mw2MechController>();
            try
            {
                combat.SectionItems = source.SectionItems.Select(items => new List<int>(items)).ToArray();
                combat.HeatSinks = source.HeatSinks;
                movement.JumpMp = 4;
                var expected = combat.SectionItems.Select(items => items.ToArray()).ToArray();
                combat.NetRemember();
                // Controlled post-critical state; never a fair battle or synthesized mission result.
                combat.SectionItems[4].Clear();
                combat.HeatSinks = 0; combat.SpeedFactor = 0.2f; combat.LegLost = true;
                combat.GaugeDamage[0] = 1;
                movement.JumpMp = 0; movement.JumpDisabled = true; movement.StartFall(1024);
                combat.NetRegenerate();
                Check(combat.SectionItems.Select((items, i) => items.SequenceEqual(expected[i])).All(x => x),
                    "Network regeneration restores original critical-item inventories after section loss");
                Check(combat.HeatSinks == source.HeatSinks && combat.SpeedFactor == 1f && !combat.LegLost,
                    "Network regeneration restores cooling and leg/actuator mobility");
                Check(combat.GaugeDamage.All(value => value == 0), "Network regeneration restores undamaged cockpit gauges");
                Check(combat.PoweringUp && combat.PowerState == 0 && !combat.ShutDown && !combat.PoweredDown,
                    "Network regeneration restarts normal state0 rather than bypassing power-up");
                Check(movement.JumpMp == 4 && !movement.JumpDisabled && movement.JumpFuel == 0x712 && !movement.FallenOver,
                    "Network regeneration restores jump MP/fuel and clears the old fallen physics state");
            }
            finally { DestroyImmediate(obj); }
        }

        // 100186b1 scans every model; a Unity capacity limit must not hide the only walkable one.
        void CheckGroundCandidateCapacity()
        {
            var root = new GameObject("Native forty-surface ground fixture");
            try
            {
                root.transform.position = new Vector3(100000, 0, 100000);
                var boxes = new BoxCollider[40];
                for (int i = 0; i < boxes.Length; i++)
                {
                    var go = new GameObject("Ground candidate " + i); go.transform.SetParent(root.transform, false);
                    go.transform.localPosition = new Vector3(0, (100 + i * 10) * PolyModel.UnitsToMeters - 0.025f, 0);
                    boxes[i] = go.AddComponent<BoxCollider>(); boxes[i].size = new Vector3(1, 0.05f, 1);
                    go.AddComponent<Mw2Surface>().CollisionType = 1;
                }
                Physics.SyncTransforms();
                var legacy = new RaycastHit[32];
                int count = Physics.RaycastNonAlloc(root.transform.position + Vector3.up * 1100, Vector3.down,
                    legacy, 21100, Mw2HitPart.MovementMask, QueryTriggerInteraction.Ignore);
                var excluded = boxes.First(b => !legacy.Take(count).Any(h => h.collider == b));
                // Changing only classification keeps the PhysX traversal fixed. The old buffer sees no walkable hit.
                excluded.GetComponent<Mw2Surface>().CollisionType = 0;
                float expected = excluded.bounds.max.y;
                Check(count == 32 && Mathf.Abs(Mw2Ground.Height(root.transform.position) - expected) < 0.0001f,
                    "Ground query finds the only walkable surface even when forty hits put it outside the original 32-hit buffer");
                Check(Mathf.Abs(Mw2Ground.Height(root.transform.position + Vector3.up * 20) - expected) < 0.0001f,
                    "Expanded ground query preserves native highest-below fallback outside the near-height window");
            }
            finally { DestroyImmediate(root); }
        }

        // 10004934 -> 10019e70 -> 10017f60: real native transforms and world-axis boxes.
        void CheckPathWorldBoxes()
        {
            var root = new GameObject("Native path world-box fixture"); Mesh mesh = null;
            try
            {
                mesh = new Mesh(); var vertices = new Vector3[8];
                for (int i = 0; i < 8; i++) vertices[i] = new Vector3((i & 1) == 0 ? -2 : 2, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -4 : 4);
                mesh.vertices = vertices; mesh.triangles = new[] { 0, 1, 2, 2, 1, 3, 4, 6, 5, 5, 6, 7, 0, 4, 1, 1, 4, 5, 2, 3, 6, 6, 3, 7, 0, 2, 4, 4, 2, 6, 1, 5, 3, 3, 5, 7 }; mesh.RecalculateBounds();
                root.AddComponent<MeshFilter>().sharedMesh = mesh;
                var renderer = root.AddComponent<MeshRenderer>();
                var boxGo = new GameObject("World-axis collision box"); boxGo.transform.SetParent(root.transform);
                var box = boxGo.AddComponent<BoxCollider>();
                var surface = boxGo.AddComponent<Mw2Surface>(); surface.CollisionType = 0;
                surface.AxisBox = box; surface.BoxSources = new Renderer[] { renderer };
                var path = root.AddComponent<Mw2PathTask>(); path.enabled = false;
                var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                string[] nativeRows = {
                    "0,-1000,2000,-4000,-1200,-800,1900,2100,-4400,-3600,0,2000",
                    "0,0,2000,-3000,-200,200,1900,2100,-3400,-2600,0,2000",
                    "0,1000,2000,-2000,800,1200,1900,2100,-2400,-1600,0,2000",
                    "30,-1000,2000,-4000,-1373.20508,-626.7949,1900,2100,-4446.41,-3553.58984,0,2000",
                    "30,0,2000,-3000,-373.205078,373.205078,1900,2100,-3446.41016,-2553.58984,0,2000",
                    "30,1000,2000,-2000,626.7949,1373.20508,1900,2100,-2446.41016,-1553.58984,0,2000",
                    "45,-1000,2000,-4000,-1424.264,-575.735962,1900,2100,-4424.264,-3575.73584,1,575.73584",
                    "45,0,2000,-3000,-424.264069,424.264069,1900,2100,-3424.26416,-2575.73584,1,575.73584",
                    "45,1000,2000,-2000,575.735962,1424.264,1900,2100,-2424.26416,-1575.736,1,575.73584",
                    "90,-1000,2000,-4000,-1400,-600,1900,2100,-4200,-3800,1,800",
                    "90,0,2000,-3000,-400,400,1900,2100,-3200,-2800,1,800",
                    "90,1000,2000,-2000,600,1400,1900,2100,-2200,-1800,1,800",
                    "137,-1000,2000,-4000,-1419.07007,-580.929932,1900,2100,-4428.941,-3571.05884,1,571.0591",
                    "137,0,2000,-3000,-419.0701,419.0701,1900,2100,-3428.94116,-2571.05884,1,571.058838",
                    "137,1000,2000,-2000,580.929932,1419.07007,1900,2100,-2428.94116,-1571.05884,1,571.058838",
                };
                int checkedRows = 0;
                foreach (string row in nativeRows)
                {
                    float[] v = row.Split(',').Select(s => float.Parse(s, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                    var position = new Vector3(v[1], v[2], v[3]);
                    var table = new PathTable { Points = new[] { position, position }, Angles = new[] { new Vector3(0, v[0], 0), new Vector3(0, v[0], 0) }, Ticks = new[] { 1000, 1000 } };
                    var motion = new PathMotion(new PathTask { Mode = 2, Rotate = false, Path = table }, 0); motion.Update(100);
                    typeof(Mw2PathTask).GetField("_motion", flags).SetValue(path, motion);
                    typeof(Mw2PathTask).GetMethod("Apply", flags).Invoke(path, null);
                    var min = new Vector3(v[4], v[6], v[8]) * PolyModel.UnitsToMeters;
                    var max = new Vector3(v[5], v[7], v[9]) * PolyModel.UnitsToMeters;
                    if (Vector3.Distance(box.bounds.min, min) > 0.001f || Vector3.Distance(box.bounds.max, max) > 0.001f || Quaternion.Angle(box.transform.rotation, Quaternion.identity) > 0.001f)
                        throw new Exception("Native transformed world box mismatch: " + row);
                    var from = new Vector3(v[1] + 400, v[2], v[3] - 1000) * PolyModel.UnitsToMeters;
                    bool hit = box.Raycast(new Ray(from, Vector3.forward), out var result, 20);
                    if (hit != (v[10] != 0) || hit && Mathf.Abs(result.distance - v[11] * PolyModel.UnitsToMeters) > 0.001f)
                        throw new Exception("Native world-axis box edge-ray mismatch: " + row);
                    checkedRows++;
                }
                Check(checkedRows == 15, "All 15 native path-rotated world boxes and edge rays match through actual PTBL Apply");
            }
            finally { DestroyImmediate(root); if (mesh != null) DestroyImmediate(mesh); }
        }

        // 0x10018ad5 / 10049159 / 100492b6: direct original mixed-list references.
        // The literal rows are captured from the original program; expected hits are never generated here.
        void CheckMixedTieOrder()
        {
            const string nativeRows = @"0,0,-20,0,7,1,1,7,380
0,0,-20,1,8,1,1,7,380
0,0,-20,2,8,1,1,7,380
0,0,-20,3,8,0,1,7,380
0,0,-20,4,8,1,1,7,380
0,0,-20,5,7,2,1,7,380
0,0,-20,6,7,0,1,8,400
0,0,-20,7,7,1,1,7,380
0,0,0,0,7,1,1,7,400
0,0,0,1,8,1,1,8,400
0,0,0,2,8,1,1,8,400
0,0,0,3,8,0,1,7,400
0,0,0,4,8,1,1,8,400
0,0,0,5,7,2,1,8,400
0,0,0,6,7,0,1,8,400
0,0,0,7,7,1,1,7,400
0,0,20,0,7,1,1,7,420
0,0,20,1,8,1,1,8,400
0,0,20,2,8,1,1,8,400
0,0,20,3,8,0,1,7,420
0,0,20,4,8,1,1,8,400
0,0,20,5,7,2,1,8,400
0,0,20,6,7,0,1,8,400
0,0,20,7,7,1,1,8,400
0,1,-20,0,7,1,1,7,380
0,1,-20,1,8,1,1,7,380
0,1,-20,2,8,1,1,7,380
0,1,-20,3,8,0,1,7,380
0,1,-20,4,8,1,1,7,380
0,1,-20,5,7,2,1,7,380
0,1,-20,6,7,0,1,8,400
0,1,-20,7,7,1,1,7,380
0,1,0,0,7,1,1,7,400
0,1,0,1,8,1,1,8,400
0,1,0,2,8,1,1,8,400
0,1,0,3,8,0,1,7,400
0,1,0,4,8,1,1,8,400
0,1,0,5,7,2,1,8,400
0,1,0,6,7,0,1,8,400
0,1,0,7,7,1,1,7,400
0,1,20,0,7,1,1,7,420
0,1,20,1,8,1,1,8,400
0,1,20,2,8,1,1,8,400
0,1,20,3,8,0,1,7,420
0,1,20,4,8,1,1,8,400
0,1,20,5,7,2,1,8,400
0,1,20,6,7,0,1,8,400
0,1,20,7,7,1,1,8,400
1,0,-20,0,8,1,1,8,400
1,0,-20,1,7,1,1,7,380
1,0,-20,2,7,1,1,7,380
1,0,-20,3,7,0,1,8,400
1,0,-20,4,7,1,1,7,380
1,0,-20,5,8,2,1,7,380
1,0,-20,6,8,0,1,7,380
1,0,-20,7,8,1,1,7,380
1,0,0,0,8,1,1,8,400
1,0,0,1,7,1,1,7,400
1,0,0,2,7,1,1,7,400
1,0,0,3,7,0,1,8,400
1,0,0,4,7,1,1,7,400
1,0,0,5,8,2,1,7,400
1,0,0,6,8,0,1,7,400
1,0,0,7,8,1,1,8,400
1,0,20,0,8,1,1,8,400
1,0,20,1,7,1,1,8,400
1,0,20,2,7,1,1,8,400
1,0,20,3,7,0,1,8,400
1,0,20,4,7,1,1,8,400
1,0,20,5,8,2,1,8,400
1,0,20,6,8,0,1,7,420
1,0,20,7,8,1,1,8,400
1,1,-20,0,8,1,1,8,400
1,1,-20,1,7,1,1,7,380
1,1,-20,2,7,1,1,7,380
1,1,-20,3,7,0,1,8,400
1,1,-20,4,7,1,1,7,380
1,1,-20,5,8,2,1,7,380
1,1,-20,6,8,0,1,7,380
1,1,-20,7,8,1,1,7,380
1,1,0,0,8,1,1,8,400
1,1,0,1,7,1,1,7,400
1,1,0,2,7,1,1,7,400
1,1,0,3,7,0,1,8,400
1,1,0,4,7,1,1,7,400
1,1,0,5,8,2,1,7,400
1,1,0,6,8,0,1,7,400
1,1,0,7,8,1,1,8,400
1,1,20,0,8,1,1,8,400
1,1,20,1,7,1,1,8,400
1,1,20,2,7,1,1,8,400
1,1,20,3,7,0,1,8,400
1,1,20,4,7,1,1,8,400
1,1,20,5,8,2,1,8,400
1,1,20,6,8,0,1,7,420
1,1,20,7,8,1,1,8,400";
            var root = new GameObject("Native mixed scenery/part fixture");
            try
            {
                var observer = new GameObject("Mixed observer"); observer.transform.SetParent(root.transform);
                observer.transform.position = new Vector3(1000, 1000, 1000);
                var combat = observer.AddComponent<Mw2MechCombat>();
                var scenery = new GameObject("Mixed GT box"); scenery.transform.SetParent(root.transform); scenery.SetActive(false);
                var box = scenery.AddComponent<BoxCollider>(); box.size = Vector3.one * 2;
                var surface = scenery.AddComponent<Mw2Surface>();
                surface.AxisBox = box;
                var unit = new GameObject("Mixed GP sphere"); unit.transform.SetParent(root.transform); unit.SetActive(false);
                var collider = unit.AddComponent<BoxCollider>();
                var part = unit.AddComponent<Mw2HitPart>(); part.SphereReady = true; part.ModelRadius = 1; part.HitCollider = collider;
                int checkedRows = 0;
                foreach (string row in nativeRows.Split('\n'))
                {
                    int[] v = row.Trim().Split(',').Select(int.Parse).ToArray();
                    if (v[3] == 0)
                    {
                        scenery.SetActive(false); unit.SetActive(false);
                        surface.CollisionType = v[1];
                        scenery.transform.position = observer.transform.position + Vector3.forward * ((500 + v[2]) * PolyModel.UnitsToMeters);
                        unit.transform.position = observer.transform.position + Vector3.forward * 5;
                    }
                    var model = v[4] == 7 ? scenery : unit;
                    if (v[5] != 2) model.SetActive(v[5] == 1);
                    else // Moving a model in the default global-list mode does not re-register it.
                    {
                        var position = model.transform.position;
                        model.transform.position = position + Vector3.right; Physics.SyncTransforms();
                        model.transform.position = position;
                    }
                    bool hit = combat.FirstHit(observer.transform.position, Vector3.forward, 20, out var result);
                    var expected = v[7] == 7 ? box : collider;
                    if (hit != (v[6] != 0) || hit && (result.collider != expected || Mathf.Abs(result.distance - v[8] * PolyModel.UnitsToMeters) > 0.001f))
                        throw new Exception("Original mixed-list cast mismatch: " + row + "; got " + (hit ? result.collider.name + " at " + result.distance : "miss"));
                    checkedRows++;
                }
                Check(checkedRows == 96, "All 96 native mixed scenery/part lifecycle casts match actual FirstHit identity and distance");
                scenery.SetActive(false); unit.SetActive(false);
                var second = new GameObject("Second coincident GT"); second.transform.SetParent(root.transform);
                second.transform.position = scenery.transform.position;
                var secondBox = second.AddComponent<BoxCollider>(); secondBox.size = box.size;
                var secondSurface = second.AddComponent<Mw2Surface>(); secondSurface.CollisionType = 1; secondSurface.AxisBox = secondBox;
                scenery.SetActive(true);
                Check(combat.FirstHit(observer.transform.position, Vector3.forward, 20, out var tie) && tie.collider == box,
                    "Equal scenery hits follow activation order across unordered Physics results");
                second.SetActive(false); second.SetActive(true);
                Check(combat.FirstHit(observer.transform.position, Vector3.forward, 20, out tie) && tie.collider == secondBox,
                    "Re-enabled scenery takes priority over an equal earlier scenery hit");
            }
            finally { DestroyImmediate(root); }
        }

        // Direct original 0x10017f60 reference rows, captured from the original program.
        // Disable unrelated fixture-world colliders temporarily so zero-world boundary precision is retained.
        void CheckNativeBoxSegments()
        {
            const string nativeRows = @"0,0,0,0,0,1,0.001,1,0,0,0,0,0,0,1
0,0,0,0,0,1,99.999,1,0,0,0,0,0,0,1
0,0,0,0,0,1,100,1,0,0,0,0,0,0,1
0,0,0,0,0,1,100.001,1,0,0,0,0,0,0,1
0,0,0,0,0,1,1000,1,0,0,0,0,0,0,1
0,0,0,0,0,-1,0.001,1,0,0,0,0,0,0,1
0,0,0,0,0,-1,99.999,1,0,0,0,0,0,0,1
0,0,0,0,0,-1,100,1,0,0,0,0,0,0,1
0,0,0,0,0,-1,100.001,1,0,0,0,0,0,0,1
0,0,0,0,0,-1,1000,1,0,0,0,0,0,0,1
0,0,0,1,0,0,0.001,1,0,0,0,0,1,0,0
0,0,0,1,0,0,99.999,1,0,0,0,0,1,0,0
0,0,0,1,0,0,100,1,0,0,0,0,1,0,0
0,0,0,1,0,0,100.001,1,0,0,0,0,1,0,0
0,0,0,1,0,0,1000,1,0,0,0,0,1,0,0
0,0,0,-1,0,0,0.001,1,0,0,0,0,1,0,0
0,0,0,-1,0,0,99.999,1,0,0,0,0,1,0,0
0,0,0,-1,0,0,100,1,0,0,0,0,1,0,0
0,0,0,-1,0,0,100.001,1,0,0,0,0,1,0,0
0,0,0,-1,0,0,1000,1,0,0,0,0,1,0,0
0,0,0,0,1,0,0.001,1,0,0,0,0,0,1,0
0,0,0,0,1,0,99.999,1,0,0,0,0,0,1,0
0,0,0,0,1,0,100,1,0,0,0,0,0,1,0
0,0,0,0,1,0,100.001,1,0,0,0,0,0,1,0
0,0,0,0,1,0,1000,1,0,0,0,0,0,1,0
0,0,0,0.6,0,0.8,0.001,1,0,0,0,0,0,0,1
0,0,0,0.6,0,0.8,99.999,1,0,0,0,0,0,0,1
0,0,0,0.6,0,0.8,100,1,0,0,0,0,0,0,1
0,0,0,0.6,0,0.8,100.001,1,0,0,0,0,0,0,1
0,0,0,0.6,0,0.8,1000,1,0,0,0,0,0,0,1
0,0,0,5E-08,0,1,0.001,1,0,0,0,0,0,0,1
0,0,0,5E-08,0,1,99.999,1,0,0,0,0,0,0,1
0,0,0,5E-08,0,1,100,1,0,0,0,0,0,0,1
0,0,0,5E-08,0,1,100.001,1,0,0,0,0,0,0,1
0,0,0,5E-08,0,1,1000,1,0,0,0,0,0,0,1
0,0,0,1E-07,0,1,0.001,1,0,0,0,0,0,0,1
0,0,0,1E-07,0,1,99.999,1,0,0,0,0,0,0,1
0,0,0,1E-07,0,1,100,1,0,0,0,0,0,0,1
0,0,0,1E-07,0,1,100.001,1,0,0,0,0,0,0,1
0,0,0,1E-07,0,1,1000,1,0,0,0,0,0,0,1
0,0,0,-1E-07,0,1,0.001,1,0,0,0,0,0,0,1
0,0,0,-1E-07,0,1,99.999,1,0,0,0,0,0,0,1
0,0,0,-1E-07,0,1,100,1,0,0,0,0,0,0,1
0,0,0,-1E-07,0,1,100.001,1,0,0,0,0,0,0,1
0,0,0,-1E-07,0,1,1000,1,0,0,0,0,0,0,1
0,0,-200,0,0,1,0.001,0,0.001,0,0,-199.999,0,0,0
0,0,-200,0,0,1,99.999,0,99.999,0,0,-100.001,0,0,0
0,0,-200,0,0,1,100,1,100,0,0,-100,0,0,-1
0,0,-200,0,0,1,100.001,1,100,0,0,-100,0,0,-1
0,0,-200,0,0,1,1000,1,100,0,0,-100,0,0,-1
0,0,-200,0,0,-1,0.001,0,0.001,0,0,-200.001,0,0,0
0,0,-200,0,0,-1,99.999,0,99.999,0,0,-299.999,0,0,0
0,0,-200,0,0,-1,100,0,100,0,0,-300,0,0,0
0,0,-200,0,0,-1,100.001,0,100.001,0,0,-300.001,0,0,0
0,0,-200,0,0,-1,1000,0,1000,0,0,-1200,0,0,0
0,0,-200,1,0,0,0.001,0,0.001,0.001,0,-200,0,0,0
0,0,-200,1,0,0,99.999,0,99.999,99.999,0,-200,0,0,0
0,0,-200,1,0,0,100,0,100,100,0,-200,0,0,0
0,0,-200,1,0,0,100.001,0,100.001,100.001,0,-200,0,0,0
0,0,-200,1,0,0,1000,0,1000,1000,0,-200,0,0,0
0,0,-200,-1,0,0,0.001,0,0.001,-0.001,0,-200,0,0,0
0,0,-200,-1,0,0,99.999,0,99.999,-99.999,0,-200,0,0,0
0,0,-200,-1,0,0,100,0,100,-100,0,-200,0,0,0
0,0,-200,-1,0,0,100.001,0,100.001,-100.001,0,-200,0,0,0
0,0,-200,-1,0,0,1000,0,1000,-1000,0,-200,0,0,0
0,0,-200,0,1,0,0.001,0,0.001,0,0.001,-200,0,0,0
0,0,-200,0,1,0,99.999,0,99.999,0,99.999,-200,0,0,0
0,0,-200,0,1,0,100,0,100,0,100,-200,0,0,0
0,0,-200,0,1,0,100.001,0,100.001,0,100.001,-200,0,0,0
0,0,-200,0,1,0,1000,0,1000,0,1000,-200,0,0,0
0,0,-200,0.6,0,0.8,0.001,0,0.001,0.0006,0,-199.9992,0,0,0
0,0,-200,0.6,0,0.8,99.999,0,99.999,59.9994,0,-120.0008,0,0,0
0,0,-200,0.6,0,0.8,100,0,100,60.0000038,0,-120,0,0,0
0,0,-200,0.6,0,0.8,100.001,0,100.001,60.0006027,0,-119.9992,0,0,0
0,0,-200,0.6,0,0.8,1000,1,125,75,0,-100,0,0,-1
0,0,-200,5E-08,0,1,0.001,0,0.001,5.00000041E-11,0,-199.999,0,0,0
0,0,-200,5E-08,0,1,99.999,0,99.999,4.99995031E-06,0,-100.001,0,0,0
0,0,-200,5E-08,0,1,100,1,100,5E-06,0,-100,0,0,-1
0,0,-200,5E-08,0,1,100.001,1,100,5E-06,0,-100,0,0,-1
0,0,-200,5E-08,0,1,1000,1,100,5E-06,0,-100,0,0,-1
0,0,-200,1E-07,0,1,0.001,0,0.001,1.00000008E-10,0,-199.999,0,0,0
0,0,-200,1E-07,0,1,99.999,0,99.999,9.999901E-06,0,-100.001,0,0,0
0,0,-200,1E-07,0,1,100,1,100,1E-05,0,-100,0,0,-1
0,0,-200,1E-07,0,1,100.001,1,100,1E-05,0,-100,0,0,-1
0,0,-200,1E-07,0,1,1000,1,100,1E-05,0,-100,0,0,-1
0,0,-200,-1E-07,0,1,0.001,0,0.001,-1.00000008E-10,0,-199.999,0,0,0
0,0,-200,-1E-07,0,1,99.999,0,99.999,-9.999901E-06,0,-100.001,0,0,0
0,0,-200,-1E-07,0,1,100,1,100,-1E-05,0,-100,0,0,-1
0,0,-200,-1E-07,0,1,100.001,1,100,-1E-05,0,-100,0,0,-1
0,0,-200,-1E-07,0,1,1000,1,100,-1E-05,0,-100,0,0,-1
0,0,-100,0,0,1,0.001,1,0,0,0,-100,0,0,1
0,0,-100,0,0,1,99.999,1,0,0,0,-100,0,0,1
0,0,-100,0,0,1,100,1,0,0,0,-100,0,0,1
0,0,-100,0,0,1,100.001,1,0,0,0,-100,0,0,1
0,0,-100,0,0,1,1000,1,0,0,0,-100,0,0,1
0,0,-100,0,0,-1,0.001,1,0,0,0,-100,0,0,1
0,0,-100,0,0,-1,99.999,1,0,0,0,-100,0,0,1
0,0,-100,0,0,-1,100,1,0,0,0,-100,0,0,1
0,0,-100,0,0,-1,100.001,1,0,0,0,-100,0,0,1
0,0,-100,0,0,-1,1000,1,0,0,0,-100,0,0,1
0,0,-100,1,0,0,0.001,1,0,0,0,-100,1,0,0
0,0,-100,1,0,0,99.999,1,0,0,0,-100,1,0,0
0,0,-100,1,0,0,100,1,0,0,0,-100,1,0,0
0,0,-100,1,0,0,100.001,1,0,0,0,-100,1,0,0
0,0,-100,1,0,0,1000,1,0,0,0,-100,1,0,0
0,0,-100,-1,0,0,0.001,1,0,0,0,-100,1,0,0
0,0,-100,-1,0,0,99.999,1,0,0,0,-100,1,0,0
0,0,-100,-1,0,0,100,1,0,0,0,-100,1,0,0
0,0,-100,-1,0,0,100.001,1,0,0,0,-100,1,0,0
0,0,-100,-1,0,0,1000,1,0,0,0,-100,1,0,0
0,0,-100,0,1,0,0.001,1,0,0,0,-100,0,1,0
0,0,-100,0,1,0,99.999,1,0,0,0,-100,0,1,0
0,0,-100,0,1,0,100,1,0,0,0,-100,0,1,0
0,0,-100,0,1,0,100.001,1,0,0,0,-100,0,1,0
0,0,-100,0,1,0,1000,1,0,0,0,-100,0,1,0
0,0,-100,0.6,0,0.8,0.001,1,0,0,0,-100,0,0,1
0,0,-100,0.6,0,0.8,99.999,1,0,0,0,-100,0,0,1
0,0,-100,0.6,0,0.8,100,1,0,0,0,-100,0,0,1
0,0,-100,0.6,0,0.8,100.001,1,0,0,0,-100,0,0,1
0,0,-100,0.6,0,0.8,1000,1,0,0,0,-100,0,0,1
0,0,-100,5E-08,0,1,0.001,1,0,0,0,-100,0,0,1
0,0,-100,5E-08,0,1,99.999,1,0,0,0,-100,0,0,1
0,0,-100,5E-08,0,1,100,1,0,0,0,-100,0,0,1
0,0,-100,5E-08,0,1,100.001,1,0,0,0,-100,0,0,1
0,0,-100,5E-08,0,1,1000,1,0,0,0,-100,0,0,1
0,0,-100,1E-07,0,1,0.001,1,0,0,0,-100,0,0,1
0,0,-100,1E-07,0,1,99.999,1,0,0,0,-100,0,0,1
0,0,-100,1E-07,0,1,100,1,0,0,0,-100,0,0,1
0,0,-100,1E-07,0,1,100.001,1,0,0,0,-100,0,0,1
0,0,-100,1E-07,0,1,1000,1,0,0,0,-100,0,0,1
0,0,-100,-1E-07,0,1,0.001,1,0,0,0,-100,0,0,1
0,0,-100,-1E-07,0,1,99.999,1,0,0,0,-100,0,0,1
0,0,-100,-1E-07,0,1,100,1,0,0,0,-100,0,0,1
0,0,-100,-1E-07,0,1,100.001,1,0,0,0,-100,0,0,1
0,0,-100,-1E-07,0,1,1000,1,0,0,0,-100,0,0,1
100,0,-200,0,0,1,0.001,0,0.001,100,0,-199.999,0,0,0
100,0,-200,0,0,1,99.999,0,99.999,100,0,-100.001,0,0,0
100,0,-200,0,0,1,100,1,100,100,0,-100,0,0,-1
100,0,-200,0,0,1,100.001,1,100,100,0,-100,0,0,-1
100,0,-200,0,0,1,1000,1,100,100,0,-100,0,0,-1
100,0,-200,0,0,-1,0.001,0,0.001,100,0,-200.001,0,0,0
100,0,-200,0,0,-1,99.999,0,99.999,100,0,-299.999,0,0,0
100,0,-200,0,0,-1,100,0,100,100,0,-300,0,0,0
100,0,-200,0,0,-1,100.001,0,100.001,100,0,-300.001,0,0,0
100,0,-200,0,0,-1,1000,0,1000,100,0,-1200,0,0,0
100,0,-200,1,0,0,0.001,0,0.001,100.001,0,-200,0,0,0
100,0,-200,1,0,0,99.999,0,99.999,199.999,0,-200,0,0,0
100,0,-200,1,0,0,100,0,100,200,0,-200,0,0,0
100,0,-200,1,0,0,100.001,0,100.001,200.001,0,-200,0,0,0
100,0,-200,1,0,0,1000,0,1000,1100,0,-200,0,0,0
100,0,-200,-1,0,0,0.001,0,0.001,99.999,0,-200,0,0,0
100,0,-200,-1,0,0,99.999,0,99.999,0.0009994507,0,-200,0,0,0
100,0,-200,-1,0,0,100,0,100,0,0,-200,0,0,0
100,0,-200,-1,0,0,100.001,0,100.001,-0.0009994507,0,-200,0,0,0
100,0,-200,-1,0,0,1000,0,1000,-900,0,-200,0,0,0
100,0,-200,0,1,0,0.001,0,0.001,100,0.001,-200,0,0,0
100,0,-200,0,1,0,99.999,0,99.999,100,99.999,-200,0,0,0
100,0,-200,0,1,0,100,0,100,100,100,-200,0,0,0
100,0,-200,0,1,0,100.001,0,100.001,100,100.001,-200,0,0,0
100,0,-200,0,1,0,1000,0,1000,100,1000,-200,0,0,0
100,0,-200,0.6,0,0.8,0.001,0,0.001,100.0006,0,-199.9992,0,0,0
100,0,-200,0.6,0,0.8,99.999,0,99.999,159.9994,0,-120.0008,0,0,0
100,0,-200,0.6,0,0.8,100,0,100,160,0,-120,0,0,0
100,0,-200,0.6,0,0.8,100.001,0,100.001,160.00061,0,-119.9992,0,0,0
100,0,-200,0.6,0,0.8,1000,0,1000,700,0,600,0,0,0
100,0,-200,5E-08,0,1,0.001,0,0.001,100,0,-199.999,0,0,0
100,0,-200,5E-08,0,1,99.999,0,99.999,100.000008,0,-100.001,0,0,0
100,0,-200,5E-08,0,1,100,1,100,100.000008,0,-100,0,0,-1
100,0,-200,5E-08,0,1,100.001,1,100,100.000008,0,-100,0,0,-1
100,0,-200,5E-08,0,1,1000,1,100,100.000008,0,-100,0,0,-1
100,0,-200,1E-07,0,1,0.001,0,0.001,100,0,-199.999,0,0,0
100,0,-200,1E-07,0,1,99.999,0,99.999,100.000008,0,-100.001,0,0,0
100,0,-200,1E-07,0,1,100,0,100,100.000008,0,-100,0,0,0
100,0,-200,1E-07,0,1,100.001,0,100.001,100.000008,0,-99.999,0,0,0
100,0,-200,1E-07,0,1,1000,0,1000,100.0001,0,800,0,0,0
100,0,-200,-1E-07,0,1,0.001,0,0.001,100,0,-199.999,0,0,0
100,0,-200,-1E-07,0,1,99.999,0,99.999,99.99999,0,-100.001,0,0,0
100,0,-200,-1E-07,0,1,100,1,100,99.99999,0,-100,0,0,-1
100,0,-200,-1E-07,0,1,100.001,1,100,99.99999,0,-100,0,0,-1
100,0,-200,-1E-07,0,1,1000,1,100,99.99999,0,-100,0,0,-1
100.001,0,-200,0,0,1,0.001,0,0.001,100.001,0,-199.999,0,0,0
100.001,0,-200,0,0,1,99.999,0,99.999,100.001,0,-100.001,0,0,0
100.001,0,-200,0,0,1,100,0,100,100.001,0,-100,0,0,0
100.001,0,-200,0,0,1,100.001,0,100.001,100.001,0,-99.999,0,0,0
100.001,0,-200,0,0,1,1000,0,1000,100.001,0,800,0,0,0
100.001,0,-200,0,0,-1,0.001,0,0.001,100.001,0,-200.001,0,0,0
100.001,0,-200,0,0,-1,99.999,0,99.999,100.001,0,-299.999,0,0,0
100.001,0,-200,0,0,-1,100,0,100,100.001,0,-300,0,0,0
100.001,0,-200,0,0,-1,100.001,0,100.001,100.001,0,-300.001,0,0,0
100.001,0,-200,0,0,-1,1000,0,1000,100.001,0,-1200,0,0,0
100.001,0,-200,1,0,0,0.001,0,0.001,100.002,0,-200,0,0,0
100.001,0,-200,1,0,0,99.999,0,99.999,200,0,-200,0,0,0
100.001,0,-200,1,0,0,100,0,100,200.001,0,-200,0,0,0
100.001,0,-200,1,0,0,100.001,0,100.001,200.002,0,-200,0,0,0
100.001,0,-200,1,0,0,1000,0,1000,1100.001,0,-200,0,0,0
100.001,0,-200,-1,0,0,0.001,0,0.001,100,0,-200,0,0,0
100.001,0,-200,-1,0,0,99.999,0,99.999,0.00199890137,0,-200,0,0,0
100.001,0,-200,-1,0,0,100,0,100,0.0009994507,0,-200,0,0,0
100.001,0,-200,-1,0,0,100.001,0,100.001,0,0,-200,0,0,0
100.001,0,-200,-1,0,0,1000,0,1000,-899.999,0,-200,0,0,0
100.001,0,-200,0,1,0,0.001,0,0.001,100.001,0.001,-200,0,0,0
100.001,0,-200,0,1,0,99.999,0,99.999,100.001,99.999,-200,0,0,0
100.001,0,-200,0,1,0,100,0,100,100.001,100,-200,0,0,0
100.001,0,-200,0,1,0,100.001,0,100.001,100.001,100.001,-200,0,0,0
100.001,0,-200,0,1,0,1000,0,1000,100.001,1000,-200,0,0,0
100.001,0,-200,0.6,0,0.8,0.001,0,0.001,100.0016,0,-199.9992,0,0,0
100.001,0,-200,0.6,0,0.8,99.999,0,99.999,160.0004,0,-120.0008,0,0,0
100.001,0,-200,0.6,0,0.8,100,0,100,160.001,0,-120,0,0,0
100.001,0,-200,0.6,0,0.8,100.001,0,100.001,160.0016,0,-119.9992,0,0,0
100.001,0,-200,0.6,0,0.8,1000,0,1000,700.001,0,600,0,0,0
100.001,0,-200,5E-08,0,1,0.001,0,0.001,100.001,0,-199.999,0,0,0
100.001,0,-200,5E-08,0,1,99.999,0,99.999,100.001007,0,-100.001,0,0,0
100.001,0,-200,5E-08,0,1,100,0,100,100.001007,0,-100,0,0,0
100.001,0,-200,5E-08,0,1,100.001,0,100.001,100.001007,0,-99.999,0,0,0
100.001,0,-200,5E-08,0,1,1000,0,1000,100.001053,0,800,0,0,0
100.001,0,-200,1E-07,0,1,0.001,0,0.001,100.001,0,-199.999,0,0,0
100.001,0,-200,1E-07,0,1,99.999,0,99.999,100.001007,0,-100.001,0,0,0
100.001,0,-200,1E-07,0,1,100,0,100,100.001007,0,-100,0,0,0
100.001,0,-200,1E-07,0,1,100.001,0,100.001,100.001007,0,-99.999,0,0,0
100.001,0,-200,1E-07,0,1,1000,0,1000,100.0011,0,800,0,0,0
100.001,0,-200,-1E-07,0,1,0.001,0,0.001,100.001,0,-199.999,0,0,0
100.001,0,-200,-1E-07,0,1,99.999,0,99.999,100.000992,0,-100.001,0,0,0
100.001,0,-200,-1E-07,0,1,100,0,100,100.000992,0,-100,0,0,0
100.001,0,-200,-1E-07,0,1,100.001,0,100.001,100.000992,0,-99.999,0,0,0
100.001,0,-200,-1E-07,0,1,1000,0,1000,100.0009,0,800,0,0,0
0,100,-200,0,0,1,0.001,0,0.001,0,100,-199.999,0,0,0
0,100,-200,0,0,1,99.999,0,99.999,0,100,-100.001,0,0,0
0,100,-200,0,0,1,100,1,100,0,100,-100,0,0,-1
0,100,-200,0,0,1,100.001,1,100,0,100,-100,0,0,-1
0,100,-200,0,0,1,1000,1,100,0,100,-100,0,0,-1
0,100,-200,0,0,-1,0.001,0,0.001,0,100,-200.001,0,0,0
0,100,-200,0,0,-1,99.999,0,99.999,0,100,-299.999,0,0,0
0,100,-200,0,0,-1,100,0,100,0,100,-300,0,0,0
0,100,-200,0,0,-1,100.001,0,100.001,0,100,-300.001,0,0,0
0,100,-200,0,0,-1,1000,0,1000,0,100,-1200,0,0,0
0,100,-200,1,0,0,0.001,0,0.001,0.001,100,-200,0,0,0
0,100,-200,1,0,0,99.999,0,99.999,99.999,100,-200,0,0,0
0,100,-200,1,0,0,100,0,100,100,100,-200,0,0,0
0,100,-200,1,0,0,100.001,0,100.001,100.001,100,-200,0,0,0
0,100,-200,1,0,0,1000,0,1000,1000,100,-200,0,0,0
0,100,-200,-1,0,0,0.001,0,0.001,-0.001,100,-200,0,0,0
0,100,-200,-1,0,0,99.999,0,99.999,-99.999,100,-200,0,0,0
0,100,-200,-1,0,0,100,0,100,-100,100,-200,0,0,0
0,100,-200,-1,0,0,100.001,0,100.001,-100.001,100,-200,0,0,0
0,100,-200,-1,0,0,1000,0,1000,-1000,100,-200,0,0,0
0,100,-200,0,1,0,0.001,0,0.001,0,100.001,-200,0,0,0
0,100,-200,0,1,0,99.999,0,99.999,0,199.999,-200,0,0,0
0,100,-200,0,1,0,100,0,100,0,200,-200,0,0,0
0,100,-200,0,1,0,100.001,0,100.001,0,200.001,-200,0,0,0
0,100,-200,0,1,0,1000,0,1000,0,1100,-200,0,0,0
0,100,-200,0.6,0,0.8,0.001,0,0.001,0.0006,100,-199.9992,0,0,0
0,100,-200,0.6,0,0.8,99.999,0,99.999,59.9994,100,-120.0008,0,0,0
0,100,-200,0.6,0,0.8,100,0,100,60.0000038,100,-120,0,0,0
0,100,-200,0.6,0,0.8,100.001,0,100.001,60.0006027,100,-119.9992,0,0,0
0,100,-200,0.6,0,0.8,1000,1,125,75,100,-100,0,0,-1
0,100,-200,5E-08,0,1,0.001,0,0.001,5.00000041E-11,100,-199.999,0,0,0
0,100,-200,5E-08,0,1,99.999,0,99.999,4.99995031E-06,100,-100.001,0,0,0
0,100,-200,5E-08,0,1,100,1,100,5E-06,100,-100,0,0,-1
0,100,-200,5E-08,0,1,100.001,1,100,5E-06,100,-100,0,0,-1
0,100,-200,5E-08,0,1,1000,1,100,5E-06,100,-100,0,0,-1
0,100,-200,1E-07,0,1,0.001,0,0.001,1.00000008E-10,100,-199.999,0,0,0
0,100,-200,1E-07,0,1,99.999,0,99.999,9.999901E-06,100,-100.001,0,0,0
0,100,-200,1E-07,0,1,100,1,100,1E-05,100,-100,0,0,-1
0,100,-200,1E-07,0,1,100.001,1,100,1E-05,100,-100,0,0,-1
0,100,-200,1E-07,0,1,1000,1,100,1E-05,100,-100,0,0,-1
0,100,-200,-1E-07,0,1,0.001,0,0.001,-1.00000008E-10,100,-199.999,0,0,0
0,100,-200,-1E-07,0,1,99.999,0,99.999,-9.999901E-06,100,-100.001,0,0,0
0,100,-200,-1E-07,0,1,100,1,100,-1E-05,100,-100,0,0,-1
0,100,-200,-1E-07,0,1,100.001,1,100,-1E-05,100,-100,0,0,-1
0,100,-200,-1E-07,0,1,1000,1,100,-1E-05,100,-100,0,0,-1
0,100.001,-200,0,0,1,0.001,0,0.001,0,100.001,-199.999,0,0,0
0,100.001,-200,0,0,1,99.999,0,99.999,0,100.001,-100.001,0,0,0
0,100.001,-200,0,0,1,100,0,100,0,100.001,-100,0,0,0
0,100.001,-200,0,0,1,100.001,0,100.001,0,100.001,-99.999,0,0,0
0,100.001,-200,0,0,1,1000,0,1000,0,100.001,800,0,0,0
0,100.001,-200,0,0,-1,0.001,0,0.001,0,100.001,-200.001,0,0,0
0,100.001,-200,0,0,-1,99.999,0,99.999,0,100.001,-299.999,0,0,0
0,100.001,-200,0,0,-1,100,0,100,0,100.001,-300,0,0,0
0,100.001,-200,0,0,-1,100.001,0,100.001,0,100.001,-300.001,0,0,0
0,100.001,-200,0,0,-1,1000,0,1000,0,100.001,-1200,0,0,0
0,100.001,-200,1,0,0,0.001,0,0.001,0.001,100.001,-200,0,0,0
0,100.001,-200,1,0,0,99.999,0,99.999,99.999,100.001,-200,0,0,0
0,100.001,-200,1,0,0,100,0,100,100,100.001,-200,0,0,0
0,100.001,-200,1,0,0,100.001,0,100.001,100.001,100.001,-200,0,0,0
0,100.001,-200,1,0,0,1000,0,1000,1000,100.001,-200,0,0,0
0,100.001,-200,-1,0,0,0.001,0,0.001,-0.001,100.001,-200,0,0,0
0,100.001,-200,-1,0,0,99.999,0,99.999,-99.999,100.001,-200,0,0,0
0,100.001,-200,-1,0,0,100,0,100,-100,100.001,-200,0,0,0
0,100.001,-200,-1,0,0,100.001,0,100.001,-100.001,100.001,-200,0,0,0
0,100.001,-200,-1,0,0,1000,0,1000,-1000,100.001,-200,0,0,0
0,100.001,-200,0,1,0,0.001,0,0.001,0,100.002,-200,0,0,0
0,100.001,-200,0,1,0,99.999,0,99.999,0,200,-200,0,0,0
0,100.001,-200,0,1,0,100,0,100,0,200.001,-200,0,0,0
0,100.001,-200,0,1,0,100.001,0,100.001,0,200.002,-200,0,0,0
0,100.001,-200,0,1,0,1000,0,1000,0,1100.001,-200,0,0,0
0,100.001,-200,0.6,0,0.8,0.001,0,0.001,0.0006,100.001,-199.9992,0,0,0
0,100.001,-200,0.6,0,0.8,99.999,0,99.999,59.9994,100.001,-120.0008,0,0,0
0,100.001,-200,0.6,0,0.8,100,0,100,60.0000038,100.001,-120,0,0,0
0,100.001,-200,0.6,0,0.8,100.001,0,100.001,60.0006027,100.001,-119.9992,0,0,0
0,100.001,-200,0.6,0,0.8,1000,0,1000,600,100.001,600,0,0,0
0,100.001,-200,5E-08,0,1,0.001,0,0.001,5.00000041E-11,100.001,-199.999,0,0,0
0,100.001,-200,5E-08,0,1,99.999,0,99.999,4.99995031E-06,100.001,-100.001,0,0,0
0,100.001,-200,5E-08,0,1,100,0,100,5E-06,100.001,-100,0,0,0
0,100.001,-200,5E-08,0,1,100.001,0,100.001,5.00005E-06,100.001,-99.999,0,0,0
0,100.001,-200,5E-08,0,1,1000,0,1000,5.00000024E-05,100.001,800,0,0,0
0,100.001,-200,1E-07,0,1,0.001,0,0.001,1.00000008E-10,100.001,-199.999,0,0,0
0,100.001,-200,1E-07,0,1,99.999,0,99.999,9.999901E-06,100.001,-100.001,0,0,0
0,100.001,-200,1E-07,0,1,100,0,100,1E-05,100.001,-100,0,0,0
0,100.001,-200,1E-07,0,1,100.001,0,100.001,1.00001E-05,100.001,-99.999,0,0,0
0,100.001,-200,1E-07,0,1,1000,0,1000,0.000100000005,100.001,800,0,0,0
0,100.001,-200,-1E-07,0,1,0.001,0,0.001,-1.00000008E-10,100.001,-199.999,0,0,0
0,100.001,-200,-1E-07,0,1,99.999,0,99.999,-9.999901E-06,100.001,-100.001,0,0,0
0,100.001,-200,-1E-07,0,1,100,0,100,-1E-05,100.001,-100,0,0,0
0,100.001,-200,-1E-07,0,1,100.001,0,100.001,-1.00001E-05,100.001,-99.999,0,0,0
0,100.001,-200,-1E-07,0,1,1000,0,1000,-0.000100000005,100.001,800,0,0,0
100,100,100,0,0,1,0.001,1,0,100,100,100,0,0,1
100,100,100,0,0,1,99.999,1,0,100,100,100,0,0,1
100,100,100,0,0,1,100,1,0,100,100,100,0,0,1
100,100,100,0,0,1,100.001,1,0,100,100,100,0,0,1
100,100,100,0,0,1,1000,1,0,100,100,100,0,0,1
100,100,100,0,0,-1,0.001,1,0,100,100,100,0,0,1
100,100,100,0,0,-1,99.999,1,0,100,100,100,0,0,1
100,100,100,0,0,-1,100,1,0,100,100,100,0,0,1
100,100,100,0,0,-1,100.001,1,0,100,100,100,0,0,1
100,100,100,0,0,-1,1000,1,0,100,100,100,0,0,1
100,100,100,1,0,0,0.001,1,0,100,100,100,1,0,0
100,100,100,1,0,0,99.999,1,0,100,100,100,1,0,0
100,100,100,1,0,0,100,1,0,100,100,100,1,0,0
100,100,100,1,0,0,100.001,1,0,100,100,100,1,0,0
100,100,100,1,0,0,1000,1,0,100,100,100,1,0,0
100,100,100,-1,0,0,0.001,1,0,100,100,100,1,0,0
100,100,100,-1,0,0,99.999,1,0,100,100,100,1,0,0
100,100,100,-1,0,0,100,1,0,100,100,100,1,0,0
100,100,100,-1,0,0,100.001,1,0,100,100,100,1,0,0
100,100,100,-1,0,0,1000,1,0,100,100,100,1,0,0
100,100,100,0,1,0,0.001,1,0,100,100,100,0,1,0
100,100,100,0,1,0,99.999,1,0,100,100,100,0,1,0
100,100,100,0,1,0,100,1,0,100,100,100,0,1,0
100,100,100,0,1,0,100.001,1,0,100,100,100,0,1,0
100,100,100,0,1,0,1000,1,0,100,100,100,0,1,0
100,100,100,0.6,0,0.8,0.001,1,0,100,100,100,0,0,1
100,100,100,0.6,0,0.8,99.999,1,0,100,100,100,0,0,1
100,100,100,0.6,0,0.8,100,1,0,100,100,100,0,0,1
100,100,100,0.6,0,0.8,100.001,1,0,100,100,100,0,0,1
100,100,100,0.6,0,0.8,1000,1,0,100,100,100,0,0,1
100,100,100,5E-08,0,1,0.001,1,0,100,100,100,0,0,1
100,100,100,5E-08,0,1,99.999,1,0,100,100,100,0,0,1
100,100,100,5E-08,0,1,100,1,0,100,100,100,0,0,1
100,100,100,5E-08,0,1,100.001,1,0,100,100,100,0,0,1
100,100,100,5E-08,0,1,1000,1,0,100,100,100,0,0,1
100,100,100,1E-07,0,1,0.001,1,0,100,100,100,0,0,1
100,100,100,1E-07,0,1,99.999,1,0,100,100,100,0,0,1
100,100,100,1E-07,0,1,100,1,0,100,100,100,0,0,1
100,100,100,1E-07,0,1,100.001,1,0,100,100,100,0,0,1
100,100,100,1E-07,0,1,1000,1,0,100,100,100,0,0,1
100,100,100,-1E-07,0,1,0.001,1,0,100,100,100,1,0,0
100,100,100,-1E-07,0,1,99.999,1,0,100,100,100,1,0,0
100,100,100,-1E-07,0,1,100,1,0,100,100,100,1,0,0
100,100,100,-1E-07,0,1,100.001,1,0,100,100,100,1,0,0
100,100,100,-1E-07,0,1,1000,1,0,100,100,100,1,0,0
-100,-100,-100,0,0,1,0.001,1,0,-100,-100,-100,0,0,1
-100,-100,-100,0,0,1,99.999,1,0,-100,-100,-100,0,0,1
-100,-100,-100,0,0,1,100,1,0,-100,-100,-100,0,0,1
-100,-100,-100,0,0,1,100.001,1,0,-100,-100,-100,0,0,1
-100,-100,-100,0,0,1,1000,1,0,-100,-100,-100,0,0,1
-100,-100,-100,0,0,-1,0.001,1,0,-100,-100,-100,0,0,1
-100,-100,-100,0,0,-1,99.999,1,0,-100,-100,-100,0,0,1
-100,-100,-100,0,0,-1,100,1,0,-100,-100,-100,0,0,1
-100,-100,-100,0,0,-1,100.001,1,0,-100,-100,-100,0,0,1
-100,-100,-100,0,0,-1,1000,1,0,-100,-100,-100,0,0,1
-100,-100,-100,1,0,0,0.001,1,0,-100,-100,-100,1,0,0
-100,-100,-100,1,0,0,99.999,1,0,-100,-100,-100,1,0,0
-100,-100,-100,1,0,0,100,1,0,-100,-100,-100,1,0,0
-100,-100,-100,1,0,0,100.001,1,0,-100,-100,-100,1,0,0
-100,-100,-100,1,0,0,1000,1,0,-100,-100,-100,1,0,0
-100,-100,-100,-1,0,0,0.001,1,0,-100,-100,-100,1,0,0
-100,-100,-100,-1,0,0,99.999,1,0,-100,-100,-100,1,0,0
-100,-100,-100,-1,0,0,100,1,0,-100,-100,-100,1,0,0
-100,-100,-100,-1,0,0,100.001,1,0,-100,-100,-100,1,0,0
-100,-100,-100,-1,0,0,1000,1,0,-100,-100,-100,1,0,0
-100,-100,-100,0,1,0,0.001,1,0,-100,-100,-100,0,1,0
-100,-100,-100,0,1,0,99.999,1,0,-100,-100,-100,0,1,0
-100,-100,-100,0,1,0,100,1,0,-100,-100,-100,0,1,0
-100,-100,-100,0,1,0,100.001,1,0,-100,-100,-100,0,1,0
-100,-100,-100,0,1,0,1000,1,0,-100,-100,-100,0,1,0
-100,-100,-100,0.6,0,0.8,0.001,1,0,-100,-100,-100,1,0,0
-100,-100,-100,0.6,0,0.8,99.999,1,0,-100,-100,-100,1,0,0
-100,-100,-100,0.6,0,0.8,100,1,0,-100,-100,-100,1,0,0
-100,-100,-100,0.6,0,0.8,100.001,1,0,-100,-100,-100,1,0,0
-100,-100,-100,0.6,0,0.8,1000,1,0,-100,-100,-100,1,0,0
-100,-100,-100,5E-08,0,1,0.001,1,0,-100,-100,-100,0,0,1
-100,-100,-100,5E-08,0,1,99.999,1,0,-100,-100,-100,0,0,1
-100,-100,-100,5E-08,0,1,100,1,0,-100,-100,-100,0,0,1
-100,-100,-100,5E-08,0,1,100.001,1,0,-100,-100,-100,0,0,1
-100,-100,-100,5E-08,0,1,1000,1,0,-100,-100,-100,0,0,1
-100,-100,-100,1E-07,0,1,0.001,1,0,-100,-100,-100,1,0,0
-100,-100,-100,1E-07,0,1,99.999,1,0,-100,-100,-100,1,0,0
-100,-100,-100,1E-07,0,1,100,1,0,-100,-100,-100,1,0,0
-100,-100,-100,1E-07,0,1,100.001,1,0,-100,-100,-100,1,0,0
-100,-100,-100,1E-07,0,1,1000,1,0,-100,-100,-100,1,0,0
-100,-100,-100,-1E-07,0,1,0.001,1,0,-100,-100,-100,0,0,1
-100,-100,-100,-1E-07,0,1,99.999,1,0,-100,-100,-100,0,0,1
-100,-100,-100,-1E-07,0,1,100,1,0,-100,-100,-100,0,0,1
-100,-100,-100,-1E-07,0,1,100.001,1,0,-100,-100,-100,0,0,1
-100,-100,-100,-1E-07,0,1,1000,1,0,-100,-100,-100,0,0,1
-200,-200,-200,0,0,1,0.001,0,0.001,-200,-200,-199.999,0,0,0
-200,-200,-200,0,0,1,99.999,0,99.999,-200,-200,-100.001,0,0,0
-200,-200,-200,0,0,1,100,0,100,-200,-200,-100,0,0,0
-200,-200,-200,0,0,1,100.001,0,100.001,-200,-200,-99.999,0,0,0
-200,-200,-200,0,0,1,1000,0,1000,-200,-200,800,0,0,0
-200,-200,-200,0,0,-1,0.001,0,0.001,-200,-200,-200.001,0,0,0
-200,-200,-200,0,0,-1,99.999,0,99.999,-200,-200,-299.999,0,0,0
-200,-200,-200,0,0,-1,100,0,100,-200,-200,-300,0,0,0
-200,-200,-200,0,0,-1,100.001,0,100.001,-200,-200,-300.001,0,0,0
-200,-200,-200,0,0,-1,1000,0,1000,-200,-200,-1200,0,0,0
-200,-200,-200,1,0,0,0.001,0,0.001,-199.999,-200,-200,0,0,0
-200,-200,-200,1,0,0,99.999,0,99.999,-100.001,-200,-200,0,0,0
-200,-200,-200,1,0,0,100,0,100,-100,-200,-200,0,0,0
-200,-200,-200,1,0,0,100.001,0,100.001,-99.999,-200,-200,0,0,0
-200,-200,-200,1,0,0,1000,0,1000,800,-200,-200,0,0,0
-200,-200,-200,-1,0,0,0.001,0,0.001,-200.001,-200,-200,0,0,0
-200,-200,-200,-1,0,0,99.999,0,99.999,-299.999,-200,-200,0,0,0
-200,-200,-200,-1,0,0,100,0,100,-300,-200,-200,0,0,0
-200,-200,-200,-1,0,0,100.001,0,100.001,-300.001,-200,-200,0,0,0
-200,-200,-200,-1,0,0,1000,0,1000,-1200,-200,-200,0,0,0
-200,-200,-200,0,1,0,0.001,0,0.001,-200,-199.999,-200,0,0,0
-200,-200,-200,0,1,0,99.999,0,99.999,-200,-100.001,-200,0,0,0
-200,-200,-200,0,1,0,100,0,100,-200,-100,-200,0,0,0
-200,-200,-200,0,1,0,100.001,0,100.001,-200,-99.999,-200,0,0,0
-200,-200,-200,0,1,0,1000,0,1000,-200,800,-200,0,0,0
-200,-200,-200,0.6,0,0.8,0.001,0,0.001,-199.9994,-200,-199.9992,0,0,0
-200,-200,-200,0.6,0,0.8,99.999,0,99.999,-140.0006,-200,-120.0008,0,0,0
-200,-200,-200,0.6,0,0.8,100,0,100,-140,-200,-120,0,0,0
-200,-200,-200,0.6,0,0.8,100.001,0,100.001,-139.99939,-200,-119.9992,0,0,0
-200,-200,-200,0.6,0,0.8,1000,0,1000,400,-200,600,0,0,0
-200,-200,-200,5E-08,0,1,0.001,0,0.001,-200,-200,-199.999,0,0,0
-200,-200,-200,5E-08,0,1,99.999,0,99.999,-200,-200,-100.001,0,0,0
-200,-200,-200,5E-08,0,1,100,0,100,-200,-200,-100,0,0,0
-200,-200,-200,5E-08,0,1,100.001,0,100.001,-200,-200,-99.999,0,0,0
-200,-200,-200,5E-08,0,1,1000,0,1000,-199.999954,-200,800,0,0,0
-200,-200,-200,1E-07,0,1,0.001,0,0.001,-200,-200,-199.999,0,0,0
-200,-200,-200,1E-07,0,1,99.999,0,99.999,-199.999985,-200,-100.001,0,0,0
-200,-200,-200,1E-07,0,1,100,0,100,-199.999985,-200,-100,0,0,0
-200,-200,-200,1E-07,0,1,100.001,0,100.001,-199.999985,-200,-99.999,0,0,0
-200,-200,-200,1E-07,0,1,1000,0,1000,-199.9999,-200,800,0,0,0
-200,-200,-200,-1E-07,0,1,0.001,0,0.001,-200,-200,-199.999,0,0,0
-200,-200,-200,-1E-07,0,1,99.999,0,99.999,-200.000015,-200,-100.001,0,0,0
-200,-200,-200,-1E-07,0,1,100,0,100,-200.000015,-200,-100,0,0,0
-200,-200,-200,-1E-07,0,1,100.001,0,100.001,-200.000015,-200,-99.999,0,0,0
-200,-200,-200,-1E-07,0,1,1000,0,1000,-200.0001,-200,800,0,0,0
-99.999,0,0,0,0,1,0.001,1,0,-99.999,0,0,0,0,1
-99.999,0,0,0,0,1,99.999,1,0,-99.999,0,0,0,0,1
-99.999,0,0,0,0,1,100,1,0,-99.999,0,0,0,0,1
-99.999,0,0,0,0,1,100.001,1,0,-99.999,0,0,0,0,1
-99.999,0,0,0,0,1,1000,1,0,-99.999,0,0,0,0,1
-99.999,0,0,0,0,-1,0.001,1,0,-99.999,0,0,0,0,1
-99.999,0,0,0,0,-1,99.999,1,0,-99.999,0,0,0,0,1
-99.999,0,0,0,0,-1,100,1,0,-99.999,0,0,0,0,1
-99.999,0,0,0,0,-1,100.001,1,0,-99.999,0,0,0,0,1
-99.999,0,0,0,0,-1,1000,1,0,-99.999,0,0,0,0,1
-99.999,0,0,1,0,0,0.001,1,0,-99.999,0,0,1,0,0
-99.999,0,0,1,0,0,99.999,1,0,-99.999,0,0,1,0,0
-99.999,0,0,1,0,0,100,1,0,-99.999,0,0,1,0,0
-99.999,0,0,1,0,0,100.001,1,0,-99.999,0,0,1,0,0
-99.999,0,0,1,0,0,1000,1,0,-99.999,0,0,1,0,0
-99.999,0,0,-1,0,0,0.001,1,0,-99.999,0,0,1,0,0
-99.999,0,0,-1,0,0,99.999,1,0,-99.999,0,0,1,0,0
-99.999,0,0,-1,0,0,100,1,0,-99.999,0,0,1,0,0
-99.999,0,0,-1,0,0,100.001,1,0,-99.999,0,0,1,0,0
-99.999,0,0,-1,0,0,1000,1,0,-99.999,0,0,1,0,0
-99.999,0,0,0,1,0,0.001,1,0,-99.999,0,0,0,1,0
-99.999,0,0,0,1,0,99.999,1,0,-99.999,0,0,0,1,0
-99.999,0,0,0,1,0,100,1,0,-99.999,0,0,0,1,0
-99.999,0,0,0,1,0,100.001,1,0,-99.999,0,0,0,1,0
-99.999,0,0,0,1,0,1000,1,0,-99.999,0,0,0,1,0
-99.999,0,0,0.6,0,0.8,0.001,1,0,-99.999,0,0,1,0,0
-99.999,0,0,0.6,0,0.8,99.999,1,0,-99.999,0,0,1,0,0
-99.999,0,0,0.6,0,0.8,100,1,0,-99.999,0,0,1,0,0
-99.999,0,0,0.6,0,0.8,100.001,1,0,-99.999,0,0,1,0,0
-99.999,0,0,0.6,0,0.8,1000,1,0,-99.999,0,0,1,0,0
-99.999,0,0,5E-08,0,1,0.001,1,0,-99.999,0,0,0,0,1
-99.999,0,0,5E-08,0,1,99.999,1,0,-99.999,0,0,0,0,1
-99.999,0,0,5E-08,0,1,100,1,0,-99.999,0,0,0,0,1
-99.999,0,0,5E-08,0,1,100.001,1,0,-99.999,0,0,0,0,1
-99.999,0,0,5E-08,0,1,1000,1,0,-99.999,0,0,0,0,1
-99.999,0,0,1E-07,0,1,0.001,1,0,-99.999,0,0,0,0,1
-99.999,0,0,1E-07,0,1,99.999,1,0,-99.999,0,0,0,0,1
-99.999,0,0,1E-07,0,1,100,1,0,-99.999,0,0,0,0,1
-99.999,0,0,1E-07,0,1,100.001,1,0,-99.999,0,0,0,0,1
-99.999,0,0,1E-07,0,1,1000,1,0,-99.999,0,0,0,0,1
-99.999,0,0,-1E-07,0,1,0.001,1,0,-99.999,0,0,0,0,1
-99.999,0,0,-1E-07,0,1,99.999,1,0,-99.999,0,0,0,0,1
-99.999,0,0,-1E-07,0,1,100,1,0,-99.999,0,0,0,0,1
-99.999,0,0,-1E-07,0,1,100.001,1,0,-99.999,0,0,0,0,1
-99.999,0,0,-1E-07,0,1,1000,1,0,-99.999,0,0,0,0,1";
            var previous = FindObjectsByType<Collider>(FindObjectsSortMode.None).Where(c => c.enabled).ToArray();
            var root = new GameObject("Native box segment fixture");
            try
            {
                foreach (var c in previous) c.enabled = false;
                var observer = new GameObject("Box observer"); observer.transform.SetParent(root.transform);
                var combat = observer.AddComponent<Mw2MechCombat>();
                var scenery = new GameObject("Native world box"); scenery.transform.SetParent(root.transform);
                var box = scenery.AddComponent<BoxCollider>(); box.size = Vector3.one * 2;
                var surface = scenery.AddComponent<Mw2Surface>(); surface.CollisionType = 1; surface.AxisBox = box;
                int checkedRows = 0;
                foreach (string row in nativeRows.Split('\n'))
                {
                    var v = row.Trim().Split(',').Select(s => float.Parse(s, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                    var start = new Vector3(v[0], v[1], v[2]) * PolyModel.UnitsToMeters;
                    var direction = new Vector3(v[3], v[4], v[5]);
                    bool hit = combat.FirstHit(start, direction, v[6] * PolyModel.UnitsToMeters, out var result);
                    var point = new Vector3(v[9], v[10], v[11]) * PolyModel.UnitsToMeters;
                    var normal = new Vector3(v[12], v[13], v[14]);
                    if (hit != (v[7] != 0) || hit && (result.collider != box || Mathf.Abs(result.distance - v[8] * PolyModel.UnitsToMeters) > 0.00001f ||
                        Vector3.Distance(result.point, point) > 0.00001f || result.normal != normal))
                        throw new Exception("Original box segment adapter mismatch: " + row + "; got " + (hit ? result.distance.ToString("R") : "miss"));
                    checkedRows++;
                }
                Check(checkedRows == 495, "All 495 original box inside/boundary/parallel casts match actual FirstHit distance, point and normal");
                box.enabled = false;
                Check(!combat.FirstHit(Vector3.zero, Vector3.forward, 10, out _), "Disabled world-axis box is excluded from native segment queries");
                box.enabled = true; scenery.transform.SetParent(observer.transform);
                Check(!combat.FirstHit(Vector3.zero, Vector3.forward, 10, out _), "A unit's own world-axis box is excluded from native segment queries");
            }
            finally
            {
                DestroyImmediate(root);
                foreach (var c in previous) if (c != null) c.enabled = true;
            }
        }

        // 10049159 head insertion + 10018ad5 first equal-distance hit.
        void CheckPartTieOrder()
        {
            var root = new GameObject("Native equal-hit order fixture");
            try
            {
                var gun = new GameObject("Equal-hit observer"); gun.transform.SetParent(root.transform);
                gun.transform.position = new Vector3(100000, 1000, 100000);
                var combat = gun.AddComponent<Mw2MechCombat>();
                var parts = new Mw2HitPart[2];
                for (int i = 0; i < 2; i++)
                {
                    var go = new GameObject("Equal part " + i); go.transform.SetParent(root.transform);
                    go.transform.position = gun.transform.position + Vector3.forward * 10;
                    var collider = go.AddComponent<BoxCollider>();
                    parts[i] = go.AddComponent<Mw2HitPart>();
                    parts[i].SphereReady = true; parts[i].ModelRadius = 1; parts[i].HitCollider = collider;
                }
                Check(combat.FirstHit(gun.transform.position, Vector3.forward, 20, out var hit) && hit.collider == parts[1].HitCollider,
                    "Equal part hits prefer the last collision-enabled model, matching native head insertion");
                parts[0].gameObject.SetActive(true);
                Check(combat.FirstHit(gun.transform.position, Vector3.forward, 20, out hit) && hit.collider == parts[1].HitCollider,
                    "Enabling an already active part preserves native equal-hit order");
                parts[0].gameObject.SetActive(false); parts[0].gameObject.SetActive(true);
                Check(combat.FirstHit(gun.transform.position, Vector3.forward, 20, out hit) && hit.collider == parts[0].HitCollider,
                    "Re-enabled part moves to the front of the equal-distance hit order");
                var body = new GameObject("LOD tie owner"); body.transform.SetParent(root.transform);
                var lods = body.AddComponent<Mw2LodGroup>();
                var levels = new Mw2HitPart[2][];
                for (int level = 0; level < 2; level++)
                {
                    levels[level] = new Mw2HitPart[2]; var renderers = new Renderer[2];
                    for (int i = 0; i < 2; i++)
                    {
                        var go = new GameObject("LOD tie " + level + "/" + i); go.transform.SetParent(body.transform);
                        go.transform.position = parts[0].transform.position;
                        renderers[i] = go.AddComponent<MeshRenderer>();
                        var collider = go.AddComponent<BoxCollider>(); var part = go.AddComponent<Mw2HitPart>();
                        part.SphereReady = true; part.ModelRadius = 1; part.HitCollider = collider;
                        part.LodGroup = lods; part.Representation = level == 0 ? 1 : 4; levels[level][i] = part;
                    }
                    lods.Levels.Add(new Mw2LodGroup.Level { Lod = level == 0 ? 1 : 4, Renderers = renderers });
                }
                parts[0].gameObject.SetActive(false); parts[0].gameObject.SetActive(true);
                lods.Show(4);
                Check(combat.FirstHit(gun.transform.position, Vector3.forward, 20, out hit) && hit.collider == levels[1][1].HitCollider,
                    "LOD model replacement takes native registration priority over an older equal hit");
                parts[0].gameObject.SetActive(false); parts[0].gameObject.SetActive(true);
                lods.Show(4);
                Check(combat.FirstHit(gun.transform.position, Vector3.forward, 20, out hit) && hit.collider == parts[0].HitCollider,
                    "Keeping the same LOD does not spuriously re-register its collision models");
                levels[1][1].transform.SetParent(root.transform, true); lods.Show(1);
                parts[0].gameObject.SetActive(false); parts[0].gameObject.SetActive(true); lods.Show(4);
                Check(combat.FirstHit(gun.transform.position, Vector3.forward, 20, out hit) && hit.collider == levels[1][0].HitCollider,
                    "Former owner LOD changes do not re-register a detached part");
            }
            finally { DestroyImmediate(root); }
        }

        // FUN_100047ff / Object_SegmentTest type 6 with actual archived Atlas arm bounds.
        // A small triangle deliberately misses where the original model sphere must hit.
        void CheckPartSpheres()
        {
            var root = new GameObject("Part sphere fixture"); Mesh mesh = null;
            try
            {
                var self = new GameObject("Part-sphere shooter"); self.transform.SetParent(root.transform);
                var combat = self.AddComponent<Mw2MechCombat>();
                var target = new GameObject("Part-sphere target"); target.transform.SetParent(root.transform);
                target.transform.position = new Vector3(100000, 1000, 100000);
                target.AddComponent<Mw2MechCombat>();
                using var prj = new PrjArchive(Path.Combine(Plan.gameFolder, "MW2.PRJ"));
                var bounds = PolyModel.SphereBounds(PolyModel.Decode(prj.Read("POLY", "AT1_RARM"))[0]);
                var partObject = new GameObject("Atlas arm bounds / tiny triangle"); partObject.transform.SetParent(target.transform, false);
                mesh = new Mesh { vertices = new[] { bounds.center + new Vector3(-0.05f, -0.05f, 0),
                    bounds.center + new Vector3(0.05f, -0.05f, 0), bounds.center + new Vector3(0, 0.05f, 0) }, triangles = new[] { 0, 1, 2 } };
                var collider = partObject.AddComponent<MeshCollider>(); collider.sharedMesh = mesh;
                var part = partObject.AddComponent<Mw2HitPart>(); part.SphereReady = true; part.HitCollider = collider;
                part.LocalCentre = bounds.center; part.ModelRadius = bounds.radius;
                var center = part.transform.TransformPoint(bounds.center);
                var from = center + new Vector3(bounds.radius * 0.9f, 0, -bounds.radius * 5);
                Physics.SyncTransforms();
                Check(!collider.Raycast(new Ray(from, Vector3.forward), out _, bounds.radius * 10) &&
                    combat.FirstHit(from, Vector3.forward, bounds.radius * 10, out var hit) && hit.collider == collider &&
                    Mathf.Abs(hit.point.z - (center.z - bounds.radius)) < 0.02f,
                    "Part hit uses native conservative sphere distance where the triangle misses (real Atlas arm bounds)");
                target.transform.position += Vector3.right * 20;
                center = part.transform.TransformPoint(bounds.center);
                from = center + new Vector3(bounds.radius * 0.9f, 0, -bounds.radius * 5);
                Check(combat.FirstHit(from, Vector3.forward, bounds.radius * 10, out hit) && hit.collider == collider,
                    "Part-sphere query follows the current transformed pose before a physics tick");
                collider.enabled = false;
                Check(!combat.FirstHit(from, Vector3.forward, bounds.radius * 10, out _), "Disabled part collider is excluded from the native sphere adapter");
                collider.enabled = true;
                Check(combat.FirstHit(center, Vector3.forward, 0.01f, out hit) && hit.collider == collider &&
                    Mathf.Abs(hit.distance - 0.1f) < 0.00001f,
                    "Inside-part sphere retains native ten-unit clipping even for a shorter segment");
            }
            finally { DestroyImmediate(root); if (mesh != null) DestroyImmediate(mesh); }
        }

        // 1006c554 calls 1006dc4a and 1006dd6d before spawning the shot.
        void CheckAiShotConvergence()
        {
            var root = new GameObject("Native AI shot convergence fixture");
            var combat = root.AddComponent<Mw2MechCombat>();
            GameObject victimGo = null;
            try
            {
                root.transform.position = new Vector3(100000, 1000, 100000);
                var eye = new GameObject("EYEO"); eye.transform.SetParent(root.transform, false);
                var muzzle = new GameObject("POFO"); muzzle.transform.SetParent(root.transform, false); muzzle.transform.localPosition = new Vector3(1, -1, 0);
                root.AddComponent<Mw2Cockpit>().Eye = eye.transform;
                root.AddComponent<Mw2WeaponMounts>().Sections[0] = muzzle.transform;
                root.AddComponent<Mw2TorsoTwist>();
                victimGo = new GameObject("Off-axis pilot target"); victimGo.transform.SetParent(root.transform, false); victimGo.transform.localPosition = Vector3.right * 100;
                combat.Target = victimGo.AddComponent<Mw2MechCombat>();
                var def = WeaponTable.Get(Plan.gameFolder)[53];
                var weapon = new Mw2MechCombat.MountedWeapon { Def = def, Section = 0, Ammo = -1 };
                combat.Weapons.Add(weapon);
                var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                typeof(Mw2MechCombat).GetField("_now", flags).SetValue(combat, 1);
                typeof(Mw2MechCombat).GetMethod("FireShot", flags).Invoke(combat, new object[] { weapon });
                var shot = Mw2Projectile.Active.Last(p => p.Owner == combat);
                var expected = new Vector3(-0.001998002f, 0.001998002f, 0.999001f) * def.Speed;
                Check(Vector3.Distance(shot.Velocity, expected) < 0.0001f,
                    "Actual AI projectile uses native EYEO-to-convergence from POFO and approximate normalization despite an off-axis pilot target");
                victimGo.transform.SetParent(null, true); victimGo.transform.position = root.transform.position + Vector3.forward * 10;
                victimGo.AddComponent<BoxCollider>();
                typeof(Mw2MechCombat).GetField("_now", flags).SetValue(combat, 2);
                typeof(Mw2MechCombat).GetMethod("FireShot", flags).Invoke(combat, new object[] { weapon });
                shot = Mw2Projectile.Active.Last(p => p.Owner == combat);
                expected = new Vector3(-0.00205267151f, 0.00205267151f, 0.998973668f) * def.Speed;
                Check(Vector3.Distance(shot.Velocity, expected) < 0.0001f,
                    "Actual AI shot applies native twenty-metre target-hit clamp and integer convergence smoothing");
            }
            finally
            {
                foreach (var shot in Mw2Projectile.Active.ToArray()) if (shot != null && shot.Owner == combat) DestroyImmediate(shot.gameObject);
                if (victimGo != null) DestroyImmediate(victimGo);
                DestroyImmediate(root);
            }
        }

        // Original player 1006c554 consumes GP+a0 without updating either approach block.
        void CheckPlayerShotConvergence(Mw2MissionDirector director)
        {
            const string nativeRows = @"0,0,0,2000,1,2000,7000,0,-0.342020154,0.9396926,-4.29185534,-25.0661659,80.6605,88
0,0,0,12345,1,12345,7000,0,-0.342020154,0.9396926,-0.6953188,-28.6627026,80.66049,88
0,0,0,50000,1,50000,7000,0,-0.342020154,0.9396926,-0.171674222,-29.1863441,80.66049,88
0,0,1,2000,1,2000,7000,0,-0.342020154,0.9396926,-4.29185534,-25.0661659,80.6605,88
0,0,1,12345,1,12345,7000,0,-0.342020154,0.9396926,-0.6953188,-28.6627026,80.66049,88
0,0,1,50000,1,50000,7000,0,-0.342020154,0.9396926,-0.171674222,-29.1863441,80.66049,88
0,20,0,2000,1,2000,7000,0,-0.342020154,0.9396926,-4.29185534,-25.0661659,80.6605,88
0,20,0,12345,1,12345,7000,0,-0.342020154,0.9396926,-0.6953188,-28.6627026,80.66049,88
0,20,0,50000,1,50000,7000,0,-0.342020154,0.9396926,-0.171674222,-29.1863441,80.66049,88
0,20,1,2000,1,2000,7000,0,0,1,-4.29268265,4.29268265,85.85366,88
0,20,1,12345,1,12345,7000,0,0,1,-0.7099637,0.7099637,87.64502,88
0,20,1,50000,1,50000,7000,0,0,1,-0.175824165,0.175824165,87.9120941,88
45,0,0,2000,1,2000,7000,0.664463043,-0.342020154,0.664463043,60.682,-28.8387814,65.6198044,88
45,0,0,12345,1,12345,7000,0.664463043,-0.342020154,0.664463043,63.33089,-32.21913,64.11249,88
45,0,0,50000,1,50000,7000,0.664463043,-0.342020154,0.664463043,63.70646,-32.6983948,63.89879,88
45,0,1,2000,1,2000,7000,0.664463043,-0.342020154,0.664463043,60.682,-28.8387814,65.6198044,88
45,0,1,12345,1,12345,7000,0.664463043,-0.342020154,0.664463043,63.33089,-32.21913,64.11249,88
45,0,1,50000,1,50000,7000,0.664463043,-0.342020154,0.664463043,63.70646,-32.6983948,63.89879,88
45,20,0,2000,1,2000,7000,0.664463043,-0.342020154,0.664463043,60.682,-28.8387814,65.6198044,88
45,20,0,12345,1,12345,7000,0.664463043,-0.342020154,0.664463043,63.33089,-32.21913,64.11249,88
45,20,0,50000,1,50000,7000,0.664463043,-0.342020154,0.664463043,63.70646,-32.6983948,63.89879,88
45,20,1,2000,1,2000,7000,0.707106769,-1.66447563E-07,0.707106769,65.4219742,4.978014,70.4,88
45,20,1,12345,1,12345,7000,0.707106769,-1.66447563E-07,0.707106769,69.59352,0.806468844,70.4,88
45,20,1,50000,1,50000,7000,0.707106769,-1.66447563E-07,0.707106769,70.20089,0.199104756,70.4,88
90,0,0,2000,1,2000,7000,0.9396926,-0.342020154,5.753768E-17,81.32663,-26.6935043,0,88
90,0,0,12345,1,12345,7000,0.9396926,-0.342020154,5.753768E-17,80.76285,-28.94862,0,88
90,0,0,50000,1,50000,7000,0.9396926,-0.342020154,5.753768E-17,80.68557,-29.2576923,0,88
90,0,1,2000,1,2000,7000,0.9396926,-0.342020154,5.753768E-17,81.32663,-26.6935043,0,88
90,0,1,12345,1,12345,7000,0.9396926,-0.342020154,5.753768E-17,80.76285,-28.94862,0,88
90,0,1,50000,1,50000,7000,0.9396926,-0.342020154,5.753768E-17,80.68557,-29.2576923,0,88
90,20,0,2000,1,2000,7000,0.9396926,-0.342020154,5.753768E-17,81.32663,-26.6935043,0,88
90,20,0,12345,1,12345,7000,0.9396926,-0.342020154,5.753768E-17,80.76285,-28.94862,0,88
90,20,0,50000,1,50000,7000,0.9396926,-0.342020154,5.753768E-17,80.68557,-29.2576923,0,88
90,20,1,2000,1,2000,7000,1,0,6.123032E-17,86.85714,4.571429,0,88
90,20,1,12345,1,12345,7000,1,0,6.123032E-17,87.8207,0.7171964,0,88
90,20,1,50000,1,50000,7000,1,0,6.123032E-17,87.95593,0.1762644,0,88";
            var previous = FindObjectsByType<Collider>(FindObjectsSortMode.None).Where(c => c.enabled).ToArray();
            var root = new GameObject("Native player convergence fixture");
            var gun = new GameObject("Player gun"); gun.transform.SetParent(root.transform);
            var combat = gun.AddComponent<Mw2MechCombat>(); combat.IsPlayer = true;
            var previousPlayer = director.Player.GetComponent<Mw2MechCombat>();
            bool oldPlayer = previousPlayer.IsPlayer;
            try
            {
                previousPlayer.IsPlayer = false;
                foreach (var c in previous) c.enabled = false;
                var eye = new GameObject("EYEO"); eye.transform.SetParent(gun.transform, false); eye.transform.localPosition = new Vector3(10, 20, -30);
                var muzzle = new GameObject("POFO"); muzzle.transform.SetParent(gun.transform, false); muzzle.transform.localPosition = new Vector3(11, 19, -30);
                var cockpit = gun.AddComponent<Mw2Cockpit>(); cockpit.Eye = eye.transform; cockpit.EyeVerticalOffset = 0;
                gun.AddComponent<Mw2WeaponMounts>().Sections[0] = muzzle.transform;
                var twist = gun.AddComponent<Mw2TorsoTwist>(); twist.Pitch = -20;
                var controller = gun.AddComponent<Mw2MechController>(); controller.KeyboardControl = false; controller.FollowCamera = false;
                controller.Collisions = false; controller.WalkingMp = 0; controller.PlanetGravity = 0;
                var blocker = new GameObject("Changed reticle obstruction"); blocker.transform.SetParent(root.transform);
                blocker.transform.position = eye.transform.position + Vector3.forward * 10;
                var blockerCollider = blocker.AddComponent<BoxCollider>();
                var victim = blocker.AddComponent<Mw2MechCombat>(); combat.Target = victim;
                var def = WeaponTable.Get(Plan.gameFolder)[53];
                var weapon = new Mw2MechCombat.MountedWeapon { Def = def, Section = 0, Ammo = -1 }; combat.Weapons.Add(weapon);
                var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                var cache = (AimConvergence)typeof(Mw2MechCombat).GetField("_aimConvergence", flags).GetValue(combat);
                var fall = (MechFall)typeof(Mw2MechController).GetField("_fall", flags).GetValue(controller);
                typeof(Mw2MechCombat).GetField("_now", flags).SetValue(combat, 2000);
                int checkedRows = 0;
                foreach (string row in nativeRows.Split('\n'))
                {
                    var v = row.Trim().Split(',').Select(s => float.Parse(s, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                    eye.transform.localRotation = Quaternion.Euler(v[1], v[0], 0); fall.Falling = v[2] != 0;
                    typeof(AimConvergence).GetProperty("Distance").SetValue(cache, (int)v[3]);
                    typeof(AimConvergence).GetProperty("LongDistance").SetValue(cache, 7000);
                    typeof(Mw2MechCombat).GetMethod("FireShot", flags).Invoke(combat, new object[] { weapon });
                    var shot = Mw2Projectile.Active.Last(p => p.Owner == combat);
                    var expected = new Vector3(v[10], v[11], v[12]) * (def.Speed / v[13]);
                    if (Vector3.Distance(shot.Velocity, expected) > 0.0002f || cache.Distance != (int)v[5] || cache.LongDistance != (int)v[6])
                        throw new Exception("Original player fire adapter mismatch: " + row + "; got " + shot.Velocity + "; cache " + cache.Distance + "/" + cache.LongDistance);
                    DestroyImmediate(shot.gameObject); checkedRows++;
                }
                Check(checkedRows == 36, "All 36 native player shots retain cached convergence and match actual EYEO/POFO emission despite changed scene geometry");
                fall.Falling = false; eye.transform.localRotation = Quaternion.identity; twist.Pitch = 0;
                typeof(AimConvergence).GetProperty("Distance").SetValue(cache, 50000);
                typeof(AimConvergence).GetProperty("LongDistance").SetValue(cache, 50000);
                typeof(Mw2MechController).GetMethod("Start", flags).Invoke(controller, null);
                typeof(Mw2MechController).GetField("_clock", flags).SetValue(controller, 36.0);
                typeof(Mw2MechController).GetMethod("Update", flags).Invoke(controller, null);
                Check(cache.Distance == 2000 && cache.LongDistance == 2000,
                    "Actual player physics updates the integer convergence cache without firing, including the native close-hit clamp");
                typeof(AimConvergence).GetProperty("Distance").SetValue(cache, 50000);
                typeof(AimConvergence).GetProperty("LongDistance").SetValue(cache, 50000);
                eye.transform.localRotation = Quaternion.Euler(0, 90, 0);
                blocker.transform.position = eye.transform.position + Vector3.right * 10;
                typeof(Mw2MechCombat).GetField("_clock", flags).SetValue(combat, 100.0);
                combat.TargetAtReticle();
                Check(cache.Distance == 2000 && cache.LongDistance == 2000,
                    "Target-at-reticle refreshes the same integer cache along EYEO rather than the body/twist forward ray");
                combat.IsPlayer = false;
                blockerCollider.enabled = false;
                combat.UpdatePlayerConvergence(5000);
                Check(cache.Distance == 2000 && cache.LongDistance == 2000,
                    "Non-player physics does not update the player's convergence path");
            }
            finally
            {
                previousPlayer.IsPlayer = oldPlayer;
                foreach (var shot in Mw2Projectile.Active.ToArray()) if (shot != null && shot.Owner == combat) DestroyImmediate(shot.gameObject);
                DestroyImmediate(root);
                foreach (var c in previous) if (c != null) c.enabled = true;
            }
        }

        // 1006c554 / 10010f40: allocation, deferred scene segment and retained history on reuse.
        void CheckProjectileSegments()
        {
            var root = new GameObject("Native projectile segment fixture");
            var owner = root.AddComponent<Mw2MechCombat>();
            var wall = new GameObject("Projectile segment wall");
            Mw2Projectile shot = null;
            var reserved = new System.Collections.Generic.List<int>();
            try
            {
                root.transform.position = new Vector3(200000, 2000, 200000);
                var def = WeaponTable.Get(Plan.gameFolder)[53];
                var models = Mw2ShotModels.Instance;
                int slot = models.Take(def.ProjectileType);
                Check(slot >= 0 && models.Slots.Count == 207, "Mission imports all 207 ordered BTHG projectile slots");
                Mw2Projectile.Spawn(owner, def, root.transform.position, Vector3.forward, 100, null, models, slot);
                shot = Mw2Projectile.Active.Last(p => p.Owner == owner);
                var start = shot.transform.position;
                var next = start + Vector3.forward * (def.Speed * 3 * PolyModel.UnitsToMeters);
                models.Pool.Advance(slot, new AiLeadPoint.Vector(start.x / PolyModel.UnitsToMeters,
                    start.y / PolyModel.UnitsToMeters, start.z / PolyModel.UnitsToMeters));
                wall.transform.position = (start + next) * 0.5f;
                wall.AddComponent<BoxCollider>();
                var structure = wall.AddComponent<Mw2Destructible>(); structure.Strength = 100;
                Physics.SyncTransforms();
                var move = typeof(Mw2Projectile).GetMethod("Move", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                move.Invoke(shot, new object[] { 3 });
                Check(Mw2Projectile.Active.Contains(shot) && structure.Strength == 100 && Vector3.Distance(shot.transform.position, next) < 0.02f,
                    "Projectile moves through the future scene segment without testing it early");
                move.Invoke(shot, new object[] { 3 });
                Check(!Mw2Projectile.Active.Contains(shot) && structure.Strength == 100 - (int)def.Damage,
                    "Next projectile move tests the retained segment and damages its building");
                DestroyImmediate(shot.gameObject); shot = null;
                int reused = models.Take(def.ProjectileType);
                var retained = models.Pool.Previous(reused);
                Check(reused == slot && Mathf.Abs(retained.Z * PolyModel.UnitsToMeters - next.z) < 0.02f,
                    "Retired projectile releases the first slot immediately and retains its last node position");
                Mw2Projectile.Spawn(owner, def, next + Vector3.forward * 20, Vector3.forward, 100, null, models, reused);
                shot = Mw2Projectile.Active.Last(p => p.Owner == owner);
                wall.transform.position = (next + shot.transform.position) * 0.5f;
                structure.Strength = 100;
                Physics.SyncTransforms();
                move.Invoke(shot, new object[] { 3 });
                Check(!Mw2Projectile.Active.Contains(shot) && structure.Strength == 100 - (int)def.Damage,
                    "Reused projectile checks retained old XYZ to its new muzzle node on its first move");
                int free;
                while ((free = models.Take(def.ProjectileType)) >= 0) reserved.Add(free);
                int active = Mw2Projectile.Active.Count;
                float heat = owner.Heat;
                var weapon = new Mw2MechCombat.MountedWeapon { Def = def, Section = 0, Ammo = -1 };
                owner.Weapons.Add(weapon);
                var fire = typeof(Mw2MechCombat).GetMethod("FireShot", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                bool more = (bool)fire.Invoke(owner, new object[] { weapon });
                Check(more && Mw2Projectile.Active.Count == active && Mathf.Abs(owner.Heat - heat - def.Heat) < 0.001f,
                    "Exhausted projectile slots retain the energy fire hook result and heat without emitting a projectile");
            }
            finally
            {
                foreach (int index in reserved) Mw2ShotModels.Instance.Pool.Release(index);
                if (shot != null) DestroyImmediate(shot.gameObject);
                foreach (var flying in Mw2Projectile.Active.ToArray()) if (flying != null && flying.Owner == owner) DestroyImmediate(flying.gameObject);
                DestroyImmediate(wall); DestroyImmediate(root);
            }
        }

        // Ai_LockOnPlayer 10005c42 and Weapon_UpdateLock 1006d794.
        // These numeric rows were emitted by the unchanged DLL, not by this adapter.
        void CheckNativeAiLock()
        {
            var root = new GameObject("Native AI lock fixture");
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            var originalStyle = AiTables.Styles[0];
            try
            {
                var combat = root.AddComponent<Mw2MechCombat>();
                var ai = root.AddComponent<Mw2MechAI>(); ai.enabled = false; ai.PilotStyleId = 0;
                root.GetComponent<Mw2MechController>().enabled = false;
                var update = typeof(Mw2MechCombat).GetMethod("UpdateLock", flags);
                string[] nativeRows = {
                    "0,0,0,0,0,0",
                    "0,0,0,2,0,0",
                    "0,0,1,0,1,0",
                    "0,0,1,2,1,0",
                    "0,14,0,0,0,0",
                    "0,14,0,2,0,0",
                    "0,14,1,0,1,0",
                    "0,14,1,2,1,0",
                    "0,15,0,0,0,0",
                    "0,15,0,2,0,0",
                    "0,15,1,0,1,0",
                    "0,15,1,2,1,0",
                    "0,24,0,0,0,0",
                    "0,24,0,2,0,0",
                    "0,24,1,0,1,0",
                    "0,24,1,2,1,0",
                    "0,25,0,0,0,0",
                    "0,25,0,2,0,0",
                    "0,25,1,0,1,0",
                    "0,25,1,2,1,0",
                    "0,59,0,0,0,0",
                    "0,59,0,2,0,0",
                    "0,59,1,0,1,0",
                    "0,59,1,2,1,0",
                    "0,60,0,0,0,0",
                    "0,60,0,2,0,0",
                    "0,60,1,0,1,0",
                    "0,60,1,2,1,0",
                    "0,84,0,0,0,0",
                    "0,84,0,2,0,0",
                    "0,84,1,0,1,0",
                    "0,84,1,2,1,0",
                    "0,85,0,0,0,0",
                    "0,85,0,2,0,0",
                    "0,85,1,0,1,0",
                    "0,85,1,2,1,0",
                    "0,99,0,0,0,0",
                    "0,99,0,2,0,0",
                    "0,99,1,0,1,0",
                    "0,99,1,2,1,0",
                    "15,0,0,0,1,0",
                    "15,0,0,2,1,1",
                    "15,0,1,0,1,0",
                    "15,0,1,2,1,1",
                    "15,14,0,0,1,0",
                    "15,14,0,2,1,1",
                    "15,14,1,0,1,0",
                    "15,14,1,2,1,1",
                    "15,15,0,0,0,0",
                    "15,15,0,2,0,0",
                    "15,15,1,0,1,0",
                    "15,15,1,2,1,1",
                    "15,24,0,0,0,0",
                    "15,24,0,2,0,0",
                    "15,24,1,0,1,0",
                    "15,24,1,2,1,1",
                    "15,25,0,0,0,0",
                    "15,25,0,2,0,0",
                    "15,25,1,0,1,0",
                    "15,25,1,2,1,1",
                    "15,59,0,0,0,0",
                    "15,59,0,2,0,0",
                    "15,59,1,0,1,0",
                    "15,59,1,2,1,1",
                    "15,60,0,0,0,0",
                    "15,60,0,2,0,0",
                    "15,60,1,0,1,0",
                    "15,60,1,2,1,1",
                    "15,84,0,0,0,0",
                    "15,84,0,2,0,0",
                    "15,84,1,0,1,0",
                    "15,84,1,2,1,1",
                    "15,85,0,0,0,0",
                    "15,85,0,2,0,0",
                    "15,85,1,0,1,0",
                    "15,85,1,2,1,1",
                    "15,99,0,0,0,0",
                    "15,99,0,2,0,0",
                    "15,99,1,0,1,0",
                    "15,99,1,2,1,1",
                    "25,0,0,0,1,0",
                    "25,0,0,2,1,1",
                    "25,0,1,0,1,0",
                    "25,0,1,2,1,1",
                    "25,14,0,0,1,0",
                    "25,14,0,2,1,1",
                    "25,14,1,0,1,0",
                    "25,14,1,2,1,1",
                    "25,15,0,0,1,0",
                    "25,15,0,2,1,1",
                    "25,15,1,0,1,0",
                    "25,15,1,2,1,1",
                    "25,24,0,0,1,0",
                    "25,24,0,2,1,1",
                    "25,24,1,0,1,0",
                    "25,24,1,2,1,1",
                    "25,25,0,0,0,0",
                    "25,25,0,2,0,0",
                    "25,25,1,0,1,0",
                    "25,25,1,2,1,1",
                    "25,59,0,0,0,0",
                    "25,59,0,2,0,0",
                    "25,59,1,0,1,0",
                    "25,59,1,2,1,1",
                    "25,60,0,0,0,0",
                    "25,60,0,2,0,0",
                    "25,60,1,0,1,0",
                    "25,60,1,2,1,1",
                    "25,84,0,0,0,0",
                    "25,84,0,2,0,0",
                    "25,84,1,0,1,0",
                    "25,84,1,2,1,1",
                    "25,85,0,0,0,0",
                    "25,85,0,2,0,0",
                    "25,85,1,0,1,0",
                    "25,85,1,2,1,1",
                    "25,99,0,0,0,0",
                    "25,99,0,2,0,0",
                    "25,99,1,0,1,0",
                    "25,99,1,2,1,1",
                    "60,0,0,0,1,0",
                    "60,0,0,2,1,1",
                    "60,0,1,0,1,0",
                    "60,0,1,2,1,1",
                    "60,14,0,0,1,0",
                    "60,14,0,2,1,1",
                    "60,14,1,0,1,0",
                    "60,14,1,2,1,1",
                    "60,15,0,0,1,0",
                    "60,15,0,2,1,1",
                    "60,15,1,0,1,0",
                    "60,15,1,2,1,1",
                    "60,24,0,0,1,0",
                    "60,24,0,2,1,1",
                    "60,24,1,0,1,0",
                    "60,24,1,2,1,1",
                    "60,25,0,0,1,0",
                    "60,25,0,2,1,1",
                    "60,25,1,0,1,0",
                    "60,25,1,2,1,1",
                    "60,59,0,0,1,0",
                    "60,59,0,2,1,1",
                    "60,59,1,0,1,0",
                    "60,59,1,2,1,1",
                    "60,60,0,0,0,0",
                    "60,60,0,2,0,0",
                    "60,60,1,0,1,0",
                    "60,60,1,2,1,1",
                    "60,84,0,0,0,0",
                    "60,84,0,2,0,0",
                    "60,84,1,0,1,0",
                    "60,84,1,2,1,1",
                    "60,85,0,0,0,0",
                    "60,85,0,2,0,0",
                    "60,85,1,0,1,0",
                    "60,85,1,2,1,1",
                    "60,99,0,0,0,0",
                    "60,99,0,2,0,0",
                    "60,99,1,0,1,0",
                    "60,99,1,2,1,1",
                    "85,0,0,0,1,0",
                    "85,0,0,2,1,1",
                    "85,0,1,0,1,0",
                    "85,0,1,2,1,1",
                    "85,14,0,0,1,0",
                    "85,14,0,2,1,1",
                    "85,14,1,0,1,0",
                    "85,14,1,2,1,1",
                    "85,15,0,0,1,0",
                    "85,15,0,2,1,1",
                    "85,15,1,0,1,0",
                    "85,15,1,2,1,1",
                    "85,24,0,0,1,0",
                    "85,24,0,2,1,1",
                    "85,24,1,0,1,0",
                    "85,24,1,2,1,1",
                    "85,25,0,0,1,0",
                    "85,25,0,2,1,1",
                    "85,25,1,0,1,0",
                    "85,25,1,2,1,1",
                    "85,59,0,0,1,0",
                    "85,59,0,2,1,1",
                    "85,59,1,0,1,0",
                    "85,59,1,2,1,1",
                    "85,60,0,0,1,0",
                    "85,60,0,2,1,1",
                    "85,60,1,0,1,0",
                    "85,60,1,2,1,1",
                    "85,84,0,0,1,0",
                    "85,84,0,2,1,1",
                    "85,84,1,0,1,0",
                    "85,84,1,2,1,1",
                    "85,85,0,0,0,0",
                    "85,85,0,2,0,0",
                    "85,85,1,0,1,0",
                    "85,85,1,2,1,1",
                    "85,99,0,0,0,0",
                    "85,99,0,2,0,0",
                    "85,99,1,0,1,0",
                    "85,99,1,2,1,1",
                    "100,0,0,0,1,0",
                    "100,0,0,2,1,1",
                    "100,0,1,0,1,0",
                    "100,0,1,2,1,1",
                    "100,14,0,0,1,0",
                    "100,14,0,2,1,1",
                    "100,14,1,0,1,0",
                    "100,14,1,2,1,1",
                    "100,15,0,0,1,0",
                    "100,15,0,2,1,1",
                    "100,15,1,0,1,0",
                    "100,15,1,2,1,1",
                    "100,24,0,0,1,0",
                    "100,24,0,2,1,1",
                    "100,24,1,0,1,0",
                    "100,24,1,2,1,1",
                    "100,25,0,0,1,0",
                    "100,25,0,2,1,1",
                    "100,25,1,0,1,0",
                    "100,25,1,2,1,1",
                    "100,59,0,0,1,0",
                    "100,59,0,2,1,1",
                    "100,59,1,0,1,0",
                    "100,59,1,2,1,1",
                    "100,60,0,0,1,0",
                    "100,60,0,2,1,1",
                    "100,60,1,0,1,0",
                    "100,60,1,2,1,1",
                    "100,84,0,0,1,0",
                    "100,84,0,2,1,1",
                    "100,84,1,0,1,0",
                    "100,84,1,2,1,1",
                    "100,85,0,0,1,0",
                    "100,85,0,2,1,1",
                    "100,85,1,0,1,0",
                    "100,85,1,2,1,1",
                    "100,99,0,0,1,0",
                    "100,99,0,2,1,1",
                    "100,99,1,0,1,0",
                    "100,99,1,2,1,1",
                };
                int checkedRows = 0;
                foreach (string row in nativeRows)
                {
                    int[] v = row.Split(',').Select(int.Parse).ToArray();
                    AiTables.Styles[0] = new PilotStyle(0, 0, 75, v[0], 0, 0);
                    combat.IsPlayer = v[3] != 2; combat.Locked = v[2] != 0;
                    combat.TryAiLock(0, v[1]);
                    if (combat.Locked != (v[4] != 0)) throw new Exception("Native AI lock grant mismatch: " + row);
                    update.Invoke(combat, new object[] { 1 });
                    if (combat.Locked != (v[5] != 0)) throw new Exception("Native AI lock retention mismatch: " + row);
                    checkedRows++;
                }
                Check(checkedRows == 240, "All 240 native AI lock grants/retentions match probability boundaries and pilot type");
                AiTables.Styles[0] = originalStyle;
                var target = new GameObject("AI missile lock target"); target.transform.SetParent(root.transform);
                var victim = target.AddComponent<Mw2MechCombat>(); combat.Target = victim;
                var def = WeaponTable.Get(Plan.gameFolder).First(w => w.LockOn);
                combat.Weapons.Add(new Mw2MechCombat.MountedWeapon { Def = def, Ammo = -1 });
                combat.IsPlayer = false; combat.Locked = false;
                combat.TryAiLock(5, 99);
                typeof(Mw2MechCombat).GetMethod("StartBurst", flags).Invoke(combat, new object[] { 0 });
                Check(combat.Weapons[0].LockTarget == victim, "AI-granted lock reaches the actual missile burst without a player cone timer");
            }
            finally { AiTables.Styles[0] = originalStyle; DestroyImmediate(root); }
        }

        // AI_ChooseSingle 10005864 / AI_ChooseGroup 10005a23: original DLL rows,
        // real type-53 PPC data, controlled ready/cooling/broken slots and scene validation.
        void CheckNativeWeaponCycle()
        {
            var root = new GameObject("Native weapon-cycle fixture");
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            var randomState = UnityEngine.Random.state;
            try
            {
                var combat = root.AddComponent<Mw2MechCombat>();
                var ai = root.AddComponent<Mw2MechAI>(); ai.enabled = false;
                root.GetComponent<Mw2MechController>().enabled = false;
                typeof(Mw2MechAI).GetField("_combat", flags).SetValue(ai, combat);
                var def = WeaponTable.Get(Plan.gameFolder)[53];
                var style = new PilotStyle(0, 0, 1000, 0, 0, 0);
                var valid = typeof(Mw2MechAI).GetField("_sceneTargetValid", flags);
                int seed = 0, divisor = def.RecycleTicks / 0x5a + 1;
                for (; seed < 10000; seed++)
                {
                    UnityEngine.Random.InitState(seed);
                    if (UnityEngine.Random.Range(0, divisor) == 0) break;
                }
                string[] nativeRows = {
                    "0,-1,0,0,513,1,1,0,0,0",
                    "0,-1,0,0,4609,1,1,0,0,0",
                    "0,-1,0,0,-1,1,1,0,0,0",
                    "0,-1,0,1,513,1,1,0,0,1",
                    "0,-1,0,1,4609,1,1,0,0,1",
                    "0,-1,0,1,-1,1,1,0,0,1",
                    "0,-1,0,3,513,1,1,0,0,3",
                    "0,-1,0,3,4609,1,1,0,0,3",
                    "0,-1,0,3,-1,1,1,0,0,3",
                    "0,-1,2,0,513,1,1,0,2,0",
                    "0,-1,2,0,4609,1,1,0,2,0",
                    "0,-1,2,0,-1,1,1,0,2,0",
                    "0,-1,2,1,513,1,1,0,2,1",
                    "0,-1,2,1,4609,1,1,0,2,1",
                    "0,-1,2,1,-1,1,1,0,2,1",
                    "0,-1,2,3,513,1,1,0,2,3",
                    "0,-1,2,3,4609,1,1,0,2,3",
                    "0,-1,2,3,-1,1,1,0,2,3",
                    "0,1,0,0,513,0,1,0,0,0",
                    "0,1,0,0,4609,0,1,0,0,0",
                    "0,1,0,0,-1,0,1,0,0,0",
                    "0,1,0,1,513,0,1,0,0,1",
                    "0,1,0,1,4609,0,1,0,0,1",
                    "0,1,0,1,-1,0,1,0,0,1",
                    "0,1,0,3,513,0,1,0,0,3",
                    "0,1,0,3,4609,0,1,0,0,3",
                    "0,1,0,3,-1,0,1,0,0,3",
                    "0,1,2,0,513,0,1,0,2,0",
                    "0,1,2,0,4609,0,1,0,2,0",
                    "0,1,2,0,-1,0,1,0,2,0",
                    "0,1,2,1,513,0,1,0,2,1",
                    "0,1,2,1,4609,0,1,0,2,1",
                    "0,1,2,1,-1,0,1,0,2,1",
                    "0,1,2,3,513,0,1,0,2,3",
                    "0,1,2,3,4609,0,1,0,2,3",
                    "0,1,2,3,-1,0,1,0,2,3",
                    "0,3,0,0,513,1,1,0,0,0",
                    "0,3,0,0,4609,1,1,0,0,0",
                    "0,3,0,0,-1,1,1,0,0,0",
                    "0,3,0,1,513,1,1,0,0,1",
                    "0,3,0,1,4609,1,1,0,0,1",
                    "0,3,0,1,-1,1,1,0,0,1",
                    "0,3,0,3,513,1,1,0,0,3",
                    "0,3,0,3,4609,1,1,0,0,3",
                    "0,3,0,3,-1,1,1,0,0,3",
                    "0,3,2,0,513,1,1,0,2,0",
                    "0,3,2,0,4609,1,1,0,2,0",
                    "0,3,2,0,-1,1,1,0,2,0",
                    "0,3,2,1,513,1,1,0,2,1",
                    "0,3,2,1,4609,1,1,0,2,1",
                    "0,3,2,1,-1,1,1,0,2,1",
                    "0,3,2,3,513,1,1,0,2,3",
                    "0,3,2,3,4609,1,1,0,2,3",
                    "0,3,2,3,-1,1,1,0,2,3",
                    "1,-1,0,0,513,1,1,0,0,1",
                    "1,-1,0,0,4609,1,0,0,0,0",
                    "1,-1,0,0,-1,1,0,0,0,0",
                    "1,-1,0,1,513,1,1,0,0,1",
                    "1,-1,0,1,4609,1,0,0,0,0",
                    "1,-1,0,1,-1,1,0,0,0,0",
                    "1,-1,0,3,513,1,1,0,0,1",
                    "1,-1,0,3,4609,1,0,0,0,0",
                    "1,-1,0,3,-1,1,0,0,0,0",
                    "1,-1,2,0,513,1,1,0,0,1",
                    "1,-1,2,0,4609,1,0,0,0,0",
                    "1,-1,2,0,-1,1,0,0,0,0",
                    "1,-1,2,1,513,1,1,0,0,1",
                    "1,-1,2,1,4609,1,0,0,0,0",
                    "1,-1,2,1,-1,1,0,0,0,0",
                    "1,-1,2,3,513,1,1,0,0,1",
                    "1,-1,2,3,4609,1,0,0,0,0",
                    "1,-1,2,3,-1,1,0,0,0,0",
                    "1,1,0,0,513,0,1,0,1,1",
                    "1,1,0,0,4609,1,0,0,0,0",
                    "1,1,0,0,-1,1,0,0,0,0",
                    "1,1,0,1,513,0,1,0,1,1",
                    "1,1,0,1,4609,1,0,0,0,0",
                    "1,1,0,1,-1,1,0,0,0,0",
                    "1,1,0,3,513,0,1,0,1,1",
                    "1,1,0,3,4609,1,0,0,0,0",
                    "1,1,0,3,-1,1,0,0,0,0",
                    "1,1,2,0,513,0,1,0,1,1",
                    "1,1,2,0,4609,1,0,0,0,0",
                    "1,1,2,0,-1,1,0,0,0,0",
                    "1,1,2,1,513,0,1,0,1,1",
                    "1,1,2,1,4609,1,0,0,0,0",
                    "1,1,2,1,-1,1,0,0,0,0",
                    "1,1,2,3,513,0,1,0,1,1",
                    "1,1,2,3,4609,1,0,0,0,0",
                    "1,1,2,3,-1,1,0,0,0,0",
                    "1,3,0,0,513,0,1,0,1,1",
                    "1,3,0,0,4609,1,0,0,0,0",
                    "1,3,0,0,-1,1,0,0,0,0",
                    "1,3,0,1,513,0,1,0,1,1",
                    "1,3,0,1,4609,1,0,0,0,0",
                    "1,3,0,1,-1,1,0,0,0,0",
                    "1,3,0,3,513,0,1,0,1,1",
                    "1,3,0,3,4609,1,0,0,0,0",
                    "1,3,0,3,-1,1,0,0,0,0",
                    "1,3,2,0,513,0,1,0,1,1",
                    "1,3,2,0,4609,1,0,0,0,0",
                    "1,3,2,0,-1,1,0,0,0,0",
                    "1,3,2,1,513,0,1,0,1,1",
                    "1,3,2,1,4609,1,0,0,0,0",
                    "1,3,2,1,-1,1,0,0,0,0",
                    "1,3,2,3,513,0,1,0,1,1",
                    "1,3,2,3,4609,1,0,0,0,0",
                    "1,3,2,3,-1,1,0,0,0,0",
                };
                int checkedRows = 0;
                foreach (string row in nativeRows)
                {
                    int[] v = row.Split(',').Select(int.Parse).ToArray();
                    combat.Weapons.Clear(); combat.SelectedWeapon = 0; combat.Heat = 0;
                    combat.Weapons.Add(new Mw2MechCombat.MountedWeapon { Def = def, Ammo = -1, State = v[1], Broken = v[1] == -1, Group = v[2] });
                    combat.Weapons.Add(new Mw2MechCombat.MountedWeapon { Def = def, Ammo = -1, State = 1, Group = v[3] });
                    valid.SetValue(ai, v[4] == 0x201);
                    UnityEngine.Random.InitState(seed);
                    object[] args = { 10000f, style, false };
                    var chosen = (System.Collections.Generic.List<int>)typeof(Mw2MechAI).GetMethod(v[0] == 0 ? "SingleFire" : "GroupFire", flags).Invoke(ai, args);
                    if ((chosen != null) != (v[6] != 0) || combat.SelectedWeapon != v[5] ||
                        (bool)args[2] != (v[7] != 0) || combat.Weapons[0].Group != v[8] || combat.Weapons[1].Group != v[9])
                        throw new Exception("Native weapon-cycle mismatch: " + row);
                    checkedRows++;
                }
                Check(checkedRows == 108, "All 108 native single/group weapon choices match selected slots, rewritten groups and scene validity");
            }
            finally { UnityEngine.Random.state = randomState; DestroyImmediate(root); }
        }

        // Native AI_LeadPoint rows for original weapon type 0, scenario 4/5/7.
        // Calls the actual scene adapter on first observation with unequal GP origin heights.
        void CheckNativeLead(Mw2MissionDirector scene)
        {
            var root = new GameObject("Native lead fixture");
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            try
            {
                var gun = new GameObject("Lead gun"); gun.transform.SetParent(root.transform);
                gun.transform.position = new Vector3(12, 27, -7); gun.transform.rotation = Quaternion.Euler(0, 30, 0);
                var combat = gun.AddComponent<Mw2MechCombat>(); combat.OriginHeight = 3;
                combat.Weapons.Add(new Mw2MechCombat.MountedWeapon { Def = WeaponTable.Get(Plan.gameFolder)[0] });
                combat.SelectedWeapon = 0;
                var ai = gun.AddComponent<Mw2MechAI>(); ai.enabled = false;
                var gunController = gun.GetComponent<Mw2MechController>(); gunController.enabled = false;
                typeof(Mw2MechAI).GetField("_controller", flags).SetValue(ai, gunController);
                var target = new GameObject("Lead target"); target.transform.SetParent(root.transform);
                target.transform.position = new Vector3(125, 80, 180);
                var victim = target.AddComponent<Mw2MechCombat>(); victim.OriginHeight = 20;
                var controller = target.AddComponent<Mw2MechController>(); controller.enabled = false;
                var motion = new MechMotion(3, 0) { VelX = 100 };
                typeof(Mw2MechController).GetField("_motion", flags).SetValue(controller, motion);
                typeof(Mw2MechAI).GetField("_combat", flags).SetValue(ai, combat);
                typeof(Mw2MechAI).GetField("_pilotTarget", flags).SetValue(ai, AiTarget.ForUnit(victim));
                typeof(Mw2MechAI).GetField("_dist3", flags).SetValue(ai, 20000f);
                var frame = typeof(Mw2MechAI).GetField("_fireFrameTicks", flags);
                var lead = typeof(Mw2MechAI).GetMethod("LeadBearing", flags);
                frame.SetValue(ai, 1);
                object[] angles = { 17.25f, 321.5f };
                float bearing = (float)lead.Invoke(ai, angles);
                Check(Mathf.Abs(bearing - 1.60125732f) < 0.0001f && Mathf.Abs((float)angles[1] + 17.65936f) < 0.0001f,
                    "Native lead adapter uses GP origins and physics velocity on first observation, including pitch");
                frame.SetValue(ai, 2); angles = new object[] { 17.25f, 321.5f };
                bearing = (float)lead.Invoke(ai, angles);
                Check(bearing == 17.25f && (float)angles[1] == 321.5f, "Native lead time equal to integer frame leaves both angles unchanged");
                frame.SetValue(ai, 1); motion.VelX = 100f * 0.9999995f; angles = new object[] { 17.25f, 321.5f };
                bearing = (float)lead.Invoke(ai, angles);
                Check(bearing == 17.25f && (float)angles[1] == 321.5f, "Near-equal target speed does not take native exact-equality fallback");
                gun.transform.SetPositionAndRotation(new Vector3(100000, 1000, 100000), Quaternion.identity);
                target.transform.position = new Vector3(100100, 1000, 100000);
                var cachedBearing = typeof(Mw2MechAI).GetField("_targetBearing", flags);
                var aimClear = typeof(Mw2MechAI).GetMethod("AimRayClear", flags);
                cachedBearing.SetValue(ai, 90f);
                Check((bool)aimClear.Invoke(ai, new object[] { null, 10000f }), "AI line-of-fire accepts the native exact 90-degree boundary");
                cachedBearing.SetValue(ai, 90.001f);
                Check(!(bool)aimClear.Invoke(ai, new object[] { null, 10000f }), "AI line-of-fire rejects angles beyond the native boundary");
                var eye = new GameObject("Native eye"); eye.transform.SetParent(gun.transform);
                eye.transform.SetPositionAndRotation(new Vector3(10, 20, -30), Quaternion.Euler(-30, 45, 0));
                var cockpit = gun.AddComponent<Mw2Cockpit>(); cockpit.Eye = eye.transform; cockpit.EyeVerticalOffset = -10;
                var twist = gun.AddComponent<Mw2TorsoTwist>(); twist.Pitch = 15;
                var playerCockpit = scene.Player.GetComponent<Mw2Cockpit>();
                float originalOffset = playerCockpit.EyeVerticalOffset;
                try
                {
                    playerCockpit.EyeVerticalOffset = 3;
                    var aimRay = typeof(Mw2MechAI).GetMethod("AimRay", flags);
                    var fall = (MechFall)typeof(Mw2MechController).GetField("_fall", flags).GetValue(gunController);
                    object[] ray = { twist, null, null }; aimRay.Invoke(ai, ray);
                    var expected = new Vector3(0.6830127f, 0.25881904f, 0.6830127f);
                    Check(((Vector3)ray[1] - new Vector3(10, 23, -30)).sqrMagnitude < 0.000001f &&
                        ((Vector3)ray[2] - expected).sqrMagnitude < 0.000001f,
                        "AI aim ray starts at world EYEO plus player MGEO offset and uses eye yaw with weapon pitch");
                    fall.Falling = true; ray = new object[] { twist, null, null }; aimRay.Invoke(ai, ray);
                    Check(((Vector3)ray[2] - new Vector3(expected.x, -expected.y, expected.z)).sqrMagnitude < 0.000001f,
                        "AI aim direction adds native eye pitch while falling (bit 4)");
                    fall.Falling = false; fall.Down = true; ray = new object[] { twist, null, null }; aimRay.Invoke(ai, ray);
                    Check(((Vector3)ray[2] - expected).sqrMagnitude < 0.000001f,
                        "AI aim direction omits eye pitch after landing on the side (bit 8)");
                }
                finally { playerCockpit.EyeVerticalOffset = originalOffset; }
                // Native Look rows: GP selector 0x201 and self selector 0x202, low aim off.
                combat.OriginHeight = 0;
                gun.transform.position = new Vector3(12, 30, -7);
                victim.OriginHeight = 10; target.transform.position = new Vector3(120, 170, 240);
                ai.PilotStyleId = 0;
                var look = typeof(Mw2MechAI).GetMethod("Look", flags);
                var scenePoint = typeof(Mw2MechAI).GetField("_sceneTargetPoint", flags);
                var pilotTarget = typeof(Mw2MechAI).GetField("_pilotTarget", flags).GetValue(ai);
                Check((bool)look.Invoke(ai, new object[] { AiTarget.ForUnit(victim) }) &&
                    (bool)typeof(Mw2MechAI).GetField("_sceneTargetValid", flags).GetValue(ai) &&
                    Mathf.Abs((float)typeof(Mw2MechAI).GetField("_dist3", flags).GetValue(ai) - 30861.58f) < 0.02f,
                    "Look matches native target-cache distance using unequal GP heights");
                var savedPoint = (Vector3)scenePoint.GetValue(ai);
                Check(!(bool)look.Invoke(ai, new object[] { AiTarget.ForUnit(combat) }) &&
                    !(bool)typeof(Mw2MechAI).GetField("_sceneTargetValid", flags).GetValue(ai) &&
                    (Vector3)scenePoint.GetValue(ai) == savedPoint &&
                    typeof(Mw2MechAI).GetField("_pilotTarget", flags).GetValue(ai) == pilotTarget,
                    "Failed self Look retains geometry and leaves the pilot target unchanged");
                gun.transform.SetPositionAndRotation(new Vector3(100000, 1000, 100000), Quaternion.identity);
                target.transform.position = gun.transform.position + Vector3.right * 100;
                var nav = new Mw2MissionDirector.NavInfo { Position = gun.transform.position + Vector3.forward * 20 };
                look.Invoke(ai, new object[] { AiTarget.ForNav(nav) });
                var blocker = GameObject.CreatePrimitive(PrimitiveType.Cube); blocker.transform.SetParent(root.transform);
                blocker.transform.position = gun.transform.position + Vector3.forward * 10;
                var clear = typeof(Mw2MechAI).GetMethod("SceneClear", flags);
                Check(!(bool)clear.Invoke(ai, new object[] { 100000f }),
                    "Scene query follows last Look NAV rather than the separate pilot target");
                nav.Position = gun.transform.position + Vector3.right * 20;
                Check(!(bool)clear.Invoke(ai, new object[] { 100000f }),
                    "Scene query retains cached endpoint when the NAV moves without Look");
                look.Invoke(ai, new object[] { AiTarget.ForNav(nav) });
                Check((bool)clear.Invoke(ai, new object[] { 100000f }),
                    "A fresh Look updates the scene endpoint and clears the obstacle");
                cockpit.Eye = null; twist.Pitch = 0; twist.Current = 0;
                var obstruction = blocker.AddComponent<Mw2MechCombat>();
                look.Invoke(ai, new object[] { AiTarget.ForUnit(obstruction) });
                Check((bool)aimClear.Invoke(ai, new object[] { twist, 2000f }),
                    "Aim ray accepts its scene target even when pilot target is another unit");
                typeof(Mw2MechAI).GetField("_pilotTarget", flags).SetValue(ai, AiTarget.ForUnit(obstruction));
                nav.Position = gun.transform.position + Vector3.forward * 20;
                look.Invoke(ai, new object[] { AiTarget.ForNav(nav) });
                Check(!(bool)aimClear.Invoke(ai, new object[] { twist, 2000f }),
                    "Aim ray does not accept pilot-unit obstruction when scene target is NAV");
                typeof(Mw2MechAI).GetField("_pilotTarget", flags).SetValue(ai, pilotTarget);
                // Eight no-weapon AI_FireNoLead 10005633 rows in native ai-fire-cache.csv.
                gun.transform.position = new Vector3(12, 100, -7);
                target.transform.position = new Vector3(120, 170, 240); victim.IsPlayer = true;
                combat.Weapons.Clear(); ai.FireChance = 1; twist.Pitch = 0; twist.Current = 0;
                typeof(Mw2MechAI).GetField("_fireClock", flags).SetValue(ai, 100d);
                var cachePitch = typeof(Mw2MechAI).GetField("_targetPitch", flags);
                var nextCheck = typeof(Mw2MechAI).GetField("_nextFireCheck", flags);
                var sceneTarget = typeof(Mw2MechAI).GetField("_sceneTarget", flags);
                var fireDecision = typeof(Mw2MechAI).GetMethod("FireDecision", flags);
                foreach (bool due in new[] { false, true })
                foreach (float pitch in new[] { 0f, 45f })
                foreach (float inputBearing in new[] { 0f, 30f })
                {
                    var request = AiTarget.ForNav(nav); look.Invoke(ai, new object[] { request });
                    cachePitch.SetValue(ai, pitch); nextCheck.SetValue(ai, due ? 100 : 200);
                    bool fired = (bool)fireDecision.Invoke(ai, new object[] { inputBearing });
                    bool refresh = due && pitch == 0 && inputBearing == 0;
                    float expectedPitch = refresh ? 16.5348377f : pitch;
                    Check(!fired && Mathf.Abs((float)cachePitch.GetValue(ai) - expectedPitch) < 0.0001f &&
                        twist.PitchTarget == (refresh ? 17 : pitch == 45 ? 48 : 0) &&
                        twist.Target == (inputBearing == 30 ? 10 : 0) &&
                        sceneTarget.GetValue(ai) == (refresh ? pilotTarget : request),
                        "Native no-lead cache/controls: due=" + due + ", pitch=" + pitch + ", bearing=" + inputBearing);
                }
                // GP+5c participates even after the falling bit has changed to lying.
                var bodyFall = (MechFall)typeof(Mw2MechController).GetField("_fall", flags).GetValue(gunController);
                foreach (bool lying in new[] { false, true })
                foreach (bool compensated in new[] { false, true })
                {
                    bodyFall.Falling = !lying; bodyFall.Down = lying; bodyFall.Pitch = 30;
                    twist.Pitch = compensated ? 30 : 0;
                    var request = AiTarget.ForNav(nav); look.Invoke(ai, new object[] { request });
                    cachePitch.SetValue(ai, 0f); nextCheck.SetValue(ai, 100);
                    fireDecision.Invoke(ai, new object[] { 0f });
                    Check(sceneTarget.GetValue(ai) == (compensated ? pilotTarget : request),
                        "Native body-pitch firing gate: lying=" + lying + ", weapon compensates=" + compensated);
                }
                bodyFall.Pitch = 0;
            }
            finally { DestroyImmediate(root); }
        }

        // Full original 10008407 observer references, including the different Pop check result.
        void CheckPilotRulePhase()
        {
            const string nativeRows = @"0,0,0,0,1,0,4,4096
0,0,0,1,0,0,0,4096
0,0,1,0,1,1,4,4096
0,0,1,1,0,1,0,4096
0,1,0,0,1,0,4,4096
0,1,0,1,0,0,0,4096
0,1,1,0,1,1,4,4096
0,1,1,1,0,0,0,4096
0,2,0,0,1,0,4,4096
0,2,0,1,0,1,0,4096
0,2,1,0,1,1,4,4096
0,2,1,1,0,1,0,4096
0,3,0,0,1,0,4,4096
0,3,0,1,0,0,0,4096
0,3,1,0,1,1,4,4096
0,3,1,1,1,0,0,4096
1,0,0,0,1,0,4,4096
1,0,0,1,0,0,0,4096
1,0,1,0,1,1,4,4096
1,0,1,1,0,1,0,4096
1,1,0,0,1,0,4,4096
1,1,0,1,0,0,0,4096
1,1,1,0,1,1,4,4096
1,1,1,1,0,0,0,4096
1,2,0,0,1,0,4,4096
1,2,0,1,0,1,0,4096
1,2,1,0,1,1,4,4096
1,2,1,1,0,1,0,4096
1,3,0,0,1,0,4,4096
1,3,0,1,0,0,0,4096
1,3,1,0,1,1,4,4096
1,3,1,1,1,0,0,4096
2,0,0,0,1,0,4,4096
2,0,0,1,0,0,0,4096
2,0,1,0,1,1,4,4096
2,0,1,1,0,1,0,4096
2,1,0,0,1,0,4,4096
2,1,0,1,0,0,0,4096
2,1,1,0,1,1,4,4096
2,1,1,1,0,0,0,4096
2,2,0,0,1,0,4,4096
2,2,0,1,0,1,0,4096
2,2,1,0,1,1,4,4096
2,2,1,1,0,1,0,4096
2,3,0,0,1,0,4,4096
2,3,0,1,0,0,0,4096
2,3,1,0,1,1,4,4096
2,3,1,1,1,0,0,4096
3,0,0,0,1,0,4,4096
3,0,0,1,0,0,0,4096
3,0,1,0,1,1,4,4096
3,0,1,1,0,1,0,4096
3,1,0,0,1,0,4,4096
3,1,0,1,0,0,0,4096
3,1,1,0,1,1,4,4096
3,1,1,1,0,0,0,4096
3,2,0,0,1,0,4,4096
3,2,0,1,0,1,0,4096
3,2,1,0,1,1,4,4096
3,2,1,1,0,1,0,4096
3,3,0,0,1,0,4,4096
3,3,0,1,0,0,0,4096
3,3,1,0,1,1,4,4096
3,3,1,1,1,0,0,4096
12,0,0,0,0,0,4,0
12,0,0,1,0,0,4,0
12,0,1,0,0,1,4,0
12,0,1,1,0,1,4,0
12,1,0,0,0,0,4,0
12,1,0,1,0,0,4,0
12,1,1,0,0,1,4,0
12,1,1,1,0,1,4,0
12,2,0,0,0,0,4,0
12,2,0,1,0,0,4,0
12,2,1,0,0,1,4,0
12,2,1,1,0,1,4,0
12,3,0,0,0,0,4,0
12,3,0,1,0,0,4,0
12,3,1,0,0,1,4,0
12,3,1,1,0,1,4,0";
            var root = new GameObject("Native pilot phase fixture");
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            var type = typeof(Mw2MechAI);
            try
            {
                var director = root.AddComponent<Mw2MissionDirector>(); director.enabled = false;
                director.AiTables = new AiTable[10]; director.AiTables[1] = new AiTable { Name = "Controlled no-listed-state fixture" };
                var go = new GameObject("Pilot phase unit"); go.transform.SetParent(root.transform);
                var combat = go.AddComponent<Mw2MechCombat>();
                var ai = go.AddComponent<Mw2MechAI>(); ai.enabled = false;
                var controller = go.GetComponent<Mw2MechController>(); controller.enabled = false;
                ai.Director = director; ai.Team = 15; ai.Leader = root.transform;
                type.GetField("_combat", flags).SetValue(ai, combat);
                type.GetField("_controller", flags).SetValue(ai, controller);
                type.GetField("_orderPilotFlags", flags).SetValue(ai, 1);
                var active = (List<AiRule>)type.GetField("_activeRules", flags).GetValue(ai);
                var stack = (Stack<(AiState state, AiTarget target)>)type.GetField("_stateStack", flags).GetValue(ai);
                var update = type.GetMethod("Update", flags);
                int checkedRows = 0;
                foreach (string row in nativeRows.Split('\n'))
                {
                    int[] v = row.Trim().Split(',').Select(int.Parse).ToArray();
                    ai.PilotState = (AiState)v[0]; combat.PoweredDown = false;
                    active.Clear(); stack.Clear();
                    if (v[2] != 0) stack.Push((AiState.Idle, null));
                    if (v[3] != 0) active.Add(new AiRule { Event = AiRule.Always, Check = v[1], NewState = -1, Target = 0 });
                    type.GetField("_sceneTarget", flags).SetValue(ai, AiTarget.ForNav(new Mw2MissionDirector.NavInfo { Position = Vector3.one * 999 }));
                    type.GetField("_sceneTargetValid", flags).SetValue(ai, true);
                    controller.Throttle = 0.375f; controller.Turn = 0.25f;
                    update.Invoke(ai, null);
                    bool acted = controller.Throttle != 0.375f;
                    bool sceneValid = (bool)type.GetField("_sceneTargetValid", flags).GetValue(ai);
                    if (acted != (v[4] != 0) || stack.Count != v[5] || sceneValid != (v[7] == 0))
                        throw new Exception("Original pilot rule/action phase mismatch: " + row + "; throttle " + controller.Throttle + "; stack " + stack.Count);
                    checkedRows++;
                }
                Check(checkedRows == 80, "All 80 original pilot rule/action phase cases match actual AI.Update control retention and stack effects");
                director.AiTables[1].States.Add(new AiStateRules { State = (int)AiState.Idle });
                director.AiTables[1].States.Add(new AiStateRules { State = (int)AiState.Avoid });
                ai.PilotState = AiState.Idle; combat.PoweredDown = false; stack.Clear(); active.Clear();
                active.Add(new AiRule { Event = AiRule.Always, Check = AiRule.Accept, NewState = (int)AiState.Avoid, Target = 0 });
                controller.Throttle = 0.375f;
                update.Invoke(ai, null);
                Check(ai.PilotState == AiState.Avoid && controller.Throttle == 0.375f,
                    "A valid AIT transition changes the state immediately while deferring its normal movement action");
                update.Invoke(ai, null);
                Check(Mathf.Abs(controller.Throttle - 0.8f) < 0.00001f,
                    "The transitioned Avoid state runs its ordinary movement on the following AI update");
            }
            finally { DestroyImmediate(root); }
        }

        // Original 1000854b/10009fe8: Pop retains its rule index after rebuilding the array.
        void CheckPilotPopContinuation()
        {
            const string nativeRows = @"0,0,0,0,0,1,0,3,1,4096,0.375,1234,0
0,0,0,10,0,1,0,3,1,4096,0.375,1234,0
0,0,0,11,0,1,0,3,1,4096,0.375,1234,0
0,0,1,0,1,1,0,3,1,4096,0.375,1234,0
0,0,1,10,1,1,0,3,1,4096,0.375,1234,0
0,0,1,11,1,1,0,3,1,4096,0.375,1234,0
0,1,0,0,0,1,0,3,1,4096,0.375,1234,0
0,1,0,10,0,1,0,3,1,4096,0.375,1234,0
0,1,0,11,0,1,0,3,1,4096,0.375,1234,0
0,1,1,0,1,0,0,0,1,4096,0,0,0
0,1,1,10,1,10,0,0,1,4096,0,0,16
0,1,1,11,1,11,0,0,1,4096,0,0,16
1,0,0,0,0,1,0,3,2,4096,0.375,1234,0
1,0,0,10,0,1,0,3,2,4096,0.375,1234,0
1,0,0,11,0,1,0,3,2,4096,0.375,1234,0
1,0,1,0,0,1,0,3,2,4096,0.375,1234,0
1,0,1,10,0,1,0,3,2,4096,0.375,1234,0
1,0,1,11,0,1,0,3,2,4096,0.375,1234,0
1,1,0,0,0,1,0,3,2,4096,0.375,1234,0
1,1,0,10,0,1,0,3,2,4096,0.375,1234,0
1,1,0,11,0,1,0,3,2,4096,0.375,1234,0
1,1,1,0,0,0,0,0,2,4096,0,0,0
1,1,1,10,0,10,0,0,2,4096,0,0,16
1,1,1,11,0,11,0,0,2,4096,0,0,16
2,0,0,0,0,1,0,3,2,4096,0.375,1234,0
2,0,0,10,0,1,0,3,2,4096,0.375,1234,0
2,0,0,11,0,1,0,3,2,4096,0.375,1234,0
2,0,1,0,1,1,0,3,2,4096,0.375,1234,0
2,0,1,10,1,1,0,3,2,4096,0.375,1234,0
2,0,1,11,1,1,0,3,2,4096,0.375,1234,0
2,1,0,0,0,1,0,3,2,4096,0.375,1234,0
2,1,0,10,0,1,0,3,2,4096,0.375,1234,0
2,1,0,11,0,1,0,3,2,4096,0.375,1234,0
2,1,1,0,1,0,0,0,2,4096,0,0,0
2,1,1,10,1,10,0,0,2,4096,0,0,16
2,1,1,11,1,11,0,0,2,4096,0,0,16
3,0,0,0,0,1,0,3,3,4096,0.375,1234,0
3,0,0,10,0,1,0,3,3,4096,0.375,1234,0
3,0,0,11,0,1,0,3,3,4096,0.375,1234,0
3,0,1,0,0,1,0,3,3,4096,0.375,1234,0
3,0,1,10,0,1,0,3,3,4096,0.375,1234,0
3,0,1,11,0,1,0,3,3,4096,0.375,1234,0
3,1,0,0,0,1,0,3,3,4096,0.375,1234,0
3,1,0,10,0,1,0,3,3,4096,0.375,1234,0
3,1,0,11,0,1,0,3,3,4096,0.375,1234,0
3,1,1,0,0,0,0,0,3,4096,0,0,0
3,1,1,10,0,10,0,0,3,4096,0,0,16
3,1,1,11,0,11,0,0,3,4096,0,0,16
4,0,0,0,0,1,0,3,2,4096,0.375,1234,0
4,0,0,10,0,1,0,3,2,4096,0.375,1234,0
4,0,0,11,0,1,0,3,2,4096,0.375,1234,0
4,0,1,0,0,1,0,3,2,4096,0.375,1234,0
4,0,1,10,0,1,0,3,2,4096,0.375,1234,0
4,0,1,11,0,1,0,3,2,4096,0.375,1234,0
4,1,0,0,0,1,0,3,2,4096,0.375,1234,0
4,1,0,10,0,1,0,3,2,4096,0.375,1234,0
4,1,0,11,0,1,0,3,2,4096,0.375,1234,0
4,1,1,0,0,0,0,0,2,4096,0,0,0
4,1,1,10,0,10,0,0,2,4096,0,0,16
4,1,1,11,0,11,0,0,2,4096,0,0,16
5,0,0,0,0,1,0,3,3,4096,0.375,1234,0
5,0,0,10,0,1,0,3,3,4096,0.375,1234,0
5,0,0,11,0,1,0,3,3,4096,0.375,1234,0
5,0,1,0,1,1,0,3,3,4096,0.375,1234,0
5,0,1,10,1,1,0,3,3,4096,0.375,1234,0
5,0,1,11,1,1,0,3,3,4096,0.375,1234,0
5,1,0,0,0,1,0,3,3,4096,0.375,1234,0
5,1,0,10,0,1,0,3,3,4096,0.375,1234,0
5,1,0,11,0,1,0,3,3,4096,0.375,1234,0
5,1,1,0,1,0,0,0,3,4096,0,0,0
5,1,1,10,1,10,0,0,3,4096,0,0,16
5,1,1,11,1,11,0,0,3,4096,0,0,16
6,0,0,0,0,1,0,3,3,4096,0.375,1234,0
6,0,0,10,0,1,0,3,3,4096,0.375,1234,0
6,0,0,11,0,1,0,3,3,4096,0.375,1234,0
6,0,1,0,0,1,0,3,3,4096,0.375,1234,0
6,0,1,10,0,1,0,3,3,4096,0.375,1234,0
6,0,1,11,0,1,0,3,3,4096,0.375,1234,0
6,1,0,0,0,1,0,3,3,4096,0.375,1234,0
6,1,0,10,0,1,0,3,3,4096,0.375,1234,0
6,1,0,11,0,1,0,3,3,4096,0.375,1234,0
6,1,1,0,0,0,0,0,3,4096,0,0,0
6,1,1,10,0,10,0,0,3,4096,0,0,16
6,1,1,11,0,11,0,0,3,4096,0,0,16";
            var root = new GameObject("Native Pop continuation fixture");
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            var type = typeof(Mw2MechAI);
            try
            {
                var director = root.AddComponent<Mw2MissionDirector>(); director.enabled = false;
                director.AiTables = new AiTable[10];
                var go = new GameObject("Pop unit"); go.transform.SetParent(root.transform);
                var combat = go.AddComponent<Mw2MechCombat>();
                var ai = go.AddComponent<Mw2MechAI>(); ai.enabled = false;
                var controller = go.GetComponent<Mw2MechController>(); controller.enabled = false;
                ai.Director = director; ai.Team = 15; ai.Leader = root.transform;
                type.GetField("_combat", flags).SetValue(ai, combat);
                type.GetField("_controller", flags).SetValue(ai, controller);
                type.GetField("_orderPilotFlags", flags).SetValue(ai, 1);
                var active = (List<AiRule>)type.GetField("_activeRules", flags).GetValue(ai);
                var stack = (Stack<(AiState state, AiTarget target)>)type.GetField("_stateStack", flags).GetValue(ai);
                var evaluate = type.GetMethod("UpdatePilotRules", flags);
                int checkedRows = 0;
                foreach (string row in nativeRows.Split('\n'))
                {
                    var v = row.Trim().Split(',').Select(x => float.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                    int scenario = (int)v[0], count = scenario == 0 ? 1 : scenario == 3 || scenario >= 5 ? 3 : 2;
                    director.AiTables[1] = new AiTable { Name = "Controlled Pop rebuild" };
                    var restored = new AiStateRules { State = (int)v[3] };
                    for (int i = 0; i < count; i++)
                        restored.Rules.Add(new AiRule { A = i + 1, NewState = -1,
                            Event = i == 1 && (scenario == 2 || scenario == 3) || i == 2 && scenario == 5 ? AiRule.Never : AiRule.Always,
                            Check = i == 1 && scenario == 4 ? AiRule.Pop : i == 1 && scenario >= 5 ? AiRule.TellLeader : AiRule.Accept });
                    if (v[1] != 0) director.AiTables[1].States.Add(restored);
                    active.Clear(); stack.Clear();
                    active.Add(new AiRule { Event = AiRule.Always, A = 1, Check = AiRule.Pop, NewState = -1 });
                    for (int i = 1; i < count; i++) active.Add(restored.Rules[i]);
                    if (v[2] != 0) stack.Push(((AiState)v[3], null));
                    ai.PilotState = AiState.Avoid; combat.PoweredDown = false;
                    controller.Throttle = 0.375f;
                    type.GetField("_flat", flags).SetValue(ai, 1234f);
                    bool consumed = (bool)evaluate.Invoke(ai, null);
                    if (consumed != (v[4] == 0) || (int)ai.PilotState != (int)v[5] || stack.Count != (int)v[6] ||
                        active[0].Check != (int)v[7] || active.Count != (int)v[8] ||
                        controller.Throttle != v[10] || (float)type.GetField("_flat", flags).GetValue(ai) != v[11] ||
                        combat.PoweredDown != (((int)v[12] & 16) != 0))
                        throw new Exception("Original Pop continuation mismatch: " + row);
                    checkedRows++;
                }
                Check(checkedRows == 84, "All 84 original Pop continuation/rebuild cases match actual rules, state, stack and immediate entry controls");
            }
            finally { DestroyImmediate(root); }
        }

        // FUN_1000c0c2: exercise the actual Unity dispatcher with original archived AIT tables.
        void CheckDetectionAssignment(Mw2MissionDirector scene)
        {
            var root = new GameObject("Detection assignment fixture");
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            var type = typeof(Mw2MechAI);
            try
            {
                var director = root.AddComponent<Mw2MissionDirector>(); director.enabled = false;
                director.AiTables = scene.Rules;
                var orders = (Dictionary<int, int>)typeof(Mw2MissionDirector).GetField("_currentOrder", flags).GetValue(director);
                var pilots = new List<Mw2MechAI>();
                for (int i = 0; i < 3; i++)
                {
                    var go = new GameObject("Detection pilot " + i); go.transform.SetParent(root.transform);
                    var combat = go.AddComponent<Mw2MechCombat>(); // Keep registered until synchronous fixture cleanup.
                    var ai = go.AddComponent<Mw2MechAI>(); ai.enabled = false;
                    var controller = go.GetComponent<Mw2MechController>(); controller.enabled = false;
                    ai.Director = director; ai.Team = 15;
                    if (i != 0) ai.Leader = pilots[0].transform;
                    type.GetField("_combat", flags).SetValue(ai, combat);
                    type.GetField("_controller", flags).SetValue(ai, controller);
                    type.GetField("_engageAtWill", flags).SetValue(ai, true);
                    pilots.Add(ai);
                }
                var enemy = scene.Player.GetComponent<Mw2MechCombat>();
                var target = AiTarget.ForUnit(enemy);
                var receive = type.GetMethod("ReceiveDetection", flags);
                void Reset(int mode)
                {
                    foreach (var ai in pilots)
                    {
                        ai.PilotState = AiState.Idle;
                        type.GetField("_pilotTarget", flags).SetValue(ai, null);
                        type.GetField("_orderPilotFlags", flags).SetValue(ai, mode);
                    }
                }
                // Index 4 is special even when Assignees=1; all members and leader must receive it.
                director.Teams.Add(new TeamOrders { Team = 15, Orders = Enumerable.Range(0, 5)
                    .Select(_ => new MissionOrder { Assignees = 1 }).ToList() });
                orders[15] = 4; Reset(3);
                receive.Invoke(pilots[0], new object[] { target });
                Check(pilots.All(ai => ai.PilotState == AiState.Target && ai.PilotTarget.Unit == enemy),
                    "Detection at order index 4 reaches all eligible pilots and leader despite Assignees=1/PilotMode");
                orders[15] = 3; Reset(3);
                receive.Invoke(pilots[0], new object[] { target });
                Check(pilots.All(ai => ai.PilotState == AiState.Idle), "Other order indices retain PilotMode detection restrictions");
                Reset(0);
                pilots[1].GetComponent<Mw2MechCombat>().Tons = enemy.Tons;
                pilots[1].GetComponent<Mw2MechCombat>().PilotCombatValue = enemy.PilotCombatValue == 0 ? 1000 : 0;
                pilots[2].GetComponent<Mw2MechCombat>().Tons = enemy.Tons + 50;
                pilots[2].GetComponent<Mw2MechCombat>().PilotCombatValue = enemy.PilotCombatValue;
                receive.Invoke(pilots[0], new object[] { target });
                Check(pilots[2].PilotState == AiState.Target && pilots[1].PilotState == AiState.Idle && pilots[0].PilotState == AiState.Idle,
                    "Detection assignment ranks load-time combat value rather than equal tonnage");
            }
            finally { DestroyImmediate(root); }
        }

        void CheckUnitSpheres()
        {
            var a = new GameObject("Sphere self fixture");
            var b = new GameObject("Sphere other fixture");
            try
            {
                a.transform.position = new Vector3(10005, 10000, 10000);
                b.transform.position = new Vector3(10000, 10000, 10000);
                var self = a.AddComponent<Mw2MechCombat>(); self.IsPlayer = true; self.GamepieceIndex = 10000;
                var other = b.AddComponent<Mw2MechCombat>(); other.GamepieceIndex = 10001;
                var control = a.AddComponent<Mw2MechController>(); control.CollisionRadius = 5;
                var otherControl = b.AddComponent<Mw2MechController>(); otherControl.CollisionRadius = 5;
                var motion = new MechMotion(0, 0, 0) { X = 1000500, Y = 1000000, Z = 1000000 };
                var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                var type = typeof(Mw2MechController);
                type.GetField("_motion", flags).SetValue(control, motion);
                type.GetField("_preCollisionVelocity", flags).SetValue(control, new Vector3(10, 0, 0));
                var collide = type.GetMethod("CollideUnits", flags);
                bool hit = (bool)collide.Invoke(control, null);
                Check(hit && Mathf.Abs(motion.X - 1001000) < 0.1f && motion.VelX == 5 && motion.VelY == 0,
                    "Live-unit sphere response uses MGEO radius and saved relative speed without a root collider");
                motion.X = 1000500; motion.Y = 1001200;
                Check(!(bool)collide.Invoke(control, null) && motion.X == 1000500,
                    "Vertically separated units do not collide as horizontal boxes");
                motion.Y = 1000000; other.UnitFlags |= 0x4000;
                Check(!(bool)collide.Invoke(control, null), "GP flag 0x4000 excludes other-unit sphere collision");
                other.UnitFlags &= ~0x4000;
                self.GamepieceIndex = 10001; other.GamepieceIndex = 10000; motion.X = 1002000;
                type.GetField("_contactUnit", flags).SetValue(otherControl, self);
                type.GetField("_contactFrame", flags).SetValue(otherControl, Time.frameCount);
                Check((bool)collide.Invoke(control, null) && motion.X == 1001000 && motion.VelX == 10,
                    "Reciprocal contact retains the first position and doubles its response after the second native-style pass");
                DestroyImmediate(otherControl);
                other.ModelRadius = 5; self.GamepieceIndex = 10000; other.GamepieceIndex = 10001;
                motion.X = 1000500;
                Check((bool)collide.Invoke(control, null) && motion.X == 1001000 && motion.VelX == 5,
                    "Static GP unit without an animation/controller still collides using its MGEO radius");
            }
            finally { DestroyImmediate(a); DestroyImmediate(b); }
        }

        // Exercise the real audio queue, including the source's unusual promoted-head
        // exemption. Disable Update so playback/device availability cannot race this check.
        void CheckRadioExit()
        {
            var obj = new GameObject("Radio exit integration fixture");
            try
            {
                var previous = Mw2Audio.Instance;
                var audio = obj.AddComponent<Mw2Audio>();
                audio.enabled = false;
                Mw2Audio.Instance = previous;
                var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                var type = typeof(Mw2Audio);
                var slotType = type.GetNestedType("RadioMessage", System.Reflection.BindingFlags.NonPublic);
                var queue = (System.Collections.IList)type.GetField("_voices", flags).GetValue(audio);
                object Slot(int first, int priority)
                {
                    var slot = Activator.CreateInstance(slotType);
                    slotType.GetField("First").SetValue(slot, first);
                    slotType.GetField("Priority").SetValue(slot, priority);
                    return slot;
                }
                type.GetField("_radioActive", flags).SetValue(audio, true);
                type.GetField("_activeMessage", flags).SetValue(audio, Slot(1, 79));
                queue.Add(Slot(2, 79)); // Promoted head: retained despite low priority.
                queue.Add(Slot(3, 79)); // Remaining low-priority waiting slot: removed.
                queue.Add(Slot(4, 80));
                queue.Add(Slot(0, 100)); // Display-only text is not a resource pointer.
                audio.PruneRadioForExit();
                Check(queue.Count == 2 && (int)slotType.GetField("First").GetValue(queue[0]) == 2 &&
                    (int)slotType.GetField("First").GetValue(queue[1]) == 4 &&
                    !(bool)type.GetField("_radioActive", flags).GetValue(audio),
                    "Radio exit removes low-priority active/waiting slots but preserves promoted head and priority 80");
                Check(!audio.QueuePair(new SoundTables.RadioPart { Sound = 1 }, null),
                    "Radio exit latch rejects subsequent queued speech");
                audio.PruneRadioForExit();
                Check(queue.Count == 2, "Radio exit latch makes repeated pruning inert");
            }
            finally { DestroyImmediate(obj); }
        }

        // Original gate 0x10041469 is called by both 0x1004154a and 0x100421e6 on
        // every pass. Exercise that integration, including the retained timer,
        // without completing the real BET3 director or changing its objectives.
        void CheckConditionReevaluation()
        {
            var obj = new GameObject("MTBL condition integration fixture");
            try
            {
                var fixture = obj.AddComponent<Mw2MissionDirector>();
                fixture.enabled = false;
                var team = new TeamOrders { Team = 0, TimeLimit = -1 };
                team.Orders.Add(new MissionOrder { Mask = 0x200, AllConditions = false });
                team.Orders.Add(new MissionOrder { Mask = 0x200, AllConditions = true, Mandatory = true, TimeLimit = 1,
                    ConditionTypes = "S", ConditionA = new[] { 0 }, ConditionB = new[] { 0 } });
                fixture.Teams.Add(team);
                const System.Reflection.BindingFlags fields = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                var type = typeof(Mw2MissionDirector);
                var states = (Dictionary<(int team, int order), Mw2MissionDirector.State>)type.GetField("_states", fields).GetValue(fixture);
                var started = (Dictionary<(int team, int order), int>)type.GetField("_started", fields).GetValue(fixture);
                states[(0, 1)] = Mw2MissionDirector.State.Active;
                started[(0, 1)] = 0;
                type.GetField("_missionClock", fields).SetValue(fixture, 2f);
                var update = type.GetMethod("Update", fields);
                update.Invoke(fixture, null);
                Check(fixture.StateOf(0, 1) == Mw2MissionDirector.State.Active && fixture.CurrentOrder(0) == -1 && started[(0, 1)] == 0,
                    "Active MTBL order pauses evaluation and AI selection when its dependency no longer holds");
                states[(0, 0)] = Mw2MissionDirector.State.Succeeded;
                update.Invoke(fixture, null);
                Check(fixture.StateOf(0, 1) == Mw2MissionDirector.State.Succeeded && started[(0, 1)] == 0,
                    "MTBL condition recovery resumes evaluation with the original start second");
            }
            finally { Destroy(obj); }
        }

        // Mech_CriticalHit 0x10036aa9, recursive calls 0x10037316/0x1003734d:
        // the reroll covers slot zero and is silent even when it selects itself.
        void CheckStructuralCriticals()
        {
            var obj = new GameObject("Structural critical integration fixture");
            var random = UnityEngine.Random.state;
            try
            {
                var unit = obj.AddComponent<Mw2MechCombat>();
                unit.enabled = false;
                unit.SectionItems = Enumerable.Range(0, 8).Select(_ => new List<int>()).ToArray();
                var critical = typeof(Mw2MechCombat).GetMethod("Critical", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                unit.SectionItems[1].AddRange(new[] { 9000, 9500 });
                critical.Invoke(unit, new object[] { 2, 0, false, null });
                Check(unit.SectionItems[1].Count == 1, "Structural-only critical removes a slot without recursive overflow");
                critical.Invoke(unit, new object[] { 2, 0, false, null });
                Check(unit.SectionItems[1].Count == 0, "Single structural slot rerolls slot zero and is removed");
                bool sawWeapon = false, sawStructure = false;
                for (int seed = 0; seed < 64; seed++)
                {
                    UnityEngine.Random.InitState(seed);
                    unit.SectionItems[1].AddRange(new[] { 100, 9000, 9500 });
                    var weapon = new Mw2MechCombat.MountedWeapon { Id = 100 };
                    unit.Weapons.Clear(); unit.Weapons.Add(weapon);
                    critical.Invoke(unit, new object[] { 2, 1, false, null });
                    if (unit.SectionItems[1].Count != 2 || weapon.Broken == unit.SectionItems[1].Contains(100))
                        throw new Exception("Structural reroll did not remove exactly the selected native critical item");
                    sawWeapon |= weapon.Broken;
                    sawStructure |= !weapon.Broken;
                    unit.SectionItems[1].Clear();
                }
                Check(sawWeapon && sawStructure, "Structural reroll reaches both slot-zero weapons and structural slots over 64 seeds");
            }
            finally { UnityEngine.Random.state = random; Destroy(obj); }
        }

        // Original point POLYs remain visible, while only actual triangle
        // surfaces may be passed to Unity's MeshCollider (0x10018ee2/0x1001855c).
        void CheckPointCollisionMeshes()
        {
            using (var prj = new PrjArchive(Path.Combine(Plan.gameFolder, "MW2.PRJ")))
            {
                var palette = Enumerable.Repeat(new Color32(255, 255, 255, 255), 256).ToArray();
                var dummy = PolyMeshBuilder.Build(PolyModel.Decode(prj.Read("POLY", "DUMMY"))[0], palette);
                var street = PolyMeshBuilder.Build(PolyModel.Decode(prj.Read("POLY", "TL1CBLOK"))[0], palette);
                try
                {
                    Check(dummy.vertexCount == 1 && !PolyMeshBuilder.HasCollisionTriangles(dummy),
                        "Original DUMMY point keeps its render mesh without an invalid surface collider");
                    Check(PolyMeshBuilder.HasCollisionTriangles(street), "Original TL1CBLOK street retains triangle collision geometry");
                }
                finally { Destroy(dummy); Destroy(street); }
            }
        }
    }
}
