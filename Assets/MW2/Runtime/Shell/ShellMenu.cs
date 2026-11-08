using System;
using System.Collections.Generic;
using System.Text;

namespace MW2.Shell
{
    /// <summary>
    /// The shell window's menu (MENU resource 104 of MW2SHELL.DLL: Mercenary, Options, Facility, Help) and the state of
    /// its items. Items are grayed and enabled the way the original calls EnableMenuItem: all of it at the start and after
    /// a full-screen flic (FUN_100438f4 0x100438f4), off while a flic plays (FUN_10043a00 0x10043a00), the Facility popup
    /// and its screens after every pass of the main loop (FUN_100423aa 0x100423aa), Save/Load off inside the save screen
    /// and back on in the next screen (ShellWindowProc 0x100408c7), Retire after the debriefing and a load. The commands
    /// themselves are carried out by Mw2Shell.MenuCommand.
    /// </summary>
    public sealed class ShellMenu
    {
        public const int NewMercenary = 0x9c41, FleeToWindows = 0x9c43, CockpitControls = 0x9c4b, CombatVariables = 0x9c94,
            TechnicalHelp = 0x9c95, MoviePlayback = 0x9c96, SaveLoad = 0x9c97, MercLords = 0x9c99, FirstCircuit = 0x9c9a,
            Newsfeed = 0x9c9d, Contracts = 0x9c9e, Personnel = 0x9c9f, Inventory = 0x9ca0, Financial = 0x9ca1, Repair = 0x9ca2,
            ArmsMerchant = 0x9ca3, MechFactory = 0x9ca4, Customization = 0x9ca5, WeaponsGrouping = 0x9ca6, Dropship = 0x9ca7,
            DutyRoster = 0x9ca8, LaunchMission = 0x9ca9, Retire = 0x9caa, VideoOptions = 0x9cab;

        public sealed class Item
        {
            public int Id;
            public string Text;     // '&' marks the access letter, '\t' the key text
            public bool Grayed;
            public bool Separator => Id == 0 && Text.Length == 0;
        }

        public sealed class Popup
        {
            public string Text;
            public bool Grayed;
            public readonly List<Item> Items = new List<Item>();
        }

        public readonly List<Popup> Popups = new List<Popup>();
        /// <summary>DAT_100a8a94: what FUN_100423aa last did to the Facility popup (999 at the start).</summary>
        int _facility = 999;

        /// <summary>A standard MENU template: per item a flags word (0x10 popup, 0x80 last, 1 grayed, 2 disabled), an id
        /// for plain items and a UTF-16 text.</summary>
        public static ShellMenu Parse(byte[] template)
        {
            var m = new ShellMenu();
            if (template == null) return m;
            int p = 4 + BitConverter.ToUInt16(template, 2);
            string Text()
            {
                int e = p;
                while (e + 1 < template.Length && (template[e] != 0 || template[e + 1] != 0)) e += 2;
                string s = Encoding.Unicode.GetString(template, p, e - p);
                p = e + 2;
                return s;
            }
            bool lastPopup = false;
            while (!lastPopup && p + 2 <= template.Length)
            {
                int flags = BitConverter.ToUInt16(template, p);
                p += 2;
                var popup = new Popup { Text = Text(), Grayed = (flags & 3) != 0 };
                lastPopup = (flags & 0x80) != 0;
                m.Popups.Add(popup);
                if ((flags & 0x10) == 0) continue;
                bool last = false;
                while (!last && p + 4 <= template.Length)
                {
                    int f = BitConverter.ToUInt16(template, p);
                    int id = BitConverter.ToUInt16(template, p + 2);
                    p += 4;
                    popup.Items.Add(new Item { Id = id, Text = Text(), Grayed = (f & 3) != 0 });
                    last = (f & 0x80) != 0;
                }
            }
            return m;
        }

        /// <summary>The menu as extracted into shell-defaults.json (ShellDefaultTables.Menu).</summary>
        public static ShellMenu From(List<MenuPopupDef> popups)
        {
            var m = new ShellMenu();
            if (popups == null) return m;
            foreach (var p in popups)
            {
                var popup = new Popup { Text = p.Text ?? "", Grayed = p.Grayed };
                foreach (var i in p.Items) popup.Items.Add(new Item { Id = i.Id, Text = i.Text ?? "", Grayed = i.Grayed });
                m.Popups.Add(popup);
            }
            return m;
        }

        public Item Find(int id)
        {
            foreach (var p in Popups)
                foreach (var i in p.Items)
                    if (i.Id == id) return i;
            return null;
        }

        /// <summary>EnableMenuItem by command (MF_ENABLED 0 / MF_GRAYED 1).</summary>
        public void Enable(int id, bool on)
        {
            var i = Find(id);
            if (i != null) i.Grayed = !on;
        }

        /// <summary>EnableMenuItem by position (0x400 / 0x401).</summary>
        public void EnablePopup(int position, bool on)
        {
            if (position >= 0 && position < Popups.Count) Popups[position].Grayed = !on;
        }

        public bool Enabled(int id)
        {
            foreach (var p in Popups)
                foreach (var i in p.Items)
                    if (i.Id == id) return !i.Grayed && !p.Grayed;
            return false;
        }

        /// <summary>FUN_100438f4 0x100438f4 (0x9c72 QuickTips is not in this menu).</summary>
        public void EnableMain(Career c)
        {
            if (c.Registered) EnablePopup(2, true);
            EnablePopup(1, true);
            foreach (int id in new[] { NewMercenary, MercLords, SaveLoad, CombatVariables, CockpitControls, MoviePlayback, FirstCircuit })
                Enable(id, true);
        }

        /// <summary>FUN_10043a00 0x10043a00.</summary>
        public void DisableMain()
        {
            EnablePopup(2, false);
            EnablePopup(1, false);
            foreach (int id in new[] { NewMercenary, MercLords, SaveLoad, CombatVariables, CockpitControls, MoviePlayback, FirstCircuit })
                Enable(id, false);
        }

        /// <summary>FUN_100423aa 0x100423aa: the Facility popup only for a registered career outside the title, the
        /// debriefing and salvage; New Mercenary off in instant action; its screens by mode and docking (+0x23b9).</summary>
        public void Update(Career c, int unitMechs)
        {
            uint cur = c.Msg23C7;
            if (c.Registered && cur != 0xc02b && cur != 0xc00a && cur != 0xc039)
            {
                if (_facility == 0 || _facility == 999)
                {
                    EnablePopup(2, true);
                    _facility = 1;
                }
            }
            else if (_facility == 1 || _facility == 999)
            {
                EnablePopup(2, false);
                _facility = 0;
            }
            Enable(NewMercenary, c.Mode != 2);
            if (_facility == 1)
            {
                if (c.Mode == 0)
                {
                    Enable(Repair, false);
                    Enable(Retire, false);
                    Enable(WeaponsGrouping, false);
                }
                if (c.Byte23B9)
                {
                    foreach (int id in new[] { Dropship, Newsfeed, Contracts, Personnel, ArmsMerchant, MechFactory, Repair, WeaponsGrouping })
                        Enable(id, false);
                    if (c.Mode == 0)
                    {
                        Enable(Inventory, false); Enable(Financial, false); Enable(Repair, false);
                        Enable(Customization, true); Enable(WeaponsGrouping, false);
                    }
                    else
                    {
                        Enable(Inventory, true); Enable(Financial, true); Enable(Repair, true);
                        Enable(Customization, false); Enable(WeaponsGrouping, true);
                    }
                    Enable(DutyRoster, true);
                    Enable(LaunchMission, true);
                }
                else
                {
                    Enable(Dropship, !c.Finished239D && unitMechs != 0);
                    Enable(Newsfeed, true);
                    bool regular = c.Mode == 1;
                    foreach (int id in new[] { Contracts, Personnel, ArmsMerchant, MechFactory, Customization, Inventory, Financial })
                        Enable(id, regular);
                    Enable(WeaponsGrouping, true);
                    Enable(DutyRoster, false);
                    Enable(LaunchMission, false);
                }
            }
            if (c.Mode == 0)
            {
                Enable(Repair, false);
                Enable(WeaponsGrouping, false);
            }
            if (c.Byte23B9) Enable(WeaponsGrouping, false);
        }

        /// <summary>The Facility commands 0x9c9d..0x9ca9 and the screens they open (FUN_1004228f targets).</summary>
        public static uint FacilityTarget(int id)
        {
            switch (id)
            {
                case Newsfeed: return 0xc026;
                case Contracts: return 0xc024;
                case Personnel: return 0xc022;
                case Inventory: return 0xc01f;
                case Financial: return 0xc020;
                case Repair: return 0xc028;
                case ArmsMerchant: return 0xc01e;
                case MechFactory: return 0xc01d;
                case Customization: return 0xc02f;
                case WeaponsGrouping: return 0xc030;
                case DutyRoster: return 0xc025;
                case LaunchMission: return 0xc012;
            }
            return 0;
        }
    }
}
