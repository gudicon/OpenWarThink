using System;
using UnityEngine;

namespace MW2.Formats
{
    /// <summary>
    /// PTBL chunk (FUN_1006fd5b 0x1006fd5b): a 0x40-byte name, then (body length - 0x40) / 0x1c points of seven i32:
    /// x, y, z in model units, pitch, yaw, roll in degrees and the ticks to the next point. The engine keeps up to 64
    /// tables of at most 64 points (DAT_102675e0, entries 0x744) and converts the first six values to float.
    /// </summary>
    public sealed class PathTable
    {
        public string Name;
        public Vector3[] Points;    // model units, MW2 axes
        public Vector3[] Angles;    // pitch, yaw, roll in degrees
        public int[] Ticks;         // duration of the segment that starts at this point

        public const int MaxTables = 0x40, MaxPoints = 0x40;

        public static PathTable Decode(byte[] d, int body, int length)
        {
            int count = (length - 0x40) / 0x1c;
            if (count < 0 || count > MaxPoints) return null;
            var t = new PathTable
            {
                Name = BwdChunks.ReadString(d, body, 0x40),
                Points = new Vector3[count],
                Angles = new Vector3[count],
                Ticks = new int[count]
            };
            for (int i = 0; i < count; i++)
            {
                int p = body + 0x40 + i * 0x1c;
                t.Points[i] = new Vector3(BitConverter.ToInt32(d, p), BitConverter.ToInt32(d, p + 4), BitConverter.ToInt32(d, p + 8));
                t.Angles[i] = new Vector3(BitConverter.ToInt32(d, p + 12), BitConverter.ToInt32(d, p + 16), BitConverter.ToInt32(d, p + 20));
                t.Ticks[i] = BitConverter.ToInt32(d, p + 24);
            }
            return t;
        }
    }

    /// <summary>
    /// TSK class 5 (task table 0x100a1130 -> FUN_1000e89e 0x1000e89e): "node;mode,rotate,path" read with
    /// "%[^,],%[^,],%[^,]". Mode "loop" = 0, "repeat" = 1, anything else 2 (one pass, e.g. "oneshot"); the second word
    /// "rotate" turns the node along the path. The path is looked up by name among the PTBL tables loaded so far.
    /// </summary>
    public sealed class PathTask
    {
        public string File;
        public int Node;
        public int Mode;
        public bool Rotate;
        public string PathName;
        public PathTable Path;      // null when no PTBL of that name was loaded before the task (the task does nothing)
    }

    /// <summary>
    /// Approach_Init 0x10025396 / Approach_Update 0x100253ed: moves the value towards its target by elapsed ticks *
    /// difference / (seconds * 181) and snaps to the target when the step would overshoot. With a period
    /// (FUN_100254b2 0x100254b2 / FUN_10025512 0x10025512) the difference is wrapped into +-period/2 and the result
    /// into the period by C fmod.
    /// </summary>
    public struct ApproachFilter
    {
        public int LastTick;
        public float Target, Value, Ticks, Period;

        public static ApproachFilter Init(float target, float value, double seconds, int now, float period = 0f) =>
            new ApproachFilter { Target = target, Value = value, Ticks = (float)(seconds * 181.0), LastTick = now, Period = period };

        public float Update(int now)
        {
            float diff = Target - Value;
            if (Period != 0f)
            {
                float half = Period / 2f;
                diff = CFmod(diff, Period);
                if (diff > half) diff -= Period;
                else if (diff < -half) diff += Period;
            }
            else if (Math.Abs(diff) < 1e-7f)
            {
                Value = Target;
                LastTick = now;
                return Value;
            }
            float step = (now - LastTick) * diff / Ticks;
            if ((diff < 0f && step <= diff) || (diff > 1e-7f && diff <= step)) Value = Target;
            else Value += step;
            if (Period != 0f) Value = CFmod(Value, Period);
            LastTick = now;
            return Value;
        }

        /// <summary>C fmod: the remainder takes the sign of the dividend.</summary>
        public static float CFmod(float a, float b) => (float)(a - b * Math.Truncate((double)a / b));
    }

    /// <summary>
    /// The update of TSK class 5 (FUN_1000e89e 0x1000e89e, task call 1): picks the segment by ticks since the start,
    /// interpolates the PTBL points linearly and feeds the position into three 0.3 s Approach filters and the angles into
    /// three 0.3 s filters with period 360. "rotate" adds the heading FUN_10045fd8(dx, dz) to yaw and subtracts
    /// asin(dy / 65536) * 57.29578 from pitch. One pass ends (the update returns 0) once the elapsed time reaches the
    /// segment that starts at the last point; "repeat" restarts from the first point with fresh filters; "loop" also uses
    /// the last point -> first point segment and wraps the time. FUN_10045fd8 is a table arctangent in degrees; this
    /// port uses the exact one.
    /// </summary>
    public sealed class PathMotion
    {
        public readonly PathTask Task;
        int _start, _total;
        ApproachFilter _x, _y, _z, _pitch, _yaw, _roll;
        public bool Finished;

        public Vector3 Position => new Vector3(_x.Value, _y.Value, _z.Value);   // model units
        public Vector3 Angles => new Vector3(_pitch.Value, _yaw.Value, _roll.Value);

        public PathMotion(PathTask task, int now)
        {
            Task = task;
            _start = now;
            foreach (int t in task.Path.Ticks) _total += t;
            ResetFilters(now);
        }

        void ResetFilters(int now)
        {
            var p0 = Task.Path.Points[0];
            _x = ApproachFilter.Init(p0.x, p0.x, 0.3, now);
            _y = ApproachFilter.Init(p0.y, p0.y, 0.3, now);
            _z = ApproachFilter.Init(p0.z, p0.z, 0.3, now);
            _pitch = ApproachFilter.Init(0, 0, 0.3, now, 360f);
            _yaw = ApproachFilter.Init(0, 0, 0.3, now, 360f);
            _roll = ApproachFilter.Init(0, 0, 0.3, now, 360f);
        }

        /// <summary>Returns false when the task does not move the node this tick.</summary>
        public bool Update(int now)
        {
            var path = Task.Path;
            int count = path.Points.Length;
            if (count < 2 || Finished) return false;
            int elapsed = now - _start, cumulative = 0, previous = 0, i;
            for (i = 0; i < count; i++)
            {
                previous = cumulative;
                cumulative += path.Ticks[i];
                if (elapsed <= cumulative) break;
            }
            if (count - 1 <= i)
            {
                if (Task.Mode == 2) { Finished = true; return false; }
                if (Task.Mode == 1)
                {
                    i = 0;
                    elapsed = 0;
                    _start = now;
                    previous = 0;
                    ResetFilters(now);
                }
                else if (i == count)
                {
                    i = 0;
                    elapsed -= _total;
                    _start = now - elapsed;
                    previous = 0;
                }
            }
            var a = path.Points[i];
            var b = i == count - 1 ? path.Points[0] : path.Points[i + 1];
            float f = (float)(elapsed - previous) / path.Ticks[i];
            _x.Target = (b.x - a.x) * f + a.x;
            _y.Target = (b.y - a.y) * f + a.y;
            _z.Target = (b.z - a.z) * f + a.z;
            _x.Update(now);
            _y.Update(now);
            _z.Update(now);

            var angles = path.Angles[i];
            float yaw = angles.y;
            if (Task.Rotate) yaw += Mathf.Atan2(b.x - a.x, b.z - a.z) * Mathf.Rad2Deg;
            SetTarget(ref _yaw, yaw);
            float pitch = angles.x;
            if (Task.Rotate) pitch -= (float)(Math.Asin((b.y - a.y) / 65536.0) * 57.29577951308232);
            SetTarget(ref _pitch, pitch);
            SetTarget(ref _roll, angles.z);
            _roll.Update(now);
            _yaw.Update(now);
            _pitch.Update(now);
            return true;
        }

        /// <summary>The value is first moved to the turn of the target nearest in fmod terms (target - fmod(target - value, 360)).</summary>
        static void SetTarget(ref ApproachFilter filter, float target)
        {
            filter.Target = target;
            filter.Value = target - ApproachFilter.CFmod(target - filter.Value, 360f);
        }
    }
}
