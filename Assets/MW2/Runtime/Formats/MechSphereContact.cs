using System;

namespace MW2.Formats
{
    /// <summary>Mech_CollideMechs 0x10034973, one candidate in the GP-index scan.
    /// Positions/radii are model units; velocities are saved +0xe34..0xe3c values.
    /// World segment/GT tests are separate and remain the scene adapter's responsibility.</summary>
    public static class MechSphereContact
    {
        public struct Body { public float X, Y, Z, Radius, Vx, Vy, Vz; }
        public enum Result { Miss, Contact, DeadImpact }

        /// <summary>Other-unit exclusions at 0x10034973: mech +0xe40 bit 0x100, GP +0x14 bit 0x4000.</summary>
        public static bool Eligible(int mechFlags, int gpFlags) => (mechFlags & 0x100) == 0 && (gpFlags & 0x4000) == 0;

        /// <summary>FUN_100461fe: max component plus one quarter of the other two, not Euclidean length.</summary>
        public static double Length(float x, float y, float z)
        {
            float largest = Math.Abs(x), a = Math.Abs(z), b = Math.Abs(y);
            if (largest < a) { float old = largest; largest = a; a = old; }
            if (largest < b) { float old = largest; largest = b; b = old; }
            return ((double)largest * 4 + a + b) / 4;
        }

        /// <summary>Cached earlier-index reciprocal contact bypasses the overlap test. A dead other
        /// unit is blown apart by the caller; the original returns 0 without moving this unit.</summary>
        public static Result Resolve(ref Body self, Body other, bool cached, bool otherDead)
        {
            float dx = self.X - other.X, dy = self.Y - other.Y, dz = self.Z - other.Z;
            double length = Length(dx, dy, dz);
            float radii = self.Radius + other.Radius;
            if (!cached && length >= radii) return Result.Miss;
            if (otherDead) return Result.DeadImpact;
            float distance = (float)length;
            float nx = 0, ny = 0, nz = 1;
            if (distance > 1e-7f) { nx = dx / distance; ny = dy / distance; nz = dz / distance; }
            self.X = nx * radii + other.X; self.Y = ny * radii + other.Y; self.Z = nz * radii + other.Z;
            double relative = Length(self.Vx - other.Vx, self.Vy - other.Vy, self.Vz - other.Vz) / 2;
            float speed = relative < 1 ? 1 : (float)relative;
            self.Vx = nx * speed; self.Vy = ny * speed; self.Vz = nz * speed;
            return Result.Contact;
        }
    }
}
