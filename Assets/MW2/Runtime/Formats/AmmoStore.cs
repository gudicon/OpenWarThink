using System;
using System.Collections.Generic;

namespace MW2.Formats
{
    /// <summary>
    /// The ammunition of a unit as Mech_LoadMek 0x1002f9e0 builds it. Every MEK ammunition entry (at most 0x19) is a bin
    /// (mech +0x5c, 0x20 bytes: +0 type = weapon id / 100, +2 rounds = shots per ton (+0x30) x shots per trigger (+0x2c),
    /// +4 its critical id, +8 damage per round (+0x40), +0x14 pool). Bins of one type share a pool (mech +0x1a4, 0xda
    /// bytes, count +0x1a0: +0 type, +2 rounds of all its bins, +6 number of bins, +0xa current bin, +0xe bin indices,
    /// +0xd6 the current bin). A weapon (slot +0x20) uses the pool of its type, 9999 when there is none; a weapon that
    /// needs ammunition and has no pool starts in state -1.
    /// </summary>
    [Serializable]
    public sealed class AmmoStore
    {
        public const int NoPool = 9999;

        [Serializable]
        public sealed class Bin
        {
            public int Type, Count, CritId, Pool;
            public float Damage;
        }

        [Serializable]
        public sealed class Pool
        {
            public int Type, Total, Current;
            public List<int> Bins = new List<int>();
        }

        public List<Bin> Bins = new List<Bin>();
        public List<Pool> Pools = new List<Pool>();

        public static AmmoStore Load(MekLoadout mek, IList<WeaponDef> table)
        {
            var store = new AmmoStore();
            int n = Math.Min(mek.AmmoForWeapon.Count, 0x19);
            for (int k = 0; k < n; k++)
            {
                int type = mek.AmmoForWeapon[k] / 100;
                var def = type >= 0 && type < table.Count ? table[type] : null;
                var bin = new Bin
                {
                    Type = type,
                    Count = (short)((def?.ShotsPerTon ?? 0) * (def?.Burst ?? 0)),
                    CritId = (ushort)(k < mek.AmmoCrits.Count ? mek.AmmoCrits[k] : 0),
                    Damage = def?.Damage ?? 0f
                };
                int pool = store.Pools.FindIndex(p => p.Type == type);
                if (pool < 0)
                {
                    pool = store.Pools.Count;
                    store.Pools.Add(new Pool { Type = type });
                }
                bin.Pool = pool;
                store.Pools[pool].Total += bin.Count;
                store.Pools[pool].Bins.Add(store.Bins.Count);
                store.Bins.Add(bin);
            }
            return store;
        }

        /// <summary>Slot +0x20 of a weapon: the pool of its type, or <see cref="NoPool"/>.</summary>
        public int PoolOf(int weaponId)
        {
            int i = Pools.FindIndex(p => p.Type == weaponId / 100);
            return i < 0 ? NoPool : i;
        }

        public Pool Get(int pool) => pool >= 0 && pool < Pools.Count ? Pools[pool] : null;

        /// <summary>Rounds left for a weapon of pool <paramref name="pool"/>; -1 without a pool.</summary>
        public int Rounds(int pool) => Get(pool)?.Total ?? -1;

        Bin Current(Pool p) => Bins[p.Bins[p.Current]];

        /// <summary>The pool after a bin ran dry: the next bin becomes current (the last one stays current).</summary>
        void Take(Pool p, int rounds)
        {
            var bin = Current(p);
            if (bin.Count < 1)
            {
                bin.Count = 0;
                p.Current++;
                if (p.Current == p.Bins.Count) p.Current--;
                else Current(p).Count -= rounds;
            }
            else bin.Count -= rounds;
        }

        /// <summary>Weapon_FireProjectile 0x1006c554: whether a shot leaves: no pool, rounds left, or mw2dif.cfg +0 (for
        /// any unit).</summary>
        public bool CanFire(int pool, bool unlimitedOption)
        {
            var p = Get(pool);
            return p == null || p.Total != 0 || unlimitedOption;
        }

        /// <summary>
        /// Weapon_FireProjectile 0x1006c554, one round: unless the pool is -1 or the option +0 spares this (player) unit,
        /// the pool loses one and so does its current bin (an empty pool empties the current bin; an empty bin passes to
        /// the next). Returns false when the pool ran dry: every weapon of its type goes to state -1 and the burst ends.
        /// </summary>
        public bool Consume(int pool, bool spare)
        {
            var p = Get(pool);
            if (p == null || p.Total == -1 || spare) return true;
            p.Total--;
            if (p.Total < 1) Current(p).Count = 0;
            Take(p, 1);
            return p.Total >= 1;
        }

        /// <summary>
        /// FUN_1000ce3f 0x1000ce3f (AMS): RandomInt(5) + 1 rounds, at most the burst's remaining <paramref name="shotsLeft"/>,
        /// leave the pool; returns the rounds used, 0 when the pool is empty (the slot then goes to state -1), 2 when the
        /// pool is spared (option +0 for the player) or is -1.
        /// </summary>
        public int AmsFire(int pool, int shotsLeft, bool spare, Func<int, int> randomInt, out bool empty)
        {
            empty = false;
            var p = Get(pool);
            if (p == null || p.Total == 0) return 0;
            if (p.Total == -1 || spare) return 2;
            int used = randomInt(5) + 1;
            if (shotsLeft - used < 0) used = shotsLeft;
            p.Total -= used;
            if (p.Total < 1) { p.Total = 0; Current(p).Count = 0; }
            Take(p, used);
            if (p.Total == 0) { empty = true; used = 0; }
            return used;
        }

        /// <summary>
        /// Mech_CriticalHit 0x10036aa9 on an ammunition item: the first bin with that critical id; nothing when its pool is
        /// invalid or the bin is empty. Otherwise the pool loses the bin's rounds (an emptied pool returns true in
        /// <paramref name="poolEmptied"/>), the bin is emptied and its rounds and damage per round are returned for the
        /// explosion (the caller then takes the item out of the section).
        /// </summary>
        public (int rounds, float damage) Explode(int critId, out bool poolEmptied, out int poolIndex)
        {
            poolEmptied = false;
            poolIndex = -1;
            foreach (var bin in Bins)
            {
                if (bin.CritId != critId) continue;
                if (bin.Pool >= Pools.Count || bin.Count < 1) return (0, 0f);
                var p = Pools[bin.Pool];
                poolIndex = bin.Pool;
                p.Total -= bin.Count;
                if (p.Total < 1) { p.Total = 0; poolEmptied = true; }
                int rounds = bin.Count;
                bin.Count = 0;
                return (rounds, bin.Damage);
            }
            return (0, 0f);
        }

        /// <summary>Mech_HeatExplode 0x10035915: a bin with this critical id whose pool still has rounds.</summary>
        public bool Live(int critId)
        {
            foreach (var bin in Bins)
                if (bin.CritId == critId && bin.Pool < Pools.Count && Pools[bin.Pool].Total > 0) return true;
            return false;
        }

        /// <summary>Weapon_JettisonAmmo 0x1006d162: every bin of the pool and the pool itself are emptied.</summary>
        public void Jettison(int pool)
        {
            var p = Get(pool);
            if (p == null) return;
            foreach (int b in p.Bins) Bins[b].Count = 0;
            p.Total = 0;
        }

        /// <summary>Mek_WriteUnit 0x10058f90: the MEK ammunition list after the mission, one (critical id, type x 100)
        /// entry per bin with rounds left; the critical ids of empty bins leave the section lists.</summary>
        public List<(int crit, int weapon)> Remaining()
        {
            var list = new List<(int, int)>();
            foreach (var bin in Bins) if (bin.Count > 0) list.Add((bin.CritId, bin.Type * 100));
            return list;
        }
    }
}
