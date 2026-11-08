using System.Collections.Generic;
using MW2.Formats;
using UnityEngine;

namespace MW2
{
    /// <summary>
    /// Radar modes, range and the overhead map.
    ///
    /// Map modes (DAT_101087a8, requested DAT_101087b0, previous DAT_101087a0; configurations 0x1009e768): 0 off, 1 the
    /// radar in gauge 0 (0x1009e588, rectangle (0.02, 0.02)-(0.26, 0.34) of the screen = gauge 0), 2 the large radar
    /// (0x1009e630, (0.13, 0)-(0.87, 1), range text at (0.08, 0.13) of it), 4 the overhead map (0x1009e6d8, full
    /// screen); 3 and 5 are the steps into and out of 4. FUN_1004f4d4 starts at 0 with 1 requested. FUN_1004f5ad
    /// 0x1004f5ad applies a request: 0..2 directly; 3 replaces the main view by the map (DAT_1009e964 = FUN_1004f7df),
    /// plays the configuration's sample [3] (0xfd) and computer message 0x12, and requests 4; 4 enters the map and
    /// switches the reticle and nav marker flags off (DAT_10099f9c / fa0); 5 brings the view back, plays [4] (0xfd),
    /// says 0x11 in the external camera and requests the previous mode, whose entry restores the flags. A destroyed
    /// mech leaves the map. Keys: 0x45 (F2) FUN_10050af8 cycles 0..2, 0x49 (F3) FUN_10050ba6 enters (3) or leaves (5)
    /// the map, the cockpit camera key leaves it (FUN_10050b40).
    /// Range (FUN_10050be5, configuration +0x18; default +0x20, minimum +0x24, maximum +0x28): 0x46 halves it and wraps
    /// to the maximum below the minimum; 0x47 halves it and wraps to the minimum above the maximum, which never happens
    /// (the original divides by zero once it reaches 0; the port stops at 1); 0x48 resets it. Radar 200000 (50000..400000),
    /// map 100000 (12500..100000). Range keys need DAT_100aea44 + 9.
    /// Hud_UpdatePlayer 100176cb skips radar updates/drawing in network play when that byte is zero;
    /// Hud_CreateGauges 10016ed7 also hides target-view gauge 17. Window-map mode 6 needs the byte in all modes.
    /// The map (Map_DrawRadar 0x1004f802 in mode 4, FUN_1004c4a0): an orthographic camera at (x, range, z) of the mech,
    /// pitch 90, heading 0 (north up), width = range units over the rectangle, far plane range + 1000 (DAT_100af974),
    /// drawn in the 3D pass only (DAT_1009e964), so its cone, icons and range text (DAT_100a6670 == 0) never show.
    /// Window mode 6 (FUN_1004fae4) renders the same camera with pitch 45 into the window; its 2D pass adds the cone,
    /// nav points, target and a frame. Rendering hooks: units with flags 0x16 are culled, game pieces and GTs are
    /// drawn in flat side colours (RenderOverhead).
    /// </summary>
    public partial class Mw2Cockpit
    {
        static readonly int[] RadarDefault = { 0, 200000, 200000, 0, 100000, 0 };
        static readonly int[] RadarMin = { 0, 50000, 50000, 0, 12500, 0 };
        static readonly int[] RadarMax = { 0, 400000, 400000, 0, 100000, 0 };
        readonly int[] _radarRanges = { 0, 200000, 200000, 0, 100000, 0 };      // configuration +0x18

        public int RadarMode;           // DAT_101087a8
        int _radarRequest = 1;          // DAT_101087b0
        int _radarPrevious;             // DAT_101087a0
        bool _mapView;                  // DAT_1009e964 = FUN_1004f7df
        bool _mapFlagsSaved;            // DAT_100af970
        public bool ReticleShown = true;    // DAT_10099f9c
        public bool NavMarkerShown = true;  // DAT_10099fa0
        bool _savedReticle, _savedNavMarker;

        /// <summary>Radar diameter of the current mode in model units.</summary>
        public int RadarRange => _radarRanges[Mathf.Clamp(RadarMode, 0, 5)];

        /// <summary>Hud_UpdatePlayer 100176cb: options +9 OR not a network game (DAT_100a6c3c).</summary>
        public bool RadarAvailable => !Mw2MechCombat.NetGame || Mw2Options.GameOptions.RadarKeys;

        /// <summary>FUN_10050b53 0x10050b53.</summary>
        void RequestRadarMode(int mode)
        {
            if (RadarMode == mode) return;
            if (RadarMode < 3 && mode < 3) _radarRequest = mode;
            else if (RadarMode < 3) _radarRequest = 3;
            else
            {
                _radarRequest = 5;
                _radarPrevious = mode;
            }
        }

        /// <summary>FUN_10050ba6 0x10050ba6.</summary>
        void ToggleMap()
        {
            if (RadarMode == 4 && _radarRequest == 4) _radarRequest = 5;
            else if (RadarMode < 3 && _radarRequest < 3) _radarRequest = 3;
        }

        /// <summary>FUN_10050be5 0x10050be5: 0 reset, 1 halve (wrap to max), 2 halve (wrap to min above max).</summary>
        void StepRadarRange(int how)
        {
            if (!Mw2Options.GameOptions.RadarKeys) return; // Map_StepRange 10050be5.
            int m = RadarMode;
            if (m <= 0 || m > 5 || RadarDefault[m] == 0) return;
            if (how == 0) _radarRanges[m] = RadarDefault[m];
            else if (how == 1)
            {
                _radarRanges[m] /= 2;
                if (_radarRanges[m] < RadarMin[m]) _radarRanges[m] = RadarMax[m];
            }
            else
            {
                _radarRanges[m] = Mathf.Max(1, _radarRanges[m] / 2);
                if (RadarMax[m] < _radarRanges[m]) _radarRanges[m] = RadarMin[m];
            }
        }

        void UpdateRadarCommands()
        {
            if (Mw2Input.Command(0x45) && RadarMode < 3 && _radarRequest < 3) _radarRequest = (RadarMode + 1) % 3;
            if (Mw2Input.Command(0x46)) StepRadarRange(1);
            if (Mw2Input.Command(0x47)) StepRadarRange(2);
            if (Mw2Input.Command(0x48)) StepRadarRange(0);
            if (Mw2Input.Command(0x49)) ToggleMap();
        }

        /// <summary>FUN_1004f5ad 0x1004f5ad, once per frame.</summary>
        void UpdateRadarMode()
        {
            if (!RadarAvailable) return; // Hud_UpdatePlayer 100176cb does not call the map/radar updater.
            if (RadarMode < 3 && _radarPrevious == 4 && _mapFlagsSaved)
            {
                ReticleShown = _savedReticle;
                NavMarkerShown = _savedNavMarker;
                _mapFlagsSaved = false;
            }
            if (RadarMode == 4 && _combat != null && _combat.Destroyed) _radarRequest = 5;
            if (RadarMode == _radarRequest) return;
            var audio = Mw2Audio.Instance;
            switch (_radarRequest)
            {
                case 0:
                case 1:
                case 2:
                    _radarPrevious = RadarMode;
                    RadarMode = _radarRequest;
                    break;
                case 3:
                    _radarRequest = 4;
                    _mapView = true;
                    if (audio != null)
                    {
                        audio.Play(0xfd, transform.position, true);
                        audio.Computer(0x12);
                    }
                    break;
                case 4:
                    _radarPrevious = RadarMode;
                    RadarMode = 4;
                    if (!_mapFlagsSaved)
                    {
                        _savedReticle = ReticleShown;
                        ReticleShown = false;
                        _savedNavMarker = NavMarkerShown;
                        NavMarkerShown = false;
                        _mapFlagsSaved = true;
                    }
                    break;
                case 5:
                    _mapView = false;
                    _radarRequest = _radarPrevious;
                    if (audio != null)
                    {
                        audio.Play(0xfd, transform.position, true);
                        if (!CockpitView) audio.Computer(0x11);
                    }
                    break;
            }
        }

        /// <summary>The cockpit camera key (0x14) first leaves the map (FUN_10050b40); true when the map was up.</summary>
        bool LeaveMapForCamera()
        {
            if (RadarMode != 4) return false;
            ToggleMap();
            return true;
        }

        /// <summary>The overhead camera of FUN_1004c4a0 for a rectangle of the given aspect (height / width).</summary>
        void PlaceOverheadCamera(Camera cam, int range, float aspect, float pitch)
        {
            const float m = PolyModel.UnitsToMeters;
            cam.orthographic = true;
            cam.orthographicSize = range / 2f * aspect * m;
            cam.nearClipPlane = 0.3f;
            cam.farClipPlane = (range + 1000f) * m;
            var p = transform.position;
            cam.transform.SetPositionAndRotation(new Vector3(p.x, range * m, p.z), Quaternion.Euler(pitch, 0f, 0f));
        }

        // Unit colours of the map (FUN_10050d4f, colour table 0x1009e418): game pieces by Gp_Side 14 / 10 / 6,
        // GTs by Gt_Side 15 / 11 / 245.
        static readonly int[] MapUnitColors = { 14, 10, 6 }, MapGtColors = { 15, 11, 245 };
        readonly List<Renderer> _mapRenderers = new List<Renderer>();
        readonly List<int> _mapColors = new List<int>();

        /// <summary>
        /// The overhead view into a texture: the world, then the units and GTs in their side colours (material hooks of
        /// Map_DrawRadar: FUN_10050cb6 culls units with flags 0x16, FUN_10050d4f colours game pieces and GTs flat,
        /// FUN_1005100a draws them). Approximation: the terrain keeps its textures (the hook turns it into flat colours
        /// of its texture row).
        /// </summary>
        Matrix4x4 RenderOverhead(Camera main, RenderTexture tex, int range, float pitch)
        {
            if (_viewCamera == null)
            {
                var go = new GameObject("MW2 window view") { hideFlags = HideFlags.HideAndDontSave };
                _viewCamera = go.AddComponent<Camera>();
                _viewCamera.enabled = false;
            }
            var cam = _viewCamera;
            cam.CopyFrom(main);
            cam.targetTexture = tex;
            cam.rect = new Rect(0, 0, 1, 1);
            PlaceOverheadCamera(cam, range, tex.height / (float)tex.width, pitch);

            _mapRenderers.Clear();
            _mapColors.Clear();
            foreach (var unit in Mw2MechCombat.All)
            {
                if (unit == null) continue;
                int side = Mathf.Clamp(unit.Side, 0, 2);
                foreach (var r in unit.GetComponentsInChildren<Renderer>())
                    if (r.enabled) { _mapRenderers.Add(r); _mapColors.Add(unit.Destroyed ? -1 : MapUnitColors[side]); }
            }
            foreach (var gt in Mw2Destructible.All)
            {
                if (gt == null) continue;
                int side = Mathf.Clamp(gt.Side, 0, 2);
                foreach (var r in gt.GetComponentsInChildren<Renderer>())
                    if (r.enabled) { _mapRenderers.Add(r); _mapColors.Add(MapGtColors[side]); }
            }
            foreach (var r in _mapRenderers) r.enabled = false;
            cam.Render();
            if (TargetViewShader != null)
            {
                const int layer = 31;
                var layers = new int[_mapRenderers.Count];
                var block = new MaterialPropertyBlock();
                for (int i = 0; i < _mapRenderers.Count; i++)
                {
                    var r = _mapRenderers[i];
                    layers[i] = r.gameObject.layer;
                    if (_mapColors[i] < 0) continue;
                    r.enabled = true;
                    r.gameObject.layer = layer;
                    block.SetColor("_Mw2TargetDark", Pal(_mapColors[i]));
                    block.SetColor("_Mw2TargetLight", Pal(_mapColors[i]));
                    r.SetPropertyBlock(block);
                }
                cam.clearFlags = CameraClearFlags.Nothing;
                cam.cullingMask = 1 << layer;
                cam.RenderWithShader(TargetViewShader, "RenderType");
                for (int i = 0; i < _mapRenderers.Count; i++)
                {
                    _mapRenderers[i].gameObject.layer = layers[i];
                    _mapRenderers[i].SetPropertyBlock(null);
                }
            }
            foreach (var r in _mapRenderers) r.enabled = true;
            var viewProjection = cam.projectionMatrix * cam.worldToCameraMatrix;
            cam.orthographic = false;
            return viewProjection;
        }

        RenderTexture _mapScreen;
        bool _mapScreenRendered;

        /// <summary>The main view replaced by the map (DAT_1009e964 = FUN_1004f7df): the overhead view over the whole screen.</summary>
        void RenderMapScreen()
        {
            _mapScreenRendered = false;
            if (!RadarAvailable) return;
            if (!_mapView || RadarMode != 4) return;
            var main = Camera.main;
            if (main == null) return;
            int w = Mathf.Max(16, Screen.width), h = Mathf.Max(16, Screen.height);
            if (_mapScreen == null || _mapScreen.width != w || _mapScreen.height != h)
            {
                if (_mapScreen != null) _mapScreen.Release();
                _mapScreen = new RenderTexture(w, h, 24);
            }
            RenderOverhead(main, _mapScreen, RadarRange, 90f);
            _mapScreenRendered = true;
        }

        /// <summary>Drawn first in OnGUI: the map covers the world view.</summary>
        void DrawMapScreen()
        {
            if (!RadarAvailable) return;
            if (_mapScreenRendered && _mapScreen != null) GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), _mapScreen);
        }

        delegate Vector2 MapProjection(Vector3 world, out bool inside);

        /// <summary>
        /// Map_DrawNavs 0x100504b4 and Map_DrawTarget 0x10050131 (and, unless the map configuration, Map_DrawUnits
        /// 0x1004ff5a).
        /// </summary>
        void DrawMapIcons(MapProjection project, bool units)
        {
            if (_director != null)
                for (int i = 0; i < _director.Navs.Count; i++)
                {
                    var nav = _director.Navs[i];
                    if (nav.Team != _combat.Team || nav.Type == 0 || (nav.Flags & 1) != 0) continue;
                    var p = project(nav.Position, out bool inside);
                    if (inside) DrawSprite(_director.NavVisited(i) ? RadarNavVisited : RadarNav, p);
                }
            if (units)
            {
                foreach (var unit in Mw2MechCombat.All)
                {
                    int f = unit.GamepieceFlags;
                    if (unit == _combat || (f & 0x1400) == 0 || (f & 0x4816) != 0) continue;
                    var p = project(unit.transform.position, out bool inside);
                    if (inside) DrawSprite(BySide(unit.Side, RadarFriendly, RadarEnemy, RadarNeutral), p);
                }
                foreach (var gt in Mw2Destructible.All)
                {
                    int f = gt.GtFlags;
                    if ((f & 0x1400) == 0 || (f & 0x1e) != 0) continue;
                    var p = project(gt.transform.position, out bool inside);
                    if (inside) DrawSprite(BySide(gt.Side, RadarFriendly, RadarEnemy, RadarNeutral), p);
                }
            }
            if (_director != null)
                foreach (int navIndex in new[] { _combat.TargetNav, _combat.NavTarget })
                {
                    if (navIndex < 0 || navIndex >= _director.Navs.Count) continue;
                    bool visited = _director.NavVisited(navIndex);
                    var p = project(_director.Navs[navIndex].Position, out bool inside);
                    DrawSprite(inside ? (visited ? RadarNavTargetVisited : RadarNavTarget) : (visited ? RadarOffEnemy : RadarOffFriendly), p);
                }
            var targetUnit = _combat.Target != null && !_combat.Target.Destroyed ? _combat.Target : null;
            var targetGt = targetUnit == null ? _combat.TargetStructure : null;
            if (targetUnit != null || targetGt != null)
            {
                int f = targetUnit != null ? targetUnit.GamepieceFlags : targetGt.GtFlags;
                int side = targetUnit != null ? targetUnit.Side : targetGt.Side;
                bool shown = (f & 0x1400) != 0 && (targetGt != null || (f & 0x800) == 0);
                var p = project(targetUnit != null ? targetUnit.transform.position : targetGt.transform.position, out bool inside);
                DrawSprite(!inside ? BySide(side, RadarOffFriendly, RadarOffEnemy, RadarOffNeutral)
                    : shown ? BySide(side, RadarTargetFriendly, RadarTargetEnemy, RadarTargetNeutral)
                    : BySide(side, RadarTargetHiddenFriendly, RadarTargetHiddenEnemy, RadarTargetHiddenNeutral), p);
            }
        }

        /// <summary>The screen rectangle of radar mode 1 (gauge 0) or 2 (0x1009e618: (0.13, 0)-(0.87, 1)).</summary>
        Rect RadarRect()
        {
            if (RadarMode == 2) return new Rect(0x2148 / 65536f * Screen.width, 0, (0xdeb8 - 0x2148) / 65536f * Screen.width, Screen.height);
            return GaugeRect(0);
        }

        /// <summary>Range text point of the radar configuration ([0x18], [0x19]): mode 1 (0, 0.04), mode 2 (0.08, 0.13).</summary>
        Vector2 RadarTextPoint => RadarMode == 2 ? new Vector2(0x147b / 65536f, 0x2148 / 65536f) : new Vector2(0f, 0xa3d / 65536f);

        // Window mode 6: the overhead camera into the window (FUN_1004fae4).
        readonly Dictionary<int, RenderTexture> _mapTextures = new Dictionary<int, RenderTexture>();
        readonly Dictionary<int, Matrix4x4> _mapProjections = new Dictionary<int, Matrix4x4>();

        void RenderWindowMaps()
        {
            _mapProjections.Clear();
            if (!Mw2Options.GameOptions.RadarKeys) return; // Hud_DrawWindow 1003e925, mode 6.
            if (HudState == 0 || Layout == null || Layout.Rects == null || Layout.Rects.Length < HudLayout.GaugeCount) return;
            if (_combat == null || _combat.PowerState != 2) return;
            var main = Camera.main;
            if (main == null) return;
            for (int i = 0; i < 7; i++)
            {
                if (WindowModes[i] != 6) continue;
                int g = WindowGauges[i];
                var (w, h) = WindowPixels(g);
                w = Mathf.Max(16, w);
                h = Mathf.Max(16, h);
                if (!_mapTextures.TryGetValue(g, out var tex) || tex == null || tex.width != w || tex.height != h)
                {
                    if (tex != null) tex.Release();
                    tex = new RenderTexture(w, h, 16) { filterMode = FilterMode.Point };
                    _mapTextures[g] = tex;
                }
                _mapProjections[g] = RenderOverhead(main, tex, _radarRanges[4], 45f);
            }
        }

        /// <summary>
        /// Window mode 6 (FUN_1004fae4 with the map configuration): the 3D image, then in the 2D pass the cone from the
        /// window centre (FUN_100507c4, colour 1, to the window edge) unless destroyed, the nav points and the target
        /// projected by the same camera (FUN_1004fd8c, no units in mode 4), and a frame in colour 6.
        /// </summary>
        void DrawWindowMap(int g)
        {
            if (!Mw2Options.GameOptions.RadarKeys) return; // Hud_DrawWindow 1003e925, mode 6.
            var r = GaugeRect(g);
            if (!_mapProjections.TryGetValue(g, out var vp)) return;
            if (_mapTextures.TryGetValue(g, out var tex) && tex != null) GUI.DrawTexture(r, tex);
            if (_combat != null && !_combat.Destroyed)
            {
                float heading = TryGetComponent<Mw2MechController>(out var mc) ? mc.HeadingDeg : transform.eulerAngles.y;
                float twist = _twist != null ? _twist.Current : 0f;
                float half = Mathf.Atan(1f / Mathf.Max(0.5f, Zoom)) * Mathf.Rad2Deg;
                var centre = r.center;
                if (_lineMaterial == null)
                {
                    _lineMaterial = new Material(Shader.Find("Hidden/Internal-Colored")) { hideFlags = HideFlags.HideAndDontSave };
                    _lineMaterial.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
                }
                _lineMaterial.SetPass(0);
                GL.PushMatrix();
                GL.LoadPixelMatrix();
                GL.Begin(GL.LINES);
                GL.Color(Pal(1));
                foreach (float a in new[] { heading + twist - half, heading + twist + half })
                {
                    float rad = a * Mathf.Deg2Rad;
                    var dir = new Vector2(Mathf.Sin(rad), -Mathf.Cos(rad));
                    float tx = Mathf.Abs(dir.x) > 1e-5f ? r.width / 2f / Mathf.Abs(dir.x) : float.MaxValue;
                    float ty = Mathf.Abs(dir.y) > 1e-5f ? r.height / 2f / Mathf.Abs(dir.y) : float.MaxValue;
                    var end = centre + dir * Mathf.Min(tx, ty);
                    GL.Vertex3(centre.x, Screen.height - centre.y, 0);
                    GL.Vertex3(end.x, Screen.height - end.y, 0);
                }
                GL.End();
                GL.PopMatrix();
            }
            Vector2 Project(Vector3 world, out bool inside)
            {
                var c = vp * new Vector4(world.x, world.y, world.z, 1f);
                float w = Mathf.Abs(c.w) > 1e-6f ? c.w : 1f;
                float nx = c.x / w, ny = c.y / w;
                inside = nx >= -1f && nx <= 1f && ny >= -1f && ny <= 1f;
                return new Vector2(r.x + (nx * 0.5f + 0.5f) * r.width, r.y + (0.5f - ny * 0.5f) * r.height);
            }
            DrawMapIcons(Project, false);
            DrawFrame(r, 6);
        }

        void ReleaseWindowMaps()
        {
            foreach (var t in _mapTextures.Values) if (t != null) t.Release();
            _mapTextures.Clear();
            if (_mapScreen != null) _mapScreen.Release();
        }
    }
}
