using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace MW2.Formats
{
    /// <summary>
    /// POLY resource: a sequence of 'WTBO' objects (layout from mercsw.dll 0x10073c76 / 0x10073d2f).
    ///   +0x00 'WTBO', +0x04 checksum, +0x08 name[16] (bytes negated when the first byte > 0x80),
    ///   +0x18 i16 vertexCount, +0x1A i16 polygonCount, +0x1C u16 flags,
    ///   +0x20 vertices (16 bytes: i32 x, y, z in world units (~1 cm, loader scale 1.0); i16 a, b),
    ///   then polygons (u16 material, u16 n, u16 idx[4]; 18 bytes with idx[7] when n >= 5).
    /// flags 0x2000: articulated part, the last vertex is the pivot. 0x1000: LOD set, name suffix "_N" is the distance.
    /// material: low byte = palette index; indices below 16 are runtime paint (camo) slots.
    /// </summary>
    public sealed class PolyObject
    {
        public string Name;
        public ushort Flags;
        public Vector3[] Vertices;
        public short[] VertexA;
        public short[] VertexB;
        public Vector3 Pivot;
        public List<(ushort material, int[] indices)> Polygons = new List<(ushort, int[])>();
        public bool ChecksumOk;

        public bool Articulated => (Flags & 0x2000) != 0;
        public bool IsLodSet => (Flags & 0x1000) != 0;
    }

    public static class PolyModel
    {
        const uint Magic = 0x4F425457;  // "WTBO"

        /// <summary>Atlas parts add up to ~1570 units for a ~15.7 m mech, so one unit is taken as 1 cm.</summary>
        public const float UnitsToMeters = 0.01f;

        /// <summary>Model_ComputeBounds 0x10003efa: bbox midpoint and maximum vertex distance.
        /// DecodeObject has already omitted the articulated pivot, as Poly_ReadObject 0x10073d2f does.</summary>
        public static (Vector3 center, float radius) SphereBounds(PolyObject obj)
        {
            if (obj.Vertices.Length == 0) return (Vector3.zero, 0);
            var min = obj.Vertices[0]; var max = min;
            foreach (var v in obj.Vertices)
            {
                min.x = Math.Min(min.x, v.x); min.y = Math.Min(min.y, v.y); min.z = Math.Min(min.z, v.z);
                max.x = Math.Max(max.x, v.x); max.y = Math.Max(max.y, v.y); max.z = Math.Max(max.z, v.z);
            }
            var center = new Vector3((min.x + max.x) * 0.5f, (min.y + max.y) * 0.5f, (min.z + max.z) * 0.5f);
            double squared = 0;
            foreach (var v in obj.Vertices)
            {
                double x = (double)v.x - center.x, y = (double)v.y - center.y, z = (double)v.z - center.z;
                squared = Math.Max(squared, x * x + y * y + z * z);
            }
            return (center, (float)Math.Sqrt(squared));
        }

        /// <summary>
        /// Poly_ReadObject 0x10073d2f: the distance threshold of a LOD step is the number after the first '_' of the
        /// object name (atol), read only when the character after '_' is a digit; otherwise 0.
        /// </summary>
        public static float LodThreshold(string name)
        {
            int u = name.IndexOf('_');
            if (u < 0 || u + 1 >= name.Length || !char.IsDigit(name[u + 1])) return 0f;
            long value = 0;
            for (int i = u + 1; i < name.Length && char.IsDigit(name[i]); i++)
            {
                value = value * 10 + (name[i] - '0');
                if (value > int.MaxValue) { value = int.MaxValue; break; }   // atol saturates at LONG_MAX
            }
            return value;
        }

        /// <summary>
        /// LOD chain of one POLY, as Poly_ReadObject 0x10073d2f builds it. Once an object with flag 0x1000 has been read
        /// (the flag stays set for the rest of the POLY), every object starts a new step with its name threshold
        /// (FUN_100035d1 0x100035d1); objects read before that are merged into the first step, whose threshold is 0.
        /// FUN_100035d1 inserts each step after all steps with a threshold not above it, so the chain is a stable ascending
        /// sort. Returns the steps as (threshold, object indices), or null when the POLY is a single model (no LOD flag)
        /// or has an articulated object (0x2000 objects each make their own model and never read the LOD flag).
        /// </summary>
        public static List<(float threshold, List<int> objects)> LodChain(List<PolyObject> objects)
        {
            if (objects.Count < 2 || !objects.Exists(o => o.IsLodSet) || objects.Exists(o => o.Articulated)) return null;
            var steps = new List<(float threshold, List<int> objects)>();
            bool lod = false;
            for (int k = 0; k < objects.Count; k++)
            {
                lod |= objects[k].IsLodSet;
                float threshold = lod ? LodThreshold(objects[k].Name) : 0f;
                if (k == 0) { steps.Add((threshold, new List<int> { 0 })); continue; }
                if (!lod) { steps[0].objects.Add(k); continue; }
                int at = 0;
                while (at < steps.Count && steps[at].threshold <= threshold) at++;
                steps.Insert(at, (threshold, new List<int> { k }));
            }
            return steps;
        }

        /// <summary>
        /// Model_SelectLod 0x10055950: the step drawn at view depth <paramref name="depth"/> (model units) is the last one
        /// whose threshold * camera[+0xb8] is not above the depth, or the first step when none is. A first step with
        /// threshold 0 (_DAT_1008823c) is always drawn.
        /// </summary>
        public static int SelectLod(IReadOnlyList<float> thresholds, float depth, float focalScale)
        {
            if (thresholds.Count == 0 || thresholds[0] == 0f) return 0;
            int selected = 0;
            for (int i = 0; i < thresholds.Count; i++)
            {
                if (depth < focalScale * thresholds[i]) break;
                selected = i;
            }
            return selected;
        }

        public static List<PolyObject> Decode(byte[] d)
        {
            var objects = new List<PolyObject>();
            int pos = 0;
            while (pos + 0x20 <= d.Length && BitConverter.ToUInt32(d, pos) == Magic)
            {
                try
                {
                    objects.Add(DecodeObject(d, ref pos));
                }
                catch (ArgumentOutOfRangeException)
                {
                    break;  // truncated resource (e.g. SCBSTEAL): keep the objects that were complete
                }
            }
            return objects;
        }

        /// <summary>Same folding the loader applies when accumulating the checksum.</summary>
        static int Fold(long v)
        {
            int x = unchecked((int)v);
            return x < 0 ? -((-x) & 0xFFFFF) : x & 0xFFFFF;
        }

        static PolyObject DecodeObject(byte[] d, ref int pos)
        {
            var o = new PolyObject();
            int checksum = BitConverter.ToInt32(d, pos + 4);

            var name = new byte[16];
            Buffer.BlockCopy(d, pos + 8, name, 0, 16);
            if (name[0] > 0x80)
                for (int i = 0; i < 16; i++) name[i] = unchecked((byte)-name[i]);
            int len = Array.IndexOf(name, (byte)0);
            o.Name = Encoding.ASCII.GetString(name, 0, len < 0 ? 16 : len);

            int vertexCount = BitConverter.ToInt16(d, pos + 0x18);
            int polygonCount = BitConverter.ToInt16(d, pos + 0x1A);
            o.Flags = BitConverter.ToUInt16(d, pos + 0x1C);

            int p = pos + 0x20;
            long sum = 0;
            var verts = new Vector3[vertexCount];
            o.VertexA = new short[vertexCount];
            o.VertexB = new short[vertexCount];
            for (int i = 0; i < vertexCount; i++, p += 16)
            {
                int x = BitConverter.ToInt32(d, p), y = BitConverter.ToInt32(d, p + 4), z = BitConverter.ToInt32(d, p + 8);
                short a = BitConverter.ToInt16(d, p + 12), b = BitConverter.ToInt16(d, p + 14);
                verts[i] = new Vector3(x * UnitsToMeters, y * UnitsToMeters, z * UnitsToMeters);
                o.VertexA[i] = a;
                o.VertexB[i] = b;
                sum = Fold(sum + x + y + z + a + b);
            }

            for (int i = 0; i < polygonCount; i++)
            {
                ushort material = BitConverter.ToUInt16(d, p);
                int n = BitConverter.ToUInt16(d, p + 2);
                int size = n < 5 ? 12 : 18;
                int slots = (size - 4) / 2;
                var idx = new int[Math.Min(n, slots)];
                for (int k = 0; k < slots; k++)
                {
                    int v = BitConverter.ToUInt16(d, p + 4 + k * 2);
                    if (k < idx.Length) idx[k] = v;
                    if (k < Math.Max(n, 4)) sum += v;
                }
                sum = Fold(sum + material + n);
                o.Polygons.Add((material, idx));
                p += size;
            }

            o.ChecksumOk = sum == checksum;
            if (o.Articulated && vertexCount > 0)
            {
                o.Pivot = verts[vertexCount - 1];
                Array.Resize(ref verts, vertexCount - 1);
            }
            o.Vertices = verts;
            pos = p;
            return o;
        }
    }
}
