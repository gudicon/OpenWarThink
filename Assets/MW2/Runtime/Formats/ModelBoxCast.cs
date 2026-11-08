using System;

namespace MW2.Formats
{
    /// <summary>Axis-aligned box segment test 0x10017f60, slab helper 0x10051ce4.
    /// Inputs and output length are model units; bounds are already in world axes.</summary>
    public static class ModelBoxCast
    {
        public const float ParallelThreshold = 1e-7f; // DAT_10088214
        const float Infinite = 3.4e38f;

        /// <summary>0x10051ce4: a parallel component accepts both slab boundaries;
        /// each difference and quotient is stored as float before interval reduction.</summary>
        static bool Slab(float start, float direction, float min, float max, out float enter, out float exit)
        {
            enter = -Infinite; exit = Infinite;
            if (Math.Abs(direction) < ParallelThreshold) return start >= min && start <= max;
            float a = min - start, b = max - start;
            a /= direction; b /= direction;
            enter = Math.Min(a, b); exit = Math.Max(a, b);
            return true;
        }

        /// <summary>0x10017f60: inside starts clip to zero, exact endpoints are included,
        /// and equal entry planes keep X before Y before Z. Normal sign uses the clipped
        /// displacement after 0x10051a46, hence a zero-length hit has a positive normal.</summary>
        public static bool Hit(AiLeadPoint.Vector min, AiLeadPoint.Vector max, AiLeadPoint.Vector start,
            AiLeadPoint.Vector direction, float length, out float clipped, out AiLeadPoint.Vector point, out AiLeadPoint.Vector normal)
        {
            clipped = length; normal = default;
            point = new AiLeadPoint.Vector(start.X + direction.X * length, start.Y + direction.Y * length, start.Z + direction.Z * length);
            if (!Slab(start.X, direction.X, min.X, max.X, out float enter, out float exit) ||
                !Slab(start.Y, direction.Y, min.Y, max.Y, out float yEnter, out float yExit)) return false;
            int axis = 0;
            if (enter < yEnter) { enter = yEnter; axis = 1; }
            if (yExit < exit) exit = yExit;
            if (!Slab(start.Z, direction.Z, min.Z, max.Z, out float zEnter, out float zExit)) return false;
            if (enter < zEnter) { enter = zEnter; axis = 2; }
            if (zExit < exit) exit = zExit;
            if (enter > exit) return false;
            if (enter < 0) enter = 0;
            if (length < exit) exit = length;
            if (enter > exit) return false;
            clipped = enter;
            point = new AiLeadPoint.Vector((float)((double)direction.X * enter + start.X),
                (float)((double)direction.Y * enter + start.Y), (float)((double)direction.Z * enter + start.Z));
            if (axis == 0) normal.X = point.X - start.X > 0 ? -1 : 1;
            else if (axis == 1) normal.Y = point.Y - start.Y > 0 ? -1 : 1;
            else normal.Z = point.Z - start.Z > 0 ? -1 : 1;
            return true;
        }
    }
}
