using System.Collections.Generic;
using System.IO;
using MW2.Formats;
using UnityEngine;

namespace MW2.Shell
{
    /// <summary>
    /// Screen animation of the shell (animate.cpp, 0x144-byte object; constructor FUN_1000120d 0x1000120d): an SHP (FUN_10001b4b,
    /// "smk\name.shp") or Smacker file (FUN_10001cff, "smk\name.smk") at x, y. Flags (+0x10c): 1 stopped, 2 Smacker decoded straight
    /// into the screen, 4 hold the last frame, 8 loop, 0x10 drawn (erase before moving), 0x20 hidden, 0x40 Smacker open flag
    /// 0x200, 0x80 x, y is the bottom centre, 0x100 redraw, 0x200 Smacker: at the end go to frame 1 and stop, 0x2000 Smacker,
    /// 0x10000000 remap the Smacker colours to the screen palette, 0x20000000 follow the mouse, 0x40000000 dead (removed).
    /// SHP frames advance by the object's own rate (fps, 0 -> 10; FUN_10002037), Smacker frames by the file's (SmackWait).
    /// </summary>
    public sealed class ShellAnim
    {
        public readonly string Name;
        public int X, Y;            // +0x110, +0x114
        int _oldX, _oldY;           // +0x120, +0x124
        public uint Flags;          // +0x10c
        public int W, H;            // +0x118, +0x11c
        public int Frame;           // +0x12c
        int _oldFrame;              // +0x128
        public int FrameCount;      // +0x130
        readonly int _msPerFrame;   // +0x134
        long _nextTime;             // +0x138
        Shape[] _frames;            // +0x104
        Smacker _smk;               // +0x100
        byte[] _smkBytes;
        byte[] _buffer;             // +0x108
        AudioSource _audio;
        AudioClip _audioClip;
        double _smkNextTime;
        readonly ShellAnimList _list;
        readonly ShellSurface _surface;

        public const uint Stopped = 1, Direct = 2, Hold = 4, Loop = 8, Drawn = 0x10, Hidden = 0x20, BottomCentre = 0x80,
            Redraw = 0x100, GotoFirstAndStop = 0x200, IsSmacker = 0x2000, Remap = 0x10000000, FollowMouse = 0x20000000,
            Dead = 0x40000000;

        public bool Has(uint f) => (Flags & f) != 0;

        /// <summary>FUN_1000120d: name, position, flags (bit 0x10000000 dropped, 0x100 added), frame rate, the list.</summary>
        public ShellAnim(ShellAnimList list, string name, int x, int y, uint flags, int fps)
        {
            _list = list;
            _surface = list.Surface;
            Name = name;
            X = x;
            Y = y;
            Flags = (flags & 0xefffffff) | Redraw;
            if (fps == 0) fps = 10;
            _msPerFrame = 1000 / fps;
            if (!LoadShape(list.SmkFolder) && !LoadSmacker(list.SmkFolder)) Flags = Dead;
            _oldX = X;
            _oldY = Y;
            if (Flags != Dead) list.Add(this);
        }

        /// <summary>FUN_10001b4b: SHP frames; the size is the largest frame size + 1.</summary>
        bool LoadShape(string folder)
        {
            string path = Path.Combine(folder, Name + ".shp");
            if (!File.Exists(path)) return false;
            byte[] d = File.ReadAllBytes(path);
            _frames = Shape.DecodeFrames(d);
            FrameCount = _frames.Length;
            int mw = -1, mh = -1;
            for (int i = 0; i < FrameCount; i++)
            {
                int o = System.BitConverter.ToInt32(d, 8 + i * 8);
                int hw = System.BitConverter.ToUInt16(d, o + 2), hh = System.BitConverter.ToUInt16(d, o);
                if (mw < hw) mw = hw;
                if (mh < hh) mh = hh;
            }
            W = mw + 1;
            H = mh + 1;
            if (Has(BottomCentre)) { X -= W / 2; Y -= H; }
            Frame = 0;
            _oldFrame = 0;
            _nextTime = 0;
            return true;
        }

        /// <summary>FUN_10001cff: Smacker file; without flag 2 frames decode into an own w x h buffer.</summary>
        bool LoadSmacker(string folder)
        {
            string path = Path.Combine(folder, Name + ".smk");
            if (!File.Exists(path)) return false;
            _smkBytes = File.ReadAllBytes(path);
            _smk = new Smacker(_smkBytes);
            if (_smk.AudioRate[0] != 0 && _list.AudioHost != null)
            {
                var audio = new Smacker(_smkBytes);
                while (audio.NextFrame()) { }
                if (audio.Audio.Count > 0 && audio.AudioHz > 0)
                {
                    var samples = new float[audio.Audio.Count];
                    for (int i = 0; i < samples.Length; i++) samples[i] = audio.Audio[i] / 32768f;
                    _audioClip = AudioClip.Create(Name, samples.Length / audio.AudioChannels,
                        audio.AudioChannels, audio.AudioHz, false);
                    _audioClip.SetData(samples, 0);
                    _audio = _list.AudioHost.AddComponent<AudioSource>();
                    _audio.playOnAwake = false;
                    _audio.spatialBlend = 0f;
                    _audio.clip = _audioClip;
                }
            }
            W = _smk.Width;
            H = _smk.Height;
            if (Has(BottomCentre)) { X -= W / 2; Y -= H; }
            Flags |= IsSmacker;
            Frame = 0;
            FrameCount = _smk.FrameCount;
            _oldFrame = Frame;
            if (!Has(Direct)) _buffer = new byte[W * H];
            return true;
        }

        /// <summary>SmackDoFrame: show the decoded current frame (into the buffer or the screen).</summary>
        void DoFrame()
        {
            if (_buffer != null) System.Buffer.BlockCopy(_smk.Pixels, 0, _buffer, 0, _buffer.Length);
            else _surface.DrawBuffer(_smk.Pixels, X, Y, W, H);
        }

        /// <summary>SmackGoto: decode from the start up to frame index n (0-based, the Smacker SDK numbering).</summary>
        void Goto(int n)
        {
            _smk = new Smacker(_smkBytes);
            for (int i = 0; i <= n && _smk.NextFrame(); i++) { }
        }

        /// <summary>FUN_10001445 0x10001445: before drawing; erase the old rectangle when the object moved, changed frame or hid.
        /// True when the object is dead and must be removed.</summary>
        public bool PreUpdate()
        {
            if (Has(Dead)) { ReleaseAudio(); return true; }
            if (_smk != null && Frame == 0 && !Has(Stopped))
            {
                _smk.NextFrame();   // the first SmackDoFrame decodes frame 0
                _audio?.Play();
                DoFrame();
                if (Has(Direct)) Flags |= Drawn;
                Frame = 1;
                _smkNextTime = _list.Now + _smk.MillisecondsPerFrame;
            }
            if (_smk != null && !Has(Direct)) _oldFrame = Frame;
            if (X != _oldX || Y != _oldY || Frame != _oldFrame || (Flags & 0x40000020) != 0)
            {
                if (Has(Drawn))
                {
                    _surface.Restore(_oldX, _oldY, W, H);
                    Flags |= Redraw;
                }
                _oldX = X;
                _oldY = Y;
                _oldFrame = Frame;
                Flags &= ~Drawn;
                if (Has(Dead)) { ReleaseAudio(); return true; }
            }
            return false;
        }

        /// <summary>FUN_10001640 0x10001640: draw and advance.</summary>
        public void Update()
        {
            if (Has(Dead)) return;
            if (Has(FollowMouse)) { X = _list.MouseX; Y = _list.MouseY; }
            if (_smk == null)
            {
                if (_frames == null) return;
                if (!Has(Hidden))
                {
                    Flags |= Drawn;
                    _surface.DrawShape(_frames[Frame], X, Y, W, H, onlyDirty: !Has(Redraw));
                    Flags &= ~Redraw;
                }
                if (!Has(Stopped))
                {
                    long now = _list.Now;
                    if (now >= _nextTime)
                    {
                        _nextTime = now + _msPerFrame;
                        Frame++;
                        if (FrameCount <= Frame)
                        {
                            Frame--;
                            if (!Has(Hold))
                            {
                                if (Has(Loop)) Frame = 0;
                                else Flags |= Dead | Stopped;
                            }
                            else Flags |= Stopped;
                        }
                        Flags |= Redraw;
                    }
                }
                return;
            }
            if (_buffer != null && !Has(Hidden))
            {
                Flags |= Drawn;
                _surface.DrawBuffer(_buffer, X, Y, W, H, onlyDirty: !Has(Redraw));
                Flags &= ~Redraw;
            }
            if (Has(Stopped) || _list.Now < _smkNextTime) return;     // SmackWait
            _smkNextTime = _list.Now + _smk.MillisecondsPerFrame;
            Frame++;
            if (FrameCount < Frame)
            {
                Frame--;
                if (Has(GotoFirstAndStop))
                {
                    Frame = 1;
                    Flags |= Stopped;
                    Goto(Frame);
                }
                else if (Has(Hold)) { Flags |= Stopped; return; }
                else if (!Has(Loop)) { Flags |= Dead | Stopped; ReleaseAudio(); return; }
                else
                {
                    Frame = 1;
                    Goto(Frame);
                    _audio?.Play();
                }
            }
            else if (Frame != 1) _smk.NextFrame();
            DoFrame();
            Flags |= Redraw;
            if (Has(Direct)) Flags |= Drawn;
        }

        /// <summary>FUN_10002117 0x10002117: show (clears 0x20, sets 0x100).</summary>
        public void Show()
        {
            if (!Has(Dead) && Has(Hidden)) Flags = (Flags & ~Hidden) | Redraw;
        }

        /// <summary>FUN_100021cd: hide; FUN_100021b0: kill; FUN_100021e4 / FUN_100021fb: stop / run.</summary>
        public void Hide() => Flags |= Hidden;
        public void Kill() { Flags |= Dead; ReleaseAudio(); }
        public void Stop() { Flags |= Stopped; _audio?.Pause(); }
        public void Run() { Flags &= ~Stopped; _audio?.UnPause(); }

        void ReleaseAudio()
        {
            if (_audio != null) { _audio.Stop(); Object.Destroy(_audio); _audio = null; }
            if (_audioClip != null) { Object.Destroy(_audioClip); _audioClip = null; }
        }

        /// <summary>FUN_100020c3 0x100020c3: set the frame (out of range -> 0) and force a redraw.</summary>
        public void SetFrame(int f)
        {
            if (Has(Dead)) return;
            if (FrameCount <= f) f = 0;
            Frame = f;
            _oldFrame = -1;
        }

        /// <summary>FUN_10001fbd 0x10001fbd: move (bottom-centre objects keep their anchor).</summary>
        public void MoveTo(int x, int y)
        {
            if (Has(Dead)) return;
            if (Has(BottomCentre)) { x -= W / 2; y -= H; }
            X = x;
            Y = y;
        }

        /// <summary>FUN_10002167 0x10002167: still running (neither dead nor stopped).</summary>
        public bool Running => !Has(Dead) && !Has(Stopped);

        /// <summary>FUN_1000137d 0x1000137d: removed from the list (the shell then deletes it).</summary>
        public void Destroy() { ReleaseAudio(); _list.Remove(this); }

        internal void Release() => ReleaseAudio();
    }

    /// <summary>
    /// Animation list (FUN_10001000 0x10001000, DAT_100cd0c4) with the screen it draws on. FUN_10001103 0x10001103: erase pass
    /// (removing dead objects), the "before" draw list, every object's draw/advance, the "after" draw list.
    /// </summary>
    public sealed class ShellAnimList
    {
        public readonly ShellSurface Surface;
        public readonly string SmkFolder;
        public readonly GameObject AudioHost;
        readonly List<ShellAnim> _items = new List<ShellAnim>();
        /// <summary>timeGetTime of the frame, in milliseconds.</summary>
        public long Now;
        public int MouseX, MouseY;

        public ShellAnimList(ShellSurface surface, string smkFolder, GameObject audioHost = null)
        {
            Surface = surface;
            SmkFolder = smkFolder;
            AudioHost = audioHost;
        }

        public void Add(ShellAnim a) => _items.Add(a);
        public void Remove(ShellAnim a) => _items.Remove(a);
        public int Count => _items.Count;

        public void Step()
        {
            for (int i = 0; i < _items.Count;)
            {
                if (_items[i].PreUpdate()) _items.RemoveAt(i);
                else i++;
            }
            Surface.DrawList(0);
            foreach (var a in _items.ToArray()) a.Update();
            Surface.DrawList(1);
        }

        /// <summary>FUN_1000108e 0x1000108e: delete every object.</summary>
        public void Clear()
        {
            foreach (var item in _items) item.Release();
            _items.Clear();
        }
    }
}
