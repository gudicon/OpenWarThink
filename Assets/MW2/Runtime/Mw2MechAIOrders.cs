using System.Collections.Generic;
using MW2.Formats;
using UnityEngine;

namespace MW2
{
    /// <summary>
    /// The team order dispatcher and the per-state movement of the AI pilot.
    ///
    /// FUN_1000c4c4 0x1000c4c4, run every update by an AI team's leader (not when the leader is the player): for the
    /// team's current order (none, or types 0x10 / 0x200 / 0x400 / 0x800: nothing to hand out) it walks the order's
    /// bound targets; a target that is not done yet (FUN_1000ad13 0x1000ad13: destroy / protect / 0x1000 orders are
    /// done when the unit or GT has flag 4, recon / goto (8, 0x100) when it has 0x20, nav points never for destroy)
    /// needs "Assignees" members (MTBL record +0x79, 0 = all), less those already on it in the state of the order. The
    /// first member (not the leader) idle, or following and armed (FUN_10064c70 0x10064c70), else the leader if
    /// idle and armed, gets the order's AIT (AI_SelectOrderTable) and the state: destroy (1, 2) and recon (8) -> 2
    /// (Target), protect (4) -> 7 (Patrol), 0x20 / 0x100 / 0x200 / 0x400 -> 8 (GoDirect), leave (0x2000) -> 4 (Flee),
    /// 0x1000 -> 0, with the target as its pilot target (+0x178 / +0x17a). One member per update.
    ///
    /// State handlers, table 0x1008b580: 0 and 11 (0x10009cbd) do not move; 1..10 use 0x10009cd7: the target becomes
    /// mech +0xdc and Target_Validate gives its bearing (+0xd4) and flat distance (+0xc0); the wanted distance is
    /// FUN_1000a18f 0x1000a18f (a nav point's radius, 10000 units for a unit or GT, at least 100). State 2 (Target)
    /// and 5..8 (Follow, Recon, Patrol, GoDirect): FUN_100643e1 obstacle steering, else throttle FUN_1000a20b and
    /// FUN_1000a3b9 bearing steering; state 4 (Flee) the same with the bearing + 180 and full throttle; state 3 (Attack)
    /// runs the maneuvers (0x10009f0e); 1, 9 and 10 set throttle 0.0125. FUN_1000a20b: full (1/64) beyond twice the
    /// wanted distance, 0.0125 down to it, 0 inside (aircraft types 2 / 8: full, and never 0.0125); with |turn +0xc| >
    /// 45, 1/64 - |turn| / 1.5 / 65536 (at least 0.0015625); heat above 65 halves it; below 0.0015625 it is 0.
    /// Follow (5): FUN_1000b2f8 0x1000b2f8 steers to a private nav point (radius 3000 units, FUN_10066940) at the
    /// team's formation slot (Team_FormationSlot); here the slot point from FormationOffset.
    /// State 2 first clears the fire bytes and ends with the fire decision FUN_10005370 on the bearing it steered by;
    /// states 5..8 start with AI_TorsoSweep 0x1000caca (both in Mw2MechAIFire).
    /// </summary>
    public partial class Mw2MechAI
    {
        float _turnControl;             // control +0xc of this pilot, +-1024

        static readonly List<Mw2MechAI> _members = new List<Mw2MechAI>();

        /// <summary>The state an order type hands out (FUN_1000c4c4), -1 for none.</summary>
        static int StateForOrder(uint type)
        {
            if (type == 4) return (int)AiState.Patrol;
            if (type == 1 || type == 2 || type == 8) return (int)AiState.Target;
            if (type == 0x20 || type == 0x100 || type == 0x200 || type == 0x400) return (int)AiState.GoDirect;
            if (type == 0x1000) return (int)AiState.Idle;
            if (type == 0x2000) return (int)AiState.Flee;
            return -1;
        }

        /// <summary>FUN_1000ad13: the order is done with this target.</summary>
        bool TargetDone(AiTarget t, uint type)
        {
            if (t == null) return true;
            bool killType = type == 1 || type == 2 || type == 4 || type == 0x1000;
            if (killType) return t.Kind != AiTarget.TargetKind.Nav && t.Destroyed;
            if (type == 8 || type == 0x100)
            {
                if (t.Kind == AiTarget.TargetKind.Unit) return t.Unit != null && t.Unit.IdentifiedTeams != 0;
                if (t.Kind == AiTarget.TargetKind.Structure) return t.Structure != null && t.Structure.IdentifiedTeams != 0;
                int nav = Director.Navs.FindIndex(n => n.File == t.Nav.File && n.Position == t.Nav.Position);
                return Director.NavVisited(nav);
            }
            return false;
        }

        /// <summary>Whether the member is on the target in the order's state (the counting of FUN_1000c4c4).</summary>
        static bool Engaged(Mw2MechAI m, AiTarget t, uint type)
        {
            if (!SameTarget(m._pilotTarget, t)) return false;
            var s = m.PilotState;
            if (type == 4) return s == AiState.Patrol;
            if (type == 1 || type == 2) return s == AiState.Target || s == AiState.Attack;
            if (type == 8) return s == AiState.Target;
            if (type == 0x20 || type == 0x100 || type == 0x200 || type == 0x400) return s == AiState.GoDirect;
            if (type == 0x1000) return true;
            if (type == 0x2000) return s == AiState.Flee;
            return false;
        }

        static bool SameTarget(AiTarget a, AiTarget b)
        {
            if (a == null || b == null || a.Kind != b.Kind) return false;
            return a.Kind == AiTarget.TargetKind.Unit ? a.Unit == b.Unit
                 : a.Kind == AiTarget.TargetKind.Structure ? a.Structure == b.Structure
                 : a.Nav.File == b.Nav.File && a.Nav.Position == b.Nav.Position;
        }

        /// <summary>FUN_10064c70: 1 when the mech has weapons and none of them can fire.</summary>
        bool Unarmed => _combat != null && _combat.Weapons.Count > 0 && !_combat.HasWeapons;

        void DispatchTeamOrder()
        {
            if (Leader != null || Director == null || _combat == null || _combat.Destroyed) return;
            int index = Director.CurrentOrder(Team);
            var order = Director.Order(Team, index);
            if (order == null || order.Mask == 0 || (order.Mask & 0xe10) != 0) return;
            uint type = order.Mask;
            int state = StateForOrder(type);
            _members.Clear();
            foreach (var c in Mw2MechCombat.All)
                if (c != null && !c.Destroyed && !c.IsPlayer && !c.PoweredDown && c.TryGetComponent<Mw2MechAI>(out var ai) &&
                    ai.Director == Director && ai.Team == Team)
                    _members.Add(ai);
            if (_members.Count == 0) return;
            AiTarget chosen = null;
            foreach (var t in Director.OrderTargets(Team, index))
            {
                if (TargetDone(t, type)) continue;
                int need = order.Assignees != 0 ? order.Assignees : _members.Count;
                foreach (var m in _members) if (Engaged(m, t, type)) need--;
                if (need != 0) { chosen = t; break; }
            }
            if (chosen == null || state < 0) return;
            Mw2MechAI member = null;
            foreach (var m in _members)
                if (m != this && (m.PilotState == AiState.Idle || m.PilotState == AiState.Follow && !m.Unarmed)) { member = m; break; }
            if (member == null)
            {
                if (PilotState != AiState.Idle || Unarmed) return;
                member = this;
            }
            int bit = 0;
            while (bit < 16 && (type & (1u << bit)) == 0) bit++;
            member._orderTable = AiTables.TableFor(bit, member == this);
            // FUN_1000ae8f stores the new assignment in the saved slot while a pilot
            // is temporarily busy, then the AIT Pop rule resumes that assignment.
            if (member._stateStack.Count == 0) member.EnterState((AiState)state, chosen);
            else
            {
                member._stateStack.Clear();
                member._stateStack.Push(((AiState)state, chosen));
            }
        }

        /// <summary>FUN_1000a3b9: +0xc = 819.2 * bearing / 45, +-819.2 beyond +-45 degrees.</summary>
        static float BearingTurn(float bearing) =>
            bearing > 45f ? 819.2f : bearing < -45f ? -819.2f : 819.2f * (bearing / 45f);

        /// <summary>FUN_1000a20b: throttle control (+8) for the flat distance and the wanted distance (model units).</summary>
        float StateThrottle(float flatUnits, float wantedUnits)
        {
            float t = flatUnits > wantedUnits * 2f ? 0.015625f : flatUnits >= wantedUnits ? 0.0125f : 0f;
            bool aircraft = _combat != null && (_combat.GamepieceType == 2 || _combat.GamepieceType == 8);
            if (aircraft) t = 0.015625f;
            if (t != 0f && Mathf.Abs(_turnControl) > 45f)
            {
                t = 0.015625f - Mathf.Abs(_turnControl) / 1.5f / 65536f;
                if (t < 0.0015625f) t = 0.0015625f;
            }
            if (_combat != null && _combat.Heat > 65f) t /= 2f;
            if (t < 0.0015625f) t = 0f;
            if (aircraft && t == 0.0125f) t = 0f;
            return t;
        }

        /// <summary>FUN_1000a18f: wanted distance to a target, model units.</summary>
        static float WantedDistance(AiTarget t)
        {
            float d = t == null ? 100f : t.Kind == AiTarget.TargetKind.Nav ? t.Nav.Radius / PolyModel.UnitsToMeters : 10000f;
            return d < 100f ? 100f : d;
        }

        /// <summary>0x10009cd7 for states 1, 2 and 4..10 (and 0 / 11, which do not move). Returns false for Attack.</summary>
        bool StateMovement()
        {
            var s = PilotState;
            if (s == AiState.Attack) return false;
            if (s == AiState.Idle || s == AiState.Shutdown || s == AiState.Dead)
            {
                // 0x10009cbd; entering state 0 (FUN_1000a5e5) zeroed the throttle.
                _controller.Throttle = 0f;
                _controller.Turn = _turnControl / 1024f;
                Status = $"{s}";
                return true;
            }
            if (s == AiState.Avoid || s == AiState.Rest || s == AiState.Silent)
            {
                _controller.Throttle = 0.0125f * 64f;
                _controller.Turn = _turnControl / 1024f;
                Status = $"{s}";
                return true;
            }
            Vector3 point;
            float wanted;
            if (s == AiState.Follow && Leader != null)
            {
                point = Leader.TransformPoint(new Vector3(FormationOffset.x, 0, FormationOffset.z));
                wanted = 3000f;
            }
            else if (_pilotTarget != null && !_pilotTarget.Destroyed)
            {
                point = _pilotTarget.Position;
                wanted = WantedDistance(_pilotTarget);
            }
            else
            {
                _controller.Throttle = 0f;
                _controller.Turn = _turnControl / 1024f;
                Status = $"{s} (no target)";
                return true;
            }
            if (s == AiState.Follow || s == AiState.Recon || s == AiState.Patrol || s == AiState.GoDirect) TorsoSweep();
            var to = point - transform.position;
            float flat = new Vector2(to.x, to.z).magnitude / PolyModel.UnitsToMeters;
            float bearing = Mathf.Atan2(to.x, to.z) * Mathf.Rad2Deg - transform.eulerAngles.y;
            bearing = Mathf.Repeat(bearing + 180f, 360f) - 180f;
            float throttle;
            if (s == AiState.Flee)
            {
                bearing = Mathf.Repeat(bearing + 360f, 360f) - 180f;     // +180
                throttle = 0.015625f;
            }
            else throttle = StateThrottle(flat, wanted);
            float turn = _turnControl;
            if (!_controller.AiAvoid(bearing, ref turn, ref throttle)) turn = BearingTurn(bearing);
            _turnControl = turn;
            _controller.Throttle = throttle * 64f;
            _controller.Turn = turn / 1024f;
            if (s == AiState.Target) FireDecision(bearing);
            Status = $"{s} {(_pilotTarget != null ? _pilotTarget.Kind.ToString() : "slot")} {flat * PolyModel.UnitsToMeters:0} m";
            return true;
        }
    }
}
