using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace MW2.Formats
{
    /// <summary>Chunk walker shared by all BWD readers: chunks start at 0xC, each is tag[4] + u32 length (incl. header).</summary>
    public static class BwdChunks
    {
        public static IEnumerable<(string tag, int body, int length)> Enumerate(byte[] d)
        {
            int pos = 0xC;
            while (pos + 8 <= d.Length)
            {
                string tag = Encoding.ASCII.GetString(d, pos, 4).TrimEnd('\0');
                int length = BitConverter.ToInt32(d, pos + 4);
                if (length < 8 || pos + length > d.Length) yield break;
                yield return (tag, pos + 8, length - 8);
                pos += length;
            }
        }

        public static string ReadString(byte[] d, int start, int max)
        {
            int end = start;
            while (end < start + max && end < d.Length && d[end] != 0) end++;
            return Encoding.ASCII.GetString(d, start, end - start);
        }
    }

    /// <summary>
    /// WTHR chunk (World_LoadChunks 0x10070e92 -> FUN_1006eb46 0x1006eb46): i32 type (2 snow, else rain), i32 flags; with flag 1
    /// the 32 palette entries at +8, flag 2 seven parameters at +0x88, flag 4 the rate (+0xb0, particles per second, default
    /// 14), flag 8 the velocity (+0xa4, model units per second; default (0, -5000, 0) for snow, (0, -10000, 0) for rain);
    /// flags 0x10000 << k replace emitter k's own record (FUN_1006e88b, 0xf4 bytes at +0xb4 + k * 0xf4), which no WTHR in
    /// MW2.PRJ sets. All nine emitters take the same values.
    /// </summary>
    public sealed class Weather
    {
        public int Type;
        public int Flags;
        public int[] Palette = new int[32];
        public float Rate = 14f;
        public Vector3 Velocity;
        public int EmitterFlags;
    }

    /// <summary>
    /// MLIT chunk (World_LoadChunks 0x10070e92 at 0x10073701): a point light from the light pool (FUN_1002e843 0x1002e843)
    /// set with flags 0x107 (FUN_1002e8af 0x1002e8af): position = i32 x, y, z at +0 (model units, not moved by the block),
    /// cube radius (+0x30 of the light) = i32 at +0xc, colour (+0x44) 0.6, 0.6, 0.6, word +0x10 = the i32 at +0x14 (past
    /// the 16-byte record of MCC1SMO1, so the next chunk's tag; unused by the shading).
    /// </summary>
    public struct MissionLight
    {
        public Vector3 Position;    // metres
        public float Radius;        // metres
        public Color Color;
    }

    public struct NavPoint
    {
        public string File;
        public string Name;
        public Vector3 Position;    // metres, MW2 axes
        public float Heading;       // degrees
        public int Type;            // +0x10 (0 start point / misc, 1 waypoint)
        public int Flags;           // +0x12 (nav record +0x24): bit 1 hidden from the player, 0x20 visited
        public int Team;            // +0x16 (nav record +0xc): team the nav point belongs to (ST11 -> 1 ... ST51 -> 5)
        public float Radius;        // +0x18 u16 * 100 units: arrival radius in metres (checked at 0x1003a449 path, line using +0xe8)
    }

    /// <summary>
    /// One order of a team's MTBL table (entry 0x97 bytes at +0x26, MTBL processor 0x1003fca0):
    /// +0 u32 order type mask, +4 'V' vital, +0xA 8 conditions (type 'I'/'C'/'S'/'F', a, b), +0x2A i32 time limit (s),
    /// +0x2E kind, +0x2F 'M'/'O', +0x34/+0x3F start/finish radio message (SNDS), +0x4A target BWD file, +0x57 objective text.
    /// </summary>
    [Serializable]
    public sealed class MissionOrder
    {
        /// <summary>Objective_Evaluate 0x1004154a: +0x6f &lt; whole seconds now − +0x67; equality is still active.</summary>
        public static bool TimedOut(int limitSeconds, int startedSecond, int nowSecond) =>
            limitSeconds >= 0 && startedSecond >= 0 && limitSeconds < nowSecond - startedSecond;

        public const uint StartPoint = 0x10;
        public const uint Destroy = 0x2;
        public const uint Protect = 0x4;
        public const uint GotoNav = 0x100;
        public const uint HoldWith = 0x200;
        public const uint Hold = 0x400;
        public const uint LeaveArea = 0x2000;

        public uint Mask;
        public bool Vital;
        public bool Mandatory;      // +0x2F 'M': decides mission success/failure (Mission_CheckObjectives 0x100421e6)
        public bool AllConditions;  // +6 = 1: all conditions must hold (groups like "all enemy lances destroyed")
        public string ConditionTypes = "";     // C finished, S succeeded, F failed; I/unknown terminates the source list (0x1003fca0)
        public int[] ConditionA = new int[0];
        public int[] ConditionB = new int[0];

        /// <summary>FUN_10041469 0x10041469 / FUN_100413bf 0x100413bf: recheck every unfinished
        /// entry on each pass. Source states 5/6 are terminal; 8 is neither C nor F. I (code 0)
        /// ends the condition list; any nonzero combine byte means AND, an empty OR is false.</summary>
        public bool CanEvaluate(int sourceState, Func<int, int, int> stateOf)
        {
            if (sourceState == 5 || sourceState == 6) return false;
            if (Mask == StartPoint) return true;
            for (int c = 0; c < ConditionTypes.Length && c < 8; c++)
            {
                char kind = ConditionTypes[c];
                if (kind != 'C' && kind != 'S' && kind != 'F') break;
                int state = stateOf(ConditionB[c] & 0xf, ConditionA[c]);
                bool holds = kind == 'C' ? state == 5 || state == 6 : kind == 'S' ? state == 5 : state == 6;
                if (!AllConditions && holds) return true;
                if (AllConditions && !holds) return false;
            }
            return AllConditions;
        }
        /// <summary>+0x2A: time limit in seconds from activation (-1 none), state +0x6f (Mtbl_Load 0x1003fca0).</summary>
        public int TimeLimit = -1;
        /// <summary>+0x53 / +0x55: team and entry acted on by the group types 0x10000..0x400000 (state +0xa9 / +0xab).</summary>
        public int TargetTeam = -1, TargetOrder = -1;
        public int Kind;            // +0x2E: objectives screen group (0x1006842b): 1 Target, 2 Bonus, 4/0 Bounty, 8 Return
        /// <summary>+0x30 (chunk +0x56): members FUN_1000c4c4 assigns to each target, 0 = all (record +0x79).</summary>
        public int Assignees;
        /// <summary>+0x31 (chunk +0x57): pilot flag source of FUN_1000bd7e (1 -> 2, 2 -> 1, else 0; record +0x7d).</summary>
        public int PilotMode;
        public string Target;
        public string Text;
        public string StartSound;  // MTBL +0x34: played when this order succeeds (FUN_10040fbe 0x10040fbe)
        public string FinishSound; // MTBL +0x3F: played when this order fails
    }

    [Serializable]
    public sealed class TeamOrders
    {
        public int Team;
        public string SuccessSound; // MTBL header +0x12, FUN_1004127f (0x1004127f)
        public string FailureSound; // MTBL header +0x1D
        public int TimeLimit;       // MTBL header +0xC, seconds (Mtbl_Load 0x1003fca0 -> DAT_1020078c); 0 = none
        public List<MissionOrder> Orders = new List<MissionOrder>();
    }

    /// <summary>Objective_Evaluate 0x1004154a, type 4: protect-target state transitions.</summary>
    public static class ObjectiveProtection
    {
        /// <summary>Original state bytes: 3 active, 5 success, 6 failure, 8 failure of another team.</summary>
        public static int State(bool timedOut, bool allStand, bool allDown, bool playerTeam)
        {
            if (timedOut) return allStand ? 5 : 6;
            if (allDown) return 6;
            if (!allStand) return playerTeam ? 6 : 8;
            return 3;
        }
    }

    public struct Spawn
    {
        public string File;
        public int ObjectiveMask;    // GPS body +0x18, normalized by FUN_10070e60 (0x10070e60)
        public string Model;        // mech/vehicle BWD
        public string Loadout;      // MEK
        public string DisplayName;  // +0x2E -> GP +0x110, the target name (Hud_DrawTargetText 0x10065990)
        /// <summary>+0x44 -> GP +0x126: the text shown when the unit is inspected with I (0x10065990), e.g. a pilot or crew.</summary>
        public string InspectText;
        /// <summary>u16 at body +0x1A -> GP +0x14 flags (World_LoadChunks 0x100733ca): 0x400 targetable / on radar, 0x800 hidden.</summary>
        public int Flags;
        public int AiStyle;         // GPS parameter 6: u16 at body +0x14, copied to pilot +0x168 (0x10070e92 -> 0x10007f35)
        public int AiFireChance;    // GPS parameter 0: the byte at body +0x08 -> pilot +0x15c -> fire check divisor +0x186 (0x10007f35)
        public int PilotVoice;      // GPS body +0x16 -> pilot +0x16a -> voice table index +0x18a (0x10007f35)
        public int AiNearRange;     // GPS parameter 1: body +0x0A, metres after *100 model units (0x10007f35)
        public int AiFarRange;      // GPS parameter 2: body +0x0C
        public int AiDetectRange;   // GPS parameter 3: body +0x0E
        public int PilotSkill;      // GPS parameter 4: the byte at body +0x10 -> pilot +0x164 -> +0x187 (0 -> 1, clamped 1..6)
        public int PilotTemper;     // GPS parameter 5: the byte at body +0x12 -> +0x166 -> +0x188 (FUN_100081c1: 0 / 1 / 2)
        public int Team;            // +4, 0..15 (GPS handler in 0x10070e92)
        public bool Leader;         // +5 == 1: becomes the team leader (0x1002a334)
        public bool Player;         // +6 == 0: the player's own unit
    }

    /// <summary>
    /// GT chunk (68 bytes, handler in 0x10070e92): a destructible scenery object. +0 i16 intact node, +2 i16 base node
    /// (shadow/rubble parent), +0x10 i16 damage it takes, +0x12 u16 objective mask (matched against MTBL order masks,
    /// e.g. 2 = "destroy", 4 = "protect"), +0x14 u16 flags (0x8000 = debris), +0x18 name.
    /// Loaded at 0x10072487 from the chunk header: flags -> GT +0 (0x400 targetable / on radar, 0x800 never a player
    /// target, 0x1000 targetable at any range; 0x4 is set when destroyed), name -> GT +0x14, contents (+0x2E) -> GT +0x2A,
    /// the AFFL of the same BWD -> GT +0xC (-1 without one), read by Gt_Side 0x1002a7dc.
    /// </summary>
    [Serializable]
    public sealed class Destructible
    {
        public string File;
        public int Node;
        public int BaseNode;
        public int Strength;
        public int ObjectiveMask;
        public int Flags;
        public string Name;
        public string Contents;     // +0x2E, shown when inspected with I (0x10065990)
        public int Affiliation = -1;
    }

    /// <summary>FTBL body: name[16], then 0..8 slots of i32 x, z (model units) and heading (16.16 degrees).</summary>
    public sealed class Formation
    {
        public string Name;
        public readonly List<(Vector3 offset, float heading)> Slots = new List<(Vector3, float)>();

        // Team_SetLeader 0x1002a334 subtracts the leader's slot position from all slots.
        // Team_FormationSlot 0x1002a57c uses the NAV heading for the leader and
        // the FTBL heading (without adding the leader heading) for each follower.
        public Vector3 InitialOffset(int slot) => (slot < Slots.Count ? Slots[slot].offset : Vector3.zero) - Slots[0].offset;
        public float InitialHeading(int slot, float navHeading) => slot == 0 ? navHeading : slot < Slots.Count ? Slots[slot].heading : 0f;
    }

    /// <summary>
    /// A mission is a tree of BWD files rooted at "&lt;prefix&gt;SCN1", linked by INCL chunks (u16 id, name).
    /// PLT1 holds the planet (PLNT, CLIM, LITE...) and includes the palette group (PALG: 20 PAL ids, day/dusk/night/IR...),
    /// the material map (MAP1) and the sky dome; WLD1 includes the areas whose OBJ nodes place the scenery;
    /// NAVP chunks are nav points (ST* start points, NAV* waypoints); GPS chunks spawn units.
    /// </summary>
    public sealed class Mission
    {
        /// <summary>FUN_10070e60 0x10070e60 expands related MTBL target-type bits before binding.</summary>
        public static int NormalizeObjectiveMask(int mask)
        {
            if ((mask & 0x730) != 0) mask |= 0x730;
            if ((mask & 3) != 0) mask |= 3;
            return mask;
        }

        public string Prefix;
        public int[] PaletteIds;
        /// <summary>PLNT +0xc: seconds per day (DAT_10090150); the .data default is 86400.</summary>
        public int DayLength = 86400;
        /// <summary>INIT +8: the period the mission starts in (0 dawn, 1 day, 2 dusk, 3 night), -1 without INIT.</summary>
        public int StartPeriod = -1;

        /// <summary>Palette mode and fade of each period, table 0x10090120: dawn 4, day 0, dusk 4, night 8.</summary>
        public static readonly int[] PeriodPaletteMode = { 4, 0, 4, 8 };

        /// <summary>
        /// Period at <paramref name="second"/> seconds into the mission (FirstEnvironment 0x1001f4f0, FUN_1001f584 0x1001f584):
        /// thresholds (day / 24) * 5, 7, 17, 19; the clock starts at the threshold of the INIT period (without INIT at
        /// 43200, the .data value of DAT_10090158); the period is the last threshold reached, else 3.
        /// INIT periods outside 0..3 (SPA1: 250) read past the threshold array in the original; here they start at 43200.
        /// </summary>
        public int PeriodAt(int second) => PeriodAt(DayLength, StartPeriod, second);

        public static int PeriodAt(int dayLength, int startPeriod, int second)
        {
            int DayLength = dayLength, StartPeriod = startPeriod;
            int unit = DayLength / 24;
            int[] thresholds = { unit * 5, unit * 7, unit * 17, unit * 19 };
            int start = StartPeriod >= 0 && StartPeriod < 4 ? thresholds[StartPeriod] : 43200;
            int t = (int)(((long)second + start) % DayLength);
            int period = 3;
            for (int k = 0; k < 4; k++) if (thresholds[k] <= t) period = k;
            return period;
        }

        /// <summary>PALG entry the scene is drawn with at the start (FUN_1001f6a1: PALG[mode of the period]).</summary>
        public int StartPaletteId => PaletteIds == null || PaletteIds.Length == 0 ? -1
            : PaletteIds[Math.Min(PeriodPaletteMode[PeriodAt(0)], PaletteIds.Length - 1)];
        public readonly List<(string file, List<BwdNode> nodes)> Areas = new List<(string, List<BwdNode>)>();
        public readonly List<(string file, List<BwdNode> nodes)> Sky = new List<(string, List<BwdNode>)>();

        /// <summary>
        /// Scene blocks (World_LoadChunks 0x10070e92): BLKX stores 9 i32 for the next block (scale x, y, z — unused —,
        /// rotation x, y, z in 16.16 degrees, translation x, y, z in model units; FUN_1005ef87 0x1005ef87, defaults
        /// 0x1009fc28 = 1, 1, 1, 0...); BLK (FUN_1005ea6e 0x1005ea6e) opens a block from its box (i32 min x, y, z,
        /// size x, y, z; a negative size moves the minimum) with the matrix T(centre) T(translation) R T(-centre), then
        /// multiplied by the matrix of every enclosing block up the chain, and resets the pending values; ENDB
        /// (FUN_1005ef46 0x1005ef46) closes it. OBJ records without a parent take the current block's matrix when
        /// their model loads (Scene_LoadObjectModel 0x1005f729), and NAVP positions inside a block are moved by it
        /// (FUN_1005ef9f 0x1005ef9f). E.g. BET2WLD1 moves the city area bet2are4 by (-94, 0, -290) m. Every BLKX in
        /// MW2.PRJ rotates about Y only, so a block is kept as a yaw and an offset (metres).
        /// </summary>
        public readonly List<(float yaw, Vector3 offset, int parent)> Blocks = new List<(float, Vector3, int)>();
        readonly int[] _pendingBlock = { 1, 1, 1, 0, 0, 0, 0, 0, 0 };
        readonly List<int> _blockStack = new List<int>();
        int _currentBlock = -1;

        /// <summary>Applies block <paramref name="index"/>'s matrix to a point (metres).</summary>
        public Vector3 BlockPoint(int index, Vector3 p)
        {
            if (index < 0 || index >= Blocks.Count) return p;
            var (yaw, offset, _) = Blocks[index];
            return RotateY(p, yaw) + offset;
        }

        static Vector3 RotateY(Vector3 p, float degrees)
        {
            double a = degrees * Math.PI / 180.0;
            float c = (float)Math.Cos(a), s = (float)Math.Sin(a);
            return new Vector3(c * p.x + s * p.z, p.y, -s * p.x + c * p.z);
        }

        void OpenBlock(byte[] d, int b)
        {
            var min = new float[3];
            var size = new float[3];
            for (int k = 0; k < 3; k++)
            {
                min[k] = BitConverter.ToInt32(d, b + k * 4);
                size[k] = BitConverter.ToInt32(d, b + 12 + k * 4);
                if (size[k] < 0) { min[k] += size[k]; size[k] = -size[k]; }
            }
            var centre = new Vector3((size[0] + min[0] + min[0]) / 2f, (size[1] + min[1] + min[1]) / 2f,
                                     (size[2] + min[2] + min[2]) / 2f) * PolyModel.UnitsToMeters;
            var move = new Vector3(_pendingBlock[6], _pendingBlock[7], _pendingBlock[8]) * PolyModel.UnitsToMeters;
            float yaw = _pendingBlock[4] / 65536f;
            // p -> centre + move + R (p - centre)
            var offset = centre + move - RotateY(centre, yaw);
            int parent = _blockStack.Count > 0 ? _blockStack[_blockStack.Count - 1] : -1;
            // The chain loop of FUN_1005ea6e multiplies by each enclosing block's full matrix in turn.
            for (int p = parent; p >= 0; p = Blocks[p].parent)
            {
                offset = RotateY(offset, Blocks[p].yaw) + Blocks[p].offset;
                yaw += Blocks[p].yaw;
            }
            Blocks.Add((yaw, offset, parent));
            _currentBlock = Blocks.Count - 1;
            _blockStack.Add(_currentBlock);
            int[] defaults = { 1, 1, 1, 0, 0, 0, 0, 0, 0 };
            defaults.CopyTo(_pendingBlock, 0);
        }

        void CloseBlock()
        {
            if (_blockStack.Count > 0) _blockStack.RemoveAt(_blockStack.Count - 1);
            _currentBlock = _blockStack.Count > 0 ? _blockStack[_blockStack.Count - 1] : -1;
        }
        public readonly List<NavPoint> NavPoints = new List<NavPoint>();
        public Weather Weather;
        /// <summary>PLNT +0x38 = 0: explosions move the sun to themselves (Effect_Start 0x10011ea8, DAT_1008eb78).</summary>
        public bool ExplosionFlashes = true;
        public readonly List<MissionLight> Lights = new List<MissionLight>();
        public readonly List<Spawn> Spawns = new List<Spawn>();
        public readonly List<Formation> Formations = new List<Formation>();
        /// <summary>STAR records or GRP names select each team's initial FTBL entry (0x10070145 / 0x1007025c).</summary>
        public readonly string[] InitialFormationNames = new string[16];
        /// <summary>
        /// Side of each team (DAT_1024f5d4 + team*0x3c, read by FUN_1002a7c1 0x1002a7c1): 0 the player's side, 1 enemy,
        /// 2 neutral. Only STAR sets it (0x10070145, i32 at record +4); a GRP-only world leaves the static zero. Network
        /// games (AfterWorldLoader 0x100739f9, DAT_100a6c3c) override it with 0 for the local team and 1 for the others.
        /// </summary>
        public readonly int[] TeamSides = new int[16];
        /// <summary>STAR +0 of each team (DAT_1024f5cc + team * 0x3c): the paint value of its units (Material_ApplyPaint 0x100745eb).</summary>
        public readonly int[] TeamPaint = new int[16];
        /// <summary>Side of a team, 0 for an index outside the 16 teams.</summary>
        public int SideOf(int team) => team >= 0 && team < TeamSides.Length ? TeamSides[team] : 0;
        /// <summary>
        /// DAT_1024f980[affiliation]: STAR (0x10070145) writes record +4 (side) at the index of record +0, the last record
        /// winning. The table is never cleared, so an affiliation no STAR record names keeps the static zero of a fresh
        /// start (a later mission in the same session would see the previous mission's value).
        /// </summary>
        public readonly int[] AffiliationSides = new int[8];
        /// <summary>Gt_Side 0x1002a7dc: 2 without an AFFL, else the side of its affiliation.</summary>
        public int SideOfAffiliation(int affiliation) =>
            affiliation < 0 ? 2 : affiliation < AffiliationSides.Length ? AffiliationSides[affiliation] : 0;
        public readonly List<TeamOrders> Teams = new List<TeamOrders>();
        public readonly List<Destructible> Destructibles = new List<Destructible>();
        /// <summary>PTBL tables in load order (FUN_1006fd5b 0x1006fd5b, at most 64).</summary>
        public readonly List<PathTable> Paths = new List<PathTable>();
        /// <summary>TSK class 5 path tasks (FUN_1000e89e 0x1000e89e), resolved against the PTBLs loaded before them.</summary>
        public readonly List<PathTask> PathTasks = new List<PathTask>();
        public readonly List<string> Missing = new List<string>();

        /// <summary>
        /// LITE chunk (World_LoadChunks 0x10070e92; offsets from the chunk data): i32 [2..4] go to the camera sun vector
        /// +0x1c..+0x24 (the camera struct is at 0x1009e828), 999999 = no sun; i16 at +0x14 = ambient level (camera +0x2a,
        /// DAT_10248aac); i16 at +0x16 = 1 for a direction, 0 for a sun position (camera +0x28). BET1: (-100000, 20000, 0), 45, 1.
        /// </summary>
        public bool HasLight;
        public Vector3 SunVector;
        public bool SunEnabled;
        public bool SunIsDirection;
        public int AmbientLevel;

        /// <summary>PLNT (World_LoadChunks 0x10070e92): gravity is i32 at body +0 in 16.16 units;
        /// temperature is i32 at body +0x1c. CLIM is a separate chunk and does not set these two values.</summary>
        public bool HasPlanet;
        public float Gravity = 0.0296f;
        public int Temperature;
        /// <summary>World_LoadChunks 0x10070e92: PLNT body +0x30 is tested for zero and stored in
        /// DAT_100a6bc4; a zero marks an atmosphere hostile to ejection.</summary>
        public bool HostileAtmosphere;
        /// <summary>CLIM body +4, 0x10070e92: maximum range of positioned sounds in metres; zero disables the cutoff.</summary>
        public short SoundRange;

        /// <summary>
        /// MUSI (World_LoadChunks 0x10070e92): i16 MUS resource id at body +0 (-1 = look up the name at +2, e.g. "track2").
        /// FUN_1000f9a7 (0x1000f9a7) reads that MUS text with "%d %d": the CD audio track and a MIDI sequence (0 in all
        /// shipped MUS resources; Music_MissionStart 1000faa4 then unconditionally sets MIDI to -1).
        /// -1 when the mission has no MUSI. Flag 4 does not enable an XMID score in this shipped version.
        /// </summary>
        public int CdTrack = -1;
        public string MusicName;

        /// <summary>Texture key (0x100 + material low byte for scenery) → CEL ids of its animation frames, from MAP1.</summary>
        public readonly Dictionary<int, List<int>> TextureKeys = new Dictionary<int, List<int>>();

        /// <summary>
        /// Texture animation of a key: BSEC (u16 key, i16 ticks per frame) sets the period (0x10002182) and mode 1 (loop);
        /// BMEN (u16 key, i16 mode) sets the mode (Texture_SetSpeed 0x10002094; 2 = play once).
        /// </summary>
        public readonly Dictionary<int, int> TexturePeriods = new Dictionary<int, int>();
        public readonly Dictionary<int, int> TextureModes = new Dictionary<int, int>();

        // Texture table state, mirroring mercsw.dll 0x10001de2 (BMPJ) / 0x10001efc (BMID).
        readonly List<List<int>> _textureRows = new List<List<int>>();
        bool _rowClosed = true;

        public static Mission Load(PrjArchive prj, string prefix)
        {
            var m = new Mission { Prefix = prefix.ToUpperInvariant() };
            m.Visit(prj, m.Prefix + "SCN1", null, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            return m;
        }

        void Visit(PrjArchive prj, string name, string includedFrom, HashSet<string> seen, bool fileFirst = false)
        {
            if (name.EndsWith(".bwd", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 4);
            if (!seen.Add(name)) return;
            if (!TryReadBwd(prj, name, out var d, fileFirst))
            {
                Missing.Add(name);
                return;
            }
            // SCRI is included directly by SCN1; its OBJ nodes carry TSK class 2 orbiting crowd sounds.
            bool isArea = (includedFrom != null && includedFrom.EndsWith("WLD1", StringComparison.OrdinalIgnoreCase)) ||
                          name.EndsWith("SCRI", StringComparison.OrdinalIgnoreCase);
            bool isSky = name.StartsWith("SKYDOM", StringComparison.OrdinalIgnoreCase);
            bool hasObj = false;
            int affiliation = -1;   // World_LoadChunks 0x10070e92 local_20, per BWD file
            var objBlocks = new List<int>();     // block of each OBJ record, in BwdSkeleton.Decode order

            foreach (var (tag, b, len) in BwdChunks.Enumerate(d))
            {
                switch (tag)
                {
                    case "PLNT" when !HasPlanet && len >= 0x34:
                        HasPlanet = true;
                        Gravity = 0.0296f * BitConverter.ToInt32(d, b) / 65536f;
                        Temperature = BitConverter.ToInt32(d, b + 0x1c);
                        HostileAtmosphere = BitConverter.ToInt32(d, b + 0x30) == 0;
                        // World_LoadChunks 0x10070e92: DAT_1008eb78 = (PLNT +0x38 == 0), explosion sun flashes allowed.
                        if (len >= 0x3c) ExplosionFlashes = BitConverter.ToInt32(d, b + 0x38) == 0;
                        // DAT_10090154 days per year (+8), DAT_10090150 seconds per day (+0xc, 0 -> 1).
                        DayLength = BitConverter.ToInt32(d, b + 0xc);
                        if (DayLength == 0) DayLength = 1;
                        break;
                    case "INIT" when StartPeriod == -1 && len >= 12:
                        // World_LoadChunks 0x10070e92 (single play): DAT_10090140 = INIT +8, the time-of-day period to start in.
                        StartPeriod = BitConverter.ToInt32(d, b + 8);
                        break;
                    case "WTHR" when len >= 0xb4:
                    {
                        int I(int k) => BitConverter.ToInt32(d, b + k * 4);
                        var w = new Weather { Type = I(0) == 2 ? 2 : 1, Flags = I(1) };
                        if ((w.Flags & 1) != 0) for (int k = 0; k < 32; k++) w.Palette[k] = I(2 + k);
                        if ((w.Flags & 4) != 0) w.Rate = I(0x2c);
                        w.Velocity = (w.Flags & 8) != 0 ? new Vector3(I(0x29), I(0x2a), I(0x2b))
                                                        : new Vector3(0, w.Type == 2 ? -5000f : -10000f, 0);
                        w.EmitterFlags = w.Flags & 0x1ff0000;
                        Weather = w;
                        break;
                    }
                    case "MLIT" when len >= 16:
                        Lights.Add(new MissionLight
                        {
                            Position = new Vector3(BitConverter.ToInt32(d, b), BitConverter.ToInt32(d, b + 4), BitConverter.ToInt32(d, b + 8))
                                       * PolyModel.UnitsToMeters,
                            Radius = BitConverter.ToInt32(d, b + 12) * PolyModel.UnitsToMeters,
                            Color = new Color(0.6f, 0.6f, 0.6f)
                        });
                        break;
                    case "CLIM" when len >= 6:
                        SoundRange = BitConverter.ToInt16(d, b + 4);
                        break;
                    case "MUSI" when len >= 2:
                    {
                        int id = BitConverter.ToInt16(d, b);
                        MusicName = BwdChunks.ReadString(d, b + 2, len - 2);
                        PrjArchive.Entry mus = default;
                        bool found = false;
                        foreach (var e in prj.Entries("MUS"))
                            if (id != -1 ? e.Id == id : string.Equals(e.Name, MusicName, StringComparison.OrdinalIgnoreCase))
                            { mus = e; found = true; break; }
                        CdTrack = -1;
                        if (found)
                        {
                            var text = Encoding.ASCII.GetString(prj.Read(mus)).Trim();
                            int end = 0;
                            while (end < text.Length && char.IsDigit(text[end])) end++;
                            if (end > 0) CdTrack = int.Parse(text.Substring(0, end));
                        }
                        break;
                    }
                    case "PTBL" when Paths.Count < PathTable.MaxTables:
                    {
                        var path = PathTable.Decode(d, b, len);
                        if (path != null) Paths.Add(path);
                        break;
                    }
                    case "TSK" when len > 6 && BitConverter.ToUInt16(d, b) == 5:
                    {
                        // FUN_1000e89e: "node;mode,rotate,path" -> sscanf "%[^,],%[^,],%[^,]", path name searched by _stricmp.
                        string spec = BwdChunks.ReadString(d, b + 6, len - 6);
                        int semi = spec.IndexOf(';');
                        if (semi < 0 || !int.TryParse(spec.Substring(0, semi), out int node)) break;
                        var words = spec.Substring(semi + 1).Split(',');
                        if (words.Length < 3) break;
                        string mode = words[0];
                        PathTasks.Add(new PathTask
                        {
                            File = name,
                            Node = node,
                            Mode = string.Equals(mode, "loop", StringComparison.OrdinalIgnoreCase) ? 0
                                : string.Equals(mode, "repeat", StringComparison.OrdinalIgnoreCase) ? 1 : 2,
                            Rotate = string.Equals(words[1], "rotate", StringComparison.OrdinalIgnoreCase),
                            PathName = words[2],
                            Path = Paths.Find(t => string.Equals(t.Name, words[2], StringComparison.OrdinalIgnoreCase))
                        });
                        break;
                    }
                    case "INCL":
                        // FUN_100078be: an include with id -2 names a file beside the game (userstar.bwd, written by
                        // the shell for the launch); other ids and -1 take the archive first.
                        Visit(prj, BwdChunks.ReadString(d, b + 2, len - 2), name, seen, BitConverter.ToInt16(d, b) == -2);
                        break;
                    case "LITE" when !HasLight && len >= 0x18:
                        HasLight = true;
                        SunVector = new Vector3(BitConverter.ToInt32(d, b + 8), BitConverter.ToInt32(d, b + 12), BitConverter.ToInt32(d, b + 16));
                        SunEnabled = SunVector != new Vector3(999999, 999999, 999999) && SunVector != new Vector3(888888, 888888, 888888);
                        AmbientLevel = BitConverter.ToInt16(d, b + 0x14);
                        SunIsDirection = BitConverter.ToInt16(d, b + 0x16) != 0;
                        break;

                    case "PALG" when PaletteIds == null:
                        PaletteIds = new int[len / 2];
                        for (int i = 0; i < PaletteIds.Length; i++) PaletteIds[i] = BitConverter.ToUInt16(d, b + i * 2);
                        break;
                    case "NAVP":
                        NavPoints.Add(new NavPoint
                        {
                            File = name,
                            Position = BlockPoint(_currentBlock, new Vector3(BitConverter.ToInt32(d, b), BitConverter.ToInt32(d, b + 4),
                                                                            BitConverter.ToInt32(d, b + 8)) * PolyModel.UnitsToMeters),
                            Heading = BitConverter.ToInt32(d, b + 12) / 65536f,
                            Type = BitConverter.ToInt16(d, b + 0x10),
                            Flags = BitConverter.ToInt16(d, b + 0x12),
                            Team = BitConverter.ToInt16(d, b + 0x16),
                            Radius = BitConverter.ToUInt16(d, b + 0x18),
                            Name = BwdChunks.ReadString(d, b + 0x1C, len - 0x1C)
                        });
                        break;
                    case "GPS":
                        Spawns.Add(new Spawn
                        {
                            File = name,
                            ObjectiveMask = NormalizeObjectiveMask(BitConverter.ToUInt16(d, b + 0x18)),
                            Model = BwdChunks.ReadString(d, b + 0x1C, 9),
                            Loadout = BwdChunks.ReadString(d, b + 0x25, 9),
                            DisplayName = BwdChunks.ReadString(d, b + 0x2E, 22),
                            InspectText = BwdChunks.ReadString(d, b + 0x44, len - 0x44),
                            Flags = BitConverter.ToUInt16(d, b + 0x1A),
                            AiStyle = BitConverter.ToUInt16(d, b + 0x14),
                            AiFireChance = (sbyte)d[b + 0x08],
                            PilotVoice = BitConverter.ToUInt16(d, b + 0x16),
                            AiNearRange = BitConverter.ToUInt16(d, b + 0x0A),
                            AiFarRange = BitConverter.ToUInt16(d, b + 0x0C),
                            AiDetectRange = BitConverter.ToUInt16(d, b + 0x0E),
                            PilotSkill = (sbyte)d[b + 0x10],
                            PilotTemper = (sbyte)d[b + 0x12],
                            Team = d[b + 4] > 15 ? 0 : d[b + 4],
                            Leader = d[b + 5] == 1,
                            Player = d[b + 6] == 0
                        });
                        break;
                    case "MTBL" when len >= 0x26 - 8:
                        Teams.Add(ReadOrders(d, b - 8, len + 8));
                        break;
                    case "AFFL" when len >= 4:
                        affiliation = BitConverter.ToInt32(d, b);
                        break;
                    case "STAR":
                        // FUN_10070145 0x10070145: records 0x18 bytes, i32 affiliation at +0, side at +4, formation name at +8.
                        for (int team = 0; team < Math.Min(16, len / 0x18); team++)
                        {
                            TeamSides[team] = BitConverter.ToInt32(d, b + team * 0x18 + 4);
                            int affl = BitConverter.ToInt32(d, b + team * 0x18);
                            TeamPaint[team] = affl;
                            if (affl >= 0 && affl < AffiliationSides.Length) AffiliationSides[affl] = TeamSides[team];
                            InitialFormationNames[team] = BwdChunks.ReadString(d, b + team * 0x18 + 8, 16);
                        }
                        break;
                    case "GRP":
                        // FUN_1007025c 0x1007025c: sixteen 0x11-byte formation names.
                        for (int team = 0; team < Math.Min(16, len / 0x11); team++)
                            InitialFormationNames[team] = BwdChunks.ReadString(d, b + team * 0x11, 0x11);
                        break;
                    // FUN_1006ff07 0x1006ff07: (chunk bytes - 24) / 12 slots, rejected above eight.
                    case "FTBL" when len >= 16 && (len - 16) / 12 <= 8:
                    {
                        var f = new Formation { Name = BwdChunks.ReadString(d, b, 16) };
                        for (int k = 0; k < (len - 16) / 12; k++)
                        {
                            int o = b + 16 + k * 12;
                            f.Slots.Add((new Vector3(BitConverter.ToInt32(d, o), 0, BitConverter.ToInt32(d, o + 4)) * PolyModel.UnitsToMeters,
                                         BitConverter.ToInt32(d, o + 8) / 65536f));
                        }
                        Formations.Add(f);
                        break;
                    }
                    case "OBJ":
                        hasObj = true;
                        if (len >= 52) objBlocks.Add(_currentBlock);
                        break;
                    case "BLKX" when len >= 36:
                        for (int k = 0; k < 9; k++) _pendingBlock[k] = BitConverter.ToInt32(d, b + k * 4);
                        break;
                    case "BLK" when len >= 24:
                        OpenBlock(d, b);
                        break;
                    case "ENDB":
                        CloseBlock();
                        break;
                    case "GT" when len >= 0x2E && isArea:
                    {
                        int flags = BitConverter.ToUInt16(d, b + 0x14);
                        if ((flags & 0x8000) != 0) break;       // debris pieces
                        Destructibles.Add(new Destructible
                        {
                            File = name,
                            Node = BitConverter.ToInt16(d, b),
                            BaseNode = BitConverter.ToInt16(d, b + 2),
                            Strength = BitConverter.ToInt16(d, b + 0x10),
                            ObjectiveMask = NormalizeObjectiveMask(BitConverter.ToUInt16(d, b + 0x12)),
                            Flags = flags,
                            Name = BwdChunks.ReadString(d, b + 0x18, 0x16),
                            Contents = BwdChunks.ReadString(d, b + 0x2E, 0x16),
                            Affiliation = affiliation
                        });
                        break;
                    }
                    case "BMPJ":
                    {
                        // u16 CEL id (-1: look up by name), CEL name. A BMPJ after a BMID starts a new row.
                        int cel = BitConverter.ToInt16(d, b);
                        if (cel == -1 && prj.TryFind("CEL", BwdChunks.ReadString(d, b + 2, len - 2), out var celEntry))
                            cel = celEntry.Id;
                        if (cel <= 0) break;
                        if (_rowClosed)
                        {
                            _textureRows.Add(new List<int>());
                            _rowClosed = false;
                        }
                        var row = _textureRows[_textureRows.Count - 1];
                        if (row.Count < 32) row.Add(cel);
                        break;
                    }
                    case "BSEC" when len >= 4:
                    {
                        int key = BitConverter.ToUInt16(d, b);
                        TexturePeriods[key] = BitConverter.ToInt16(d, b + 2);
                        TextureModes[key] = 1;
                        break;
                    }
                    case "BMEN" when len >= 4:
                        TextureModes[BitConverter.ToUInt16(d, b)] = BitConverter.ToInt16(d, b + 2);
                        break;
                    case "BMID":
                    {
                        // u16 texture key, i16 row (-1: the current row).
                        int key = BitConverter.ToUInt16(d, b);
                        int rowIndex = BitConverter.ToInt16(d, b + 2);
                        if (rowIndex < 0) rowIndex = _textureRows.Count - 1;
                        _rowClosed = true;
                        if (key < 0x200 && rowIndex >= 0 && rowIndex < _textureRows.Count)
                            TextureKeys[key] = _textureRows[rowIndex];
                        break;
                    }
                }
            }

            if (!hasObj || !(isArea || isSky)) return;
            var nodes = new List<BwdNode>();
            foreach (var rep in BwdSkeleton.Decode(d)) nodes.AddRange(rep);
            // Scene_LoadObjectModel 0x1005f729: a record without a parent takes its block's matrix.
            if (objBlocks.Count == nodes.Count)
                for (int i = 0; i < nodes.Count; i++)
                {
                    int block = objBlocks[i];
                    nodes[i].Block = block;
                    if (block < 0 || nodes[i].Parent >= 0) continue;
                    nodes[i].Position = BlockPoint(block, nodes[i].Position);
                    nodes[i].RotationDegrees = new Vector3(nodes[i].RotationDegrees.x, nodes[i].RotationDegrees.y + Blocks[block].yaw,
                                                           nodes[i].RotationDegrees.z);
                }
            (isSky ? Sky : Areas).Add((name, nodes));
        }

        /// <summary>MW2SHELL.DLL 0x1006ad2f maps RANn's randwld1 to ran{n}wld1.bwd.
        /// Read that generated file in place, then resolve other loose BWDs beside MW2.PRJ.</summary>
        /// <summary>Loose BWDs supplied in memory (the campaign launch's userstar.bwd) before the files beside MW2.PRJ.</summary>
        public static Func<string, byte[]> LooseBwd;

        bool TryReadBwd(PrjArchive prj, string name, out byte[] data, bool fileFirst = false)
        {
            data = null;
            if (name.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 || name.Contains("..")) return false;
            string folder = Path.GetDirectoryName(prj.FilePath);
            string loose = name;
            if (string.Equals(name, "randwld1", StringComparison.OrdinalIgnoreCase) &&
                Prefix.StartsWith("RAN", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(Prefix.Substring(3), out int number) && number > 0)
                loose = "ran" + number + "wld1";
            string path = Path.Combine(folder, loose + ".bwd");
            // A random mission's files are the shell's (in memory); without them the ones a run of the original left.
            if (Prefix.StartsWith("RAN", StringComparison.OrdinalIgnoreCase))
            {
                data = LooseBwd?.Invoke(name);
                if (data != null) return true;
            }
            // Generated RAN files are the mission selected by the shell; named missions use the archive first.
            if (Prefix.StartsWith("RAN", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
            {
                data = File.ReadAllBytes(path);
                return true;
            }
            // A file the campaign shell writes beside the game for this launch (userstar.bwd) comes from memory.
            if (fileFirst)
            {
                data = LooseBwd?.Invoke(name);
                if (data != null) return true;
                if (File.Exists(path))
                {
                    data = File.ReadAllBytes(path);
                    return true;
                }
            }
            if (prj.TryFind("BWD", name, out var entry))
            {
                data = prj.Read(entry);
                return true;
            }
            data = LooseBwd?.Invoke(name);
            if (data != null) return true;
            if (!File.Exists(path)) return false;
            data = File.ReadAllBytes(path);
            return true;
        }

        /// <summary>MTBL chunk at <paramref name="c"/> (offsets include the 8-byte chunk header, as the engine copies it).</summary>
        static TeamOrders ReadOrders(byte[] d, int c, int length)
        {
            var team = new TeamOrders
            {
                Team = BitConverter.ToInt32(d, c + 8),
                TimeLimit = BitConverter.ToInt32(d, c + 0xC),
                SuccessSound = BwdChunks.ReadString(d, c + 0x12, 11),
                FailureSound = BwdChunks.ReadString(d, c + 0x1D, 9)
            };
            int count = (length - 0x26) / 0x97;
            for (int k = 0; k < count; k++)
            {
                int e = c + 0x26 + k * 0x97;
                var o = new MissionOrder
                {
                    Mask = BitConverter.ToUInt32(d, e),
                    Vital = d[e + 4] == 'V',
                    Mandatory = d[e + 0x2F] == 'M',
                    AllConditions = d[e + 6] != 0,
                    TimeLimit = BitConverter.ToInt32(d, e + 0x2A),
                    TargetTeam = BitConverter.ToInt16(d, e + 0x53),
                    TargetOrder = BitConverter.ToInt16(d, e + 0x55),
                    Kind = d[e + 0x2E],
                    Assignees = d[e + 0x30],
                    PilotMode = d[e + 0x31],
                    StartSound = BwdChunks.ReadString(d, e + 0x34, 11),
                    FinishSound = BwdChunks.ReadString(d, e + 0x3F, 11),
                    Target = BwdChunks.ReadString(d, e + 0x4A, 9),
                    Text = BwdChunks.ReadString(d, e + 0x57, 0x97 - 0x57)
                };
                var types = new List<char>();
                var a = new List<int>();
                var bb = new List<int>();
                for (int j = 0; j < 8; j++)
                {
                    byte t = d[e + 0xA + j * 4];
                    if (t != 'C' && t != 'S' && t != 'F') break;
                    types.Add((char)t);
                    a.Add(d[e + 0xB + j * 4]);
                    bb.Add(d[e + 0xC + j * 4]);
                }
                o.ConditionTypes = new string(types.ToArray());
                o.ConditionA = a.ToArray();
                o.ConditionB = bb.ToArray();
                team.Orders.Add(o);
            }
            return team;
        }

        /// <summary>
        /// DoFirstObjtv 0x10040733 takes the first MTBL record's first bound NAVP. If none is
        /// bound it still calls Team_PlaceAtStart with zero position and heading.
        /// </summary>
        public bool TryGetTeamStart(int team, out NavPoint start)
        {
            var orders = Teams.Find(t => t.Team == team);
            if (orders != null && orders.Orders.Count > 0)
            {
                if (TryGetNav(orders.Orders[0].Target, out start)) return true;
                start = default; // original DoFirstObjtv passes (0,0,0,0) when the NAVP was not bound
                return true;
            }
            start = default;
            return false;
        }

        public bool TryGetNav(string file, out NavPoint nav)
        {
            foreach (var n in NavPoints)
                if (n.File.Equals(file, StringComparison.OrdinalIgnoreCase)) { nav = n; return true; }
            nav = default;
            return false;
        }

        /// <summary>Player team (0) uses the same first MTBL record as every other team (0x10040733).</summary>
        public bool TryGetStart(out NavPoint start) => TryGetTeamStart(0, out start);
    }
}
