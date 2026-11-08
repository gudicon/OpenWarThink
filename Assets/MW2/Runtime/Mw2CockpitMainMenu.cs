using System;
using System.Collections.Generic;
using MW2.Formats;
using UnityEngine;

namespace MW2
{
    /// <summary>
    /// The in-game main menu: MAIN_MENU (command 0x4a, Esc in gamekey.map) opens window 4 (Window_Open 0x1003bf5a; only
    /// when no window is open, DAT_1023c8dc). The window and its pages are static data of mercsw.dll: descriptor
    /// 0x10099e68 (table 0x1009c210), root page "MAIN MENU" 0x10099ba0:
    ///   Abort Mission    -> "ABORT MISSION" 0x100993d8: Confirmation requested / Accept (Esc to cancel) (0x1001f870)
    ///   Audio Ctrl       -> "SET AUDIO VOLUME" 0x1009f818: Music, Sound Effects, Voice sliders (Menu_DrawSlider 0x1003d310)
    ///   Combat Variables -> "COMBAT VARIABLES" 0x1009dd08: Explosion chunks, Particles, Multiple Lights, Shadows Off/On
    ///                       (Menu_DrawValueCycle 0x1003daaa)
    ///   Flee to Windows  -> "FLEE TO WINDOWS" 0x10099908: Confirm your cowardice / Accept (Esc to cancel) (0x1001f908)
    ///   Accept (Esc to cancel)
    /// Engine (Window_RunActive 0x1003c8c8 -> Window_Draw 0x1003c9ba -> Menu_DrawPage 0x1003cb59, keys Menu_ReadKey
    /// 0x1003c6a0 / Menu_KeyToAction 0x1003ca92): window flags 0x11 (1: arrows, Enter, Tab and the menu_ channels, and the
    /// input holds only menu_ channels while open; 0x10: the background's left edge on the window's), 9 lines, colours
    /// 0xfa / 0x0a (selected), font 1 + resolution (BASE6X76), background SHP 0xe8 + 1 (WESCSIM6), sounds 0xed (selection)
    /// and 0xf5 (sub-page). While it is open the sim is in the pause mode (FUN_10047a1d 0x10047a1d, Mw2Pause).
    /// Approximations: the original keeps windows in a list and runs the first open one; here the main menu takes the keys
    /// before the lance menu (window 4 is registered first). MissionLeft hands the result to the campaign shell.
    /// </summary>
    public partial class Mw2Cockpit
    {
        [Header("Main menu (window 4, 0x10099e68)")]
        public HudSprite MenuBackground;    // WESCSIM6 (SHP 0xe8 + 1)
        // Slider sprites, list 0x10099368: MENDL6, MBAR6, MENDR6, MSLIDE6 (SHP 0x2b, 0x28, 0x2e, 0x3a + 1).
        public HudSprite SliderLeft, SliderBar, SliderRight, SliderKnob;

        /// <summary>Menu item (0x14 bytes): type, text, draw function, value record, sub-page.
        /// Types: 0 sub-page, 1 handled by its draw function, 2 accept and back, 3 label, 5 action and back, 6 exit.</summary>
        sealed class MainItem
        {
            public int Type;
            public string Text;
            public MainValue Value;
            public Action Confirm;      // 0x1001f870 / 0x1001f908: run on Enter or its digit
            public MainPage Sub;
        }

        /// <summary>
        /// Value record: [1] value, [2] list (the strings, or the slider sprites), [3] argument, [5] getter, [6] live
        /// function (each frame while selected), [7] accept, [8] cancel. Sliders run 0..0x10000 in steps of 0x199a.
        /// </summary>
        sealed class MainValue
        {
            public bool Slider;
            public bool Refresh;
            public string[] List;
            public int Arg;
            public int Value;
            public Func<int, int> Get;
            public Action<int, int> Live, Accept;
            public Action<int> Cancel;
        }

        sealed class MainPage
        {
            public string Title;
            public int State;           // +0: 0 -> 1 set up values, 2 running, 3 open sub-page, 4 accept, 5 cancel
            public int Selected;        // +0x10
            public readonly List<MainItem> Items = new List<MainItem>();
        }

        const int MainLines = 9;                                    // descriptor [0xf]
        const int MainNormal = 0xfa, MainSelected = 0x0a;           // [0xd], [0xe]
        const int MainSoundMove = 0xed, MainSoundOpen = 0xf5;       // [10], [9]

        MainPage _mainRoot;
        MainPage _systemsRoot;
        public bool SystemsStatusOpen { get; private set; }
        readonly List<MainPage> _mainStack = new List<MainPage>();  // [2] / [3], at most 8 pages (0x1003d235)
        int _menuKey;               // DAT_1023c8e8
        double _menuClock;          // clock DAT_1009c208 (ticks, not stopped by the pause mode)
        bool _windowsFreed;         // FUN_1003c003: every window unregistered (after Abort / Flee)
        int _leaveFrames = -1;      // SimMain 0x100465ae: frames until mercsw.dll returns after DAT_100aea38
        bool _fled;                 // DAT_100a6bf8: Flee to Windows, mercsw.dll returns 0xff and the shell quits
        int _exitAt;                // DAT_100a7084: tick of the end of "Exiting Mission Area..."
        int _radioExitAt;           // DAT_100a7088: priority-preserving radio cleanup deadline.
        bool _exitMode;             // DAT_100a7098: only CTRL-Q (command 0x71) is still accepted
        bool _exitRequestSeen;      // DAT_100a70b0 consumed (Mission_CheckObjectives clears the two timers once)
        bool _left;                 // the mission has been handed back

        /// <summary>
        /// Where the mission goes once mercsw.dll would return (SimMain 0x100465ae after "Calling EndTheMission"): the
        /// campaign shell takes over when it launched the mission; without it Play mode stops. The flag is true after
        /// Flee to Windows (return code 0xff).
        /// </summary>
        public static Action<bool> MissionLeft;

        public static bool MainMenuOpen => Mw2Pause.MainMenuOpen;

        MainPage BuildMainMenu()
        {
            MainItem Accept() => new MainItem { Type = 2, Text = "Accept (Esc to cancel)" };
            MainValue Volume(int arg) => new MainValue
            {
                Slider = true, Arg = arg, Get = Mw2Options.VolumeGet, Live = Mw2Options.VolumePreview,
                Accept = Mw2Options.VolumeAccept, Cancel = Mw2Options.VolumeCancel
            };
            string[] offOn = { "Off", "On" };
            MainValue Toggle(Func<int> get, Action<int> set) => new MainValue
            {
                List = offOn, Get = _ => get(), Accept = (_, v) => set(v)
            };

            var abort = new MainPage { Title = "ABORT MISSION" };
            abort.Items.Add(new MainItem { Type = 3, Text = "Confirmation requested" });
            abort.Items.Add(new MainItem { Type = 1, Text = "Accept (Esc to cancel)", Confirm = AbortMission });

            var audio = new MainPage { Title = "SET AUDIO VOLUME" };
            audio.Items.Add(new MainItem { Type = 1, Text = "Music", Value = Volume(2) });
            audio.Items.Add(new MainItem { Type = 1, Text = "Sound Effects", Value = Volume(0) });
            audio.Items.Add(new MainItem { Type = 1, Text = "Voice", Value = Volume(1) });
            audio.Items.Add(Accept());

            var combat = new MainPage { Title = "COMBAT VARIABLES" };
            combat.Items.Add(new MainItem { Type = 1, Text = "Explosion chunks", Value = Toggle(Mw2Options.ChunksGet, Mw2Options.ChunksSet) });
            combat.Items.Add(new MainItem { Type = 1, Text = "Particles", Value = Toggle(Mw2Options.ParticlesGet, Mw2Options.ParticlesSet) });
            combat.Items.Add(new MainItem { Type = 1, Text = "Multiple Lights", Value = Toggle(Mw2Options.LightsGet, Mw2Options.LightsSet) });
            combat.Items.Add(new MainItem { Type = 1, Text = "Shadows", Value = Toggle(Mw2Options.ShadowsGet, Mw2Options.ShadowsSet) });
            combat.Items.Add(Accept());

            var flee = new MainPage { Title = "FLEE TO WINDOWS" };
            flee.Items.Add(new MainItem { Type = 3, Text = "Confirm your cowardice" });
            flee.Items.Add(new MainItem { Type = 1, Text = "Accept (Esc to cancel)", Confirm = FleeToWindows });

            var root = new MainPage { Title = "MAIN MENU" };
            root.Items.Add(new MainItem { Type = 0, Text = "Abort Mission", Sub = abort });
            root.Items.Add(new MainItem { Type = 0, Text = "Audio Ctrl", Sub = audio });
            root.Items.Add(new MainItem { Type = 0, Text = "Combat Variables", Sub = combat });
            root.Items.Add(new MainItem { Type = 0, Text = "Flee to Windows", Sub = flee });
            root.Items.Add(Accept());
            return root;
        }

        // Window 5 descriptor 100a0f28/root 100a0c60. The typo is in the original DLL.
        MainPage BuildSystemsStatus()
        {
            MainValue Option(int arg) => new MainValue { Arg = arg, List = new[] { "Off", "On" }, Refresh = true,
                Get = SystemOptionGet, Accept = SystemOptionSet };
            var root = new MainPage { Title = "SYSTEMS STATUS" };
            root.Items.Add(new MainItem { Type = 4, Text = "Light Amplification", Value = Option(0xbf) });
            root.Items.Add(new MainItem { Type = 4, Text = "Image Emhancement", Value = Option(0xc0) });
            root.Items.Add(new MainItem { Type = 4, Text = "HUD", Value = Option(0x2a) });
            root.Items.Add(new MainItem { Type = 4, Text = "Auto Eject", Value = Option(0x53) });
            root.Items.Add(new MainItem { Type = 6, Text = "(Esc to exit)" });
            return root;
        }

        /// <summary>Option_Get 1006a060. HUD is a 0/1/2 state, displayed modulo the two-value list.</summary>
        public int SystemOptionGet(int arg)
        {
            switch (arg)
            {
                case 0x2a: return HudState;
                case 0x53: return Mw2MechCombat.AutomaticEjection ? 1 : 0;
                case 0xbf: return Mw2PaletteFade.Instance != null && Mw2PaletteFade.Instance.InfraredOn ? 1 : 0;
                case 0xc0: return Mw2ImageEnhancement.Enabled ? 1 : 0;
                default: return 0;
            }
        }

        /// <summary>Option_Set 1006a18d writes directly: no keyboard HUD reset, ejection toast or toggle sound.</summary>
        public void SystemOptionSet(int arg, int value)
        {
            switch (arg)
            {
                case 0x2a: HudState = value; break;
                case 0x53: Mw2MechCombat.AutomaticEjection = value != 0; break;
                case 0xbf: if (Mw2PaletteFade.Instance != null) Mw2PaletteFade.Instance.SetInfrared(value != 0); break;
                case 0xc0:
                    Mw2ImageEnhancement.Enabled = value != 0;
                    if (value != 0 && Mw2Audio.Instance != null) Mw2Audio.Instance.Computer(0x1b);
                    break;
            }
        }

        /// <summary>
        /// Per frame, before the lance menu: MAIN_MENU opens the window (Input_Command case 0x4a; commands do not run in
        /// the pause mode); FUN_1003c3a9 then starts the page stack at the root, resets the key clock and puts the input in
        /// menu mode; Menu_ReadKey takes this frame's key and Menu_DrawPage runs the top page. An empty stack closes the
        /// window (Window_Draw 0x1003c9ba, FUN_1003c471 restores the input).
        /// </summary>
        void UpdateMainMenu()
        {
            UpdateLeaving();
            if (_mainRoot == null) _mainRoot = BuildMainMenu();
            if (_systemsRoot == null) _systemsRoot = BuildSystemsStatus();
            if (!MainMenuOpen && Mw2Input.Command(0x4b) && !_windowsFreed)
            {
                if (SystemsStatusOpen) { CloseMainMenu(); return; }
                if (_menu.Count > 0) return; // Window_Open refuses another open window.
                SystemsStatusOpen = true;
                _mainStack.Clear(); _mainStack.Add(_systemsRoot); _menuClock = 0;
                Mw2Input.WindowOpen = true; Mw2Input.WindowTakesArrows = false;
                return;
            }
            if (!MainMenuOpen && !SystemsStatusOpen)
            {
                if (_windowsFreed || _menu.Count > 0 || !Mw2Input.Command(0x4a)) return;
                Mw2Pause.MainMenuOpen = true;
                _mainStack.Clear();
                _mainStack.Add(_mainRoot);
                _menuClock = 0;
                Mw2Input.MenuMode = true;
                Mw2Input.WindowOpen = true;
                Mw2Input.WindowTakesArrows = true;
                return;
            }
            _menuClock += Time.unscaledDeltaTime * MechMotion.TicksPerSecond;
            ReadMainMenuKey();
            RunMainPage();
            if (_mainStack.Count == 0) CloseMainMenu();
        }

        void CloseMainMenu()
        {
            SystemsStatusOpen = false;
            Mw2Pause.MainMenuOpen = false;
            _mainStack.Clear();
            Mw2Input.MenuMode = Mw2Pause.PauseMode;   // FUN_1002ca4b; the pause mode keeps menu input until it ends
            Mw2Input.WindowOpen = _menu.Count > 0;
            Mw2Input.WindowTakesArrows = false;
        }

        /// <summary>
        /// Menu_ReadKey 0x1003c6a0 for a window with flag 1: a key code from the queue (Esc, digits, Enter, Tab, Shift+Tab,
        /// space, arrows) or, without one, menu_enter (+0x9e) -> Enter, menu_abort (+0x9f) -> Esc, after 0x5a ticks
        /// menu_item (+0x90) below -8192 -> up 0xc6 / above 8192 -> down 0xc7, after 0x2d ticks menu_value (+0x94) below
        /// -8192 -> 0xc9 / above 8192 -> space. Any key restarts the clock and sets menu_item_reset / menu_value_reset.
        /// </summary>
        void ReadMainMenuKey()
        {
            var keys = Mw2Input.MenuKeys;
            _menuKey = keys.Count > 0 ? keys[0] : 0;
            // Window 5 flags=2: Esc/digits only, no arrows/menu_ controls and no pause mode.
            if (SystemsStatusOpen)
            {
                if (_menuKey != 0x1b && !IsDigit(_menuKey)) _menuKey = 0;
                return;
            }
            if (_menuKey == 0)
            {
                if (Mw2Input.Held(0x9e)) _menuKey = 0x0d;
                else if (Mw2Input.Held(0x9f)) _menuKey = 0x1b;
                if (_menuKey == 0 && _menuClock > 0x5a)
                {
                    float item = Mw2Input.Float(0x90);
                    if (item < -8192f) _menuKey = 0xc6;
                    else if (item > 8192f) _menuKey = 0xc7;
                }
                if (_menuKey == 0 && _menuClock > 0x2d)
                {
                    float value = Mw2Input.Float(0x94);
                    if (value < -8192f) _menuKey = 0xc9;
                    else if (value > 8192f) _menuKey = 0x20;
                }
            }
            if (_menuKey != 0)
            {
                _menuClock = 0;
                Mw2Input.Set(0x9a);
                Mw2Input.Set(0x9d);
            }
        }

        static bool IsDigit(int key) => key > 0x2f && key < 0x3a;

        /// <summary>Menu_KeyToAction 0x1003ca92: Enter on type 0 -> 3, 2 -> 4, 5 / 6 -> 5; Esc -> 5; Tab and down 0xc7
        /// step +1, up 0xc6 and Shift+Tab step -1.</summary>
        static void MainKeyToAction(int key, int type, MainPage page, ref int step)
        {
            if (key == 0x0d)
            {
                if (type == 0) page.State = 3;
                else if (type == 2) page.State = 4;
                else if (type == 5 || type == 6) page.State = 5;
            }
            else if (key == 9 || key == 0xc7) step = 1;
            else if (key == 0x1b) page.State = 5;
            else if (key == 0xc6 || key == 0x209) step = -1;
        }

        /// <summary>
        /// Menu_DrawPage 0x1003cb59 without the drawing: page state, the key (a digit picks the item with that number and
        /// acts as Enter: 0 the exit row; past the label row the key code is moved up one, so the value columns, which
        /// compare their row with key - '1', see the right row), selection steps wrapping round and skipping a label, the
        /// value columns and confirm items, then sub-page (3) or back (4 / 5: state 0, selection 0, page popped).
        /// </summary>
        void RunMainPage()
        {
            if (_mainStack.Count == 0) return;
            var page = _mainStack[_mainStack.Count - 1];
            var items = page.Items;
            int n = items.Count;
            int exit = -1, label = 0x20, hasLabel = 0;
            for (int i = 0; i < n; i++)
            {
                if (items[i].Type == 2 || items[i].Type == 6) exit = i;
                if (items[i].Type == 3) { label = i; hasLabel = 1; }
            }
            if (exit == -1) hasLabel = 0;
            int sel = page.Selected, step = 0, key24 = _menuKey;
            if (page.State == 0) page.State = 1;
            else
            {
                if (page.State == 1) page.State = 2;
                if (_menuKey != 0)
                {
                    if (IsDigit(_menuKey))
                    {
                        int d = _menuKey - 0x30;
                        if (d == 0)
                        {
                            if (exit != -1) { sel = exit; key24 = 0x0d; }
                        }
                        else if (d > 0 && d < n - hasLabel)
                        {
                            if (label < d)
                            {
                                if (++_menuKey > 0x39) _menuKey = 0x30;
                                sel = d;
                            }
                            else sel = _menuKey - 0x31;
                            key24 = 0x0d;
                        }
                    }
                    MainKeyToAction(key24, items[sel].Type, page, ref step);
                    if (step != 0) sel = (sel + step + n) % n;
                    if (sel != page.Selected) PlayMenuSound(MainSoundMove);
                }
            }
            if (step == 0) step = 1;
            if (items[sel].Type == 3)
            {
                sel = (sel + step + n) % n;
                if (IsDigit(_menuKey))
                {
                    if (++_menuKey > 0x39) _menuKey = 0x30;
                    MainKeyToAction(key24, items[sel].Type, page, ref step);
                }
            }
            page.Selected = sel;

            for (int i = 0; i < n; i++)
            {
                var item = items[i];
                if (item.Value != null) RunMainValue(page, i, item);
                if (item.Confirm != null && (i == page.Selected && _menuKey == 0x0d || i == _menuKey - 0x31))
                {
                    item.Confirm();
                    return;
                }
            }

            if (page.State == 3)
            {
                page.State = 2;
                var sub = items[page.Selected].Sub;
                if (sub != null && _mainStack.Count < 8)
                {
                    _mainStack.Add(sub);
                    PlayMenuSound(MainSoundOpen);
                }
            }
            else if (page.State == 4 || page.State == 5)
            {
                page.State = 0;
                page.Selected = 0;
                _mainStack.RemoveAt(_mainStack.Count - 1);
            }
        }

        /// <summary>
        /// The value column of a row, Menu_DrawSlider 0x1003d310 / Menu_DrawValueCycle 0x1003daaa: state 1 reads the getter;
        /// state 2 on the selected row steps the value up on space / right 0xc8 / its digit and down on left 0xc9 (sliders by
        /// 0x199a within 0..0x10000, lists wrapping round); state 4 accepts, 5 cancels; the selected row then runs its live
        /// function unless accepting or cancelling (and accepts a step at once for row type 4, or its digit for type 5).
        /// </summary>
        void RunMainValue(MainPage page, int row, MainItem item)
        {
            var v = item.Value;
            bool digit = row == _menuKey - 0x31;
            int value = v.Refresh && v.Get != null ? v.Get(v.Arg) : v.Value, delta = 0;
            bool live = true;
            switch (page.State)
            {
                case 1:
                    value = v.Get != null ? v.Get(v.Arg) : 0;
                    v.Value = value;
                    break;
                case 2:
                    if (row == page.Selected)
                    {
                        if (_menuKey == 0x20 || _menuKey == 0xc8) delta = 1;
                        else if (_menuKey == 0xc9) delta = -1;
                        else if (digit) delta = 1;
                    }
                    break;
                case 4:
                    v.Accept?.Invoke(v.Arg, value);
                    live = false;
                    break;
                case 5:
                    v.Cancel?.Invoke(v.Arg);
                    live = false;
                    break;
            }
            if (v.Slider) value = Mathf.Clamp(value + delta * 0x199a, 0, 0x10000);
            else if (v.List != null && v.List.Length > 0) value = (value + delta + v.List.Length) % v.List.Length;
            if (row == page.Selected)
            {
                if ((delta != 0 && item.Type == 4 || digit && item.Type == 5)) v.Accept?.Invoke(v.Arg, value);
                if (live) v.Live?.Invoke(v.Arg, value);
            }
            v.Value = value;
        }

        /// <summary>
        /// 0x1001f870 (Abort Mission, Accept): options byte +1 cleared, Input_Command(0x71) sets the quit flag DAT_100aea38
        /// and adds 2 to the frame counter DAT_100aea3c, every window is unregistered (FUN_1003c003); SimMain returns to
        /// the shell once the counter passes 2.
        /// </summary>
        void AbortMission()
        {
            FreeWindows();
            _leaveFrames = 1;
        }

        /// <summary>
        /// 0x1001f908 (Flee to Windows, Accept): DAT_100a6bf8 = 1, Mech_Eject(player, 0) (line 4, then Mech_Destroy),
        /// every window unregistered. Input_HandleCommands 0x10026391 then shows "Exiting Mission Area..." (0x1536 ticks)
        /// and sets the quit flag 0x16a ticks later; mercsw.dll returns 0xff and the shell quits to Windows.
        /// </summary>
        void FleeToWindows()
        {
            FreeWindows();
            _fled = true;
            if (_combat != null) _combat.Destruct();
        }

        void FreeWindows()
        {
            _windowsFreed = true;
            _menu.Clear();
            CloseMainMenu();
        }

        /// <summary>
        /// Input_HandleCommands 0x10026391, the single-player exit sequence (DAT_100a6c88 = 0). After the death camera
        /// (DAT_100ad360), the decided mission (DAT_100a70b0) or Flee to Windows (DAT_100a6bf8) it shows "Exiting Mission
        /// Area..." for 0x1536 ticks and sets the quit flag DAT_100aea38 0x16a ticks later; until then only command 0x71
        /// (CTRL-Q) leaves at once. Outside the sequence command 0x71 leaves as well (Input_Command 0x100267c0). The radio
        /// clean-up after 0x108 ticks is FUN_100105c5(1), preserving important queued speech.
        /// </summary>
        void UpdateLeaving()
        {
            if (_left) return;
            if (_leaveFrames < 0)
            {
                int now = (int)((_director != null ? _director.MissionTime : Time.timeSinceLevelLoad) * MechMotion.TicksPerSecond);
                if (_radioExitAt > 0 && _radioExitAt < now)
                {
                    if (Mw2Audio.Instance != null) Mw2Audio.Instance.PruneRadioForExit();
                    _radioExitAt = 0;
                }
                bool requested = _director != null && _director.ExitRequested;
                if (requested && !_exitRequestSeen) { _exitRequestSeen = true; _exitAt = 0; _exitMode = false; }
                if (_exitAt == 0 && !_exitMode)
                {
                    if (requested || _fled || (_deathCamera && !Mw2MechCombat.NetGame))
                    {
                        _exitMode = true;
                        _exitAt = now + 0x16a;
                        _radioExitAt = now + 0x108;
                        ShowMessage("Exiting Mission Area...", 0x1536);
                    }
                }
                else if (_exitAt > 0 && _exitAt < now) _leaveFrames = 2;
                if (_leaveFrames < 0 && Mw2Input.Command(0x71)) _leaveFrames = 2;
                return;
            }
            if (_leaveFrames-- > 0) return;
            _left = true;
            if (MissionLeft != null) { MissionLeft(_fled); return; }
            Debug.Log("MW2: mission left (no campaign shell)");
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }

        /// <summary>
        /// The drawing of Window_Draw 0x1003c9ba / Menu_DrawPage 0x1003cb59 for window 4 (Window_Layout 0x1003c05e):
        /// the window is (0.25, 0.2)-(1.0, 0.8) of the screen; the background pane is the sprite's size centred on the
        /// screen (0x1004ace5) with its left edge moved to the window's (flag 0x10). A row is 1/(9 + 2) of the window height;
        /// the title at (0.04, 1/22) in 0xfa, underlined in colour 1 (Font_Underline 0x1004b040); rows from 2/11: the number
        /// (exit 0, labels none, others 1, 2, ...) at x 0.04, the text after the width of "00.", the value at x 0.47; the
        /// selected row in 0x0a; the exit row goes to the last line (flag 8 clear).
        /// </summary>
        void DrawMainMenu()
        {
            if ((!MainMenuOpen && !SystemsStatusOpen) || _mainStack.Count == 0) return;
            float x0 = (SystemsStatusOpen ? 0x8ccd / 65536f : 0.25f) * Screen.width;
            float y0 = (SystemsStatusOpen ? 0x547b / 65536f : 0.2f) * Screen.height;
            float w = Screen.width - x0, h = (SystemsStatusOpen ? 0x9eb8 / 65536f : 0.8f) * Screen.height - y0;
            if (!SystemsStatusOpen && MenuBackground != null && MenuBackground.Texture != null)
            {
                float bw = MenuBackground.Texture.width * Px, bh = MenuBackground.Texture.height * Py;
                GUI.DrawTexture(new Rect(x0 - MenuBackground.HotX * Px, Screen.height * 0.5f - bh / 2f - MenuBackground.HotY * Py, bw, bh),
                                MenuBackground.Texture);
            }
            var page = _mainStack[_mainStack.Count - 1];
            int lines = SystemsStatusOpen ? 6 : MainLines;
            float row = h / (lines + 2);
            float titleX = x0 + (SystemsStatusOpen ? 0 : 0.04f) * w;
            float titleY = y0 + row / 2f; // Window_Layout replaces descriptor y values at runtime.
            DrawText(page.Title, new Vector2(titleX, titleY), SystemsStatusOpen ? 0x0e : MainNormal);
            DrawUnderline(titleX, titleY, TextWidth(page.Title), 1);

            int exit = -1;
            for (int i = 0; i < page.Items.Count; i++) if (page.Items[i].Type == 2 || page.Items[i].Type == 6) exit = i;
            float numberX = x0 + (SystemsStatusOpen ? 0x51f / 65536f : 0.04f) * w;
            float textX = numberX + TextWidth("00."), valueX = x0 + (SystemsStatusOpen ? 0xcccd / 65536f : 0.47f) * w;
            float y = y0 + 2f * row;
            int number = 0;
            for (int i = 0; i < page.Items.Count; i++)
            {
                var item = page.Items[i];
                int color = SystemsStatusOpen ? 0x0e : i == page.Selected ? MainSelected : MainNormal;
                float rowY = y + i * row + (i == exit ? (lines - i - 1) * row : 0f);
                if (item.Type != 3)
                {
                    number = i == exit ? 0 : number + 1;
                    DrawText(number.ToString(), new Vector2(numberX, rowY), color);
                }
                DrawText(item.Text, new Vector2(textX, rowY), color);
                var v = item.Value;
                if (v == null) continue;
                if (v.Slider) DrawSlider(valueX, rowY + FontHeight / 2 * Py, v.Value);
                else if (v.List != null && v.List.Length > 0) DrawText(v.List[v.Value % v.List.Length], new Vector2(valueX, rowY), color);
            }
        }

        /// <summary>Menu_DrawSlider 0x1003d310: left end, bar, right end, and the knob centred at value * bar width
        /// (FUN_100076a0) along the bar, all at half a font height below the row.</summary>
        void DrawSlider(float x, float y, int value)
        {
            if (SliderBar == null || SliderBar.Texture == null || SliderKnob == null || SliderKnob.Texture == null) return;
            float bar = x;
            if (SliderLeft != null && SliderLeft.Texture != null)
            {
                PutSprite(SliderLeft, new Vector2(x, y));
                bar = x + SliderLeft.Texture.width * Px;
            }
            int barWidth = SliderBar.Texture.width;
            PutSprite(SliderBar, new Vector2(bar, y));
            if (SliderRight != null && SliderRight.Texture != null) PutSprite(SliderRight, new Vector2(bar + barWidth * Px, y));
            int offset = (int)((long)value * barWidth >> 16);
            PutSprite(SliderKnob, new Vector2(bar + (offset - SliderKnob.Texture.width / 2) * Px, y));
        }
    }
}
