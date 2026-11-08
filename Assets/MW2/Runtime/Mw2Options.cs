using MW2.Formats;

namespace MW2
{
    /// <summary>
    /// Engine options that the main menu (window 4) reads and changes, with the functions of its value records.
    /// Sound: SimMain 0x100465ae copies mw2snd.cfg (SoundConfig, DAT_100a6308) to DAT_1008b898..DAT_1008b8a8 (Betty,
    /// effects, voice, music, flags). The audio sliders read the current volume (0x1000f500), set it live while selected
    /// (0x1000f5ac), store it in the config on Accept (0x1000f6f7) and take the config value back on Esc (0x1000f888).
    /// Combat variables: globals with their getter / setter pairs (0x100603c4 / 0x100603d4 explosion chunks DAT_1009fc4c,
    /// 0x1004ea6c / 0x1004ea81 particles DAT_1009db88, 0x1004eaa4 / 0x1004eab9 multiple lights DAT_10097cb8,
    /// 0x1004eadc / 0x1004eaf1 shadows DAT_1009f71c); the setters also write config words 9..12. At mission start the page's
    /// set-up function (0x1004e9a1, from FirstMenu 0x1003c56a) applies those config words.
    /// Approximations: mw2snd.cfg is not written back (FUN_1000fd09 saves it when the sim ends; the port leaves the game
    /// folder untouched), so changes last for the session. Explosion chunks gate the models of GT pieces with flag 0x8000
    /// (Scene_LoadObjectModel 0x1005f729), which the port does not load; shadows gate FUN_1005d44e (0x1005d52c), which is
    /// not ported; both are kept as values only.
    /// </summary>
    public static class Mw2Options
    {
        static bool _loaded;
        static SoundConfig _config;

        /// <summary>DAT_100a6308: the config as read (and as changed by Accept).</summary>
        public static SoundConfig Config { get { EnsureLoaded(); return _config; } }

        // DAT_1008b898, DAT_1008b89c, DAT_1008b8a0, DAT_1008b8a4 (16.16, 0x10000 full) and DAT_1008b8a8.
        static int _betty = 0x10000, _effects = 0x10000, _voice = 0x10000, _music = 0x10000, _soundFlags = 0xb;
        public static int Effects { get { EnsureLoaded(); return _effects; } }
        public static int Voice { get { EnsureLoaded(); return _voice; } }
        public static int Music { get { EnsureLoaded(); return _music; } }
        /// <summary>DAT_1008b8a8: 2 voice on, 4 MIDI music, 8 CD music.</summary>
        public static int SoundFlags { get { EnsureLoaded(); return _soundFlags; } }

        // Initial values in .data: all four 1; 0x1004e9a1 then applies config words 9..12.
        static int _chunks = 1, _particles = 1, _lights = 1, _shadows = 1;
        public static bool ExplosionChunks { get { EnsureLoaded(); return _chunks != 0; } }
        public static bool Particles { get { EnsureLoaded(); return _particles != 0; } }
        public static bool MultipleLights { get { EnsureLoaded(); return _lights != 0; } }
        public static bool Shadows { get { EnsureLoaded(); return _shadows != 0; } }

        static DifficultyConfig _difficulty;
        /// <summary>DAT_100aea44: mw2dif.cfg with the single-play forced bytes (LoadCfgDifficulty 0x10024cf0).</summary>
        public static DifficultyConfig GameOptions { get { EnsureLoaded(); return _difficulty ?? (_difficulty = DifficultyConfig.Load(null)); } }

        public static float EffectsGain => Effects / 65536f;
        public static float VoiceGain => Voice / 65536f;

        static void EnsureLoaded()
        {
            if (_loaded) return;
            string folder = Mw2Input.GameFolder;
            if (string.IsNullOrEmpty(folder)) return;
            _loaded = true;
            _config = SoundConfig.Load(folder);
            _betty = _config.Betty;
            _effects = _config.Effects;
            _voice = _config.Voice;
            _music = _config.Music;
            _soundFlags = _config.Flags;
            // 0x1004e9a1: chunks, particles, lights, shadows from words 9..12.
            _chunks = _config.Word[9];
            _particles = _config.Word[10];
            _lights = _config.Word[11];
            _shadows = _config.Word[12];
            _difficulty = DifficultyConfig.Load(folder);
            Mw2MechCombat.Difficulty = _difficulty.Difficulty;
        }

        /// <summary>A new game folder (a newly loaded mission) reads mw2snd.cfg again.</summary>
        public static void Reset() => _loaded = false;

        static Mw2Audio Audio => Mw2Audio.Instance;

        // ---------------- audio sliders (arg 0 effects, 1 voice, 2 music, 3 Betty) ----------------

        /// <summary>0x1000f500: the current volume; reading the music volume also sets flag 8 and resumes the music.</summary>
        public static int VolumeGet(int arg)
        {
            EnsureLoaded();
            switch (arg)
            {
                case 0: return _effects;
                case 1: return _voice;
                case 2:
                    _soundFlags |= 8;
                    if (Audio != null) Audio.MusicResume();
                    return _music;
                case 3: return _betty;
                case 4: return (_soundFlags & 2) != 0 ? 1 : 0;
            }
            return 0;
        }

        /// <summary>
        /// 0x1000f5ac, every frame while the slider is selected: any slider but music pauses the music; a changed effects
        /// volume plays sample 0xe3 at it (0x10006fc7), a changed voice volume sets flag 2 and plays 0x31 at it (0x4f at full
        /// volume when the voice is off, 0x10010566); music sets the CD volume (0x1005302b), starts the music when it was
        /// 0 (Music_MissionStart) and resumes it.
        /// </summary>
        public static void VolumePreview(int arg, int value)
        {
            EnsureLoaded();
            if (arg < 0 || arg >= 5) return;
            var audio = Audio;
            if (arg != 2 && audio != null) audio.MusicPause();
            switch (arg)
            {
                case 0:
                {
                    int old = _effects;
                    _effects = value;
                    if (old != value && audio != null) audio.PlayPreview(0xe3, _effects);
                    break;
                }
                case 1:
                {
                    int old = _voice;
                    _voice = value;
                    if (value == 0) _soundFlags &= ~2; else _soundFlags |= 2;
                    if (old != value && audio != null)
                    {
                        if ((_soundFlags & 2) == 0) audio.PlayPreview(0x4f, 0x10000);
                        else audio.PlayPreview(0x31, _voice);
                    }
                    break;
                }
                case 2:
                {
                    int old = _music;
                    _music = value;
                    if (old != value && audio != null)
                    {
                        audio.ApplyMusicVolume();
                        if (old == 0) audio.MusicStart();
                    }
                    if (audio != null) audio.MusicResume();
                    break;
                }
                case 3:
                    _betty = value;
                    break;
            }
        }

        /// <summary>
        /// 0x1000f6f7 (Accept): the volume into the config; music 0 clears flags 4 and 8 and stops the music, otherwise
        /// sets them and pauses it (the pause mode resumes it on leaving); the flags become the config's.
        /// </summary>
        public static void VolumeAccept(int arg, int value)
        {
            EnsureLoaded();
            var c = _config;
            switch (arg)
            {
                case 0: _effects = value; c.Effects = value; break;
                case 1:
                    _voice = value; c.Voice = value;
                    if (value == 0) _soundFlags &= ~2; else _soundFlags |= 2;
                    break;
                case 2:
                    _music = value; c.Music = value;
                    if (value == 0)
                    {
                        c.Flags &= ~0xc;
                        if (Audio != null) Audio.MusicStop();
                    }
                    else
                    {
                        c.Flags |= 0xc;
                        if (Audio != null) Audio.MusicPause();
                    }
                    _soundFlags = c.Flags;
                    break;
                case 3: _betty = value; c.Betty = value; break;
                case 4:
                    if (value == 0) c.Flags &= ~2; else c.Flags |= 2;
                    _soundFlags = c.Flags;
                    break;
            }
        }

        /// <summary>0x1000f888 (Esc): the config value back; music 0 clears flags 4 and 8 and stops, otherwise sets them,
        /// sets the CD volume and pauses.</summary>
        public static void VolumeCancel(int arg)
        {
            EnsureLoaded();
            var c = _config;
            switch (arg)
            {
                case 0: _effects = c.Effects; break;
                case 1:
                    _voice = c.Voice;
                    if (_voice == 0) _soundFlags &= ~2; else _soundFlags |= 2;
                    break;
                case 2:
                    _music = c.Music;
                    if (_music == 0)
                    {
                        _soundFlags &= ~0xc;
                        if (Audio != null) Audio.MusicStop();
                    }
                    else
                    {
                        _soundFlags |= 0xc;
                        if (Audio != null) { Audio.ApplyMusicVolume(); Audio.MusicPause(); }
                    }
                    break;
                case 3: _betty = c.Betty; break;
                case 4: _soundFlags = c.Flags; break;
            }
        }

        // ---------------- combat variables ----------------

        /// <summary>0x100603c4 / 0x100603d4.</summary>
        public static int ChunksGet() { EnsureLoaded(); return _chunks; }
        public static void ChunksSet(int value) { EnsureLoaded(); _chunks = value; _config.Word[9] = value; }

        /// <summary>0x1004ea6c / 0x1004ea81 -> FUN_1004d25a: 0 also removes every live particle group.</summary>
        public static int ParticlesGet() { EnsureLoaded(); return _particles; }
        public static void ParticlesSet(int value) { EnsureLoaded(); _particles = value; _config.Word[10] = value; }

        /// <summary>0x1004eaa4 / 0x1004eab9 -> FUN_1002e892: no new lights (Light_Take 0x1002e843), no point light
        /// shading (0x1002ec0a, 0x1002f543) and no ground glows (0x1005d52c) while 0.</summary>
        public static int LightsGet() { EnsureLoaded(); return _lights; }
        public static void LightsSet(int value) { EnsureLoaded(); _lights = value; _config.Word[11] = value; }

        /// <summary>0x1004eadc / 0x1004eaf1 -> FUN_1005c847.</summary>
        public static int ShadowsGet() { EnsureLoaded(); return _shadows; }
        public static void ShadowsSet(int value) { EnsureLoaded(); _shadows = value; _config.Word[12] = value; }
    }
}
