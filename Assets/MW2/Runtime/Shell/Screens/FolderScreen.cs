using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using MW2.Formats;
using UnityEngine;

namespace MW2.Shell
{
    /// <summary>
    /// The first screen when no usable game folder is known: the Mercenaries folder or the Titanium CD, and an optional
    /// CD image with the mission music. Port addition (the original was installed by its setup program), drawn before
    /// any game data is open, so it uses Unity's GUI in the look of the shell's Options screens (as ControlsScreen):
    /// black, a red title, grey text, white options that turn red under the mouse. BROWSE opens a folder list in the
    /// same look, because a built Player has no system file dialog.
    /// </summary>
    public static class FolderScreen
    {
        static readonly Color White = new Color32(236, 236, 236, 255), Red = new Color32(230, 32, 32, 255),
            Grey = new Color32(140, 140, 140, 255), TitleShadow = new Color32(90, 0, 0, 255);
        static GUIStyle _title, _titleShadow, _grey, _greyWrap, _header, _headerWrap, _red, _redWrap, _option, _field;
        static int _fontSize;
        static float _areaWidth;
        static Texture2D _black, _line;

        // ---- browsing ----
        enum Browse { None, Folder, Cue }
        struct Entry { public string Path, Name, Note; public bool File, Root; }
        static Browse _browse;
        static string _dir;                 // null = the list of drives
        static readonly List<Entry> _entries = new List<Entry>();
        static Vector2 _scroll;
        static string _listError, _here;

        static void Styles(float pictureHeight)
        {
            int size = Mathf.Clamp(Mathf.RoundToInt(pictureHeight / 42f), 12, 30);
            if (_title != null && size == _fontSize) return;
            _fontSize = size;
            _black = Solid(_black, Color.black);
            _line = Solid(_line, Grey);
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
            _headerWrap = new GUIStyle(_header) { wordWrap = true };
            _red = Text(Red, size);
            _redWrap = new GUIStyle(_red) { wordWrap = true };
            _option = Text(White, size);
            _option.hover.textColor = Red;
            _option.active.textColor = Red;
            _option.margin = new RectOffset(0, size * 2, 0, 0);
            // The path fields: white text on black with a grey underline (drawn in Field).
            _field = new GUIStyle(GUI.skin.textField)
            {
                fontSize = size, border = new RectOffset(0, 0, 0, 0), padding = new RectOffset(2, 2, 2, 3),
                margin = new RectOffset(0, size, 0, 0), clipping = TextClipping.Clip,
            };
            foreach (var st in new[] { _field.normal, _field.hover, _field.focused, _field.active,
                         _field.onNormal, _field.onHover, _field.onFocused, _field.onActive })
            {
                st.background = _black;
                st.textColor = White;
            }
            _field.focused.textColor = Red;
        }

        static Texture2D Solid(Texture2D t, Color c)
        {
            if (t != null) return t;
            t = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
            t.SetPixel(0, 0, c);
            t.Apply();
            return t;
        }

        static bool Option(string text, GUIStyle style = null) => GUILayout.Button(text, style ?? _option, GUILayout.ExpandWidth(false));

        static void Title(string text)
        {
            text = Mw2Lang.Tr(text);
            var r = GUILayoutUtility.GetRect(new GUIContent(text), _title, GUILayout.ExpandWidth(true));
            float shadow = Mathf.Max(2f, _fontSize / 8f);
            GUI.Label(new Rect(r.x + shadow, r.y + shadow, r.width, r.height), text, _titleShadow);
            GUI.Label(r, text, _title);
            GUILayout.Space(_fontSize * 0.5f);
        }

        static string Field(string value, string name)
        {
            GUI.SetNextControlName(name);
            // A fixed width keeps BROWSE in place however long the path is.
            float browse = _option.CalcSize(new GUIContent(Mw2Lang.Tr("BROWSE"))).x + _option.margin.left;
            value = GUILayout.TextField(value ?? "", _field, GUILayout.Width(_areaWidth - browse - _field.margin.right));
            var r = GUILayoutUtility.GetLastRect();
            if (Event.current.type == EventType.Repaint) GUI.DrawTexture(new Rect(r.x, r.yMax, r.width, 1), _line);
            return value;
        }

        /// <summary>Draws the screen for <paramref name="s"/> (called from its OnGUI while the game folder is not open).</summary>
        public static void Draw(Mw2Shell s)
        {
            var e = Event.current;
            var picture = s.ScreenRect();
            Styles(picture.height);
            if (e.type == EventType.Repaint) GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), _black);
            var area = new Rect(picture.x + picture.width * 0.03f, picture.y + picture.height * 0.02f,
                                picture.width * 0.94f, picture.height * 0.96f);
            _areaWidth = area.width;
            GUILayout.BeginArea(area);
            if (s.Install != null) DrawCopy(s);
            else if (_browse != Browse.None) DrawBrowser(s);
            else DrawFields(s);
            GUILayout.EndArea();
        }

        static void DrawCopy(Mw2Shell s)
        {
            Title("GAME FILES");
            float p = Mathf.Clamp01(s.Install.Progress);
            GUILayout.Space(_fontSize * 2);
            GUILayout.Label(Mw2Lang.Format("Copying the game from the disc...  %d%", Mathf.RoundToInt(p * 100f)), _header);
            GUILayout.Space(_fontSize * 0.5f);
            var bar = GUILayoutUtility.GetRect(10, _fontSize, GUILayout.ExpandWidth(true));
            if (Event.current.type == EventType.Repaint)
            {
                GUI.DrawTexture(new Rect(bar.x, bar.yMax - 1, bar.width, 1), _line);
                var old = GUI.color;
                GUI.color = Red;
                GUI.DrawTexture(new Rect(bar.x, bar.y, bar.width * p, bar.height - 2), Texture2D.whiteTexture);
                GUI.color = old;
            }
            GUILayout.Space(_fontSize * 0.5f);
            GUILayout.Label(Mw2Lang.Format("To %s", s.Install.Target), _greyWrap);
            GUILayout.Label(Mw2Lang.Tr("The disc is only read, and is not needed after the copy."), _grey);
        }

        static void DrawFields(Mw2Shell s)
        {
            var e = Event.current;
            bool enter = e.type == EventType.KeyDown && (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter);
            if (enter) e.Use();

            Title("GAME FILES");
            GUILayout.Label(Mw2Lang.Tr("OpenWarThink plays MechWarrior 2: Mercenaries from your own copy of the game."), _greyWrap);
            string error = s.FolderError;
            if (error != null && error != Mw2Shell.NoFolderYet) GUILayout.Label(Mw2Lang.Tr(error), _redWrap);
            GUILayout.Space(_fontSize);

            GUILayout.Label(Mw2Lang.Tr("GAME FOLDER"), _header);
            GUILayout.Label(Mw2Lang.Tr("Your Mercenaries folder, or the Titanium CD drive: the game is then copied from the disc once."), _greyWrap);
            GUILayout.BeginHorizontal();
            s.GameFolder = Field(s.GameFolder, "MW2.Folder");
            if (Option(Mw2Lang.Tr("BROWSE"))) StartBrowse(Browse.Folder, s.GameFolder);
            GUILayout.EndHorizontal();
            GUILayout.Space(_fontSize);

            GUILayout.Label(Mw2Lang.Tr("MISSION MUSIC"), _header);
            GUILayout.Label(Mw2Lang.Tr("Optional: the .cue file of a CD image of the game with the music tracks."), _greyWrap);
            GUILayout.BeginHorizontal();
            s.MusicField = Field(s.MusicField, "MW2.Music");
            if (Option(Mw2Lang.Tr("BROWSE"))) StartBrowse(Browse.Cue, s.MusicField);
            GUILayout.EndHorizontal();

            GUILayout.FlexibleSpace();
            GUILayout.BeginHorizontal();
            if (Option(Mw2Lang.Tr("OPEN")) || enter) { GUI.FocusControl(null); s.OpenFromSetup(); }
            if (Option(Mw2Lang.Tr("QUIT"))) Application.Quit();
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
        }

        // ---- the folder list ----

        static void StartBrowse(Browse what, string current)
        {
            GUI.FocusControl(null);
            _browse = what;
            string start = null;
            try
            {
                current = (current ?? "").Trim().Trim('"');
                if (current.Length > 0)
                {
                    if (Directory.Exists(current)) start = current;
                    else if (File.Exists(current)) start = Path.GetDirectoryName(current);
                    else if (Directory.Exists(Path.GetDirectoryName(current))) start = Path.GetDirectoryName(current);
                }
            }
            catch (Exception) { start = null; }
            Go(start == null ? null : Path.GetFullPath(start));
        }

        static void Go(string dir)
        {
            _dir = dir;
            _scroll = Vector2.zero;
            _entries.Clear();
            _listError = null;
            _here = dir == null ? null : Kind(dir);
            try
            {
                if (dir == null) ListRoots();
                else
                {
                    var subdirs = Directory.GetDirectories(dir);
                    Array.Sort(subdirs, StringComparer.OrdinalIgnoreCase);
                    foreach (string d in subdirs)
                    {
                        if (Hidden(d)) continue;
                        _entries.Add(new Entry { Path = d, Name = Path.GetFileName(d), Note = Kind(d) });
                    }
                    if (_browse == Browse.Cue)
                    {
                        var cues = Directory.GetFiles(dir, "*.cue");
                        Array.Sort(cues, StringComparer.OrdinalIgnoreCase);
                        foreach (string f in cues)
                            _entries.Add(new Entry { Path = f, Name = Path.GetFileName(f), File = true });
                    }
                }
            }
            catch (Exception ex) { _listError = ex.Message; }
        }

        static void ListRoots()
        {
            if (Application.platform == RuntimePlatform.WindowsPlayer || Application.platform == RuntimePlatform.WindowsEditor)
            {
                foreach (var drive in DriveInfo.GetDrives())
                {
                    try { if (!drive.IsReady) continue; }
                    catch (Exception) { continue; }
                    string label = VolumeLabel(drive.RootDirectory.FullName);
                    string note = Kind(drive.RootDirectory.FullName);
                    _entries.Add(new Entry
                    {
                        Path = drive.RootDirectory.FullName, Root = true,
                        Name = drive.Name + (string.IsNullOrEmpty(label) ? "" : "  " + label),
                        Note = note,
                    });
                }
                return;
            }
            // macOS and others: the mounted volumes (a CD or a mounted image appears there), the home folder, the root.
            string home = Environment.GetFolderPath(Environment.SpecialFolder.Personal);
            if (!string.IsNullOrEmpty(home)) _entries.Add(new Entry { Path = home, Name = home, Root = true });
            if (Directory.Exists("/Volumes"))
                foreach (string v in Directory.GetDirectories("/Volumes"))
                    _entries.Add(new Entry { Path = v, Name = v, Note = Kind(v), Root = true });
            _entries.Add(new Entry { Path = "/", Name = "/", Root = true });
        }

        // Mono's DriveInfo.VolumeLabel returns the drive path on Windows, so the label is read from the system.
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool GetVolumeInformationW(string root, StringBuilder name, int nameSize, out uint serial,
            out uint maxComponent, out uint flags, StringBuilder fileSystem, int fileSystemSize);

        static string VolumeLabel(string root)
        {
            try
            {
                var name = new StringBuilder(261);
                return GetVolumeInformationW(root, name, name.Capacity, out _, out _, out _, null, 0) ? name.ToString() : null;
            }
            catch (Exception) { return null; }
        }

        static bool Hidden(string dir)
        {
            if (Path.GetFileName(dir).StartsWith(".", StringComparison.Ordinal)) return true;
            try { return (File.GetAttributes(dir) & (FileAttributes.Hidden | FileAttributes.System)) != 0; }
            catch (Exception) { return true; }
        }

        /// <summary>What a folder holds, shown beside it: an installed game or the Titanium CD.</summary>
        static string Kind(string dir)
        {
            try
            {
                if (DiscCopy.IsDisc(dir)) return "Titanium CD";
                if (File.Exists(Path.Combine(dir, "MW2.PRJ"))) return "Mercenaries";
            }
            catch (Exception) { }
            return null;
        }

        static void DrawBrowser(Mw2Shell s)
        {
            var e = Event.current;
            if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape) { e.Use(); _browse = Browse.None; return; }

            Title(_browse == Browse.Folder ? "GAME FOLDER" : "MISSION MUSIC");
            GUILayout.Label(Mw2Lang.Tr(_browse == Browse.Folder
                ? "Open your Mercenaries folder or the Titanium CD drive and choose USE THIS FOLDER."
                : "Find the .cue file of the CD image and click it."), _greyWrap);
            GUILayout.Space(_fontSize * 0.5f);
            GUILayout.Label(_dir ?? Mw2Lang.Tr("Drives"), _headerWrap);
            if (_here != null) GUILayout.Label(Mw2Lang.Tr(_here), _red);
            if (_listError != null) GUILayout.Label(Mw2Lang.Tr(_listError), _redWrap);
            GUILayout.Space(_fontSize * 0.5f);

            string go = null, pick = null;
            _scroll = GUILayout.BeginScrollView(_scroll, false, false, GUIStyle.none, GUI.skin.verticalScrollbar, GUIStyle.none);
            if (_dir != null && Option(Mw2Lang.Tr(".. (up)")))
            {
                var parent = Directory.GetParent(_dir);
                go = parent == null ? "" : parent.FullName;
            }
            foreach (var entry in _entries)
            {
                GUILayout.BeginHorizontal();
                if (Option(entry.File || entry.Root ? entry.Name : entry.Name + Path.DirectorySeparatorChar))
                {
                    if (entry.File) pick = entry.Path;
                    else go = entry.Path;
                }
                if (entry.Note != null) GUILayout.Label(Mw2Lang.Tr(entry.Note), _red);
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
            }
            if (_entries.Count == 0 && _listError == null)
                GUILayout.Label(Mw2Lang.Tr(_browse == Browse.Cue ? "No folders or .cue files here." : "No folders here."), _grey);
            GUILayout.EndScrollView();

            GUILayout.Space(_fontSize * 0.5f);
            GUILayout.BeginHorizontal();
            if (_browse == Browse.Folder)
            {
                if (_dir != null) { if (Option(Mw2Lang.Tr("USE THIS FOLDER"))) pick = _dir; }
                else GUILayout.Label(Mw2Lang.Tr("USE THIS FOLDER"), new GUIStyle(_grey) { margin = _option.margin });
            }
            if (Option(Mw2Lang.Tr("CANCEL"))) _browse = Browse.None;
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            // Change the list only after it is drawn, so the layout and repaint passes see the same rows.
            if (pick != null)
            {
                if (_browse == Browse.Folder) s.GameFolder = pick;
                else s.MusicField = pick;
                _browse = Browse.None;
            }
            else if (go != null && e.type != EventType.Layout) Go(go.Length == 0 ? null : go);
        }
    }
}
