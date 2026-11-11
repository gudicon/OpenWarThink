using MW2.Formats;
using UnityEngine;

namespace MW2
{
    /// <summary>
    /// Pilot's view: the camera sits in the EYEO node (a child of the torso, so it follows the twist). C toggles
    /// cockpit/chase. HUD: gauges placed by HudLayout (320x200 units stretched to the screen like the original window),
    /// text in the FONT BASE6X76 coloured by palette index, SHP sprites (640x480 variants).
    /// Ported: weapon rows (0x1006b8a0), speed (0x100696cb) and its throttle bar (0x10029a91), altitude and jets (0x10069c8a), heat labels (0x1006994c,
    /// 0x10069bce), compass in gauge 28 (0x10033cb7, 0x10033a56).
    /// Weapon rows follow 0x10016803 (see HudLayout.AssignWeaponRows); damage wireframe 0x10060f93.
    /// Approximations: weapon row colours map our ready/firing/recycling states onto the original states (+8/+0xc);
    /// the "Heat"/"dH/dT" labels sit at gauge 24's origin and its lower right corner; heat and dH/dT bars, radar, the
    /// damage wireframe and the nav window are not ported yet; the nav marker shows only the selected nav (N cycles).
    /// </summary>
    public partial class Mw2Cockpit : MonoBehaviour
    {
        public Transform Eye;
        /// <summary>MGEO[1], mech +0xe04, metres. Hud_CreateGauges 0x10016ed7
        /// exposes the player's value globally to Weapon_AimRay 0x1006ddd8.</summary>
        public float EyeVerticalOffset;

        public bool CockpitView = true;
        [Tooltip("Camera zoom (+0x18): focal length = half window width * zoom, default 1.0, range 0.5..16 " +
                 "(mercsw.dll 0x100233c1, Viewport_Setup 0x1006a86a). Zoom 1 = 90 degrees horizontally.")]
        [Range(0.5f, 16f)] public float Zoom = 1f;

        [Header("HUD sprites (SHP)")]
        public Texture2D Damage;            // WIREDMG6
        public HudSprite NavMarker;         // TGTNP6 (FUN_100338d9 / FUN_10033594, SHP 0xc4 + 1)
        public HudSprite PausedSprite;      // PAUSED6 (HUD sprite 0x43 + 1, FUN_10049880)

        /// <summary>An SHP sprite with its hot spot (drawn at position - hot spot, 0x1007dfd9).</summary>
        [System.Serializable]
        public class HudSprite
        {
            public Texture2D Texture;
            public int HotX, HotY;
        }

        [Header("Target brackets (0x10033e92): corners TGTGP1..4 by side, off-screen TGTOFF")]
        public HudSprite[] BracketFriendly = new HudSprite[4];  // TGTGP16..TGTGP46
        public HudSprite[] BracketEnemy = new HudSprite[4];     // TGTGP1E6..TGTGP4E6
        public HudSprite[] BracketNeutral = new HudSprite[4];   // TGTGP1N6..TGTGP4N6
        public HudSprite OffFriendly, OffEnemy, OffNeutral;     // TGTOFFF6, TGTOFFE6, TGTOFFN6
        Component _bracketTarget;
        float _bracketShrink;

        [Header("Reticles by the selected weapon (0x100333e1)")]
        public HudSprite Reticle;           // RETICLE6 (0x5b): off target
        public HudSprite ReticleOnTarget;   // RCLTGT6 (0x58): on target
        public HudSprite ReticleSrm;        // RCLLOCK6 (0x4f): SRM (projectile type 3) on target
        public HudSprite ReticleNoLock;     // RCLNOLK6 (0x52): SRM off target, lock-on weapon without lock
        public HudSprite ReticleLocking;    // RCLPLOC6 (0x55): lock-on weapon, target in the lock cone
        public HudSprite ReticleLocked;     // RCLGLOC6 (0x46): locked
        public HudSprite ReticleNotReady;   // RCLINOP6 (0x4c): selected weapon not ready

        [Header("Compass (gauge 28: 0x10033cb7 tape, 0x10033a56 target marks)")]
        public HudSprite Compass;               // COMPASS6, 719 px for 360 degrees
        public HudSprite CompassTarget;         // CMPMKR16: target bearing
        public HudSprite CompassTargetAhead;    // CMPMKR26: target bearing exactly 0
        public HudSprite CompassCentre;         // CMPMKR36
        public HudSprite CompassCentreAhead;    // CMPMKR46
        public HudSprite CompassUp, CompassDown, CompassLeft, CompassRight;    // CRTUP6, CRTDOWN6, CRTLFT6, CRTRGHT6

        [Header("Layout and text")]
        public HudLayout Layout;
        public Texture2D FontAtlas;         // BASE6X76, white ink
        public int[] GlyphX, GlyphWidth;
        public int FontHeight = 14;
        public Color32[] Palette;           // mission palette, HUD colours are indices into it
        [Tooltip("Top notification strip (MSGBAR6).")]
        public Texture2D MessageBar;
        string _message;
        float _messageUntil;
        [Header("Radar (gauge 0, map mode 1: config 0x1009e588)")]
        public Texture2D RadarUser;                         // RUSERGP6
        public Texture2D RadarFriendly, RadarEnemy, RadarNeutral;                   // RGPF6, RGPE6, RGPN6
        public Texture2D RadarTargetFriendly, RadarTargetEnemy, RadarTargetNeutral; // RTGTGPF6, RTGTGPE6, RTGTGPN6
        public Texture2D RadarOffFriendly, RadarOffEnemy, RadarOffNeutral;          // RTGTOFF6, RTGTOFE6, RTGTOFN6
        public Texture2D RadarTargetHiddenFriendly, RadarTargetHiddenEnemy, RadarTargetHiddenNeutral; // row 3: RTGTGTF6, RTGTGTE6, RTGTGTN6
        public Texture2D RadarNav, RadarNavTarget;          // RTGTNP6, RNP6 (icon rows 4 / 5, column 0: not visited)
        public Texture2D RadarNavVisited, RadarNavTargetVisited; // RRTGTNP6, RRNP6 (column 1: visited, flag 0x20)
        Material _lineMaterial;

        // Bar filters of 0x100256b5: value += (target - value) >> shift.
        int _heatRateBar, _heatLevelDisplay, _throttleBar;
        static int Filter(ref int value, int target, int shift) => value += (target - value) >> shift;

        [Header("Target window (window 17, mode 8: 0x1006606e; text gauge 18: 0x10065990)")]
        public Texture2D TargetNavIcon;     // VTGT_NP6 (SHP 0xdc + resolution)
        public Texture2D TargetNavVisitedIcon; // VTGT_NR6 (0xdf) when the nav has flag 0x20
        public Shader TargetViewShader;     // MW2/TargetView
        Camera _targetCamera;
        RenderTexture _targetTexture;

        [Header("Damage wireframe (WIREDM6, 0x10060f93)")]
        public int WireWidth, WireHeight;
        public int[] WirePixels;            // palette index, -1 transparent, rows top-down
        public int[] WireRegions;           // damage region per pixel (HudLayout.DamageRegionMap), -1 none
        Texture2D _wire;
        Color32[] _wireColors;

        Mw2MechCombat _combat;
        Mw2TorsoTwist _twist;
        Mw2MissionDirector _director;

        void Start()
        {
            _combat = GetComponent<Mw2MechCombat>();
            _twist = GetComponent<Mw2TorsoTwist>();
            _director = FindAnyObjectByType<Mw2MissionDirector>();
        }

        public bool IsPlayer => _combat != null && _combat.IsPlayer;

        /// <summary>The player's cockpit (every mech with an eye node has one; only the player's draws).</summary>
        public static Mw2Cockpit Player
        {
            get
            {
                foreach (var c in FindObjectsByType<Mw2Cockpit>(FindObjectsSortMode.None)) if (c.IsPlayer) return c;
                return null;
            }
        }

        /// <summary>Camera mode 0 of the engine (DAT_100a6e40): the LOD switcher shows LOD5 for the player then.</summary>
        public bool InCockpit => IsPlayer && CockpitView && Eye != null;

        static Mw2Cockpit s_player;

        /// <summary>
        /// DAT_100a6e48, the player looks from the cockpit: positioned sounds take it as their half-volume flag
        /// (FUN_10007081), and the cockpit tones play only then.
        /// </summary>
        public static bool PlayerInCockpit => s_player != null && s_player.InCockpit;

        void Update()
        {
            if (IsPlayer) s_player = this;
            if (IsPlayer && _combat != null && Mw2Audio.Instance != null && TryGetComponent<Mw2MechController>(out var humController))
                Mw2Audio.Instance.UpdatePowerHum(_combat.PowerState, humController.PowerHumPitch, HudState != 0, InCockpit);
            // COCKPIT_VIEW 0x14 (Input_Command 0x100267c0), unless ejected (game piece flag 2): from the cockpit, without
            // the map, to the external camera (Camera_SetMode 1); otherwise the map closes (FUN_10050b40) and the cockpit
            // camera returns (Camera_SetMode 0). Either leaves the missile camera.
            if (IsPlayer && Mw2Input.Command(0x14))
            {
                bool external = CockpitView && !MissileCamera && RadarMode != 4;
                LeaveMapForCamera();
                MissileCamera = false;
                CockpitView = !external;
                if (!external)
                {
                    // Input_Command 0x100267c0: returning to mode 0 resets pilot tilt/pan and zoom.
                    Mw2Input.Set(0x72);
                    Mw2Input.Set(0x75);
                    Mw2Input.Set(0x88);
                }
                // 0x100237c0: leaving the cockpit while alive (DAT_100ad360 = 0) says "External camera engaged".
                if (external && _combat != null && !_combat.Destroyed && Mw2Audio.Instance != null) Mw2Audio.Instance.Computer(0x11);
            }
            if (IsPlayer) UpdateHudCommands();
            if (IsPlayer && HudState != 0 && (_combat == null || _combat.PowerState == 2 || RadarMode == 4)) UpdateRadarMode();
            if (IsPlayer && _combat != null)
            {
                Filter(ref _heatRateBar, (int)_combat.HeatRate << 10, 4);     // 0x100298fb: shift 4
            }
            if (IsPlayer && Layout != null && Layout.Rects != null && Layout.Rects.Length >= HudLayout.GaugeCount && TryGetComponent<Mw2MechController>(out var mc))
            {
                // 0x10029a91: trunc(bar height * throttle), reverse -(that / 2); target << 16, shift 1.
                int v = (int)(ThrottleBox(WindowWithMode(9, 22)).h * mc.Throttle);
                if (mc.Reverse) v = -(v / 2);
                Filter(ref _throttleBar, v << 16, 1);
            }
            if (IsPlayer) { UpdateMainMenu(); UpdateCommandMenu(); }
        }

        /// <summary>
        /// Called by the controller after the mech has moved this frame. Cockpit camera of 0x100235e1: position = the
        /// world position of the eye node (+0x44, animated) plus MGEO [1] up (0 for the Atlas, not applied); angles not
        /// from the node: pitch = body pitch (+0x5c) + torso pitch (+0x68 = half the weapon pitch with flag 0x2000,
        /// 0x1003880d), yaw = heading (+0x60) + twist (+0x6c), roll = the eye node's roll / 2 (DAT_100ad360 = 0 while
        /// alive). So the step sway turns the cockpit shell in front of a steady view. The torso pitch turns about the
        /// body's cross axis, so a twisted torso rolls the view; the reticle shows the full weapon pitch (0x10033e16).
        /// Approximation: flag 0x2000 is taken as always set for the player.
        /// </summary>
        public bool PlaceCamera(Camera cam)
        {
            if (cam == null) return false;
            cam.orthographic = false;
            if (!CockpitView || Eye == null || MissileCamera) _cockpitLookActive = false;
            // Another camera mode this frame: the next external update re-enters it (DAT_10090300 != 1, Camera_External).
            if ((CockpitView && Eye != null) || MissileCamera) _external = false;
            if (IsPlayer && MissileCamera)
            {
                // Camera mode 3 (FUN_1002372f): the followed missile at zoom 2.0 (DAT_100902fc); once it is gone the
                // previous mode returns.
                var shot = Mw2Projectile.Followed;
                if (shot != null)
                {
                    cam.transform.SetPositionAndRotation(shot.transform.position, shot.CameraRotation);
                    cam.fieldOfView = Camera.HorizontalToVerticalFieldOfView(2f * Mathf.Atan(1f / 2f) * Mathf.Rad2Deg, cam.aspect);
                    cam.nearClipPlane = 0.3f;
                    return true;
                }
                MissileCamera = false;
            }
            if (!CockpitView || Eye == null) return false;
            // FUN_100230d9: the cockpit zoom is the zoom_factor channel (control +0x6c, 1..4; Z / Shift+Z / Ctrl+Z, reset
            // byte +0x88); FUN_100233c1 applies it in camera mode 0 and plays 0x15b when it changes.
            if (IsPlayer && Mw2Input.Loaded)
            {
                float z = Mw2Input.Float(0x6c);
                if (z > 0f && z != Zoom)
                {
                    Zoom = z;
                    if (Mw2Audio.Instance != null) Mw2Audio.Instance.Play(0x15b, transform.position, true);
                }
            }
            var rotation = CockpitRotation();
            if (IsPlayer && Mw2Input.Loaded)
            {
                int now = CameraTicks();
                if (!_cockpitLookActive) { _cockpitLook.Enter(now); _cockpitLookActive = true; }
                _cockpitLook.Step(Mw2Input.System, now);
                // FUN_10023d89 adds pilot pitch/yaw to the eye angles without turning torso or weapons.
                var angles = rotation.eulerAngles;
                rotation = Quaternion.Euler(angles.x + _cockpitLook.Pitch, angles.y + _cockpitLook.Yaw, angles.z);
            }
            cam.transform.SetPositionAndRotation(Eye.position, rotation);
            float horizontal = 2f * Mathf.Atan(1f / Zoom) * Mathf.Rad2Deg;
            cam.fieldOfView = Camera.HorizontalToVerticalFieldOfView(horizontal, cam.aspect);
            cam.nearClipPlane = 0.3f;
            return true;
        }

        /// <summary>The angles of Camera_CockpitView 0x100235e1 (see PlaceCamera).</summary>
        Quaternion CockpitRotation()
        {
            var yaw = _twist != null ? _twist.TorsoYaw : transform.rotation;
            var look = Quaternion.LookRotation(yaw * Vector3.forward, Vector3.up);
            // Remove the modeled torso tilt before reading the eye node's own roll. The tilt is applied to the
            // camera below; otherwise a twisted torso would contribute the same roll twice.
            var eyeRotation = Eye.rotation;
            if (_twist != null && _twist.Pivot != null && Eye.IsChildOf(_twist.Pivot))
                eyeRotation = Quaternion.AngleAxis(_twist.Pitch * 0.5f, transform.right) * eyeRotation;
            float roll = Mathf.DeltaAngle(0f, (Quaternion.Inverse(yaw) * eyeRotation).eulerAngles.z) * 0.5f;
            look = look * Quaternion.Euler(0, 0, roll);
            if (_twist != null) look = Quaternion.AngleAxis(-_twist.Pitch * 0.5f, transform.right) * look;
            return look;
        }

        /// <summary>The gauge of the first window in <paramref name="mode"/>, else <paramref name="fallback"/>.</summary>
        int WindowWithMode(int mode, int fallback)
        {
            for (int i = 0; i < 7; i++) if (WindowModes[i] == mode) return WindowGauges[i];
            return fallback;
        }

        // 320x200 HUD units to screen pixels; the text and sprites use 640x480 pixels.
        float Sx => Screen.width / 320f;
        float Sy => Screen.height / 200f;
        float Px => Screen.width / 640f;
        float Py => Screen.height / 480f;

        Color Pal(int index) => Palette != null && index >= 0 && index < Palette.Length ? (Color)Palette[index] : Color.green;

        Rect GaugeRect(int g)
        {
            if (_gaugeOverride == g) return _gaugeOverrideRect;
            var r = Layout.Rects[g];
            return new Rect(r.x * Sx, r.y * Sy, r.width * Sx, r.height * Sy);
        }

        Vector2 GaugeOrigin(int g)
        {
            var o = Layout.Origins[g];
            if (_gaugeOverride == g) return _gaugeOverrideRect.position + Vector2.Scale(o, _gaugeOverrideRect.size);
            var r = Layout.Rects[g];
            return new Vector2((r.x + o.x * r.width) * Sx, (r.y + o.y * r.height) * Sy);
        }

        // A window drawn into its power-up / power-down surface (Mw2CockpitPower.DrawInSurface).
        int _gaugeOverride = -1;
        Rect _gaugeOverrideRect;

        float LineHeight => FontHeight * Py;

        /// <summary>Draws a string with the FONT glyphs, like 0x1007fbe4 (each glyph advances by its width).</summary>
        void DrawText(string text, Vector2 at, int colorIndex)
        {
            if (FontAtlas == null || GlyphX == null) return;
            var old = GUI.color;
            GUI.color = Pal(colorIndex);
            float x = at.x;
            foreach (char ch in text)
            {
                if (ch >= GlyphWidth.Length) continue;
                int w = GlyphWidth[ch];
                if (w <= 0) continue;
                var uv = new Rect(GlyphX[ch] / (float)FontAtlas.width, 0, w / (float)FontAtlas.width, 1);
                GUI.DrawTextureWithTexCoords(new Rect(x, at.y, w * Px, FontHeight * Py), FontAtlas, uv);
                x += w * Px;
            }
            GUI.color = old;
        }

        float TextWidth(string text)
        {
            float w = 0;
            if (GlyphWidth != null) foreach (char ch in text) if (ch < GlyphWidth.Length) w += GlyphWidth[ch];
            return w * Px;
        }

        /// <summary>Message_Add 0x10042d4e: MASC notices use 0x16a ticks, about two seconds.</summary>
        public void ShowMessage(string message, int ticks = 0x16a)
        {
            _message = message;
            _messageUntil = Time.time + ticks / (float)MechMotion.TicksPerSecond;
        }

        void DrawMessage()
        {
            if (string.IsNullOrEmpty(_message) || Time.time >= _messageUntil) return;
            if (MessageBar != null)
                GUI.DrawTexture(new Rect(0, 0, Screen.width, MessageBar.height * Py), MessageBar);
            DrawText(_message, new Vector2((Screen.width - TextWidth(_message)) * 0.5f,
                ((MessageBar != null ? MessageBar.height : 30) - FontHeight) * 0.5f * Py), 0x0e);
        }

        /// <summary>One-pixel outline (0x1004af13 frames the selected weapon row in the row colour).</summary>
        void DrawFrame(Rect r, int colorIndex)
        {
            var old = GUI.color;
            GUI.color = Pal(colorIndex);
            float t = Mathf.Max(1f, Px);
            GUI.DrawTexture(new Rect(r.x, r.y, r.width, t), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(r.x, r.yMax - t, r.width, t), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(r.x, r.y, t, r.height), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(r.xMax - t, r.y, t, r.height), Texture2D.whiteTexture);
            GUI.color = old;
        }

        /// <summary>
        /// Weapon row colour (0x1006b8a0): state 0 (recycling) -> 0x0b, 1 (ready) -> by group +0xc (0 -> 0x0e, 1 -> 0xfe,
        /// 2 -> 3, 3 -> 5, 4 -> 7), 2 (firing) -> 0x0e, -1 (destroyed or out of ammunition) -> 8.
        /// Our states: 1 ready, 2 firing, 3 recycling.
        /// </summary>
        static readonly int[] GroupColors = { 0x0e, 0xfe, 3, 5, 7 };

        int WeaponColor(int i)
        {
            var w = _combat.Weapons[i];
            if (!_combat.Works(i) || w.Ammo == 0) return 8;
            if (w.State == 3) return 0x0b;
            if (w.State == 2) return 0x0e;
            return GroupColors[Mathf.Clamp(w.Group, 0, 4)];
        }

        void OnGUI()
        {
            if (!IsPlayer || Event.current.type != EventType.Repaint) return;
            DrawCockpitGui();
        }

        void DrawCockpitGui()
        {
            DrawMapScreen();
            DrawHudFrame();
            // FUN_10049880 0x10049880 after the HUD in the pause mode: PAUSED at its own size, centred in the rectangle
            // (0, 0.2)-(1, 0.4) of the screen (0x1009da24, scaled by 0x1004ae02 / 0x1004ace5 about its centre).
            if (Mw2Pause.Paused && Mw2Pause.PauseMode && PausedSprite != null && PausedSprite.Texture != null)
            {
                float w = PausedSprite.Texture.width * Px, h = PausedSprite.Texture.height * Py;
                GUI.DrawTexture(new Rect(Screen.width * 0.5f - w / 2f, Screen.height * 0.3f - h / 2f, w, h), PausedSprite.Texture);
            }
            DrawMainMenu();
        }

        void DrawHudFrame()
        {
            var cam = Camera.main;
            if (cam == null) return;
            float scale = Py;
            // HUD state 0 (DAT_10099f98): Hud_UpdatePlayer draws nothing.
            bool layout = Layout != null && Layout.Rects != null && Layout.Rects.Length >= HudLayout.GaugeCount;
            if (HudState == 0 || !layout) { DrawCommandMenu(); DrawMessage(); return; }
            if (DrawPowerHud()) { DrawCommandMenu(); DrawMessage(); return; }

            // Radar modes 1 and 2 (FUN_1004f56d -> Map_DrawRadar); the map (4) has no 2D part.
            if (RadarMode == 1 || RadarMode == 2) DrawRadar(RadarRect());
            DrawGauges();

            // FUN_100338d9 0x100338d9: a nav target (+0xdc) as TGTNP6 at its position, off screen TGTOFFF6 clipped to the
            // screen (0x1004b71f); with another target FUN_10033594 0x10033594 draws the nav target +0x104 the same way.
            int markedNav = _combat.TargetNav >= 0 ? _combat.TargetNav : _combat.NavTarget;
            if (NavMarkerShown && _director != null && markedNav >= 0 && markedNav < _director.Navs.Count)
                DrawNavMarker(cam, _director.Navs[markedNav].Position);

            // Target bracket.
            if (ReticleShown) DrawTargetBracket(cam);

            // Reticle at the projection of the aim point (0x10033e16: the aim ray at the aim distance), at its hot spot.
            var reticle = ChooseReticle();
            if (ReticleShown && reticle != null && reticle.Texture != null && CockpitView && !MissileCamera && Eye != null)
            {
                var aim = _twist != null ? _twist.Aim : transform.forward;
                var sp = cam.WorldToScreenPoint(cam.transform.position + aim.normalized * 1000f);
                if (sp.z > 0)
                    GUI.DrawTexture(new Rect(sp.x - reticle.HotX * scale, Screen.height - sp.y - reticle.HotY * scale,
                                             reticle.Texture.width * scale, reticle.Texture.height * scale), reticle.Texture);
            }

            DrawCommandMenu();
            DrawMessage();
        }

        /// <summary>
        /// Target bracket (0x10033e92). The target's position is projected (0x1006abcd); on screen, four corner sprites
        /// (TGTGP1..4, friendly plain, enemy E, neutral N) sit at the centre +- s with s = camera focal (+0x94) * target
        /// radius (+0xe1c) / depth, each at its hot spot. A new target first shrinks a box from the screen half height by
        /// half height / 50 per tick (0x10033783, rate 0x10099f00); its drawing hook 0x10033779 is empty, so the corners
        /// only reappear once it has shrunk. Off screen: TGTOFF (E / F / N) at the projected point clipped to the screen
        /// (0x1004b71f). Approximations: the centre is the middle of the visible model (our unit origin sits at the feet
        /// and a flying unit's model above it) and the radius of units without a mech controller is the model's
        /// horizontal extent. The sprite set is chosen by the target's side (FUN_1002a7c1): 0 plain, 2 N, anything else E.
        /// </summary>
        void DrawTargetBracket(Camera cam)
        {
            var target = _combat.Target != null && !_combat.Target.Destroyed ? _combat.Target : null;
            var structure = target == null && _combat.TargetStructure != null && !_combat.TargetStructure.Destroyed ? _combat.TargetStructure : null;
            Component current = target != null ? target : (Component)structure;
            if (current == null) { _bracketTarget = null; return; }
            float ticks = (float)(Time.deltaTime * MechMotion.TicksPerSecond);
            if (current != _bracketTarget) { _bracketTarget = current; _bracketShrink = Screen.height / 2f; }
            else if (_bracketShrink > 0f) _bracketShrink -= Screen.height / 2f / 50f * ticks;

            // FUN_100340ef 0x100340ef for a GT: the same corner sets by Gt_Side, sized by the model radius (FUN_10003c5e)
            // at the model centre, at most the screen width / 2 (_DAT_10087eac).
            int side = target != null ? target.Side : structure.Side;
            float radius;
            Vector3 centreWorld;
            if (target != null)
            {
                radius = target.TryGetComponent<Mw2MechController>(out var tc) ? tc.CollisionRadius
                    : target.TryGetComponent<BoxCollider>(out var box) ? new Vector2(box.size.x, box.size.z).magnitude / 2f : 5f;
                centreWorld = target.ModelCentre;
            }
            else
            {
                radius = structure.Radius;
                centreWorld = Mw2MechCombat.StructureCentre(structure);
            }

            var p = cam.WorldToScreenPoint(centreWorld);
            var centre = new Vector2(p.x, Screen.height - p.y);
            bool onScreen = p.z > 0 && centre.x >= 0 && centre.x <= Screen.width && centre.y >= 0 && centre.y <= Screen.height;
            if (!onScreen)
            {
                // Clip the direction from the screen centre to the screen border (behind the camera it is mirrored).
                var mid = new Vector2(Screen.width / 2f, Screen.height / 2f);
                var dir = centre - mid;
                if (p.z <= 0) dir = -dir;
                if (dir.sqrMagnitude < 1e-4f) dir = Vector2.down;
                float k = Mathf.Min(Mathf.Abs(mid.x / (dir.x == 0 ? 1e-4f : dir.x)), Mathf.Abs(mid.y / (dir.y == 0 ? 1e-4f : dir.y)));
                PutSprite(side == 0 ? OffFriendly : side == 2 ? OffNeutral : OffEnemy, mid + dir * k);
                return;
            }
            if (_bracketShrink > 0f) return;
            // s = focal * radius / depth, focal = half screen width / tan(half horizontal FOV).
            float s = cam.projectionMatrix[0, 0] * Screen.width / 2f * radius / p.z;
            if (structure != null) s = Mathf.Min(s, Screen.width / 2f);
            var set = side == 0 ? BracketFriendly : side == 2 ? BracketNeutral : BracketEnemy;
            if (set == null || set.Length < 4) return;
            PutSprite(set[0], centre + new Vector2(-s, -s));
            PutSprite(set[1], centre + new Vector2(s, -s));
            PutSprite(set[2], centre + new Vector2(-s, s));
            PutSprite(set[3], centre + new Vector2(s, s));
        }

        void DrawNavMarker(Camera cam, Vector3 position)
        {
            var p = cam.WorldToScreenPoint(position);
            var at = new Vector2(p.x, Screen.height - p.y);
            if (p.z > 0 && at.x >= 0 && at.x <= Screen.width && at.y >= 0 && at.y <= Screen.height)
            {
                PutSprite(NavMarker, at);
                return;
            }
            var mid = new Vector2(Screen.width / 2f, Screen.height / 2f);
            var dir = at - mid;
            if (p.z <= 0) dir = -dir;
            if (dir.sqrMagnitude < 1e-4f) dir = Vector2.down;
            float k = Mathf.Min(Mathf.Abs(mid.x / (dir.x == 0 ? 1e-4f : dir.x)), Mathf.Abs(mid.y / (dir.y == 0 ? 1e-4f : dir.y)));
            PutSprite(OffFriendly, mid + dir * k);
        }

        /// <summary>An SHP sprite with its hot spot at a screen point (640x480 pixels scaled).</summary>
        void PutSprite(HudSprite sp, Vector2 at)
        {
            if (sp == null || sp.Texture == null) return;
            GUI.DrawTexture(new Rect(at.x - sp.HotX * Px, at.y - sp.HotY * Py, sp.Texture.width * Px, sp.Texture.height * Py), sp.Texture);
        }

        /// <summary>
        /// Reticle of 0x100333e1 for the selected weapon: not ready (slot state != 1, or no ammunition: state -1) ->
        /// RCLINOP; the on / off pair is RCLLOCK / RCLNOLK for projectile type 3 (SRM), else RCLTGT / RETICLE; a weapon
        /// without lock-on (+0x28 = 0) shows "on" with a unit target between its minimum and maximum range and within 3
        /// degrees of the torso heading (the elevation test, +-196608, always passes); a lock-on weapon shows RCLGLOC
        /// when locked (0x80), RCLPLOC while the target is in the lock cone (0x8000), else RCLNOLK.
        /// </summary>
        HudSprite ChooseReticle()
        {
            int sel = _combat.SelectedWeapon;
            if (sel < 0 || sel >= _combat.Weapons.Count) return Reticle;
            var w = _combat.Weapons[sel];
            if (w.State != 1 || !_combat.Works(sel) || w.Ammo == 0) return ReticleNotReady;
            bool srm = w.Def.ProjectileType == 3;
            var on = srm ? ReticleSrm : ReticleOnTarget;
            var off = srm ? ReticleNoLock : Reticle;
            if (!w.Def.LockOn)
                return _combat.TargetAngles(out float bearing, out _, out float d) && d > w.Def.MinRangeMeters &&
                       d < w.Def.RangeMeters && Mathf.Abs(bearing) < 3f ? on : off;
            return _combat.Locked ? ReticleLocked : _combat.TargetInCone ? ReticleLocking : ReticleNoLock;
        }

        /// <summary>
        /// Damage wireframe in window gauge 2, centred (0x10060920): the sprite's colour 6 is replaced per region by the
        /// colour of its section (HudLayout.DamageColor), later regions win. Window mode 1 in any window (gauge g).
        /// </summary>
        void DrawDamage(int g)
        {
            if (WirePixels == null || WireRegions == null || WireWidth <= 0 || WireHeight <= 0) return;
            if (_wire == null)
            {
                _wire = new Texture2D(WireWidth, WireHeight, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point };
                _wireColors = new Color32[WireWidth * WireHeight];
            }
            var sectionColor = new int[8];
            for (int sct = 0; sct < 8; sct++)
                sectionColor[sct] = HudLayout.DamageColor(_combat.Front[sct] / Mathf.Max(1, _combat.ArmorFactor), _combat.Rear[sct] / Mathf.Max(1, _combat.ArmorFactor), _combat.Internal[sct],
                                                          _combat.DamageScale != null && sct < _combat.DamageScale.Length ? _combat.DamageScale[sct] : 0,
                                                          _combat.Internal[sct] <= 0);
            for (int y = 0; y < WireHeight; y++)
                for (int x = 0; x < WireWidth; x++)
                {
                    int i = y * WireWidth + x;
                    int index = WirePixels[i];
                    if (index == 6)
                    {
                        int region = WireRegions[i];
                        int section = region >= 0 && region < HudLayout.DamageRegionSection.Length ? HudLayout.DamageRegionSection[region] : -1;
                        if (section >= 0) index = sectionColor[section];
                    }
                    _wireColors[(WireHeight - 1 - y) * WireWidth + x] = index < 0 ? new Color32(0, 0, 0, 0) : (Color32)Pal(index);
                }
            _wire.SetPixels32(_wireColors);
            _wire.Apply();
            var r = GaugeRect(g);
            float w = WireWidth * Px, h = WireHeight * Py;
            GUI.DrawTexture(new Rect(r.x + (r.width - w) / 2, r.y + (r.height - h) / 2, w, h), _wire);
        }

        /// <summary>
        /// Radar in gauge 0 (map mode 1, 0x1004f802). Heading-up around the mech: circle in colour 2 (0x1004bb40, radius
        /// half the gauge width - 1, vertical radius scaled by the pixel aspect), torso cone in colour 1 at the twist
        /// angle +- the half field of view (0x10050677), "R: %3.1lfkm" of half the range at the gauge origin
        /// (0x10050909), the player sprite at the centre (0x1004fd8c), units as row 1 sprites by side (0x1004ff5a),
        /// nav points as RTGTNP (0x100504b4), the target as row 2 or, outside the circle, row 6 at the edge (0x10050131).
        /// Mode 2 draws the same into the large rectangle (0x1009e618) with the text at (0.08, 0.13); the range is the
        /// mode's own (Mw2CockpitMap). Approximation: pixel aspect 200/240.
        /// </summary>
        void DrawRadar(Rect r)
        {
            if (!RadarAvailable) return; // Hud_UpdatePlayer 100176cb: no radar drawing with network option +9 off.
            // r: the radar's rectangle on screen, the gauge itself or its power-up / power-down rectangle (0x10051139).
            int widthUnits = Mathf.RoundToInt(r.width / Sx), heightUnits = Mathf.RoundToInt(r.height / Sy);
            var centre = new Vector2(r.x + (widthUnits / 2) * Sx, r.y + (heightUnits / 2) * Sy);
            float radiusUnits = Mathf.Max(0f, widthUnits / 2f - 1f);
            float rx = radiusUnits * Sx, ry = radiusUnits * (200f / 240f) * Sy;

            if (_lineMaterial == null)
            {
                _lineMaterial = new Material(Shader.Find("Hidden/Internal-Colored")) { hideFlags = HideFlags.HideAndDontSave };
                _lineMaterial.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
            }
            _lineMaterial.SetPass(0);
            GL.PushMatrix();
            GL.LoadPixelMatrix();
            GL.Begin(GL.LINES);
            // GUI y runs down, GL pixel y runs up.
            void Line(Vector2 a, Vector2 b, Color c)
            {
                GL.Color(c);
                GL.Vertex3(a.x, Screen.height - a.y, 0);
                GL.Vertex3(b.x, Screen.height - b.y, 0);
            }
            var circle = Pal(2);
            const int segments = 64;
            for (int k = 0; k < segments; k++)
            {
                float a0 = k * Mathf.PI * 2 / segments, a1 = (k + 1) * Mathf.PI * 2 / segments;
                Line(centre + new Vector2(Mathf.Cos(a0) * rx, Mathf.Sin(a0) * ry), centre + new Vector2(Mathf.Cos(a1) * rx, Mathf.Sin(a1) * ry), circle);
            }
            // Torso cone: twist relative to the legs, +- atan(1 / zoom).
            // The twist angle itself (object +0xc), not the animated torso node: the walk sway must not shake the cone.
            // Current is the MW2 twist [3]: a clockwise (right) twist is positive.
            float twist = _twist != null ? _twist.Current : 0f;
            float half = Mathf.Atan(1f / Mathf.Max(0.5f, Zoom)) * Mathf.Rad2Deg;
            foreach (float a in new[] { twist - half, twist + half })
            {
                float rad = a * Mathf.Deg2Rad;
                Line(centre, centre + new Vector2(Mathf.Sin(rad) * rx, -Mathf.Cos(rad) * ry), Pal(1));
            }
            GL.End();
            GL.PopMatrix();

            // World offset -> radar pixels, heading-up (the mech's forward is up).
            Vector2 ToRadar(Vector3 world, out bool inside)
            {
                var d = world - transform.position;
                var fwd = transform.forward;
                var right = Vector3.Cross(Vector3.up, fwd);
                float scale = 1f / (RadarRange / 2f * PolyModel.UnitsToMeters);
                float x = Vector3.Dot(d, right) * scale, y = Vector3.Dot(d, fwd) * scale;
                inside = x * x + y * y <= 1f;
                if (!inside) { float n = Mathf.Sqrt(x * x + y * y); x /= n; y /= n; }
                return centre + new Vector2(x * rx, -y * ry);
            }

            // Map_DrawNavs 0x100504b4: nav points of the player's team, not start points, not private (flag 1), inside
            // the circle; RTGTNP, or RRTGTNP once visited (flag 0x20, FUN_100500f1 0x100500f1).
            if (_director != null)
                for (int i = 0; i < _director.Navs.Count; i++)
                {
                    var nav = _director.Navs[i];
                    if (nav.Team != _combat.Team || nav.Type == 0 || (nav.Flags & 1) != 0) continue;
                    var p = ToRadar(nav.Position, out bool inside);
                    if (inside) DrawSprite(_director.NavVisited(i) ? RadarNavVisited : RadarNav, p);
                }

            // Map_DrawUnits 0x1004ff5a: units with GP flags 0x1400 and none of 0x800 / 0x4000 / 0x16, then GTs with
            // 0x1400 and none of 0x1e, as row 1 by side (icon table 0x1009e310: friendly / enemy / neutral columns).
            foreach (var unit in Mw2MechCombat.All)
            {
                int f = unit.GamepieceFlags;
                if (unit == _combat || (f & 0x1400) == 0 || (f & 0x4816) != 0) continue;
                var p = ToRadar(unit.transform.position, out bool inside);
                if (inside) DrawSprite(BySide(unit.Side, RadarFriendly, RadarEnemy, RadarNeutral), p);
            }
            foreach (var gt in Mw2Destructible.All)
            {
                int f = gt.GtFlags;
                if ((f & 0x1400) == 0 || (f & 0x1e) != 0) continue;
                var p = ToRadar(gt.transform.position, out bool inside);
                if (inside) DrawSprite(BySide(gt.Side, RadarFriendly, RadarEnemy, RadarNeutral), p);
            }
            // Map_DrawTarget 0x10050131, for +0xdc and then +0x104: a nav point as row 5 (RNP / RRNP by the visited
            // flag); outside the circle row 6 at the edge, whose column is that same flag (RTGTOFF / RTGTOFE).
            if (_director != null)
                foreach (int navIndex in new[] { _combat.TargetNav, _combat.NavTarget })
                {
                    if (navIndex < 0 || navIndex >= _director.Navs.Count) continue;
                    bool visited = _director.NavVisited(navIndex);
                    var p = ToRadar(_director.Navs[navIndex].Position, out bool inside);
                    DrawSprite(inside ? (visited ? RadarNavTargetVisited : RadarNavTarget) : (visited ? RadarOffEnemy : RadarOffFriendly), p);
                }
            // Units and GTs: row 2 for a target with flags 0x1400 (a unit also without 0x800), else row 3;
            // outside the circle row 6 at the edge.
            var targetUnit = _combat.Target != null && !_combat.Target.Destroyed ? _combat.Target : null;
            var targetGt = targetUnit == null ? _combat.TargetStructure : null;
            if (targetUnit != null || targetGt != null)
            {
                int f = targetUnit != null ? targetUnit.GamepieceFlags : targetGt.GtFlags;
                int side = targetUnit != null ? targetUnit.Side : targetGt.Side;
                bool shown = (f & 0x1400) != 0 && (targetGt != null || (f & 0x800) == 0);
                var p = ToRadar(targetUnit != null ? targetUnit.transform.position : targetGt.transform.position, out bool inside);
                DrawSprite(!inside ? BySide(side, RadarOffFriendly, RadarOffEnemy, RadarOffNeutral)
                    : shown ? BySide(side, RadarTargetFriendly, RadarTargetEnemy, RadarTargetNeutral)
                    : BySide(side, RadarTargetHiddenFriendly, RadarTargetHiddenEnemy, RadarTargetHiddenNeutral), p);
            }

            DrawSprite(RadarUser, centre);

            // Range text: half the range in km (m below 1 km), 0x10050909, at the configuration's point.
            float halfRange = RadarRange / 2f;
            string range = halfRange < 100000f ? $"R: {halfRange / 100f:0.0}m" : $"R: {halfRange / 100000f:0.0}km";
            DrawText(range, r.position + Vector2.Scale(RadarTextPoint, r.size), 0x0e);
        }

        /// <summary>
        /// Target window (window 17, mode 8, 0x1006606e) and its text (gauge 18, 0x10065990). No target: black with a
        /// frame in colour 8. Nav target: the VTGT_NP icon in the centre. Unit target: a view from 3 x the target's radius
        /// (+0xe1c) behind it along its heading, in view mode 1 (DAT_100a0544 = 1: material mode 3, lit from the camera).
        /// Text: the name at the gauge corner in the side colour (friendly 0x0e, enemy 10, neutral 6, nav 2), the distance
        /// below in 0x0e: "%3ldm" up to 1000 m, else "%2.2fk".
        /// Approximations: the target is our combat target, else the selected nav; the view is a two-tone blue (palette 5 to
        /// 7 by the angle to the camera) rather than the mode-3 palette rows; the camera looks at the centre of the visible
        /// model (the original uses the target's position) and draws its most detailed LOD (Lod_SetForGamepiece(target, 0)); the
        /// sensor noise of a damaged head and building targets are not ported.
        /// </summary>
        void DrawTargetWindow(int g) => DrawTargetWindow(g, true, false);

        void DrawTargetText() => DrawTargetWindow(17, false, true);

        void DrawTargetWindow(int g, bool window, bool text)
        {
            if (window && g == 17 && !RadarAvailable) return; // Hud_CreateGauges 10016ed7 hides gauge 17, not text gauge 18.
            // DAT_100a0544 = 0: Hud_DrawTargetWindow draws nothing.
            if (window && TargetViewMode == 0) return;
            if (window && g == 17 && TargetSensorHidden()) return; // 0x1006606e, gauge +6 and DAT_100b18b8
            var r = GaugeRect(g);
            var old = GUI.color;
            GUI.color = window ? Pal(0) : Color.clear;
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = old;

            var target = _combat.Target != null && !_combat.Target.Destroyed ? _combat.Target : null;
            var structure = target == null ? _combat.TargetStructure : null;
            bool hasNav = target == null && structure == null && _director != null && _combat.TargetNav >= 0 &&
                          _combat.TargetNav < _director.Navs.Count;
            string name = null;
            int color = 0x0e;
            Vector3 where = Vector3.zero;

            if (target != null)
            {
                if (window && _targetTexture != null) GUI.DrawTexture(r, _targetTexture);
                // 0x10065990: GP +0x110, "Mech" when empty ("Unknown" only with GP flag 0x100 unidentified; no GPS has it).
                name = string.IsNullOrEmpty(target.DisplayName) ? "Mech" : target.DisplayName;
                color = target.Side == 0 ? 0x0e : target.Side == 2 ? 6 : 10;
                where = target.transform.position;
            }
            else if (structure != null)
            {
                // GT +0x14, "Installation" when empty; colour by Gt_Side 0x1002a7dc; the same 3D view as a unit.
                if (window && _targetTexture != null) GUI.DrawTexture(r, _targetTexture);
                name = string.IsNullOrEmpty(structure.DisplayName) ? "Installation" : structure.DisplayName;
                color = structure.Side == 0 ? 0x0e : structure.Side == 2 ? 6 : 10;
                where = structure.transform.position;
            }
            else if (hasNav)
            {
                var nav = _director.Navs[_combat.TargetNav];
                bool visited = _director.NavVisited(_combat.TargetNav);
                var icon = visited ? TargetNavVisitedIcon : TargetNavIcon;
                if (window && icon != null)
                {
                    float w = icon.width * Px, h = icon.height * Py;
                    GUI.DrawTexture(new Rect(r.center.x - w / 2, r.center.y - h / 2, w, h), icon);
                }
                name = string.IsNullOrEmpty(nav.Name) ? "Nav Point" : nav.Name;
                color = visited ? 1 : 2;
                where = nav.Position;
            }
            if (window) DrawFrame(r, 8);

            // Gauge 18 (Hud_DrawTargetText) is not one of the seven windows and stays on.
            if (name == null || !text) return;
            // An inspection result replaces the name for 0x16a ticks while the target stays the same (DAT_100b18b0/b4).
            object current = target != null ? target : structure != null ? (object)structure : null;
            if (_inspectMessage != null && (current != _inspectTarget || Time.time >= _inspectUntil)) _inspectMessage = null;
            if (_inspectMessage != null) name = _inspectMessage;
            var at = new Vector2(Layout.Rects[18].x * Sx, Layout.Rects[18].y * Sy);
            DrawText(name, at, color);
            // The distance is +0xc4 (the 3D distance of Vector_ToSpherical 0x10067cff) in units / 100.
            long metres = (long)Vector3.Distance(transform.position, where);
            string distance = metres <= 1000 ? $"{metres,3}m" : $"{metres / 1000f:0.00}k";
            DrawText(distance, at + new Vector2(0, LineHeight), 0x0e);
        }

        string _inspectMessage;
        object _inspectTarget;
        float _inspectUntil;

        /// <summary>
        /// Hud_DrawTargetText 0x10065990 on an inspection result (DAT_100b18c8): 2 -> "Out of range" and computer message
        /// 0x14; 1 or 3 -> the target's text (GP +0x126 / GT +0x2A, "Contents unknown" when empty) and the 0xf0 tone
        /// (rate index 5, priority 0x32). Either is shown for 0x16a ticks.
        /// </summary>
        void UpdateInspectMessage()
        {
            int status = _combat.ConsumeInspectStatus();
            if (status == 0) return;
            var target = _combat.Target;
            var structure = target == null ? _combat.TargetStructure : null;
            if (target == null && structure == null) return;
            _inspectTarget = target != null ? target : (object)structure;
            _inspectUntil = Time.time + 0x16a / (float)MechMotion.TicksPerSecond;
            var audio = Mw2Audio.Instance;
            if (status == 2)
            {
                _inspectMessage = "Out of range";
                if (audio != null) audio.Computer(0x14);
                return;
            }
            string contents = target != null ? target.InspectText : structure.Contents;
            _inspectMessage = string.IsNullOrEmpty(contents) ? "Contents unknown" : contents;
            if (audio != null) audio.Play(0xf0, transform.position, true);
        }

        // The target view is rendered here, outside OnGUI, so the camera render does not disturb the GUI pass.
        void LateUpdate()
        {
            UpdateDestroyedCamera();
            if (IsPlayer && _combat != null) UpdateInspectMessage();
            if (!IsPlayer || _combat == null || Layout == null || Layout.Rects == null || Layout.Rects.Length < HudLayout.GaugeCount) return;
            RenderMapScreen();
            RenderWindowViews();
            RenderWindowMaps();
            // The target view for the first window in mode 8 (its size), unless DAT_100a0544 = 0.
            _targetViewGauge = WindowWithMode(8, -1);
            if (_targetViewGauge < 0 || TargetViewMode == 0 || HudState == 0) return;
            if (_targetViewGauge == 17 && !RadarAvailable) return;
            if (_targetViewGauge == 17 && TargetSensorHidden()) return;
            var target = _combat.Target;
            if (target != null && !target.Destroyed) RenderTargetView(target, target);
            else if (_combat.TargetStructure != null && !_combat.TargetStructure.Destroyed)
                RenderTargetView(_combat.TargetStructure, null);
        }

        int _targetViewGauge = 17;

        /// <summary>
        /// 0x1006606e: a unit (target) or a GT (structure, its view radius the model radius from FUN_10003c5e instead
        /// of +0xe1c; its nodes carry flag 0x200, which material mode 3 shows in base colour 7). View mode 2
        /// (DAT_100a0544 = 2: DAT_1009e944 = 0) draws the target with its own materials.
        /// </summary>
        void RenderTargetView(Component root, Mw2MechCombat target)
        {
            if (TargetViewShader == null) return;
            int g = _targetViewGauge >= 0 ? _targetViewGauge : 17;
            int w = Mathf.Max(16, Layout.Rects[g].width * 2), h = Mathf.Max(16, (int)(Layout.Rects[g].height * 2.4f));
            if (_targetTexture == null || _targetTexture.width != w || _targetTexture.height != h)
            {
                if (_targetTexture != null) _targetTexture.Release();
                _targetTexture = new RenderTexture(w, h, 16) { filterMode = FilterMode.Point };
            }
            if (_targetCamera == null)
            {
                var go = new GameObject("MW2 target view") { hideFlags = HideFlags.HideAndDontSave };
                _targetCamera = go.AddComponent<Camera>();
                _targetCamera.enabled = false;
                _targetCamera.clearFlags = CameraClearFlags.SolidColor;
                _targetCamera.nearClipPlane = 0.3f;
                _targetCamera.farClipPlane = 2000f;
            }
            _targetCamera.backgroundColor = Pal(0);
            _targetCamera.targetTexture = _targetTexture;
            // FUN_100325c9 0x100325c9 renders the window with zoom 0x40000000 = 2.0: Hud_RenderView 0x100325e8 writes it
            // into the camera (+0x18) and restores the previous value after the pass, so the cockpit zoom (Z, 0x100233c1)
            // never reaches the target window.
            float horizontal = 2f * Mathf.Atan(1f / 2f) * Mathf.Rad2Deg;
            _targetCamera.fieldOfView = Camera.HorizontalToVerticalFieldOfView(horizontal, w / (float)h);

            // The window camera is close to the target, so the target is drawn at the most detailed level, as the LOD
            // choice of 0x1001f099 gives for a unit nearer than 1500 units; the cockpit camera's level is restored below.
            int lod = 0;
            if (root.TryGetComponent<Mw2LodGroup>(out var lods)) { lod = lods.Current; lods.Show(1); }

            // Aim at the centre of the visible model (a flying unit's hit box stays where it was built).
            var renderers = root.GetComponentsInChildren<Renderer>();
            Bounds bounds = default;
            bool any = false;
            foreach (var rr in renderers)
            {
                if (!rr.enabled) continue;
                if (any) bounds.Encapsulate(rr.bounds); else bounds = rr.bounds;
                any = true;
            }
            if (!any) bounds = new Bounds(root.transform.position, Vector3.zero);

            // 0x1006606e: camera = target - dir(b) * 3 * radius (+0xe1c), looking along b = the player's bearing to the
            // target (+0xd4): the target as seen from the cockpit, closer.
            float radius = target == null && root is Mw2Destructible gt ? gt.Radius
                : root.TryGetComponent<Mw2MechController>(out var tc) ? tc.CollisionRadius
                : new Vector2(bounds.extents.x, bounds.extents.z).magnitude;
            var heading = root.transform.position - transform.position;
            heading.y = 0;
            heading = heading.sqrMagnitude > 1e-6f ? heading.normalized : transform.forward;
            _targetCamera.transform.SetPositionAndRotation(bounds.center - heading * (3f * radius), Quaternion.LookRotation(heading, Vector3.up));

            const int layer = 31;
            var layers = new int[renderers.Length];
            for (int i = 0; i < renderers.Length; i++) { layers[i] = renderers[i].gameObject.layer; renderers[i].gameObject.layer = layer; }
            _targetCamera.cullingMask = 1 << layer;
            // Material mode 3 (Material_Shade 0x1001afb0): each part with the unit flag 0x100 by its own node's damage level
            // (model word & 0xf0, Mech_ApplyDamage 0x10037b59 -> FUN_1005b367) as a lit row-0 colour: base 7 at level 0,
            // 3 below 12, 11 from 12 on.
            Shader.SetGlobalColor("_Mw2TargetDark", Pal(5));
            Shader.SetGlobalColor("_Mw2TargetLight", Pal(7));
            Mw2HitMap hitMap = null;
            if (target != null) target.TryGetComponent(out hitMap);
            var block = new MaterialPropertyBlock();
            for (int i = 0; i < renderers.Length; i++)
            {
                int level = hitMap != null ? hitMap.LevelOf(renderers[i].transform) : 0;
                int baseColor = level == 0 ? 7 : level < 12 ? 3 : 0x0b;
                block.SetColor("_Mw2TargetDark", Pal(baseColor - 2));
                block.SetColor("_Mw2TargetLight", Pal(baseColor));
                renderers[i].SetPropertyBlock(block);
            }
            // Hud_RenderView temporarily selects material mode 0/3 independently of enhanced mode 1.
            Shader.SetGlobalFloat("_Mw2Enhanced", 0);
            if (TargetViewMode == 2) _targetCamera.Render();
            else _targetCamera.RenderWithShader(TargetViewShader, "RenderType");
            Shader.SetGlobalFloat("_Mw2Enhanced", 0);
            for (int i = 0; i < renderers.Length; i++) { renderers[i].gameObject.layer = layers[i]; renderers[i].SetPropertyBlock(null); }
            if (lods != null && lod != 0) lods.Show(lod);
        }

        void OnDestroy()
        {
            if (_targetCamera != null) Destroy(_targetCamera.gameObject);
            if (_targetTexture != null) _targetTexture.Release();
            ReleaseWindowViews();
            ReleaseWindowMaps();
        }

        /// <summary>Side 0 friendly, 2 neutral, anything else enemy (FUN_1002a7c1 values).</summary>
        static Texture2D BySide(int side, Texture2D friendly, Texture2D enemy, Texture2D neutral) =>
            side == 0 ? friendly : side == 2 ? neutral : enemy;

        /// <summary>Draws an SHP sprite with its hot spot at <paramref name="at"/> (640x480 pixels scaled).</summary>
        void DrawSprite(Texture2D tex, Vector2 at)
        {
            if (tex == null) return;
            float w = tex.width * Px, h = tex.height * Py;
            GUI.DrawTexture(new Rect(at.x - w / 2, at.y - h / 2, w, h), tex);
        }

        /// <summary>Heat dial scale W = window +0x8c: 0xea4e/65536 of the window's width in 640x480 pixels (0x10028e60).</summary>
        int HeatDialScale(int g) => Layout != null && Layout.Rects != null && Layout.Rects.Length > g
            ? (int)(0xea4e / 65536f * Layout.Rects[g].width * 2f) : 1;

        /// <summary>
        /// Heat dial (0x10029521) in window 32: a needle in colour 2 from the top edge to the centre; rays every 15 degrees
        /// clockwise from the needle up to the heat angle (filtered value >> 16) / W * 360 (355 when full), colour 0x0e
        /// below 121 degrees, 3 up to 269, then 10. A ray (0x10029332) runs from the edge of gauge 30's box around the
        /// centre to the edge of the window. Approximation: both ends are clipped to boxes (0x1004b71f / 0x1004bd21);
        /// in another window than 32 gauge 30's box is scaled with the window.
        /// </summary>
        void DrawHeatDial(int g)
        {
            // Hud_DrawHeatDial 0x10029521: both heat modes update DAT_10090658 while they draw.
            // The dial's target is 16.16 pixels; the bar's target is whole pixels.
            Filter(ref _heatLevelDisplay, (int)(_combat.Heat * 65536f * HeatDialScale(g) / 100f), 1);
            var r = GaugeRect(g);
            var units = Layout.Rects[g];
            var c = new Vector2(r.x + ((units.width + 1) >> 1) * Sx, r.y + ((units.height + 1) >> 1) * Sy);
            var inner = Layout.Rects[30];
            float innerX = ((inner.width + 1) >> 1) * Sx, innerY = ((inner.height + 1) >> 1) * Sy;
            if (g != 32)
            {
                innerX *= units.width / (float)Mathf.Max(1, Layout.Rects[32].width);
                innerY *= units.height / (float)Mathf.Max(1, Layout.Rects[32].height);
            }
            float outerX = c.x - r.x, outerY = c.y - r.y;

            int w = Mathf.Max(1, HeatDialScale(g));
            int angle = (_heatLevelDisplay >> 16) < w ? (int)((_heatLevelDisplay >> 16) * 360L / w) : 0x163;

            if (_lineMaterial == null)
            {
                _lineMaterial = new Material(Shader.Find("Hidden/Internal-Colored")) { hideFlags = HideFlags.HideAndDontSave };
                _lineMaterial.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
            }
            _lineMaterial.SetPass(0);
            GL.PushMatrix();
            GL.LoadPixelMatrix();
            GL.Begin(GL.LINES);
            void Line(Vector2 a, Vector2 b, Color col)
            {
                GL.Color(col);
                GL.Vertex3(a.x, Screen.height - a.y, 0);
                GL.Vertex3(b.x, Screen.height - b.y, 0);
            }
            // Point where a ray from the centre leaves a box of half extents hx, hy.
            Vector2 Edge(float deg, float hx, float hy)
            {
                float rad = deg * Mathf.Deg2Rad;
                var d = new Vector2(Mathf.Cos(rad), Mathf.Sin(rad));
                float tx = Mathf.Abs(d.x) > 1e-5f ? hx / Mathf.Abs(d.x) : float.MaxValue;
                float ty = Mathf.Abs(d.y) > 1e-5f ? hy / Mathf.Abs(d.y) : float.MaxValue;
                return c + d * Mathf.Min(tx, ty);
            }
            Line(new Vector2(c.x, r.y), c, Pal(2));
            if (angle != 0)
            {
                int color = 0x0e;
                void Ray(int a)
                {
                    if (a >= 0x79 && a <= 0x10d) color = 3;
                    else if (a > 0x10d) color = 10;
                    float deg = (a + 0x10e) % 0x168;    // 270 = up on screen, angles grow clockwise
                    Line(Edge(deg, innerX, innerY), Edge(deg, outerX, outerY), Pal(color));
                }
                for (int a = 0; a < angle; a += 15) Ray(a);
                Ray(angle);
            }
            GL.End();
            GL.PopMatrix();
        }

        /// <summary>
        /// Throttle bar box of window 22 in its 640x480 pixels (0x10028f68): w = 0x1e7a/65536 of the window width,
        /// h = 0x8dc9/65536 of its height; the frame spans x (W - w - 1)..(W - 1), y (H - 1.5h - 3)..(H - 1); the fill
        /// starts at x + 1 and grows up from the zero line H - h/2 - 2.
        /// </summary>
        (int w, int h, int winW, int winH) ThrottleBox(int g)
        {
            var u = Layout.Rects[g];
            int winW = u.width * 2, winH = (int)(u.height * 2.4f);
            return ((int)(0x1e7a / 65536f * winW), (int)(0x8dc9 / 65536f * winH), winW, winH);
        }

        /// <summary>
        /// Throttle bar (0x10029a91, called by the speed gauge 0x100696cb): a frame in colour 10, then the filtered
        /// length (value >> 16) as columns (0x10029e41: quarters of the width in colours c-1, c, c-1, c-2) from the zero
        /// line upwards in colour 0x0f, capped at h - 1; in reverse (control +0x30) downwards in colour 7, capped at h/2.
        /// </summary>
        void DrawThrottle(int g, bool reverse)
        {
            var (w, h, winW, winH) = ThrottleBox(g);
            var o = GaugeRect(g).position;
            Rect Px2Screen(int x0, int y0, int x1, int y1) => new Rect(o.x + x0 * Px, o.y + y0 * Py, (x1 - x0 + 1) * Px, (y1 - y0 + 1) * Py);

            int left = winW - w - 1, zero = winH - h / 2 - 2;
            DrawFrame(Px2Screen(left, winH - (h + h / 2) - 3, winW - 1, winH - 1), 10);

            int len = _throttleBar >> 16, color = 0x0f, bottom = zero;
            if (!reverse) { if (h <= len) len = h - 1; }
            else
            {
                len = -len;
                if (h / 2 < len) len = h / 2;
                color = 7;
                bottom = zero + len;
            }
            int height = len + 1, columns = w - 1, half = columns / 2;
            if (height <= 0) return;
            var old = GUI.color;
            void Columns(int from, int to, int c)
            {
                if (to <= from) return;
                GUI.color = Pal(c);
                GUI.DrawTexture(Px2Screen(left + 1 + from, bottom - height, left + to, bottom), Texture2D.whiteTexture);
            }
            Columns(0, half / 2, color - 1);
            Columns(half / 2, half, color);
            Columns(half, half + (columns - half) / 2, color - 1);
            Columns(half + (columns - half) / 2, columns, color - 2);
            GUI.color = old;
        }

        static float Wrap180(float a)
        {
            if (a > 180f) a -= 360f;
            else if (a < -180f) a += 360f;
            return a;
        }

        /// <summary>
        /// Compass in gauge 28 (0x10032f05 from 0x100176cb). Angles: H = (body heading +0x60 mod 360 + 360) mod 360,
        /// T = torso twist mod 360, B = target bearing (+0xd4) - H wrapped to +-180, B - T wrapped again. Centre
        /// (cx, cy) = (0x8000, 0x6666)/65536 of the gauge (0x10099ee8), k = COMPASS width / 360 px per degree.
        /// 0x10033cb7: the tape at cx - trunc(k * H) and a copy trunc(k * 360) px to the side, a shaded bar of colour 0x0f
        /// from cx over trunc(T) px (pixels, not degrees * k) in the rows above the centre (CMPMKR3 hot spot y), then
        /// CMPMKR3. 0x10033a56 with a target: CRTUP above and CRTDOWN below the centre (their elevation test,
        /// |pitch| < 196608, always passes); the bearing mark (B for a nav, B - T for a unit): CMPMKR1 at cx + trunc(k * b),
        /// or CMPMKR2 and CMPMKR4 at the centre when b = 0; outside the gauge CRTRGHT when b > -3 (twice when > 90),
        /// CRTLFT when b < 3 (twice when < -90). Tape and marks are clipped to the gauge, the arrows are not (0x10034440).
        /// Approximation: the target is our combat target, else the selected nav (as in the target window).
        /// </summary>
        void DrawCompass()
        {
            if (Compass == null || Compass.Texture == null || Layout.Rects.Length <= 28) return;
            var gauge = GaugeRect(28);
            var u = Layout.Rects[28];
            int winW = u.width * 2, winH = (int)(u.height * 2.4f);
            int cx = (int)(0x8000 / 65536f * winW), cy = (int)(0x6666 / 65536f * winH);
            float k = Compass.Texture.width / 360f;

            float heading = TryGetComponent<Mw2MechController>(out var mc) ? mc.HeadingDeg : 0f;
            float h = ((heading % 360f) + 360f) % 360f;
            float t = _twist != null ? _twist.Current % 360f : 0f;      // MW2 twist [3]

            void Put(HudSprite sp, float x, float y, Vector2 origin)
            {
                if (sp == null || sp.Texture == null) return;
                GUI.DrawTexture(new Rect(origin.x + (x - sp.HotX) * Px, origin.y + (y - sp.HotY) * Py,
                                         sp.Texture.width * Px, sp.Texture.height * Py), sp.Texture);
            }

            GUI.BeginGroup(gauge);
            var o = Vector2.zero;
            int x0 = cx - (int)(k * h), wrap = (int)(k * 360f);
            Put(Compass, x0, cy, o);
            Put(Compass, x0 < 0 ? x0 + wrap : x0 - wrap, cy, o);
            int hotY = CompassCentre != null ? CompassCentre.HotY : 5;
            int len = (int)t;
            if (len != 0)
                DrawShadedBar(new Rect((len < 0 ? cx + len : cx) * Px, (cy - hotY) * Py, Mathf.Abs(len) * Px, hotY * Py), 0x0f);
            Put(CompassCentre, cx, cy, o);

            // Target: our combat target, else the selected nav.
            Vector3? where = null;
            bool nav = false;
            if (_combat != null && _combat.Target != null && !_combat.Target.Destroyed) where = _combat.Target.transform.position;
            else if (_combat != null && _combat.TargetStructure != null) where = _combat.TargetStructure.transform.position;
            else if (_combat != null && _director != null && _combat.TargetNav >= 0 && _combat.TargetNav < _director.Navs.Count)
            {
                where = _director.Navs[_combat.TargetNav].Position;
                nav = true;
            }
            float b = 0f;
            if (where.HasValue)
            {
                var d = where.Value - transform.position;
                float bearing = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;      // MW2 heading that faces the target
                b = Wrap180(bearing - h);
                if (!nav) b = Wrap180(b - t);
                if (b == 0f) Put(CompassTargetAhead, cx, cy, o);
                else Put(CompassTarget, cx + (int)(k * b), cy, o);
                if (b == 0f) Put(CompassCentreAhead, cx, cy, o);
            }
            GUI.EndGroup();
            if (!where.HasValue) return;

            int upH = CompassUp != null && CompassUp.Texture != null ? CompassUp.Texture.height : 0;
            int centreH = CompassCentre != null && CompassCentre.Texture != null ? CompassCentre.Texture.height : 0;
            Put(CompassUp, cx, cy - upH - hotY, gauge.position);
            Put(CompassDown, cx, cy + upH + (centreH - hotY), gauge.position);

            int arrowW = CompassLeft != null && CompassLeft.Texture != null ? CompassLeft.Texture.width : 0;
            int arrowH = CompassLeft != null && CompassLeft.Texture != null ? CompassLeft.Texture.height : 0;
            int ay = cy + arrowH / 2;
            if (b > -3f)
            {
                Put(CompassRight, winW - 1 + arrowW, ay, gauge.position);
                if (b > 90f) Put(CompassRight, winW + arrowW, ay, gauge.position);
            }
            if (b < 3f)
            {
                Put(CompassLeft, -arrowW, ay, gauge.position);
                if (b < -90f) Put(CompassLeft, -arrowW - 1, ay, gauge.position);
            }
        }

        /// <summary>
        /// Shaded bar of 0x10029fcb: the quarters of its height use colours c-1, c, c-1, c-2.
        /// </summary>
        void DrawShadedBar(Rect r, int color)
        {
            if (r.width <= 0) return;
            // Rows of 0x10029fcb: h/2/2 in c-1, up to h/2 in c, up to h/2 + (h - h/2)/2 in c-1, the rest c-2 (in pixels
            // of the 640x480 surface).
            int h = Mathf.Max(1, Mathf.RoundToInt(r.height / Py)), half = h / 2, q1 = half / 2, q3 = half + (h - half) / 2;
            var old = GUI.color;
            void Rows(int from, int to, int c)
            {
                if (to <= from) return;
                GUI.color = Pal(c);
                GUI.DrawTexture(new Rect(r.x, r.y + from * Py, r.width, (to - from) * Py), Texture2D.whiteTexture);
            }
            Rows(0, q1, color - 1);
            Rows(q1, half, color);
            Rows(half, q3, color - 1);
            Rows(q3, h, color - 2);
            GUI.color = old;
        }

        /// <summary>
        /// dH/dT bar (0x100298fb): v = filtered heat change << 10. v < 1: all colour 7; v < 0x300: the left v/0x300 of
        /// the width in colour 3, the rest 7; otherwise the left (v - 0x300)/0x300 in 0x0b, the rest 3.
        /// </summary>
        void DrawHeatRate(Rect r)
        {
            int v = _heatRateBar, left, right;
            if (v < 1) { left = 7; right = 7; v = 0; }
            else if (v < 0x300) { left = 3; right = 7; }
            else { left = 0x0b; right = 3; v -= 0x300; }
            float fill = Mathf.Clamp(v / 768f, 0f, 1f) * r.width;
            if (fill > 0) DrawShadedBar(new Rect(r.x, r.y, fill, r.height), left);
            if (fill < r.width) DrawShadedBar(new Rect(r.x + fill, r.y, r.width - fill, r.height), right);
        }

        void DrawGauges()
        {
            // Weapon rows: gauges 3..9 (left column) and 10..16 (right column), assigned by HudLayout.AssignWeaponRows;
            // hidden by command 0xc (_DAT_1008f590).
            for (int i = 0; i < _combat.Weapons.Count && WeaponRowsShown; i++)
            {
                var w = _combat.Weapons[i];
                int g = w.HudGauge, color = WeaponColor(i);
                if (g < 3 || g > 16) continue;
                // The pool count is in rounds (mech +0x1a6): LRM20 shows 240 for 12 salvos of 20 missiles.
                string text = w.Ammo >= 0 ? $"{w.Def.Name} {w.Ammo}" : w.Def.Name;
                DrawText(text, GaugeOrigin(g), color);
                if (i == _combat.SelectedWeapon) DrawFrame(GaugeRect(g), color);
            }

            // The seven windows by their modes (FUN_1003e925), in gauge order, then the target text (gauge 18,
            // Hud_DrawTargetText), the objectives (gauge 19) and the compass (DAT_10099fa4).
            for (int i = 0; i < 7; i++) DrawWindow(WindowGauges[i], WindowModes[i]);
            DrawTargetText();
            if (ObjectivesShown && Layout.Rects.Length > 19) DrawObjectives();
            // FUN_100695df: gauge 21 shows the autopilot status at its layout text origin.
            if (Layout.Rects.Length > 21 && TryGetComponent<Mw2MechController>(out var controller) && controller.Autopilot)
                DrawText("AUTOPILOT", GaugeOrigin(21), 0x0e);
            if (CompassShown) DrawCompass();
        }

        /// <summary>Window mode 9 (0x100696cb): "Speed:" and "%d kph", colour 6 while moving backwards, and the throttle bar.</summary>
        void DrawSpeedWindow(int g)
        {
            if (!TryGetComponent<Mw2MechController>(out var controller)) return;
            var at = GaugeOrigin(g);
            int kph = controller.HudSpeedKph;
            DrawText("Speed:", at, kph < 0 ? 6 : 0x0e);
            DrawText($"{kph} kph", at + new Vector2(0, LineHeight), kph < 0 ? 6 : 0x0e);
            DrawThrottle(g, controller.Reverse);
        }

        int _jetFuelDisplay; // DAT_10090688: shared across altitude windows, initial 0, filter shift 1.

        /// <summary>Window mode 10 (0x10069c8a): absolute altitude text (GP Y minus MGEO[0]), Jets label and
        /// FUN_10029c9c's smoothed vertical fuel bar when mech +0xdf4 >=0.</summary>
        void DrawAltitudeWindow(int g)
        {
            if (!TryGetComponent<Mw2MechController>(out var controller)) return;
            var at = GaugeOrigin(g);
            int alt = (int)(controller.AltitudeUnits * 1000f);
            DrawText("Alt:", at, 0x0e);
            DrawText($"{alt / 65536.0:0.00} m", at + new Vector2(0, LineHeight), 0x0e);
            if (controller.HudJetFuel >= 0)
            {
                DrawText("Jets", at + new Vector2(0, 2 * LineHeight), 0x0e);
                DrawJetFuel(g, controller.HudJetFuel);
            }
        }

        /// <summary>FUN_10029037 / FUN_10029c9c / FUN_10029e41: a colour-10 frame
        /// and upward columns in palette 10,11,10,9. Use the current window's
        /// dimensions so mercs.hud reassignment and power animation scale it.</summary>
        void DrawJetFuel(int g, int fuel)
        {
            var (w, h) = WindowPixels(g);
            if (_gaugeOverride == g)
            {
                w = Mathf.Max(1, Mathf.RoundToInt(_gaugeOverrideRect.width / Px));
                h = Mathf.Max(1, Mathf.RoundToInt(_gaugeOverrideRect.height / Py));
            }
            var box = HudJetBar.Layout(w, h);
            var origin = GaugeRect(g).position;
            Rect Pixels(int x, int y, int width, int height) =>
                new Rect(origin.x + x * Px, origin.y + y * Py, width * Px, height * Py);
            var saved = GUI.color;
            GUI.color = Pal(10);
            int fw = box.Right - box.Left + 1, fh = box.Bottom - box.Top + 1;
            GUI.DrawTexture(Pixels(box.Left, box.Top, 1, fh), Texture2D.whiteTexture);
            GUI.DrawTexture(Pixels(box.Right, box.Top, 1, fh), Texture2D.whiteTexture);
            GUI.DrawTexture(Pixels(box.Left, box.Top, fw, 1), Texture2D.whiteTexture);
            GUI.DrawTexture(Pixels(box.Left, box.Bottom, fw, 1), Texture2D.whiteTexture);
            int length = HudJetBar.FillHeight(box, fuel, ref _jetFuelDisplay);
            if (length <= 0 || box.Columns <= 0) { GUI.color = saved; return; }
            int half = box.Columns / 2, q1 = half / 2, q3 = half + (box.Columns - half) / 2;
            void Columns(int from, int to, int colour)
            {
                if (to <= from) return;
                GUI.color = Pal(colour);
                GUI.DrawTexture(Pixels(box.FillX + from, box.FillBottom - length, to - from, length + 1), Texture2D.whiteTexture);
            }
            Columns(0, q1, 10); Columns(q1, half, 11);
            Columns(half, q3, 10); Columns(q3, box.Columns, 9);
            GUI.color = saved;
        }

        static bool DrawAtWorld(Camera cam, Texture2D tex, Vector3 world, float scale, out Vector2 screen)
        {
            var p = cam.WorldToScreenPoint(world);
            screen = new Vector2(p.x, Screen.height - p.y);
            if (p.z <= 0) return false;
            GUI.DrawTexture(new Rect(screen.x - tex.width * scale / 2, screen.y - tex.height * scale / 2,
                                     tex.width * scale, tex.height * scale), tex);
            return true;
        }
    }
}
