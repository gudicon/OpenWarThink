using System;
using System.Collections.Generic;
using MW2.Formats;

namespace MW2.Shell
{
    /// <summary>FUN_100675e8 0x100675e8: the salvage screen starts from a pristine STD mech, applies the wreck's
    /// armour and structure, then uses its saved rand() values to decide which criticals remain. The later screen
    /// inventory and player selection are separate from this recovery pass.</summary>
    public static class SalvageRecovery
    {
        public sealed class Candidate
        {
            public MechRecord Current;
            public MechRecord Original;
            public int[] SlotStatus;
            public bool CanRecover;
        }

        /// <summary>FUN_100675e8 0x100675e8: resolve the wreck's base code to a STD template, keep the untouched
        /// template as the second record, apply damage and critical recovery to the first, copy the rebuilt weapon
        /// and ammo lists to both, and calculate whether this mech may be chosen.</summary>
        public static Candidate Prepare(SalvageFile.Wreck wreck, ShellData data, int roll)
        {
            if (wreck == null) throw new ArgumentNullException(nameof(wreck));
            if (data == null) throw new ArgumentNullException(nameof(data));
            string name = wreck.MekName + "STD";
            byte[] mek = data.ReadMek(name);
            var template = MechRecord.FromMek(name, mek, data.Tables, () => -1);
            if (template == null) return null; // Mech_FromMek failed: the source skips this wreck.
            var current = template.Clone();
            var original = template.Clone();
            int end = Array.IndexOf(wreck.NameBlock, (byte)0);
            if (end < 0) end = wreck.NameBlock.Length;
            current.SetStr(0, System.Text.Encoding.ASCII.GetString(wreck.NameBlock, 0, end));
            current.SetI32(0x364, 0);
            int[] slotStatus = RecoverCriticals(current, wreck);
            RebuildLists(current, data.Tables);
            Buffer.BlockCopy(current.Data, 0x2f0, original.Data, 0x2f0, 0x70);
            Buffer.BlockCopy(current.Data, 0x28c, original.Data, 0x28c, 0x64);
            original.Refit(data.Tables);
            return new Candidate { Current = current, Original = original, SlotStatus = slotStatus,
                CanRecover = Available(current, roll) };
        }

        /// <summary>FUN_100675e8 0x100675e8: OUT3 adds a free, always recoverable KDK00STD after
        /// ordinary wrecks, even when salvage.mw2 does not exist.</summary>
        public static Candidate PrepareKodiak(ShellData data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            var mek = data.ReadMek("KDK00STD");
            var current = MechRecord.FromMek("KDK00STD", mek, data.Tables, () => -1);
            if (current == null) return null;
            current.MekName = "KDK00MER.MEK";
            RebuildLists(current, data.Tables);
            var original = current.Clone();
            return new Candidate { Current = current, Original = original,
                SlotStatus = new int[96], CanRecover = true };
        }

        /// <summary>FUN_10069597 0x10069597: accepting a selected chassis clones the damaged and original records,
        /// assigns the first free unit number and a fresh shared ID, then puts both in the unit's lists.</summary>
        public static MechRecord Commit(Candidate selected, Career career, ShellTables tables)
        {
            if (selected == null || !selected.CanRecover || career.UnitMechs.Count >= 12) return null;
            var mech = selected.Current.Clone();
            var original = selected.Original.Clone();
            mech.MekName = mech.Code + "00MER.MEK";
            Career.Renumber(mech, career.UnitMechs);
            original.MekName = mech.MekName;
            mech.PilotId = original.PilotId = -1;
            mech.MechId = original.MechId = career.NextId();
            mech.SetI32(0x364, 0); original.SetI32(0x364, 0);
            mech.Refit(tables);
            career.UnitMechs.Add(mech); career.UnitCopies.Add(original);
            return mech;
        }

        /// <summary>FUN_100652b8 0x100652b8 and FUN_10064537 0x10064537: move one recoverable critical
        /// from the displayed wreck to the provisional drop-ship inventory. Every slot with the same ID is
        /// removed from both mech copies; a multi-slot weapon is one item. The original limits the load to 200.</summary>
        public static ItemRecord TakeCritical(Candidate candidate, int section, int slot,
            List<ItemRecord> weapons, List<ItemRecord> ammo, ShellTables tables, out string error)
        {
            error = null;
            if (candidate == null || section < 0 || section >= 8 || slot < 0 ||
                slot >= (section == 0 || section >= 6 ? 6 : 12)) return null;
            int id = candidate.Current.Crit(section, slot);
            int kind = CriticalKind(id);
            if (kind < 0 || id <= 0) return null;
            int decade = id / 10 * 10;
            if (decade >= 7000 && decade < 7900 || decade == 9000 || decade == 9500)
            {
                error = "Selected critical cannot be moved";
                return null;
            }
            int load = ammo.Count;
            foreach (var item in weapons) load += ArmsDealer.Space(item.Index, tables);
            if (load + (kind >= 3 ? 1 : tables.Weapon(id / 100).Slots) > 200)
            {
                error = "Cannot store any more items on drop-ship";
                return null;
            }
            var result = new ItemRecord { FieldA = -1 };
            bool damaged = false;
            int slots = 0;
            for (int s = 0; s < 8; s++)
                for (int k = 0; k < 12; k++)
                    if (candidate.Current.Crit(s, k) == id)
                    {
                        slots++;
                        if (candidate.SlotStatus[s * 12 + k] == 1) damaged = true;
                    }
            if (id < 7000)
            {
                result.Index = id / 100;
                result.Kind = 0;
                result.Price = tables.Weapon(result.Index).Price;
                result.FieldE = tables.Weapon(result.Index).Year;
                result.Field16 = damaged ? 1 : 0;
                weapons.Add(result);
            }
            else if (id < 10000)
            {
                int group = id / 100 * 100;
                if (group == 8000) { result.Index = slots == 1 ? 1 : 2; result.Price = 2000; }
                else if (group == 8500) { result.Index = 6; result.Price = 10000; }
                // FUN_10064537 compares the rounded group with 9550; that branch is unreachable.
                result.Kind = 2;
                result.FieldE = 0xbe4;
                result.Field16 = damaged ? 1 : 0;
                weapons.Add(result);
            }
            else
            {
                result.Index = (id - 10000) / 100;
                result.Kind = 1;
                result.Price = tables.Weapon(result.Index).AmmoPrice;
                result.FieldE = tables.Weapon(result.Index).Year;
                ammo.Add(result);
            }
            for (int s = 0; s < 8; s++)
                for (int k = 0; k < 12; k++)
                {
                    if (candidate.Current.Crit(s, k) == id) candidate.Current.SetCrit(s, k, 0);
                    if (candidate.Original.Crit(s, k) == id) candidate.Original.SetCrit(s, k, 0);
                }
            RebuildLists(candidate.Current, tables);
            // Prepare copies the recovered lists to Original. Rebuilding from Original's untouched
            // template slots would restore criticals that the wreck recovery already lost.
            Buffer.BlockCopy(candidate.Current.Data, 0x2f0, candidate.Original.Data, 0x2f0, 0x70);
            Buffer.BlockCopy(candidate.Current.Data, 0x28c, candidate.Original.Data, 0x28c, 0x64);
            candidate.Original.Refit(tables);
            return result;
        }

        /// <summary>FUN_1006729d 0x1006729d: a wreck with a destroyed centre torso (section 2) cannot be claimed.
        /// All other wrecks use the eight damage-picture states and one of the file's recorded rand() rolls.</summary>
        public static bool Available(MechRecord mech, int roll)
        {
            if (mech == null) throw new ArgumentNullException(nameof(mech));
            if (mech.SectionState(2) == 3) return false;
            int intact = 0, light = 0, heavy = 0, gone = 0;
            for (int s = 0; s < 8; s++)
                switch (mech.SectionState(s))
                {
                    case 0: intact++; break;
                    case 1: light++; break;
                    case 2: heavy++; break;
                    case 3: gone++; break;
                }
            if (intact >= 7 || light == 1 && intact > 5 ||
                light < 5 && intact > 3 && intact + light > 5 ||
                light < 6 && intact > 1 && intact + light > 5)
                return true;
            int chance;
            if (heavy == 1 && intact + light > 5 || light >= 1 && intact >= 6) chance = 90;
            else if (light >= 2 && light <= 3 && intact >= 4) chance = 80;
            else if (light >= 4 && light <= 5 && intact >= 4) chance = 70;
            else if (light >= 6) chance = 60;
            else if (heavy == 1 && intact + light > 4) chance = 50;
            else if (heavy >= 2 && heavy <= 3 && intact + light >= 4) chance = 40;
            else if (heavy >= 4 && heavy <= 5 && intact + light >= 4 || gone == 1 || heavy > 6 || gone == 2) chance = 30;
            else if (gone > 2) chance = 5;
            else chance = 0;
            return roll % 100 < chance;
        }

        /// <summary>FUN_10061b13 0x10061b13: 0 missiles, 1 projectile, 2 energy, 3 integral equipment,
        /// 4 ammunition, -1 other equipment.</summary>
        public static int CriticalKind(int id)
        {
            int group = id / 100;
            if (group > 99) return 4;
            if (group >= 1 && group <= 65)
            {
                if (group > 34) group -= 35;
                if (group <= 9 || group >= 31 && group <= 34) return 0;
                if (group >= 10 && group <= 19) return 2;
                if (group >= 20 && group <= 27) return 1;
            }
            int decade = id / 10 * 10;
            return decade == 8000 || decade == 8500 || decade == 9550 ? 3 : -1;
        }

        /// <summary>The slot statuses at mech +0x4e8 are -1 untouched, 0 recovered, 1 damaged item, 2 lost item.
        /// FUN_100675e8 uses roll (slot + section*8) % 100 even for sections with twelve slots.</summary>
        public static int[] RecoverCriticals(MechRecord mech, SalvageFile.Wreck wreck)
        {
            if (mech == null) throw new ArgumentNullException(nameof(mech));
            if (wreck == null) throw new ArgumentNullException(nameof(wreck));
            var status = new int[8 * 12];
            for (int s = 0; s < 8; s++)
            {
                int p = s * 0x28, o = 0x8e0 + s * 0x12;
                mech.SetI32(o, BitConverter.ToInt32(wreck.Sections, p + 8));
                mech.SetI32(o + 8, BitConverter.ToInt32(wreck.Sections, p));
                if (s == 1 || s == 2 || s == 3)
                    mech.SetI32(o + 12, BitConverter.ToInt32(wreck.Sections, p + 4));
                else mech.SetI32(o + 12, -1);
            }
            for (int i = 0; i < status.Length; i++) status[i] = -1;
            for (int s = 0; s < 8; s++)
            {
                int limit = s == 0 || s == 6 || s == 7 ? 6 : 12;
                int sectionState = mech.SectionState(s);
                for (int slot = 0; slot < limit; slot++)
                {
                    int position = s * 12 + slot;
                    int id = mech.Crit(s, slot);
                    if (id == -1 || CriticalKind(id / 100 * 100) == -1 || status[position] != -1)
                    {
                        if (sectionState == 3) mech.SetCrit(s, slot, 0);
                        continue;
                    }
                    int roll = wreck.Rolls[s * 8 + slot] % 100;
                    bool weapon = CriticalKind(id) != 4;
                    int lost, damagedFrom, damagedThrough, intact;
                    switch (sectionState)
                    {
                        case 0: lost = 21; damagedFrom = 21; damagedThrough = 54; intact = 56; break;
                        case 1: lost = 30; damagedFrom = 32; damagedThrough = 64; intact = 66; break;
                        case 2: lost = 46; damagedFrom = 46; damagedThrough = 94; intact = 96; break;
                        default: lost = damagedFrom = damagedThrough = intact = 0; break;
                    }
                    if (sectionState == 3 || roll < lost)
                    {
                        mech.SetCrit(s, slot, 0);
                        status[position] = 2;
                    }
                    else if (roll >= damagedFrom && roll <= damagedThrough)
                        status[position] = weapon ? 1 : 0;
                    else if (roll >= intact)
                        status[position] = 0;
                    // The gaps (55, 65 and 95) deliberately leave the slot untouched, as in the DLL.
                    int updated = mech.Crit(s, slot), result = status[position];
                    for (int other = 0; other < 8; other++)
                        for (int k = 0; k < 12; k++)
                            if ((other != s || k != slot) && mech.Crit(other, k) == id)
                            {
                                mech.SetCrit(other, k, updated);
                                status[other * 12 + k] = result;
                            }
                }
                for (int slot = limit; slot < 12; slot++) mech.SetCrit(s, slot, -1);
            }
            return status;
        }

        /// <summary>FUN_100675e8 0x100675e8: rebuild the 14 weapon/section pairs at +0x2f0 and the 25 ammo
        /// critical IDs at +0x28c from the recovered slots, suppressing later copies of the same ID. The original
        /// then calls FUN_1004ad22 to recalculate mass and movement.</summary>
        public static void RebuildLists(MechRecord mech, ShellTables tables)
        {
            for (int k = 0; k < 14; k++)
            {
                mech.SetI32(0x2f0 + k * 8, -1);
                mech.SetI32(0x2f4 + k * 8, -1);
            }
            for (int k = 0; k < 25; k++) mech.SetI32(0x28c + k * 4, -1);
            int weapons = 0, ammo = 0;
            for (int s = 0; s < 8; s++)
            {
                int limit = s == 0 || s == 6 || s == 7 ? 6 : 12;
                for (int slot = 0; slot < limit; slot++)
                {
                    int id = mech.Crit(s, slot), kind = CriticalKind(id);
                    if (kind < 0 || kind > 2) continue;
                    bool seen = false;
                    for (int k = 0; k < weapons; k++) if (mech.I32(0x2f0 + k * 8) == id) { seen = true; break; }
                    if (!seen && weapons < 14)
                    {
                        mech.SetI32(0x2f0 + weapons * 8, id);
                        mech.SetI32(0x2f4 + weapons * 8, s);
                        weapons++;
                    }
                }
            }
            for (int s = 0; s < 8; s++)
            {
                int limit = s == 0 || s == 6 || s == 7 ? 6 : 12;
                for (int slot = 0; slot < limit; slot++)
                {
                    int id = mech.Crit(s, slot);
                    if (CriticalKind(id) != 4) continue;
                    bool seen = false;
                    for (int k = 0; k < ammo; k++) if (mech.I32(0x28c + k * 4) == id) { seen = true; break; }
                    if (!seen && ammo < 25) mech.SetI32(0x28c + ammo++ * 4, id);
                }
            }
            mech.Refit(tables);
        }
    }
}
