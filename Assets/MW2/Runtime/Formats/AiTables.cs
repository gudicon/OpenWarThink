using System;
using System.Collections.Generic;
using System.IO;

namespace MW2.Formats
{
    /// <summary>Pilot states of mercsw.dll (names at 0x1008ad38, per-state update functions at 0x1008b580).</summary>
    public enum AiState { Idle, Avoid, Target, Attack, Flee, Follow, Recon, Patrol, GoDirect, Rest, Silent, Shutdown, Dead }

    /// <summary>
    /// One reaction rule of an AIT table (12 bytes): +0 u16 event, +2 i16 A (who: selector), +4 i16 B (distance),
    /// +6 u8 check, +7 i16 new state, +9 u16 new target (selector), +11 pad. Evaluated by 0x1000854b.
    /// </summary>
    [Serializable]
    public sealed class AiRule
    {
        // Events: condition functions at 0x1008b5b8 + event * 4.
        public const int Nearer = 1, Farther = 2, Arrived = 3, Always = 4, Never = 5, Destroyed = 6, Detect = 7;
        // Checks run when the event fires (0x1008b5d8): 0 accept, 1 clear the state stack, 2 push the current state,
        // 3 pop the state stack (the rule's own state change only happens when the stack was empty), 4 tell the leader.
        public const int Accept = 0, ClearStack = 1, Push = 2, Pop = 3, TellLeader = 4;

        public int Event;
        public int A;
        public int B;
        public int Check;
        public int NewState;
        public int Target;
    }

    [Serializable]
    public sealed class AiStateRules
    {
        public int State;
        public List<AiRule> Rules = new List<AiRule>();
    }

    /// <summary>
    /// AIT resource: u16 n, n x (i16 state, u16 rule count), then 12-byte rules for the listed states.
    /// The pilot builds its active rule list at 0x100086f6. Some resources contain a trailing unused payload.
    /// </summary>
    [Serializable]
    public sealed class AiTable
    {
        public string Name;
        public List<AiStateRules> States = new List<AiStateRules>();

        public static AiTable Decode(string name, byte[] d)
        {
            if (d == null || d.Length < 2) throw new InvalidDataException($"AIT {name}: missing header");
            var t = new AiTable { Name = name };
            int n = BitConverter.ToUInt16(d, 0);
            int pos = 2 + n * 4;
            if (pos > d.Length) throw new InvalidDataException($"AIT {name}: state directory exceeds resource");
            for (int i = 0; i < n; i++)
            {
                var s = new AiStateRules { State = BitConverter.ToInt16(d, 2 + i * 4) };
                int count = BitConverter.ToUInt16(d, 4 + i * 4);
                if (count > (d.Length - pos) / 12)
                    throw new InvalidDataException($"AIT {name}: rules for state {s.State} exceed resource");
                for (int r = 0; r < count; r++, pos += 12)
                    s.Rules.Add(new AiRule
                    {
                        Event = BitConverter.ToUInt16(d, pos),
                        A = BitConverter.ToUInt16(d, pos + 2),
                        B = BitConverter.ToInt16(d, pos + 4),
                        Check = d[pos + 6],
                        NewState = BitConverter.ToInt16(d, pos + 7),
                        Target = BitConverter.ToUInt16(d, pos + 9)
                    });
                t.States.Add(s);
            }
            return t;
        }

        /// <summary>Whether the table lists <paramref name="state"/> (0x1000aeea: a unit only enters states its tables know).</summary>
        public bool Lists(int state) => States.Exists(s => s.State == state);
    }

    /// <summary>
    /// Pilot style (+0x189 of the unit, from GPS AI parameter 6): records of 7 ints at 0x1008b2f8, read by 0x100081c1.
    /// </summary>
    public readonly struct PilotStyle
    {
        public readonly int AttackRange;    // +0x1fe, model units: maneuver 0 closes in to this distance
        public readonly int GroupFire;      // +0x192: 0 picks one weapon at a time (0x10005864), else fires groups (0x10005a23)
        public readonly int HeatLimit;      // +0x1b0: no shot that would take the heat to this level
        public readonly int TauntPercent;   // +0x1b4
        public readonly int AimLow;         // +0x1bc: aims at the target's legs (0x10067128)
        public readonly int LeadTarget;     // +0x1c0: leads moving targets (0x100053af instead of 0x10005633)

        public PilotStyle(int range, int group, int heat, int taunt, int low, int lead)
        {
            AttackRange = range; GroupFire = group; HeatLimit = heat; TauntPercent = taunt; AimLow = low; LeadTarget = lead;
        }
    }

    public static class AiTables
    {
        /// <summary>Ai_LockOnPlayer 0x10005c42: RandomInt(100) is below pilot +0x1b4.</summary>
        public static bool EnemyFireWarns(int styleId, int roll) =>
            roll < Styles[Math.Max(0, Math.Min(styleId, Styles.Length - 1))].TauntPercent;

        public static readonly string[] Names = { "DEFLT", "FDEFEND", "FDESTROY", "FLEAVE", "FRECON", "LDEFEND", "LDESTROY", "LLEAVE", "LRECON" };

        /// <summary>
        /// Rule table for an order (0x1000bd9f, data at 0x1008b400; identical for every unit class): index = lowest set bit of the
        /// order mask + 1 (row 0 is the default table), value = (AIT id for the team leader, AIT id for the other members).
        /// This is the class-1 table; 0x1000bd9f selects different mappings for other unit classes.
        /// </summary>
        static readonly int[,] ByOrderBit =
        {
            { 1, 1 }, { 7, 3 }, { 7, 3 }, { 6, 2 }, { 9, 5 }, { 1, 1 }, { 9, 5 }, { 1, 1 },
            { 1, 1 }, { 9, 5 }, { 1, 1 }, { 1, 1 }, { 1, 1 }, { 1, 1 }, { 8, 4 }, { 0, 0 }
        };

        public static int TableFor(int orderBit, bool leader) =>
            orderBit < 0 || orderBit >= 15 ? 0 : ByOrderBit[orderBit + 1, leader ? 0 : 1];

        /// <summary>Pilot styles 0..7 (0x1008b2f8); index 0 has no maneuver table of its own and uses the class table.</summary>
        public static readonly PilotStyle[] Styles =
        {
            new PilotStyle(0, 0, 75, 25, 0, 0),
            new PilotStyle(20000, 0, 75, 25, 0, 0),
            new PilotStyle(20000, 1, 100, 15, 0, 0),
            new PilotStyle(15000, 2, 75, 25, 1, 0),
            new PilotStyle(15000, 2, 75, 60, 0, 0),
            new PilotStyle(25000, 0, 75, 100, 0, 0),
            new PilotStyle(15000, 0, 80, 85, 0, 1),
            new PilotStyle(25000, 0, 75, 25, 0, 0),
        };

        /// <summary>
        /// Mech maneuver chain (0x1009fef0, header 0x100a0470 + 1 * 8, used by every style): maneuver → possible next ones;
        /// with no previous maneuver the first five entries are drawn from (header 12, 7).
        /// </summary>
        public static readonly (int id, int[] next)[] MechManeuvers =
        {
            (0, new[] { 0, 1, 2, 3, 4, 4 }),
            (1, new[] { 0, 1, 2, 3, 4 }),
            (2, new[] { 7, 8, 4 }),
            (3, new[] { 7, 8 }),
            (4, new[] { 5, 5, 8 }),
            (5, new[] { 7, 8 }),
            (6, new[] { 6 }),
            (7, new[] { 0, 1, 8 }),
            (8, new[] { 0, 1, 2, 3, 4 }),
            (9, new[] { 0, 8, 7 }),
            (10, new[] { 7, 7, 4 }),
            (11, new[] { 3, 1 }),
        };
        public const int FirstManeuvers = 12 - 7;

        /// <summary>
        /// Aircraft (GP types 2 and 8) maneuver chain: table header 0x100a0470 + type * 8 -> 0x1009fff8 (4 entries, drawn first
        /// from 4 - 2 = 2). Every other non-mech type (3..7) uses 0x1009ffc8: maneuver 0 only (AI_SelectMechManeuver 0x10062aa8).
        /// </summary>
        public static readonly (int id, int[] next)[] AircraftManeuvers =
        {
            (2, new[] { 7, 13 }), (3, new[] { 7, 13 }), (7, new[] { 13, 2, 3 }), (13, new[] { 13, 2, 3 }),
        };
        public const int FirstAircraftManeuvers = 4 - 2;
        public static readonly (int id, int[] next)[] GroundManeuvers = { (0, new[] { 0 }) };

        /// <summary>The nine AIT tables (index = resource id; 0 unused).</summary>
        public static AiTable[] Load(PrjArchive prj)
        {
            var tables = new AiTable[Names.Length + 1];
            for (int i = 0; i < Names.Length; i++)
                if (prj.TryFind("AIT", Names[i], out var e)) tables[i + 1] = AiTable.Decode(Names[i], prj.Read(e));
            return tables;
        }

        static AiTable[] _cache;

        /// <summary>Tables read once from the game's MW2.PRJ (used when the scene was built without them).</summary>
        public static AiTable[] FromGame(string gameFolder)
        {
            if (_cache != null) return _cache;
            string path = Path.Combine(gameFolder, "MW2.PRJ");
            if (!File.Exists(path)) return null;
            using (var prj = new PrjArchive(path)) _cache = Load(prj);
            return _cache;
        }
    }
}
