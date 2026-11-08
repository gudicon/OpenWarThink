using MW2.Formats;
using UnityEngine;

namespace MW2
{
    /// <summary>
    /// Sound and computer-voice cues of a unit (mercsw.dll): start-up (0x1003a449 state 0), the player's cockpit tones
    /// (0x10017c5c), the missile-tracking warning (0x1000fc0e), critical-hit voices (0x10036aa9 -> 0x10010b01),
    /// "Enemy ... destroyed" (Mech_Destroy 0x10035ef6) and ammunition jettison (0x1006d162).
    /// </summary>
    public partial class Mw2MechCombat
    {
        // Player mech state for the tones (+0xdd4): 0 starting, 1 powering up, 2 running, 3 shut down, 4 destroyed.
        int _toneState = -1, _lastToneState = -1;
        public bool HostileAtmosphere;
        public bool Ejected { get; private set; }
        /// <summary>DAT_10099fb4 defaults to 1 in mercsw.dll; command 0x53 toggles it.</summary>
        public static bool AutomaticEjection = true;
        bool _acquireToned, _lockToned;             // DAT_100a66d8, DAT_100a66d4
        static int _trackWarnAt;                    // DAT_102675c0

        /// <summary>
        /// States 0 and 1 (+0xdd4): starting and powering up. Mech_InitMovement 0x100380d0 puts every unit in state 0, so
        /// each mission starts powered down; Mech_UpdateControl 0x1003a449 zeroes throttle, turn and torso controls and
        /// skips the weapons until state 2, while the physics still runs (a unit placed high falls and lands meanwhile).
        /// </summary>
        public bool PoweringUp { get; private set; }

        /// <summary>
        /// SELF_DESTRUCT (Input_Command 0x6f -> control +0x44), read by Mech_UpdateControl 0x1003a449 in state 2:
        /// [0x36f] = now + 0x16a, state 7, the player hears "Self-destruct sequence initiated". State 7 is not state 2,
        /// so controls are zeroed and the weapons stop until the destruction.
        /// </summary>
        public void StartSelfDestruct()
        {
            if (Destroyed || _toneState != 2 || ShutDown || PoweredDown) return;
            _heatStamp = _now + 0x16a;
            _toneState = 7;
            SetMobile(false);
            if (IsPlayer && Mw2Audio.Instance != null) Mw2Audio.Instance.Computer(4);
        }

        /// <summary>Mech state +0xdd4 for the HUD (0x100176cb): 0 starting, 1 powering up, 2 running, 3 shut down, 4 destroyed, 7 self-destruct.</summary>
        public int PowerState => Destroyed ? (Ejected ? 5 : 4) : _toneState < 0 ? 0 : _toneState;

        /// <summary>Mech_Eject 0x10037fd6: the player's manual ejection (command 0x52).
        /// PLNT +0x30 decides whether state 5 and MECBEJXX are allowed; Mech_Destroy 0x10035ef6
        /// then reports line 0xc for a successful ejection, or line 0x20 for hostile atmosphere.</summary>
        public void Eject()
        {
            if (Destroyed || !IsPlayer) return;
            var audio = Mw2Audio.Instance;
            if (HostileAtmosphere)
            {
                if (audio != null) audio.Computer(0x20);
            }
            else
            {
                Ejected = true;
                _stateFive = true;
                _toneState = 5;
                if (audio != null)
                {
                    audio.Play(0xd6, transform.position, true);
                    audio.Computer(0xc);
                }
            }
            Die(this);
        }

        /// <summary>Mech_Eject 0x10037fd6 with argument 0 (Flee to Windows 0x1001f908): the player hears line 4
        /// ("Self-destruct sequence initiated"), then Mech_Destroy without ejecting.</summary>
        public void Destruct()
        {
            if (Destroyed) return;
            if (IsPlayer && Mw2Audio.Instance != null) Mw2Audio.Instance.Computer(4);
            Die(this);
        }

        /// <summary>Mech_CriticalHit 0x10036aa9: after a live ammunition bin detonates, the
        /// automatic-ejection option enters state 5 and calls Mech_Destroy. This path has no
        /// MECBEJXX sound, unlike the manual Mech_Eject 0x10037fd6 path.</summary>
        void AutoEject(Mw2MechCombat source)
        {
            if (Destroyed) return;
            Ejected = !HostileAtmosphere;
            _stateFive = true;
            _toneState = Ejected ? 5 : 4;
            if (IsPlayer && Mw2Audio.Instance != null)
                Mw2Audio.Instance.Computer(Ejected ? 0xc : 0x20);
            Die(source);
        }

        void Start() => StartUp();

        /// <summary>
        /// Mech_UpdateControl 0x1003a449, state 0 (mission start or restart): power-up in 0x43e + RandomInt(0x16a) ticks.
        /// An enemy of the player plays NONPSTRT at itself while the player is running; others play MECTURX2 (at half
        /// volume outside the cockpit) and the player hears "Reactor, Sensors, and Weapons on-line".
        /// </summary>
        void StartUp()
        {
            _toneState = 0;
            PoweringUp = true;
            SetMobile(false);
        }

        /// <summary>Mech_UpdateControl 0x1003a449, state 0: schedule the power-up deadline and play the start cue on the next game tick.</summary>
        void BeginPowerUp(int now)
        {
            _heatStamp = MechPowerTiming.Deadline(now, Random.Range(0, MechPowerTiming.RandomWindow));
            _toneState = 1;
            var audio = Mw2Audio.Instance;
            // GP table 0x1008f3e0: types 3 (targets, 0x10058b17) and 7 (0x1001d130 family) have their own update
            // without these start-up sounds; type 3 keeps the same 0x43e + RandomInt(0x16a) power-up timer.
            if (audio == null || GamepieceType == 3 || GamepieceType == 7) return;
            var player = PlayerUnit;
            // 0x1003a449: NONPSTRT only for side 1 (FUN_1002a7c1), so neutrals take the ordinary start cue.
            if (player != null && Side == 1)
            {
                if (player._toneState == 2) audio.Play(0x11b, transform.position, false, 100, Mw2Cockpit.PlayerInCockpit);
                return;
            }
            audio.Play(0x10c, transform.position, false, 100, !Mw2Cockpit.PlayerInCockpit);
            if (IsPlayer) audio.Computer(0x26);
        }

        public static Mw2MechCombat PlayerUnit
        {
            get
            {
                foreach (var c in All) if (c != null && c.IsPlayer) return c;
                return null;
            }
        }

        /// <summary>Runs each update after heat and lock.</summary>
        void UpdateSoundCues(int now)
        {
            // Mech_UpdateControl 0x1003a449, state 7 (self-destruct): once [0x36f] has passed, Mech_Eject(mech, 0) gives
            // the player "Self-destruct sequence initiated" again and Mech_Destroy.
            if (_toneState == 7)
            {
                if (_heatStamp < now)
                {
                    if (IsPlayer && Mw2Audio.Instance != null) Mw2Audio.Instance.Computer(4);
                    Die(this);
                }
                return;
            }
            // 0x1003b7dd writes state 3 even when S is pressed during the initial state-1 wait.
            if (ShutDown || PoweredDown)
            {
                _toneState = 3;
                PoweringUp = false;
            }
            else if (_toneState == 0) BeginPowerUp(now);
            else if (_toneState == 1)
            {
                // 0x1003b561-0x1003b567: the deadline must be strictly earlier than the current tick.
                if (MechPowerTiming.Ready(now, _heatStamp))
                {
                    _toneState = 2;
                    // State 1 -> 2: running (game piece flag 0x2000 set again).
                    PoweringUp = false;
                    if (!ShutDown && !PoweredDown) SetMobile(true);
                }
            }
            if (Destroyed) _toneState = 4;
            else if (!ShutDown && !PoweredDown && _toneState == 3) _toneState = 2;
            if (!IsPlayer) return;
            CockpitTones();
            TrackingWarning(now);
        }

        /// <summary>
        /// 0x10017c5c, in the cockpit view only: lock acquiring (flag 0x40 without 0x80) plays MECBYHLX once at volume
        /// 0x1f; locked (0x80) with the selected weapon ready plays MECXLOCK once at 0x5f; both while running. A new
        /// state plays MECBSYRX (running, volume 0x2f) or MECTRDXX (starting or destroyed).
        /// </summary>
        void CockpitTones()
        {
            var audio = Mw2Audio.Instance;
            if (audio != null && Mw2Cockpit.PlayerInCockpit)
            {
                if (!Locked)
                {
                    _lockToned = false;
                    if (!LockAcquiring) _acquireToned = false;
                    else if (!_acquireToned && _toneState == 2) { _acquireToned = true; audio.Play(0xe0, transform.position, true, 0x1f); }
                }
                else if (!_lockToned && SelectedWeapon >= 0 && SelectedWeapon < Weapons.Count &&
                         Weapons[SelectedWeapon].State == 1 && _toneState == 2)
                {
                    _lockToned = true;
                    audio.Play(0x113, transform.position, true, 0x5f);
                }
                if (_lastToneState != _toneState)
                {
                    if (_toneState == 2) audio.Play(0xdf, transform.position, true, 0x2f);
                    else if (_toneState == 0 || _toneState == 4) audio.Play(0x10b, transform.position, true);
                }
            }
            _lastToneState = _toneState;
        }

        /// <summary>
        /// 0x1000fc0e, every 0x389 ticks while the player is not destroyed: MISLTRCK when a unit targets the player in the
        /// pilot states target (2) or attack (3) within 150000 units (1500 m).
        /// </summary>
        void TrackingWarning(int now)
        {
            if (now < _trackWarnAt || Destroyed) return;
            _trackWarnAt = now + 0x389;
            foreach (var c in All)
            {
                if (c == null || c == this || c.Destroyed || c.Target != this || !c.TryGetComponent<Mw2MechAI>(out var ai)) continue;
                if (ai.PilotState != AiState.Target && ai.PilotState != AiState.Attack) continue;
                if (Vector3.Distance(c.transform.position, transform.position) > 1500f) continue;
                if (Mw2Audio.Instance != null) Mw2Audio.Instance.Play(0x116, transform.position, true);
                return;
            }
        }

        /// <summary>
        /// Critical-hit voices of the player (0x10036aa9 -> 0x10010b01, parts of 0x1008cd78). A weapon (id / 100 below
        /// 0x42): its type's part (LRM 0..3, SRM 4..6, streak SRM 7..9, machine gun 10, gauss 11, LBX 12..15, ultra AC
        /// 16..19, PPC 21, ER large / medium / small laser 22..24, large / medium / small pulse laser 25..27, anti-missile
        /// 29; none for 28, "weapon destroyed" for 20), then "weapon destroyed". Equipment by id / 10 * 10: sensors 7300,
        /// 7350, 7700; arm 7400, 7450; hip 7500, 7550, 7650; leg 7600; gyro 7800; engine 7850; life support 7900;
        /// heat sink 8000 (while sinks are left); jump-jet exhaust port 8500. Ammunition is voiced where it explodes.
        /// </summary>
        void CriticalVoices(int id)
        {
            var audio = Mw2Audio.Instance;
            if (audio == null) return;
            int kind = id / 100;
            if (kind < 0x42)
            {
                int part = kind switch
                {
                    <= 3 => 0x16, <= 6 => 0x17, <= 9 => 0x18, 10 => 0x19, 11 => 0x1a, <= 15 => 0x1b, <= 19 => 0x1c,
                    20 => 0, 21 => 0x1d, 22 => 0x1e, 23 => 0x1f, 24 => 0x20, 25 => 0x21, 26 => 0x22, 27 => 0x23,
                    29 => 3, _ => -1
                };
                if (part >= 0) audio.CriticalVoice(part);
                audio.CriticalVoice(0);
                return;
            }
            if (kind >= 100) return;
            int equipment = (id / 10 * 10) switch
            {
                7300 or 7350 or 7700 => 9,
                7400 or 7450 => 0xc,
                7500 or 7550 or 7650 => 0xb,
                7600 => 0xa,
                7800 => 8,
                7850 => 7,
                7900 => 6,
                8000 when HeatSinks > 0 => 5,
                8500 => 4,
                _ => -1
            };
            if (equipment >= 0) audio.CriticalVoice(equipment);
        }

        /// <summary>
        /// Mech_Destroy 0x10035ef6: a unit on the enemy side of the player (0x1002a7c1 = 1) says by gamepiece type
        /// "Enemy mech destroyed" (1), "Enemy aircraft destroyed" (2, 5, 8), "Enemy target destroyed" (3) or
        /// "Enemy vehicle destroyed" (4).
        /// </summary>
        void DestroyedVoice()
        {
            // 0x10017c5c: the player's own destruction (state 4) plays MECTRDXX in the cockpit.
            if (IsPlayer && !Ejected && Mw2Cockpit.PlayerInCockpit && Mw2Audio.Instance != null)
                Mw2Audio.Instance.Play(0x10b, transform.position, true);
            var player = PlayerUnit;
            if (player == null || player == this || Mw2Audio.Instance == null) return;
            // Mech_Destroy 0x10035ef6: a non-player member of the player's team sends reply 6
            // (pilot voice line 7) using the member's lance position (+0xc).
            if (Side == 0 && TryGetComponent<Mw2MechAI>(out var wingman) && wingman.Team == 0)
            {
                foreach (var member in Mw2MechAI.LanceOf(0))
                    if (member == wingman && member.LancePosition is 1 or 2)
                    {
                        Mw2Audio.Instance.LanceReply(6, member.LancePosition);
                        break;
                    }
            }
            if (Side != 1) return;
            int line = SoundTables.DestroyedComputerLine(GamepieceType);
            if (line >= 0) Mw2Audio.Instance.Computer(line);
        }

        /// <summary>
        /// ; jettison ammunition (0x1006d162): the selected weapon's bin, when the weapon uses ammunition and rounds are
        /// left, is emptied with JETTISON twice (priorities 0x50 and 0x32) and "Jettisoning Ammo".
        /// Approximation: ammunition is kept per weapon here, so only the selected weapon's rounds go.
        /// </summary>
        public void JettisonAmmo()
        {
            int sel = SelectedWeapon;
            if (sel < 0 || sel >= Weapons.Count) return;
            var w = Weapons[sel];
            var pool = Ammunition.Get(w.Pool);
            if (pool == null || w.Def.Burst < 1 || pool.Total < 1) return;
            var audio = Mw2Audio.Instance;
            if (audio != null)
            {
                audio.Play(0xa7, transform.position, true);
                audio.Play(0xa7, transform.position, true);
                audio.Computer(0x22);
            }
            PoolEmpty(w.Pool);
            Ammunition.Jettison(w.Pool);
            SyncAmmo();
        }
    }
}
