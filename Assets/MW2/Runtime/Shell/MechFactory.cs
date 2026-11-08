using System;
using System.Collections.Generic;
using MW2.Formats;

namespace MW2.Shell
{
    /// <summary>
    /// The trade of the Mech Factory 0xc01d (mechfact.cpp, MW2SHELL.DLL). A mech sells for 0.6 of its value
    /// (FUN_10026db4, float 0x10093b08) and is bought for 1.0 of it (0x10093b10); the drop ship takes 12 mechs.
    /// </summary>
    public sealed class MechFactory
    {
        public const float Sell = 0.6f;
        public const int Capacity = 12;
        readonly Career _c;
        readonly ShellTables _t;

        public MechFactory(Career c, ShellTables t)
        {
            _c = c;
            _t = t;
        }

        public int Value(MechRecord m) => ShellEconomy.MechPrice(m, _c.Year, _t);
        public int SellPrice(MechRecord m) => (int)(Value(m) * (double)Sell);
        public int BuyPrice(MechRecord m) => (int)(Value(m) * 1.0);

        /// <summary>
        /// FUN_10038eb7 0x10038eb7 after "Sell this Mech?": funds + 0.6 of the value; the mech and its pristine copy go to the
        /// market (DAT_100cd11c / 120), the pilot flying it (+0x20) and both records lose the pilot.
        /// </summary>
        public void SellMech(MechRecord m)
        {
            _c.Started = true;
            _c.Funds += SellPrice(m);
            _c.UnitMechs.Remove(m);
            _c.MarketMechs.Add(m);
            var pristine = _c.UnitCopies.Find(x => x.MechId == m.MechId);
            if (pristine != null)
            {
                _c.UnitCopies.Remove(pristine);
                _c.MarketCopies.Add(pristine);
            }
            var pilot = _c.UnitPilots.Find(p => p.MechId == m.MechId);
            if (pilot != null) pilot.MechId = -1;
            m.PilotId = -1;
            if (pristine != null) pristine.PilotId = -1;
        }

        /// <summary>
        /// The purchase after "Buy this Mech?" (when the value × 1.0 does not exceed the funds): funds − the value; the mech
        /// leaves the market and gets the first free unit number (Mech_Renumber 0x10048df5) and a new id (Career_NextId),
        /// its pristine copy (found in DAT_100cd120 by the old id) the same name and id; both join the unit. The copy is
        /// not taken out of the market's copies (FUN_10009724 is never called on DAT_100cd120 here).
        /// </summary>
        public void BuyMech(MechRecord m)
        {
            _c.Started = true;
            _c.Funds -= BuyPrice(m);
            _c.MarketMechs.Remove(m);
            var pristine = _c.MarketCopies.Find(x => x.MechId == m.MechId);
            Career.Renumber(m, _c.UnitMechs);
            m.PilotId = -1;
            m.MechId = _c.NextId();
            if (pristine != null)
            {
                pristine.MekName = m.MekName;
                pristine.PilotId = -1;
                pristine.MechId = m.MechId;
            }
            _c.UnitMechs.Add(m);
            if (pristine != null) _c.UnitCopies.Add(pristine);
        }

        public bool CanAfford(MechRecord m) => Value(m) * 1.0 <= _c.Funds;

        /// <summary>FUN_1004a2b4 0x1004a2b4: the mech of the player's pilot.</summary>
        public MechRecord PlayerMech()
        {
            foreach (var p in _c.UnitPilots)
                if (p.Player) return _c.UnitMechs.Find(m => m.PilotId == p.Id);
            return null;
        }

        /// <summary>FUN_1004576c 0x1004576c on the MEK name: the table name of its code.</summary>
        public string TableName(MechRecord m)
        {
            int i = _t.FindMech(m.MekName);
            return i >= 0 ? _t.MechName(i) : "";
        }

        /// <summary>FUN_10038c3d 0x10038c3d: the list line, " [Plyr]" after the player's mech.</summary>
        public string ListName(MechRecord m)
        {
            string name = m.Str(0x100, 0x3a);
            if (name.Length == 0) name = TableName(m);
            return m == PlayerMech() ? name + " [Plyr]" : name;
        }

        /// <summary>
        /// The figures DAT_100a7838 (43, 206): FUN_1003716b mass "%s %d.%d%d/%d.%d%d T" (used / tons), FUN_10037337 speeds
        /// (MP × 10.8), FUN_100373bb jump jets (criticals of the equipment entry 8500), FUN_1003752d engine heat sinks
        /// (rating / 25), FUN_10037767 external heat sinks (entry 8000: Inner Sphere doubles count / 2, Clan / 3),
        /// FUN_100379db armour mass, then sixteen FUN_10037ad4 lines of weapons "%d %s" and ammunition "%d ton(s) %s Ammo".
        /// </summary>
        public List<string> Figures(MechRecord m)
        {
            var lines = new List<string>();
            string name = m.Str(0x100, 0x3a);
            if (name.Length == 0) name = TableName(m);
            string T(int v) => $"{v / 100}.{v / 10 % 10}{v % 10}";
            lines.Add($"{name} {T(m.I32(0x204))}/{T(m.I32(0x200))} T");
            lines.Add(string.Format(System.Globalization.CultureInfo.InvariantCulture, "walk: {0:0.0} kph run: {1:0.0} kph",
                m.I32(0x280) * 10.8, m.I32(0x284) * 10.8));
            int jets = CountEquipment(m, 0x2134);
            lines.Add(jets < 1 ? "No Jump Jets" : $"Equipped w/{jets} Jump Jets");
            lines.Add($"{m.I32(0x210) / 0x19} internal engine heat sinks");
            int sinks = CountEquipment(m, 8000);
            bool single = false, dbl = false;
            int singles = 0, doubles = 0;
            if (sinks > 0)
            {
                if (m.Technology == 0)
                {
                    dbl = m.I32(0x228) != 0;
                    if (dbl) doubles = sinks / 2; else singles = sinks;
                    single = !dbl;
                }
                else if (m.I32(0x228) == 0) { single = true; singles = sinks; }
                else { dbl = true; doubles = sinks / 3; }
            }
            lines.Add(single ? $"{singles} external single heat sinks" : dbl ? $"{doubles} external double heat sinks" : "No external heat sinks");
            lines.Add($"Armor: {T(m.I32(0x250))} T");
            var seen = new bool[8 * 12];   // DAT_100cdb70, cleared by FUN_10037a83
            for (int line = 0; line < 16; line++)
            {
                string text = WeaponLine(m, seen);
                if (text != null) lines.Add(text);
            }
            return lines;
        }

        /// <summary>The criticals matching the equipment table entry (at 0x100a8198) whose code is <paramref name="code"/>.</summary>
        int CountEquipment(MechRecord m, int code)
        {
            int n = 0;
            for (int s = 0; s < 8; s++)
                for (int j = 0; j < 12; j++)
                {
                    int c = m.Crit(s, j);
                    if (c >= 10000) continue;
                    for (int k = 0x16; k >= 0; k--)
                    {
                        int e = _t.Item(k).Code;
                        if (c / 0x32 == e / 0x32 && e == code) n++;
                    }
                }
            return n;
        }

        /// <summary>
        /// FUN_10037ad4 0x10037ad4: the next unmarked weapon or ammunition critical; it and the slots FUN_10037e8f marks
        /// (the same code in every later section, but only from the slot after it), then the unmarked ones of the same
        /// kind and index from the same section and slot on (the slot start again kept for later sections), counted.
        /// </summary>
        string WeaponLine(MechRecord m, bool[] seen)
        {
            for (int s = 0; s < 8; s++)
                for (int j = 0; j < 12; j++)
                {
                    if (seen[s * 12 + j]) continue;
                    seen[s * 12 + j] = true;
                    int c = m.Crit(s, j);
                    if (c < 0) continue;
                    int kind = Campaign.ItemKind(c);
                    if (kind == 3 || kind == 2) continue;
                    int idx = Career.ItemIndex(kind, c, _t);
                    Mark(m, s, j, c, seen);
                    int more = 0;
                    for (int s2 = s; s2 < 8; s2++)
                        for (int j2 = j; j2 < 12; j2++)
                        {
                            if (seen[s2 * 12 + j2]) continue;
                            int c2 = m.Crit(s2, j2);
                            if (c2 < 0) continue;
                            int k2 = Campaign.ItemKind(c2);
                            if (k2 == 3 || k2 == 2 || Career.ItemIndex(k2, c2, _t) != idx || k2 != kind) continue;
                            seen[s2 * 12 + j2] = true;
                            more++;
                            Mark(m, s2, j2, c2, seen);
                        }
                    if (kind == 0) return $"{more + 1} {_t.WeaponName(idx)}";
                    return more + 1 == 1 ? $"1 ton {_t.WeaponName(idx)} Ammo" : $"{more + 1} tons {_t.WeaponName(idx)} Ammo";
                }
            return null;
        }

        /// <summary>FUN_10037e8f 0x10037e8f.</summary>
        static void Mark(MechRecord m, int section, int slot, int code, bool[] seen)
        {
            for (int s = section; s < 8; s++)
                for (int j = slot + 1; j < 12; j++)
                    if (m.Crit(s, j) == code) seen[s * 12 + j] = true;
        }
    }
}
