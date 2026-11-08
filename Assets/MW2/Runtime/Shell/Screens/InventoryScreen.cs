using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace MW2.Shell
{
    /// <summary>Office/drop-ship inventory 0xc01f (FUN_10030204 / FUN_100317e0).
    /// Column anchors and row baselines come from FUN_100310af / FUN_100328eb; scrolling needs Unity QA.</summary>
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
        static readonly int[] TabX = { 0x42, 0x9c, 0xf5, 0x14e, 0x1a8, 0x202 };
        static Mw2Shell _shell;
        static ShellRegions _regions;
        static ShellSound _click, _exit;
        static InventoryCategories _categories;
        static readonly List<ShellText> Lines = new List<ShellText>();
        static int _tab, _page;

        public static void Open(Mw2Shell shell)
        {
            _shell = shell;
            _tab = 0; _page = 0;
            _categories = InventoryCategories.Read(shell.Career);
            shell.Anims.Clear();
            shell.Surface.LoadBackground(ShellPcx.Decode(shell.Db.Unpack(shell.Career.Byte23B9 ? 45 : 31)));
            string[] sprites = shell.Career.Byte23B9 ? DockSprites : OfficeSprites;
            for (int i = 0; i < 6; i++) new ShellAnim(shell.Anims, sprites[i], TabX[i], 0x2e, 0x61, 0).Show();
            new ShellAnim(shell.Anims, sprites[6], 510, 440, 0x61, 0).Show();
            _regions = new ShellRegions(shell.Surface, shell.Font7, false, Spots, () => true);
            _click = Sound(0x35, 50); _exit = Sound(0x39, 30);
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

        static void Draw(string value, int x, int y)
        {
            var text = new ShellText(_shell.Surface, _shell.Font11, value, x, y, ShellSurface.MakeRemap(0x16));
            text.Show(); Lines.Add(text);
        }

        /// <summary>FUN_10049693 0x10049693: right edge minus the font's measured width.</summary>
        static void DrawRight(string value, int right, int y) => Draw(value, right - _shell.Font11.Width(value), y);

        static void ShowRows()
        {
            foreach (var line in Lines) line.Hide();
            Lines.Clear();
            var c = _shell.Career;
            string Money(int value) => value.ToString("N0", CultureInfo.InvariantCulture);
            int rowHeight = _shell.Font11.Height;
            if (_tab == 0)
            {
                Draw("Mech", 0x52, 0x73);
                DrawRight("Value", 0x11a, 0x73);
            }
            else
            {
                Draw(_tab == 5 ? "Ammo" : _tab == 4 ? "Equipment" : "Weapon", 0x52, 0x5a);
                int headerY = 0x5a + 2 * rowHeight;
                DrawRight(_tab == 5 ? "Tons" : "Qty", 0xca, headerY);
                DrawRight(_tab == 5 ? "Price/ton" : "Price/unit", 0x142, headerY);
            }
            int maxRows = Mathf.Max(1, 300 / rowHeight);
            if (_tab == 0)
            {
                _page = Mathf.Min(_page, Mathf.Max(0, (c.UnitMechs.Count - 1) / maxRows));
                int first = _page * maxRows;
                for (int row = 0; row < maxRows && first + row < c.UnitMechs.Count; row++)
                {
                    var mech = c.UnitMechs[first + row];
                    int model = _shell.Data.Tables.FindMech(mech.Code);
                    string name = mech.Name.Length > 0 ? mech.Name :
                        model >= 0 ? _shell.Data.Tables.MechName(model) : mech.MekName;
                    int y = 0x73 + (2 + row) * rowHeight;
                    Draw(name, 0x52, y);
                    DrawRight(Money(ShellEconomy.MechPrice(mech, c.Year, _shell.Data.Tables)), 0x11a, y);
                }
                return;
            }
            List<ItemRecord> items = _tab == 1 ? _categories.Missiles : _tab == 2 ? _categories.Energy :
                _tab == 3 ? _categories.Projectiles : _tab == 4 ? _categories.Equipment : _categories.Ammo;
            var groups = InventoryCategories.GroupItems(items, _tab == 5);
            _page = Mathf.Min(_page, Mathf.Max(0, (groups.Count - 1) / maxRows));
            int start = _page * maxRows;
            for (int row = 0; row < maxRows && start + row < groups.Count; row++)
            {
                var group = groups[start + row];
                string name = _tab == 4 ? _shell.Data.Tables.EquipmentName(group.Index) :
                    _shell.Data.Tables.WeaponName(group.Index);
                int y = 0x5a + (5 + row) * rowHeight;
                Draw(name, 0x52, y);
                DrawRight(group.Pristine.ToString(), 0xca, y);
                if (_tab != 5 && group.Damaged > 0) DrawRight(group.Damaged.ToString(), 0xd4, y);
                DrawRight(Money(group.Price), 0x142, y);
            }
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
                    if (spot == 0 && shell.Career.Mode == 1)
                    { shell.BlockUntilEnd(_exit); ev = shell.Career.Byte23B9 ? 0xc036u : 0xc021u; }
                    else if (spot > 0 && spot < 7)
                    { _tab = spot - 1; _page = 0; _click.Play(); ShowRows(); }
                }
            }
            if (ev == 0xc004) return;
            foreach (var line in Lines) line.Hide();
            Lines.Clear();
            _regions.Dispose(); _regions = null;
            shell.Anims.Clear();
            shell.Surface.DrawAfter.Clear(); shell.Surface.DrawBefore.Clear();
            _click?.Release(); _exit?.Release();
            shell.Unregister();
            shell.Post(ev, 0xc01f);
        }
    }
}
