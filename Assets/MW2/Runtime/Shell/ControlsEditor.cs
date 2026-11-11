using System;
using System.Collections.Generic;
using System.Linq;
using MW2.Formats;
using UnityEngine;

namespace MW2.Shell
{
    /// <summary>
    /// The player's key bindings being edited (port extension, see ControlBindings): the game's shipped bindings, the
    /// working copy of the profile, the texts of keys and chords, conflicts, and turning a pressed key or mouse button
    /// into a binding. Shared by the shell's bindings screen (ControlsScreen) and the in-mission menu pages
    /// (Mw2Cockpit, Cockpit Controls).
    /// </summary>
    public sealed class ControlsEditor
    {
        public readonly InputSystem Shipped;
        /// <summary>Why the game's input tables could not be read (null when they could).</summary>
        public readonly string Error;
        public readonly List<string> Commands = new List<string>();
        readonly Dictionary<string, List<string>> _defaultChords;
        readonly Dictionary<string, List<int>> _defaultCodes = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        ControlProfile _edit;
        Dictionary<string, List<string>> _conflicts;

        public ControlsEditor(string gameFolder)
        {
            try
            {
                Shipped = InputSystem.Get(gameFolder);
                _defaultChords = ControlBindings.DefaultChords(InputSystem.ReadMap(gameFolder, "inputw.map"));
                foreach (string name in ControlBindings.CommandNames(InputSystem.ReadMap(gameFolder, "gamekey.map")))
                {
                    if (!Shipped.CommandIds.ContainsKey(name)) continue;
                    Commands.Add(name);
                    _defaultCodes[name] = ControlBindings.CommandCodes(Shipped, name);
                }
            }
            catch (Exception e)
            {
                Shipped = null;
                Error = "The game's input tables could not be read: " + e.Message;
                _defaultChords = ControlBindings.Actions.ToDictionary(a => a.Id, a => new List<string>());
            }
            _edit = Mw2Controls.Profile?.Clone() ?? new ControlProfile();
        }

        // ---- the profile ----

        public List<string> Chords(string action) => _edit.FindAction(action)?.Chords ?? _defaultChords[action];
        public List<int> Codes(string command) => _edit.FindCommand(command)?.Codes ?? _defaultCodes[command];

        List<string> EditChords(string action)
        {
            var e = _edit.FindAction(action);
            if (e == null) _edit.Actions.Add(e = new ControlProfile.ActionBinding { Id = action, Chords = new List<string>(_defaultChords[action]) });
            _conflicts = null;
            return e.Chords;
        }

        List<int> EditCodes(string command)
        {
            var e = _edit.FindCommand(command);
            if (e == null) _edit.Commands.Add(e = new ControlProfile.CommandBinding { Name = command, Codes = new List<int>(_defaultCodes[command]) });
            _conflicts = null;
            return e.Codes;
        }

        public bool ActionChanged(string id) => _edit.FindAction(id) is var e && e != null && !Same(e.Chords, _defaultChords[id]);
        public bool CommandChanged(string name) => _edit.FindCommand(name) is var e && e != null && !Same(e.Codes, _defaultCodes[name]);
        static bool Same<T>(List<T> a, List<T> b) => a.Count == b.Count && !a.Except(b).Any();

        public void ResetAction(string id) { _edit.Actions.RemoveAll(x => x.Id == id); _conflicts = null; }
        public void ResetCommand(string name)
        {
            _edit.Commands.RemoveAll(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            _conflicts = null;
        }
        public void RestoreAll() { _edit = new ControlProfile(); _conflicts = null; }

        public void RemoveChord(string action, int index) { var c = EditChords(action); if (index >= 0 && index < c.Count) c.RemoveAt(index); }
        public void RemoveCode(string command, int index) { var c = EditCodes(command); if (index >= 0 && index < c.Count) c.RemoveAt(index); }

        /// <summary>Only what differs from the game's maps is saved.</summary>
        public ControlProfile Changes()
        {
            var p = new ControlProfile();
            foreach (var a in ControlBindings.Actions)
                if (ActionChanged(a.Id)) p.Actions.Add(new ControlProfile.ActionBinding { Id = a.Id, Chords = new List<string>(Chords(a.Id)) });
            foreach (string c in Commands)
                if (CommandChanged(c)) p.Commands.Add(new ControlProfile.CommandBinding { Name = c, Codes = new List<int>(Codes(c)) });
            return p;
        }

        public void Save() => Mw2Controls.Save(Changes());

        // ---- binding a pressed key ----

        public static bool IsModifier(KeyCode k) =>
            k == KeyCode.LeftShift || k == KeyCode.RightShift || k == KeyCode.LeftControl || k == KeyCode.RightControl ||
            k == KeyCode.LeftAlt || k == KeyCode.RightAlt || k == KeyCode.AltGr;

        /// <summary>Adds a key to an action or a command; returns why it can't be used, or null.</summary>
        public string AddKey(string action, string command, KeyCode key, bool ctrl, bool alt, bool shift)
        {
            if (Shipped == null) return Error;
            if (action != null)
            {
                if (alt || key == KeyCode.LeftAlt || key == KeyCode.RightAlt || key == KeyCode.AltGr)
                    return "Cockpit actions can't use Alt: the game reads no keys while Alt is held.";
                int bit = Mw2Input.BitForKey(key);
                string name = Shipped.KeyName(bit).Trim();
                if (bit < 0 || name.Length == 0) return $"{KeyText(key)} can't be used for cockpit actions.";
                var tokens = new List<string> { ControlBindings.Token('+', "keyboard", name) };
                bool isCtrl = key == KeyCode.LeftControl || key == KeyCode.RightControl;
                bool isShift = key == KeyCode.LeftShift || key == KeyCode.RightShift;
                if (ctrl && !isCtrl) tokens.Add(ControlBindings.Token('+', "keyboard", Shipped.KeyName(InputSystem.ControlBit)));
                if (shift && !isShift) tokens.Add(ControlBindings.Token('+', "keyboard", Shipped.KeyName(InputSystem.ShiftBit)));
                AddChord(action, ControlBindings.Chord(tokens));
                return null;
            }
            if (IsModifier(key)) return "Commands need a key other than Shift, Ctrl or Alt.";
            int code = Mw2Input.CommandCode(Shipped, key, ctrl, alt, shift);
            if (code <= 0 || code >= Shipped.CommandByCode.Length) return $"{KeyText(key)} can't be used for commands.";
            var codes = EditCodes(command);
            if (!codes.Contains(code)) codes.Add(code);
            return null;
        }

        /// <summary>Adds a mouse button (Unity numbering: 0 left, 1 right, 2 middle) to an action.</summary>
        public string AddMouse(string action, string command, int button)
        {
            if (Shipped == null) return Error;
            if (action == null) return "Commands use keyboard keys; bind mouse buttons to cockpit actions.";
            // MouseButtons bit 0 left, 1 middle, 2 right.
            int index = button == 0 ? 0 : button == 2 ? 1 : button == 1 ? 2 : -1;
            if (index < 0 || index >= Shipped.MouseButtons.Length) return "That mouse button can't be used.";
            AddChord(action, ControlBindings.Token('+', "mouse", Shipped.MouseButtons[index]));
            return null;
        }

        void AddChord(string action, string chord)
        {
            var chords = EditChords(action);
            if (!chords.Contains(chord)) chords.Add(chord);
        }

        // ---- texts ----

        public static string KeyText(KeyCode k)
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

        string TokenText(string token)
        {
            if (!ControlBindings.ParseToken(token, out _, out string device, out string key)) return token;
            if (device == "mouse")
                return key == "LeftBtn" ? "Mouse left" : key == "RightBtn" ? "Mouse right" : key == "MiddleBtn" ? "Mouse middle" : "Mouse " + key;
            int bit = Shipped != null ? Array.IndexOf(Shipped.KeyboardNames, key) : -1;
            if (bit == InputSystem.ShiftBit) return "Shift";
            if (bit == InputSystem.ControlBit) return "Ctrl";
            var k = Mw2Input.KeyForBit(bit);
            return k != KeyCode.None ? KeyText(k) : key.TrimStart('*');
        }

        bool IsModifierToken(string token) =>
            Shipped != null && ControlBindings.ParseToken(token, out _, out _, out string key) &&
            (key == Shipped.KeyName(InputSystem.ShiftBit) || key == Shipped.KeyName(InputSystem.ControlBit));

        /// <summary>A chord as keys joined with "+"; <paramref name="compact"/> leaves out "(no Ctrl)" and the like.</summary>
        public string ChordText(string chord, bool compact = false)
        {
            var tokens = ControlBindings.Tokens(chord);
            var held = tokens.Where(t => t[0] == '+').OrderBy(t => IsModifierToken(t) ? 0 : 1).Select(TokenText);
            var free = compact ? new List<string>() : tokens.Where(t => t[0] == '-').Select(TokenText).ToList();
            return string.Join("+", held) + (free.Count > 0 ? " (no " + string.Join(", ", free) + ")" : "");
        }

        public string CodeText(int code)
        {
            if (Shipped == null) return code.ToString();
            var k = Mw2Input.KeyForCommandCode(Shipped, code);
            if (k != KeyCode.None) return KeyText(k);
            string mods = ((code & 0x100) != 0 ? "Ctrl+" : "") + ((code & 0x400) != 0 ? "Alt+" : "") + ((code & 0x200) != 0 ? "Shift+" : "");
            k = Mw2Input.KeyForCommandCode(Shipped, code & 0xff);
            return mods + (k != KeyCode.None ? KeyText(k) : $"key 0x{code & 0xff:x2}");
        }

        /// <summary>The bindings of an action or a command, comma separated ("none" when empty).</summary>
        public List<string> BindingParts(string action, string command, bool compact = false) =>
            (action != null ? Chords(action).Select(c => ChordText(c, compact)) : Codes(command).Select(CodeText)).ToList();

        /// <summary>The bindings of an action or a command, comma separated ("none" when empty).</summary>
        public string BindingsText(string action, string command)
        {
            string text = string.Join(", ", BindingParts(action, command));
            return text.Length > 0 ? text : "none";
        }

        static readonly string[] Acronyms = { "MASC", "HUD", "MFD", "MFDS", "AMS", "HTAL", "A", "B", "C", "D", "E", "F" };

        public static string CommandText(string name)
        {
            var words = name.Split('_').Where(w => w.Length > 0).Select(w =>
                Acronyms.Contains(w) ? w : w.Substring(0, 1) + w.Substring(1).ToLowerInvariant()).ToList();
            for (int i = 1; i < words.Count; i++) if (!Acronyms.Contains(words[i])) words[i] = words[i].ToLowerInvariant();
            return string.Join(" ", words).Replace("Starmate", "Lancemate").Replace("Aerotechstrike", "Aerospace strike");
        }

        public static string CommandGroup(string name)
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

        public static string ActionText(string id) => ControlBindings.Action(id)?.Label ?? id;

        // ---- conflicts: the same keys bound to two things ----

        string Physical(string chord)
        {
            var parts = new List<string>();
            foreach (string t in ControlBindings.Tokens(chord))
            {
                if (t[0] != '+' || !ControlBindings.ParseToken(t, out _, out string device, out string key)) continue;
                int bit = device == "keyboard" ? Array.IndexOf(Shipped.KeyboardNames, key) : -1;
                parts.Add(bit == InputSystem.ShiftBit ? "shift" : bit == InputSystem.ControlBit ? "ctrl" : device + ":" + (bit >= 0 ? bit.ToString() : key));
            }
            return string.Join(" ", parts.OrderBy(p => p, StringComparer.Ordinal));
        }

        string Physical(int code)
        {
            var k = Mw2Input.KeyForCommandCode(Shipped, code);
            var parts = new List<string>();
            if (k == KeyCode.None)
            {
                k = Mw2Input.KeyForCommandCode(Shipped, code & 0xff);
                if ((code & 0x100) != 0) parts.Add("ctrl");
                if ((code & 0x200) != 0) parts.Add("shift");
                if ((code & 0x400) != 0) parts.Add("alt");
            }
            int bit = Mw2Input.BitForKey(k);
            parts.Add("keyboard:" + (bit >= 0 ? bit.ToString() : "code" + code));
            return string.Join(" ", parts.OrderBy(p => p, StringComparer.Ordinal));
        }

        /// <summary>The other actions and commands that share a binding with this one.</summary>
        public List<string> Conflicts(string action, string command)
        {
            if (Shipped == null) return new List<string>();
            if (_conflicts == null)
            {
                var users = new Dictionary<string, List<string>>();
                void Add(string key, string who)
                {
                    if (!users.TryGetValue(key, out var list)) users[key] = list = new List<string>();
                    if (!list.Contains(who)) list.Add(who);
                }
                foreach (var a in ControlBindings.Actions)
                    foreach (string c in Chords(a.Id)) Add(Physical(c), a.Label);
                foreach (string c in Commands)
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
            string label = action != null ? ActionText(action) : CommandText(command);
            return _conflicts.TryGetValue(label, out var list2) ? list2 : new List<string>();
        }
    }
}
