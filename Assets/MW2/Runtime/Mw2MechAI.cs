using System.Collections.Generic;
using MW2.Formats;
using UnityEngine;

namespace MW2
{
    /// <summary>
    /// Runs AIT pilot reactions (0x1000854b) and mech maneuver selection (0x10062aa8) through the ported
    /// Mw2MechController. Formation slots come from 0x1002a57c. Selector resolution, report delivery and
    /// throttle/turn mapping remain approximations where the original scene/control structures are absent.
    /// </summary>
    [DefaultExecutionOrder(-10)]
    [RequireComponent(typeof(Mw2MechController))]
    public partial class Mw2MechAI : MonoBehaviour
    {
        public Mw2MissionDirector Director;
        public int Team;
        public Transform Leader;                // null for the team leader
        [Tooltip("Formation slot, MW2 local metres (x right, z forward), relative to the leader.")]
        public Vector3 FormationOffset;
        [Tooltip("Distance to keep from an attack target, metres.")]
        public float EngageDistance = 150f;
        [Tooltip("Pilot style 0..7 from GPS parameter 6 (0x10007f35, 0x100081c1).")]
        [Range(0, 7)] public int PilotStyleId;
        [Tooltip("Pilot voice table index 0..32 from GPS body +0x16 (0x10007f35 -> 0x10010b36).")]
        public int PilotVoiceId;
        public int NearRange = 250;
        public int FarRange = 250;
        public int DetectRange = 250;

        [Header("Read-only")]
        public string Status;
        public AiState PilotState;
        public int ManeuverId = -1;

        Mw2MechController _controller;
        Mw2MechCombat _combat;

        /// <summary>Refresh the team's pilot controls after its active MTBL order changes (0x1000ba3f).</summary>
        public static void TeamOrderChanged(Mw2MissionDirector director, int team)
        {
            foreach (var combat in Mw2MechCombat.All)
                if (combat != null && combat.TryGetComponent<Mw2MechAI>(out var ai) &&
                    ai.Director == director && ai.Team == team)
                    ai.RefreshOrder();
        }

        /// <summary>
        /// Position in the team (gamepiece +0xc; FUN_1000b4e1 0x1000b4e1 finds a member by it): the player is 0 in the
        /// player's team (team 0), other members follow in spawn order. Approximation: the original numbers come from
        /// the campaign roster, which the mission files do not hold.
        /// </summary>
        public int LancePosition { get; private set; }

        public AiTarget PilotTarget => _pilotTarget;

        /// <summary>Team members with AI in spawn order, with their lance positions set.</summary>
        public static List<Mw2MechAI> LanceOf(int team)
        {
            var list = new List<Mw2MechAI>();
            foreach (var combat in Mw2MechCombat.All)
                if (combat != null && !combat.IsPlayer && combat.TryGetComponent<Mw2MechAI>(out var ai) && ai.Team == team) list.Add(ai);
            list.Sort((a, b) => a.transform.GetSiblingIndex().CompareTo(b.transform.GetSiblingIndex()));
            for (int i = 0; i < list.Count; i++) list[i].LancePosition = team == 0 ? i + 1 : i;
            return list;
        }

        /// <summary>
        /// Lance_SendCommand 0x1000b7aa -> FUN_1000b57f 0x1000b57f: state 2 targets, 3 toggles autonomous
        /// engagement (+0x182) without changing state, 5 follows selector 0x2201, 7 defends the chosen target,
        /// 8 goes to selector 0x2100 and 11 shuts down. A state must exist in the pilot's AIT.
        /// </summary>
        public bool LanceCommand(int command, AiTarget target)
        {
            if (_combat == null || _combat.Destroyed || _combat.IsPlayer || Director == null || PilotState == AiState.Dead)
                return false;
            if (command == 3)
            {
                if (!StateIsListed(AiState.Attack)) return false;
                _engageAtWill = !_engageAtWill;
                // FUN_1000b57f clears the two PilotMode bits when free engagement is toggled.
                _orderPilotFlags &= ~3;
                return true;
            }
            AiState state;
            switch (command)
            {
                case 2: state = AiState.Target; break;
                case 5:
                    if (Leader == null && Director != null && Director.Player != null) Leader = Director.Player;
                    state = AiState.Follow;
                    target = TargetForLeader();
                    break;
                case 7: state = AiState.Patrol; break;
                case 8: state = AiState.GoDirect; target = ResolveNewTarget(0x2100, null); break;
                case 11: state = AiState.Shutdown; target = null; break;
                default: return false;
            }
            if (!StateIsListed(state)) return false;
            _stateStack.Clear();
            if (command != 7) _stateStack.Push((PilotState, _pilotTarget));
            EnterState(state, target);
            // FUN_1000b57f clears +0x17e, then sets 0x11 for Target, 0x13 for Follow,
            // or 0x12 for the other commands. Detection reads only its low two bits.
            _orderPilotFlags = command == 2 ? 1 : command == 5 ? 3 : 2;
            if (command == 5) _engageAtWill = false;
            return true;
        }

        void RefreshOrder()
        {
            if (_combat == null) return;
            RefreshPilotOrder();
            Drive(0, 0);
        }

        void Start()
        {
            _controller = GetComponent<Mw2MechController>();
            _combat = GetComponent<Mw2MechCombat>();
            // FUN_10007f35: autonomous attack (+0x182) starts enabled outside the player's team.
            _engageAtWill = Team != 0;
            _controller.KeyboardControl = false;
            _controller.FollowCamera = false;
            // FUN_1006280a: a mech pilot of skill 6 (flag bit 3 clear) gets twist limit +0xe14 = 0.
            if (_combat != null && _combat.GamepieceType == 1 && PilotSkill >= 6 && TryGetComponent<Mw2TorsoTwist>(out var twist))
                twist.Limit = 0f;
            if (Leader == null && Director != null) Director.RegisterLeader(Team, transform);
            if (Director != null && Director.CurrentOrder(Team) >= 0) RefreshOrder();
        }

        void Update()
        {
            if (Director == null || PilotState == AiState.Dead) return; // 10008451 skips dead pilots before order/rule dispatch.
            TickFireClock();
            // 10008479 dispatches the leader's team order before evaluating its AIT rules.
            if (Leader == null) DispatchTeamOrder();
            bool ruleConsumedUpdate = UpdatePilotRules();
            if (_combat != null)
            {
                _controller.PowerEnabled = !_combat.PoweredDown && !_combat.ShutDown && !_combat.PoweringUp;
                if (!_controller.PowerEnabled) { _combat.Trigger = false; Drive(0, 0); Status = "shutdown"; Look(_pilotTarget); return; }
            }
            UpdateWeapons();
            // 100084fd..10008524: accepted rules skip the normal state handler this pass.
            // Preserve the controls left by the transition rather than running movement/attack immediately.
            if (ruleConsumedUpdate) Status = $"{PilotState} (AIT rule)";
            else UpdatePilotStateAction();
            // 10008524..10008538 refreshes the pilot target after either path, including consumed rules.
            Look(_pilotTarget);
        }

        /// <summary>State action table 0x1008b580, reached only when 0x1000854b returns zero.</summary>
        void UpdatePilotStateAction()
        {
            if (StateMovement()) return;                    // 0x10009cd7; Attack continues below with the maneuvers
            if (PilotState == AiState.Attack && _pilotTarget != null && !_pilotTarget.Destroyed)
            {
                UpdateAttack();                             // FUN_10009f0e -> AI_UpdateMechManeuver 0x10062270
                Status = $"Attack {_pilotTarget.Kind} maneuver {ManeuverId}";
                return;
            }
            // The attack state without a valid target holds still until the pilot rules pick another state.
            Drive(0, 0);
            Status = "Attack (no target)";
        }

        /// <summary>
        /// The pilot target (+0x17a) is the combat target in states 2 and 3 (Target_Validate through FUN_1000a168), so the
        /// lock (0x1006d794) and the shots go to it; firing itself is FUN_10005370 (Mw2MechAIFire), called by state 2 and
        /// by the attack maneuvers. Other states hold no target and fire nothing.
        /// </summary>
        void UpdateWeapons()
        {
            if (_combat == null || _combat.Destroyed) return;
            _combat.Trigger = false;
            bool engaged = (PilotState == AiState.Target || PilotState == AiState.Attack) && _pilotTarget != null && !_pilotTarget.Destroyed;
            _combat.Target = engaged && _pilotTarget.Kind == AiTarget.TargetKind.Unit ? _pilotTarget.Unit : null;
            _combat.TargetStructure = engaged && _pilotTarget.Kind == AiTarget.TargetKind.Structure ? _pilotTarget.Structure : null;
        }

        void Drive(float throttle, float turn)
        {
            _controller.Throttle = throttle;
            _controller.Turn = turn;
            _controller.Reverse = false;
        }

        static Vector3 Flat(Vector3 v) => new Vector3(v.x, 0, v.z);
    }
}
