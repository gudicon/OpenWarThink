using System.Collections.Generic;
using MW2.Formats;
using UnityEngine;

namespace MW2
{
    /// <summary>
    /// AIT reaction processing from mercsw.dll 0x100086f6, 0x1000854b and 0x1000af67.
    /// Selector resolution is limited to mission objects and units represented in the Unity scene.
    /// </summary>
    public partial class Mw2MechAI
    {
        readonly List<AiRule> _activeRules = new List<AiRule>();
        readonly List<AiTarget> _candidates = new List<AiTarget>();
        readonly Stack<(AiState state, AiTarget target)> _stateStack = new Stack<(AiState, AiTarget)>();
        AiTarget _pilotTarget;
        AiTarget _eventTarget;
        AiTarget _pendingDetection;
        bool _engageAtWill;
        int _orderTable;
        // Pilot +0x17e low bits: FUN_1000bd7e maps MTBL PilotMode 1 -> 2, 2 -> 1.
        // FUN_10009b73 does not run detection while either bit is set.
        int _orderPilotFlags;

        /// <summary>Team order to state and order-specific AIT (0x1000ba52, 0x1000bd9f).</summary>
        void RefreshPilotOrder()
        {
            var order = Director.Order(Team, Director.CurrentOrder(Team));
            uint mask = order?.Mask ?? 0x200u;
            _orderPilotFlags = order?.PilotMode == 1 ? 2 : order?.PilotMode == 2 ? 1 : 0;
            int bit = 0;
            while (bit < 16 && (mask & (1u << bit)) == 0) bit++;
            _orderTable = AiTables.TableFor(bit, Leader == null);
            var initial = mask == 0x400 ? AiState.Silent :
                          mask == 0x800 || mask == 0x10 ? AiState.Shutdown :
                          Leader == null ? AiState.Idle : AiState.Follow;
            _stateStack.Clear();
            AiTarget target = null;
            if (initial == AiState.Follow && Leader != null)
                target = TargetForLeader();
            EnterState(initial, target);
        }

        AiTarget TargetForLeader()
        {
            if (Leader == null) return null;
            if (Leader.TryGetComponent<Mw2MechCombat>(out var combat)) return AiTarget.ForUnit(combat);
            return AiTarget.ForNav(new Mw2MissionDirector.NavInfo { Position = Leader.position });
        }

        bool StateIsListed(AiState state)
        {
            var tables = Director.Rules;
            return (_orderTable > 0 && _orderTable < tables.Length && tables[_orderTable] != null && tables[_orderTable].Lists((int)state)) ||
                   (tables.Length > 1 && tables[1] != null && tables[1].Lists((int)state));
        }

        /// <summary>
        /// Enter a state only when an assigned AIT lists it (0x1000af67, 0x1000aeea).
        /// </summary>
        void EnterState(AiState state, AiTarget target)
        {
            if (state == AiState.Follow && target != null && target.Kind == AiTarget.TargetKind.Unit && target.Unit == _combat)
            { state = AiState.Idle; target = null; } // 0x1000af67: leader cannot follow itself.
            if (!StateIsListed(state)) return;
            // FUN_1000a44f: leaving the attack state ends the maneuver (FUN_10063171) and zeroes the turn control.
            if (PilotState == AiState.Attack) { EndManeuver(); _turnControl = 0f; _controller.Turn = 0f; }
            PilotState = state;
            // AI_StateEntry 0x1000a5e5: Idle and the Silent/Shutdown fallthrough clear
            // throttle and cached flat range immediately, before any later state action.
            if (state == AiState.Idle || state == AiState.Silent || state == AiState.Shutdown)
            { _controller.Throttle = 0f; _flat = 0f; }
            _pilotTarget = target;
            _steer = target;                // AI_EnterState: +0x178 = +0x17a = the state target
            _combat.PoweredDown = state == AiState.Silent || state == AiState.Shutdown || state == AiState.Dead;
            _combat.Trigger = false;
            _combat.Target = null;
            ManeuverId = -1;
            RebuildRules();
        }

        /// <summary>
        /// Visit order AIT before default AIT, preserving rule order and the first event/selector pair
        /// (0x100086f6, slots 2 to 0). Slot 1 has no table for the current mech setup.
        /// </summary>
        void RebuildRules()
        {
            _activeRules.Clear();
            var tables = Director.Rules;
            AddRules(_orderTable > 0 && _orderTable < tables.Length ? tables[_orderTable] : null);
            AddRules(tables.Length > 1 ? tables[1] : null);
        }

        void AddRules(AiTable table)
        {
            if (table == null) return;
            foreach (var state in table.States)
            {
                if (state.State != (int)PilotState) continue;
                foreach (var rule in state.Rules)
                {
                    if (rule.Event == 0) break;
                    bool duplicate = false;
                    foreach (var existing in _activeRules)
                        if (existing.Event == rule.Event && existing.A == rule.A) { duplicate = true; break; }
                    if (!duplicate) _activeRules.Add(rule);
                }
            }
        }

        /// <summary>First firing rule wins (0x1000854b); checks 0..4 use 0x1008b5d8.
        /// A successful check consumes the state-action pass (0x100084fd), even with new state -1.
        /// A nonempty Pop returns zero from 0x10009fe8 and continues at the next rule index.</summary>
        bool UpdatePilotRules()
        {
            if (_combat == null || _combat.Destroyed) return false;
            RetaliateAgainstAttacker(); // FUN_10008a55 runs before the AIT rule loop.
            if (_pendingDetection != null)
            {
                var detected = _pendingDetection;
                _pendingDetection = null;
                ReceiveDetection(detected);
            }
            if (_activeRules.Count == 0) return false;
            for (int ruleIndex = 0; ruleIndex < _activeRules.Count; ruleIndex++)
            {
                var rule = _activeRules[ruleIndex];
                if (!EventFires(rule, out _eventTarget)) continue;
                if (rule.Check == AiRule.TellLeader)
                {
                    ReportToLeader(_eventTarget);
                    continue; // 0x1000b48e returns 0, so 0x1000854b checks the following rule.
                }
                if (rule.Check == AiRule.ClearStack) _stateStack.Clear();
                if (rule.Check == AiRule.Push)
                {
                    _stateStack.Clear();
                    _stateStack.Push((PilotState, _pilotTarget));
                }
                if (rule.Check == AiRule.Pop && _stateStack.Count != 0)
                {
                    var previous = _stateStack.Pop();
                    _stateStack.Clear();
                    EnterState(previous.state, previous.target);
                    // 10008557/1000856a retain the current index across the rebuilt active array.
                    // Do not restart its first rule or stop after restoring the saved state.
                    continue;
                }
                if (rule.NewState >= 0 && rule.NewState <= (int)AiState.Dead && StateIsListed((AiState)rule.NewState))
                {
                    // FUN_1000854b calls FUN_1000af67 with its last argument zero.
                    // That clears +0x16c before changing state, including after check 2.
                    _stateStack.Clear();
                    EnterState((AiState)rule.NewState, ResolveNewTarget(rule.Target, _eventTarget));
                }
                return true;
            }
            return false;
        }

        void RetaliateAgainstAttacker()
        {
            if ((_orderPilotFlags & 1) != 0 || !StateIsListed(AiState.Target)) return;
            foreach (var unit in Mw2MechCombat.All)
            {
                if (unit == null || unit.Destroyed || unit.Side == _combat.Side ||
                    !unit.TryGetComponent<Mw2MechAI>(out var attacker) ||
                    attacker.PilotState != AiState.Attack ||
                    attacker._pilotTarget == null || attacker._pilotTarget.Kind != AiTarget.TargetKind.Unit ||
                    attacker._pilotTarget.Unit != _combat) continue;
                if ((PilotState == AiState.Target || PilotState == AiState.Attack) &&
                    _pilotTarget != null && _pilotTarget.Kind == AiTarget.TargetKind.Unit && _pilotTarget.Unit == unit)
                    return;
                _stateStack.Clear();
                _stateStack.Push((PilotState, _pilotTarget));
                EnterState(AiState.Target, AiTarget.ForUnit(unit));
                return;
            }
        }

        void ReportToLeader(AiTarget target)
        {
            if (target == null || target.Kind != AiTarget.TargetKind.Unit || target.Destroyed) return;
            if (Leader != null && Leader.TryGetComponent<Mw2MechAI>(out var leaderAi))
            {
                if (leaderAi._pendingDetection == null) leaderAi._pendingDetection = target;
            }
            else if (_pendingDetection == null) _pendingDetection = target;
        }

        /// <summary>AIT destroyed-target event 0x10009c69: a pilot led by the player reports completion with
        /// Radio_LanceReply 0x10010985 code 5 (pilot voice line 6) at its lance position +0xc.</summary>
        void ReportDestroyedTarget()
        {
            if (Team != 0 || _combat == null || _combat.IsPlayer || Mw2Audio.Instance == null) return;
            foreach (var member in LanceOf(0))
                if (member == this && member.LancePosition is 1 or 2)
                {
                    Mw2Audio.Instance.LanceReply(5, member.LancePosition);
                    break;
                }
        }

        // FUN_1000c0c2 distributes a reported enemy to a willing team member before trying the leader.
        // +0x17c is the load-time u16 combat value from FUN_1003107e, not tonnage or current health.
        void ReceiveDetection(AiTarget target)
        {
            if (target == null || target.Kind != AiTarget.TargetKind.Unit || target.Destroyed ||
                Director == null || Director.CurrentOrder(Team) < 0) return;
            var members = LanceOf(Team);
            if (Director.CurrentOrder(Team) == 4)
            {
                // 1000c1bd..1000c262: the count is tested but never decremented.
                // Its zero default becomes the nonzero team count, so every eligible pilot,
                // including the leader, receives this detection regardless of PilotMode.
                foreach (var member in members)
                {
                    if (member.Director != Director || !member.CanAcceptDetection(false)) continue;
                    member.AcceptDetection(target);
                }
                return;
            }
            Mw2MechAI chosen = null;
            int difference = 0x7fff;
            foreach (var member in members)
            {
                if (member.Director != Director || member.Leader == null || !member.CanAcceptDetection(true)) continue;
                int score = Mathf.Abs(member._combat.PilotCombatValue - target.Unit.PilotCombatValue);
                if (score < difference) { chosen = member; difference = score; }
            }
            if (chosen == null)
                foreach (var member in members)
                    if (member.Director == Director && member.Leader == null && member.CanAcceptDetection(true))
                    { chosen = member; break; }
            chosen?.AcceptDetection(target);
        }

        bool CanAcceptDetection(bool checkPilotMode) =>
            _combat != null && !_combat.Destroyed && !_combat.IsPlayer && !Unarmed &&
            PilotState != AiState.Target && PilotState != AiState.Attack && _engageAtWill &&
            (!checkPilotMode || (_orderPilotFlags & 3) == 0) && StateIsListed(AiState.Target);

        void AcceptDetection(AiTarget target)
        {
            _stateStack.Clear();
            _stateStack.Push((PilotState, _pilotTarget));
            EnterState(AiState.Target, target);
        }

        AiTarget ResolveNewTarget(int selector, AiTarget eventTarget)
        {
            if (selector == 0x25) return eventTarget;
            if (selector == 0x2a) return _steer;
            if (selector == 0x40) return _pilotTarget;
            if (selector == 0x2201) return TargetForLeader();
            if (selector == 0) return null;
            FillCandidates(selector, _candidates);
            return _candidates.Count > 0 ? _candidates[0] : eventTarget;
        }

        bool EventFires(AiRule rule, out AiTarget candidate)
        {
            candidate = null;
            switch (rule.Event)
            {
                case AiRule.Always: return true;                         // 0x10009511
                case AiRule.Never: return false;                        // 0x10009528
                case AiRule.Destroyed:                                  // 0x10009c69
                    FillCandidates(rule.A, _candidates);
                    foreach (var t in _candidates) if (t.Destroyed) { candidate = t; ReportDestroyedTarget(); return true; }
                    return false;
                case AiRule.Detect:                                     // 0x10009b73
                    if (_orderPilotFlags != 0) return false;
                    FillCandidates(rule.A, _candidates);
                    return PickNearest(_candidates, ResolveDistance(rule.B), out candidate);
                case AiRule.Nearer:                                     // 0x1000964c
                    FillCandidates(rule.A, _candidates);
                    return PickNearest(_candidates, ResolveDistance(rule.B), out candidate);
                case AiRule.Arrived:                                    // 0x10009546
                    FillCandidates(rule.A, _candidates);
                    if (rule.B != 0) return PickNearest(_candidates, ResolveDistance(rule.B), out candidate);
                    foreach (var t in _candidates)
                    {
                        float radius = t.Kind == AiTarget.TargetKind.Nav ? Mathf.Max(1f, t.Nav.Radius) : 100f;
                        if (!t.Destroyed && FlatDistance(t) < radius)
                        { candidate = t; return true; }
                    }
                    return false;
                case AiRule.Farther:                                    // 0x10009720
                    FillCandidates(rule.A, _candidates);
                    float threshold = rule.B == -3 ? 0f : ResolveDistance(rule.B);
                    foreach (var t in _candidates)
                    {
                        if (t.Destroyed) continue;
                        float distance = FlatDistance(t);
                        if (distance > threshold) { candidate = t; threshold = distance; }
                    }
                    return candidate != null;
                default: return false;
            }
        }

        float ResolveDistance(int value)
        {
            // 0x10009b73 and 0x10009720: GPS ranges are multiplied by 100 model units (1 metre).
            return value switch
            {
                -4 => NearRange,
                -3 => float.MaxValue,
                -2 => DetectRange,
                -1 => FarRange,
                0 => 1f, // 0x1000a18f returns 100 model units
                _ => value
            };
        }

        bool PickNearest(List<AiTarget> choices, float threshold, out AiTarget nearest)
        {
            nearest = null;
            float distance = threshold;
            foreach (var t in choices)
            {
                if (t.Destroyed) continue;
                float d = FlatDistance(t);
                if (d < distance) { nearest = t; distance = d; }
            }
            return nearest != null;
        }

        float FlatDistance(AiTarget target)
        {
            var delta = target.Position - transform.position;
            return new Vector2(delta.x, delta.z).magnitude;
        }

        // FUN_100427f3: selector 0x2100 uses the first 0x20 team order's
        // target, or the first order's target when there is no 0x20 order.
        AiTarget TeamNavTarget()
        {
            if (Director == null) return null;
            var team = Director.Teams.Find(t => t.Team == Team);
            if (team == null || team.Orders.Count == 0) return null;
            int index = team.Orders.FindIndex(o => o.Mask == 0x20);
            var targets = Director.OrderTargets(Team, index >= 0 ? index : 0);
            return targets.Count > 0 ? targets[0] : null;
        }

        void FillCandidates(int selector, List<AiTarget> result)
        {
            result.Clear();
            // 0x2101 reads pilot +0x1a2, the pilot's nav target. AI pilots keep
            // that slot at the invalid 0x1000 sentinel; their private maneuver
            // navs are steering targets (+0x178), not candidates for this selector.
            if (selector == 0x2101) return;
            if (selector == 0x40)
            {
                if (_pilotTarget != null) result.Add(_pilotTarget);
            }
            else if (selector == 0x2a)
            {
                if (_steer != null) result.Add(_steer);
            }
            else if (selector == 0x2201)
            {
                var leader = TargetForLeader();
                if (leader != null) result.Add(leader);
            }
            else if (selector == 0x2100)
            {
                var target = TeamNavTarget();
                if (target != null) result.Add(target);
            }
            else if (selector == 0x4200 || selector == 0x4201)
            {
                foreach (var unit in Mw2MechCombat.All)
                    if (unit != null && !unit.Destroyed && unit != _combat &&
                        (selector == 0x4201 ? unit.Side != _combat.Side && unit.Side != 2 : unit.Side == 0))
                        result.Add(AiTarget.ForUnit(unit));
            }
        }
    }
}
