using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using MW2.Formats;
using UnityEngine;
using UnityEngine.Networking;

namespace MW2
{
    /// <summary>
    /// Plays SNDS resource ids through Unity. 0x10006839 loads a sample by id; SFLX is decoded by 0x1005b838.
    /// The sample table of mercsw.dll (0x1008b900, see SoundTables) limits the copies of a sample and sets its playback
    /// rate; the radio queue (0x1000fec0) plays a pair of samples with one text line ("Chain fire" + "engaged").
    /// Approximations: Unity spatial rolloff and pan. One-shot samples use the eight channels of FirstAudio 0x1000fbd6;
    /// radio shares these channels with one-shot samples; continuous samples still use separate Unity sources.
    /// Music is the mission's CD audio track (see LoadTrack). The shipped Music_MissionStart 0x1000faa4
    /// unconditionally sets the MIDI sequence to -1, even with flag 4; no XMID mission score is selected.
    /// Volumes and sound flags come from mw2snd.cfg through Mw2Options (DAT_1008b89c effects, DAT_1008b8a0 voice,
    /// DAT_1008b8a4 the CD aux volume, flag 8 of DAT_1008b8a8 CD music); the main menu changes them.
    /// </summary>
    public class Mw2Audio : MonoBehaviour
    {
        public string PrjPath;
        public static Mw2Audio Instance;
        [Tooltip("CLIM body +4: positioned sounds beyond this many metres are muted; zero means no climate cutoff.")]
        public int ClimateSoundRange;

        [Tooltip("CD audio track of the mission (MUSI -> MUS text '%d %d'); -1 = none.")]
        public int CdTrack = -1;
        [Tooltip("CD music on (bit 8 of the sound flags DAT_1008b8a8, default 0xb).")]
        public bool CdMusic = true;
        [Range(0f, 1f), Tooltip("cdt.ini of the TE release plays the tracks at volume 80; the game sets the CD aux volume to full (DAT_1008b8a4).")]
        public float MusicVolume = 0.8f;

        AudioSource _music;
        int _musicTicks;

        PrjArchive _archive;
        readonly Dictionary<int, AudioClip> _clips = new Dictionary<int, AudioClip>();
        // Mission speech can be a loose SFL registered by the original file table
        // (FUN_100433d2), notably the TRA1 training voices in KEATING.
        readonly Dictionary<string, int> _looseVoiceIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<int, string> _looseVoicePaths = new Dictionary<int, string>();
        /// <summary>Radio slot (0x53 bytes at 0x100a6318): first and second SNDS id, text (0x33 chars), priority.</summary>
        struct RadioMessage
        {
            public int First, Second;
            public string Text;
            public int Priority;
        }
        readonly List<RadioMessage> _voices = new List<RadioMessage>();
        /// <summary>DAT_100a65b0 = 0: no radio message is playing or waiting.</summary>
        public bool RadioIdle => !_radioActive && _voices.Count == 0;
        readonly int[] _pilotAlternation = new int[5]; // DAT_1008ca4c/54 and neighbouring reply toggles
        bool _radioActive;
        RadioMessage _activeMessage;
        bool _radioBlocked; // DAT_100a65b8: FUN_1000fec0 rejects all subsequent messages.
        float _voiceUntil;
        AudioSource _voice;
        int _voiceChannel = -1;
        int _pendingSecond;     // second sample of the active message, started at the end of the first (0x10006e09)
        bool _openFailed;
        bool _midiChecked;
        WindowsMidiOutput _midiOutput;
        MidiPowerHum _powerHum;

        /// <summary>Hud_UpdatePlayer 0x100176cb supplies power state and trunc(mech +0x4c).
        /// Native output is disabled in automated headless runs; the message path has separate capture tests.</summary>
        public void UpdatePowerHum(int state, int pitch, bool hudVisible, bool inCockpit)
        {
            if (_paused) return;
            if (!_midiChecked)
            {
                _midiChecked = true;
                if (Array.IndexOf(Environment.GetCommandLineArgs(), "-nographics") >= 0 ||
                    Array.IndexOf(Environment.GetCommandLineArgs(), "--mw2-mute") >= 0) return;
                _midiOutput = WindowsMidiOutput.Open();
                if (_midiOutput != null) _powerHum = new MidiPowerHum(_midiOutput.Send);
            }
            _powerHum?.Update(state, pitch, Mw2Options.Effects, hudVisible, inCockpit);
        }

        SoundTables _tables;
        bool _tablesFailed;
        readonly SoundChannelPool _channels = new SoundChannelPool(8);
        readonly AudioSource[] _channelSources = new AudioSource[8];

        /// <summary>Sample and radio tables: the extracted sound.json, from the mercsw.dll next to MW2.PRJ on first use.</summary>
        public SoundTables Tables
        {
            get
            {
                if (_tables != null || _tablesFailed) return _tables;
                try { _tables = SoundTables.Get(Path.GetDirectoryName(PrjPath) ?? ""); }
                catch (Exception e) when (e is IOException || e is ArgumentException || e is UnauthorizedAccessException)
                {
                    Debug.LogWarning($"MW2: sound tables unavailable: {e.Message}");
                    _tablesFailed = true;
                }
                return _tables;
            }
        }

        SoundTables.SampleInfo? Info(int id) =>
            Tables != null && Tables.Samples.TryGetValue(id, out var info) ? info : (SoundTables.SampleInfo?)null;

        void OnEnable() => Instance = this;

        void Start()
        {
            // Music_MissionStart 0x1000f9a7 plays the CD track only with flag 8 of DAT_1008b8a8.
            if (CdMusic && CdTrack > 0 && (Mw2Options.SoundFlags & 8) != 0) StartCoroutine(LoadTrack(CdTrack));
        }

        /// <summary>Cd_SetVolume 0x10052fc5: auxSetVolume with DAT_1008b8a4 clamped to 0xffff, over cdt.ini's volume.</summary>
        float MusicGain => MusicVolume * Mathf.Min(Mw2Options.Music, 0xffff) / 65535f;

        bool _musicPaused;  // DAT_100a630c

        /// <summary>0x1005302b: the CD volume from DAT_1008b8a4.</summary>
        public void ApplyMusicVolume()
        {
            if (_music != null) _music.volume = MusicGain;
        }

        /// <summary>Music_MissionStart 0x1000f9a7: the mission's CD track, with flag 8.</summary>
        public void MusicStart()
        {
            if (!CdMusic || CdTrack <= 0 || (Mw2Options.SoundFlags & 8) == 0) return;
            if (_music == null) StartCoroutine(LoadTrack(CdTrack));
            else if (!_music.isPlaying) _music.Play();
        }

        /// <summary>Music_Pause 0x1000fab2: pause the CD music (flag 8) unless paused already.</summary>
        public void MusicPause()
        {
            if (_musicPaused) return;
            if (_music != null && (Mw2Options.SoundFlags & 8) != 0) _music.Pause();
            _musicPaused = true;
        }

        /// <summary>Music_Resume 0x1000faff: resume the CD music (flag 8) when paused.</summary>
        public void MusicResume()
        {
            if (!_musicPaused) return;
            if (_music != null && (Mw2Options.SoundFlags & 8) != 0) _music.UnPause();
            _musicPaused = false;
        }

        /// <summary>Music_Stop 0x1000fb4c.</summary>
        public void MusicStop()
        {
            if (_music != null) _music.Stop();
        }

        /// <summary>
        /// CD audio: 0x1000f9a7 at mission start, 0x10052b83 plays one track over MCI. The TE release replaces the disc
        /// with cdt.dll, which plays "game\CD\NN.ogg" for track NN; a track outside the disc (0x10052f6e) stays silent.
        /// </summary>
        IEnumerator LoadTrack(int track)
        {
            string dir = Path.GetDirectoryName(PrjPath) ?? "";
            string file = Path.Combine(dir, "CD", track.ToString("00") + ".ogg");
            // Standalone ports can bundle the disc audio without modifying the original installation.
            if (!File.Exists(file))
                file = Path.Combine(Application.dataPath, "StreamingAssets", "MW2Music", track.ToString("00") + ".ogg");
            if (!File.Exists(file))
            {
                Debug.Log($"MW2: no CD track {track} ({file})");
                yield break;
            }
            using (var request = UnityWebRequestMultimedia.GetAudioClip(new Uri(file).AbsoluteUri, AudioType.OGGVORBIS))
            {
                ((DownloadHandlerAudioClip)request.downloadHandler).streamAudio = true;
                yield return request.SendWebRequest();
                if (request.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogWarning($"MW2: CD track {track}: {request.error}");
                    yield break;
                }
                var clip = DownloadHandlerAudioClip.GetContent(request);
                clip.name = $"CD track {track}";
                _music = gameObject.AddComponent<AudioSource>();
                _music.clip = clip;
                _music.spatialBlend = 0f;
                _music.volume = MusicGain;
                _music.Play();
                if (_musicPaused) _music.Pause();
            }
        }

        /// <summary>
        /// 0x1000fb6d: every 0x16a ticks, when the CD state has changed (the track ended), play the track again, so the
        /// music repeats after a pause of up to two seconds.
        /// </summary>
        void UpdateMusic()
        {
            if (_music == null || _music.clip == null) return;
            _music.volume = MusicGain;
            if (_musicPaused || (Mw2Options.SoundFlags & 8) == 0) return;
            _musicTicks += Mathf.RoundToInt(Time.deltaTime * (float)MechMotion.TicksPerSecond);
            if (_musicTicks <= 0x169) return;
            _musicTicks = 0;
            if (!_music.isPlaying) _music.Play();
        }

        void OnDestroy()
        {
            _powerHum?.Dispose();
            _midiOutput?.Dispose();
            if (Instance == this) Instance = null;
            _archive?.Dispose();
            foreach (var clip in _clips.Values) if (clip != null) Destroy(clip);
        }

        /// <summary>
        /// FUN_1000fd4b 0x1000fd4b, entering the pause mode: FUN_10007259(0) ends every sample except the radio voice,
        /// FUN_10010713 stops the voice sample, Music_Pause.
        /// </summary>
        public void PauseForGame()
        {
            _powerHum?.Mute();
            for (int i = 0; i < _channelSources.Length; i++)
                if (i != _voiceChannel && _channelSources[i] != null)
                {
                    Destroy(_channelSources[i].gameObject);
                    _channelSources[i] = null;
                    _channels.Release(i);
                }
            if (_voice != null) _voice.Pause();
            MusicPause();
            _paused = true;
        }

        /// <summary>FUN_1000fd69 0x1000fd69, leaving the pause mode: Music_Resume and the voice sample (FUN_1001075f).</summary>
        public void ResumeFromPause()
        {
            _paused = false;
            MusicResume();
            if (_voice != null) _voice.UnPause();
        }
        bool _paused;

        void Update()
        {
            if (_paused) return;
            UpdateMusic();
            ReleaseFinishedSounds();
            if (_radioActive && _pendingSecond != 0 && _voice != null && !_voice.isPlaying)
            {
                // 0x10006e09: the end of the first sample starts the second one on the same channel.
                int chainedId = _pendingSecond;
                var second = Clip(chainedId);
                _pendingSecond = 0;
                if (second != null)
                {
                    _channels.ReplaceInChannel(_voiceChannel, chainedId);
                    _voice.clip = second;
                    _voice.pitch = Pitch(second);
                    _voice.Play();
                }
            }
            if (_radioActive && (Time.time < _voiceUntil || (_voice != null && _voice.isPlaying))) return;
            if (_voiceChannel >= 0)
            {
                _channelSources[_voiceChannel] = null;
                _channels.Release(_voiceChannel);
                _voiceChannel = -1;
            }
            _radioActive = false;
            if (_voices.Count == 0) return;
            var message = _voices[0];
            _voices.RemoveAt(0);
            // 0x10010329: a message without a first sound plays the second one alone.
            int first = message.First, secondId = message.Second;
            if (first == 0 && secondId != 0) { first = secondId; secondId = 0; }
            var clip = Clip(first);
            var text = message.Text;
            _radioActive = true;
            _activeMessage = message;
            float length = 0f;
            _pendingSecond = 0;
            if (clip != null)
            {
                // Radio_Start 0x10010329 calls Sound_Start with flag 0x150. The 0x100 bit protects its
                // channel from Sound_Alloc 0x10006839 eviction while the message is sounding.
                var info = Info(first);
                int priority = info != null && info.Value.Priority != -1 ? info.Value.Priority & 0xff : 0x50;
                _voiceChannel = _channels.Allocate(first, priority, info != null ? info.Value.Copies : -1, true);
                if (_voiceChannel < 0) { _radioActive = false; return; }
                if (_channelSources[_voiceChannel] != null)
                {
                    _channelSources[_voiceChannel].Stop();
                    Destroy(_channelSources[_voiceChannel].gameObject);
                }
                if (_voice == null)
                {
                    _voice = gameObject.AddComponent<AudioSource>();
                    _voice.spatialBlend = 0f;
                }
                _voice.clip = clip;
                _voice.volume = Mw2Options.VoiceGain;   // Radio_Start: Sound_Start with DAT_1008b8a0
                _channelSources[_voiceChannel] = _voice;
                _voice.pitch = Pitch(clip);
                _voice.Play();
                length = clip.length / _voice.pitch;
                var next = Clip(secondId);
                if (next != null)
                {
                    _pendingSecond = secondId;
                    length += next.length / Pitch(next);
                }
            }
            // Radio_Start 0x10010329: a spoken message shows no text; only a message without a playable sound puts its
            // text on the top strip (Message_Add 0x10042d4e, 0x712 ticks, priority 0x32) and holds the radio that long.
            if (clip == null && !string.IsNullOrEmpty(text))
            {
                var cockpit = Mw2Cockpit.Player;
                if (cockpit != null) cockpit.ShowMessage(text, 0x712);
                length = 0x712 / (float)MechMotion.TicksPerSecond;
            }
            _voiceUntil = Time.time + length;
        }

        AudioClip Clip(int id)
        {
            if (id < 1) return null;
            if (_clips.TryGetValue(id, out var cached)) return cached;
            try
            {
                if (_looseVoicePaths.TryGetValue(id, out var path))
                {
                    var loose = SoundSample.Decode(File.ReadAllBytes(path));
                    var voice = AudioClip.Create(Path.GetFileNameWithoutExtension(path),
                        loose.Samples.Length / loose.Channels, loose.Channels, loose.Rate, false);
                    voice.SetData(loose.Samples, 0);
                    _clips[id] = voice;
                    return voice;
                }
                if (_openFailed) return null;
                if (_archive == null) _archive = new PrjArchive(PrjPath);
                PrjArchive.Entry entry = default;
                bool found = false;
                foreach (var candidate in _archive.Entries("SNDS"))
                    if (candidate.Id == id) { entry = candidate; found = true; break; }
                if (!found) return null;
                var sample = SoundSample.Decode(_archive.Read(entry));
                var clip = AudioClip.Create(entry.Name, sample.Samples.Length / sample.Channels,
                                            sample.Channels, sample.Rate, false);
                clip.SetData(sample.Samples, 0);
                _clips[id] = clip;
                return clip;
            }
            catch (Exception e) when (e is IOException || e is ArgumentException)
            {
                Debug.LogWarning($"MW2: sound {id} unavailable: {e.Message}");
                if (_archive == null) _openFailed = true;
                return null;
            }
        }

        /// <summary>
        /// Queue a radio message: an MTBL voice name (resolved through FUN_100433d2 0x100433d2) and its text, as
        /// FUN_10010b21 (0x10010b21) does with priority 0x50. The top-strip text timing remains approximate.
        /// </summary>
        public void PlayVoice(string name, string text = null, int priority = 0x50)
        {
            int id = 0;
            if (!string.IsNullOrWhiteSpace(name))
                try
                {
                    if (!_openFailed)
                    {
                        if (_archive == null) _archive = new PrjArchive(PrjPath);
                        if (_archive.TryFind("SNDS", name, out var entry)) id = entry.Id;
                    }
                    if (id == 0 && name.IndexOfAny(new[] { '/', '\\', ':' }) < 0 &&
                        !_looseVoiceIds.TryGetValue(name, out id))
                    {
                        string gameFolder = Path.GetDirectoryName(PrjPath) ?? "";
                        string path = Path.Combine(gameFolder, "KEATING", name + ".SFL");
                        if (File.Exists(path))
                        {
                            id = 0x10000 + _looseVoicePaths.Count;
                            _looseVoiceIds[name] = id;
                            _looseVoicePaths[id] = path;
                        }
                    }
                }
                catch (Exception e) when (e is IOException || e is ArgumentException)
                {
                    Debug.LogWarning($"MW2: sound {name} unavailable: {e.Message}");
                    _openFailed = true;
                }
            QueuePair(new SoundTables.RadioPart { Sound = id, Text = text }, null, priority);
        }

        /// <summary>
        /// Radio_QueuePair (0x1000fec0): one of 8 slots takes the first part's sound and text and, when it has a sound,
        /// the second part's; the text is "first second." cut to 0x33 characters. Priority -1 takes the first sound's
        /// priority from the sample table. With the radio idle the slot plays at once; otherwise a waiting message with
        /// the same first sound and the same (or no) second sound refuses it (with a copies limit, the copies-th such
        /// message does), and it goes before the first waiting message of lower priority. Returns false when refused.
        /// </summary>
        public bool QueuePair(SoundTables.RadioPart? firstPart, SoundTables.RadioPart? secondPart, int priority = -1)
        {
            if (_radioBlocked) return false;
            if (firstPart == null) { firstPart = secondPart; secondPart = null; }
            if (firstPart == null) return false;
            var first = firstPart.Value;
            if (first.Sound < 0) first.Sound = 0;
            if (string.IsNullOrEmpty(first.Text)) first.Text = null;
            if (first.Sound == 0 && first.Text == null)
            {
                if (secondPart == null) return false;
                first = secondPart.Value;
                secondPart = null;
            }
            SoundTables.RadioPart? second = null;
            if (secondPart != null && secondPart.Value.Sound > 0)
            {
                var part = secondPart.Value;
                if (string.IsNullOrEmpty(part.Text)) part.Text = null;
                second = part;
            }
            if (_voices.Count + (_radioActive ? 1 : 0) >= 8) return false;

            var text = new StringBuilder();
            if (first.Text != null) text.Append(first.Text);
            if (second?.Text != null) text.Append(' ').Append(second.Value.Text);
            if (text.Length > 0) text.Append('.');
            string line = text.Length > 0x33 ? text.ToString(0, 0x33) : text.ToString();
            if (priority == -1 && first.Sound != 0) priority = Info(first.Sound)?.Priority ?? -1;
            var message = new RadioMessage { First = first.Sound, Second = second?.Sound ?? 0, Text = line, Priority = priority };

            int copies = first.Sound != 0 ? Info(first.Sound)?.Copies ?? -1 : -1, same = 0;
            foreach (var q in _voices)
                if (q.First != 0 && q.First == message.First && (q.Second == 0 || q.Second == message.Second) &&
                    (copies == -1 || ++same >= copies))
                    return false;
            int insert = 0;
            while (insert < _voices.Count && _voices[insert].Priority >= priority) insert++;
            _voices.Insert(insert, message);
            Update();
            return true;
        }

        /// <summary>FUN_100105c5(1), called by Input_HandleCommands after 0x108 ticks.
        /// Loose SFL ids represent the original resource-backed first part (+0x0c);
        /// they have no positive archive sample id in the original slot.</summary>
        public void PruneRadioForExit()
        {
            if (_radioBlocked || RadioIdle) return;
            var head = _radioActive ? _activeMessage : _voices[0];
            int waiting = _voices.Count - (_radioActive ? 0 : 1);
            int sample = _looseVoicePaths.ContainsKey(head.First) ? -1 : head.First;
            var plan = RadioExit.Evaluate(true, false, sample, head.Priority, waiting);
            int firstWaiting = _radioActive ? 0 : 1;
            if (plan.RemoveHead)
            {
                if (_radioActive)
                {
                    if (_voice != null) _voice.Stop();
                    if (_voiceChannel >= 0)
                    {
                        _channelSources[_voiceChannel] = null;
                        _channels.Release(_voiceChannel);
                        _voiceChannel = -1;
                    }
                    _radioActive = false;
                    _pendingSecond = 0;
                    _voiceUntil = 0;
                }
                else _voices.RemoveAt(0);
                firstWaiting = 1; // The promoted head is not tested by the waiting-node loop.
            }
            for (int i = _voices.Count - 1; i >= firstWaiting; i--)
            {
                var q = _voices[i];
                bool resource = _looseVoicePaths.ContainsKey(q.First);
                if (!RadioExit.KeepWaiting(resource ? -1 : q.First, resource, q.Priority)) _voices.RemoveAt(i);
            }
            _radioBlocked = plan.BlockNewMessages;
        }

        /// <summary>
        /// Cockpit computer voice (FUN_10010ab9 0x10010ab9): message <paramref name="message"/> of 0x1008cba0, followed by
        /// "engaged" (variant 1) or "disengaged" (2); variant 0 says nothing, -1 the message alone.
        /// </summary>
        public void Computer(int message, int variant = -1)
        {
            var t = Tables;
            if (variant == 0 || t == null || message < 0 || message >= t.Computer.Length) return;
            SoundTables.RadioPart? second = variant > 0 && variant < t.ComputerState.Length ? t.ComputerState[variant] : (SoundTables.RadioPart?)null;
            QueuePair(t.Computer[message], second);
        }

        /// <summary>FUN_10010b01 (0x10010b01): "Critical hit : " and the part (0x1008cd78).</summary>
        public void CriticalVoice(int part)
        {
            var t = Tables;
            if (t == null || part < 0 || part >= t.CriticalParts.Length) return;
            QueuePair(t.CriticalHit, t.CriticalParts[part]);
        }

        /// <summary>
        /// Radio_PilotReply 0x10010800: point 0 joins the first two pilots' replies; points 1, 2 and 4 use one.
        /// Reply 0 alternates the pilot table's first two lines, other replies take their direct index.
        /// FUN_10010b36 0x10010b36 sets each point's table index from GPS body +0x16.
        /// </summary>
        public void PilotReply(int point, int reply)
        {
            var t = Tables;
            if (t == null || reply < 0 || reply >= 8 || point < 0 || point >= _pilotAlternation.Length || point == 3) return;
            SoundTables.RadioPart? Part(int position)
            {
                foreach (var ai in Mw2MechAI.LanceOf(0))
                    if (ai.LancePosition == position && ai.PilotVoiceId >= 0 && ai.PilotVoiceId < t.PilotVoices.Length)
                    {
                        int line = reply;
                        if (reply == 0) { line = _pilotAlternation[position]; _pilotAlternation[position] = line == 0 ? 1 : 0; }
                        return t.PilotVoices[ai.PilotVoiceId][line];
                    }
                return null;
            }
            if (point == 0) QueuePair(Part(1), Part(2));
            else QueuePair(Part(point), null);
        }

        /// <summary>Radio_LanceReply 0x10010985: immediate reply code from Lance_SendCommand 0x1000b7aa.</summary>
        public void LanceReply(int code, int point)
        {
            var t = Tables;
            if (t == null || point < 0 || point > 2 || code < 0 || code >= t.LanceReplies.Length) return;
            int pilotLine = code switch { 1 => 2, 2 => 4, 3 => 3, 4 => 5, 5 => 6, 6 => 7, 9 => 1, _ => -1 };
            if (pilotLine >= 0) PilotReply(point, pilotLine);
            else if (code != 10) QueuePair(t.LancePoints[point], t.LanceReplies[code]);
        }

        /// <summary>A sample by its SNDS name (0x10061c9e resolves task and chunk names the same way).</summary>
        public AudioClip ClipByName(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || _openFailed) return null;
            try
            {
                if (_archive == null) _archive = new PrjArchive(PrjPath);
                return _archive.TryFind("SNDS", name, out var entry) ? Clip(entry.Id) : null;
            }
            catch (Exception e) when (e is IOException || e is ArgumentException)
            {
                Debug.LogWarning($"MW2: sound {name} unavailable: {e.Message}");
                _openFailed = true;
                return null;
            }
        }

        /// <summary>Playback rate of the sample table (the base rate otherwise) relative to the decoded clip.</summary>
        float Pitch(AudioClip clip)
        {
            if (clip == null) return 1f;
            foreach (var kv in _clips)
                if (kv.Value == clip)
                {
                    var info = Info(kv.Key);
                    return info != null && info.Value.Rate > 0 ? info.Value.Rate / (float)clip.frequency : 1f;
                }
            return 1f;
        }

        // Sound_Alloc 0x10006839 tests active channels and the sample's copies limit before its priority scan.
        void ReleaseFinishedSounds()
        {
            for (int i = 0; i < _channels.Count; i++)
                if (i != _voiceChannel && _channelSources[i] != null && !_channelSources[i].isPlaying)
                {
                    Destroy(_channelSources[i].gameObject);
                    _channelSources[i] = null;
                    _channels.Release(i);
                }
        }

        /// <summary>
        /// 0x10007134 (local) / 0x100071c8 (positioned), with basic Unity spatialization. <paramref name="volume"/> is the
        /// Miles volume of the call (100 plays at full volume here); <paramref name="half"/> is the positioned call's flag
        /// that halves the volume (FUN_10007081).
        /// </summary>
        public void Play(int id, Vector3 position, bool local = false, int volume = 100, bool half = false)
        {
            // FUN_10007081 (0x10007081): CLIM cutoff is checked before allocating a positioned sample.
            if (!local && ClimateSoundRange > 0 && Camera.main != null &&
                Vector3.Distance(position, Camera.main.transform.position) >= ClimateSoundRange) return;
            var clip = Clip(id);
            if (clip == null) return;
            float pitch = Pitch(clip);
            ReleaseFinishedSounds();
            var info = Info(id);
            // Sound_Start 0x10006687 replaces the caller's low-byte priority with the sample table's value;
            // unlisted samples use 0x32. Sound_Alloc 0x10006839 enforces copies before scanning channels.
            int priority = info != null && info.Value.Priority != -1 ? info.Value.Priority & 0xff : 0x32;
            int copies = info != null ? info.Value.Copies : -1;
            int channel = _channels.Allocate(id, priority, copies);
            if (channel < 0) return;
            if (_channelSources[channel] != null)
            {
                _channelSources[channel].Stop();
                Destroy(_channelSources[channel].gameObject);
            }
            var go = new GameObject($"MW2 sound {id}");
            go.transform.position = position;
            var source = go.AddComponent<AudioSource>();
            source.clip = clip;
            source.pitch = pitch;
            // Sound_Start 0x10006687 scales the call's volume by DAT_1008b89c (sound effects).
            source.volume = Mathf.Clamp01(volume / 100f) * (half ? 0.5f : 1f) * Mw2Options.EffectsGain;
            source.spatialBlend = local ? 0f : 1f;
            source.rolloffMode = AudioRolloffMode.Linear;
            source.minDistance = 5f;
            source.maxDistance = ClimateSoundRange > 0 ? Mathf.Min(750f, ClimateSoundRange) : 750f;
            _channelSources[channel] = source;
            source.Play();
        }

        /// <summary>
        /// A local sample at a 16.16 volume, as the audio sliders' previews (0x10006fc7: 0xe3 at the effects volume;
        /// 0x10010566: 0x31 at the voice volume or 0x4f at full volume), which play during the pause mode.
        /// </summary>
        public void PlayPreview(int id, int volume)
        {
            var clip = Clip(id);
            if (clip == null) return;
            ReleaseFinishedSounds();
            var info = Info(id);
            int priority = info != null && info.Value.Priority != -1 ? info.Value.Priority & 0xff : 0x32;
            int channel = _channels.Allocate(id, priority, info != null ? info.Value.Copies : -1);
            if (channel < 0) return;
            if (_channelSources[channel] != null)
            {
                _channelSources[channel].Stop();
                Destroy(_channelSources[channel].gameObject);
            }
            var go = new GameObject($"MW2 sound {id}");
            var source = go.AddComponent<AudioSource>();
            source.clip = clip;
            source.pitch = Pitch(clip);
            source.volume = Mathf.Clamp01(volume / 65536f);
            source.spatialBlend = 0f;
            _channelSources[channel] = source;
            source.Play();
        }

        /// <summary>
        /// The jump exhaust path 0x10032adc requests MECJJRXX while the jets run. Keep one source per mech so the
        /// repeated requests from the 181 Hz physics loop do not stack copies of the same sample.
        /// </summary>
        public AudioSource StartLoop(int id, Transform owner)
        {
            var clip = Clip(id);
            if (clip == null || owner == null) return null;
            var source = owner.gameObject.AddComponent<AudioSource>();
            source.clip = clip;
            source.loop = true;
            source.spatialBlend = 1f;
            source.rolloffMode = AudioRolloffMode.Linear;
            source.minDistance = 5f;
            source.maxDistance = ClimateSoundRange > 0 ? Mathf.Min(750f, ClimateSoundRange) : 750f;
            source.Play();
            return source;
        }
    }
}
