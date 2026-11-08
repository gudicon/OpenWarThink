using System;
using System.Collections.Generic;

namespace MW2.Shell
{
    /// <summary>FUN_10049a12 0x10049a12 / FUN_10049c6c 0x10049c6c:
    /// split the unit item list for the five inventory tabs. The original
    /// moves record pointers between lists; this view preserves the saved list.</summary>
    public sealed class InventoryCategories
    {
        /// <summary>FUN_100328eb groups rows by item index, displaying total,
        /// pristine (+0x16 = 0) and damaged/salvaged (+0x16 != 0) counts.</summary>
        public sealed class Group
        {
            public int Index, Price, Total, Pristine, Damaged;
        }

        public readonly List<ItemRecord> Missiles = new List<ItemRecord>();
        public readonly List<ItemRecord> Energy = new List<ItemRecord>();
        public readonly List<ItemRecord> Projectiles = new List<ItemRecord>();
        public readonly List<ItemRecord> Equipment = new List<ItemRecord>();
        public readonly List<ItemRecord> Ammo = new List<ItemRecord>();

        public static List<Group> GroupItems(List<ItemRecord> items, bool ammo)
        {
            if (items == null) throw new ArgumentNullException(nameof(items));
            var rows = new List<Group>();
            var byIndex = new Dictionary<int, Group>();
            foreach (var item in items)
            {
                if (!byIndex.TryGetValue(item.Index, out var group))
                {
                    group = new Group { Index = item.Index, Price = item.Price };
                    byIndex.Add(item.Index, group);
                    rows.Add(group);
                }
                group.Total++;
                if (ammo || item.Field16 == 0) group.Pristine++;
                else group.Damaged++;
            }
            rows.Sort((a, b) => a.Index.CompareTo(b.Index)); // FUN_10048d27
            return rows;
        }

        public static InventoryCategories Read(Career career)
        {
            if (career == null) throw new ArgumentNullException(nameof(career));
            var categories = new InventoryCategories();
            foreach (var item in career.UnitWeapons)
            {
                if (item.Kind == 2) { categories.Equipment.Add(item); continue; }
                int i = item.Index;
                if (i >= 0 && i <= 9 || i == 28 || i == 31 || i >= 33 && i <= 42 ||
                    i == 61 || i >= 63 && i <= 65) categories.Missiles.Add(item);
                // DAT_100cd140 (the inventory's projectile tab, case 4) and DAT_100cd13c (energy, case 3).
                else if (i >= 10 && i <= 19 || i == 29 || i >= 43 && i <= 50 ||
                         i == 62) categories.Projectiles.Add(item);
                else if (i >= 20 && i <= 27 || i >= 51 && i <= 60) categories.Energy.Add(item);
            }
            categories.Ammo.AddRange(career.UnitAmmo);
            return categories;
        }
    }
}
