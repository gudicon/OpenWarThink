using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace MW2.Formats
{
    /// <summary>
    /// Weapon type from the table in mercsw.dll (.data, 0x68-byte records at VA 0x1009a038, indexed by type;
    /// MEK ids are type * 100 + instance). Fields used by the fire logic 0x1006bc57:
    /// +0x08 flags, +0x20 auto-repeat, +0x2C shots per trigger, +0x30 shots per ton of ammo (-1 none), +0x34 sound,
    /// +0x28 lock-on, +0x3C speed, +0x40 damage, +0x44 heat, +0x4C minimum range, +0x50 maximum range (model units),
    /// +0x54 recycle ticks, +0x58 ticks between shots,
    /// projectile (0x1006c554): +0x10 projectile type (BTHG slot type of the shot file), +0x14 impact effect,
    /// +0x18 / +0x1C effects started after each shot (0x10011e79: muzzle flash, sound), +0x38 gravity factor,
    /// +0x48 heat added to the unit hit, +0x5C lifetime in ticks;
    /// +0x60 name (types 0..34 Clan with 'c' prefix, 35..65 Inner Sphere).
    /// </summary>
    [Serializable]
    public sealed class WeaponDef
    {
        public int Type;
        public int PilotValue;          // u16 table 0x10098758, used by FUN_1003107e
        public string Name;
        public int Flags;
        public bool Auto;
        public int Burst;
        public int ShotsPerTon;
        public int Sound;
        public float Speed;
        public float Damage;
        public float Heat;
        public float RangeMeters;
        public float MinRangeMeters;    // +0x4c: inside it the AI does not fire the weapon (LRMs 30 m), 0x10005309
        public bool LockOn;             // +0x28: missiles that lock on (LRMs)
        public int RecycleTicks;
        public int IntervalTicks;
        public int ProjectileType;      // +0x10: 0..2 lasers, 3 SRM, 4 LRM, 5 autocannon, 6 PPC, 7 gauss
        public int ImpactEffect;        // +0x14: effect code of the hit (0x10011ea8), ORed with the hit flags
        public int FireEffect1, FireEffect2;    // +0x18, +0x1C: effects at the muzzle after each shot
        public float Gravity;           // +0x38: vertical acceleration -0.0296 * this, units/tick^2
        public float HeatToTarget;      // +0x48: heat added to the unit hit (PPC 3.5)
        public int LifetimeTicks;       // +0x5C: projectile lifetime (ML 181, LRM 724, PPC 1705)
        /// <summary>
        /// What the original runs for this weapon, as a name rather than as the address of the function.
        /// The table stores two hooks per type (+0x00 every update, +0x04 one shot) and they are addresses inside
        /// mercsw.dll, so they mean nothing once a table has been extracted, and nothing at all in another build.
        /// Across the 66 types the retail table installs exactly two combinations, which this field distinguishes.
        /// </summary>
        public string Behavior = BehaviorProjectile;

        /// <summary>Fires a projectile: the ordinary case, with no update hook of its own.</summary>
        public const string BehaviorProjectile = "projectile";

        /// <summary>Anti-missile system (types 29 cAMS, 62 AMS): its own ready and fire functions.</summary>
        public const string BehaviorAms = "ams";

        /// <summary>The hooks as they stand in the library this was read from; 0 when it came from a table file.</summary>
        [NonSerialized] public uint ReadyHook;  // +0x00: run for the slot every Weapons_Update (AMS 0x1000cc95), 0 for the rest
        [NonSerialized] public uint FireHook;   // +0x04: one shot (Weapon_FireProjectile 0x1006c554; AMS 0x1000ce3f)

        /// <summary>Anti-missile system (types 29 cAMS, 62 AMS): its own ready and fire functions.</summary>
        public bool IsAms => Behavior == BehaviorAms;

        /// <summary>Projectile types that spawn with a random offset and use the missile trail (0x1006c554).</summary>
        public bool IsMissileType => ProjectileType == 3 || ProjectileType == 4 || ProjectileType == 9 ||
                                     ProjectileType == 10 || ProjectileType == 12;

        public bool UsesAmmo => ShotsPerTon > 0;
    }

    public static class WeaponTable
    {
        const uint TableVa = 0x1009a038;
        const int RecordSize = 0x68;
        public const int TypeCount = 66;

        /// <summary>Hook addresses the retail table installs, which <see cref="WeaponDef.Behavior"/> replaces.</summary>
        const uint AmsReadyHook = 0x1000cc95, AmsFireHook = 0x1000ce3f, ProjectileFireHook = 0x1006c554;

        /// <summary>Name of this table in a <see cref="TableStore"/>.</summary>
        public const string TableName = "weapons";

        /// <summary>
        /// The weapon table as it stands: the extracted file if there is one, otherwise read out of the game's
        /// mercsw.dll and written to the store so the library is not needed again. Edits to the file are kept.
        /// </summary>
        public static List<WeaponDef> Get(string gameFolder, TableStore store = null)
        {
            if (store == null) store = TableStore.Default;
            return store.Extract(TableName, () => new WeaponTableFile(Load(Path.Combine(gameFolder, "mercsw.dll"))))
                        .Weapons;
        }

        /// <summary>Reads the table from the game's mercsw.dll.</summary>
        public static List<WeaponDef> Load(string dllPath)
        {
            byte[] d = File.ReadAllBytes(dllPath);
            int FileOffset(uint va) => PeImage.FileOffset(d, va);

            var list = new List<WeaponDef>();
            for (int t = 0; t < TypeCount; t++)
            {
                int r = FileOffset(TableVa + (uint)(t * RecordSize));
                int nameEnd = r + 0x60;
                while (nameEnd < r + RecordSize && d[nameEnd] != 0) nameEnd++;
                list.Add(new WeaponDef
                {
                    Type = t,
                    PilotValue = BitConverter.ToUInt16(d, FileOffset(0x10098758 + (uint)t * 2)),
                    Name = Encoding.ASCII.GetString(d, r + 0x60, nameEnd - (r + 0x60)),
                    Flags = BitConverter.ToInt32(d, r + 0x08),
                    Auto = BitConverter.ToInt32(d, r + 0x20) != 0,
                    Burst = BitConverter.ToInt32(d, r + 0x2C),
                    ShotsPerTon = BitConverter.ToInt32(d, r + 0x30),
                    Sound = BitConverter.ToInt32(d, r + 0x34),
                    Speed = BitConverter.ToSingle(d, r + 0x3C),
                    Damage = BitConverter.ToSingle(d, r + 0x40),
                    Heat = BitConverter.ToSingle(d, r + 0x44),
                    RangeMeters = BitConverter.ToSingle(d, r + 0x50) * PolyModel.UnitsToMeters,
                    MinRangeMeters = BitConverter.ToSingle(d, r + 0x4C) * PolyModel.UnitsToMeters,
                    LockOn = BitConverter.ToInt32(d, r + 0x28) != 0,
                    RecycleTicks = BitConverter.ToInt32(d, r + 0x54),
                    IntervalTicks = BitConverter.ToInt32(d, r + 0x58),
                    ProjectileType = BitConverter.ToInt32(d, r + 0x10),
                    ImpactEffect = BitConverter.ToInt32(d, r + 0x14),
                    FireEffect1 = BitConverter.ToInt32(d, r + 0x18),
                    FireEffect2 = BitConverter.ToInt32(d, r + 0x1C),
                    Gravity = BitConverter.ToSingle(d, r + 0x38),
                    HeatToTarget = BitConverter.ToSingle(d, r + 0x48),
                    LifetimeTicks = BitConverter.ToInt32(d, r + 0x5C),
                    ReadyHook = BitConverter.ToUInt32(d, r + 0x00),
                    FireHook = BitConverter.ToUInt32(d, r + 0x04),
                    Behavior = BehaviorOf(BitConverter.ToUInt32(d, r + 0x00), BitConverter.ToUInt32(d, r + 0x04))
                });
            }
            return list;
        }

        /// <summary>
        /// The pair of hooks a weapon type carries, as a name. The retail table uses exactly two combinations: the
        /// anti-missile types, which have both an update and a fire function of their own, and everything else, which
        /// has no update hook and the ordinary projectile one. A combination outside those is recorded as it stands
        /// rather than guessed at, so an unexpected build is visible in the extracted file instead of silently
        /// becoming an ordinary weapon.
        /// </summary>
        static string BehaviorOf(uint readyHook, uint fireHook)
        {
            if (readyHook == AmsReadyHook && fireHook == AmsFireHook) return WeaponDef.BehaviorAms;
            if (readyHook == 0 && fireHook == ProjectileFireHook) return WeaponDef.BehaviorProjectile;
            return $"unknown:{readyHook:x8}:{fireHook:x8}";
        }
    }

    /// <summary>The weapon table as a <see cref="TableStore"/> file. The wrapper is what lets a list round-trip.</summary>
    [Serializable]
    public sealed class WeaponTableFile
    {
        public List<WeaponDef> Weapons = new List<WeaponDef>();

        public WeaponTableFile() { }

        public WeaponTableFile(List<WeaponDef> weapons) { Weapons = weapons; }
    }

    /// <summary>
    /// MEK loadout (loader 0x1002f9e0): i32 tons, walking MP, jump MP, heat sinks, weapon count (max 14), ammo count;
    /// +0x18 8 sections x 0x28 (u32 front armor, u32 rear armor, u32 internal, u16 slots[12] with item ids, ...);
    /// +0x158 weapons x 8 (u16 id = type*100+n, u16, u32 with the fire group in bits 8..11), then ammo x 8 (u32 id = 10000+weapon id, u32 weapon id), then name.
    /// Section order (inferred from the AS7-D Atlas, whose AC/20 sits in the right torso): head, right torso, centre torso,
    /// left torso, right arm, left arm, right leg, left leg.
    /// </summary>
    [Serializable]
    public sealed class MekLoadout
    {
        public static readonly string[] SectionNames = { "Head", "Right torso", "Center torso", "Left torso", "Right arm", "Left arm", "Right leg", "Left leg" };

        public int Tons, WalkingMp, JumpMp, HeatSinks;
        /// <summary>Mech_LoadMek 0x1002f9e0: any of the 12 section slots contains 7000..7049.</summary>
        public bool HasMasc;
        public int[] FrontArmor = new int[8], RearArmor = new int[8], Internal = new int[8];
        public List<int> WeaponIds = new List<int>();
        public List<int> WeaponSections = new List<int>();
        /// <summary>Items in each section's slots (u16 ids: weapon type*100+n, ammo 10000+, equipment 7300..9500), the first +0x24 count.</summary>
        public List<int>[] SectionItems = new List<int>[8];
        /// <summary>Fire group of each weapon: bits 8..11 of the entry's second u32, values above 4 become 0 (0x1002f9e0).</summary>
        public List<int> WeaponGroups = new List<int>();
        public List<int> AmmoForWeapon = new List<int>();     // one entry per ton, value = weapon id
        /// <summary>The first u32 of each ammunition entry: the bin's critical id in the section slots.</summary>
        public List<int> AmmoCrits = new List<int>();
        /// <summary>Section +0x26 flags (0x100 keeps an ammunition explosion inside the section, Mech_CriticalHit 0x10036aa9).</summary>
        public int[] SectionFlags = new int[8];
        public string Name;
        int _pilotEquipmentValue;

        /// <summary>FUN_1003107e: load-time u16 pilot value (+0x17c), before difficulty scaling or damage.</summary>
        public ushort PilotCombatValue(IList<WeaponDef> table)
        {
            int value = _pilotEquipmentValue;
            // Malformed TNK00STD contains name bytes in its second declared weapon. Native replay crashes;
            // retain the port's existing omission of unknown weapon types (not an original recovery rule).
            foreach (int id in WeaponIds)
                if (id / 100 < table.Count) value += table[id / 100].PilotValue;
            return unchecked((ushort)value);
        }

        // FUN_1003107e deliberately reads the raw armour words as floats, even in integer stock MEKs.
        // Equipment counts and armour totals persist across sections and are added again on each pass.
        static int PilotEquipmentValue(byte[] d)
        {
            int[] limits = { 7000, 7050, 7100, 7150, 7200, 7250, 7300, 7350, 7400, 7450,
                             7500, 7550, 7600, 7650, 7700, 7750, 7800, 7850, 7900, 8000, 8500, 9000, 9500 };
            int[] weights = { 1, 1, -60, -60, -50, 1, 1, 1, 1, 1, 1, 1, 1, 1, 2, 0, 2, 3, 2, 1, 1, 0, 0 };
            int[] counts = new int[limits.Length];
            int tons = BitConverter.ToInt32(d, 0), value = tons, items = 0, armor = 0;
            for (int s = 0; s < 8; s++)
            {
                int offset = 0x18 + s * 0x28;
                int count = BitConverter.ToInt16(d, offset + 0x24);
                for (int slot = 0; slot < count; slot++)
                {
                    armor += (int)((double)BitConverter.ToSingle(d, offset) + BitConverter.ToSingle(d, offset + 4));
                    int item = BitConverter.ToUInt16(d, offset + 12 + slot * 2);
                    for (int i = limits.Length - 1; i >= 0; i--)
                        if (item > limits[i]) { counts[i]++; items++; break; }
                }
                for (int i = 20; i >= 0; i--)
                    value += counts[i] * (weights[i] < 0 ? -weights[i] : weights[i] * tons);
                value += items * (counts[21] != 0 ? 2 : 1) + armor + (int)BitConverter.ToSingle(d, offset + 8);
            }
            return value;
        }

        public static MekLoadout Decode(byte[] d)
        {
            var m = new MekLoadout
            {
                Tons = BitConverter.ToInt32(d, 0),
                WalkingMp = BitConverter.ToInt32(d, 4),
                JumpMp = BitConverter.ToInt32(d, 8),
                HeatSinks = BitConverter.ToInt32(d, 12)
            };
            m._pilotEquipmentValue = PilotEquipmentValue(d);
            int weapons = Math.Min(BitConverter.ToInt32(d, 16), 14);
            int ammo = BitConverter.ToInt32(d, 20);
            var slots = new List<int>[8];
            for (int s = 0; s < 8; s++)
            {
                int e = 0x18 + s * 0x28;
                m.FrontArmor[s] = (int)BitConverter.ToUInt32(d, e);
                m.RearArmor[s] = (int)BitConverter.ToUInt32(d, e + 4);
                m.Internal[s] = (int)BitConverter.ToUInt32(d, e + 8);
                slots[s] = new List<int>();
                for (int k = 0; k < 12; k++)
                {
                    int item = BitConverter.ToUInt16(d, e + 12 + k * 2);
                    slots[s].Add(item);
                    if (item >= 7000 && item < 7050) m.HasMasc = true;
                }
                int count = Math.Clamp((int)BitConverter.ToInt16(d, e + 0x24), 0, 12);
                m.SectionFlags[s] = BitConverter.ToUInt16(d, e + 0x26);
                m.SectionItems[s] = slots[s].GetRange(0, count);
            }
            for (int w = 0; w < weapons; w++)
            {
                int id = BitConverter.ToUInt16(d, 0x158 + w * 8);
                m.WeaponIds.Add(id);
                m.WeaponSections.Add(Array.FindIndex(slots, s => s.Contains(id)));
                int group = (int)((BitConverter.ToUInt32(d, 0x158 + w * 8 + 4) & 0xf00) >> 8);
                m.WeaponGroups.Add(group > 4 ? 0 : group);
            }
            int a0 = 0x158 + weapons * 8;
            for (int a = 0; a < ammo; a++)
            {
                m.AmmoCrits.Add((int)BitConverter.ToUInt32(d, a0 + a * 8));
                m.AmmoForWeapon.Add((int)BitConverter.ToUInt32(d, a0 + a * 8 + 4));
            }
            int n0 = a0 + ammo * 8;
            m.Name = BwdChunks.ReadString(d, n0, Math.Min(0x1E, d.Length - n0)).Trim();
            return m;
        }
    }
}
