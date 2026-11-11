using System;
using MW2.Formats;
using UnityEngine;

namespace MW2.Shell
{
    /// <summary>
    /// Options -> Cockpit Controls of the shell: lets the player bind every cockpit action and command to keys or mouse
    /// buttons. Port extension drawn with Unity's GUI in the look of the original screen (black, red title, white /
    /// red / grey text options); the original item opens FUN_1000f864, a fixed set of choices that rewrites inputw.map. The game's map files stay as they are; the
    /// changes go to controls.json (ControlsEditor, ControlBindings, Mw2Controls). A modal hook over the current shell
    /// screen, like Merc Lords. In a mission the same bindings are edited through the main menu (Mw2Cockpit).
    /// </summary>
    public static class ControlsScreen
    {
        static object _host;
        static Action _closed;
        static ControlsEditor _editor;
        static Vector2 _scroll;
        static string _message;
        static string _captureAction, _captureCommand;
        static KeyCode _pendingModifier;
        static bool _leftClickCancels;

        public static bool IsOpen => _host != null;

        /// <summary>Options -> Cockpit Controls of the shell.</summary>
        public static void Open(Mw2Shell s)
        {
            Open(s, s.GameFolder, () =>
            {
                s.Hook = null;
                s.Menu.Enable(ShellMenu.CockpitControls, true);
                s.Menu.Enable(ShellMenu.SaveLoad, true);
            });
            s.Hook = pass => { if (pass == 0) Close(false); };
        }

        /// <summary>Opens the screen for <paramref name="host"/>, which draws it from its OnGUI (see Draw).</summary>
        public static void Open(object host, string gameFolder, Action closed)
        {
            _host = host;
            _closed = closed;
            _captureAction = _captureCommand = null;
            _scroll = Vector2.zero;
            _editor = new ControlsEditor(gameFolder);
            _message = _editor.Error;
        }

        public static void Close(bool save)
        {
            if (_host == null) return;
            if (save) _editor.Save();
            var closed = _closed;
            _host = null;
            _closed = null;
            StopCapture();
            closed?.Invoke();
        }

        // ---- capturing a key ----

        static bool Capturing => _captureAction != null || _captureCommand != null;

        static void StopCapture() { _captureAction = _captureCommand = null; _pendingModifier = KeyCode.None; }

        static void Done(string error) { _message = error; StopCapture(); }

        static void HandleCapture(Event e)
        {
            if (!Capturing) return;
            if (e.type == EventType.KeyDown && e.keyCode != KeyCode.None)
            {
                if (e.keyCode == KeyCode.Escape) StopCapture();
                else if (ControlsEditor.IsModifier(e.keyCode)) _pendingModifier = e.keyCode;
                else Done(_editor.AddKey(_captureAction, _captureCommand, e.keyCode, e.control, e.alt, e.shift));
                e.Use();
            }
            else if (e.type == EventType.KeyUp && e.keyCode != KeyCode.None && e.keyCode == _pendingModifier)
            {
                // A modifier pressed and released on its own is bound as a key itself.
                Done(_editor.AddKey(_captureAction, _captureCommand, e.keyCode, false, false, false));
                e.Use();
            }
            else if (e.type == EventType.MouseDown && e.button != 0)
            {
                Done(_editor.AddMouse(_captureAction, _captureCommand, e.button));
                e.Use();
            }
            // The left button binds only when clicked on the row's own capture button (see Row); a left click
            // anywhere else cancels the capture and still works as a click.
            else if (e.type == EventType.MouseDown && e.button == 0) _leftClickCancels = true;
        }

        // ---- look: the original Cockpit Controls screen (FUN_1000f864): black, a red title, text options in
        // white (selectable), red (selected) and grey (unselectable), no buttons ----

        static readonly Color White = new Color32(236, 236, 236, 255), Red = new Color32(230, 32, 32, 255),
            Grey = new Color32(140, 140, 140, 255), TitleShadow = new Color32(90, 0, 0, 255);
        static GUIStyle _title, _titleShadow, _grey, _greyWrap, _header, _red, _option, _optionRed, _legendWhite, _legendRed, _legendGrey;
        static int _fontSize;
        static Texture2D _black;

        static void Styles(float pictureHeight)
        {
            int size = Mathf.Clamp(Mathf.RoundToInt(pictureHeight / 42f), 12, 30);
            if (_title != null && size == _fontSize) return;
            _fontSize = size;
            if (_black == null)
            {
                _black = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
                _black.SetPixel(0, 0, Color.black);
                _black.Apply();
            }
            GUIStyle Text(Color c, int fontSize, FontStyle style = FontStyle.Normal) => new GUIStyle(GUI.skin.label)
            {
                fontSize = fontSize, fontStyle = style, normal = { textColor = c, background = null },
                hover = { textColor = c }, active = { textColor = c }, padding = new RectOffset(0, 0, 1, 1),
                margin = new RectOffset(0, 0, 0, 0), wordWrap = false
            };
            _title = Text(Red, size * 2, FontStyle.Bold);
            _title.alignment = TextAnchor.MiddleCenter;
            _titleShadow = new GUIStyle(_title) { normal = { textColor = TitleShadow } };
            _grey = Text(Grey, size);
            _greyWrap = new GUIStyle(_grey) { wordWrap = true };
            _header = Text(White, size, FontStyle.Bold);
            _red = Text(Red, size);
            // A selectable option: white, red under the mouse and while pressed.
            _option = Text(White, size);
            _option.hover.textColor = Red;
            _option.active.textColor = Red;
            _option.margin = new RectOffset(0, size, 0, 0);
            _optionRed = new GUIStyle(_option) { normal = { textColor = Red } };
            _legendWhite = Text(White, size);
            _legendRed = Text(Red, size);
            _legendGrey = Text(Grey, size);
            foreach (var s in new[] { _legendWhite, _legendRed, _legendGrey }) s.alignment = TextAnchor.MiddleCenter;
        }

        static bool Option(string text, GUIStyle style, params GUILayoutOption[] options) =>
            GUILayout.Button(text, style, options);

        // ---- drawing ----

        /// <summary>Draws the screen when <paramref name="host"/> opened it (called from the host's OnGUI, every event).</summary>
        public static void Draw(object host)
        {
            if (_host == null || !ReferenceEquals(host, _host)) return;
            var e = Event.current;
            HandleCapture(e);
            var picture = host is Mw2Shell s ? s.ScreenRect() : new Rect(0, 0, Screen.width, Screen.height);
            Styles(picture.height);

            // The whole window black, like the original screen over the shell.
            if (e.type == EventType.Repaint) GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), _black);
            var area = new Rect(picture.x + picture.width * 0.03f, picture.y + picture.height * 0.02f,
                                picture.width * 0.94f, picture.height * 0.96f);
            GUILayout.BeginArea(area);

            var titleRect = GUILayoutUtility.GetRect(new GUIContent("COCKPIT CONTROLS"), _title, GUILayout.ExpandWidth(true));
            float shadow = Mathf.Max(2f, _fontSize / 8f);
            GUI.Label(new Rect(titleRect.x + shadow, titleRect.y + shadow, titleRect.width, titleRect.height), "COCKPIT CONTROLS", _titleShadow);
            GUI.Label(titleRect, "COCKPIT CONTROLS", _title);
            GUILayout.Space(_fontSize * 0.5f);
            GUILayout.Label("Current Config:  " + (_editor.Changes().Actions.Count + _editor.Changes().Commands.Count == 0 ? "Default Config" : "Custom Config"), _grey);
            GUILayout.Label("Click ADD KEY and press a key or mouse button; click a key to remove it. Esc cancels. " +
                            "Saved to " + Mw2Controls.FileName + "; the game's map files are not changed. In a mission: Esc, Cockpit Controls.", _greyWrap);
            if (_message != null) GUILayout.Label(_message, _red);
            GUILayout.Space(_fontSize * 0.5f);

            if (_editor.Shipped == null)
            {
                if (Option("ABORT", _option)) Close(false);
                GUILayout.EndArea();
                return;
            }

            _scroll = GUILayout.BeginScrollView(_scroll, false, false, GUIStyle.none, GUI.skin.verticalScrollbar, GUIStyle.none);
            string group = null;
            foreach (var a in ControlBindings.Actions)
            {
                if (a.Group != group) { group = a.Group; Header(group); }
                Row(a.Label, a.Id, null);
            }
            string commandGroup = null;
            foreach (string c in _editor.Commands)
            {
                string g = ControlsEditor.CommandGroup(c);
                if (g != commandGroup) { commandGroup = g; Header(g); }
                Row(ControlsEditor.CommandText(c), null, c);
            }
            GUILayout.EndScrollView();

            GUILayout.Space(_fontSize * 0.5f);
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            GUILayout.Label("WHITE = Selectable Option", _legendWhite);
            GUILayout.Space(_fontSize * 2);
            GUILayout.Label("RED = Selected Option", _legendRed);
            GUILayout.Space(_fontSize * 2);
            GUILayout.Label("GREY = Unselectable", _legendGrey);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(_fontSize * 0.5f);
            GUILayout.BeginHorizontal();
            if (Option("ACCEPT", _option)) { StopCapture(); Close(true); }
            if (Option("RESTORE DEFAULTS", _option))
            {
                StopCapture();
                _editor.RestoreAll();
                _message = "All bindings restored to the game's defaults. ACCEPT to keep them.";
            }
            if (Option("ABORT", _option)) { StopCapture(); Close(false); }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.EndArea();
            if (_leftClickCancels) { _leftClickCancels = false; StopCapture(); }
        }

        static void Header(string text)
        {
            GUILayout.Space(_fontSize * 0.6f);
            GUILayout.Label(text.ToUpperInvariant(), _header);
        }

        static void Row(string label, string action, string command)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, _grey, GUILayout.Width(_fontSize * 16));
            if (action != null)
            {
                var chords = _editor.Chords(action);
                for (int i = 0; i < chords.Count; i++)
                    if (Option(_editor.ChordText(chords[i]), _option)) { _editor.RemoveChord(action, i); break; }
            }
            else
            {
                var codes = _editor.Codes(command);
                for (int i = 0; i < codes.Count; i++)
                    if (Option(_editor.CodeText(codes[i]), _option)) { _editor.RemoveCode(command, i); break; }
            }
            bool capturing = action != null ? _captureAction == action : _captureCommand == command;
            var content = new GUIContent(!capturing ? "ADD KEY" : action != null ? "PRESS A KEY (or click here for Mouse left)" : "PRESS A KEY");
            var style = capturing ? _optionRed : _option;
            Rect r = GUILayoutUtility.GetRect(content, style, GUILayout.ExpandWidth(false));
            var ev = Event.current;
            if (capturing && action != null && ev.type == EventType.MouseDown && ev.button == 0 && r.Contains(ev.mousePosition))
            {
                _leftClickCancels = false;
                Done(_editor.AddMouse(action, null, 0));
                ev.Use();
            }
            else if (GUI.Button(r, content, style))
            {
                StopCapture();
                if (!capturing) { _captureAction = action; _captureCommand = command; _leftClickCancels = false; }
            }
            GUILayout.FlexibleSpace();
            bool changed = action != null ? _editor.ActionChanged(action) : _editor.CommandChanged(command);
            if (changed && Option("DEFAULT", _option))
            {
                if (action != null) _editor.ResetAction(action);
                else _editor.ResetCommand(command);
            }
            GUILayout.EndHorizontal();
            var others = _editor.Conflicts(action, command);
            if (others.Count > 0) GUILayout.Label("      Same keys as: " + string.Join(", ", others), _red);
        }
    }
}
