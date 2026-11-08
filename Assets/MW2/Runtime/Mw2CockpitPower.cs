using System;
using MW2.Formats;
using UnityEngine;

namespace MW2
{
    /// <summary>
    /// HUD while the mech powers up or down (Hud_UpdatePlayer 0x100176cb by the mech state +0xdd4): state 1 calls each
    /// gauge's power-up method (+0xf0), states 0 and 3 its power-down method (+0xf8), state 2 the normal one (+0xf4);
    /// destroyed or ejected (4, 5) draws no HUD. Gauges without such a method are not drawn in that state.
    /// Power-up / power-down methods exist for:
    /// - the windows (0x1003f2b7 / 0x1003f3e6; target window 0x10066734 / 0x1006682f): the window is drawn into a
    ///   surface that opens from a point in its centre to the full window in two halves, first the width then the
    ///   height, and closes the other way (0x1004c2f5); windows in mode 0 (off), 1 or 2 (damage wireframe) are skipped
    ///   and windows 32 / 33 (heat) have no animation, so they stay dark. Once opened the window draws normally.
    /// - the weapon rows (0x1006bae9): just the weapon name, each row once the mission clock passes its time in
    ///   0x1008fa68 (543..723 ticks for the left column, 693..814 for the right), so the rows come up one by one.
    /// - the radar (0x100511fc / 0x100514fe -> 0x10051139): drawn into a rectangle that grows from (closes to) its centre
    ///   evenly in 181 ticks (0x1004c1e3).
    /// Animations (gauge +0xac, table 0x1008f9e0): 181 ticks from (0.5, 0.5, 0.5, 0.5) to (0, 0, 1, 1) of the window;
    /// the speed, altitude and other windows share one (0x1008f9d8), and each window's call advances it, so with two
    /// open windows it runs twice as fast; the target window (0x1008f990) and the radar (0x1009e4d8) have their own.
    /// A window restarts its animation when its last state was another one (+0xb0).
    /// FUN_10016ddc transforms those fractions by the gauge rectangle with FUN_1004a818; the animation temporarily
    /// replaces both the surface and gauge rectangle, so the contents scale with the opening. The MIDI power hum
    /// (0x1003fa00 / 0x1003fb0e / 0x1003fbbe) uses the Windows MIDI output adapter; the reticle, target brackets and the nav
    /// marker are hidden outside state 2 like the gauges without power methods.
    /// </summary>
    public partial class Mw2Cockpit
    {
        sealed class WindowAnimation
        {
            public int Current, Previous, Timer;    // +0, +4, +8 of the animation state
            public const int Duration = 181;
            public void Reset() { Current = 1; Previous = 0; }      // 0x1004c0e0
        }

        // Rectangle A (closed) and B (open) of every animation in 0x1008f9e0 / 0x1009e4d8, 16.16 fractions.
        static readonly Vector4 Closed = new Vector4(0.5f, 0.5f, 0.5f, 0.5f), Open = new Vector4(0f, 0f, 1f, 1f);

        /// <summary>Time of each gauge's power-up text (0x1008fa68), mission ticks.</summary>
        static readonly int[] RevealTicks =
        {
            362, 181, 181, 543, 573, 603, 633, 663, 693, 723, 814, 784, 754, 724, 693, 723, 753,
            181, 181, 181, 181, 181, 181, 181, 181, 181, 181, 181, 181, 181, 181, 181, 181, 0
        };

        readonly WindowAnimation _sharedWindows = new WindowAnimation(), _targetWindow = new WindowAnimation(),
                                 _radarWindow = new WindowAnimation();
        readonly int[] _gaugeLastState = new int[HudLayout.GaugeCount];
        int _radarPhase;            // DAT_101087ac: 1 opening, 3 closing, 2 normal
        float _lastHudTime = -1f;

        /// <summary>
        /// Draws the HUD of a mech that is not running and returns true; returns false (after noting state 2 for the
        /// window animations) when the normal HUD should be drawn.
        /// </summary>
        bool DrawPowerHud()
        {
            int state = _combat != null ? _combat.PowerState : 2;
            int dt = 0;
            if (_lastHudTime >= 0f) dt = Mathf.Max(0, Mathf.RoundToInt((Time.time - _lastHudTime) * (float)MechMotion.TicksPerSecond));
            _lastHudTime = Time.time;
            bool layout = Layout != null && Layout.Rects != null && Layout.Rects.Length >= HudLayout.GaugeCount;
            if (state == 2)
            {
                _gaugeLastState[2] = _gaugeLastState[17] = _gaugeLastState[22] = _gaugeLastState[26] = 2;
                _radarPhase = 2;
                return false;
            }
            if (state == 4 || state == 5 || !layout) return true;

            bool up = state == 1;
            if (up) DrawRevealedRows();
            // Windows in gauge order: Hud_WindowPowerUp / PowerDown skip modes 0, 1 and 2 and windows without an animation
            // (32, 33); the target window object (f0c4) has its own methods that always draw the target window unless
            // DAT_100a0544 = 0, whatever its mode.
            for (int i = 0; i < 7; i++)
            {
                int g = WindowGauges[i], mode = WindowModes[i];
                if (g == 17)
                {
                    if (!RadarAvailable) continue; // Hud_CreateGauges 10016ed7.
                    if (TargetViewMode != 0) PowerWindow(17, _targetWindow, state, dt, () => DrawTargetWindow(17));
                    continue;
                }
                if (mode == 0 || mode == 1 || mode == 2 || g == 32 || g == 33) continue;
                PowerWindow(g, _sharedWindows, state, dt, () => DrawWindow(g, mode));
            }
            if (RadarAvailable && (RadarMode == 1 || RadarMode == 2)) PowerRadar(up, dt);
            return true;
        }

        /// <summary>0x1006bae9: the weapon name at the row's corner once the mission clock passes the row's time.</summary>
        void DrawRevealedRows()
        {
            int now = (int)((_director != null ? _director.MissionTime : Time.timeSinceLevelLoad) * MechMotion.TicksPerSecond);
            for (int i = 0; i < _combat.Weapons.Count; i++)
            {
                int g = _combat.Weapons[i].HudGauge;
                if (g < 3 || g > 16 || RevealTicks[g] >= now) continue;
                DrawText(_combat.Weapons[i].Def.Name, GaugeRect(g).position, 0x0e);
            }
        }

        /// <summary>0x1003f2b7 / 0x1003f3e6 (and 0x10066734 / 0x1006682f for the target window).</summary>
        void PowerWindow(int g, WindowAnimation anim, int state, int dt, Action draw)
        {
            int last = _gaugeLastState[g];
            if (state == 1)
            {
                if (last != 1) anim.Reset();
                var surface = Step(anim, true, dt, true);
                if (surface == null) draw();
                else DrawInSurface(g, surface.Value, draw);
            }
            else
            {
                if (last != 0 && last != 3 && last != 4) anim.Reset();
                var surface = Step(anim, false, dt, true);
                if (surface != null) DrawInSurface(g, surface.Value, draw);
            }
            _gaugeLastState[g] = state;
        }

        /// <summary>0x100511fc / 0x100514fe: the radar opens or closes in its own rectangle; open, it draws normally.</summary>
        void PowerRadar(bool up, int dt)
        {
            int phase = up ? 1 : 3;
            if (_radarPhase != phase) _radarWindow.Reset();
            _radarPhase = phase;
            var full = RadarRect();
            var surface = Step(_radarWindow, up, dt, false);
            if (surface == null) { if (up) DrawRadar(full); return; }
            var f = surface.Value;
            var r = new Rect(full.x + f.x * full.width, full.y + f.y * full.height, (f.z - f.x) * full.width, (f.w - f.y) * full.height);
            if (r.width >= 1f) DrawRadar(r);
        }

        /// <summary>
        /// 0x1004c2f5 (two halves) / 0x1004c1e3 (even): the surface rectangle of this call, or null once done (and before
        /// a reset). Opening goes from A to B, closing from B to A; in halves the first moves x (opening) or y (closing),
        /// the second the other axis. Every call after the first adds the frame's ticks.
        /// </summary>
        static Vector4? Step(WindowAnimation a, bool opening, int dt, bool halves)
        {
            Vector4? result = null;
            int current = a.Current;
            if (current == 1)
            {
                Vector4 from = opening ? Closed : Open, to = opening ? Open : Closed;
                if (a.Previous == 0)
                {
                    result = from;
                    a.Timer = 0;
                }
                else
                {
                    a.Timer += dt;
                    float f = (int)((long)a.Timer * 65536 / WindowAnimation.Duration) / 65536f;
                    if (f < 1f)
                    {
                        if (!halves) result = Vector4.Lerp(from, to, f);
                        else
                        {
                            bool firstHalf = f <= 0.5f;
                            var r = firstHalf ? from : to;
                            float k = firstHalf ? f * 2f : (f - 0.5f) * 2f;
                            bool moveX = firstHalf == opening;      // opening: x then y; closing: y then x
                            if (moveX) { r.x = from.x + (to.x - from.x) * k; r.z = from.z + (to.z - from.z) * k; }
                            else { r.y = from.y + (to.y - from.y) * k; r.w = from.w + (to.w - from.w) * k; }
                            result = r;
                        }
                    }
                    else
                    {
                        result = to;
                        a.Timer = 0;
                        current = 0;
                    }
                }
            }
            a.Previous = a.Current;
            a.Current = current;
            return result;
        }

        /// <summary>
        /// Hud_WindowPowerUp 0x1003f2b7 / FUN_10016ddc: replace both the window's surface and gauge rectangles with
        /// the animated rectangle, transformed from fractions by the gauge's full rectangle.
        /// </summary>
        void DrawInSurface(int g, Vector4 f, Action draw)
        {
            var full = GaugeRect(g);
            // Surface rectangles are inclusive (x1 - x0 + 1), so a closed axis is still one 320x200 unit wide.
            var r = new Rect(full.x + f.x * full.width, full.y + f.y * full.height,
                             Mathf.Max(Sx, (f.z - f.x) * full.width), Mathf.Max(Sy, (f.w - f.y) * full.height));
            GUI.BeginGroup(r);
            _gaugeOverride = g;
            _gaugeOverrideRect = new Rect(0, 0, r.width, r.height);
            try { draw(); }
            finally
            {
                _gaugeOverride = -1;
                GUI.EndGroup();
            }
        }
    }
}
