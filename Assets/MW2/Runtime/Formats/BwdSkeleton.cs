using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace MW2.Formats
{
    /// <summary>
    /// Node hierarchy from a BWD resource: "BWD\0", u32 total, u32 ?, then chunks (tag[4], u32 length incl. header).
    /// Mech BWDs (ATLAS, MADDOG...) hold REV, DTBL, MON, a root OBJ, then one OBJ list per REPR (level of detail 1..5).
    /// Mission BWDs (e.g. BET1ARE2) use the same OBJ nodes to place objects on the map.
    /// OBJ body (52 bytes): +0 u16 node, +2 i16 parent (-1 = root), +4 i16 collision type (OBJ loader 0x10070785),
    /// +6 i32 scale[3] (1 = unit),
    /// +18 i32 rotation[3] (degrees, 16.16 fixed), +30 i32 position[3] (model units), +44 u32 flags, +48 u32 POLY id.
    /// flags 0x70: mount point without geometry (mech BWDs; area and sky BWDs build every node, see Mw2MissionBuilder.PlaceNodes).
    /// </summary>
    public sealed class BwdNode
    {
        public int Node;
        public int Parent;
        /// <summary>
        /// Collision type (0x10070785, tests in the table at 0x1008faf0): 0 box you can stand on, 1 box, 5 walkable
        /// surface (quadtree), 6 sphere, 7 polygon-accurate obstacle; -1 and values outside 0..7 become 4 (no collision).
        /// </summary>
        public int CollisionType;
        public Vector3Int Scale;
        public Vector3 RotationDegrees;
        public Vector3 Position;     // metres, MW2 axes
        public uint Flags;
        public int PolyId;
        /// <summary>Scene block of the record (record +4, DAT_1009fbf8 at load; Mission.Blocks), -1 none.</summary>
        public int Block = -1;

        /// <summary>
        /// Flags 0xA0 mark the cockpit shell (Atlas LOD5 AT5_HEAD). SecondRender (mercsw.dll 0x1001bd1e) keeps it in
        /// DAT_100a6750; in cockpit view the world pass skips it (0x1006ad7b) and 0x1001bea2 draws it with its children
        /// after the world, near plane 8 units, no fog, while the toggle DAT_1008fcf4 (default 1, command 0x13) is on.
        /// </summary>
        public bool IsCockpitShell => (Flags & 0xF0) == 0xA0;

        public bool HasGeometry => Flags == 0 || IsCockpitShell;
    }

    public static class BwdSkeleton
    {
        /// <summary>
        /// Mech +0xe00 ([0x380]) = MGEO[0] (Mgeo_Load 0x100242a0, World_LoadChunks 0x10070e92), in metres: the height of the
        /// engine object's origin above the feet (here the unit's transform sits at the feet). 0 without MGEO.
        /// </summary>
        /// <summary>
        /// Mech +0xe1c = MGEO[6] in metres: the unit radius (Mgeo_Load 0x100242a0 writes its seventh output there, call at
        /// 0x1007222e in World_LoadChunks) used by collisions, target brackets, area damage and the external camera; 0 without MGEO.
        /// </summary>
        public static float UnitRadius(PrjArchive prj, string mechName)
        {
            string geometry = DecodeGeometryName(prj.Read("BWD", mechName));
            if (geometry == null || !prj.TryFind("MGEO", geometry, out var mgeo)) return 0f;
            byte[] g = prj.Read(mgeo);
            return g.Length >= 28 ? BitConverter.ToInt32(g, 24) * PolyModel.UnitsToMeters : 0f;
        }

        public static float OriginHeight(PrjArchive prj, string mechName)
        {
            string geometry = BwdSkeleton.DecodeGeometryName(prj.Read("BWD", mechName));
            if (geometry == null || !prj.TryFind("MGEO", geometry, out var mgeo)) return 0f;
            byte[] g = prj.Read(mgeo);
            return g.Length >= 4 ? BitConverter.ToInt32(g, 0) * PolyModel.UnitsToMeters : 0f;
        }

        /// <summary>
        /// Nodes of one representation that belong to the cockpit shell: a node with flags 0xA0 (BwdNode.IsCockpitShell)
        /// and its descendants, which 0x1001bea2 draws together (10056204: the window, the arms).
        /// </summary>
        public static HashSet<int> ShellNodes(List<BwdNode> rep)
        {
            var parent = new Dictionary<int, int>();
            var roots = new HashSet<int>();
            foreach (var n in rep)
            {
                parent[n.Node] = n.Parent;
                if (n.IsCockpitShell) roots.Add(n.Node);
            }
            var result = new HashSet<int>();
            if (roots.Count == 0) return result;
            foreach (var n in rep)
            {
                int p = n.Node;
                for (int guard = 0; p >= 0 && guard < 256; guard++)
                {
                    if (roots.Contains(p)) { result.Add(n.Node); break; }
                    if (!parent.TryGetValue(p, out p)) break;
                }
            }
            return result;
        }

        /// <summary>TSK class 0, FUN_1000e170 0x1000e170: "node;x,y,z,seconds".
        /// The last IVA1ARE1 record omits a comma; sscanf leaves its duration uninitialised, so it is skipped.</summary>
        public static List<(int node, Vector3 angle, int ticks)> DecodeRotationTasks(byte[] d)
        {
            var tasks = new List<(int, Vector3, int)>();
            foreach (var (tag, body, length) in BwdChunks.Enumerate(d))
            {
                if (tag != "TSK" || length <= 6 || BitConverter.ToUInt16(d, body) != 0) continue;
                string spec = BwdChunks.ReadString(d, body + 6, length - 6);
                int semi = spec.IndexOf(';');
                if (semi < 0 || !int.TryParse(spec.Substring(0, semi), out int node)) continue;
                string[] p = spec.Substring(semi + 1).Split(',');
                if (p.Length != 4 ||
                    !float.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x) ||
                    !float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y) ||
                    !float.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float z) ||
                    !float.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float seconds)) continue;
                int ticks = (int)(seconds * MechMotion.TicksPerSecond); // _DAT_10087114 = 181
                if (ticks == 0) ticks = (int)MechMotion.TicksPerSecond; // abs(duration) < 1e-7, original default 181
                tasks.Add((node, new Vector3(x, y, z), ticks));
            }
            return tasks;
        }

        /// <summary>TSK class 2, FUN_1000e3d0 0x1000e3d0: "node;radius,period,enabled".
        /// The first two values set angular speed 360/period and tangential speed radius*2*pi/period;
        /// the third enables updates. SOL/WOL SCRI resources carry all instances in the archive.</summary>
        public static List<(int node, int degreesPerSecond, int unitsPerSecond)> DecodeOrbitTasks(byte[] d)
        {
            var tasks = new List<(int, int, int)>();
            foreach (var (tag, body, length) in BwdChunks.Enumerate(d))
            {
                if (tag != "TSK" || length <= 6 || BitConverter.ToUInt16(d, body) != 2) continue;
                string spec = BwdChunks.ReadString(d, body + 6, length - 6);
                int semi = spec.IndexOf(';');
                if (semi < 0 || !int.TryParse(spec.Substring(0, semi), out int node)) continue;
                string[] p = spec.Substring(semi + 1).Split(',');
                if (p.Length < 3 ||
                    !float.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float radius) ||
                    !float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float period) ||
                    !int.TryParse(p[2], out int enabled) || period == 0 || enabled == 0) continue;
                tasks.Add((node, (int)(360f / period), (int)(radius * (float)(2 * Math.PI) / period)));
            }
            return tasks;
        }

        /// <summary>TSK class 1, FUN_1000df2a 0x1000df2a: "node;colour,...".
        /// Each colour value becomes a POLY material after shifting left by four bits.
        /// The TSK header's second u16 supplies the update period in game ticks.</summary>
        public static List<(int node, int period, ushort[] materials)> DecodeMaterialTasks(byte[] d)
        {
            var tasks = new List<(int, int, ushort[])>();
            foreach (var (tag, body, length) in BwdChunks.Enumerate(d))
            {
                if (tag != "TSK" || length <= 6 || BitConverter.ToUInt16(d, body) != 1) continue;
                string spec = BwdChunks.ReadString(d, body + 6, length - 6);
                int semi = spec.IndexOf(';');
                if (semi < 0 || !int.TryParse(spec.Substring(0, semi), out int node)) continue;
                var values = spec.Substring(semi + 1).Split(',');
                var materials = new List<ushort>();
                foreach (string value in values)
                {
                    if (materials.Count == 16 || !uint.TryParse(value.Trim(), NumberStyles.Integer,
                                                                CultureInfo.InvariantCulture, out uint raw)) break;
                    materials.Add(unchecked((ushort)(raw << 4)));
                }
                if (materials.Count == 0) continue;
                int period = BitConverter.ToUInt16(d, body + 2);
                tasks.Add((node, period == 0 ? 1 : period, materials.ToArray()));
            }
            return tasks;
        }

        /// <summary>World_LoadChunks 0x10070e92: model BWD's GP chunk header +10 (body +2) selects
        /// the gamepiece table at 0x1008f3e0 and becomes gamepiece +0. -1 means no GP chunk.</summary>
        public static int DecodeGamepieceType(byte[] d)
        {
            foreach (var (tag, body, length) in BwdChunks.Enumerate(d))
                if (tag == "GP" && length >= 4) return BitConverter.ToInt16(d, body + 2);
            return -1;
        }

        /// <summary>Returns the representations; index 0 holds the nodes in front of the first REPR (the shared root).</summary>
        public static List<List<BwdNode>> Decode(byte[] d)
        {
            if (d.Length < 12 || Encoding.ASCII.GetString(d, 0, 3) != "BWD")
                throw new ArgumentException("not a BWD resource");
            var reps = new List<List<BwdNode>>();
            var current = new List<BwdNode>();
            int pos = 0xC;
            while (pos + 8 <= d.Length)
            {
                string tag = Encoding.ASCII.GetString(d, pos, 4).TrimEnd('\0');
                int length = BitConverter.ToInt32(d, pos + 4);
                if (length < 8 || pos + length > d.Length) break;
                int b = pos + 8;
                if (tag == "REPR")
                {
                    reps.Add(current);
                    current = new List<BwdNode>();
                }
                else if (tag == "OBJ" && length >= 8 + 52)
                {
                    current.Add(new BwdNode
                    {
                        Node = BitConverter.ToUInt16(d, b),
                        Parent = BitConverter.ToInt16(d, b + 2),
                        CollisionType = CollisionTypeOf(BitConverter.ToInt16(d, b + 4)),
                        Scale = new Vector3Int(BitConverter.ToInt32(d, b + 6), BitConverter.ToInt32(d, b + 10), BitConverter.ToInt32(d, b + 14)),
                        RotationDegrees = new Vector3(BitConverter.ToInt32(d, b + 18), BitConverter.ToInt32(d, b + 22), BitConverter.ToInt32(d, b + 26)) / 65536f,
                        Position = new Vector3(BitConverter.ToInt32(d, b + 30), BitConverter.ToInt32(d, b + 34), BitConverter.ToInt32(d, b + 38)) * PolyModel.UnitsToMeters,
                        Flags = BitConverter.ToUInt32(d, b + 44),
                        PolyId = (int)BitConverter.ToUInt32(d, b + 48)
                    });
                }
                pos += length;
            }
            reps.Add(current);
            return reps;
        }

        static int CollisionTypeOf(int raw) => raw < 0 || raw > 7 ? 4 : raw;

        /// <summary>
        /// Animation binding of a mech BWD: the ANIM chunk (u16 id, name) and TSK chunks
        /// (u32 task class, 3 = ANIM player; u16; "node;period,flags,track").
        /// </summary>
        public static (string animName, List<string> tasks) DecodeAnimation(byte[] d)
        {
            string animName = null;
            var tasks = new List<string>();
            int pos = 0xC;
            while (pos + 8 <= d.Length)
            {
                string tag = Encoding.ASCII.GetString(d, pos, 4).TrimEnd('\0');
                int length = BitConverter.ToInt32(d, pos + 4);
                if (length < 8 || pos + length > d.Length) break;
                if (tag == "ANIM" && length > 10)
                    animName = ReadString(d, pos + 10, pos + length).ToUpperInvariant();
                // TSK class is the low u16. FIREMOTH stores 0x00010003 here: high u16 is a flag,
                // and rejecting the whole u32 dropped all its animation tasks and thus its controller.
                else if (tag == "TSK" && length > 14 && BitConverter.ToUInt16(d, pos + 8) == 3)
                    tasks.Add(ReadString(d, pos + 14, pos + length));
                pos += length;
            }
            return (animName, tasks);
        }

        /// <summary>
        /// THNG chunks (i16 node, loader in 0x10070e92): the engine takes them in order as the torso twist node
        /// ([0x364], absolute yaw = twist each frame in 0x1003880d) and the weapon pitch node ([0x365]).
        /// </summary>
        /// <summary>
        /// ASND chunk (World_LoadChunks 0x10070e92): body +0 i16 SNDS id of the unit's step sound, +2 its name (resolved by
        /// name when the id is -1). The engine stores it in game piece +0x90 for the player's unit only (+0x10 = 0);
        /// Mech_InitStepSound 0x10028a20 gives every other unit NONFOOT. Mech BWDs: MECHVFT1 (heavy), MECFSMED (medium),
        /// MECLIGHT (light); aircraft HELICPTR. Returns (-1, null) without the chunk.
        /// </summary>
        /// <summary>
        /// Sound tasks: TSK chunks of class 4 (task table 0x100a1130 -> 0x1000e68f), "node;range,name,flag" read with
        /// "%d,%[^,],%d": a looping SNDS sample at the node, heard within range * 100 model units of the camera
        /// (helicptr on aircraft, mecfire1, windmill, jettaxi, longship, crowd03 in mission areas).
        /// </summary>
        public static List<(int node, int rangeUnits, string sound, int flag)> DecodeSoundTasks(byte[] d)
        {
            var list = new List<(int, int, string, int)>();
            foreach (var (tag, body, length) in BwdChunks.Enumerate(d))
            {
                if (tag != "TSK" || length <= 6 || BitConverter.ToUInt16(d, body) != 4) continue;
                string spec = BwdChunks.ReadString(d, body + 6, length - 6);
                int semi = spec.IndexOf(';');
                if (semi < 0 || !int.TryParse(spec.Substring(0, semi), out int node)) continue;
                var parts = spec.Substring(semi + 1).Split(',');
                if (parts.Length < 2 || !int.TryParse(parts[0], out int range)) continue;
                int flag = parts.Length > 2 && int.TryParse(parts[2], out int f) ? f : 0;
                list.Add((node, range * 100, parts[1].Trim().ToUpperInvariant(), flag));
            }
            return list;
        }

        public static (int id, string name) DecodeStepSound(byte[] d)
        {
            foreach (var (tag, body, length) in BwdChunks.Enumerate(d))
                if (tag == "ASND" && length >= 2)
                    return (BitConverter.ToInt16(d, body), BwdChunks.ReadString(d, body + 2, Math.Max(0, length - 2)));
            return (-1, null);
        }

        public static List<int> DecodeThings(byte[] d)
        {
            var nodes = new List<int>();
            int pos = 0xC;
            while (pos + 8 <= d.Length)
            {
                string tag = Encoding.ASCII.GetString(d, pos, 4).TrimEnd('\0');
                int length = BitConverter.ToInt32(d, pos + 4);
                if (length < 8 || pos + length > d.Length) break;
                if (tag == "THNG" && length >= 10) nodes.Add(BitConverter.ToInt16(d, pos + 8));
                pos += length;
            }
            return nodes;
        }

        /// <summary>
        /// POFO chunks (u16 node, u16 section; loader in 0x10070e92 stores the node in mech +0xd98[section]): the muzzle
        /// of the weapons in that MEK section. Weapons_Update (0x1006bc57) sets it as the shot origin from the weapon
        /// slot's section (+0x28) before each shot.
        /// </summary>
        public static List<(int node, int section)> DecodeMuzzles(byte[] d)
        {
            var list = new List<(int, int)>();
            int pos = 0xC;
            while (pos + 8 <= d.Length)
            {
                string tag = Encoding.ASCII.GetString(d, pos, 4).TrimEnd('\0');
                int length = BitConverter.ToInt32(d, pos + 4);
                if (length < 8 || pos + length > d.Length) break;
                if (tag == "POFO" && length >= 12) list.Add((BitConverter.ToInt16(d, pos + 8), BitConverter.ToInt16(d, pos + 10)));
                pos += length;
            }
            return list;
        }

        /// <summary>
        /// BTHG chunks of the shot file (MERCSHT1; i16 node, i16 projectile type; 0x10070e92): up to 255 projectile
        /// slots, each with the node's POLY model. Returns the first node of each type.
        /// </summary>
        public static Dictionary<int, int> DecodeShotNodes(byte[] d)
        {
            var byType = new Dictionary<int, int>();
            foreach (var slot in DecodeShotSlots(d))
                if (!byType.ContainsKey(slot.type)) byType[slot.type] = slot.node;
            return byType;
        }

        /// <summary>World_Load 0x10070e92 BTHG branch: preserve every slot in file order, up to 255.</summary>
        public static List<(int node, int type)> DecodeShotSlots(byte[] d)
        {
            var slots = new List<(int, int)>();
            int pos = 0xC;
            while (pos + 8 <= d.Length)
            {
                string tag = Encoding.ASCII.GetString(d, pos, 4).TrimEnd('\0');
                int length = BitConverter.ToInt32(d, pos + 4);
                if (length < 8 || pos + length > d.Length) break;
                if (tag == "BTHG" && length >= 12 && slots.Count < 255)
                {
                    int node = BitConverter.ToInt16(d, pos + 8), type = BitConverter.ToInt16(d, pos + 10);
                    slots.Add((node, type));
                }
                pos += length;
            }
            return slots;
        }

        /// <summary>
        /// XPLO chunks of the shot file (i16 node, i16 texture key, i16 effect type 0..35, others become 3; 0x10070e92):
        /// up to 255 explosion slots (0x102434a0), each with the node's POLY model and its own animated texture key.
        /// </summary>
        public static List<(int node, int key, int effect)> DecodeExplosions(byte[] d)
        {
            var list = new List<(int, int, int)>();
            int pos = 0xC;
            while (pos + 8 <= d.Length)
            {
                string tag = Encoding.ASCII.GetString(d, pos, 4).TrimEnd('\0');
                int length = BitConverter.ToInt32(d, pos + 4);
                if (length < 8 || pos + length > d.Length) break;
                if (tag == "XPLO" && length >= 14)
                {
                    int effect = BitConverter.ToInt16(d, pos + 12);
                    if (effect < 0 || effect > 0x23) effect = 3;
                    list.Add((BitConverter.ToInt16(d, pos + 8), BitConverter.ToInt16(d, pos + 10), effect));
                }
                pos += length;
            }
            return list;
        }

        /// <summary>
        /// OBJL chunks (u16 node, u16 MEK section + 1; 0x1001f074): which body section a node belongs to (hit map, and the
        /// node damage level that Material_Shade 0x1001afb0 colours in material mode 3).
        /// </summary>
        public static List<(int node, int section)> DecodeHitSections(byte[] d)
        {
            var list = new List<(int, int)>();
            int pos = 0xC;
            while (pos + 8 <= d.Length)
            {
                string tag = Encoding.ASCII.GetString(d, pos, 4).TrimEnd('\0');
                int length = BitConverter.ToInt32(d, pos + 4);
                if (length < 8 || pos + length > d.Length) break;
                if (tag == "OBJL" && length >= 12) list.Add((BitConverter.ToInt16(d, pos + 8), BitConverter.ToInt16(d, pos + 10) - 1));
                pos += length;
            }
            return list;
        }

        /// <summary>EYEO chunk: i16 node of the pilot's eye (cockpit camera), a mount under the torso node.</summary>
        public static int DecodeEye(byte[] d)
        {
            int pos = 0xC;
            while (pos + 8 <= d.Length)
            {
                string tag = Encoding.ASCII.GetString(d, pos, 4).TrimEnd((char)0);
                int length = BitConverter.ToInt32(d, pos + 4);
                if (length < 8 || pos + length > d.Length) break;
                if (tag == "EYEO" && length >= 10) return BitConverter.ToInt16(d, pos + 8);
                pos += length;
            }
            return -1;
        }

        /// <summary>MGDF chunk: u16 id, name of the MGEO resource (7 ints; [5] / 65536 = torso twist limit in degrees).</summary>
        public static string DecodeGeometryName(byte[] d)
        {
            int pos = 0xC;
            while (pos + 8 <= d.Length)
            {
                string tag = Encoding.ASCII.GetString(d, pos, 4).TrimEnd('\0');
                int length = BitConverter.ToInt32(d, pos + 4);
                if (length < 8 || pos + length > d.Length) break;
                if (tag == "MGDF" && length > 10) return ReadString(d, pos + 10, pos + length);
                pos += length;
            }
            return null;
        }

        static string ReadString(byte[] d, int start, int end)
        {
            int n = start;
            while (n < end && d[n] != 0) n++;
            return Encoding.ASCII.GetString(d, start, n - start);
        }
    }
}
