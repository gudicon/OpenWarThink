using System;
using System.Collections.Generic;
using System.Linq;
using MW2.Formats;
using UnityEngine;

namespace MW2.Shell
{
    /// <summary>
    /// Options -> Cockpit Controls: lets the player bind every cockpit action and command to keys or mouse buttons.
    /// Port extension drawn with Unity's GUI. The original item opens FUN_1000f864, a fixed set of choices that rewrites
    /// inputw.map; this screen keeps the game's map files as they are and saves the changes to controls.json (see
    /// ControlBindings, Mw2Controls). Opened as a modal hook over the current shell screen, like Merc Lords.
    /// </summary>
    public static class ControlsScreen
    {
        static Mw2Shell _s;
        static InputSystem _shipped;
        static Dictionary<string, List<string>> _defaultChords;
        static readonly List<string> _commands = new List<string>();
        static readonly Dictionary<string, List<int>> _defaultCodes = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        static ControlProfile _edit;
        static Vector2 _scroll;
        static string _message;
        static string _captureAction, _captureCommand;
        static KeyCode _pendingModifier;
        static bool _leftClickCancels;
        static Dictionary<string, List<string>> _conflicts;
        static GUIStyle _warning, _header, _wrap;

        public static bool IsOpen => _s != null;

        public static void Open(Mw2Shell s)
        {
            _s = s;
            _message = null;
            _captureAction = _captureCommand = null;
            _scroll = Vector2.zero;
            try
            {
                _shipped = InputSystem.Get(s.GameFolder);
                _defaultChords = ControlBindings.DefaultChords(InputSystem.ReadMap(s.GameFolder, "inputw.map"));
                _commands.Clear();
                _defaultCodes.Clear();
                foreach (string name in ControlBindings.CommandNames(InputSystem.ReadMap(s.GameFolder, "gamekey.map")))
                {
                    if (!_shipped.CommandIds.ContainsKey(name)) continue;
                    _commands.Add(name);
                    _defaultCodes[name] = ControlBindings.CommandCodes(_shipped, name);
                }
            }
            catch (Exception e)
            {
                _shipped = null;
                _message = "The game's input tables could not be read: " + e.Message;
            }
            _edit = Mw2Controls.Profile?.Clone() ?? new ControlProfile();
            _conflicts = null;
            s.Hook = pass => { if (pass == 0) Close(false); };
        }

        static void Close(bool save)
        {
            var s = _s;
            if (s == null) return;
            if (save) Mw2Controls.Save(Changes());
            _s = null;
            s.Hook = null;
            s.Menu.Enable(ShellMenu.CockpitControls, true);
            s.Menu.Enable(ShellMenu.SaveLoad, true);
        }

        // ---- the profile being edited ----

        static List<string> Chords(string action) => _edit.FindAction(action)?.Chords ?? _defaultChords[action];
        static List<int> Codes(string command) => _edit.FindCommand(command)?.Codes ?? _defaultCodes[command];

        static List<string> EditChords(string action)
        {
            var e = _edit.FindAction(action);
            if (e == null) _edit.Actions.Add(e = new ControlProfile.ActionBinding { Id = action, Chords = new List<string>(_defaultChords[action]) });
            _conflicts = null;
            return e.Chords;
        }

        static List<int> EditCodes(string command)
        {
            var e = _edit.FindCommand(command);
            if (e == null) _edit.Commands.Add(e = new ControlProfile.CommandBinding { Name = command, Codes = new List<int>(_defaultCodes[command]) });
            _conflicts = null;
            return e.Codes;
        }

        static bool ActionChanged(string id) => _edit.FindAction(id) is var e && e != null && !Same(e.Chords, _defaultChords[id]);
        static bool CommandChanged(string name) => _edit.FindCommand(name) is var e && e != null && !Same(e.Codes, _defaultCodes[name]);
        static bool Same<T>(List<T> a, List<T> b) => a.Count == b.Count && !a.Except(b).Any();

        /// <summary>Only what differs from the game's maps is saved.</summary>
        static ControlProfile Changes()
        {
            var p = new ControlProfile();
            foreach (var a in ControlBindings.Actions)
                if (ActionChanged(a.Id)) p.Actions.Add(new ControlProfile.ActionBinding { Id = a.Id, Chords = new List<string>(Chords(a.Id)) });
            foreach (string c in _commands)
                if (CommandChanged(c)) p.Commands.Add(new ControlProfile.CommandBinding { Name = c, Codes = new List<int>(Codes(c)) });
            return p;
        }

        // ---- names ----

        static string KeyText(KeyCode k)
        {
            if (k >= KeyCode.Alpha0 && k <= KeyCode.Alpha9) return ((int)(k - KeyCode.Alpha0)).ToString();
            if (k >= KeyCode.Keypad0 && k <= KeyCode.Keypad9) return "Num " + (int)(k - KeyCode.Keypad0);
            switch (k)
            {
                case KeyCode.KeypadPlus: return "Num +";
                case KeyCode.KeypadMinus: return "Num -";
                case KeyCode.KeypadMultiply: return "Num *";
                case KeyCode.KeypadDivide: return "Num /";
                case KeyCode.KeypadPeriod: return "Num .";
                case KeyCode.KeypadEnter: return "Num Enter";
                case KeyCode.Numlock: return "Num Lock";
                case KeyCode.Return: return "Enter";
                case KeyCode.LeftArrow: return "Left";
                case KeyCode.RightArrow: return "Right";
                case KeyCode.UpArrow: return "Up";
                case KeyCode.DownArrow: return "Down";
                case KeyCode.PageUp: return "Page Up";
                case KeyCode.PageDown: return "Page Down";
                case KeyCode.BackQuote: return "`";
                case KeyCode.Equals: return "=";
                case KeyCode.Minus: return "-";
                case KeyCode.Comma: return ",";
                case KeyCode.Period: return ".";
                case KeyCode.Slash: return "/";
                case KeyCode.Backslash: return "\\";
                case KeyCode.Semicolon: return ";";
                case KeyCode.Quote: return "'";
                case KeyCode.LeftBracket: return "[";
                case KeyCode.RightBracket: return "]";
                case KeyCode.LeftControl: return "Left Ctrl";
                case KeyCode.RightControl: return "Right Ctrl";
                case KeyCode.LeftShift: return "Left Shift";
                case KeyCode.RightShift: return "Right Shift";
                case KeyCode.CapsLock: return "Caps Lock";
                case KeyCode.ScrollLock: return "Scroll Lock";
                case KeyCode.SysReq: return "Print Screen";
                default: return k.ToString();
            }
        }

        static string TokenText(string token)
        {
            if (!ControlBindings.ParseToken(token, out _, out string device, out string key)) return token;
            if (device == "mouse")
                return key == "LeftBtn" ? "Mouse left" : key == "RightBtn" ? "Mouse right" : key == "MiddleBtn" ? "Mouse middle" : "Mouse " + key;
            int bit = _shipped != null ? Array.IndexOf(_shipped.KeyboardNames, key) : -1;
            if (bit == InputSystem.ShiftBit) return "Shift";
            if (bit == InputSystem.ControlBit) return "Ctrl";
            var k = Mw2Input.KeyForBit(bit);
            return k != KeyCode.None ? KeyText(k) : key.TrimStart('*');
        }

        static bool IsModifierToken(string token) =>
            _shipped != null && ControlBindings.ParseToken(token, out _, out _, out string key) &&
            (key == _shipped.KeyName(InputSystem.ShiftBit) || key == _shipped.KeyName(InputSystem.ControlBit));

        static string ChordText(string chord)
        {
            var tokens = ControlBindings.Tokens(chord);
            var held = tokens.Where(t => t[0] == '+').OrderBy(t => IsModifierToken(t) ? 0 : 1).Select(TokenText);
            var free = tokens.Where(t => t[0] == '-').Select(TokenText).ToList();
            return string.Join("+", held) + (free.Count > 0 ? " (no " + string.Join(", ", free) + ")" : "");
        }

        static string CodeText(int code)
        {
            var k = Mw2Input.KeyForCommandCode(_shipped, code);
            if (k != KeyCode.None) return KeyText(k);
            string mods = ((code & 0x100) != 0 ? "Ctrl+" : "") + ((code & 0x400) != 0 ? "Alt+" : "") + ((code & 0x200) != 0 ? "Shift+" : "");
            k = Mw2Input.KeyForCommandCode(_shipped, code & 0xff);
            return mods + (k != KeyCode.None ? KeyText(k) : $"key 0x{code & 0xff:x2}");
        }

        static readonly string[] Acronyms = { "MASC", "HUD", "MFD", "MFDS", "AMS", "HTAL", "A", "B", "C", "D", "E", "F" };

        static string CommandText(string name)
        {
            var words = name.Split('_').Where(w => w.Length > 0).Select(w =>
                Acronyms.Contains(w) ? w : w.Substring(0, 1) + w.Substring(1).ToLowerInvariant()).ToList();
            for (int i = 1; i < words.Count; i++) if (!Acronyms.Contains(words[i])) words[i] = words[i].ToLowerInvariant();
            return string.Join(" ", words).Replace("Starmate", "Lancemate").Replace("Aerotechstrike", "Aerospace strike");
        }

        static string CommandGroup(string name)
        {
            if (name.StartsWith("STARMATE") || name.StartsWith("AEROTECH")) return "Lance commands";
            if (name.StartsWith("THROTTLE") || name.Contains("REVERSE") || name.Contains("FEET")) return "Movement commands";
            if (name.Contains("TARGET") || name.Contains("NAVPOINT")) return "Targeting commands";
            if (name.Contains("WEAPON") || name.Contains("FIRE") || name.Contains("AMMO") || name.Contains("AMS")) return "Weapon commands";
            if (name.Contains("MFD") || name.Contains("RADAR") || name.Contains("DISPLAY") || name.Contains("HUD") ||
                name.Contains("VIEW") || name.Contains("COCKPIT") || name.Contains("COMPASS") || name.Contains("INFRARED") ||
                name.Contains("HTAL") || name.Contains("OBJECTIVES") || name.Contains("WEAPONS")) return "Cockpit and display commands";
            return "Mech and game commands";
        }

        // ---- conflicts: the same keys bound to two things ----

        static string Physical(string chord)
        {
            var parts = new List<string>();
            foreach (string t in ControlBindings.Tokens(chord))
            {
                if (t[0] != '+' || !ControlBindings.ParseToken(t, out _, out string device, out string key)) continue;
                int bit = device == "keyboard" ? Array.IndexOf(_shipped.KeyboardNames, key) : -1;
                parts.Add(bit == InputSystem.ShiftBit ? "shift" : bit == InputSystem.ControlBit ? "ctrl" : device + ":" + (bit >= 0 ? bit.ToString() : key));
            }
            return string.Join(" ", parts.OrderBy(p => p, StringComparer.Ordinal));
        }

        static string Physical(int code)
        {
            var k = Mw2Input.KeyForCommandCode(_shipped, code);
            var parts = new List<string>();
            if (k == KeyCode.None)
            {
                k = Mw2Input.KeyForCommandCode(_shipped, code & 0xff);
                if ((code & 0x100) != 0) parts.Add("ctrl");
                if ((code & 0x200) != 0) parts.Add("shift");
                if ((code & 0x400) != 0) parts.Add("alt");
            }
            int bit = Mw2Input.BitForKey(k);
            parts.Add("keyboard:" + (bit >= 0 ? bit.ToString() : "code" + code));
            return string.Join(" ", parts.OrderBy(p => p, StringComparer.Ordinal));
        }

        static void FindConflicts()
        {
            var users = new Dictionary<string, List<string>>();
            void Add(string key, string who)
            {
                if (!users.TryGetValue(key, out var list)) users[key] = list = new List<string>();
                if (!list.Contains(who)) list.Add(who);
            }
            foreach (var a in ControlBindings.Actions)
                foreach (string c in Chords(a.Id)) Add(Physical(c), a.Label);
            foreach (string c in _commands)
                foreach (int code in Codes(c)) Add(Physical(code), CommandText(c));
            _conflicts = new Dictionary<string, List<string>>();
            foreach (var kv in users)
                if (kv.Value.Count > 1)
                    foreach (string who in kv.Value)
                    {
                        if (!_conflicts.TryGetValue(who, out var others)) _conflicts[who] = others = new List<string>();
                        others.AddRange(kv.Value.Where(o => o != who && !others.Contains(o)));
                    }
        }

        // ---- capturing a key ----

        static bool Capturing => _captureAction != null || _captureCommand != null;

        static void StopCapture() { _captureAction = _captureCommand = null; _pendingModifier = KeyCode.None; }

        static bool IsModifier(KeyCode k) =>
            k == KeyCode.LeftShift || k == KeyCode.RightShift || k == KeyCode.LeftControl || k == KeyCode.RightControl ||
            k == KeyCode.LeftAlt || k == KeyCode.RightAlt || k == KeyCode.AltGr;

        static void HandleCapture(Event e)
        {
            if (!Capturing) return;
            if (e.type == EventType.KeyDown && e.keyCode != KeyCode.None)
            {
                if (e.keyCode == KeyCode.Escape) StopCapture();
                else if (IsModifier(e.keyCode)) _pendingModifier = e.keyCode;
                else Commit(e.keyCode, e.control, e.alt, e.shift);
                e.Use();
            }
            else if (e.type == EventType.KeyUp && e.keyCode != KeyCode.None && e.keyCode == _pendingModifier)
            {
                // A modifier pressed and released on its own is bound as a key itself.
                Commit(e.keyCode, false, false, false);
                e.Use();
            }
            else if (e.type == EventType.MouseDown && e.button != 0)
            {
                CommitMouse(e.button);
                e.Use();
            }
            // The left button binds only when clicked on the row's own capture button (see Row); a left click
            // anywhere else cancels the capture and still works as a click.
            else if (e.type == EventType.MouseDown && e.button == 0) _leftClickCancels = true;
        }

        static void Commit(KeyCode key, bool ctrl, bool alt, bool shift)
        {
            if (_captureAction != null)
            {
                if (alt || key == KeyCode.LeftAlt || key == KeyCode.RightAlt || key == KeyCode.AltGr)
                { _message = "Cockpit actions can't use Alt: the game's keyboard input reads no keys while Alt is held."; StopCapture(); return; }
                int bit = Mw2Input.BitForKey(key);
                string name = _shipped.KeyName(bit).Trim();
                if (bit < 0 || name.Length == 0) { _message = $"{KeyText(key)} can't be used for cockpit actions."; StopCapture(); return; }
                var tokens = new List<string> { ControlBindings.Token('+', "keyboard", name) };
                bool isCtrl = key == KeyCode.LeftControl || key == KeyCode.RightControl;
                bool isShift = key == KeyCode.LeftShift || key == KeyCode.RightShift;
                if (ctrl && !isCtrl) tokens.Add(ControlBindings.Token('+', "keyboard", _shipped.KeyName(InputSystem.ControlBit)));
                if (shift && !isShift) tokens.Add(ControlBindings.Token('+', "keyboard", _shipped.KeyName(InputSystem.ShiftBit)));
                AddChord(ControlBindings.Chord(tokens));
            }
            else
            {
                if (IsModifier(key)) { _message = "Commands need a key other than Shift, Ctrl or Alt."; StopCapture(); return; }
                int code = Mw2Input.CommandCode(_shipped, key, ctrl, alt, shift);
                if (code <= 0 || code >= _shipped.CommandByCode.Length) { _message = $"{KeyText(key)} can't be used for commands."; StopCapture(); return; }
                var codes = EditCodes(_captureCommand);
                if (!codes.Contains(code)) codes.Add(code);
                _message = null;
            }
            StopCapture();
        }

        static void CommitMouse(int button)
        {
            if (_captureCommand != null) { _message = "Commands use keyboard keys; bind mouse buttons to cockpit actions."; StopCapture(); return; }
            // MouseButtons bit 0 left, 1 middle, 2 right; Unity: 0 left, 1 right, 2 middle.
            int index = button == 0 ? 0 : button == 2 ? 1 : button == 1 ? 2 : -1;
            if (index < 0 || index >= _shipped.MouseButtons.Length) { StopCapture(); return; }
            AddChord(ControlBindings.Token('+', "mouse", _shipped.MouseButtons[index]));
            StopCapture();
        }

        static void AddChord(string chord)
        {
            var chords = EditChords(_captureAction);
            if (!chords.Contains(chord)) chords.Add(chord);
            _message = null;
        }

        // ---- drawing ----

        public static void Draw()
        {
            if (_s == null) return;
            var e = Event.current;
            HandleCapture(e);
            _warning ??= new GUIStyle(GUI.skin.label) { normal = { textColor = new Color(1f, 0.55f, 0.45f) }, wordWrap = true };
            _header ??= new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold };
            _wrap ??= new GUIStyle(GUI.skin.label) { wordWrap = true };

            float width = Mathf.Min(820, Screen.width - 20), height = Screen.height - 40;
            GUILayout.BeginArea(new Rect((Screen.width - width) / 2, 20, width, height), GUI.skin.box);
            GUILayout.Label("Cockpit Controls", _header);
            GUILayout.Label("Click + and press a key or mouse button to add a binding; click a binding to remove it. " +
                            "Cockpit actions take keys (with Shift / Ctrl) and mouse buttons; commands take keys with Shift / Ctrl / Alt. " +
                            "Esc cancels. Changes are saved to " + Mw2Controls.FileName + " in your user-data folder; " +
                            "the game's own map files are not changed.", _wrap);
            if (_message != null) GUILayout.Label(_message, _warning);

            if (_shipped == null)
            {
                if (GUILayout.Button("Close", GUILayout.Width(120))) Close(false);
                GUILayout.EndArea();
                return;
            }
            if (_conflicts == null) FindConflicts();

            _scroll = GUILayout.BeginScrollView(_scroll);
            string group = null;
            foreach (var a in ControlBindings.Actions)
            {
                if (a.Group != group) { group = a.Group; GUILayout.Space(6); GUILayout.Label(group, _header); }
                Row(a.Label, a.Id, null);
            }
            foreach (var g in _commands.GroupBy(CommandGroup))
            {
                GUILayout.Space(6);
                GUILayout.Label(g.Key, _header);
                foreach (string c in g) Row(CommandText(c), null, c);
            }
            GUILayout.EndScrollView();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Save", GUILayout.Width(120))) { StopCapture(); Close(true); }
            if (GUILayout.Button("Cancel", GUILayout.Width(120))) { StopCapture(); Close(false); }
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Restore all defaults", GUILayout.Width(180)))
            {
                StopCapture();
                _edit = new ControlProfile();
                _conflicts = null;
                _message = "All bindings restored to the game's defaults. Save to keep them.";
            }
            GUILayout.EndHorizontal();
            GUILayout.EndArea();
            if (_leftClickCancels) { _leftClickCancels = false; StopCapture(); }
        }

        static void Row(string label, string action, string command)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(250));
            if (action != null)
            {
                var chords = Chords(action);
                for (int i = 0; i < chords.Count; i++)
                    if (GUILayout.Button(ChordText(chords[i]), GUILayout.ExpandWidth(false))) { EditChords(action).RemoveAt(i); break; }
            }
            else
            {
                var codes = Codes(command);
                for (int i = 0; i < codes.Count; i++)
                    if (GUILayout.Button(CodeText(codes[i]), GUILayout.ExpandWidth(false))) { EditCodes(command).RemoveAt(i); break; }
            }
            bool capturing = action != null ? _captureAction == action : _captureCommand == command;
            var content = new GUIContent(!capturing ? "+" : action != null ? "press a key, or click here for Mouse left" : "press a key...");
            Rect r = GUILayoutUtility.GetRect(content, GUI.skin.button, GUILayout.ExpandWidth(false));
            var ev = Event.current;
            if (capturing && action != null && ev.type == EventType.MouseDown && ev.button == 0 && r.Contains(ev.mousePosition))
            {
                _leftClickCancels = false;
                CommitMouse(0);
                ev.Use();
            }
            else if (GUI.Button(r, content))
            {
                StopCapture();
                if (!capturing) { _captureAction = action; _captureCommand = command; _leftClickCancels = false; }
            }
            GUILayout.FlexibleSpace();
            bool changed = action != null ? ActionChanged(action) : CommandChanged(command);
            if (changed && GUILayout.Button("Default", GUILayout.Width(70)))
            {
                if (action != null) _edit.Actions.RemoveAll(x => x.Id == action);
                else _edit.Commands.RemoveAll(x => string.Equals(x.Name, command, StringComparison.OrdinalIgnoreCase));
                _conflicts = null;
            }
            GUILayout.EndHorizontal();
            if (_conflicts.TryGetValue(label, out var others) && others.Count > 0)
                GUILayout.Label("   Same keys as: " + string.Join(", ", others), _warning);
        }
    }
}
