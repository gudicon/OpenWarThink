using System.Collections.Generic;
using System.Linq;
using MW2.Formats;
using UnityEngine;

namespace MW2
{
    /// <summary>
    /// The palette on the screen of a mission (the original draws through a 256-colour palette that it fades): mission start
    /// (StartPalettes 0x1006b802: black, then the palette of the starting period over 0x16a ticks, TimeOfDay_SetPeriod
    /// 0x1001f6a1 on the third frame), time of day (TimeOfDay_Update 0x1001f584 every 0x712 ticks: dawn / day / dusk /
    /// night palettes, fades of 2715 / 2715 / 3620 / 2715 ticks), infrared (L, INFRARED 0xbf, Infrared_Set 0x1001f733: mode
    /// 12 over 0xb5 ticks, sound 0x95, computer 0x1c; off again with the sensors damaged), the screen flash of a critical
    /// ammunition explosion or a heavy hit on the head / centre torso (Hud_CockpitTones 0x10017c5c → FUN_10032872
    /// 0x10032872: mode 16, black, its length growing with every flash: 3, 6, 9... of 15 of 0x16a ticks) and the fade of the
    /// destroyed player (FUN_1003285c 0x1003285c: mode 16, 0x16a). The fades run in <see cref="PaletteFade"/> (181 ticks a
    /// second); the colours are written to the live palette texture the shaders read (Mw2Palette.cginc).
    /// Not applied: the HUD (drawn on the screen outside the palette here) and the Glide fog / clear colour (SKYGND.PAR,
    /// RGB values in the original too).
    /// </summary>
    public sealed class Mw2PaletteFade : MonoBehaviour
    {
        public static Mw2PaletteFade Instance;

        static readonly int PaletteId = Shader.PropertyToID("_Mw2Palette");
        static readonly int InfraredId = Shader.PropertyToID("_Mw2Infrared");
        static readonly int LiveId = Shader.PropertyToID("_Mw2PaletteLive");

        /// <summary>Table 0x10090120: palette mode and fade ticks of dawn, day, dusk and night.</summary>
        static readonly int[] PeriodTicks = { 2715, 2715, 3620, 2715 };

        [System.Serializable]
        public class Blob { public byte[] Data; }

        // Serialized with the scene (a mission laid out in the editor enters Play Mode without its archive): the PAL of every
        // PALG mode (768 six-bit values, empty without one) and the day of the mission.
        [SerializeField] List<Blob> _modes = new List<Blob>();
        [SerializeField] int _dayLength = 86400, _startPeriod = -1;
        Mw2MechCombat _player;
        PaletteFade _fade;
        Texture2D _texture;
        readonly Color32[] _colors = new Color32[256];
        readonly Dictionary<int, byte[]> _palettes = new Dictionary<int, byte[]>();
        double _ticks, _carry;
        int _frames;            // DAT_100a6ba0: frames before the first period
        double _next;           // DAT_10250f84
        int _lastPeriod = -1;   // DAT_10090140
        bool _resume = true;    // DAT_100a6bb8: the time of day runs
        bool _infrared;         // DAT_10250f88
        bool _flashPending;     // DAT_100a66cc
        int _flashes;           // DAT_100a66dc
        bool _deathFaded;       // DAT_100ad378

        public bool InfraredOn => _infrared;
        public PaletteFade Fade => _fade;

        /// <summary>Adds the component with the palettes of the mission's PALG read from the archive.</summary>
        public static Mw2PaletteFade Add(GameObject root, PrjArchive prj, Mission mission)
        {
            var c = root.AddComponent<Mw2PaletteFade>();
            c._dayLength = mission.DayLength;
            c._startPeriod = mission.StartPeriod;
            c._modes.Clear();
            var ids = mission.PaletteIds ?? new int[0];
            foreach (int id in ids)
            {
                byte[] data = null;
                if (id > 0)
                {
                    var entry = prj.Entries("PAL").FirstOrDefault(e => e.Id == id);
                    if (entry.Id == id) data = prj.Read(entry);
                    if (data != null && data.Length < 768) data = null;
                }
                c._modes.Add(new Blob { Data = data });
            }
            c.Begin();
            return c;
        }

        void OnEnable() { if (_modes != null && _modes.Count > 0) Begin(); }   // Add fills the modes first

        void Begin()
        {
            if (_modes == null || _modes.Count == 0) { Debug.LogWarning("MW2 palette: no PALG, palette fades off"); return; }
            if (_fade != null) return;
            Instance = this;
            _fade = new PaletteFade { Palettes = Raw };
            _texture = new Texture2D(256, 1, TextureFormat.RGBA32, false)
            {
                name = "MW2 live palette",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };
            _fade.Begin();
            Publish();
            Shader.SetGlobalTexture(PaletteId, _texture);
            Shader.SetGlobalFloat(LiveId, 1f);
        }

        void OnDisable()
        {
            if (Instance == this) Instance = null;
            Shader.SetGlobalFloat(LiveId, 0f);
            Shader.SetGlobalFloat(InfraredId, 0f);
            if (_texture != null) Destroy(_texture);
        }

        /// <summary>PAL of a mode of the mission's PALG (768 six-bit values), null without one (FUN_1006b388: id above 0).</summary>
        byte[] Raw(int mode) => mode >= 0 && mode < _modes.Count ? _modes[mode].Data : null;

        void Publish()
        {
            for (int i = 0; i < 256; i++)
                _colors[i] = new Color32(Palette.Expand(_fade.Display[i * 3]), Palette.Expand(_fade.Display[i * 3 + 1]),
                                         Palette.Expand(_fade.Display[i * 3 + 2]), 255);
            _texture.SetPixels32(_colors);
            _texture.Apply();
            _fade.Changed = false;
        }

        /// <summary>The player's combat state (for the sensors, DAT_1025f080 +6, and the destroyed state).</summary>
        Mw2MechCombat Player
        {
            get
            {
                if (_player == null && Time.frameCount >= _nextSearch)
                {
                    _nextSearch = Time.frameCount + 30;
                    _player = FindObjectsByType<Mw2MechCombat>(FindObjectsSortMode.None).FirstOrDefault(c => c.IsPlayer);
                }
                return _player;
            }
        }
        int _nextSearch;

        bool SensorsDamaged => Player != null && Player.GaugeDamage != null && Player.GaugeDamage[0] > 0;

        void Update()
        {
            if (_fade == null) return;
            float dt = Time.deltaTime;
            _ticks += dt * MechMotion.TicksPerSecond;
            // Hud_CockpitTones: a flash requested by the damage code.
            if (_flashPending)
            {
                _flashes++;
                HitFlash(_flashes * 3);
                _flashPending = false;
            }
            var player = Player;
            if (player != null && player.Destroyed && !_deathFaded && player.PowerState == 4)
            {
                _deathFaded = true;
                SetTicksPerFrame();
                _fade.Fade(0x10, 0x16a, 1);     // FUN_1003285c
            }
            if (Mw2Input.Command(0xbf)) SetInfrared(!_infrared);
            TimeOfDay();
        }

        void LateUpdate()
        {
            if (_fade == null) return;
            // FUN_1001c1c3 at the end of every frame: FUN_1006b3f0 and FUN_1006b34b, one step a frame; the steps are counted
            // in frames of DAT_100a6644 ticks, so the time of a fade stays ticks / 181 s at any frame rate.
            _carry += Time.deltaTime * MechMotion.TicksPerSecond / Mathf.Max(1, _fade.TicksPerFrame);
            for (int n = 0; _carry >= 1.0 && n < 64; n++)
            {
                _carry -= 1.0;
                _fade.Frame();
            }
            if (_carry > 64) _carry = 0;
            if (_fade.Changed) Publish();
            Shader.SetGlobalFloat(InfraredId, _fade.Group >= 12 && _fade.Group <= 15 ? 1f : 0f);   // DAT_100b1944
        }

        /// <summary>DAT_100a6644: the ticks of this frame (kept while a fade runs, so its step count stays valid).</summary>
        void SetTicksPerFrame()
        {
            if (!_fade.Running && _fade.Remaining < 1)
                _fade.TicksPerFrame = Mathf.Max(1, Mathf.RoundToInt((float)(Mathf.Max(Time.smoothDeltaTime, 0.001f) * MechMotion.TicksPerSecond)));
        }

        /// <summary>
        /// TimeOfDay_Update 0x1001f584: the sensors off, then the first two frames only count, afterwards every 0x712 ticks
        /// the period of (seconds + start) % day gives the palette (TimeOfDay_SetPeriod 0x1001f6a1).
        /// </summary>
        void TimeOfDay()
        {
            SetTicksPerFrame();
            if (SensorsDamaged && _infrared)
            {
                SetInfrared(false);
                _infrared = false;
            }
            if (_frames < 2)
            {
                _frames++;
                _next = 0;
            }
            else if (_next <= _ticks)
            {
                _next = _ticks + 0x712;
                SetPeriod(Mission.PeriodAt(_dayLength, _startPeriod, (int)(_ticks / MechMotion.TicksPerSecond)));
            }
        }

        void SetPeriod(int period)
        {
            if (!_resume || period == _lastPeriod) return;
            int ticks = PeriodTicks[period];
            if (_infrared)
            {
                ticks = 0xb5;
                _infrared = false;
            }
            if (_frames == 2)
            {
                _frames = 3;
                ticks = 0x16a;
            }
            _fade.FadeGroup(Mission.PeriodPaletteMode[period], ticks);
            _lastPeriod = period;
        }

        /// <summary>Infrared_Set 0x1001f733: on (sensors whole) is palette mode 12 over 0xb5 ticks; off hands the palette back to the time of day.</summary>
        public void SetInfrared(bool on)
        {
            Debug.Log("MW2 palette: infrared " + (on ? "on" : "off") + " requested (now " + _infrared + ", sensors damaged " + SensorsDamaged + ", mode " + _fade.Mode + ", PAL12 " + (Raw(0xc) != null) + ")");
            if (_infrared == on) return;
            if (on)
            {
                if (SensorsDamaged) return;
                _infrared = true;
                _resume = false;
                SetTicksPerFrame();
                _fade.FadeGroup(0xc, 0xb5);
                var audio = Mw2Audio.Instance;
                if (audio != null)
                {
                    audio.Play(0x95, transform.position, true);
                    audio.Computer(0x1c, 1);
                }
            }
            else
            {
                _resume = true;
                _lastPeriod = -1;
                _next = 0;
            }
        }

        /// <summary>DAT_100a66cc = 1: a critical ammunition explosion or a heavy hit on the head / centre torso of the player.</summary>
        public void RequestHitFlash() => _flashPending = true;

        /// <summary>FUN_10032872 0x10032872: the intensity is a fraction of 15 (16 and more: 1) of 0x16a ticks, out and back.</summary>
        void HitFlash(int intensity)
        {
            long fraction = intensity < 0x10 ? ((long)intensity << 16) / 0xf : 0x10000;
            int ticks = Mul16(0x16a, (int)fraction);
            SetTicksPerFrame();
            _fade.Fade(0x10, ticks, 1);
        }

        /// <summary>FUN_100076a0 0x100076a0: (a × b) >> 16, rounded.</summary>
        static int Mul16(int a, int b)
        {
            long product = (long)a * b;
            return (int)((product >> 16) + ((product >> 15) & 1));
        }

        /// <summary>FUN_1006b64f 0x1006b64f: an effect's palette flash (offset from the mode group, ticks, out and back).</summary>
        public void EffectFlash(int offset, int ticks)
        {
            if (_fade == null) return;
            SetTicksPerFrame();
            _fade.FadeOffset(offset, ticks, 1);
        }

        /// <summary>FUN_1006b88b 0x1006b88b: frames left of the running fade.</summary>
        public int FramesLeft => _fade == null ? 0 : _fade.Remaining;
    }
}
