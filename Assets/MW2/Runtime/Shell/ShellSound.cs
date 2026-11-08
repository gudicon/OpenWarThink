using MW2.Formats;
using UnityEngine;

namespace MW2.Shell
{
    /// <summary>
    /// Shell sound object (FUN_10071cf7 0x10071cf7, 0x2d bytes): a WAV record of DATABASE.MW2 in a Miles sample. Volume 0..127
    /// (FUN_10072249 0x10072249) scaled by the digital master volume DAT_100a4304 (0x10000 = full); loop count 1 by default,
    /// FUN_10072132 0x10072132 loops forever (0). FUN_1007215f 0x1007215f starts it, FUN_100721a5 0x100721a5 starts it and waits
    /// for the end, FUN_1007220f 0x1007220f tells whether it still plays, FUN_100721dc 0x100721dc stops it.
    /// </summary>
    public sealed class ShellSound
    {
        public static int MasterVolume = 0x10000;   // DAT_100a4304
        readonly AudioSource _source;
        readonly AudioClip _clip;
        int _volume = 1;      // +0xc
        int _loops = 1;       // +0x10

        public ShellSound(GameObject host, ShellDatabase db, int record)
        {
            byte[] wav = db.Raw(record);
            if (wav == null) return;
            var s = SoundSample.Decode(wav);
            if (s == null || s.Samples == null || s.Samples.Length == 0) return;
            _clip = AudioClip.Create($"shell{record}", s.Samples.Length / Mathf.Max(1, s.Channels), s.Channels, s.Rate, false);
            _clip.SetData(s.Samples, 0);
            _source = host.AddComponent<AudioSource>();
            _source.playOnAwake = false;
            _source.clip = _clip;
            _source.spatialBlend = 0f;
        }

        public void SetVolume(int v)
        {
            _volume = v < 0 ? 0 : v > 0x7f ? 0x7f : v;
            if (_source != null) _source.volume = (_volume * MasterVolume >> 16) / 127f;
        }

        public void LoopForever() => _loops = 0;

        public void Play()
        {
            if (_source == null) return;
            _source.loop = _loops == 0;
            SetVolume(_volume);
            _source.Play();
        }

        /// <summary>FUN_100721a5: the shell blocks until the sample ends; here the caller continues (the effect is the same
        /// for the screens, which change right after).</summary>
        public void PlayAndWait() => Play();

        public bool Playing => _source != null && _source.isPlaying;

        public void Stop()
        {
            if (_source != null) _source.Stop();
        }

        /// <summary>FUN_10071fa9 0x10071fa9: stop and release. While the shell waits for a sample (FUN_100721a5) the screen's
        /// clean-up has not run yet in the original, so releases wait for the end of that wait.</summary>
        public void Release()
        {
            if (Mw2Shell.Instance != null && Mw2Shell.Instance.DeferRelease(this)) return;
            ReleaseNow();
        }

        public void ReleaseNow()
        {
            if (_source == null) return;
            _source.Stop();
            Object.Destroy(_source);
            Object.Destroy(_clip);
        }
    }
}
