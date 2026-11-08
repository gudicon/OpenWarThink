using MW2.Formats;
using UnityEngine;

namespace MW2
{
    /// <summary>
    /// Player control of an assembled mech: runs the ported movement (MechMotion) and the ANIM player on one
    /// 181 Hz game clock. The player's keys are those of the game folder's inputw.map / gamekey.map, read through the
    /// original input layer (Mw2Input); see ReadKeyboard for the control block channels used here.
    /// </summary>
    [RequireComponent(typeof(Mw2MechAnimator))]
    public partial class Mw2MechController : MonoBehaviour
    {
        [Tooltip("Walking MP from the MEK resource (MEK[1]); max speed = MP * 10.74 km/h.")]
        public int WalkingMp = 3;
        [Tooltip("Jump MP from MEK[2]; zero means no jump jets.")]
        public int JumpMp;
        public bool KeyboardControl = true;
        [System.NonSerialized] public bool PowerEnabled = true;
        [Tooltip("Keep the main camera at its starting offset from the mech.")]
        public bool FollowCamera = true;

        [Header("Collision (mercsw.dll 0x10034470)")]
        [Tooltip("Collision sphere radius in metres: MGEO[6], mech +0xe1c; bounds fallback only for models without MGEO.")]
        public float CollisionRadius = 5f;
        [Tooltip("Height of the movement ray above the feet, metres.")]
        public float ProbeHeight = 3f;
        public bool Collisions = true;

        [Range(0, 1)] public float Throttle;
        [Range(-1, 1)] public float Turn;
        public bool Reverse;
        public bool Jump;
        public bool JumpForward, JumpBackward, JumpLeft, JumpRight;
        [Tooltip("MASC equipment (MEK section slot 7000..7049).")]
        public bool HasMasc;
        public bool MascActive;
        [Tooltip("Mission gravity from PLNT, units/tick squared.")]
        public float PlanetGravity = 0.0296f;
        int _lastMascCheck;

        [Header("Read-only")]
        public float SpeedKph;
        public float HeadingDeg;
        public int AnimState;
        public int AnimGait;
        public int JumpFuel;
        /// <summary>Hud_DrawAltitude checks mech +0xdf4 >=0; zero means empty,
        /// negative means absent/destroyed jets (Mech_LoadMek / Mech_CriticalHit).</summary>
        public int HudJetFuel => JumpDisabled || JumpMp <= 0 ? -2 : JumpFuel;
        public bool Airborne;
        public float VerticalSpeedUnits;
        /// <summary>AI_LeadPoint 0x10005db4 reads the current physics velocity
        /// at mech +0xe28..0xe30, in model units per tick, including the first observation.</summary>
        public Vector3 PhysicsVelocityUnits => _motion != null ? new Vector3(_motion.VelX, _motion.VelY, _motion.VelZ) : Vector3.zero;
        /// <summary>Speed for the HUD, 0x100696cb (see MechMotion.HudSpeedKph).</summary>
        public int HudSpeedKph;
        /// <summary>Hud_UpdatePlayer 0x10017a53: trunc of the throttle block's current float at mech +0x4c.</summary>
        public int PowerHumPitch => _motion != null ? (int)_motion.Throttle.Current : 0;
        /// <summary>Absolute foot height in model units: GP+0x54 minus MGEO[0]
        /// at mech+0xe00 (Hud_DrawAltitude 0x10069cea), not GP ground height +0x74.</summary>
        public float AltitudeUnits;
        /// <summary>Jump jets lost to a critical hit (item 7800 sets [0x37d] = -2).</summary>
        [System.NonSerialized] public bool JumpDisabled;

        /// <summary>Mech_CriticalHit 0x10036aa9, item 8500: one jump jet fewer ([0x37e] - 1, the thrust in proportion).</summary>
        /// <summary>FUN_10021376 writes gp +0x50..+0x58 and +0x60 of another player's mech: the simulation goes on from there.</summary>
        public Vector3 ImpactVelocity => _preCollisionVelocity;

        public void NetSetPose(int x, int y, int z, float heading)
        {
            if (_motion == null)
            {
                transform.position = new Vector3(x, y, z) * PolyModel.UnitsToMeters;
                transform.rotation = Quaternion.Euler(0, heading, 0);
                return;
            }
            _motion.X = x; _motion.Y = y; _motion.Z = z;
            _motion.Heading = heading;
            transform.position = new Vector3(x, y, z) * PolyModel.UnitsToMeters;
            transform.rotation = Quaternion.Euler(0, heading, 0);
        }

        public void JumpJetHit()
        {
            if (JumpMp > 0) JumpMp--;
            if (_motion != null) _motion.JumpMp = JumpMp;
        }

        /// <summary>FUN_10051da0 -> Mech_LoadMek1002f9e0 / Mech_InitMovement100380d0: restore jets and clear old physical/fall state.</summary>
        internal void NetResetMovement(int jumpMp)
        {
            JumpMp = jumpMp; JumpDisabled = false; MascActive = false;
            Throttle = Turn = 0; Reverse = false;
            Jump = JumpForward = JumpBackward = JumpLeft = JumpRight = false;
            _fall.Falling = _fall.Down = false; _fall.Angle = _fall.Rate = _fall.Pitch = 0;
            _flightPitch = 0;
            var position = transform.position;
            float heading = transform.eulerAngles.y;
            _motion = new MechMotion(WalkingMp, (int)_clock, JumpMp) { Gravity = PlanetGravity,
                X = position.x / PolyModel.UnitsToMeters, Y = position.y / PolyModel.UnitsToMeters,
                Z = position.z / PolyModel.UnitsToMeters, Heading = heading };
            JumpFuel = _motion.JumpFuel;
            transform.rotation = Quaternion.Euler(0, heading, 0);
        }

        Mw2MechAnimator _animator;
        MechMotion _motion;
        readonly MechFall _fall = new MechFall();
        float _groundUnits;
        /// <summary>Ground height under the unit in model units (gamepiece +0x74, World_GroundHeight).</summary>
        public float GroundUnits => _groundUnits;
        double _clock;
        int _now;
        Transform _camera;
        Vector3 _cameraOffset;
        AudioSource _jetSound;
        int _lastGaitSound = -2;            // DAT_102500a0 per unit: last current state voiced, -2 after arrival
        Vector3 _wallNormal;            // DAT_100a66fc
        Mw2MechCombat _wallUnit;
        Mw2Destructible _wallStructure;
        bool _wallHit, _wallHitUnit, _bumped;   // this tick's collision; DAT_100ad340 (one bump per contact)
        Mw2MechCombat _contactUnit; // GP +0x7c, reset before this unit's collision pass.
        int _contactFrame = -1;
        Vector3 _preCollisionVelocity; // source +0xe34..0xe3c, including fractional components.
#if ENABLE_LEGACY_INPUT_MANAGER
        string _cheatKeys = "";
        bool _cheatJets, _cheatInfiniteFuel;
        int _jumpMpBeforeCheat;
#endif

        void Start()
        {
            if (FollowCamera && Camera.main != null)
            {
                _camera = Camera.main.transform;
                _cameraOffset = _camera.position - transform.position;
            }
            _animator = GetComponent<Mw2MechAnimator>();
            _animator.ExternalClock = true;
            _motion = new MechMotion(WalkingMp, 0, JumpMp);
            _motion.Gravity = PlanetGravity;
            var p = transform.position;
            _motion.X = p.x / PolyModel.UnitsToMeters;
            _motion.Y = p.y / PolyModel.UnitsToMeters;
            _motion.Z = p.z / PolyModel.UnitsToMeters;
            _motion.Heading = transform.eulerAngles.y;
        }

        void Update()
        {
            if (KeyboardControl && TryGetComponent<Mw2MechCombat>(out var playerCombat) && playerCombat.IsPlayer)
                ReadCheats();
            if (KeyboardControl && PowerEnabled) ReadKeyboard();
            if (!PowerEnabled && KeyboardControl && Mw2Input.Loaded)
            {
                // 0x1003a449 outside state 2: throttle +8 = 0 with throttle_set (+0x24), reverse +0x30 cleared.
                Mw2Input.SetFloat(0x08, 0f);
                Mw2Input.Set(0x24);
                Mw2Input.Clear(0x30);
            }
            if (!PowerEnabled)
            {
                Throttle = 0;
                Turn = 0;
                Jump = JumpForward = JumpBackward = JumpLeft = JumpRight = false;
            }

            _clock += Time.deltaTime * MechMotion.TicksPerSecond;
            int now = (int)_clock;
            int dt = now - _now;
            if (dt <= 0) return;
            _now = now;

            _motion.FeetToTorso = _feetToTorso;
            _motion.ManualControl = KeyboardControl;
            if (!_feetToTorso) _motion.TurnTargetOverride = null;
            _motion.ThrottleInput = Throttle;
            _motion.TurnInput = Turn;
            _motion.Reverse = Reverse;
            _motion.JumpInput = Jump;
            _motion.JumpForward = JumpForward;
            _motion.JumpBackward = JumpBackward;
            _motion.JumpLeft = JumpLeft;
            _motion.JumpRight = JumpRight;
            _motion.MascActive = MascActive && HasMasc && PowerEnabled;
            if (TryGetComponent<Mw2MechCombat>(out var damaged)) _motion.ThrottleScale = damaged.SpeedFactor;   // [0x379]
            if (JumpDisabled) _motion.JumpInput = false;
            float oldX = _motion.X, oldY = _motion.Y, oldZ = _motion.Z;
            bool wasAirborne = _motion.Airborne;
            int previousFuel = _motion.JumpFuel;
            _motion.Step(now, dt, _animator.CurrentState, _animator.FootContact);
            _preCollisionVelocity = new Vector3(_motion.VelX, _motion.VelY, _motion.VelZ);
            _contactUnit = null;
            _contactFrame = Time.frameCount;
            // Mech_UpdatePhysics 0x10039296/0x100393b4 pushes saved float bits at
            // +0xe34/+0xe38/+0xe3c. FUN_100461fe uses its approximate component norm;
            // MOV/PUSH dword does not convert these values to integers.
            float impactSpeed = (float)MechSphereContact.Length(_preCollisionVelocity.x, _preCollisionVelocity.y, _preCollisionVelocity.z);
            if (_fall.Frozen)
            {
                // 0x1003880d: with +0xe42 bits 4 / 8 Mech_Collide is skipped and the old position is written back.
                _motion.X = oldX;
                _motion.Y = oldY;
                _motion.Z = oldZ;
            }
            float landingSpeed = _motion.VelY;
            bool jetsFiring = JumpMp > 0 && _motion.JumpInput && previousFuel > 0;
            var audio = Mw2Audio.Instance;
            if (jetsFiring && audio != null)
            {
                if (_jetSound == null) _jetSound = audio.StartLoop(0xf3, transform); // 0x10032adc: MECJJRXX
                _jetSound.volume = Mw2Cockpit.PlayerInCockpit ? 0.5f : 1f;           // FUN_10007081 half flag
                // The original warns at the first update crossing 0x1c4 fuel ticks, for the player's mech only.
                if (KeyboardControl && previousFuel > 0x1c4 && _motion.JumpFuel < 0x1c4)
                    audio.Play(0xf2, transform.position, true); // 0x10032adc: MECJFUEL
            }
            else if (_jetSound != null)
            {
                _jetSound.Stop();
                Destroy(_jetSound);
                _jetSound = null;
            }
            // Mech_UpdateControl 0x1003a449: only the player's active MASC checks once after >181 ticks
            // with foot-contact flag 2. RandomInt(60) == 12 adds half the current heat and powers MASC off.
            if (KeyboardControl && _motion.MascActive && _animator.FootContact &&
                !(JumpMp > 0 && _motion.JumpInput) && now - _lastMascCheck > 181)
            {
                _lastMascCheck = now;
                if (Random.Range(0, 60) == 12 && TryGetComponent<Mw2MechCombat>(out var mascCombat) && !mascCombat.Destroyed)
                {
                    mascCombat.AddHeat(mascCombat.Heat * 0.5f);
                    MascActive = false;
                    if (Mw2Audio.Instance != null) Mw2Audio.Instance.Play(0xda, transform.position, true);
                    if (TryGetComponent<Mw2Cockpit>(out var cockpit)) cockpit.ShowMessage("MASC malfunction.");
                    else Debug.LogWarning("MASC malfunction.", this);
                }
            }
            float landingDamage = _fall.Frozen ? 0f : Collisions ? Collide(oldX, oldY, oldZ) : _motion.LandAt(0);
            GamepieceAltitude(oldY);
            Mw2MechCombat landedCombat = null;
            bool isDead = TryGetComponent(out landedCombat) && landedCombat.Destroyed;
            bool deadLanding = wasAirborne && !_motion.Airborne && landingSpeed < -5.384613f && isDead;
            bool deadCollision = Collisions && !_fall.Frozen && _wallHit && isDead;
            // 0x1003880d -> 0x10032c4e: a descent below -5.384613 units/tick plays the touchdown, MECMTNSF (0xfb) down to
            // -16.153839 and MECMTNHD (0xfa) below it, at half volume in the cockpit.
            if (wasAirborne && !_motion.Airborne && landingSpeed < -5.384613f && !deadLanding && audio != null)
                audio.Play(landingSpeed >= -16.153839f ? 0xfb : 0xfa, transform.position, false, 100, Mw2Cockpit.PlayerInCockpit);
            if (KeyboardControl) WallBump(audio, impactSpeed);
            if (_wallHit && TryGetComponent<Mw2MechCombat>(out var impacted)) ApplyImpact(impacted);
            if (TryGetComponent<Mw2MechCombat>(out var combat))
            {
                combat.AddJumpHeat(_motion.GeneratedJumpHeat);
                if (landingDamage > 0) combat.ApplyLandingDamage(landingDamage);
            }

            bool updateGait = !isDead || !landedCombat.DeathControlFinished;
            if (updateGait)
            {
                _animator.TargetState = _motion.AnimTarget;
                _animator.Gait = _motion.AnimGait;
            }
            _animator.Tick(now);
            if (updateGait) GaitSounds(audio);

            // MW2 -> Unity: the same axes (PolyMeshBuilder.ToUnity); the heading is the yaw about Y.
            transform.position = new Vector3(_motion.X, _motion.Y, _motion.Z) * PolyModel.UnitsToMeters;
            if (deadLanding || deadCollision) landedCombat.BlowApartOnImpact();
            if (_fall.Falling)
            {
                bool grounded = MechFall.Grounded(!_motion.Airborne, _motion.Y - _groundUnits);
                _fall.Step(dt, grounded, grounded && ThingTouchesGround());
            }
            // The fall pitch +0x5c turns the body about the feet (the transform's origin, the foot point of 0x10039cf5);
            // GP type 8 tilts with its climb (FUN_100090a7).
            transform.rotation = Quaternion.Euler(_fall.Pitch + _flightPitch, _motion.Heading, 0);
            // 1003a1c8: player reticle/convergence runs in physics, before a later fire hook.
            if (TryGetComponent<Mw2MechCombat>(out var aimingCombat)) aimingCombat.UpdatePlayerConvergence(now);

            SpeedKph = _motion.SpeedKph;
            HeadingDeg = _motion.Heading;
            AnimState = _animator.CurrentState;
            AnimGait = _motion.AnimGait;
            JumpFuel = _motion.JumpFuel;
            Airborne = _motion.Airborne;
            VerticalSpeedUnits = _motion.VelY;
            HudSpeedKph = _motion.HudSpeedKph;
            AltitudeUnits = _motion.Y;
        }

        /// <summary>Titanium Edition mission codes: hold Ctrl+Alt+Shift and type IN or CR.</summary>
        void ReadCheats()
        {
#if ENABLE_LEGACY_INPUT_MANAGER
            bool modifiers = (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)) &&
                             (Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt)) &&
                             (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift));
            if (!modifiers || Mw2Input.WindowOpen || Mw2Input.MenuMode || _motion == null)
            {
                _cheatKeys = "";
                return;
            }
            for (int i = 0; i < 26; i++)
            {
                if (!Input.GetKeyDown(KeyCode.A + i)) continue;
                _cheatKeys = (_cheatKeys + (char)('A' + i));
                if (_cheatKeys.Length > 2) _cheatKeys = _cheatKeys.Substring(_cheatKeys.Length - 2);
                if (_cheatKeys == "IN")
                {
                    _cheatJets = !_cheatJets;
                    if (_cheatJets) _jumpMpBeforeCheat = JumpMp;
                    JumpMp = _cheatJets ? Mathf.Max(6, JumpMp) : _jumpMpBeforeCheat;
                    _motion.SetJumpMp(JumpMp);
                    ShowCheatMessage(_cheatJets ? "Jump jets enabled" : "Jump jets restored");
                    _cheatKeys = "";
                }
                else if (_cheatKeys == "CR")
                {
                    _cheatInfiniteFuel = !_cheatInfiniteFuel;
                    _motion.SetInfiniteJumpFuel(_cheatInfiniteFuel);
                    ShowCheatMessage(_cheatInfiniteFuel ? "Infinite jump fuel enabled" : "Infinite jump fuel disabled");
                    _cheatKeys = "";
                }
            }
#endif
        }

        void ShowCheatMessage(string message)
        {
            if (TryGetComponent<Mw2Cockpit>(out var cockpit)) cockpit.ShowMessage(message);
        }

        float _flightPitch;             // +0x5c of GP type 8

        /// <summary>
        /// Mech_UpdatePhysics 0x1003880d after the landing code, single play only: GP type 2 (fighters, hover and wheeled
        /// vehicles) sets the object's height to [0x380] = MGEO[0] every update, so its feet (this transform) sit at world
        /// height 0 whatever the ground; GP type 8 (AIRCRFTC, DROPZ) runs FUN_100090a7 0x100090a7: the new object height
        /// y' = y - throttle (+8) * (y / 2 / 0.015625), moved at most 300 units (_DAT_100870b4) from the height before the
        /// update, and the pitch +0x5c steps 1 degree per update towards sign(old - new) * 10 (_DAT_100870a0).
        /// </summary>
        void GamepieceAltitude(float oldFeet)
        {
            if (!TryGetComponent<Mw2MechCombat>(out var gp)) return;
            float h = gp.OriginHeight / PolyModel.UnitsToMeters;
            _motion.TurnLimit = gp.GamepieceType == 2 || gp.GamepieceType == 8 ? 180f : 90f;
            if (_fall.Frozen) return;
            if (gp.GamepieceType == 2) { _motion.Y = 0f; return; }
            if (gp.GamepieceType != 8) return;
            float before = oldFeet + h, y = _motion.Y + h;
            float throttle = KeyboardControl && Mw2Input.Loaded ? Mw2Input.Float(0x08) : Throttle / 64f;
            float target = y - throttle * (y / 2f / 0.015625f);
            float climb = before - target;
            if (Mathf.Abs(climb) > 300f)
                target = before - (Mathf.Abs(climb) >= 1e-7f ? Mathf.Sign(climb) : 0f) * 300f;
            _motion.Y = target - h;
            float want = climb == 0f ? 0f : climb > 0f ? 10f : -10f;
            float off = _flightPitch - want;
            if (off != 0f) _flightPitch -= Mathf.Abs(off) >= 1e-7f ? Mathf.Sign(off) : 0f;
        }

        /// <summary>Mech_DestroySection 0x100365d3: a leg came off this mech (GP type 1), it starts to fall over (MechFall).</summary>
        public void StartFall(int normal) => _fall.Start(normal);

        /// <summary>The unit lies or is falling over (+0xe42 bits 4 / 8): it no longer moves.</summary>
        public bool FallenOver => _fall.Frozen;
        /// <summary>Weapon_AimNodeDirection 0x1006de54 checks only +0xe42 bit 4, not the lying bit 8.</summary>
        public bool FallingOver => _fall.Falling;

        /// <summary>GP+0x5c read by AI_FireNoLead 0x100056a4 / AI_FireLead 0x1000540a:
        /// physical body pitch, retained after falling; type-8 altitude control adds its flight pitch.</summary>
        public float BodyPitch => _fall.Pitch + _flightPitch;

        /// <summary>
        /// Weapon_AimNodeDirection 0x1006de54: the aim takes the yaw of the eye node and adds its pitch to the weapon pitch
        /// only while bit 4 is set (falling); a mech lying on the ground (bit 8) aims as if it stood upright.
        /// </summary>
        public Quaternion AimFrame => _fall.Falling ? transform.rotation : Quaternion.Euler(0, _motion != null ? _motion.Heading : transform.eulerAngles.y, 0);

        /// <summary>
        /// 0x100399aa / 0x10039a8b: the THNG nodes [0x364] and [0x365] with a model (+0x6c, FUN_1005a6e1; every BWD node has one): the node's
        /// world position (FUN_1005a74f) lowered by half the model radius (+0x40, FUN_10003c5e) is at or below
        /// World_GroundHeight there.
        /// </summary>
        bool ThingTouchesGround()
        {
            if (!TryGetComponent<Mw2TorsoTwist>(out var twist)) return false;
            TryGetComponent<Mw2LodGroup>(out var lods);
            var torso = twist.Pivot != null && twist.Pivot.childCount > 0 ? twist.Pivot.GetChild(0) : null;
            return Touches(torso, lods) || Touches(twist.PitchNode, lods);
        }

        static bool Touches(Transform node, Mw2LodGroup lods)
        {
            if (node == null) return false;
            float radius = ModelRadius(node, lods);
            var p = node.position;
            p.y -= radius / 2f;
            return p.y <= Mw2Ground.Height(p);
        }

        /// <summary>
        /// Radius of the model the node shows at the current representation (Model_ComputeBounds 0x10003efa), metres.
        /// Lod_SwapModel loads the POLY whatever the node flags, so a mount node (flags 0x70, no mesh here) shows DUMMY
        /// (POLY 8697): one vertex at the node, radius 0.
        /// </summary>
        static float ModelRadius(Transform node, Mw2LodGroup lods)
        {
            var filter = Mw2LodGroup.NodeMesh(node, lods);
            return filter != null ? Mw2LodGroup.Radius(filter.sharedMesh) : 0f;
        }

        void OnDisable()
        {
            if (_jetSound == null) return;
            _jetSound.Stop();
            Destroy(_jetSound);
            _jetSound = null;
        }

        /// <summary>
        /// Segment from the old to the new position, extended by the collision radius; on a hit the move and the
        /// velocity lose their component along the surface normal, so the mech slides along walls. Then the height
        /// follows the highest walkable surface within 10 m (tolerance 1000 units in 0x100186b1), otherwise 0.
        /// Type 1 boxes have no ground-height function, but the original 3D segment cast stops a falling mech at their roof.
        /// </summary>
        /// <summary>
        /// Mech_SelectGait 0x10028abb: a frame with the footstep flag plays the unit's step sound (+0x90) at itself: the
        /// player's mech keeps the sound of its BWD ASND chunk (0x10070e92: MECHVFT1, MECFSMED or MECLIGHT by weight),
        /// every other unit gets NONFOOT 0x11a (0x10028a20). For the player 0x10028c64 plays
        /// the gait change of table 0x10090618: once when the current state (+0x84) changes towards another target state
        /// (+0x88) and once on arrival (current = target); walking on after reversing uses entry 1 (STOP2WLK).
        /// Torso: 0x1003a449 plays TORSLOOP (one copy at a time) at the player's mech while in the cockpit and the twist
        /// is more than 2 degrees from its target. All at half volume in the cockpit (DAT_100a6e48).
        /// </summary>
        void GaitSounds(Mw2Audio audio)
        {
            bool cockpit = Mw2Cockpit.PlayerInCockpit;
            int step = KeyboardControl ? _animator.StepSound : 0x11a;
            if (_animator.TakeFootstep() && step > 0 && audio != null)
                audio.Play(step, transform.position, false, 100, cockpit);
            if (!KeyboardControl || audio == null) return;
            int cur = _animator.CurrentState, tgt = _animator.RequestedState;
            if ((cur != tgt && cur != _lastGaitSound) || (cur == tgt && _lastGaitSound != -2))
            {
                var table = audio.Tables?.GaitSounds;
                int index = cur == 0 && _lastGaitSound == 2 ? 1 : 1 + (cur + 1) * 4 + tgt;
                if (table != null && index >= 0 && index < table.Length && table[index] != -1)
                    audio.Play(table[index], transform.position, false, 100, cockpit);
                _lastGaitSound = cur == tgt ? -2 : cur;
            }
            if (cockpit && TryGetComponent<Mw2TorsoTwist>(out var twist) && Mathf.Abs(twist.Target - twist.Current) > 2f)
                audio.Play(0x155, transform.position, false, 100, true);
        }

        /// <summary>
        /// 0x1003880d: the player's mech running into an obstacle while in the cockpit, once per contact (DAT_100ad340
        /// clears on a tick without collision), at a speed above 2 units per tick plays MECMTNHD (MECSEXX1 into
        /// another unit) at volume 200 * min(speed, 150) / 150. Disassembly at 0x10039296 and 0x100393b4 shows that
        /// FUN_100461fe reads the float +0xe34/+0xe38/+0xe3c vector saved before collision response. MECBLDC1
        /// (objects with flag 0x200) is not told apart.
        /// </summary>
        /// <summary>
        /// Mech_UpdatePhysics 0x1003880d: FUN_100350fb on a unit, FUN_100353af on scenery. In a network game a unit under this mech
        /// (normal above 0.866) is only reported by CO.
        /// </summary>
        void ApplyImpact(Mw2MechCombat self)
        {
            bool net = Mw2MechCombat.NetGame && _wallUnit != null && _wallNormal.y > 0.866f;
            if (net) return;
            Vector3 other = Vector3.zero;
            if (_wallUnit != null && _wallUnit.TryGetComponent<Mw2MechController>(out var c)) other = c._preCollisionVelocity;
            self.CollisionImpact(_wallNormal, _wallUnit, _wallStructure, _preCollisionVelocity, other);
        }

        void WallBump(Mw2Audio audio, float speed)
        {
            if (!_wallHit) { _bumped = false; return; }
            if (_bumped || !Mw2Cockpit.PlayerInCockpit) return;
            _bumped = true;
            // Mech_UpdatePhysics 0x1003880d: FUN_10022122 tells the other player about the contact, once per contact in the cockpit.
            if (speed > 2f && _wallUnit != null && Mw2MechCombat.NetGame && Mw2NetGame.Instance != null) Mw2NetGame.Instance.SendCollision(_wallUnit, _wallNormal, _preCollisionVelocity);
            if (speed <= 2f || audio == null) return;
            audio.Play(_wallHitUnit ? 0x105 : 0xfa, transform.position, true, (int)(200f * Mathf.Min(speed, 150f) / 150f));
        }

        float Collide(float oldX, float oldY, float oldZ)
        {
            _wallHit = _wallHitUnit = false;
            _wallUnit = null;
            _wallStructure = null;
            const float m = PolyModel.UnitsToMeters;
            bool unitContact = CollideUnits(); // 0x10034470: units precede GT/scene segments.
            var from = new Vector3(oldX * m, _motion.Y * m + ProbeHeight, oldZ * m);
            var move = new Vector3((_motion.X - oldX) * m, 0, (_motion.Z - oldZ) * m);
            float dist = move.magnitude;
            if (!unitContact && dist > 1e-5f && Physics.Raycast(from, move / dist, out var hit, dist + CollisionRadius,
                                                Mw2HitPart.MovementMask, QueryTriggerInteraction.Ignore))
            {
                var n = new Vector3(hit.normal.x, 0, hit.normal.z);
                if (n.sqrMagnitude > 1e-6f)
                {
                    _wallHit = true;
                    var hitCombat = hit.collider.GetComponentInParent<Mw2MechCombat>();
                    _wallHitUnit = hitCombat != null;
                    _wallUnit = hitCombat;
                    _wallStructure = hit.collider.GetComponentInParent<Mw2Destructible>();
                    _wallNormal = hit.normal;
                    // Mech_CollideMechs 0x10034973 explodes the other unit if it was already dead.
                    if (hitCombat != null && hitCombat.Destroyed) hitCombat.BlowApartOnImpact();
                    n.Normalize();
                    // Stop at the radius, then slide along the wall with what is left of the move.
                    float allowed = Mathf.Max(0, hit.distance - CollisionRadius);
                    var dir = move / dist;
                    var slide = move - Vector3.Dot(move, n) * n;
                    var target = from + dir * allowed + slide * (1f - allowed / dist);
                    if (Physics.Raycast(from + dir * allowed, slide.normalized, out var hit2,
                                        slide.magnitude + CollisionRadius, Mw2HitPart.MovementMask, QueryTriggerInteraction.Ignore))
                        target = from + dir * allowed;
                    _motion.X = target.x / m;
                    _motion.Z = target.z / m;

                    // Velocity (MW2 axes) loses its normal component.
                    var v = new Vector3(_motion.VelX, 0, _motion.VelZ);
                    v -= Vector3.Dot(v, n) * n;
                    _motion.VelX = v.x;
                    _motion.VelZ = v.z;
                }
            }

            // Mech_UpdatePhysics1003880d calls World_GroundHeight100186b1 after
            // integration. Its1000-unit near-height window also admits a surface
            // just ABOVE falling feet: LandAt must see it on the crossing frame.
            // Filtering it out while airborne makes raised terrain disappear
            // under the mech (LUT4 start:120m ->114.0306m -> erroneous world0).
            float currentY = _motion.Y * m;
            float ground = Mw2Ground.Height(new Vector3(_motion.X * m, currentY, _motion.Z * m));
            // A type 1 building blocks the original 3D movement segment even though World_GroundHeight ignores it.
            // Sweep down from the previous foot height so a fast frame cannot pass through its box-top roof.
            float previousY = oldY * m;
            if (previousY >= currentY - 0.01f)
            {
                var roofProbe = new Vector3(_motion.X * m, previousY + 0.5f, _motion.Z * m);
                float sweep = Mathf.Max(1f, previousY - currentY + 1f);
                foreach (var h in Physics.RaycastAll(roofProbe, Vector3.down, sweep,
                             Mw2HitPart.MovementMask, QueryTriggerInteraction.Ignore))
                {
                    var surface = h.collider.GetComponentInParent<Mw2Surface>();
                    if (surface == null || surface.CollisionType != 1 || h.normal.y < 0.5f) continue;
                    if (h.point.y <= previousY + 0.01f && h.point.y >= currentY - 0.01f && h.point.y > ground)
                        ground = h.point.y;
                }
            }
            _groundUnits = ground / m;
            return _motion.LandAt(ground / m);
        }

        /// <summary>Mech_CollideMechs 0x10034973: scan GP indices, resolve the first
        /// eligible 3D sphere, and preserve same-frame reciprocal contact. Unity
        /// execution scheduling remains an adapter; the response math is native-checked.</summary>
        bool CollideUnits()
        {
            if (!TryGetComponent<Mw2MechCombat>(out var self) || (!self.IsPlayer && self.PowerState != 2 && self.PowerState != 4)) return false;
            const float m = PolyModel.UnitsToMeters;
            var offset = self.Origin - transform.position;
            var body = new MechSphereContact.Body { X = _motion.X + offset.x / m, Y = _motion.Y + offset.y / m,
                Z = _motion.Z + offset.z / m, Radius = CollisionRadius / m,
                Vx = _preCollisionVelocity.x, Vy = _preCollisionVelocity.y, Vz = _preCollisionVelocity.z };
            var result = FindUnitContact(self, ref body, out var other);
            if (result == MechSphereContact.Result.DeadImpact) { other.BlowApartOnImpact(); return false; }
            if (result != MechSphereContact.Result.Contact) return false;
            _contactUnit = other;
            // 0x10039061..1003915c: Mech_UpdatePhysics probes the corrected
            // displacement again. A second contact scales the FIRST response by 2;
            // the second probe's position is discarded when both GT handles are 0.
            var second = body;
            second.Vx = _preCollisionVelocity.x; second.Vy = _preCollisionVelocity.y; second.Vz = _preCollisionVelocity.z;
            var next = FindUnitContact(self, ref second, out var secondOther);
            if (next == MechSphereContact.Result.DeadImpact) secondOther.BlowApartOnImpact();
            else if (next == MechSphereContact.Result.Contact)
            {
                body.Vx *= 2; body.Vy *= 2; body.Vz *= 2;
                _contactUnit = secondOther;
            }
            _wallHit = _wallHitUnit = true;
            _wallUnit = _contactUnit;
            _wallNormal = (transform.position - _contactUnit.transform.position).normalized;
            _motion.X = body.X - offset.x / m; _motion.Y = body.Y - offset.y / m; _motion.Z = body.Z - offset.z / m;
            _motion.VelX = body.Vx; _motion.VelY = body.Vy; _motion.VelZ = body.Vz;
            return true;
        }

        /// <summary>The GP-index unit scan of Mech_CollideMechs 0x10034973,
        /// reused by both Mech_UpdatePhysics contact passes. GT/segment fallback
        /// remains in the scene adapter and is not claimed equivalent here.</summary>
        MechSphereContact.Result FindUnitContact(Mw2MechCombat self, ref MechSphereContact.Body body, out Mw2MechCombat hit)
        {
            hit = null;
            const float m = PolyModel.UnitsToMeters;
            // All is insertion ordered, but explicit index selection also handles later activation.
            int previous = -1;
            for (int scan = 0; scan < Mw2MechCombat.All.Count; scan++)
            {
                Mw2MechCombat other = null;
                foreach (var unit in Mw2MechCombat.All)
                    if (unit != null && unit.GamepieceIndex > previous && (other == null || unit.GamepieceIndex < other.GamepieceIndex)) other = unit;
                if (other == null) break;
                previous = other.GamepieceIndex;
                if (other == self || !MechSphereContact.Eligible(other.BlownApart ? 0x100 : 0, other.GamepieceFlags)) continue;
                other.TryGetComponent<Mw2MechController>(out var control);
                var origin = other.Origin / m;
                var velocity = control != null ? control._preCollisionVelocity : Vector3.zero;
                var candidate = new MechSphereContact.Body { X = origin.x, Y = origin.y, Z = origin.z, Radius = other.SphereRadius / m,
                    Vx = velocity.x, Vy = velocity.y, Vz = velocity.z };
                bool cached = control != null && control._contactFrame == Time.frameCount && control._contactUnit == self && other.GamepieceIndex < self.GamepieceIndex;
                var result = MechSphereContact.Resolve(ref body, candidate, cached, other.PowerState == 4);
                if (result == MechSphereContact.Result.Miss) continue;
                hit = other;
                return result;
            }
            return MechSphereContact.Result.Miss;
        }

        // The camera follows after every Update (torso twist, ANIM pose), so camera and reticle agree each frame.
        void LateUpdate()
        {
            if (_camera != null) FollowWithCamera();
        }

        /// <summary>Chase camera behind the torso, so it looks where the weapons point when the torso twists.</summary>
        void FollowWithCamera()
        {
            var camera = _camera.GetComponent<Camera>();
            if (TryGetComponent<Mw2Cockpit>(out var cockpit))
            {
                if (cockpit.PlaceCamera(camera)) return;
                if (cockpit.IsPlayer && cockpit.PlaceExternalCamera(camera)) return;       // Camera_External 0x100237c0
                if (camera != null) camera.fieldOfView = Camera.HorizontalToVerticalFieldOfView(90f, camera.aspect);
            }
            var forward = TryGetComponent<Mw2TorsoTwist>(out var twist) ? twist.Forward : transform.forward;
            forward.y = 0;
            forward.Normalize();
            float back = new Vector2(_cameraOffset.x, _cameraOffset.z).magnitude;
            _camera.position = transform.position - forward * back + Vector3.up * _cameraOffset.y;
            _camera.rotation = Quaternion.LookRotation(transform.position + Vector3.up * 7f - _camera.position);
        }

        /// <summary>
        /// The player's controls from the control block (Mw2Input, DAT_100a7ae8), as Mech_UpdateControl 0x1003a449 reads
        /// them: throttle float +8 (1/64 = full, THROTTLE_* digits set k/576), legs_pan_delta +0x10 copied to the turn
        /// +0xc (+-1024; legs_pan_plus turns right), reverse byte +0x30 (REVERSE_DIRECTION toggles it), jump jets
        /// +0x1d..+0x21, torso pan +4 / tilt +0 in degrees (the torso target [2] is +4, clamped to the twist limit, which
        /// is written back with the torso_pan_set byte +0x1b; the weapon pitch target [6] is +0). TOGGLE_MASC (0x6e) sets
        /// DAT_100ad34c, handled by Mech_PlayerCommands 0x1003b7dd.
        /// </summary>
        bool _feetToTorso;
        float _controlHeading;          // DAT_100ad368: the heading at the last control update

        /// <summary>
        /// FEET_TO_TORSO in Mech_UpdateControl 0x1003a449 (player, DAT_100a7094): the heading change since the last
        /// control update (DAT_100ad368, unwrapped across +-180) is taken off the twist at once ([2] = [3] - delta,
        /// [3] = [2]) while the turn block target [0xe] is -55 (twist [3] <= 0) or +55; within (-4, 4) the turn block,
        /// the twist and the torso pan control (+4, with torso_pan_reset) go to 0 and the flag clears. Meanwhile the
        /// pan control is not copied to the twist target. Torso heading is heading + twist in both frames
        /// (FUN_100358ab 0x100358ab), so the rule applies to MechMotion.Heading and Mw2TorsoTwist.Current as is.
        /// </summary>
        void FeetToTorso(Mw2TorsoTwist twist)
        {
            float Wrap(float a) => a > 180f ? a - 360f : a < -180f ? a + 360f : a;
            float h = Wrap(_motion.Heading), prev = Wrap(_controlHeading);
            float now = h;
            if (h > 90f && prev < -90f) now = h - 360f;
            else if (prev > 90f && h < -90f) now = h + 360f;
            float delta = now - prev;
            float current = twist.Current;
            if (current <= -4f || current >= 4f)
            {
                _motion.TurnTargetOverride = current <= 0f ? -55f : 55f;
                if (delta != 0f) twist.SetTwistImmediate(current - delta);
            }
            else
            {
                _motion.TurnTargetOverride = null;
                _motion.Turn.Target = 0f;
                _motion.Turn.Current = 0f;
                twist.SetTwistImmediate(0f);
                Mw2Input.Set(0x1c);
                Mw2Input.SetFloat(0x04, 0f);
                _feetToTorso = false;
            }
        }

        void ReadKeyboard()
        {
            if (!Mw2Input.Loaded) return;
            // +0xc = +0x10 while the autopilot is off (gp +0x10 == 0); else FUN_100355e0 drives +0xc and may write +8.
            float throttleControl = Mw2Input.Float(0x08), turnControl = Mw2Input.Float(0x10);
            int tick = (int)(_clock + Time.deltaTime * MechMotion.TicksPerSecond);
            UpdateAutopilot(tick, ref turnControl, ref throttleControl);
            Throttle = throttleControl * 64f;
            Turn = turnControl / 1024f;
            Reverse = Mw2Input.Held(0x30);
            Jump = Mw2Input.Held(0x1d);
            JumpLeft = Mw2Input.Held(0x1e);
            JumpRight = Mw2Input.Held(0x1f);
            JumpForward = Mw2Input.Held(0x20);
            JumpBackward = Mw2Input.Held(0x21);
            if (Mw2Input.Command(0x28)) _feetToTorso = true;        // FEET_TO_TORSO: DAT_100a7094 = 1
            bool hasTwist = TryGetComponent<Mw2TorsoTwist>(out var twist);
            if (hasTwist && _feetToTorso) FeetToTorso(twist);
            _controlHeading = _motion.Heading;                      // 0x1003a449: DAT_100ad368 = +0x60 every update (player)
            if (hasTwist && !_feetToTorso)
            {
                // Torso pan +4 is the twist target [2]: positive turns right, like Mw2TorsoTwist.Target.
                float pan = Mw2Input.Float(0x04);
                if (twist.Limit < 360f && Mathf.Abs(pan) > twist.Limit)
                {
                    pan = Mathf.Sign(pan) * twist.Limit;
                    Mw2Input.SetFloat(0x04, pan);
                    Mw2Input.Set(0x1b);
                }
                twist.Target = pan;
                twist.PitchTarget = Mw2Input.Float(0x00);
            }
            if (Mw2Input.Command(0x6e))
            {
                if (HasMasc)
                {
                    MascActive = !MascActive;
                    if (Mw2Audio.Instance != null)
                    {
                        Mw2Audio.Instance.Play(MascActive ? 0xdc : 0xdb, transform.position, true);
                        // 0x1003b7dd: "MASC engaged." (the off branch passes variant 0, which says nothing).
                        if (MascActive) Mw2Audio.Instance.Computer(0x1f, 1);
                    }
                }
                else if (TryGetComponent<Mw2Cockpit>(out var cockpit)) cockpit.ShowMessage("Not equipped with MASC.");
                else Debug.Log("Not equipped with MASC.", this);
            }
        }
    }
}
