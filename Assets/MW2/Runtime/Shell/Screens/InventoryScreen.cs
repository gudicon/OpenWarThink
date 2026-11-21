using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace MW2.Shell
{
    /// <summary>Office/drop-ship inventory 0xc01f (FUN_10030204 / FUN_100317e0).
    /// Column anchors and row baselines come from FUN_100310af / FUN_100328eb; the picture box at (0x1a2, 0x7a) shows the
    /// selected mech ("a&lt;code&gt;", FUN_100315f3) or the tab's picture ("amissle1/2", "alaser1/2", "aproj1/2",
    /// "aequipt1/2", "aammo1/2": 1 in the office, 2 in the drop ship). FUN_100328eb also puts a "%s" of DAT_100cd6e0 centred
    /// on x 0x213 at y 0x5a; DAT_100cd6e0 is copied from DAT_100cd874, which nothing in the DLL writes, so it is empty and
    /// not drawn here. Paging (PageUp/PageDown) is the port's own; the original list has no pages.</summary>
    public static class InventoryScreen
    {
        static readonly ShellRegions.Entry[] Spots =
        {
            new ShellRegions.Entry(500, 433, 602, 473, 0, 0, ""),
            new ShellRegions.Entry(60, 41, 152, 75, 0, 0, ""),
            new ShellRegions.Entry(153, 41, 241, 75, 0, 0, ""),
            new ShellRegions.Entry(242, 41, 331, 75, 0, 0, ""),
            new ShellRegions.Entry(332, 41, 421, 75, 0, 0, ""),
            new ShellRegions.Entry(422, 41, 511, 75, 0, 0, ""),
            new ShellRegions.Entry(512, 41, 602, 75, 0, 0, "")
        };
        static readonly string[] OfficeSprites = { "ocbime", "ocbim", "ocbien", "ocbip", "ocbieq", "ocbia", "ocbie" };
        static readonly string[] DockSprites = { "inmech", "inmiss", "inenrg", "inproj", "inequp", "inammo", "inexit" };
        static readonly string[] TabPictures = { "amissle", "alaser", "aproj", "aequipt", "aammo" };
        static readonly int[] TabX = { 0x42, 0x9c, 0xf5, 0x14e, 0x1a8, 0x202 };
        const int PictureX = 0x1a2, PictureY = 0x7a;
        /// <summary>DAT_100ccfb0 (colour 0x16, rows), DAT_100cceb0 (0x11, headings and the selected mech),
        /// DAT_100cccb0 (0x8f, damaged counts and prices).</summary>
        const int Normal = 0x16, Bright = 0x11, Damaged = 0x8f;
        static Mw2Shell _shell;
        static ShellRegions _regions;
        static ShellSound _click, _exit, _select;
        static InventoryCategories _categories;
        static readonly List<ShellText> Lines = new List<ShellText>();
        static ShellAnim[] _tabs;
        static ShellAnim _exitButton, _mechPicture;
        static readonly ShellAnim[] Pictures = new ShellAnim[5];
        static int _tab, _page, _selected;

        public static void Open(Mw2Shell shell)
        {
            _shell = shell;
            _tab = 0; _page = 0;
            _categories = InventoryCategories.Read(shell.Career);
            shell.Anims.Clear();
            for (int i = 0; i < Pictures.Length; i++) Pictures[i] = null;
            _mechPicture = null;
            shell.Surface.LoadBackground(ShellPcx.Decode(shell.Db.Unpack(shell.Career.Byte23B9 ? 45 : 31)));
            string[] sprites = shell.Career.Byte23B9 ? DockSprites : OfficeSprites;
            _tabs = new ShellAnim[6];
            for (int i = 0; i < 6; i++)
            {
                _tabs[i] = new ShellAnim(shell.Anims, sprites[i], TabX[i], 0x2e, 0x61, 0);
                _tabs[i].Show();
            }
            _exitButton = new ShellAnim(shell.Anims, sprites[6], 510, 440, 0x61, 0);
            _exitButton.Show();
            _tabs[0].SetFrame(1);
            _regions = new ShellRegions(shell.Surface, shell.Font7, false, Spots, () => true);
            _click = Sound(0x35, 50); _exit = Sound(0x39, 30);
            // FUN_10030204: DAT_100cd818 (record 0x34, volume 0x14) plays when a mech is picked (FUN_1003159a).
            _select = Sound(0x34, 0x14);
            // FUN_10030204: with at least one mech the first one is picked (its picture shown, its row bright).
            _selected = shell.Career.UnitMechs.Count > 0 ? 0 : -1;
            if (_selected >= 0) ShowMechPicture();
            ShowRows();
            shell.FlushKeys();
            shell.Register(Tick);
        }

        static ShellSound Sound(int record, int volume)
        {
            var sound = new ShellSound(_shell.gameObject, _shell.Db, record);
            sound.SetVolume(volume);
            return sound;
        }

        static void Draw(string value, int x, int y, int color = Normal)
        {
            var text = new ShellText(_shell.Surface, _shell.Font11, value, x, y, ShellSurface.MakeRemap(color));
            text.Show(); Lines.Add(text);
        }

        /// <summary>FUN_10049693 0x10049693: right edge minus the font's measured width.</summary>
        static void DrawRight(string value, int right, int y, int color = Normal) =>
            Draw(value, right - _shell.Font11.Width(value), y, color);

        /// <summary>FUN_100496d6 0x100496d6: money with commas.</summary>
        static string Money(int value) => value.ToString("N0", CultureInfo.InvariantCulture);

        /// <summary>FUN_100315f3 0x100315f3: hide the shown picture and make "a&lt;code&gt;" (+0x99c) at (0x1a2, 0x7a), flags 0x28.</summary>
        static void ShowMechPicture()
        {
            HidePictures();
            if (_mechPicture != null) { _mechPicture.Hide(); _mechPicture.Destroy(); _mechPicture = null; }
            if (_selected < 0 || _selected >= _shell.Career.UnitMechs.Count) return;
            _mechPicture = new ShellAnim(_shell.Anims, "a" + _shell.Career.UnitMechs[_selected].Code, PictureX, PictureY, 0x28, 0);
            _mechPicture.Show();
        }

        static void HidePictures()
        {
            _mechPicture?.Hide();
            foreach (var p in Pictures) p?.Hide();
        }

        /// <summary>FUN_100317e0 cases 2-6: the tab's picture is made once (suffix 2 in the drop ship, 1 in the office)
        /// and shown in place of the previous one.</summary>
        static void ShowTabPicture(int tab)
        {
            HidePictures();
            int i = tab - 1;
            if (Pictures[i] == null)
                Pictures[i] = new ShellAnim(_shell.Anims, TabPictures[i] + (_shell.Career.Byte23B9 ? "2" : "1"),
                    PictureX, PictureY, 0x28, 0);
            Pictures[i].Show();
        }

        static int MaxRows => Mathf.Max(1, 300 / _shell.Font11.Height);

        static void ShowRows()
        {
            foreach (var line in Lines) line.Hide();
            Lines.Clear();
            var c = _shell.Career;
            int rowHeight = _shell.Font11.Height;
            int maxRows = MaxRows;
            if (_tab == 0)
            {
                // FUN_100310af(0x52, 0x73): "Mech" (FUN_10031409) and "Value" right at x + 200 (FUN_1003144f), both bright.
                Draw("Mech", 0x52, 0x73, Bright);
                DrawRight("Value", 0x11a, 0x73, Bright);
                _page = Mathf.Min(_page, Mathf.Max(0, (c.UnitMechs.Count - 1) / maxRows));
                int first = _page * maxRows;
                for (int row = 0; row < maxRows && first + row < c.UnitMechs.Count; row++)
                {
                    var mech = c.UnitMechs[first + row];
                    int model = _shell.Data.Tables.FindMech(mech.Code);
                    string name = mech.Name.Length > 0 ? mech.Name :
                        model >= 0 ? _shell.Data.Tables.MechName(model) : mech.MekName;
                    int y = 0x73 + (2 + row) * rowHeight;
                    // FUN_100314ac / FUN_10031712: the picked mech's name and value are bright.
                    int color = first + row == _selected ? Bright : Normal;
                    Draw(name, 0x52, y, color);
                    DrawRight(Money(ShellEconomy.MechPrice(mech, c.Year, _shell.Data.Tables)), 0x11a, y, color);
                }
                return;
            }
            List<ItemRecord> items = _tab == 1 ? _categories.Missiles : _tab == 2 ? _categories.Energy :
                _tab == 3 ? _categories.Projectiles : _tab == 4 ? _categories.Equipment : _categories.Ammo;
            int headerY = 0x5a + 2 * rowHeight;
            if (items.Count == 0)
            {
                // FUN_10033b73: "None" in place of the headings.
                Draw("None", 0x52, headerY);
                return;
            }
            // FUN_100328eb picks the headings by DAT_100cd810 (ammo: "Ammo", "Tons", "Price/ton") and DAT_100cd80c
            // (equipment). Nothing in the DLL sets DAT_100cd810, so the ammo tab shows the weapon headings too.
            Draw(_tab == 4 ? "Equipment" : "Weapon", 0x52, headerY, Bright);
            DrawRight("Qty", 0xca, headerY, Bright);
            DrawRight("Price/unit", 0x142, headerY, Bright);
            var groups = InventoryCategories.GroupItems(items, _tab == 5);
            _page = Mathf.Min(_page, Mathf.Max(0, (groups.Count - 1) / maxRows));
            int start = _page * maxRows;
            for (int row = 0; row < maxRows && start + row < groups.Count; row++)
            {
                var group = groups[start + row];
                int y = 0x5a + (5 + row) * rowHeight;
                // FUN_10033846: weapons "%s", equipment "%s:" ("BAD EQUIP!! %d" outside 0..0xb).
                string name = _tab != 4 ? _shell.Data.Tables.WeaponName(group.Index) :
                    group.Index < 0 || group.Index > 0xb ? $"BAD EQUIP!! {group.Index}" :
                    ShellFont.Tr(_shell.Data.Tables.EquipmentName(group.Index)) + ":";
                Draw(name, 0x52, y);
                bool damaged = group.Damaged > 0;
                // FUN_10033930: without damaged items the count is right-aligned on the next column (0xd4);
                // with them "%d /" ends at 0xca and FUN_10033a3d puts the damaged count at 0xd4.
                if (damaged)
                {
                    DrawRight($"{group.Pristine} /", 0xca, y);
                    DrawRight(group.Damaged.ToString(), 0xd4, y, Damaged);
                }
                else DrawRight(group.Pristine.ToString(), 0xd4, y);
                // FUN_1003339a: the price (0 with no undamaged items), "%s / " when damaged ones follow;
                // FUN_100334b4 starts the damaged items' price at 0x142.
                int price = group.Pristine > 0 ? group.Price : 0;
                DrawRight(damaged ? Money(price) + " / " : Money(price), 0x142, y);
                if (damaged) Draw(Money(group.Price), 0x142, y, Damaged);
            }
        }

        /// <summary>FUN_1004b78e 0x1004b78e: a mech row is clicked within x 0x52..0x52+200 and the height of font 7.</summary>
        static int MechRowAt(int x, int y)
        {
            if (_tab != 0 || x < 0x52 || x >= 0x52 + 200) return -1;
            int rowHeight = _shell.Font11.Height;
            int first = _page * MaxRows;
            for (int row = 0; row < MaxRows && first + row < _shell.Career.UnitMechs.Count; row++)
            {
                int top = 0x73 + (2 + row) * rowHeight;
                if (y >= top && y < top + _shell.Font7.Height) return first + row;
            }
            return -1;
        }

        static void Tick(uint ev)
        {
            var shell = _shell;
            if (ev == 0xc004)
            {
                shell.TickCounter++;
                shell.Anims.Step();
                int spot = _regions.Hit(shell.MouseX, shell.MouseY);
                if (Input.GetKeyDown(KeyCode.PageDown)) { _page++; ShowRows(); }
                if (Input.GetKeyDown(KeyCode.PageUp)) { _page = Mathf.Max(0, _page - 1); ShowRows(); }
                if (shell.MouseClicked)
                {
                    int mech = MechRowAt(shell.MouseX, shell.MouseY);
                    if (mech >= 0)
                    {
                        // FUN_1003159a: pick the mech, show its picture, play DAT_100cd818.
                        _selected = mech;
                        ShowMechPicture();
                        _select.Play();
                        ShowRows();
                    }
                    if (spot == 0 && shell.Career.Mode == 1)
                    {
                        _exitButton.SetFrame(1);
                        shell.BlockUntilEnd(_exit); ev = shell.Career.Byte23B9 ? 0xc036u : 0xc021u;
                    }
                    else if (spot > 0 && spot < 7)
                    {
                        _tab = spot - 1; _page = 0;
                        // FUN_10032898: all tab buttons up, then the chosen one down.
                        foreach (var t in _tabs) t.SetFrame(0);
                        _tabs[_tab].SetFrame(1);
                        _click.Play();
                        if (_tab == 0) ShowMechPicture(); // case 1: the picked mech's click handler runs again
                        else ShowTabPicture(_tab);
                        ShowRows();
                    }
                }
            }
            if (ev == 0xc004) return;
            foreach (var line in Lines) line.Hide();
            Lines.Clear();
            _regions.Dispose(); _regions = null;
            shell.Anims.Clear();
            for (int i = 0; i < Pictures.Length; i++) Pictures[i] = null;
            _mechPicture = null;
            shell.Surface.DrawAfter.Clear(); shell.Surface.DrawBefore.Clear();
            _click?.Release(); _exit?.Release(); _select?.Release();
            shell.Unregister();
            shell.Post(ev, 0xc01f);
        }
    }
}
