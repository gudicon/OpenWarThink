using System;
using System.Collections.Generic;
using System.IO;
using MW2.Formats;
using UnityEngine;

namespace MW2
{
    /// <summary>
    /// The seven HUD windows and the HUD keys.
    ///
    /// Hud_CreateGauges 0x10016ed7 gives gauges 2, 17, 22, 26, 31, 32 and 33 (window objects DAT_1025f088, f0c4, f0d8,
    /// f0e8, f0fc, f100, f104) the window draw FUN_1003e925 0x1003e925, which draws by the window mode (+0x30):
    /// 0 nothing, 1 the damage wireframe (Hud_DrawDamageWire; also the default branch), 2 the HTAL armour columns
    /// (FUN_10061260), 3 the rear view (heading + 180, DAT_1023c860 is never set), 4 the forward view (the cockpit
    /// camera), 5 the view down (from the mech position, pitch 45), 6 the overhead map (FUN_1004fae4), 7 the missile
    /// camera (FUN_10012e05 / FUN_10012e37), 8 the target window (Hud_DrawTargetWindow), 9 speed, 10 altitude,
    /// 11 heat bar (Hud_DrawHeat 0x1006994c), 12 heat dial (Hud_DrawHeatDialWindow), 13 dH/dT (Hud_DrawHeatRate).
    /// The views 3, 4, 5 and 7 and the target window render the world at zoom 2.0 into the window: Hud_RenderView
    /// 0x100325e8 saves the camera zoom (+0x18), writes the argument and restores it, so the cockpit zoom (Z) is not
    /// used there (FUN_100325e8, 3D pass DAT_100a6670 = 1;
    /// 3 / 4 / 5 hide the player's own model, FUN_1005ab7f); in the 2D pass views 3, 5 and 7 add a frame in colour 6
    /// and the sprite VP_RER / VP_DWN / VP_WPN at (window width / 2, 2) (FUN_1003f263), view 4 a frame in colour 6.
    /// Defaults (FUN_1003e67b 0x1003e67b): 1, 8, 9, 10, 0, 12, 13.
    ///
    /// Keys (Input_Command 0x100267c0): 2 (F1) cycles window 2 through the 14 modes (FUN_1003e654), 3..8 (Shift+F1..F6)
    /// the windows f0c4, f0fc, f100, f104, f0e8, f0d8; 0xd / 0xe / 0xf / 0x10 set window 2 to 2 / 3 / 5 / 7 or back
    /// to 0, 0x11 toggles it between 1 and 0; 0x12 cycles the target view DAT_100a0544 0..2; 0xb the compass
    /// (DAT_10099fa4), 0xc the weapon rows (FUN_10017446, _DAT_1008f590); 9 restores the defaults; 10 saves mercs.hud
    /// (FUN_1003e3c0) and sets the HUD state 2; 0x2a cycles the HUD state DAT_10099f98 0..2 (1: defaults, saved as the
    /// objectives backup; 2: mercs.hud, else 0) unless the overhead map is up; 0x13 the cockpit shell (DAT_1008fcf4);
    /// 0x25 follows the player's last missile with the main camera (camera mode 3). Each of 2..8, 0xb..0x12 and 0x2a
    /// plays sample 0xf0 while the mech runs (game piece flag 0x2000); its rate index is ignored by Sound_Start.
    /// HUD state 0 skips Hud_UpdatePlayer 0x100176cb: no gauges, radar, compass, reticle or nav marker.
    /// mercs.hud: 7 x i32 window modes (f088, f0c4, f0d8, f0e8, f0fc, f100, f104), i32 weapon rows, i32 compass, i32 radar
    /// mode; the original keeps it in the game folder, the port writes it to Application.persistentDataPath (the game
    /// folder is read-only here) and reads it from there, else from the game folder.
    /// Sensor damage: Hud_DrawWindow 0x1003e925 uses gauge +6 and the shared DAT_100ad388 blank latch for modes 3..8.
    /// </summary>
    public partial class Mw2Cockpit
    {
        /// <summary>Gauges of the seven windows, in the order of the window objects f088, f0c4, f0d8, f0e8, f0fc, f100, f104.</summary>
        public static readonly int[] WindowGauges = { 2, 17, 22, 26, 31, 32, 33 };
        static readonly int[] DefaultWindowModes = { 1, 8, 9, 10, 0, 12, 13 };

        [Header("HUD windows and keys (FUN_1003e925, Input_Command 0x100267c0)")]
        public int[] WindowModes = { 1, 8, 9, 10, 0, 12, 13 };     // window +0x30
        readonly int[] _savedWindowModes = { 1, 8, 9, 10, 0, 12, 13 };    // DAT_1023c880, Hud_SaveWindowModes 0x1003e7f3
        public int HudState = 1;            // DAT_10099f98: 0 off, 1 defaults, 2 mercs.hud
        public bool WeaponRowsShown = true; // _DAT_1008f590
        public bool CompassShown = true;    // DAT_10099fa4
        public int TargetViewMode = 1;      // DAT_100a0544: 0 off, 1 material mode 3, 2 normal materials
        public bool ShellShown = true;      // DAT_1008fcf4
        public bool MissileCamera;          // camera mode 3 (FUN_1002372f)
        bool _sensorBlank;                   // DAT_100ad388, shared by all windows
        bool _targetSensorBlank;             // DAT_100b18b8, independent target-window latch
        readonly bool[] _windowHidden = new bool[7];
        int _sensorFrame = -1;
        int _targetSensorFrame = -1;
        bool _targetSensorHidden;

        /// <summary>Hud_DrawTargetWindow 0x1006606e: gauge 17 uses its own blank latch and the same 3/7 roll thresholds.</summary>
        bool TargetSensorHidden()
        {
            if (_targetSensorFrame != Time.frameCount)
            {
                _targetSensorFrame = Time.frameCount;
                int level = _combat != null && _combat.GaugeDamage != null && _combat.GaugeDamage.Length > 17
                    ? _combat.GaugeDamage[17] : 0;
                _targetSensorHidden = HudSensorDamage.HideWindow(level, 8, ref _targetSensorBlank,
                    n => UnityEngine.Random.Range(0, n));
            }
            return _targetSensorHidden;
        }

        /// <summary>Hud_DrawWindow 0x1003e925: choose a damaged window's visibility once per rendered frame.</summary>
        void UpdateSensorWindows()
        {
            if (_sensorFrame == Time.frameCount) return;
            _sensorFrame = Time.frameCount;
            for (int i = 0; i < WindowGauges.Length; i++)
            {
                int g = WindowGauges[i];
                int level = _combat != null && _combat.GaugeDamage != null && g < _combat.GaugeDamage.Length
                    ? _combat.GaugeDamage[g] : 0;
                _windowHidden[i] = HudSensorDamage.HideWindow(level, WindowModes[i], ref _sensorBlank,
                    n => UnityEngine.Random.Range(0, n));
            }
        }

        bool WindowHidden(int g)
        {
            UpdateSensorWindows();
            for (int i = 0; i < WindowGauges.Length; i++) if (WindowGauges[i] == g) return _windowHidden[i];
            return false;
        }
        public HudSprite ViewRearIcon, ViewDownIcon, ViewMissileIcon;   // VP_RER6, VP_DWN6, VP_WPN6 (SHP 0xd6 / 0xd3 / 0xd9 + 1)

        // mercs.hud (DAT_100ad384 = loaded, DAT_1023c8c0.. the stored values).
        bool _hudFileLoaded;
        readonly int[] _hudFileModes = new int[7];
        int _hudFileRows, _hudFileCompass, _hudFileRadar;

        /// <summary>Game piece flag 0x2000: the mech runs (power state 2).</summary>
        bool Running => _combat != null && _combat.PowerState == 2 && !_combat.Destroyed;

        void ToggleTone()
        {
            if (Running && Mw2Audio.Instance != null) Mw2Audio.Instance.Play(0xf0, transform.position, true);
        }

        void SetDefaultWindows()        // FUN_1003e67b
        {
            for (int i = 0; i < 7; i++) WindowModes[i] = DefaultWindowModes[i];
            WeaponRowsShown = true;
            CompassShown = true;
            _radarRequest = 1;
            RequestRadarMode(1);
        }

        void SaveWindowModes()          // Hud_SaveWindowModes 0x1003e7f3
        {
            for (int i = 0; i < 7; i++) _savedWindowModes[i] = WindowModes[i];
        }

        void RestoreWindowModes()       // Hud_RestoreWindowModes 0x1003e85c
        {
            for (int i = 0; i < 7; i++) WindowModes[i] = _savedWindowModes[i];
        }

        void WindowsOff()               // Hud_WindowsOff 0x1003e74b
        {
            for (int i = 0; i < 7; i++) WindowModes[i] = 0;
        }

        static string HudFileWritePath => Path.Combine(Application.persistentDataPath, "mercs.hud");

        /// <summary>FUN_1003e4aa 0x1003e4aa, at mission start.</summary>
        void LoadHudFile()
        {
            string path = HudFileWritePath;
            if (!File.Exists(path) && Mw2Input.GameFolder != null) path = Path.Combine(Mw2Input.GameFolder, "mercs.hud");
            _hudFileLoaded = false;
            try
            {
                if (!File.Exists(path)) return;
                var d = File.ReadAllBytes(path);
                if (d.Length < 40) return;
                for (int i = 0; i < 7; i++) _hudFileModes[i] = BitConverter.ToInt32(d, i * 4);
                _hudFileRows = BitConverter.ToInt32(d, 28);
                _hudFileCompass = BitConverter.ToInt32(d, 32);
                _hudFileRadar = BitConverter.ToInt32(d, 36);
                _hudFileLoaded = true;
            }
            catch (IOException e) { Debug.LogWarning($"MW2: mercs.hud: {e.Message}"); }
        }

        /// <summary>FUN_1003e3c0 0x1003e3c0: the current windows (via the objectives backup), rows, compass and radar mode.</summary>
        void SaveHudFile()
        {
            _hudFileLoaded = true;
            SaveWindowModes();
            for (int i = 0; i < 7; i++) _hudFileModes[i] = _savedWindowModes[i];
            _hudFileRows = WeaponRowsShown ? 1 : 0;
            _hudFileCompass = CompassShown ? 1 : 0;
            _hudFileRadar = RadarMode;
            try
            {
                var d = new byte[40];
                for (int i = 0; i < 7; i++) BitConverter.GetBytes(_hudFileModes[i]).CopyTo(d, i * 4);
                BitConverter.GetBytes(_hudFileRows).CopyTo(d, 28);
                BitConverter.GetBytes(_hudFileCompass).CopyTo(d, 32);
                BitConverter.GetBytes(_hudFileRadar).CopyTo(d, 36);
                File.WriteAllBytes(HudFileWritePath, d);
            }
            catch (IOException e) { Debug.LogWarning($"MW2: mercs.hud: {e.Message}"); }
        }

        /// <summary>FUN_1003e549 0x1003e549: applies mercs.hud; false when none was loaded or saved.</summary>
        bool ApplyHudFile()
        {
            if (!_hudFileLoaded) return false;
            for (int i = 0; i < 7; i++) WindowModes[i] = _hudFileModes[i];
            WeaponRowsShown = _hudFileRows != 0;
            CompassShown = _hudFileCompass != 0;
            _radarRequest = _hudFileRadar;
            RequestRadarMode(_hudFileRadar);
            return true;
        }

        bool _hudStarted;

        /// <summary>The HUD keys of Input_Command 0x100267c0 (player only, once per frame).</summary>
        void UpdateHudCommands()
        {
            if (!_hudStarted)
            {
                _hudStarted = true;
                LoadHudFile();
            }
            // Windows by command 2..8: f088, f0c4, f0fc, f100, f104, f0e8, f0d8.
            int[] cycleWindow = { 0, 1, 4, 5, 6, 3, 2 };
            for (int c = 2; c <= 8; c++)
                if (Mw2Input.Command(c))
                {
                    int i = cycleWindow[c - 2];
                    WindowModes[i] = WindowModes[i] + 1 == 14 ? 0 : WindowModes[i] + 1;     // FUN_1003e654
                    ToggleTone();
                }
            if (Mw2Input.Command(9)) SetDefaultWindows();
            if (Mw2Input.Command(10))
            {
                SaveHudFile();
                HudState = 2;
                ShowMessage("Saving HUD configuration", 0x21f);
            }
            if (Mw2Input.Command(0xb)) { CompassShown = !CompassShown; ToggleTone(); }
            if (Mw2Input.Command(0xc)) { WeaponRowsShown = !WeaponRowsShown; ToggleTone(); }
            if (Mw2Input.Command(0xd)) { WindowModes[0] = WindowModes[0] != 2 ? 2 : 0; ToggleTone(); }
            if (Mw2Input.Command(0xe)) { WindowModes[0] = WindowModes[0] != 3 ? 3 : 0; ToggleTone(); }
            if (Mw2Input.Command(0xf)) { WindowModes[0] = WindowModes[0] != 5 ? 5 : 0; ToggleTone(); }
            if (Mw2Input.Command(0x10)) { WindowModes[0] = WindowModes[0] != 7 ? 7 : 0; ToggleTone(); }
            if (Mw2Input.Command(0x11)) { WindowModes[0] = WindowModes[0] != 1 ? 1 : 0; ToggleTone(); }
            if (Mw2Input.Command(0x12)) { TargetViewMode = TargetViewMode + 1 > 2 ? 0 : TargetViewMode + 1; ToggleTone(); }
            if (Mw2Input.Command(0x13)) ShellShown = !ShellShown;
            if (Mw2Input.Command(0x25) && Mw2Projectile.FollowLastMissile()) MissileCamera = true;
            if (Mw2Input.Command(0x2a) && RadarMode != 4)
            {
                HudState = (HudState + 1) % 3;
                if (HudState == 1)
                {
                    SetDefaultWindows();
                    SaveWindowModes();
                }
                else if (HudState == 2 && !ApplyHudFile()) HudState = 0;
                ObjectivesShown = false;
                ToggleTone();
            }
            // DISPLAY_OBJECTIVES 0x58 (single player: DAT_100b18fc 0 <-> 1) only with the HUD on: opening saves the window
            // modes and turns the windows off, closing restores them.
            if (Mw2Input.Command(0x58) && HudState != 0)
            {
                bool was = ObjectivesShown;
                ObjectivesShown = !ObjectivesShown;
                if (!ObjectivesShown) RestoreWindowModes();
                else if (!was)
                {
                    SaveWindowModes();
                    WindowsOff();
                }
            }
            UpdateRadarCommands();
            if (TryGetComponent<Mw2LodGroup>(out var lods)) lods.ShellHidden = !ShellShown;
        }

        /// <summary>FUN_1003e925 for one window: draws mode <paramref name="mode"/> into gauge <paramref name="g"/>.</summary>
        void DrawWindow(int g, int mode)
        {
            if (g == 17 && !RadarAvailable) return; // Hud_CreateGauges 10016ed7: gauge 17 disabled with network radar off.
            if (g != 17 && WindowHidden(g)) return;
            switch (mode)
            {
                case 0: break;
                case 2: DrawHtal(g); break;
                case 3: DrawWindowView(g, ViewRearIcon); break;
                case 4: DrawWindowView(g, null); break;
                case 5: DrawWindowView(g, ViewDownIcon); break;
                case 6: DrawWindowMap(g); break;
                case 7: DrawWindowView(g, ViewMissileIcon); break;
                case 8: DrawTargetWindow(g); break;
                case 9: DrawSpeedWindow(g); break;
                case 10: DrawAltitudeWindow(g); break;
                case 11: DrawHeatWindow(g); break;
                case 12: DrawHeatDialWindow(g); break;
                case 13: DrawHeatRateWindow(g); break;
                default: DrawDamage(g); break;
            }
        }

        /// <summary>The window's size in 640x480 pixels (the surface of the original window).</summary>
        (int w, int h) WindowPixels(int g)
        {
            var u = Layout.Rects[g];
            return (u.width * 2, (int)(u.height * 2.4f));
        }

        /// <summary>Hud_DrawHeatDialWindow 0x10069a8f: the dial and "Heat" (0x0e), "Shutdown..." / "Overridden" (0x0b).</summary>
        void DrawHeatDialWindow(int g)
        {
            DrawHeatDial(g);
            DrawText(HeatLabel, GaugeOrigin(g), _combat.ShutdownSequence ? 0x0b : 0x0e);
        }

        string HeatLabel => !_combat.ShutdownSequence ? "Heat" : _combat.ShutdownOverridden ? "Overridden" : "Shutdown...";

        /// <summary>The text point of gauge 25 (window +0xa8 of DAT_1025f0e4) inside window <paramref name="g"/>.</summary>
        Vector2 Gauge25Text(int g)
        {
            var r = GaugeRect(g);
            return new Vector2(r.x + Layout.Origins[25].x * Layout.Rects[25].width * Sx, r.y + Layout.Origins[25].y * Layout.Rects[25].height * Sy);
        }

        /// <summary>
        /// Hud_DrawHeatRate 0x10069bce: "dH/dT" at gauge 25's text point, then the bar in the window box +0x94..+0xa0
        /// (0x10028ee4: x 0, y 0.5, w 0.5, h 0x2db7/65536 of the window).
        /// </summary>
        void DrawHeatRateWindow(int g)
        {
            var r = GaugeRect(g);
            DrawText("dH/dT", Gauge25Text(g), 0x0e);
            DrawHeatRate(new Rect(r.x, r.y + 0.5f * r.height, 0.5f * r.width, 0x2db7 / 65536f * r.height));
        }

        /// <summary>
        /// Hud_DrawHeat 0x1006994c: the heat bar (Hud_DrawHeatBar 0x10029747) and the label at gauge 25's text point.
        /// Bar box +0x74..+0x80 (0x10028df0: x 0, y 0.5, w 0.5, h 0x2db7/65536 of the window); target trunc(trunc(heat)
        /// * w / 100), filter shift 1; v >= w: all 0x0b; v < 1: all 7; otherwise v (or w - v from half on, middle 0x0b)
        /// wide ends in colour 3 around a middle of 7 / 0x0b (Hud_DrawShadedBar rows c-1, c, c-1, c-2).
        /// Shares the filter at 0x10090658 with the heat dial (whose target is heat * 65536 * W / 100).
        /// </summary>
        void DrawHeatWindow(int g)
        {
            var (winW, winH) = WindowPixels(g);
            int x = 0, y = (int)(0.5f * winH), w = (int)(0.5f * winW), h = (int)(0x2db7 / 65536f * winH) - 1;
            Filter(ref _heatLevelDisplay, (int)_combat.Heat * w / 100, 1);
            int v = _heatLevelDisplay;
            var o = GaugeRect(g).position;
            void Bar(int bx, int bw, int c) => DrawShadedBar(new Rect(o.x + bx * Px, o.y + y * Py, bw * Px, h * Py), c);
            if (v >= w) Bar(x, w, 0x0b);
            else if (v < 1) Bar(x, w, 7);
            else
            {
                int middle = 7;
                if (v >= w >> 1) { v = w - v; middle = 0x0b; }
                int mw = w - v * 2;
                Bar(x, v, 3);
                Bar(x + 1 + v, mw, middle);
                Bar(x + 1 + v + mw, v, 3);
            }
            DrawText(HeatLabel, Gauge25Text(g), _combat.ShutdownSequence ? 0x0b : 0x0e);
        }

        // HTAL (Hud_LayoutDamageWire 0x10060920, FUN_10061260 0x10061260), fractions of the window.
        static readonly int[] HtalColumnX = { 0x2323, 0x7373, 0x5a5a, 0x4141, 0xaaab, 0x9192, 0xe1e2, 0xc8c9 };
        static readonly int[] HtalLabelX = { 0x1e1e, 0x5050, 0x9697, 0xd2d3 };
        static readonly string[] HtalLabels = { "H", "T", "A", "L" };
        float[] _htalFront, _htalRear;     // DAT_100b0cd8: front / rear armour at layout time
        float _htalMax;                    // DAT_1009fce0: the largest front armour

        /// <summary>
        /// HTAL armour columns (FUN_10061260): "H", "T", "A", "L" in colour 6 at y 0x199a; per section a column at
        /// x HtalColumnX, top y 0x5555, width 0x1414 (sections 1..3: two half-width columns, front then rear), that grows
        /// down: the current armour (trunc) * 0x8000 of the window height / the largest front armour at layout time in
        /// colour 0x0f, the rest of the section's own layout armour below it in colour 3 (still more than a quarter) or
        /// 0x0b; a destroyed section (flag 0x2000) shows only that rest in 0xf3. Columns: Hud_DrawShadedColumns
        /// 0x10029e41 (quarters of the width in c-1, c, c-1, c-2).
        /// </summary>
        void DrawHtal(int g)
        {
            if (_combat.Front == null || _combat.Front.Length < 8) return;
            if (_htalFront == null)
            {
                _htalFront = new float[8];
                _htalRear = new float[8];
                for (int s = 0; s < 8; s++)
                {
                    _htalFront[s] = (int)_combat.Front[s];
                    _htalRear[s] = _combat.Rear != null && s < _combat.Rear.Length ? (int)_combat.Rear[s] : 0;
                    if (_htalMax < _htalFront[s]) _htalMax = _htalFront[s];
                }
            }
            if (_htalMax <= 0) return;
            var (winW, winH) = WindowPixels(g);
            var o = GaugeRect(g).position;
            int labelY = (int)(0x199a / 65536f * winH);
            for (int k = 0; k < 4; k++)
                DrawText(HtalLabels[k], o + new Vector2((int)(HtalLabelX[k] / 65536f * winW) * Px, labelY * Py), 6);

            int top = (int)(0x5555 / 65536f * winH), width = (int)(0x1414 / 65536f * winW), height = (int)(0x8000 / 65536f * winH);
            void Column(int x, int bottom, int w, int len, int c)
            {
                if (len <= 0) return;
                DrawShadedColumns(new Rect(o.x + x * Px, o.y + (bottom - len) * Py, w * Px, len * Py), w, c);
            }
            void Section(int x, int w, float current, float layout, bool destroyed)
            {
                int len, color;
                if (!destroyed)
                {
                    int cur = (int)current;
                    color = (int)layout >> 2 < cur ? 3 : 0x0b;
                    len = (int)(cur * (long)height / _htalMax);
                }
                else { len = 0; color = 0xf3; }
                int full = (int)((int)layout * (long)height / _htalMax);
                if (len != 0) Column(x, top + len, w, len, 0x0f);
                if (len < full) Column(x, top + full, w, full - len, color);
            }
            for (int s = 0; s < 8; s++)
            {
                int x = (int)(HtalColumnX[s] / 65536f * winW);
                bool torso = s > 0 && s < 4;
                int w = torso ? width / 2 : width;
                bool destroyed = _combat.Internal[s] <= 0;
                Section(x, w, _combat.Front[s], _htalFront[s], destroyed);
                if (torso) Section(x + w, w, _combat.Rear[s], _htalRear[s], destroyed);
            }
        }

        /// <summary>Hud_DrawShadedColumns 0x10029e41: vertical lines, the first w/2/2 in c-1, then c, c-1, c-2.</summary>
        void DrawShadedColumns(Rect r, int widthPixels, int color)
        {
            int half = widthPixels / 2, q1 = half / 2, q3 = half + (widthPixels - half) / 2;
            var old = GUI.color;
            void Part(int from, int to, int c)
            {
                if (to <= from) return;
                GUI.color = Pal(c);
                GUI.DrawTexture(new Rect(r.x + from * Px, r.y, (to - from) * Px, r.height), Texture2D.whiteTexture);
            }
            Part(0, q1, color - 1);
            Part(q1, half, color);
            Part(half, q3, color - 1);
            Part(q3, widthPixels, color - 2);
            GUI.color = old;
        }

        // Window views (modes 3, 4, 5, 7): one render texture per window.
        readonly Dictionary<int, RenderTexture> _viewTextures = new Dictionary<int, RenderTexture>();
        readonly HashSet<int> _viewRendered = new HashSet<int>();
        Camera _viewCamera;

        /// <summary>The 2D pass of a view: the 3D image, a frame in colour 6 and the icon at (width / 2, 2).</summary>
        void DrawWindowView(int g, HudSprite icon)
        {
            var r = GaugeRect(g);
            if (_viewRendered.Contains(g) && _viewTextures.TryGetValue(g, out var tex) && tex != null) GUI.DrawTexture(r, tex);
            DrawFrame(r, 6);
            if (icon != null) PutSprite(icon, r.position + new Vector2(WindowPixels(g).w / 2 * Px, 2 * Py));
        }

        /// <summary>Renders the views of the windows in modes 3, 4, 5 and 7 (the 3D pass of FUN_1003e925).</summary>
        void RenderWindowViews()
        {
            _viewRendered.Clear();
            if (HudState == 0 || Layout == null || Layout.Rects == null || Layout.Rects.Length < HudLayout.GaugeCount) return;
            if (_combat == null || _combat.PowerState != 2) return;
            var main = Camera.main;
            if (main == null) return;
            for (int i = 0; i < 7; i++)
            {
                int mode = WindowModes[i], g = WindowGauges[i];
                if (g == 17 && !RadarAvailable) continue;
                if (g != 17 && WindowHidden(g)) continue;
                if (mode != 3 && mode != 4 && mode != 5 && mode != 7) continue;
                Vector3 position;
                Quaternion rotation;
                // FUN_10023447: a copy of the current camera (position, angles).
                var camPos = main.transform.position;
                float camYaw = main.transform.eulerAngles.y, camRoll = main.transform.eulerAngles.z;
                if (mode == 3)
                {
                    position = camPos;
                    rotation = Quaternion.Euler(0f, transform.eulerAngles.y + 180f, camRoll);
                }
                else if (mode == 4)
                {
                    // Camera_CockpitView 0x100235e1: the cockpit camera, whatever the main camera is.
                    if (Eye == null) continue;
                    position = Eye.position;
                    rotation = CockpitRotation();
                }
                else if (mode == 5)
                {
                    position = transform.position;
                    rotation = Quaternion.Euler(45f, camYaw, 0f);
                }
                else
                {
                    var shot = Mw2Projectile.Followed;
                    if (shot == null && Mw2Projectile.FollowLastMissile()) shot = Mw2Projectile.Followed;
                    if (shot == null) continue;
                    position = shot.transform.position;
                    rotation = shot.CameraRotation;
                }
                RenderView(g, main, position, rotation, mode != 7);
                _viewRendered.Add(g);
            }
        }

        void RenderView(int g, Camera main, Vector3 position, Quaternion rotation, bool hideSelf)
        {
            var (w, h) = WindowPixels(g);
            w = Mathf.Max(16, w);
            h = Mathf.Max(16, h);
            if (!_viewTextures.TryGetValue(g, out var tex) || tex == null || tex.width != w || tex.height != h)
            {
                if (tex != null) tex.Release();
                tex = new RenderTexture(w, h, 16) { filterMode = FilterMode.Point };
                _viewTextures[g] = tex;
            }
            if (_viewCamera == null)
            {
                var go = new GameObject("MW2 window view") { hideFlags = HideFlags.HideAndDontSave };
                _viewCamera = go.AddComponent<Camera>();
                _viewCamera.enabled = false;
            }
            _viewCamera.CopyFrom(main);
            _viewCamera.orthographic = false;
            _viewCamera.targetTexture = tex;
            _viewCamera.rect = new Rect(0, 0, 1, 1);
            _viewCamera.nearClipPlane = 0.3f;
            // FUN_100325e8: zoom 2.0 (+0x18) for the window.
            _viewCamera.fieldOfView = Camera.HorizontalToVerticalFieldOfView(2f * Mathf.Atan(1f / 2f) * Mathf.Rad2Deg, w / (float)h);
            _viewCamera.transform.SetPositionAndRotation(position, rotation);
            Renderer[] own = null;
            bool[] enabled = null;
            if (hideSelf)
            {
                own = GetComponentsInChildren<Renderer>();
                enabled = new bool[own.Length];
                for (int k = 0; k < own.Length; k++) { enabled[k] = own[k].enabled; own[k].enabled = false; }
            }
            _viewCamera.Render();
            if (own != null)
                for (int k = 0; k < own.Length; k++) own[k].enabled = enabled[k];
        }

        void ReleaseWindowViews()
        {
            foreach (var t in _viewTextures.Values) if (t != null) t.Release();
            _viewTextures.Clear();
            if (_viewCamera != null) Destroy(_viewCamera.gameObject);
        }
    }
}
