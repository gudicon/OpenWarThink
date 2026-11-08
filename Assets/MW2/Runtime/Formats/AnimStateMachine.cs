using System;

namespace MW2.Formats
{
    /// <summary>
    /// Per-mech animation state shared by all of its tasks (fields at +0x80..+0x94 of the mech record in mercsw.dll).
    /// </summary>
    public sealed class MechAnimState
    {
        public const uint FlagRunning = 0x1;
        public const uint FlagFootContact = 0x2;    // set from frame flag 0x10
        public const uint FlagFootstep = 0x8;       // set from frame flag 0x800
        public const uint FlagReset = 0x10;

        public uint Flags;              // +0x80
        public int CurrentState = -1;   // +0x84
        public int TargetState = -1;    // +0x88, requested by the movement code
        public int Gait = 2;            // +0x8c: 1 slow (1.5x period), 2 normal, 3 fast (0.75x period)
        public int FrameDuration;       // +0x94, in clock ticks

        /// <summary>mercsw.dll 0x1000ddf6.</summary>
        public static int GaitDuration(int gait, int period) => gait switch
        {
            1 => period + (period >> 1),
            3 => period - (period >> 2),
            _ => period
        };
    }

    /// <summary>
    /// One animation task: binds an ANIM track to a skeleton node ("node;period,flags,track" in the mech BWD TSK chunk).
    /// Line-by-line port of the ANIM player, mercsw.dll 0x1000d2c0 (mode 0 = Init, mode 1 = Update).
    /// </summary>
    public sealed class AnimTask
    {
        public readonly int Node;
        public readonly int Period;
        public readonly bool Master;    // flags > 0x7f: drives the shared mech state
        readonly bool _enabled;
        readonly Anim _anim;
        readonly Anim.Track _defaultTrack;
        readonly MechAnimState _mech;
        Anim.Track _track;

        int _remainingTime;     // [5]
        float _remaining;       // [6]
        int _lastTime;          // [7]
        int _frame = -1;        // [9]

        /// <summary>Applies an incremental move: type 0..2 translation, 3..5 rotation (degrees), MW2 axes.</summary>
        public Action<int, float> Apply;

        public int CurrentFrame => _frame;

        public AnimTask(Anim anim, MechAnimState mech, int node, int period, int flags, int track, int now)
        {
            _anim = anim;
            _mech = mech;
            Node = node;
            Period = period;
            _enabled = flags != 0;
            Master = flags > 0x7f;
            _track = _defaultTrack = anim.Tracks[track];
            _lastTime = now;
            mech.FrameDuration = period;
            mech.CurrentState = -1;
        }

        public static bool TryParse(string spec, out int node, out int period, out int flags, out int track)
        {
            node = period = flags = track = 0;
            var parts = spec.Split(';');
            if (parts.Length != 2) return false;
            var nums = parts[1].Split(',');
            return nums.Length == 3 && int.TryParse(parts[0], out node) && int.TryParse(nums[0], out period)
                   && int.TryParse(nums[1], out flags) && int.TryParse(nums[2], out track);
        }

        public void Update(int now)
        {
            if (!_enabled) return;
            int dt = now - _lastTime;
            _lastTime = now;
            var m = _mech;

            if ((m.Flags & MechAnimState.FlagReset) != 0)
            {
                _track = _defaultTrack;
                _frame = -1;
                _remainingTime = 0;
                _remaining = 0;
                _lastTime = now;
                if (Master)
                {
                    m.FrameDuration = Period;
                    m.Flags &= ~(MechAnimState.FlagReset | MechAnimState.FlagRunning);
                    m.CurrentState = -1;
                }
                return;
            }

            if ((m.Flags & MechAnimState.FlagRunning) == 0)
            {
                if (Master && (m.TargetState != -1 || m.CurrentState != -1))
                {
                    m.Flags |= MechAnimState.FlagRunning;
                    m.FrameDuration = MechAnimState.GaitDuration(m.Gait, Period);
                }
                return;
            }

            if (Period == 0 || m.FrameDuration == 0) return;

            float step = _remainingTime < 1 ? _remaining : dt * _remaining / _remainingTime;
            if (_remaining >= 0) { if (step > _remaining) step = _remaining; }
            else if (step < _remaining) step = _remaining;
            Apply?.Invoke(_track.Type, step);

            _remaining -= step;
            _remainingTime -= dt;
            if (_frame == -1)
            {
                _frame = 0;
                _remainingTime = 0;
                m.CurrentState = 0;
            }
            if (_remainingTime >= 1) return;

            var frames = _anim.Frames;
            int k = _frame++;
            _remainingTime += m.FrameDuration;
            if (_remainingTime < -m.FrameDuration) _remainingTime = -m.FrameDuration;

            uint fl = frames[k].Flags;
            bool jumped = false;
            int target = 0;
            if ((fl & 0x40) != 0)
            {
                target = frames[k].S1;
                jumped = m.TargetState == target;
            }
            if ((fl & 0x80) != 0)
            {
                target = frames[k].S1;
                jumped |= m.TargetState != -1 && m.TargetState != m.CurrentState;
            }
            if ((fl & 0x100) != 0)
            {
                target = frames[k].S2;
                jumped |= m.TargetState == -1;
            }
            if (jumped)
            {
                jumped = false;
                int dir = m.CurrentState < target ? 1 : -1;
                int j = k;
                while (true)
                {
                    j += dir;
                    if (j >= frames.Length) j = -1;
                    if (j == -1) break;
                    if ((frames[j].Flags & 0x1) != 0 && frames[j].S0 == target) break;
                }
                if (j != -1)
                {
                    while (true)
                    {
                        if (j >= frames.Length) { j = -1; break; }
                        bool found = (frames[j].Flags & 0x200) != 0 && frames[j].S3 == m.CurrentState;
                        j++;
                        if (found) break;
                    }
                }
                if (j != -1)
                {
                    _frame = j;
                    jumped = true;
                }
            }

            if (!jumped && (fl & 0xC) != 0)
            {
                bool loop = true;
                if ((fl & 0x4) != 0)
                {
                    loop = false;
                    for (int i = 0; i < 4; i++)
                    {
                        sbyte s = frames[k].State(i);
                        if (s == -1) break;
                        loop = s == (sbyte)m.TargetState;
                        if (loop) break;
                    }
                }
                if (loop)
                {
                    int i = k, j;
                    do
                    {
                        j = i - 1;
                        if (j < 0 || (frames[j].Flags & 0x2) != 0) break;
                        i = j;
                    } while (j > -1);
                    _frame = j;
                }
            }

            if ((!jumped && (fl & 0x400) != 0) || _frame >= frames.Length)
            {
                _remainingTime = 0;
                _frame = -1;
            }
            if (_frame != -1) _remaining += _track.Deltas[_frame];

            if (!Master) return;
            if (jumped) m.CurrentState = target;
            m.Flags &= ~(MechAnimState.FlagFootContact | MechAnimState.FlagFootstep);
            if (_frame == -1)
            {
                if (m.TargetState == -1)
                {
                    m.Flags &= ~MechAnimState.FlagRunning;
                    m.CurrentState = -1;
                }
            }
            else
            {
                m.FrameDuration = MechAnimState.GaitDuration(m.Gait, Period);
                if ((frames[_frame].Flags & 0x10) != 0) m.Flags |= MechAnimState.FlagFootContact;
                if ((frames[_frame].Flags & 0x800) != 0) m.Flags |= MechAnimState.FlagFootstep;
            }
        }
    }
}
