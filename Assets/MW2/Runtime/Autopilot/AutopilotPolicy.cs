using System;
using System.Collections.Generic;
using MW2.Formats;

namespace MW2.Autopilot
{
    public enum PilotState { Navigation, Search, Engage, Evade, Reposition, Retreat, CompleteObjective, MissionFinished }
    public enum PilotTactic { Balanced, KeepRange, Flank, NavFirst, CombatFirst, UnitFirst, Mobile, Guard, MobileNav }

    /// <summary>Unity test pilot heuristics, not original AI. No enemy health, loadout or future position is an input.</summary>
    public static class AutopilotPolicy
    {
        public static bool MobileTactic(PilotTactic tactic) => tactic == PilotTactic.Mobile || tactic == PilotTactic.Guard || tactic == PilotTactic.MobileNav;
        // Test-pilot strategy, not original AI: leave optional resistance behind once the
        // public mandatory work is done. Vital/Mandatory come from the original briefing.
        // Caller supplies an active, public NAV that it can actually navigate to.
        public static bool PreferMandatoryNav(IReadOnlyList<MissionOrder> orders, int navOrder, Func<int, bool> succeeded,
                                              PilotTactic tactic = PilotTactic.Balanced)
        {
            var nav = orders[navOrder];
            if (!nav.Vital || nav.Mask != MissionOrder.GotoNav) return false;
            // Explicit test tactic for rendezvous/transit missions. Caller still requires an active public NAV.
            if (tactic == PilotTactic.NavFirst || tactic == PilotTactic.MobileNav) return true;
            if (!nav.Mandatory) return false;
            bool completedWork = false;
            for (int i = 0; i < orders.Count; i++)
            {
                var order = orders[i];
                if (!order.Vital || !order.Mandatory || order.Mask == MissionOrder.GotoNav) continue;
                if (!succeeded(i)) return false;
                completedWork = true;
            }
            return completedWork;
        }

        public static PilotState Decide(bool finished, bool enemy, bool line, bool stalled, bool goal,
                                        float heat, float structureFraction, float distance, float preferredRange)
        {
            if (finished) return PilotState.MissionFinished;
            if (stalled) return PilotState.Reposition;
            if (enemy && structureFraction < 0.3f) return PilotState.Retreat;
            if (enemy && heat >= 65f) return PilotState.Evade;
            if (enemy && !line) return PilotState.Reposition;
            if (enemy && distance < preferredRange * 0.35f) return PilotState.Evade;
            if (enemy) return PilotState.Engage;
            return goal ? PilotState.Navigation : PilotState.Search;
        }

        public static bool FireAllowed(float heat, float weaponHeat, int burst, bool ready, bool inRange,
                                       bool clear, float errorDegrees, bool lockRequired, bool locked) =>
            ready && inRange && clear && Math.Abs(errorDegrees) < 3f &&
            heat + Math.Max(0f, weaponHeat) * Math.Max(1, burst) < 65f && (!lockRequired || locked);

        public static float TargetScore(float distance, bool visible, bool objective, bool current) =>
            (visible ? 300f : 0f) + (objective ? 200f : 0f) + (current ? 80f : 0f) - distance * 0.1f;

        // Constant-velocity interception from observed positions, never the target's internal AI/physics state.
        public static float InterceptSeconds(float rx, float ry, float rz, float vx, float vy, float vz, float speed, float maxSeconds = 1.5f)
        {
            if (speed <= 0f) return 0f;
            double a = vx * vx + vy * vy + vz * vz - speed * speed;
            double b = 2.0 * (rx * vx + ry * vy + rz * vz), c = rx * rx + ry * ry + rz * rz;
            double t;
            if (Math.Abs(a) < 1e-6) t = Math.Abs(b) < 1e-6 ? Math.Sqrt(c) / speed : -c / b;
            else
            {
                double d = b * b - 4 * a * c;
                if (d < 0) return 0f;
                double x = (-b - Math.Sqrt(d)) / (2 * a), y = (-b + Math.Sqrt(d)) / (2 * a);
                t = x > 0 && y > 0 ? Math.Min(x, y) : Math.Max(x, y);
            }
            return (float)Math.Max(0, Math.Min(maxSeconds, t));
        }
    }

    /// <summary>Simulation-time displacement window: intentional stops do not accumulate a stuck timeout.</summary>
    public sealed class StuckDetector
    {
        float _time, _x, _z;
        bool _started;
        public void Reset() { _started = false; }
        public bool Update(float now, float x, float z, bool moving)
        {
            if (!_started || !moving) { _started = true; _time = now; _x = x; _z = z; return false; }
            float dx = x - _x, dz = z - _z;
            if (dx * dx + dz * dz > 16f) { _time = now; _x = x; _z = z; return false; }
            return now - _time > 7f;
        }
    }
}
