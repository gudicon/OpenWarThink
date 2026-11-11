using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using MW2.Formats;
using UnityEngine;

namespace MW2.Shell
{
    /// <summary>
    /// The Mercenaries shell (MW2SHELL.DLL ShellMain 0x100428d3 and ShellWindowProc 0x100408c7) on a 640x480 8-bit screen.
    /// Each loop pass handles one posted message, reads the mouse (FUN_1004c5ff 0x1004c5ff), forms the screen event (every
    /// second pass the F-key table 0x100a6a78 via FUN_10027ce0 0x10027ce0, otherwise 0xc004 "tick"), calls the current screen
    /// function (FUN_10044006 0x10044006) and shows the dirty part of the screen (FUN_10077849). Screens register their function
    /// (FUN_10043f22) and leave by posting the next screen message; an animation can hold the screen until it ends
    /// (FUN_1000206d / FUN_10043f7f / FUN_10043faa).
    /// </summary>
    public sealed class Mw2Shell : MonoBehaviour
    {
        public string GameFolder = Mw2GameFolder.Default;
        public static Mw2Shell Instance;

        public ShellDatabase Db, Fix;       // DAT_100cd0c8, DAT_100cd0cc
        public ShellSurface Surface;        // DAT_100cd0c0
        public ShellAnimList Anims;         // DAT_100cd0c4
        public PrjArchive Prj;              // DAT_100cd0d0
        /// <summary>Fonts of ShellMain: records 5 (DAT_100cd0dc first), 6 (DAT_100cd0e0), 7 (DAT_100cd0e4 = DAT_100cd0f8),
        /// 9 (DAT_100cd0e8), 10 (DAT_100cd0ec), 11 (DAT_100cd0f4 = DAT_100cd0f0 = DAT_100cd0dc = DAT_100cd0d8).</summary>
        public ShellFont Font5, Font6, Font7, Font9, Font10, Font11;
        public Career Career = Career.Initial();
        public ShellData Data;               // PRJ, game folder and MW2SHELL.DLL tables
        /// <summary>MSVCRT rand() of the shell (kept in ShellData so the random mission generator shares it).</summary>
        public int Rand() => Data.Random.Rand();

        /// <summary>MSVCRT srand.</summary>
        public void Srand(uint seed) => Data.Random.Srand(seed);
        /// <summary>DAT_100cd478: counted up by screen ticks, the seed of srand when a screen ends.</summary>
        public int TickCounter;

        // Mouse object DAT_100cd0bc (FUN_1004c390): +0x2b x, +0x2f y, +0x33 left down, +0x10 left pressed this pass,
        // +0x1c double click (second press within 200 ms, 0xc9).
        public int MouseX, MouseY;
        public bool MouseLeft, MouseClicked, MouseDouble;
        /// <summary>+0x14: the right button pressed this pass.</summary>
        public bool MouseRightClicked;
        bool _mouseRight;
        long _lastClickTime;

        // Keyboard object DAT_100cd0b4: last virtual key (WM_KEYDOWN) and the 64-entry key ring (FUN_100356f9 / FUN_10035a74).
        public int LastVk;
        readonly Queue<int> _keyRing = new Queue<int>();

        readonly Queue<(uint msg, uint wParam)> _posted = new Queue<(uint, uint)>();
        /// <summary>DAT_100cf3b4: the screen function, called with the event.</summary>
        public Action<uint> Handler;
        Action<uint> _heldHandler;      // DAT_100cf3b8
        ShellAnim _heldAnim;            // DAT_100cf3c0
        uint _heldMessage;              // DAT_100cf3a0
        int _passCounter;               // DAT_100cf3d4
        ShellSound _blockingSound;

        /// <summary>DAT_100cd474: a full-screen flic plays in 320x200.</summary>
        public bool LowRes;
        /// <summary>The window's menu (MENU 104), see ShellMenu and MenuCommand.</summary>
        public ShellMenu Menu = new ShellMenu();
        ShellFlic _flic;
        bool _launching;
        Mw2LaunchScreen _launchScreen;

        Texture2D _screen;
        Color32[] _pixels;
        readonly Stopwatch _clock = Stopwatch.StartNew();

        sealed class Stopwatch
        {
            readonly System.Diagnostics.Stopwatch _w = System.Diagnostics.Stopwatch.StartNew();
            public static Stopwatch StartNew() => new Stopwatch();
            public long Ms => _w.ElapsedMilliseconds;
        }

        public long Now => _clock.Ms;

        void Awake()
        {
            if (Autopilot.MissionTestRunner.Bootstrap()) { enabled = false; return; }
            if (Mw2NetShell.Bootstrap()) { enabled = false; return; }
            Instance = this;
        }

        /// <summary>ShellMain: databases, screen, fonts, PRJ, the intro flic, then the title screen (0xc02b).</summary>
        void Start()
        {
            // Unity installation adapter, not found in the original ShellMain 100428d3:
            // permit user-owned data outside the developer's serialized scene path.
            GameFolder = PlayerPrefs.GetString("MW2.GameFolder", GameFolder);
            var args = Environment.GetCommandLineArgs();
            int at = Array.IndexOf(args, "--game-folder");
            if (at >= 0)
            {
                if (at + 1 >= args.Length || args[at + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    FolderFailure("Missing value after --game-folder");
                    return;
                }
                GameFolder = args[at + 1];
            }
            OpenGameFolder();
        }

        string _folderError;
        static bool _outsideTablesLogged;

        // Installation adapter for ShellMain 100428d3; files are always opened read only.
        void OpenGameFolder()
        {
            try
            {
                GameFolder = Path.GetFullPath(GameFolder);
                // The two libraries are needed only to extract tables that are not in the store yet.
                var required = new List<string> { "MW2.PRJ", "DATABASE.MW2", "SHELLFIX.MW2" };
                if (!TableStore.Default.HasAll(ShellTables.TableNames)) required.Add("MW2SHELL.DLL");
                if (!TableStore.Default.HasAll(TableStore.MercswTables)) required.Add("mercsw.dll");
                foreach (string file in required)
                    if (!File.Exists(Path.Combine(GameFolder, file)))
                        throw new FileNotFoundException("Game folder is missing " + file);
                if (!_outsideTablesLogged)
                {
                    _outsideTablesLogged = true;
                    ShellTables.OutsideTables += m => Debug.LogWarning("MW2: " + m);
                }
                if (!Directory.Exists(Path.Combine(GameFolder, "SMK")))
                    throw new DirectoryNotFoundException("Game folder is missing SMK");
                InitializeShell();
                _folderError = null;
                Debug.Log("MW2 shell ready; game folder: " + GameFolder);
            }
            catch (Exception e)
            {
                Prj?.Dispose(); Prj = null;
                Surface = null;
                FolderFailure(e.Message);
            }
        }

        void FolderFailure(string message)
        {
            _folderError = message;
            if (Application.isBatchMode)
            {
                Debug.LogError("MW2 game folder: " + message);
                Application.Quit(2);
            }
            else Debug.LogWarning("MW2 game folder: " + message);
        }

        /// <summary>ShellMain 0x100428d3 after the installation adapter resolves the data directory.</summary>
        void InitializeShell()
        {
            string Path_(string f) => Path.Combine(GameFolder, f);
            Db = new ShellDatabase(Path_("DATABASE.MW2"));
            Fix = new ShellDatabase(Path_("SHELLFIX.MW2"));
            Surface = new ShellSurface();
            Anims = new ShellAnimList(Surface, Path_("SMK"), gameObject);
            Prj = new PrjArchive(Path_("MW2.PRJ"));
            Data = new ShellData(Prj, ShellTables.FromStore(GameFolder), GameFolder);
            Menu = ShellMenu.From(Data.Tables.Defaults.Menu);
            DifficultyConfig.UserFolder = SoundConfig.UserFolder = Application.persistentDataPath;
            InstantAction.UserFolder = Application.persistentDataPath;
            CombatVariablesScreen.Load(this);   // the shell's mw2snd.cfg / mw2dif.cfg blocks (FUN_1002782a, FUN_10027871)
            InstantAction.Difficulty = CombatVariablesScreen.Difficulty;
            Menu.EnableMain(Career);        // ShellMain: FUN_100438f4
            Font5 = new ShellFont(Db.Raw(5));
            Font6 = new ShellFont(Db.Raw(6));
            Font7 = new ShellFont(Db.Raw(7));
            Font9 = new ShellFont(Db.Raw(9));
            Font10 = new ShellFont(Db.Raw(10));
            Font11 = new ShellFont(Db.Raw(11));
            _screen = new Texture2D(ShellSurface.Width, ShellSurface.Height, TextureFormat.RGBA32, false)
                { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            _pixels = new Color32[ShellSurface.Width * ShellSurface.Height];
            var cam = Camera.main;
            if (cam == null)
            {
                cam = new GameObject("Shell camera").AddComponent<Camera>();
                cam.tag = "MainCamera";
            }
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.black;
            PlayFlic("mintro", 0xc02b, 0xc02b);
        }

        void OnDestroy()
        {
            Prj?.Dispose();
            if (Instance == this) Instance = null;
        }


        /// <summary>PostMessageA to the shell window.</summary>
        public void Post(uint msg, uint wParam) => _posted.Enqueue((msg, wParam));

        /// <summary>FUN_10043f22: register the screen function.</summary>
        public void Register(Action<uint> handler) => Handler = handler;

        /// <summary>FUN_10043f2f: unregister it.</summary>
        public void Unregister() => Handler = null;

        /// <summary>FUN_1000206d 0x1000206d: the animation stops looping; the screen waits for it, then gets the message.</summary>
        public void HoldUntilEnd(ShellAnim anim, uint msg)
        {
            if (anim.Has(ShellAnim.Dead)) return;
            anim.Flags &= ~ShellAnim.Loop;
            _heldHandler = Handler;
            _heldAnim = anim;
            Handler = null;
            _heldMessage = msg;
        }

        /// <summary>FUN_100721a5: the loop does nothing until the sample ends.</summary>
        public void BlockUntilEnd(ShellSound s)
        {
            s.Play();
            _blockingSound = s;
        }

        readonly List<ShellSound> _pendingRelease = new List<ShellSound>();

        public bool DeferRelease(ShellSound s)
        {
            if (_blockingSound == null) return false;
            _pendingRelease.Add(s);
            return true;
        }

        void Update()
        {
            if (Surface == null) return;
            if (_launching) return;
            if (_dialogLines != null) { UpdateDialog(); return; }
            PollInput();
            if (_blockingSound != null)
            {
                if (_blockingSound.Playing) return;
                _blockingSound = null;
                foreach (var p in _pendingRelease) p.ReleaseNow();
                _pendingRelease.Clear();
            }
            if (_posted.Count > 0)
            {
                var (msg, w) = _posted.Dequeue();
                WindowProc(msg, w);
            }
            Anims.Now = Now;
            Anims.MouseX = MouseX;
            Anims.MouseY = MouseY;
            uint ev;
            if (_passCounter < 2) ev = 0xc004;
            else
            {
                _passCounter = 0;
                ev = HotKey();
            }
            _passCounter++;
            LastVk = 0;
            Dispatch(ev);
            Present();
            Menu.Update(Career, Career.UnitMechs.Count);    // FUN_100423aa after every pass
        }

        /// <summary>DAT_100cf3bc: a modal hook over the screen (Merc Lords): called with 1 each pass instead of the screen
        /// function (Screen_Dispatch), with 0 to close it (FUN_100440bc before a menu command).</summary>
        public Action<int> Hook;

        /// <summary>FUN_100440bc 0x100440bc.</summary>
        public void CloseHook() => Hook?.Invoke(0);

        /// <summary>FUN_10044006 0x10044006.</summary>
        void Dispatch(uint ev)
        {
            if (Hook != null) Hook(1);
            else if (Handler != null)
            {
                Handler(ev);
                _flic?.Step(this);
            }
            else if (_heldHandler != null)
            {
                if (_heldAnim != null && _heldAnim.Running) Anims.Step();
                else
                {
                    var h = _heldHandler;
                    Handler = h;
                    _heldHandler = null;
                    _heldAnim = null;
                    h(_heldMessage);
                }
            }
        }

        /// <summary>FUN_10027ce0 0x10027ce0: F1..F12 (table 0x100a6a78) go to the screens below, filtered by mode and state.</summary>
        uint HotKey()
        {
            uint cur = Career.Msg23C7;
            if (cur == 0xc02d || cur == 0xc00a || cur == 0xc02b || cur == 0xc039) return 0xc004;
            uint target = 0xc004;
            for (int k = 0; k < HotKeys.Length; k++)
                if (LastVk == HotKeys[k].vk)
                {
                    if (HotKeys[k].msg == cur) return 0xc004;
                    target = HotKeys[k].msg;
                    LastVk = 0;
                    break;
                }
            if (target == 0xc004) return 0xc004;
            switch (Career.Mode)
            {
                case 0:
                    if (Career.Byte23B9)
                    {
                        switch (target)
                        {
                            case 0xc012: case 0xc01d: case 0xc01e: case 0xc022: case 0xc024: case 0xc026: case 0xc02f:
                            case 0xc01f: case 0xc020: case 0xc028: case 0xc030: case 0xc025:
                                Career.Byte23BA = true; return target;
                        }
                    }
                    else
                    {
                        switch (target)
                        {
                            case 0xc01d: case 0xc01e: case 0xc01f: case 0xc020: case 0xc022: case 0xc024: case 0xc028:
                            case 0xc02f: case 0xc030:
                                Career.Byte23BA = true; return target;
                            case 0xc026:
                                if (!Career.Registered) return 0xc004;
                                Career.Byte23BA = true; return target;
                        }
                    }
                    break;
                case 1:
                    if (Career.Byte23B9)
                    {
                        switch (target)
                        {
                            case 0xc012: case 0xc01f: case 0xc020: case 0xc028: case 0xc025:
                                Career.Byte23BA = true; return target;
                        }
                    }
                    else
                    {
                        switch (target)
                        {
                            case 0xc01d: case 0xc01e: case 0xc01f: case 0xc020: case 0xc022: case 0xc024: case 0xc026:
                            case 0xc028: case 0xc02f: case 0xc030:
                                if (!Career.Registered) return 0xc004;
                                Career.Byte23BA = true; return target;
                        }
                    }
                    break;
            }
            return 0xc004;
        }

        static readonly (int vk, uint msg)[] HotKeys =
        {
            (112, 0xc026), (113, 0xc024), (114, 0xc022), (115, 0xc01f), (116, 0xc020), (117, 0xc028),
            (118, 0xc01e), (119, 0xc01d), (120, 0xc02f), (121, 0xc030), (122, 0xc025), (123, 0xc012)
        };

        void PollInput()
        {
            var rect = ScreenRect();
            Vector2 m = Input.mousePosition;
            float u = (m.x - rect.x) / rect.width, v = 1f - (m.y - (Screen.height - rect.yMax)) / rect.height;
            if (u >= 0 && u < 1 && v >= 0 && v < 1)
            {
                MouseX = (int)(u * ShellSurface.Width);
                MouseY = (int)(v * ShellSurface.Height);
            }
            bool left = Input.GetMouseButton(0) && !MenuHasMouse();
            bool right = Input.GetMouseButton(1) && !MenuHasMouse();
            MouseRightClicked = right && !_mouseRight;
            _mouseRight = right;
            MouseClicked = left && !MouseLeft;
            MouseDouble = false;
            if (MouseClicked)
            {
                if (Now - _lastClickTime < 0xc9) MouseDouble = true;
                else _lastClickTime = Now;
            }
            MouseLeft = left;
            foreach (var (key, vk) in VkMap)
                if (Input.GetKeyDown(key))
                {
                    LastVk = vk;
                    _keyRing.Enqueue(vk);
                    if (_keyRing.Count > 0x40) _keyRing.Dequeue();
                }
        }

        static readonly (KeyCode, int)[] VkMap = BuildVkMap();
        static (KeyCode, int)[] BuildVkMap()
        {
            var l = new List<(KeyCode, int)>();
            for (int i = 0; i < 12; i++) l.Add((KeyCode.F1 + i, 112 + i));
            for (int i = 0; i < 26; i++) l.Add((KeyCode.A + i, 'A' + i));
            for (int i = 0; i < 10; i++) l.Add((KeyCode.Alpha0 + i, '0' + i));
            l.Add((KeyCode.Return, 13)); l.Add((KeyCode.Escape, 27)); l.Add((KeyCode.Space, 32));
            l.Add((KeyCode.Backspace, 8)); l.Add((KeyCode.Tab, 9));
            l.Add((KeyCode.LeftArrow, 37)); l.Add((KeyCode.UpArrow, 38)); l.Add((KeyCode.RightArrow, 39)); l.Add((KeyCode.DownArrow, 40));
            return l.ToArray();
        }

        /// <summary>FUN_10034e4f 0x10034e4f: take a key from the ring; 0 none, 3 Escape, 1 any other.</summary>
        public int ReadKey(out int key)
        {
            key = _keyRing.Count > 0 ? _keyRing.Dequeue() : 0;
            return key == 0 ? 0 : key == 27 ? 3 : 1;
        }

        /// <summary>FUN_10034de7: forget the pending keys.</summary>
        public void FlushKeys()
        {
            _keyRing.Clear();
            LastVk = 0;
        }

        /// <summary>
        /// ShellWindowProc 0x100408c7, screen messages. Each case stores the previous / current message in the state
        /// (+0x23c3 / +0x23c7) and calls the screen's set-up function.
        /// </summary>
        void WindowProc(uint msg, uint wParam)
        {
            // The screens entered from the save screen turn Save/Load on again; the title always does.
            if (msg == 0xc02b || wParam == 0xc02e && Array.IndexOf(SaveLoadBack, msg) >= 0) Menu.Enable(ShellMenu.SaveLoad, true);
            switch (msg)
            {
                case 0xc002:
                    Application.Quit();
                    break;
                case 0xc01c:
                    Career.Msg23C3 = wParam;
                    Career.Msg23C7 = msg;
                    MainMenuScreen.Open(this);
                    break;
                case 0xc02b:
                    Career.Msg23C7 = msg;
                    TitleScreen.Open(this);
                    break;
                case 0xc013:
                    Career.Msg23C3 = wParam;
                    Career.Msg23C7 = msg;
                    RegistrationScreen.Open(this);
                    break;
                case 0xc016:
                    Career.Msg23C3 = wParam;
                    Career.Msg23C7 = msg;
                    Career.AdvanceAtMissionCommand(Data, wParam);
                    MissionCommandScreen.Open(this);
                    break;
                case 0xc012:
                    Career.Msg23C3 = wParam;
                    Career.Msg23C7 = msg;
                    BriefingScreen.Open(this);
                    break;
                case 0xc011:
                    // ShellWindowProc 0x100408c7: FUN_10024a97 writes the assigned MEKs, then the shell exits for the mission.
                    // The Unity runtime builder consumes the same MEK bytes in memory instead of writing into the game folder.
                    Career.Msg23C7 = msg;
                    // Instant action (mode 2) launches the player roster of mw2prm.cfg (InstantAction) instead of the unit.
                    var roster = Career.Mode == 2 ? InstantAction.LaunchRoster(Data) : Career.BuildLaunchRoster(Data);
                    var player = roster.Find(e => e.Slot == 0);
                    if (player == null || string.IsNullOrEmpty(player.Model) || Career.Mission.Length < 4)
                    {
                        Debug.LogError("MW2 shell: mission launch has no player mech or mission");
                        Post(0xc012, msg);
                        break;
                    }
                    _launching = true;
                    _launchScreen = gameObject.AddComponent<Mw2LaunchScreen>();
                    _launchScreen.Begin(GameFolder);
                    StartCoroutine(LaunchMission(roster, player, msg));
                    break;
                case 0xc036:
                    Career.Msg23C3 = wParam;
                    Career.Msg23C7 = msg;
                    DropShipScreen.Open(this);
                    break;
                case 0xc026:
                    Career.Msg23C3 = wParam;
                    Career.Msg23C7 = msg;
                    NewsTerminalScreen.Open(this);
                    break;
                case 0xc01a:
                    Career.Msg23C3 = wParam;
                    Career.Msg23C7 = msg;
                    OfficeScreen.Open(this);
                    break;
                case 0xc01b:
                    Career.Msg23C3 = 0xc01b;
                    Career.Msg23C7 = msg;
                    MechBayScreen.Open(this);
                    break;
                case 0xc032:
                    Career.Msg23C3 = wParam;
                    Career.Msg23C7 = msg;
                    MechOpsScreen.Open(this);
                    break;
                case 0xc018:
                    Career.Msg23C3 = wParam;
                    Career.Msg23C7 = msg;
                    CreditsScreen.Open(this);
                    break;
                case 0xc038:
                    Career.Msg23C3 = wParam;
                    Career.Msg23C7 = msg;
                    CreditsBioScreen.Open(this);
                    break;
                case 0xc021:
                    Career.Msg23C3 = wParam;
                    Career.Msg23C7 = msg;
                    ComputerScreen.Open(this);
                    break;
                case 0xc022:
                    Career.Msg23C3 = wParam;
                    Career.Msg23C7 = msg;
                    PersonnelScreen.Open(this);
                    break;
                case 0xc025:
                    Career.Msg23C3 = wParam;
                    Career.Msg23C7 = msg;
                    DutyRosterScreen.Open(this, wParam);
                    break;
                case 0xc037:
                    // The unit can no longer buy any mech: the message, the high scores (FUN_1002eba2) and
                    // the "mpoorw" flic, then 0xc02b.
                    Dialog("You cannot afford even the cheapest mech|Your unit disbands in disgrace!#Ok",
                        _ => { RecordHighScore(); PlayFlic("mpoorw", 0xc02b, 0xc01c); });
                    break;
                case 0xc01d:
                    Career.Msg23C3 = wParam;
                    Career.Msg23C7 = msg;
                    MechFactoryScreen.Open(this);
                    break;
                case 0xc030:
                    Career.Msg23C3 = wParam;
                    Career.Msg23C7 = msg;
                    WeaponGroupingScreen.Open(this);
                    break;
                case 0xc02f:
                    Career.Msg23C3 = wParam;
                    Career.Msg23C7 = msg;
                    CustomizingScreen.Open(this);
                    break;
                case 0xc01e:
                    Career.Msg23C3 = wParam;
                    Career.Msg23C7 = msg;
                    ArmsDealerScreen.Open(this);
                    break;
                case 0xc028:
                    Career.Msg23C3 = wParam;
                    Career.Msg23C7 = msg;
                    RepairScreen.Open(this);
                    break;
                case 0xc024:
                    Career.Msg23C3 = wParam;
                    Career.Msg23C7 = msg;
                    ContractsScreen.Open(this);
                    break;
                case 0xc020:
                    Career.Msg23C3 = wParam;
                    Career.Msg23C7 = msg;
                    FinanceScreen.Open(this);
                    break;
                case 0xc01f:
                    Career.Msg23C3 = wParam;
                    Career.Msg23C7 = msg;
                    InventoryScreen.Open(this);
                    break;
                case 0xc00a:
                    Career.Msg23C7 = msg;
                    DebriefScreen.Open(this);
                    break;
                case 0xc039:
                    Career.Msg23C3 = wParam;
                    Career.Msg23C7 = msg;
                    SalvageScreen.Open(this);
                    break;
                case 0xc03a:
                    // ShellWindowProc 0x100408c7: the story is complete; Yes goes on in dock with the planet marks of
                    // +0x2342 cleared, No ends the career with mpoorw / mrich and records the high score.
                    Career.Msg23C3 = wParam;
                    Career.Msg23C7 = msg;
                    Dialog("The story is now complete|but further campaigns await. |Do you want to continue your career?|Continue ?|#Yes|No",
                        answer =>
                        {
                            if (answer == 0)
                            {
                                Career.Byte23A2 = false;
                                Career.Byte23B8 = false;
                                Career.Finished239D = true;
                                for (int i = 0; i < 0x14; i++) Career.SetI32(0x2342 + i * 4, -1);
                                Career.SetI32(0x242f, 1);
                                Post(0xc01c, 0xc00a);
                            }
                            else
                            {
                                Menu.Enable(ShellMenu.Retire, false);
                                RecordHighScore();
                                PlayFlic(Career.Funds + ShellEconomy.UnitAssets(Career, Data.Tables) < 15000000 ? "mpoorw" : "mrich",
                                         0xc02b, 0xc00a);
                            }
                        });
                    break;
                case 0xc029:
                    Career.Msg23C3 = wParam;
                    Career.Msg23C7 = msg;
                    InstantActionScreen.Open(this);
                    break;
                case 0xc031:
                    Career.Msg23C3 = wParam;
                    Career.Msg23C7 = msg;
                    MissionSettingsScreen.Open(this);
                    break;
                case 0xc02e:
                    // FUN_1006ee78 with the screen that sent it; +0x23c7 stays that screen's.
                    Career.Msg23C3 = wParam;
                    Menu.Enable(ShellMenu.SaveLoad, false);
                    SaveLoadScreen.Open(this, wParam);
                    break;
                case 0xc03b:
                    // Retire Mercenary: Debrief_Open 0x1001ede1 with 1: Retire off, DAT_100caeb8 = 1, Career_End 0x1001ec9a.
                    Menu.Enable(ShellMenu.Retire, false);
                    DebriefScreen.CareerEnded = true;
                    if (Career.Mode == 1) RecordHighScore();
                    string ending = Career.EndingFlic(Data.Tables);
                    if (ending != null) PlayFlic(ending, 0xc02b, 0xc00a);
                    else if (Career.Mode == 2) Post(0xc02b, 0xc02b);
                    break;
                case 0xc033:
                    PlayFlic("mto", 0xc016, 0xc016);
                    break;
                case 0xc034:
                    PlayFlic("mland", 0xc01c, 0xc01c);
                    break;
                case 0xc035:
                    PlayFlic("mland2", 0xc01c, 0xc01c);
                    break;
                default:
                    Debug.LogWarning($"MW2 shell: screen 0x{msg:x} (from 0x{wParam:x}) is not ported yet; back to the main menu");
                    Post(0xc01c, msg);
                    break;
            }
        }

        /// <summary>
        /// SimMain 0x100465ae calls StartSupAnim 0x100615b0 before loading world resources and StopSupAnim
        /// 0x10061aca before entering the simulation. Unity's builder runs on the main thread, so show
        /// the original screen for a second first; it remains visible while the builder is busy.
        /// </summary>
        IEnumerator LaunchMission(List<Career.LaunchEntry> roster, Career.LaunchEntry player, uint source)
        {
            yield return new WaitForSecondsRealtime(2f);
            GameObject root = null;
            try
            {
                string prefix = Career.Mission.Substring(0, 4);
                // FUN_100748e8: userstar.bwd from the roster; wingmen use the shell's written MEKs.
                byte[] userstar = Career.Mode == 2 ? Data.ReadLoose("userstar") : UserStar.Build(roster, Career, Data.Tables, Prj);
                Mission.LooseBwd = name => string.Equals(name, "userstar", StringComparison.OrdinalIgnoreCase) ? userstar : Data.ReadLoose(name);
                try
                {
                    var meks = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
                    foreach (var entry in roster)
                        if (entry.MekData != null) meks[entry.MekName.Length > 8 ? entry.MekName.Substring(0, 8) : entry.MekName] = entry.MekData;
                    root = Mw2MissionBuilder.Build(Prj, GameFolder, prefix, player.Model.ToLowerInvariant(), 1, player.MekName,
                        playerMekData: player.MekData,
                        careerMek: name => meks.TryGetValue(name, out var b) ? b : Career.Mode == 2 ? InstantAction.LoadMek(Data, name) : null);
                }
                finally { Mission.LooseBwd = null; }
            }
            catch (Exception error) { Debug.LogException(error); }
            Destroy(_launchScreen);
            _launchScreen = null;
            _launching = false;
            if (root == null) { Post(0xc012, source); yield break; }
            Mw2Cockpit.MissionLeft = OnMissionLeft;
            enabled = false;
        }

        /// <summary>FUN_1002eba2 0x1002eba2 / FUN_1002ea7f 0x1002ea7f: rank the
        /// player's net worth and write mw2high.cfg. The port keeps its writable copy in
        /// Unity's user-data directory while reading an existing original file as the seed.</summary>
        public void RecordHighScore()
        {
            string output = Path.Combine(Application.persistentDataPath, "mw2high.cfg");
            try
            {
                var scores = LoadHighScores();
                if (!scores.Offer(Career, Data.Tables)) return;
                Directory.CreateDirectory(Application.persistentDataPath);
                File.WriteAllBytes(output, scores.Save());
            }
            catch (IOException error) { Debug.LogWarning("MW2 high score: " + error.Message); }
        }

        /// <summary>FUN_1002e9c2 0x1002e9c2: mw2high.cfg from the user-data folder, else the game's, else the defaults.</summary>
        public HighScores LoadHighScores()
        {
            string output = Path.Combine(Application.persistentDataPath, "mw2high.cfg");
            string original = Path.Combine(GameFolder, "mw2high.cfg");
            byte[] saved = null;
            try
            {
                saved = File.Exists(output) ? File.ReadAllBytes(output) : File.Exists(original) ? File.ReadAllBytes(original) : null;
            }
            catch (IOException error) { Debug.LogWarning("MW2 high score: " + error.Message); }
            return HighScores.Load(saved, Data.Tables);
        }

        /// <summary>
        /// The files the engine leaves for the shell, kept in memory: MW2MSN.CFG (FUN_100424dd 0x100424dd) and MW2CAR.CFG
        /// (FUN_1001323d 0x1001323d). The debriefing 0xc00a reads them in place of the files.
        /// </summary>
        public byte[] MissionReportFile, CombatCareerFile;
        /// <summary>salvage.mw2 of the last mission (Salvage_Write 0x10059d0f); null when the engine wrote none.</summary>
        public byte[] SalvageFile;
        /// <summary>DAT_100f5044: the salvage screen 0xc039 was opened since the last Save_Read.</summary>
        public bool SalvageOpened;
        /// <summary>DAT_100cf3b4 == FUN_1006ffa3: a full-screen flic holds the screen function.</summary>
        public bool FlicRunning => Handler == FlicHandler;

        /// <summary>
        /// The end of SimMain 0x100465ae ("Calling EndTheMission": MW2CAR.CFG, the units' MEKs FUN_100598b1, salvage.mw2
        /// FUN_100599ee, MW2MSN.CFG) and mercswin's restart of the shell with "sim" (ShellMain 0x100428d3: Save_Read of
        /// SHELLMER.CFG from the mission, then 0xc00a). After Flee to Windows mercsw.dll returns 0xff and the game quits.
        /// The player's and the wingmen's MEKs are career units; the wrecks become salvage.mw2.
        /// </summary>
        void OnMissionLeft(bool fled)
        {
            Mw2Cockpit.MissionLeft = null;
            var director = FindAnyObjectByType<Mw2MissionDirector>();
            // Units_WriteMeks 0x100598b1: the career units' MEKs after the mission, by file name.
            var written = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var unit in Mw2MechCombat.All)
            {
                byte[] mek = unit != null ? unit.WriteMek() : null;
                if (mek != null) written[unit.MekName] = mek;
            }
            MissionReportFile = director != null ? director.BuildReport() : null;
            CombatCareerFile = (byte[])Mw2MissionDirector.Stats.Data.Clone();
            // Salvage_Write 0x10059d0f: srand(time(0)), then rand() for the 0x60 values of every wreck.
            uint seed = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            int CRand() { seed = seed * 214013u + 2531011u; return (int)((seed >> 16) & 0x7fff); }
            byte[] salvage = Mw2MissionDirector.Salvage.Write(CRand);
            if (director != null)
            {
                if (director.Player != null) Destroy(director.Player.gameObject);
                Destroy(director.gameObject);
            }
            if (fled)
            {
#if UNITY_EDITOR
                UnityEditor.EditorApplication.isPlaying = false;
#else
                Application.Quit();
#endif
                return;
            }
            var cam = Camera.main;
            if (cam != null)
            {
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = Color.black;
            }
            enabled = true;
            SalvageFile = salvage;
            SalvageOpened = false;
            uint next = Career.ReturnFromMission(name =>
                written.TryGetValue(name, out var mek) ||
                written.TryGetValue(name.Length > 8 ? name.Substring(0, 8) : name, out mek)
                    ? MechRecord.FromMek(name, mek, Data.Tables, Career.NextId) : null, Data.Tables);
            FlushKeys();
            Post(next, next);
        }

        /// <summary>
        /// FUN_10070135 0x10070135: play a full-screen flic, then post msg / wParam. With movies off (DAT_100a4348 = 0) only
        /// "mintro" and "mend" play. FUN_100700b4 0x100700b4 opens it in slot 0 with flag 0x1000 (into the screen, 320x200 mode)
        /// and registers FUN_1006ffa3 0x1006ffa3, which ends it on a click, a key or its last frame.
        /// </summary>
        public static int MoviesOn = 1;     // DAT_100a4348

        public void PlayFlic(string name, uint msg, uint wParam)
        {
            if (MoviesOn == 0 && name != "mintro" && name != "mend" && msg != 0xc004)
            {
                Post(msg, wParam);
                return;
            }
            string path = Path.Combine(GameFolder, "SMK", name + ".smk");
            if (!File.Exists(path))
            {
                Post(msg, wParam);
                return;
            }
            _flic = new ShellFlic(this, File.ReadAllBytes(path), msg, wParam);
            Menu.DisableMain();             // FUN_100700b4 → FUN_10043a00
            LowRes = true;
            Register(FlicHandler);
        }

        /// <summary>FUN_1006ffa3: ends the flic when it is over, on a click or key, or on another message.</summary>
        void FlicHandler(uint ev)
        {
            bool running = _flic != null && !_flic.Finished;
            if (running && !MouseClicked && ReadKey(out _) == 0 && ev == 0xc004) return;
            var f = _flic;
            _flic = null;
            f?.Stop();
            Unregister();
            if (ev == 0xc004) { if (f != null && f.Msg != 0xc004) Post(f.Msg, f.WParam); }
            else Post(ev, f?.WParam ?? 0xc004);
            LowRes = false;
            Surface.PaletteDirty = true;
            Menu.EnableMain(Career);        // FUN_10070eb3 of a 0x1000 slot → FUN_100438f4
        }

        /// <summary>FUN_10077849 / FUN_10077b79: upload the shown part of the screen.</summary>
        void Present()
        {
            if (!Surface.Present(out int x0, out int y0, out int x1, out int y1)) return;
            var pal = Surface.Palette6;
            for (int y = y0; y <= y1; y++)
            {
                int row = y * ShellSurface.Width, dst = (ShellSurface.Height - 1 - y) * ShellSurface.Width;
                for (int x = x0; x <= x1; x++)
                {
                    int c = Surface.Front[row + x] * 3;
                    _pixels[dst + x] = new Color32((byte)(pal[c] << 2), (byte)(pal[c + 1] << 2), (byte)(pal[c + 2] << 2), 255);
                }
            }
            _screen.SetPixels32(_pixels);
            _screen.Apply(false);
        }

        /// <summary>The 640x480 picture fitted into the window with its 4:3 shape; movies use a centered 320x200 viewport.</summary>
        internal Rect ScreenRect()
        {
            float aspect = 4f / 3f;
            float w = Screen.width, h = Screen.height;
            if (w / h > aspect) w = h * aspect; else h = w / aspect;
            return new Rect((Screen.width - w) / 2, (Screen.height - h) / 2, w, h);
        }

        /// <summary>
        /// FUN_100280ba 0x100280ba: the Windows message box of the shell. The text is split at '|' into lines (one line
        /// goes to the middle row; the dialog shows three), and after '#' the number of '|'-separated captions picks dialog
        /// 0x80 (two: &Yes / &No, "Mercenaries") or 0x81 (OK). DialogBoxParamA blocks the shell; the dialog procedure
        /// FUN_1002821f ends with 0 for OK / Yes and 1 for No (Escape does nothing). Here the shell stops until the
        /// answer, which goes to <paramref name="done"/>.
        /// </summary>
        public void Dialog(string text, Action<int> done)
        {
            var lines = new List<string>();
            int i = 0;
            while (i < text.Length && text[i] != '#')
            {
                int start = i;
                while (i < text.Length && text[i] != '|' && text[i] != '#') i++;
                lines.Add(text.Substring(start, i - start));
                if (i < text.Length && text[i] == '|') i++;
            }
            if (lines.Count == 1) lines.Insert(0, null);
            while (lines.Count < 3) lines.Add(null);
            int captions = 0;
            while (i < text.Length)
            {
                do i++; while (i < text.Length && text[i] != '|');
                captions++;
            }
            _dialogLines = lines.GetRange(0, 3).ToArray();
            _dialogYesNo = captions == 2;
            _dialogDone = done;
        }

        string[] _dialogLines;
        bool _dialogYesNo;
        Action<int> _dialogDone;

        public bool DialogOpen => _dialogLines != null;

        void UpdateDialog()
        {
            if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter) ||
                _dialogYesNo && Input.GetKeyDown(KeyCode.Y)) CloseDialog(0);
            else if (_dialogYesNo && Input.GetKeyDown(KeyCode.N)) CloseDialog(1);
        }

        void CloseDialog(int result)
        {
            var done = _dialogDone;
            _dialogLines = null;
            _dialogDone = null;
            MouseLeft = Input.GetMouseButton(0);
            done?.Invoke(result);
        }

        /// <summary>The screens of ShellWindowProc that turn Save/Load back on when entered from the save screen.</summary>
        static readonly uint[] SaveLoadBack =
        {
            0xc00a, 0xc010, 0xc012, 0xc013, 0xc016, 0xc01a, 0xc01b, 0xc01c, 0xc01d, 0xc01e, 0xc01f, 0xc020, 0xc021, 0xc022,
            0xc024, 0xc025, 0xc026, 0xc028, 0xc029, 0xc02f, 0xc030, 0xc031, 0xc032, 0xc036, 0xc039
        };

        /// <summary>The menu commands that are carried out (the others stay grayed, see MenuCommand).</summary>
        static readonly int[] PortedCommands =
        {
            ShellMenu.NewMercenary, ShellMenu.FleeToWindows, ShellMenu.SaveLoad, ShellMenu.Retire, ShellMenu.Dropship, ShellMenu.MercLords,
            ShellMenu.CombatVariables, ShellMenu.FirstCircuit, ShellMenu.CockpitControls,
            ShellMenu.Newsfeed, ShellMenu.Contracts, ShellMenu.Personnel, ShellMenu.Inventory, ShellMenu.Financial,
            ShellMenu.Repair, ShellMenu.ArmsMerchant, ShellMenu.MechFactory, ShellMenu.Customization,
            ShellMenu.WeaponsGrouping, ShellMenu.DutyRoster, ShellMenu.LaunchMission
        };

        /// <summary>Screen_Dispatch 0x10044006 with a message, or PostMessageA when no screen function is registered
        /// (the menu's "(*DAT_100cf3b4)(..., msg)" else "PostMessageA(msg, msg)").</summary>
        public void SendScreen(uint msg)
        {
            if (Handler != null && Handler != FlicHandler) Handler(msg);
            else Post(msg, msg);
        }

        /// <summary>
        /// The WM_COMMAND cases of ShellWindowProc 0x100408c7. New Mercenary: at the title straight to it, else
        /// "Save game?" (Yes saves first, DAT_100cf3d8, then the title). Flee to Windows: "Sell Out?". Save/Load: 0xc02e
        /// to the screen. Retire: "Retire Mercenary?", the high score (FUN_1002eba2), 0xc03b to the screen. Facility:
        /// FUN_1004228f (a mode-0 career anywhere, a docked mode-1 career only Inventory / Financial / Repair / Duty Roster
        /// / Launch, otherwise when registered; +0x23ba makes the screen accept first); on the salvage screen five of them
        /// call its screen function directly, which the port leaves out. Dropship: 0xc016 except from the title, the
        /// briefing and the debriefing. Merc Lords and Combat Variables open their hooks. The First Circuit too. Cockpit
        /// Controls opens the port's own bindings screen (ControlsScreen) instead of FUN_1000f864. Not ported: Movie
        /// Playback (dialog 0x8a), Video Options (FUN_1005c4d6) and the WinHelp items.
        /// </summary>
        public void MenuCommand(int id)
        {
            var c = Career;
            uint cur = c.Msg23C7;
            switch (id)
            {
                case ShellMenu.CombatVariables:
                    // FUN_100440bc, Combat Variables and Save/Load off, FUN_10027946.
                    CloseHook();
                    Menu.Enable(ShellMenu.CombatVariables, false);
                    Menu.Enable(ShellMenu.SaveLoad, false);
                    CombatVariablesScreen.Open(this);
                    break;
                case ShellMenu.CockpitControls:
                    // Port extension: a bindings screen over the game's maps, opened as a hook like Merc Lords.
                    CloseHook();
                    Menu.Enable(ShellMenu.CockpitControls, false);
                    Menu.Enable(ShellMenu.SaveLoad, false);
                    ControlsScreen.Open(this);
                    break;
                case ShellMenu.FirstCircuit:
                    // FUN_100440bc, The First Circuit and Save/Load off, FUN_1000fe40.
                    CloseHook();
                    Menu.Enable(ShellMenu.FirstCircuit, false);
                    Menu.Enable(ShellMenu.SaveLoad, false);
                    FirstCircuitScreen.Open(this);
                    break;
                case ShellMenu.MercLords:
                    // FUN_100440bc, Merc Lords and Save/Load off, FUN_1002e570.
                    CloseHook();
                    Menu.Enable(ShellMenu.MercLords, false);
                    Menu.Enable(ShellMenu.SaveLoad, false);
                    MercLordsScreen.Open(this);
                    break;
                case ShellMenu.NewMercenary:
                    if (cur == 0xc02b)
                    {
                        CloseHook();
                        SendScreen(0xc02b);
                        Menu.Enable(ShellMenu.Retire, false);
                    }
                    else
                        Dialog("Save game?#Yes|No", a =>
                        {
                            Menu.Enable(ShellMenu.Retire, false);
                            if (a != 0)
                            {
                                CloseHook();
                                SendScreen(0xc02b);
                            }
                            else
                            {
                                SaveLoadScreen.BeforeNewCareer = true;
                                SendScreen(0xc02e);
                            }
                        });
                    break;
                case ShellMenu.FleeToWindows:
                    Dialog("Sell Out?#Yes|No", a =>
                    {
                        if (a != 0) return;
                        CloseHook();
                        SendScreen(0xc002);
                    });
                    break;
                case ShellMenu.SaveLoad:
                    SendScreen(0xc02e);
                    break;
                case ShellMenu.Retire:
                    Dialog("Retire Mercenary?#Yes|No", a =>
                    {
                        if (a != 0) return;
                        Menu.Enable(ShellMenu.Retire, false);
                        RecordHighScore();
                        if (Handler != null && Handler != FlicHandler) Handler(0xc03b);
                    });
                    break;
                case ShellMenu.Dropship:
                    if (cur != 0xc02b && cur != 0xc012 && cur != 0xc00a)
                    {
                        c.Byte23BA = true;
                        SendScreen(0xc016);
                    }
                    break;
                default:
                    uint target = ShellMenu.FacilityTarget(id);
                    if (target == 0 || target == cur) break;
                    if (cur == 0xc039 && (id == ShellMenu.Inventory || id == ShellMenu.Financial || id == ShellMenu.Repair ||
                                          id == ShellMenu.DutyRoster || id == ShellMenu.LaunchMission)) break;
                    // FUN_1004228f 0x1004228f.
                    bool go = c.Mode == 0 ||
                              c.Mode == 1 && (c.Byte23B9
                                  ? target == 0xc01f || target == 0xc020 || target == 0xc028 || target == 0xc025 || target == 0xc012
                                  : c.Registered);
                    if (go)
                    {
                        c.Byte23BA = true;
                        SendScreen(target);
                    }
                    break;
            }
        }

        // ---- the menu bar ----
        bool _menuShown;
        int _menuOpen = -1;
        Rect _menuArea;

        /// <summary>The mouse over the menu bar or an open popup: the screen does not get the click.</summary>
        bool MenuHasMouse()
        {
            if (!_menuShown) return false;
            Vector2 m = Input.mousePosition;
            return _menuArea.Contains(new Vector2(m.x, Screen.height - m.y));
        }

        /// <summary>
        /// The menu bar of the full-screen shell (ShellWindowProc, WM_MOUSEMOVE): it appears when the mouse reaches the top
        /// of the picture and goes once the mouse is three pixels below it with no popup open; drawn with Unity's GUI.
        /// Not shown while a dialog or a full-screen flic is up.
        /// </summary>
        void DrawMenu(Rect r)
        {
            if (Menu.Popups.Count == 0 || _dialogLines != null || FlicRunning) { _menuShown = false; _menuOpen = -1; return; }
            float h = 20f;
            Vector2 mouse = Event.current.mousePosition;
            if (!_menuShown && mouse.y <= r.y + h && mouse.y >= r.y - 1 && mouse.x >= r.x && mouse.x < r.xMax) _menuShown = true;
            if (_menuShown && _menuOpen < 0 && mouse.y >= r.y + h + 3) _menuShown = false;
            if (!_menuShown) return;
            var bar = new Rect(r.x, r.y, r.width, h);
            _menuArea = bar;
            GUI.Box(bar, GUIContent.none);
            float x = r.x + 4;
            for (int p = 0; p < Menu.Popups.Count; p++)
            {
                var popup = Menu.Popups[p];
                string title = popup.Text.Replace("&", "");
                float w = GUI.skin.button.CalcSize(new GUIContent(title)).x + 8;
                GUI.enabled = !popup.Grayed;
                if (GUI.Button(new Rect(x, r.y + 1, w, h - 2), title)) _menuOpen = _menuOpen == p ? -1 : p;
                GUI.enabled = true;
                if (_menuOpen == p && !popup.Grayed)
                {
                    float iw = 220, ih = 20, y = r.y + h;
                    var list = new Rect(x, y, iw, popup.Items.Count * ih + 4);
                    GUI.Box(list, GUIContent.none);
                    float left = Mathf.Min(bar.x, list.x);
                    _menuArea = new Rect(left, bar.y, Mathf.Max(bar.xMax, list.xMax) - left, list.yMax - bar.y);
                    foreach (var item in popup.Items)
                    {
                        if (!item.Separator)
                        {
                            string[] parts = item.Text.Replace("&", "").Split('\t');
                            GUI.enabled = !item.Grayed && Array.IndexOf(PortedCommands, item.Id) >= 0;
                            var row = new Rect(x + 2, y + 2, iw - 4, ih);
                            if (GUI.Button(row, parts[0], GUI.skin.label))
                            {
                                _menuOpen = -1;
                                _menuShown = false;
                                GUI.enabled = true;
                                MenuCommand(item.Id);
                                return;
                            }
                            if (parts.Length > 1) GUI.Label(new Rect(row.xMax - 40, row.y, 40, ih), parts[1]);
                            GUI.enabled = true;
                        }
                        y += ih;
                    }
                }
                x += w + 2;
            }
            if (_menuOpen >= 0 && Event.current.type == EventType.MouseDown && !_menuArea.Contains(mouse)) _menuOpen = -1;
        }

        void OnGUI()
        {
            if (_folderError != null)
            {
                float width = Mathf.Min(600, Screen.width - 20);
                GUILayout.BeginArea(new Rect((Screen.width - width) / 2, 40, width, Screen.height - 60), GUI.skin.box);
                GUILayout.Label("MechWarrior 2: Mercenaries game folder");
                GUILayout.Label(_folderError);
                GameFolder = GUILayout.TextField(GameFolder);
                if (GUILayout.Button("Open game folder"))
                {
                    OpenGameFolder();
                    if (_folderError == null) { PlayerPrefs.SetString("MW2.GameFolder", GameFolder); PlayerPrefs.Save(); }
                }
                if (GUILayout.Button("Quit")) Application.Quit();
                GUILayout.EndArea();
                return;
            }
            if (_launching) { _launchScreen?.Draw(); return; }
            // The 4:3 shell picture leaves side margins on wide screens. Cover the camera view behind them.
            if (Event.current.type == EventType.Repaint)
                GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.blackTexture);
            if (_dialogLines != null && Event.current.type != EventType.Repaint)
            {
                DrawDialog();
                return;
            }
            if (_screen == null) return;
            var r = ScreenRect();
            if (Event.current.type == EventType.Repaint)
            {
                if (LowRes && _flic != null)
                {
                    // MINTRO is 320x144, while the other full-screen movies are 320x200.
                    // Draw only its own frame and center it in the original 320x200 viewport.
                    float width = Mathf.Min(_flic.Width, 320);
                    float height = Mathf.Min(_flic.Height, 200);
                    var frame = new Rect(r.x + r.width * (320f - width) / 640f,
                        r.y + r.height * (200f - height) / 400f,
                        r.width * width / 320f, r.height * height / 200f);
                    GUI.DrawTexture(r, Texture2D.blackTexture);
                    GUI.DrawTextureWithTexCoords(frame, _screen,
                        new Rect(0, 1f - height / ShellSurface.Height,
                            width / ShellSurface.Width, height / ShellSurface.Height));
                }
                else GUI.DrawTexture(r, _screen);
            }
            DrawMenu(r);
            ControlsScreen.Draw(this);
            if (_dialogLines != null && Event.current.type == EventType.Repaint) DrawDialog();
        }

        /// <summary>Dialogs 0x80 (208 x 85 units, buttons at 46 / 112) and 0x81 (224 x 95, OK at 88) of MW2SHELL.DLL,
        /// scaled from dialog units to the shown 640 x 480 screen.</summary>
        void DrawDialog()
        {
            var r = ScreenRect();
            float k = r.width / 640f * 1.75f;
            float w = (_dialogYesNo ? 208 : 224) * k, h = (_dialogYesNo ? 85 : 95) * k;
            var box = new Rect(r.x + (r.width - w) / 2, r.y + (r.height - h) / 2, w, h);
            GUI.Box(box, "Mercenaries");
            float textX = (_dialogYesNo ? 62 : 50) * k;
            float[] rows = _dialogYesNo ? new[] { 15f, 26f, 37f } : new[] { 17f, 28f, 41f };
            for (int i = 0; i < 3; i++)
                if (_dialogLines[i] != null)
                    GUI.Label(new Rect(box.x + textX, box.y + rows[i] * k + 8, w - textX, 12 * k), _dialogLines[i]);
            if (_dialogYesNo)
            {
                if (GUI.Button(new Rect(box.x + 46 * k, box.y + 64 * k, 50 * k, 14 * k), "Yes")) CloseDialog(0);
                if (GUI.Button(new Rect(box.x + 112 * k, box.y + 64 * k, 50 * k, 14 * k), "No")) CloseDialog(1);
            }
            else if (GUI.Button(new Rect(box.x + 88 * k, box.y + 65 * k, 50 * k, 14 * k), "OK")) CloseDialog(0);
        }
    }

    /// <summary>
    /// Full-screen flic in flic slot 0 with flag 0x1000 (FUN_100710c0 0x100710c0, FUN_100705f9 0x100705f9): each frame decoded
    /// into the screen at 0, 0, a new Smacker palette copied to the screen palette (FUN_10077d59), the slot freed after the last
    /// frame. Its sound track plays through Smacker's own Miles output.
    /// </summary>
    public sealed class ShellFlic
    {
        readonly Smacker _smk;
        public int Width => _smk.Width;
        public int Height => _smk.Height;
        public readonly uint Msg, WParam;
        double _next;
        int _shown;
        public bool Finished;
        AudioSource _audio;
        AudioClip _clip;

        public ShellFlic(Mw2Shell shell, byte[] data, uint msg, uint wParam)
        {
            _smk = new Smacker(data);
            Msg = msg;
            WParam = wParam;
            _next = shell.Now;
            if (_smk.AudioRate[0] != 0)
            {
                var all = new Smacker(data);
                while (all.NextFrame()) { }
                if (all.Audio.Count > 0)
                {
                    var f = new float[all.Audio.Count];
                    for (int i = 0; i < f.Length; i++) f[i] = all.Audio[i] / 32768f;
                    _clip = AudioClip.Create("flic", f.Length / all.AudioChannels, all.AudioChannels, all.AudioHz, false);
                    _clip.SetData(f, 0);
                    _audio = shell.gameObject.AddComponent<AudioSource>();
                    _audio.clip = _clip;
                    _audio.Play();
                }
            }
        }

        public void Step(Mw2Shell shell)
        {
            if (Finished || shell.Now < _next) return;
            _next += _smk.MillisecondsPerFrame;
            if (!_smk.NextFrame()) { Finished = true; return; }
            var s = shell.Surface;
            for (int i = 0; i < 768; i++)
            {
                byte v = (byte)(_smk.Palette[i] >> 2);
                if (s.Palette6[i] != v) { s.Palette6[i] = v; s.PaletteDirty = true; }
            }
            s.DrawBuffer(_smk.Pixels, 0, 0, _smk.Width, _smk.Height);
            _shown++;
            if (_shown >= _smk.FrameCount) Finished = true;
        }

        public void Stop()
        {
            if (_audio != null) { _audio.Stop(); UnityEngine.Object.Destroy(_audio); }
            if (_clip != null) UnityEngine.Object.Destroy(_clip);
        }
    }
}
