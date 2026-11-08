using System;

namespace MW2.Shell
{
    /// <summary>
    /// Mech valuation from MW2SHELL.DLL FUN_10026db4 0x10026db4. The base price is the mech table's +0x24,
    /// adjusted by FUN_10026a1b 0x10026a1b for the career and introduction years. Armour, structure, equipment
    /// and each distinct weapon/ammunition critical then add their table prices.
    /// </summary>
    public static class ShellEconomy
    {
        /// <summary>FUN_1004a8f8 0x1004a8f8: permanent unit assets. The four temporary trade lists are empty outside trade screens.</summary>
        public static int UnitAssets(Career career, ShellTables tables)
        {
            int value = 0;
            foreach (var mech in career.UnitMechs) value += MechPrice(mech, career.Year, tables);
            foreach (var item in career.UnitWeapons) value += item.Price;
            foreach (var item in career.UnitAmmo) value += item.Price;
            return value;
        }

        public static int MechPrice(MechRecord mech, int year, ShellTables tables)
        {
            int index = tables.FindMech(mech.Code);
            int basePrice = index < 0 ? 2000000 : tables.Mech(index).Price;
            int price = AdjustForYear(year, mech.TechYear, basePrice);
            for (int section = 0; section < 8; section++)
            {
                int off = 0x8e0 + section * 0x12;
                int front = mech.I32(off + 8), rear = mech.I32(off + 12), structure = mech.I32(off);
                if (front >= 0) price += front * 625;     // FUN_1004a4a1 0x1004a4a1
                if (rear >= 0) price += rear * 625;
                if (structure > 0) price += structure * 200; // FUN_1004a4af 0x1004a4af
                for (int slot = 0; slot < 12; slot++)
                {
                    int code = mech.Crit(section, slot);
                    if (code >= 7000 && code < 10000)
                    {
                        int ei = Career.ItemIndex(2, code, tables);
                        if (ei >= 0) price += tables.Item(ei).Price;
                    }
                }
            }
            // FUN_100271ec / FUN_1002723e: mark matching later slots (> this slot) in this and every later section.
            // The slot index is reset to the current slot for each section, so a lower slot in a later section is priced again.
            var marked = new bool[8, 12];
            for (int section = 0; section < 8; section++)
                for (int slot = 0; slot < 12; slot++)
                {
                    if (marked[section, slot]) continue;
                    marked[section, slot] = true;
                    int code = mech.Crit(section, slot);
                    if (code <= 0 || (code >= 7000 && code < 10000)) continue;
                    int kind = code < 7000 ? 0 : 1; // FUN_1004a6c4 0x1004a6c4
                    int item = Career.ItemIndex(kind, code, tables);
                    for (int otherSection = section; otherSection < 8; otherSection++)
                        for (int otherSlot = slot + 1; otherSlot < 12; otherSlot++)
                            if (mech.Crit(otherSection, otherSlot) == code) marked[otherSection, otherSlot] = true;
                    if (kind == 0) price += tables.Weapon(item).Price;
                    else price += tables.Weapon(item).AmmoPrice;
                }
            return price;
        }

        /// <summary>FUN_10026a1b: integer truncation of base * (1 + the year pair's modifier).</summary>
        public static int AdjustForYear(int careerYear, int techYear, int basePrice)
        {
            double modifier = 0;
            switch (careerYear)
            {
                case 3025:
                    if (techYear == 3039) modifier = 0.5;
                    else if (techYear == 3050) modifier = 0.75;
                    break;
                case 3039:
                    if (techYear == 3025) modifier = -0.1;
                    else if (techYear == 3050) modifier = 0.5;
                    else if (techYear == 3055) modifier = 0.75;
                    break;
                case 3050:
                    if (techYear == 3025) modifier = -0.25;
                    else if (techYear == 3039) modifier = -0.1;
                    else if (techYear == 3055) modifier = 0.5;
                    else if (techYear == 3058) modifier = 0.75;
                    break;
                case 3055:
                    if (techYear == 3025) modifier = -0.5;
                    else if (techYear == 3039) modifier = -0.25;
                    else if (techYear == 3050) modifier = -0.1;
                    else if (techYear == 3058) modifier = 0.5;
                    break;
                case 3058:
                    if (techYear == 3039) modifier = -0.5;
                    else if (techYear == 3050) modifier = -0.25;
                    else if (techYear == 3055) modifier = -0.1;
                    break;
            }
            return (int)(basePrice * (1.0 + modifier));
        }

        /// <summary>FUN_1004ac2c 0x1004ac2c: assets plus cash must cover the cheapest market mech when the unit has none.</summary>
        public static bool CanAffordAnyMech(Career career, ShellTables tables)
        {
            if (career.UnitMechs.Count != 0) return true;
            int available = career.Funds + UnitAssets(career, tables);
            int cheapest = 100000000;
            foreach (var mech in career.MarketMechs)
            {
                int price = MechPrice(mech, career.Year, tables);
                if (price < cheapest) cheapest = price; // DAT_10093b68 = 1.0
            }
            return available >= cheapest;
        }
    }
}
