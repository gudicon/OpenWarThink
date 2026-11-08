using System.Collections.Generic;
using System.IO;
using MW2.Formats;
using UnityEngine;

namespace MW2
{
    /// <summary>
    /// The player's control block (DAT_100a7ae8) fed from Unity's keyboard and mouse through the original input layer
    /// (Formats/InputSystem: mercsw.dll tables, the game folder's inputw.map and gamekey.map). Polled once per frame,
    /// before the sim reads it, like Input_Frame 0x1002c516 and Input_HandleCommands 0x10026391.
    /// Keyboard bits follow the key name table 0x10097530 (scan code - 1; extended keys remapped by 0x10097b00, so the
    /// arrow cluster is Grey*, the keypad the plain names); bits 0x76 / 0x77 / 0x78 are any Shift / Ctrl / Alt, and
    /// with Alt held the keyboard reports nothing (0x1002de98). Mouse: buttons LeftBtn / MiddleBtn / RightBtn, axes
    /// Down/Up and Left/Right as the pointer's offset from the window centre, -0x10000..0x10000 (0x1004298a).
    /// Key codes for gamekey.map are posted on key release (WM_KEYUP, 0x1002e079).
    /// Approximation: the original clips the cursor to the window while it plays; Unity's cursor is left alone, and a
    /// pointer outside the game view does not update the axes (as outside the client rectangle, 0x10042b90).
    /// </summary>
    public static class Mw2Input
    {
        public static InputSystem System { get; private set; }
        static string _folder;
        static int _frame = -1;
        static double _clock;
        static int _lastTick;
        static readonly List<int> _commands = new List<int>();

        /// <summary>Commands posted this frame that Input_Command does not turn into control bytes.</summary>
        public static IReadOnlyList<int> Commands { get { Poll(); return _commands; } }

        /// <summary>
        /// An in-game window is open (DAT_100ad380): Menu_ReadKey 0x1003c6a0 takes Esc and the digit codes 0x30..0x39
        /// before Input_HandleCommands sees them; they arrive in MenuKeys instead of running a command.
        /// </summary>
        public static bool WindowOpen;
        /// <summary>
        /// The open window has flag 1 (the main menu): Menu_ReadKey also takes Enter 0x0d, Tab 9, Shift+Tab 0x209,
        /// space 0x20 and the arrow codes 0xc6..0xc9.
        /// </summary>
        public static bool WindowTakesArrows;
        static readonly List<int> _menuKeys = new List<int>();
        public static IReadOnlyList<int> MenuKeys { get { Poll(); return _menuKeys; } }

        /// <summary>DAT_10094610 = 0 while a menu holds the input: only menu_ channels update.</summary>
        public static bool MenuMode;

        static readonly KeyCode[] KeyboardBits = BuildKeyboardBits();

        static KeyCode[] BuildKeyboardBits()
        {
            var k = new KeyCode[0x79];
            k[0x00] = KeyCode.Escape;
            for (int i = 1; i <= 9; i++) k[i] = KeyCode.Alpha0 + i;
            k[0x0a] = KeyCode.Alpha0; k[0x0b] = KeyCode.Minus; k[0x0c] = KeyCode.Equals; k[0x0d] = KeyCode.Backspace;
            k[0x0e] = KeyCode.Tab;
            KeyCode[] row1 = { KeyCode.Q, KeyCode.W, KeyCode.E, KeyCode.R, KeyCode.T, KeyCode.Y, KeyCode.U, KeyCode.I, KeyCode.O, KeyCode.P };
            for (int i = 0; i < row1.Length; i++) k[0x0f + i] = row1[i];
            k[0x19] = KeyCode.LeftBracket; k[0x1a] = KeyCode.RightBracket; k[0x1b] = KeyCode.Return; k[0x1c] = KeyCode.LeftControl;
            KeyCode[] row2 = { KeyCode.A, KeyCode.S, KeyCode.D, KeyCode.F, KeyCode.G, KeyCode.H, KeyCode.J, KeyCode.K, KeyCode.L };
            for (int i = 0; i < row2.Length; i++) k[0x1d + i] = row2[i];
            k[0x26] = KeyCode.Semicolon; k[0x27] = KeyCode.Quote; k[0x28] = KeyCode.BackQuote; k[0x29] = KeyCode.LeftShift;
            k[0x2a] = KeyCode.Backslash;
            KeyCode[] row3 = { KeyCode.Z, KeyCode.X, KeyCode.C, KeyCode.V, KeyCode.B, KeyCode.N, KeyCode.M };
            for (int i = 0; i < row3.Length; i++) k[0x2b + i] = row3[i];
            k[0x32] = KeyCode.Comma; k[0x33] = KeyCode.Period; k[0x34] = KeyCode.Slash; k[0x35] = KeyCode.RightShift;
            k[0x36] = KeyCode.KeypadMultiply; k[0x37] = KeyCode.LeftAlt; k[0x38] = KeyCode.Space; k[0x39] = KeyCode.CapsLock;
            for (int i = 0; i < 10; i++) k[0x3a + i] = KeyCode.F1 + i;
            k[0x44] = KeyCode.Numlock; k[0x45] = KeyCode.ScrollLock;
            k[0x46] = KeyCode.Keypad7; k[0x47] = KeyCode.Keypad8; k[0x48] = KeyCode.Keypad9; k[0x49] = KeyCode.KeypadMinus;
            k[0x4a] = KeyCode.Keypad4; k[0x4b] = KeyCode.Keypad5; k[0x4c] = KeyCode.Keypad6; k[0x4d] = KeyCode.KeypadPlus;
            k[0x4e] = KeyCode.Keypad1; k[0x4f] = KeyCode.Keypad2; k[0x50] = KeyCode.Keypad3; k[0x51] = KeyCode.Keypad0;
            k[0x52] = KeyCode.KeypadPeriod; k[0x53] = KeyCode.SysReq; k[0x54] = KeyCode.KeypadEnter;
            k[0x56] = KeyCode.F11; k[0x57] = KeyCode.F12; k[0x58] = KeyCode.KeypadDivide;
            k[0x5d] = KeyCode.Home; k[0x5e] = KeyCode.UpArrow; k[0x5f] = KeyCode.PageUp; k[0x60] = KeyCode.LeftArrow;
            k[0x61] = KeyCode.RightArrow; k[0x62] = KeyCode.End; k[0x63] = KeyCode.DownArrow; k[0x64] = KeyCode.PageDown;
            k[0x65] = KeyCode.Insert; k[0x66] = KeyCode.Delete; k[0x6f] = KeyCode.RightControl; k[0x70] = KeyCode.RightAlt;
            return k;
        }

        // Virtual key and unshifted character of the keys that post gamekey codes (WM_KEYUP, 0x1002e079).
        static readonly (KeyCode key, int vk, char ch)[] VirtualKeys = BuildVirtualKeys();

        static (KeyCode, int, char)[] BuildVirtualKeys()
        {
            var list = new List<(KeyCode, int, char)>();
            for (int i = 0; i < 26; i++) list.Add((KeyCode.A + i, 'A' + i, (char)('a' + i)));
            for (int i = 0; i < 10; i++) list.Add((KeyCode.Alpha0 + i, '0' + i, (char)('0' + i)));
            for (int i = 0; i < 12; i++) list.Add((KeyCode.F1 + i, 0x70 + i, '\0'));
            for (int i = 0; i < 10; i++) list.Add((KeyCode.Keypad0 + i, 0x60 + i, (char)('0' + i)));
            list.AddRange(new (KeyCode, int, char)[]
            {
                (KeyCode.Space, 0x20, ' '), (KeyCode.Return, 0x0d, '\r'), (KeyCode.KeypadEnter, 0x0d, '\r'),
                (KeyCode.Backspace, 0x08, '\b'), (KeyCode.Tab, 0x09, '\t'), (KeyCode.Escape, 0x1b, '\x1b'),
                (KeyCode.Pause, 0x13, '\0'), (KeyCode.PageUp, 0x21, '\0'), (KeyCode.PageDown, 0x22, '\0'),
                (KeyCode.End, 0x23, '\0'), (KeyCode.Home, 0x24, '\0'), (KeyCode.LeftArrow, 0x25, '\0'),
                (KeyCode.UpArrow, 0x26, '\0'), (KeyCode.RightArrow, 0x27, '\0'), (KeyCode.DownArrow, 0x28, '\0'),
                (KeyCode.Insert, 0x2d, '\0'), (KeyCode.Delete, 0x2e, '\0'),
                (KeyCode.KeypadMultiply, 0x6a, '*'), (KeyCode.KeypadPlus, 0x6b, '+'), (KeyCode.KeypadMinus, 0x6d, '-'),
                (KeyCode.KeypadPeriod, 0x6e, '.'), (KeyCode.KeypadDivide, 0x6f, '/'),
                (KeyCode.Semicolon, 0xba, ';'), (KeyCode.Equals, 0xbb, '='), (KeyCode.Comma, 0xbc, ','),
                (KeyCode.Minus, 0xbd, '-'), (KeyCode.Period, 0xbe, '.'), (KeyCode.Slash, 0xbf, '/'),
                (KeyCode.BackQuote, 0xc0, '`'), (KeyCode.LeftBracket, 0xdb, '['), (KeyCode.Backslash, 0xdc, '\\'),
                (KeyCode.RightBracket, 0xdd, ']'), (KeyCode.Quote, 0xde, '\'')
            });
            return list.ToArray();
        }

        /// <summary>Game folder holding mercsw.dll, inputw.map and gamekey.map; taken from the mission director.</summary>
        public static string GameFolder
        {
            get
            {
                if (string.IsNullOrEmpty(_folder))
                {
                    var director = Object.FindAnyObjectByType<Mw2MissionDirector>();
                    if (director != null) _folder = director.GameFolder;
                }
                return _folder;
            }
            set { _folder = value; System = null; Mw2Options.Reset(); }
        }

        static bool EnsureLoaded()
        {
            if (System != null) return true;
            string folder = GameFolder;
            // mercsw.dll is needed only until input.json has been extracted.
            if (string.IsNullOrEmpty(folder) ||
                !TableStore.Default.Has(InputSystem.TableName) && !File.Exists(Path.Combine(folder, "mercsw.dll"))) return false;
            System = InputSystem.Get(folder); // tables from input.json, maps from the game folder
            // FUN_1002ca6b: the original shows these in a message box.
            foreach (var e in System.Errors) Debug.LogWarning($"MW2 input: {e}");
            return true;
        }

        public static void Poll()
        {
            if (_frame == Time.frameCount) return;
            _frame = Time.frameCount;
            _commands.Clear();
            _menuKeys.Clear();
            if (!EnsureLoaded()) return;
#if ENABLE_LEGACY_INPUT_MANAGER
            // FUN_10015f3d: the clock runs on during an open window and stops in the pause mode (PauseTimer).
            if (!Mw2Pause.PauseMode) _clock += Autopilot.MissionTestRunner.ClockDelta * MechMotion.TicksPerSecond;
            int tick = (int)_clock, ticks = tick - _lastTick;
            _lastTick = tick;
            bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            bool alt = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
            var mouse = Input.mousePosition;
            var dev = new InputSystem.DeviceState
            {
                KeyboardBit = bit =>
                {
                    if (alt) return false;
                    if (bit == 0x76) return shift;
                    if (bit == 0x77) return ctrl;
                    return bit >= 0 && bit < KeyboardBits.Length && KeyboardBits[bit] != KeyCode.None && Input.GetKey(KeyboardBits[bit]);
                },
                MouseButtons = (Input.GetMouseButton(0) ? 1 : 0) | (Input.GetMouseButton(2) ? 2 : 0) | (Input.GetMouseButton(1) ? 4 : 0),
                MouseInside = mouse.x >= 0 && mouse.y >= 0 && mouse.x < Screen.width && mouse.y < Screen.height,
                // Screen y grows downwards in the original (client coordinates).
                MouseY = (int)(((Screen.height - mouse.y) * 2 - Screen.height) * 0x10000L / Mathf.Max(1, Screen.height)),
                MouseX = (int)((mouse.x * 2 - Screen.width) * 0x10000L / Mathf.Max(1, Screen.width))
            };
            System.Frame(dev, Mathf.Max(0, ticks), MenuMode);
            bool keyCode = false;
            foreach (var (key, vk, ch) in VirtualKeys)
            {
                if (!Input.GetKeyUp(key)) continue;
                int code = System.KeyCode(vk, ch, ctrl, alt, shift);
                if (code == 0) continue;
                keyCode = true;
                if (WindowOpen && (code == 0x1b || code >= 0x30 && code <= 0x39 ||
                                   WindowTakesArrows && (code == 0x0d || code == 9 || code == 0x20 || code == 0x209 ||
                                                         code > 0xc5 && code < 0xca)))
                { _menuKeys.Add(code); continue; }
                // Input_HandleCommands 0x10026391 runs only outside the pause mode (DAT_100aea90 == 0).
                if (Mw2Pause.PauseMode) continue;
                int command = System.CommandFor(code);
                if (command == 0x70) Mw2Pause.RequestPause();       // PAUSE_GAME: DAT_100aea94 = 1
                else if (command != 0 && !System.ApplyCommand(command)) _commands.Add(command);
            }
            Mw2Pause.Update(keyCode);
#endif
            Autopilot.MechAutopilot.ApplyInput(System, _commands);
        }

        /// <summary>A control block byte (offset from DAT_100a7ae8) is set this frame.</summary>
        public static bool Held(int offset)
        {
            Poll();
            return System != null && System.Block[offset] != 0;
        }

        /// <summary>Clears a control byte the sim has consumed (the original writes 0 after acting on it).</summary>
        public static void Clear(int offset)
        {
            if (System != null) System.Block[offset] = 0;
        }

        public static void Set(int offset)
        {
            Poll();
            if (System != null) System.Block[offset] = 1;
        }

        public static float Float(int offset)
        {
            Poll();
            return System != null ? System.Float(offset) : 0f;
        }

        public static void SetFloat(int offset, float value)
        {
            Poll();
            if (System != null) System.SetFloat(offset, value);
        }

        /// <summary>A gamekey command (id of the table 0x10095148) was posted this frame.</summary>
        public static bool Command(int id)
        {
            Poll();
            return _commands.Contains(id);
        }

        public static bool Loaded { get { Poll(); return System != null; } }
    }
}
