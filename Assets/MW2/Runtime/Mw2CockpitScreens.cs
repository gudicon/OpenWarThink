using System;
using System.Collections.Generic;
using MW2.Formats;
using UnityEngine;

namespace MW2
{
    /// <summary>
    /// Two text screens of the cockpit:
    /// F12 (DISPLAY_OBJECTIVES, command 0x58): the "MISSION OBJECTIVES" summary in gauge 19 (0x10068a01, entries
    /// 0x1006842b). While it is shown the seven HUD windows are switched off (0x1003e7f3 saves their modes,
    /// 0x1003e74b sets mode 0, 0x1003e85c restores them).
    /// B / Ctrl+F1 (command 0x59): the lance menu "COMMAND COMPUTER", in-game window 1 (descriptor 0x1008ac20, root page
    /// 0x1008a958); Ctrl+F2 / Ctrl+F3 (0x5a / 0x5b) open windows 7 / 8 straight at "COMMAND POINT 2 / 3". Menu engine
    /// 0x1003c8c8 -> 0x1003c9ba -> 0x1003cb59 (draw and keys), values 0x1003d7ce.
    /// Approximations: one window is open at a time (the original keeps a list and runs the first open one); the root
    /// page is rebuilt each time it opens (0x100076c0 runs once at mission start in FirstMenu 0x1003c56a); opening the
    /// lance menu over the objectives brings the HUD windows back (the original leaves them off).
    /// </summary>
    public partial class Mw2Cockpit
    {
        // ---------------- F12: mission objectives (gauge 19) ----------------

        /// <summary>DAT_100b18fc = 1: the objectives screen is up (2 = multiplayer scores, not ported).</summary>
        public bool ObjectivesShown;

        /// <summary>
        /// 0x10068a01: "MISSION OBJECTIVES" in colour 6 at the gauge origin, underlined in 6 (0x1004b040); then the 'V'
        /// entries of the player's team by group (+0x2E): 1, half a line, 2, half a line, 4 and 0, half a line, 8; then
        /// after a line and a half the time in colour 6. Half a line = font height / 2 (integer).
        /// </summary>
        void DrawObjectives()
        {
            if (_director == null || Layout.Rects.Length <= 19) return;
            var team = _director.Teams.Find(t => t.Team == Mw2MissionDirector.PlayerTeam);
            if (team == null) return;
            var origin = GaugeOrigin(19);
            float line = FontHeight * Py, half = FontHeight / 2 * Py;
            float right = GaugeRect(19).x + (Layout.Rects[19].width - 1) * Sx;     // x1 - x0 of the gauge

            float y = origin.y;
            const string title = "MISSION OBJECTIVES";
            DrawText(title, new Vector2(origin.x, y), 6);
            DrawUnderline(origin.x, y, TextWidth(title), 6);
            y += line + half;
            // Hud_DrawObjectives 0x10068a01: in a network game "%s : %i" for every player still in it.
            if (Mw2MechCombat.NetGame && Mw2NetGame.Instance != null)
            {
                var net = Mw2NetGame.Instance;
                for (int i = 0; i < net.Units.Length; i++)
                {
                    if (net.Units[i] == null || !net.Units[i].gameObject.activeSelf) continue;
                    DrawText($"{net.Net.PlayerName(net.Ids[i])} : {net.Scores[i]}", new Vector2(origin.x, y), 0x0e);
                    y += line;
                }
                y += half;
            }
            DrawObjectiveGroup(team, 1, origin.x, ref y, right);
            y += half;
            DrawObjectiveGroup(team, 2, origin.x, ref y, right);
            y += half;
            DrawObjectiveGroup(team, 4, origin.x, ref y, right);
            DrawObjectiveGroup(team, 0, origin.x, ref y, right);
            y += half;
            DrawObjectiveGroup(team, 8, origin.x, ref y, right);
            y += line + half;
            DrawText(MissionTimeText(), new Vector2(origin.x, y), 6);
        }

        /// <summary>
        /// 0x1006842b: one line per 'V' entry (+0x74) whose group (+1, MTBL +0x2E) matches: the label in colour 6
        /// ("Target: " for 1, "Bonus: " for 2, "Bounty: " for 4 and 0, "Return: " for 8), the text in 0x0e at the label x +
        /// width of "Secondary: " (32 characters or more: the first 32, the rest on the next line), the state right-aligned
        /// at the gauge's right edge: 5 "Complete" (7), 6 "InComplete" (0x0b), anything else "In progress" (0x0e).
        /// </summary>
        void DrawObjectiveGroup(TeamOrders team, int group, float x, ref float y, float right)
        {
            float line = FontHeight * Py;
            for (int i = 0; i < team.Orders.Count; i++)
            {
                var o = team.Orders[i];
                if (!o.Vital || o.Kind != group) continue;
                string label = group == 1 ? "Target: " : group == 2 ? "Bonus: " : group == 8 ? "Return: " : "Bounty: ";
                DrawText(label, new Vector2(x, y), 6);
                float tx = x + TextWidth("Secondary: ");
                string text = o.Text ?? "";
                bool split = text.Length >= 0x20;
                DrawText(split ? text.Substring(0, 0x20) : text, new Vector2(tx, y), 0x0e);
                if (split) DrawText(text.Substring(0x20), new Vector2(tx, y + line), 0x0e);

                var state = _director.StateOf(0, i);
                string status = state == Mw2MissionDirector.State.Succeeded ? "Complete"
                              : state == Mw2MissionDirector.State.Failed || state == Mw2MissionDirector.State.FailedOther ? "InComplete" : "In progress";
                int color = state == Mw2MissionDirector.State.Succeeded ? 7
                          : state == Mw2MissionDirector.State.Failed || state == Mw2MissionDirector.State.FailedOther ? 0x0b : 0x0e;
                DrawText(status, new Vector2(right - TextWidth(status), y), color);
                y += split ? 2 * line : line;
            }
        }

        /// <summary>
        /// Team state DAT_102007b8: 0 running -> "Elapsed Time: %s" (no limit, +0xC below 1) or "Time Remaining: %s"
        /// in ticks as "%2.2d:%2.2d:%05.2f" (0x100682f0: 181 ticks a second); 2 "Completed at %s", 3 "Incompleted at %s",
        /// 4 "Out of time at %s" with the finish second (+0x8) as "%2.2d:%2.2d:%2.2d" (0x10068371).
        /// </summary>
        string MissionTimeText()
        {
            switch (_director.MissionResult)
            {
                case Mw2MissionDirector.Result.Success: return "Completed at " + Hms((int)_director.FinishTime);
                case Mw2MissionDirector.Result.Failure: return "Incompleted at " + Hms((int)_director.FinishTime);
                case Mw2MissionDirector.Result.TimeExceeded: return "Out of time at " + Hms((int)_director.FinishTime);
            }
            int now = (int)(_director.MissionTime * MechMotion.TicksPerSecond);
            int limit = _director.TimeLimit(0);
            if (limit < 1) return "Elapsed Time: " + TicksText(now);
            return "Time Remaining: " + TicksText(limit * (int)MechMotion.TicksPerSecond - now);
        }

        static string TicksText(int ticks)
        {
            const int hour = 0x9f150, minute = 0x2a6c;    // 181 * 3600, 181 * 60
            int minutes = ticks % hour / minute;
            double seconds = (ticks % hour - minutes * minute) / 181.0;
            return $"{ticks / hour:00}:{minutes:00}:{seconds:00.00}";
        }

        static string Hms(int seconds) => $"{seconds / 3600:00}:{seconds % 3600 / 60:00}:{seconds % 3600 % 60:00}";

        /// <summary>0x1004b040: a line one pixel high under a string, at y + font height, from x over the text width.</summary>
        void DrawUnderline(float x, float y, float width, int colorIndex)
        {
            var old = GUI.color;
            GUI.color = Pal(colorIndex);
            GUI.DrawTexture(new Rect(x, y + FontHeight * Py, width, Mathf.Max(1f, Py)), Texture2D.whiteTexture);
            GUI.color = old;
        }

        // ---------------- B: lance command computer ----------------

        /// <summary>
        /// Menu item, 0x14 bytes: type, text, draw function (0x1003d7ce: the value column), value record, sub-page.
        /// Types (0x1003ca92 on Enter): 0 opens the sub-page, 1 does nothing (a destroyed lance member), 3 is a label
        /// without a number, 5 runs its action and returns, 6 "(Esc to exit)" returns.
        /// </summary>
        sealed class MenuItem
        {
            public int Type;
            public string Text;
            public Func<string> Value;
            public Action Act;
            public Func<MenuPage> Open;
        }

        sealed class MenuPage
        {
            public string Title;
            public int Selected;        // page +0x10, 0 when the page opens
            public readonly List<MenuItem> Items = new List<MenuItem>();
        }

        /// <summary>Stack of open pages (window +8 / +0xc, at most 8 deep: 0x1003d235).</summary>
        readonly List<MenuPage> _menu = new List<MenuPage>();
        int _menuWindow;

        /// <summary>True while a command window takes the digit keys (DAT_100ad380 = 1).</summary>
        public static bool CommandMenuOpen;

        /// <summary>Command status of each lance position (0x1008ac90, 8 ints, initially 7 "No Cmd"), list 0x100895e8.</summary>
        readonly int[] _commandStatus = { 7, 7, 7, 7, 7, 7, 7, 7 };
        static readonly string[] CommandNames =
            { "Change Formation", "Engage at Will", "Attack", "Join Formation", "Defend", "Disengage", "Shutdown", "No Cmd" };

        /// <summary>Formation names, list 0x10089560 (index = FTBL entry; 6 is "No Formation").</summary>
        static readonly string[] FormationNames =
            { "Echelon Left", "Echelon Right", "Line Abreast", "Line Astern", "V Form", "Wedge", "No Formation" };

        /// <summary>Pilot states + 1, list 0x10089670 (entries keep their trailing space); 13 = destroyed (state 0xc).</summary>
        static readonly string[] PilotStateNames =
        {
            "None ", "Idle ", "Avoiding ", "Targeting ", "Engaging ", "Disengaging ", "In Formation ", "Reconning ",
            "Defending ", "En Route ", "Disengaging ", "Silent ", "Shutdown ", "Destroyed "
        };

        /// <summary>FUN_1003bfa7: open the window when it is closed, close it when it is open.</summary>
        void ToggleCommandWindow(int window)
        {
            bool same = _menu.Count > 0 && _menuWindow == window;
            _menu.Clear();
            if (same) return;
            _menuWindow = window;
            var root = window == 1 ? RootPage() : LanceMemberPage(window == 7 ? 1 : 2);
            _menu.Add(root);
        }

        List<Mw2MechAI> Lance => Mw2MechAI.LanceOf(0);

        Mw2MechAI LanceMember(int position)
        {
            foreach (var ai in Lance) if (ai.LancePosition == position) return ai;
            return null;
        }

        static bool IsDown(Mw2MechAI ai) => ai == null || !ai.TryGetComponent<Mw2MechCombat>(out var c) || c.Destroyed;

        int _selectedStarMate; // DAT_100a62c8, set by STARMATE_SET_ALL/PT2/PT3 (0x10007c6e).

        /// <summary>Lance_MemberAvailable 0x10007820: the position exists and the pilot is not in dead state 12.</summary>
        bool StarMateAvailable(int position)
        {
            var ai = LanceMember(position);
            return !IsDown(ai) && ai.PilotState != AiState.Dead;
        }

        /// <summary>FUN_10007c6e 0x10007c6e: select all or one of the first two star mates and request reply 0.</summary>
        void SelectStarMate(int requested)
        {
            bool one = StarMateAvailable(1), two = StarMateAvailable(2);
            int selected = requested == 0 ? one && two ? 0 : one ? 1 : two ? 2 : -1
                : requested == 1 ? one ? 1 : -1 : two ? 2 : -1;
            if (selected < 0) return;
            _selectedStarMate = selected;
            if (Mw2Audio.Instance != null) Mw2Audio.Instance.PilotReply(selected, 0);
        }

        /// <summary>Input_Command 0x100267c0 cases 0x19..0x24: resolve DAT_100a62c8 through 0x10007d04,
        /// then call the same Lance_Cmd* functions (0x10007a7f..0x10007b60) as the command menu.</summary>
        void UpdateDirectStarCommands()
        {
            if (Mw2Input.Command(0x19)) SelectStarMate(0);
            else if (Mw2Input.Command(0x1a)) SelectStarMate(1);
            else if (Mw2Input.Command(0x1b)) SelectStarMate(2);

            int selected = _selectedStarMate;
            if (selected == 0)
            {
                bool one = StarMateAvailable(1), two = StarMateAvailable(2);
                selected = one && two ? 0 : one ? 1 : two ? 2 : -1;
            }
            else if (!StarMateAvailable(selected)) selected = -1;

            if (Mw2Input.Command(0x23) && StarMateAvailable(4)) Command(4, 2, 2);
            if (Mw2Input.Command(0x24) && StarMateAvailable(4)) Command(4, 3, 5);
            if (selected < 0) return;
            if (Mw2Input.Command(0x1c)) Command(selected, 2, 2);      // attack
            else if (Mw2Input.Command(0x1d)) Command(selected, 4, 7); // defend
            else if (Mw2Input.Command(0x1e) || Mw2Input.Command(0x22)) Command(selected, 3, 5); // join / reform
            else if (Mw2Input.Command(0x1f)) Command(selected, 1, 3); // engage at will
            else if (Mw2Input.Command(0x20)) Command(selected, 6, 11); // shutdown
            else if (Mw2Input.Command(0x21)) Command(selected, 5, 8); // disengage
        }

        /// <summary>
        /// Root page "COMMAND COMPUTER" (0x1008a958, set up by 0x100076c0): Change Formation, Command Point 2.. for each
        /// lance member (type 1 when destroyed), Command All, (Esc to exit); alone or with every member destroyed:
        /// "NO STAR MATES" and (Esc to exit). The lance size is the team's member count (0x1000ca68), at most 5.
        /// </summary>
        MenuPage RootPage()
        {
            var page = new MenuPage { Title = "COMMAND COMPUTER" };
            int size = Mathf.Min(5, Lance.Count + 1);
            int down = 0;
            page.Items.Add(new MenuItem { Type = 0, Text = "Change Formation", Value = FormationText, Open = FormationPage });
            for (int i = 1; i < size; i++)
            {
                int position = i;
                bool dead = IsDown(LanceMember(position));
                if (dead) down++;
                page.Items.Add(new MenuItem
                {
                    Type = dead ? 1 : 0, Text = $"Command Point {i + 1}",
                    Value = () => MemberStatus(position), Open = () => LanceMemberPage(position)
                });
            }
            page.Items.Add(new MenuItem { Type = 0, Text = "Command All", Value = () => CommandNames[_commandStatus[0]], Open = CommandAllPage });
            page.Items.Add(ExitItem());
            if (size == 1 || size - 1 == down)
            {
                page.Items.Clear();
                page.Items.Add(new MenuItem { Type = 3, Text = "NO STAR MATES" });
                page.Items.Add(ExitItem());
            }
            return page;
        }

        static MenuItem ExitItem() => new MenuItem { Type = 6, Text = "(Esc to exit)" };

        /// <summary>
        /// "COMMAND POINT n" (0x10089ef8, 0x1008a190, ...; set up by 0x100078a8): Status: and the five commands; when the
        /// member is missing or destroyed (0x10007820) "NOT AVAILABLE" and (Esc to exit).
        /// </summary>
        MenuPage LanceMemberPage(int position)
        {
            var page = new MenuPage { Title = $"COMMAND POINT {position + 1}" };
            if (IsDown(LanceMember(position)))
            {
                page.Items.Add(new MenuItem { Type = 3, Text = "NOT AVAILABLE" });
                page.Items.Add(ExitItem());
                return page;
            }
            page.Items.Add(new MenuItem { Type = 3, Text = "Status:", Value = () => MemberStatus(position) });
            AddCommands(page, position);
            return page;
        }

        /// <summary>"COMMAND ALL" (0x10089c60): Status: shows the last command given to all (0x1008ac90[0]).</summary>
        MenuPage CommandAllPage()
        {
            var page = new MenuPage { Title = "COMMAND ALL" };
            page.Items.Add(new MenuItem { Type = 3, Text = "Status:", Value = () => CommandNames[_commandStatus[0]] });
            AddCommands(page, 0);
            return page;
        }

        /// <summary>
        /// The five commands (records 0x100898d8.., actions 0x10007a7f, 0x10007b06, 0x10007ad9, 0x10007aac, 0x10007b60):
        /// each stores its status (2, 4, 3, 1, 6) and sends command 2, 7, 5, 3, 11 (FUN_1000b7aa).
        /// </summary>
        void AddCommands(MenuPage page, int position)
        {
            page.Items.Add(new MenuItem { Type = 5, Text = "Attack My Target", Act = () => Command(position, 2, 2) });
            page.Items.Add(new MenuItem { Type = 5, Text = "Defend My Target", Act = () => Command(position, 4, 7) });
            page.Items.Add(new MenuItem { Type = 5, Text = "Join Formation", Act = () => Command(position, 3, 5) });
            page.Items.Add(new MenuItem { Type = 5, Text = "Engage at Will", Act = () => Command(position, 1, 3) });
            page.Items.Add(new MenuItem { Type = 5, Text = "Shutdown", Act = () => Command(position, 6, 11) });
            page.Items.Add(ExitItem());
        }

        /// <summary>
        /// "CHANGE FORMATION" (0x100899c8): Current Form: (0x10007984: the team's formation) and the six formations
        /// (0x1000799e: status 0 "Change Formation", formation n for the player's team, 0x1002a29e).
        /// </summary>
        MenuPage FormationPage()
        {
            var page = new MenuPage { Title = "CHANGE FORMATION" };
            page.Items.Add(new MenuItem { Type = 3, Text = "Current Form:", Value = FormationText });
            for (int k = 0; k < 6; k++)
            {
                int formation = k;
                page.Items.Add(new MenuItem
                {
                    Type = 5, Text = FormationNames[k],
                    Act = () => { _commandStatus[0] = 0; if (_director != null) _director.SetFormation(0, formation);
                                  if (Mw2Audio.Instance != null) Mw2Audio.Instance.LanceReply(2, 0); }
                });
            }
            page.Items.Add(ExitItem());
            return page;
        }

        string FormationText() => FormationNames[(_director != null ? _director.FormationOf(0) : 5) % FormationNames.Length];

        /// <summary>
        /// 0x100079c6 + 0x10007b8d: the member's pilot state + 1 from list 0x10089670 and, unless the state is 5
        /// (in formation), the name of its pilot target (nav, unit or structure); a missing member reads "None ".
        /// </summary>
        string MemberStatus(int position)
        {
            var ai = LanceMember(position);
            if (ai == null) return PilotStateNames[0];
            if (IsDown(ai)) return PilotStateNames[13];
            int state = (int)ai.PilotState;
            string text = PilotStateNames[Mathf.Clamp(state + 1, 0, PilotStateNames.Length - 1)];
            if (state == (int)AiState.Follow || ai.PilotTarget == null) return text;
            var t = ai.PilotTarget;
            string name = t.Kind == AiTarget.TargetKind.Nav ? t.Nav.Name
                        : t.Kind == AiTarget.TargetKind.Unit ? (t.Unit != null ? t.Unit.DisplayName : null)
                        : t.Structure != null ? t.Structure.name : null;
            return text + name;
        }

        /// <summary>
        /// 0x10007a7f..0x10007b60 -> FUN_1000b7aa: store the status, then send the command. Position 0 is the player,
        /// which broadcasts to the whole team (0x4200). Attack and defend take the player's target (+0xdc): none, or the
        /// member itself, cancels the message (flag 0x1000); attack also refuses a nav target (0x100 -> 0xffff).
        /// </summary>
        void Command(int position, int status, int command)
        {
            if (position < _commandStatus.Length) _commandStatus[position] = status;
            AiTarget target = null;
            if (command == 2 || command == 7)
            {
                if (_combat.Target != null && !_combat.Target.Destroyed) target = AiTarget.ForUnit(_combat.Target);
                else if (_combat.TargetStructure != null) target = AiTarget.ForStructure(_combat.TargetStructure); // mech +0xdc kind 0x400
                else if (command == 7 && _director != null && _combat.TargetNav >= 0 && _combat.TargetNav < _director.Navs.Count)
                    target = AiTarget.ForNav(_director.Navs[_combat.TargetNav]);
                if (target == null) return;
            }
            // Lance_SendCommand checks the selector and target, then ignores FUN_1000b57f's recipient count.
            bool delivered = position == 0;
            bool acceptNext = true;
            foreach (var ai in Lance)
            {
                if (position != 0 && ai.LancePosition != position) continue;
                if (position != 0 && target != null && target.Kind == AiTarget.TargetKind.Unit &&
                    target.Unit != null && target.Unit.gameObject == ai.gameObject) continue;
                delivered = true;
                // FUN_1000b57f: position 4 (gp+0xc), not GP type 4, disables this and later
                // recipients unless attack/join addressed that point individually.
                if (ai.LancePosition == 4 && (position == 0 || command != 2 && command != 5))
                    acceptNext = false;
                if (!acceptNext) continue;
                if (target != null && target.Kind == AiTarget.TargetKind.Unit && target.Unit != null &&
                    target.Unit.gameObject == ai.gameObject) continue;
                ai.LanceCommand(command, target);
            }
            // Lance_SendCommand 0x1000b7aa: attack 2 -> reply 1, join 5 -> reply 2,
            // defend 7 -> reply 3, shutdown 11 -> reply 9; engage-at-will 3 has no immediate reply.
            if (delivered && Mw2Audio.Instance != null)
            {
                int reply = command == 2 ? 1 : command == 5 ? 2 : command == 7 ? 3 : command == 8 ? 4 : command == 11 ? 9 : -1;
                if (reply >= 0) Mw2Audio.Instance.LanceReply(reply, position);
            }
        }

        /// <summary>Number shown before an item (0x1003cb59): the exit item 0, labels none, the others 1, 2, ...</summary>
        static int[] ItemNumbers(MenuPage page)
        {
            var numbers = new int[page.Items.Count];
            int n = 0;
            for (int i = 0; i < numbers.Length; i++)
                numbers[i] = page.Items[i].Type == 6 || page.Items[i].Type == 2 ? 0 : page.Items[i].Type == 3 ? -1 : ++n;
            return numbers;
        }

        /// <summary>
        /// Keys of window 1 (flags 0xa, no bit 1: 0x1003c6a0 passes only Esc and the digits). Esc leaves the page; a
        /// digit picks the item with that number (0 = exit) and acts as Enter: type 0 opens its page (sound +0x24 =
        /// 0xf5), type 5 runs its action and leaves the page, type 6 leaves. A changed selection plays +0x28 = 0xed.
        /// The original reaches the item through the digit minus one, shifted past a label row; that picks the same
        /// item as its shown number.
        /// Leaving the last page closes the window.
        /// </summary>
        void UpdateCommandMenu()
        {
            UpdateDirectStarCommands();
            // STARMATE_MENU 0x59, STARMATE_ONE_MENU 0x5a, STARMATE_TWO_MENU 0x5b (Input_Command 0x100267c0: Window_Toggle 1 / 7 / 8).
            if (Mw2Input.Command(0x59)) OpenCommandWindow(1);
            else if (Mw2Input.Command(0x5a)) OpenCommandWindow(7);
            else if (Mw2Input.Command(0x5b)) OpenCommandWindow(8);

            CommandMenuOpen = _menu.Count > 0;
            Mw2Input.WindowOpen = CommandMenuOpen || MainMenuOpen || SystemsStatusOpen;
            // Window 4 comes first in the window list (RegisterMenu 4, 5, 1, ...), so the main menu takes the keys.
            if (_menu.Count == 0 || MainMenuOpen || SystemsStatusOpen) return;
            foreach (int code in Mw2Input.MenuKeys)
            {
                // Menu_ReadKey 0x1003c6a0: Esc leaves the page, a digit picks its item (codes on key release).
                if (code == 0x1b) { _menu.RemoveAt(_menu.Count - 1); return; }
                int d = code - 0x30;
                var page = _menu[_menu.Count - 1];
                var numbers = ItemNumbers(page);
                int index = Array.IndexOf(numbers, d);
                if (index < 0) return;
                if (index != page.Selected) PlayMenuSound(0xed);
                page.Selected = index;
                var item = page.Items[index];
                switch (item.Type)
                {
                    case 0:
                        if (item.Open != null && _menu.Count < 8) { _menu.Add(item.Open()); PlayMenuSound(0xf5); }
                        break;
                    case 5:
                        item.Act?.Invoke();
                        _menu.RemoveAt(_menu.Count - 1);
                        break;
                    case 6:
                        _menu.RemoveAt(_menu.Count - 1);
                        break;
                }
                return;
            }
        }

        /// <summary>Commands 0x59..0x5b clear the objectives screen (DAT_100b18fc = 0) before toggling the window.</summary>
        void OpenCommandWindow(int window)
        {
            ObjectivesShown = false;
            ToggleCommandWindow(window);
        }

        void PlayMenuSound(int id)
        {
            if (Mw2Audio.Instance != null) Mw2Audio.Instance.Play(id, transform.position, true);
        }

        /// <summary>
        /// 0x1003cb59 for window 1: the box is (0.22, 0.3)-(1.0, 0.6) of the screen (0x1008abf0), 8 lines, so a row is
        /// 1/10 of the box height (0x1003c05e). Title at (0, 0.05) in colour 0x0e, underlined in colour 1 (0x1004b040);
        /// rows from y 0.2: the number at x 0.02, the text after the width of "00." and the value at x 0.4; normal and
        /// selected colours are both 0x0e and there is no background or highlight sprite.
        /// </summary>
        void DrawCommandMenu()
        {
            if (_menu.Count == 0 || SystemsStatusOpen) return;
            var page = _menu[_menu.Count - 1];
            float x0 = 0.22f * Screen.width, y0 = 0.3f * Screen.height;
            float w = Screen.width - x0, h = 0.6f * Screen.height - y0;
            float row = h / 10f;

            DrawText(page.Title, new Vector2(x0, y0 + 0.05f * h), 0x0e);
            DrawUnderline(x0, y0 + 0.05f * h, TextWidth(page.Title), 1);

            float numberX = x0 + 0.02f * w, textX = numberX + TextWidth("00."), valueX = x0 + 0.4f * w;
            var numbers = ItemNumbers(page);
            for (int i = 0; i < page.Items.Count; i++)
            {
                var item = page.Items[i];
                float y = y0 + 0.2f * h + i * row;
                if (numbers[i] >= 0) DrawText(numbers[i].ToString(), new Vector2(numberX, y), 0x0e);
                DrawText(item.Text, new Vector2(textX, y), 0x0e);
                if (item.Value != null) DrawText(item.Value() ?? "", new Vector2(valueX, y), 0x0e);
            }
        }
    }
}
