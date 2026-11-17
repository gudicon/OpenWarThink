using System;
using System.Collections.Generic;
using System.IO;
using MW2.Formats;
using UnityEngine;

namespace MW2.Shell
{
    /// <summary>
    /// Options -> Mods of the shell: the mods in the Mods folder with their switches and load order. Port addition with no
    /// counterpart in the original program, drawn with Unity's GUI in the look of the shell's Options screens
    /// (ControlsScreen): black, a red title, white / red / grey text options. A modal hook over the current shell screen.
    /// ACCEPT saves the choice to mods.json (Mw2Mods) and applies it at once; ABORT drops the changes.
    /// </summary>
    public static class ModsScreen
    {
        static Mw2Shell _host;
        static ModCatalog _edit;
        static Vector2 _scroll;
        static string _message;

        public static bool IsOpen => _host != null;

        public static void Open(Mw2Shell s)
        {
            _host = s;
            _scroll = Vector2.zero;
            _message = null;
            _edit = Mw2Mods.Edit();
            s.Hook = pass => { if (pass == 0) Close(false); };
        }

        public static void Close(bool save)
        {
            if (_host == null) return;
            if (save) Mw2Mods.Apply(_edit);
            var s = _host;
            _host = null;
            _edit = null;
            s.Hook = null;
            s.Menu.Enable(ShellMenu.Mods, true);
            s.Menu.Enable(ShellMenu.SaveLoad, true);
        }

        // ---- look: as ControlsScreen ----

        static readonly Color White = new Color32(236, 236, 236, 255), Red = new Color32(230, 32, 32, 255),
            Grey = new Color32(140, 140, 140, 255), TitleShadow = new Color32(90, 0, 0, 255);
        static GUIStyle _title, _titleShadow, _grey, _greyWrap, _white, _redWrap, _option, _optionGrey;
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
            _white = Text(White, size, FontStyle.Bold);
            _redWrap = Text(Red, size);
            _redWrap.wordWrap = true;
            _option = Text(White, size);
            _option.hover.textColor = Red;
            _option.active.textColor = Red;
            _option.margin = new RectOffset(0, size, 0, 0);
            _optionGrey = new GUIStyle(_option) { normal = { textColor = Grey } };
        }

        static bool Option(string text, GUIStyle style = null, float width = 0) =>
            width > 0 ? GUILayout.Button(text, style ?? _option, GUILayout.Width(width))
                      : GUILayout.Button(text, style ?? _option, GUILayout.ExpandWidth(false));

        // ---- drawing ----

        /// <summary>Draws the screen (called from the shell's OnGUI, every event).</summary>
        public static void Draw(Mw2Shell host)
        {
            if (_host == null || !ReferenceEquals(host, _host)) return;
            var e = Event.current;
            if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape) { e.Use(); Close(false); return; }
            var picture = host.ScreenRect();
            Styles(picture.height);
            if (e.type == EventType.Repaint) GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), _black);
            var area = new Rect(picture.x + picture.width * 0.03f, picture.y + picture.height * 0.02f,
                                picture.width * 0.94f, picture.height * 0.96f);
            GUILayout.BeginArea(area);

            var titleRect = GUILayoutUtility.GetRect(new GUIContent("MODS"), _title, GUILayout.ExpandWidth(true));
            float shadow = Mathf.Max(2f, _fontSize / 8f);
            GUI.Label(new Rect(titleRect.x + shadow, titleRect.y + shadow, titleRect.width, titleRect.height), "MODS", _titleShadow);
            GUI.Label(titleRect, "MODS", _title);
            GUILayout.Space(_fontSize * 0.5f);
            GUILayout.Label("Folder:  " + _edit.Folder, _greyWrap);
            GUILayout.Label("Click ON / OFF to switch a mod, UP / DOWN to change the order: a mod lower in the list wins when two " +
                            "mods change the same file. ACCEPT applies the choice at once; TEST RANGE keeps it and opens a mission to try weapon effects in.", _greyWrap);
            if (Mw2Mods.Translocated)
                GUILayout.Label("macOS runs this app from a hidden copy, so it cannot see the Mods folder next to it. Quit, run " +
                                "xattr -dr com.apple.quarantine MW2Mercs.app in Terminal and start the game again.", _redWrap);
            if (_message != null) GUILayout.Label(_message, _redWrap);
            GUILayout.Space(_fontSize * 0.5f);

            _scroll = GUILayout.BeginScrollView(_scroll, false, false, GUIStyle.none, GUI.skin.verticalScrollbar, GUIStyle.none);
            if (!_edit.FolderExists)
                GUILayout.Label("There is no Mods folder yet. OPEN MODS FOLDER creates it; unpack each mod into its own folder there.", _greyWrap);
            else if (_edit.Mods.Count == 0)
                GUILayout.Label("No mods in the folder. Unpack each mod into its own folder there.", _greyWrap);
            ModCatalog.Mod moveUp = null, moveDown = null;
            float switchWidth = _option.CalcSize(new GUIContent("OFF")).x + _option.margin.right;
            for (int i = 0; i < _edit.Mods.Count; i++)
            {
                var m = _edit.Mods[i];
                GUILayout.Space(_fontSize * 0.4f);
                GUILayout.BeginHorizontal();
                if (m.Problem != null) GUILayout.Label("", GUILayout.Width(switchWidth));
                else if (Option(m.Enabled ? "ON" : "OFF", m.Enabled ? _option : _optionGrey, switchWidth))
                    _edit.SetEnabled(m, !m.Enabled);
                GUILayout.Label(m.Title, m.Active ? _white : _grey);
                if (m.Author.Length > 0) GUILayout.Label("   by " + m.Author, _grey);
                GUILayout.FlexibleSpace();
                if (i > 0 && Option("UP")) moveUp = m;
                if (i + 1 < _edit.Mods.Count && Option("DOWN")) moveDown = m;
                GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal();
                GUILayout.Space(switchWidth);
                GUILayout.BeginVertical();
                if (m.Description.Length > 0) GUILayout.Label(m.Description, _greyWrap);
                if (m.Problem != null) GUILayout.Label(m.Problem, _redWrap);
                else if (m.Incompatible != null)
                    GUILayout.Label(m.Incompatible + (m.Forced ? " Turned on anyway: it may not work." : " Turned off; ON loads it anyway."), _redWrap);
                else if (m.Files.Count > 0)
                    GUILayout.Label($"{m.Files.Count} file{(m.Files.Count == 1 ? "" : "s")}", _grey);
                GUILayout.EndVertical();
                GUILayout.EndHorizontal();
            }
            var conflicts = _edit.Conflicts().Count > 0 ? _edit.ConflictSummary() : null;
            if (conflicts != null)
            {
                GUILayout.Space(_fontSize);
                GUILayout.Label("CONFLICTS", _white);
                foreach (string line in conflicts) GUILayout.Label(line, _redWrap);
            }
            GUILayout.EndScrollView();

            // Change the list only after it is drawn, so the layout and repaint passes see the same rows.
            if (moveUp != null) _edit.Move(moveUp, -1);
            if (moveDown != null) _edit.Move(moveDown, +1);

            GUILayout.Space(_fontSize * 0.5f);
            bool accept = false, abort = false, range = false;
            GUILayout.BeginHorizontal();
            if (Option("ACCEPT")) accept = true;
            if (Option("OPEN MODS FOLDER")) _message = Mw2Mods.OpenFolder();
            if (Option("RELOAD"))
            {
                // Keep the switches and order made here, read the folder again.
                _edit = ModCatalog.Load(_edit.Folder, _edit.SaveSettings());
                _message = null;
            }
            if (Option("TEST RANGE")) range = true;
            if (Option("ABORT")) abort = true;
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.EndArea();
            if (range)
            {
                // The range tests the choice shown here, so it is kept first.
                Close(true);
                Mw2EffectsRange.Open(host);
            }
            else if (accept || abort) Close(accept);
        }
    }
}
