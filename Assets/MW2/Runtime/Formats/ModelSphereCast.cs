using System;

namespace MW2.Formats
{
    /// <summary>FUN_100047ff -> Object_SegmentTest 0x10018ee2, collision type 6.
    /// This is the original conservative model-sphere test, not a triangle intersection.</summary>
    public static class ModelSphereCast
    {
        public const float Miss = 3.4e38f;
        static bool Negative(float value) => BitConverter.SingleToInt32Bits(value) < 0;

        /// <summary>FUN_100047ff: start inside returns 10; crossing distance is projection-radius,
        /// not projection-sqrt(discriminant). Exact endpoint is excluded on the outside branch.</summary>
        public static float Candidate(AiLeadPoint.Vector center, float radius, AiLeadPoint.Vector from,
            AiLeadPoint.Vector direction, float length)
        {
            if (radius < 1e-7f) return Miss;
            float x = center.X - from.X, y = center.Y - from.Y, z = center.Z - from.Z;
            float outside = (float)((double)x * x + (double)y * y + (double)z * z - (double)radius * radius);
            if (Negative(outside)) return 10f;
            float dx = direction.X * length, dy = direction.Y * length, dz = direction.Z * length;
            float dot = (float)((double)x * dx + (double)y * dy + (double)z * dz);
            if (Negative(dot)) return Miss;
            dot /= length;
            if (Negative((float)((double)dot * dot - outside))) return Miss;
            float distance = dot - radius;
            if (Negative(distance)) return 0f;
            return length <= distance ? Miss : distance;
        }

        /// <summary>Object_SegmentTest type 6 / FUN_10019057 clips positive candidate distance.
        /// Zero keeps the original endpoint; an inside start can extend a short ray to 10 units.</summary>
        public static bool Hit(AiLeadPoint.Vector center, float radius, AiLeadPoint.Vector from,
            AiLeadPoint.Vector direction, float length, out float candidate, out float clipped, out AiLeadPoint.Vector point)
        {
            candidate = Candidate(center, radius, from, direction, length);
            clipped = length;
            if (candidate < Miss && length > 1e-7f && candidate > 1e-7f) clipped = candidate;
            point = new AiLeadPoint.Vector((float)((double)direction.X * clipped + from.X),
                (float)((double)direction.Y * clipped + from.Y), (float)((double)direction.Z * clipped + from.Z));
            return candidate < Miss;
        }
    }
}
