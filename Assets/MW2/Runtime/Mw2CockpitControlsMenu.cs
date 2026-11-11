using System;
using System.Collections.Generic;
using System.Linq;
using MW2.Formats;
using MW2.Shell;
using UnityEngine;

namespace MW2
{
    /// <summary>
    /// Port extension: key bindings in a mission, as pages of the main menu (window 4) drawn and run by the original
    /// page code (Menu_DrawPage 0x1003cb59), so they look and work like Audio Ctrl or Combat Variables.
    ///   COCKPIT CONTROLS: the action groups, Commands, Restore defaults, Accept (Esc to cancel)
    ///   a group page: one row per action or command, its keys in the value column; at most seven rows, then More
    /// Enter (or the row's digit) on a row waits for a key or mouse button and adds it; Backspace / Delete removes the
    /// row's last binding; Esc while waiting cancels. Accept on the root page saves (controls.json) and the new bindings
    /// apply at once; Esc there drops the changes. The sim stays paused, as for every main menu page. The editing model
    /// is shared with the shell's screen (Shell/ControlsEditor).
    /// </summary>
    public partial class Mw2Cockpit
    {
        const int ControlsRowsPerPage = 7;

        ControlsEditor _controls;           // the working copy while COCKPIT CONTROLS is open
        MainItem _capturing;                // the row waiting for a key
        KeyCode _pendingModifier;
        bool _swallowKeys;                  // a key was just bound: menu keys wait until every key is released
        string _controlsMessage;
        float _controlsMessageUntil;

        static KeyCode[] _keyboardKeys;
        static KeyCode[] KeyboardKeys => _keyboardKeys ??=
            ((KeyCode[])Enum.GetValues(typeof(KeyCode))).Where(k => k > KeyCode.None && k < KeyCode.Mouse0).Distinct().ToArray();

        MainPage BuildControlsMenu()
        {
            var root = new MainPage { Title = "COCKPIT CONTROLS", Hint = ControlsHint };
            root.Opened = () =>
            {
                _controls = new ControlsEditor(Mw2Input.GameFolder);
                _capturing = null;
                _swallowKeys = false;
                ControlsSay(_controls.Error);
                BuildControlsRoot(root);
            };
            root.Accepted = () =>
            {
                _controls?.Save();
                _controls = null;
            };
            root.Cancelled = () => _controls = null;
            return root;
        }

        void BuildControlsRoot(MainPage root)
        {
            root.Items.Clear();
            root.Selected = 0;
            foreach (var g in ControlBindings.Actions.GroupBy(a => a.Group))
                root.Items.Add(new MainItem
                {
                    Type = 0, Text = g.Key,
                    Sub = BindingPages(g.Key.ToUpperInvariant(), g.Select(a => BindingRow(a.Label, a.Id, null)).ToList())
                });
            var commands = new MainPage { Title = "COMMANDS", Hint = ControlsHint };
            if (_controls.Shipped != null)
                foreach (var g in _controls.Commands.GroupBy(ControlsEditor.CommandGroup))
                    commands.Items.Add(new MainItem
                    {
                        Type = 0, Text = g.Key,
                        Sub = BindingPages(g.Key.ToUpperInvariant(), g.Select(c => BindingRow(ControlsEditor.CommandText(c), null, c)).ToList())
                    });
            commands.Items.Add(new MainItem { Type = 6, Text = "(Esc to exit)" });
            root.Items.Add(new MainItem { Type = 0, Text = "Commands", Sub = commands });
            root.Items.Add(new MainItem
            {
                Type = 1, Text = "Restore defaults",
                Confirm = () => { _controls.RestoreAll(); ControlsSay("Game defaults restored. Accept to keep them."); }
            });
            root.Items.Add(new MainItem { Type = 2, Text = "Accept (Esc to cancel)" });
        }

        /// <summary>Pages of at most seven rows; each but the last ends with More, every page with (Esc to exit).</summary>
        MainPage BindingPages(string title, List<MainItem> rows)
        {
            int count = Math.Max(1, (rows.Count + ControlsRowsPerPage - 1) / ControlsRowsPerPage);
            MainPage first = null, previous = null;
            for (int p = 0; p < count; p++)
            {
                var page = new MainPage { Title = count > 1 ? $"{title} {p + 1}/{count}" : title, Hint = ControlsHint };
                page.Items.AddRange(rows.Skip(p * ControlsRowsPerPage).Take(ControlsRowsPerPage));
                if (previous != null) previous.Items.Insert(previous.Items.Count - 1, new MainItem { Type = 0, Text = "More", Sub = page });
                page.Items.Add(new MainItem { Type = 6, Text = "(Esc to exit)" });
                first ??= page;
                previous = page;
            }
            return first;
        }

        MainItem BindingRow(string label, string action, string command)
        {
            var row = new MainItem { Type = 1, Text = label, BindAction = action, BindCommand = command };
            row.ValueText = () => _capturing == row ? "press a key..." : null;
            row.ValueParts = () => _controls?.BindingParts(action, command, true) ?? new List<string>();
            row.Confirm = () =>
            {
                _capturing = row;
                _pendingModifier = KeyCode.None;
                _controlsMessage = null;
            };
            return row;
        }

        void ControlsSay(string message)
        {
            _controlsMessage = message;
            _controlsMessageUntil = Time.unscaledTime + 4f;
        }

        string ControlsHint()
        {
            if (_controlsMessage != null && Time.unscaledTime < _controlsMessageUntil) return "!" + _controlsMessage;
            if (_capturing != null) return "!Press a key or mouse button (Esc cancels)";
            var page = _mainStack.Count > 0 ? _mainStack[_mainStack.Count - 1] : null;
            var item = page != null && page.Selected < page.Items.Count ? page.Items[page.Selected] : null;
            if (_controls != null && item != null && (item.BindAction != null || item.BindCommand != null))
            {
                var others = _controls.Conflicts(item.BindAction, item.BindCommand);
                if (others.Count > 0) return "!Same keys as " + string.Join(", ", others);
                return "Enter: add key   Bksp: remove";
            }
            return "";
        }

        /// <summary>Called after Menu_ReadKey each frame; true when the page must not run this frame.</summary>
        bool UpdateControlsCapture()
        {
            if (_controls == null) { _capturing = null; return false; }
            if (_swallowKeys)
            {
                // The bound key's release would reach the menu as Enter, a digit or Esc.
                _menuKey = 0;
                if (!Input.anyKey) _swallowKeys = false;
                return false;
            }
            if (_capturing != null)
            {
                _menuKey = 0;
                bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
                bool alt = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt) || Input.GetKey(KeyCode.AltGr);
                bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
                foreach (var k in KeyboardKeys)
                {
                    if (!Input.GetKeyDown(k)) continue;
                    if (k == KeyCode.Escape) { FinishCapture(null); return true; }
                    if (ControlsEditor.IsModifier(k)) { _pendingModifier = k; continue; }
                    FinishCapture(_controls.AddKey(_capturing.BindAction, _capturing.BindCommand, k, ctrl, alt, shift));
                    return true;
                }
                if (_pendingModifier != KeyCode.None && Input.GetKeyUp(_pendingModifier))
                {
                    // A modifier pressed and released on its own is bound as a key itself.
                    FinishCapture(_controls.AddKey(_capturing.BindAction, _capturing.BindCommand, _pendingModifier, false, false, false));
                    return true;
                }
                for (int b = 0; b < 3; b++)
                    if (Input.GetMouseButtonDown(b))
                    {
                        FinishCapture(_controls.AddMouse(_capturing.BindAction, _capturing.BindCommand, b));
                        return true;
                    }
                return true;
            }
            if (Input.GetKeyDown(KeyCode.Backspace) || Input.GetKeyDown(KeyCode.Delete))
            {
                var page = _mainStack[_mainStack.Count - 1];
                var item = page.Selected < page.Items.Count ? page.Items[page.Selected] : null;
                if (item?.BindAction != null) _controls.RemoveChord(item.BindAction, _controls.Chords(item.BindAction).Count - 1);
                else if (item?.BindCommand != null) _controls.RemoveCode(item.BindCommand, _controls.Codes(item.BindCommand).Count - 1);
            }
            return false;
        }

        void FinishCapture(string error)
        {
            _capturing = null;
            _pendingModifier = KeyCode.None;
            _swallowKeys = true;
            if (error != null) ControlsSay(error);
            else PlayMenuSound(MainSoundMove);
        }

        /// <summary>The main menu closed without Accept (Abort, Flee, mission end): the changes are dropped.</summary>
        void DropControlsEdit()
        {
            _controls = null;
            _capturing = null;
            _swallowKeys = false;
        }

        /// <summary>The bindings joined with ", " as far as they fit; the rest counted as "+N".</summary>
        string FitParts(List<string> parts, float width)
        {
            if (parts.Count == 0) return "none";
            string text = "";
            for (int i = 0; i < parts.Count; i++)
            {
                string next = i == 0 ? parts[0] : text + ", " + parts[i];
                string rest = i + 1 < parts.Count ? " +" + (parts.Count - i - 1) : "";
                if (TextWidth(next + rest) > width)
                {
                    if (i > 0) return text + " +" + (parts.Count - i);
                    string more = parts.Count > 1 ? " +" + (parts.Count - 1) : "";
                    return FitText(parts[0], width - TextWidth(more)) + more;
                }
                text = next;
            }
            return text;
        }

        /// <summary>Cuts a text to a width with "...".</summary>
        string FitText(string text, float width)
        {
            if (string.IsNullOrEmpty(text) || TextWidth(text) <= width) return text;
            const string dots = "...";
            float limit = width - TextWidth(dots);
            int n = text.Length;
            while (n > 0 && TextWidth(text.Substring(0, n)) > limit) n--;
            return text.Substring(0, n).TrimEnd() + dots;
        }
    }
}
