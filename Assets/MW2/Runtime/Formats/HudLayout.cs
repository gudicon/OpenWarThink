using System;
using System.IO;
using UnityEngine;

namespace MW2.Formats
{
    /// <summary>
    /// Cockpit gauge layout in 320x200 screen units. The HUD has 34 gauges (mercsw.dll 0x10016ed7 creates them):
    /// rectangle table at 0x1008f598 (0x14 bytes: pointer, x0, y0, x1, y1 inclusive), text origin at 0x1008f840
    /// (two 16.16 fractions of the rectangle size). The CPIT resource named by the mech's CPTF chunk (MW2MECH) overrides
    /// the first 19 rectangles (0x100248c1: 5 unused rectangles, then 19 of i16 x, y, w, h; x1 = x + w - 1).
    /// Gauges found so far: 0 radar, 1 damage wireframe, 3..16 weapon rows (two columns of 7, 0x1006b8a0), 17 nav window,
    /// 18 nav text, 22 speed (0x100696cb), 24/25 heat (0x1006994c, dH/dT 0x10069bce), 26 altitude and jets (0x10069c8a),
    /// 27/28 compass (0x10032fa2). Gauges 2, 17, 22, 26, 31, 32, 33 are the seven windows toggled by mercs.hud.
    /// </summary>
    [Serializable]
    public sealed class HudLayout
    {
        public const int GaugeCount = 34;
        const uint RectTableVa = 0x1008f598;
        const uint OriginTableVa = 0x1008f840;

        public RectInt[] Rects = new RectInt[GaugeCount];   // x, y, width, height (inclusive x1/y1 + 1)
        public Vector2[] Origins = new Vector2[GaugeCount]; // text origin as a fraction of the rectangle

        /// <summary>
        /// HUD resource (Hud_LoadHudf 0x10024696, 292 bytes): 5 pairs, 3 ints (the first is the SHP id of the damage
        /// wireframe, WIREDM = 238; the resolution variant is the next id), 15 rectangles x, y, w, h in pixels of that
        /// base sprite (34x37). 0x10060920 scales them to the drawn variant through 16.16 fractions of the base size.
        /// </summary>
        public int DamageShapeId = -1;
        public RectInt[] DamageRegions = new RectInt[0];

        /// <summary>Section (0-based, mech +0x58 records) shown by each damage region: table 0x1009fc80 minus one.</summary>
        public static readonly int[] DamageRegionSection = { 0, 2, 2, 1, 1, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, -1 };

        /// <summary>Name of the extracted gauge table in a <see cref="TableStore"/>.</summary>
        public const string TableName = "hud";

        /// <summary>The gauge table of mercsw.dll (0x1008f598 rectangles, 0x1008f840 text origins).</summary>
        public static HudTableFile LoadTable(string dllPath)
        {
            byte[] d = File.ReadAllBytes(dllPath);
            var t = new HudTableFile();
            for (int i = 0; i < GaugeCount; i++)
            {
                int r = PeImage.FileOffset(d, RectTableVa + (uint)(i * 0x14));
                int o = PeImage.FileOffset(d, OriginTableVa + (uint)(i * 8));
                t.Gauges.Add(new HudGaugeDef
                {
                    X0 = BitConverter.ToInt32(d, r + 4), Y0 = BitConverter.ToInt32(d, r + 8),
                    X1 = BitConverter.ToInt32(d, r + 12), Y1 = BitConverter.ToInt32(d, r + 16),
                    OriginX = BitConverter.ToInt32(d, o), OriginY = BitConverter.ToInt32(d, o + 4),
                });
            }
            return t;
        }

        /// <summary>The gauge table as the engine uses it: the extracted hud.json, written from mercsw.dll on first use.</summary>
        public static HudTableFile GetTable(string gameFolder, TableStore store = null)
        {
            if (store == null) store = TableStore.Default;
            var t = store.Extract(TableName, () => LoadTable(Path.Combine(gameFolder, "mercsw.dll")));
            if (t.Format != HudTableFile.CurrentFormat)
                throw new InvalidDataException($"{store.PathFor(TableName)} has format {t.Format}, this build reads " +
                    $"{HudTableFile.CurrentFormat}: move it aside (keeping your edits) and run MW2 -> Tables -> Extract missing.");
            return t;
        }

        /// <summary>Layout from the gauge table read straight from mercsw.dll.</summary>
        public static HudLayout Load(string dllPath, byte[] cpit, byte[] hud = null) => Load(LoadTable(dllPath), cpit, hud);

        /// <summary>Layout from a gauge table, the mech's CPIT override and its HUD resource.</summary>
        public static HudLayout Load(HudTableFile table, byte[] cpit, byte[] hud = null)
        {
            var layout = new HudLayout();
            for (int i = 0; i < GaugeCount; i++)
            {
                var g = table.Gauges[i];
                layout.Rects[i] = new RectInt(g.X0, g.Y0, g.X1 - g.X0 + 1, g.Y1 - g.Y0 + 1);
                layout.Origins[i] = new Vector2(g.OriginX / 65536f, g.OriginY / 65536f);
            }
            if (cpit != null && cpit.Length >= (5 + 19) * 8)
                for (int i = 0; i < 19; i++)
                {
                    int o = (5 + i) * 8;
                    layout.Rects[i] = new RectInt(BitConverter.ToInt16(cpit, o), BitConverter.ToInt16(cpit, o + 2),
                                                  BitConverter.ToInt16(cpit, o + 4), BitConverter.ToInt16(cpit, o + 6));
                }
            if (hud != null && hud.Length >= (10 + 3 + 15 * 4) * 4)
            {
                layout.DamageShapeId = BitConverter.ToInt32(hud, 10 * 4);
                layout.DamageRegions = new RectInt[15];
                for (int i = 0; i < 15; i++)
                {
                    int o = (13 + i * 4) * 4;
                    int x = BitConverter.ToInt32(hud, o), y = BitConverter.ToInt32(hud, o + 4);
                    int w = BitConverter.ToInt32(hud, o + 8), h = BitConverter.ToInt32(hud, o + 12);
                    // 0x10024696 zeroes rectangles with values outside 0..100.
                    bool ok = x >= 0 && x <= 100 && y >= 0 && y <= 100 && w >= 0 && w <= 100 && h >= 0 && h <= 100;
                    layout.DamageRegions[i] = ok ? new RectInt(x, y, w, h) : new RectInt(0, 0, 0, 0);
                }
            }
            return layout;
        }

        /// <summary>
        /// Region of every pixel of the drawn wireframe (-1 = none): 0x10060f93 redraws the sprite clipped to each region
        /// in order, so a later region wins where they overlap. Region rectangles are in pixels of the base sprite
        /// (<paramref name="baseWidth"/> x <paramref name="baseHeight"/>) and scale to the drawn size like 0x1004aa30.
        /// </summary>
        public int[] DamageRegionMap(int width, int height, int baseWidth, int baseHeight, int scaleWidth = 0, int scaleHeight = 0)
        {
            // Pixels of the drawn sprite come from its header size (scaleWidth/Height); the map covers the whole canvas.
            int sw = scaleWidth > 0 ? scaleWidth : width, sh = scaleHeight > 0 ? scaleHeight : height;
            var map = new int[width * height];
            for (int i = 0; i < map.Length; i++) map[i] = -1;
            for (int r = 0; r < DamageRegions.Length; r++)
            {
                var rect = DamageRegions[r];
                if (rect.width <= 0 || rect.height <= 0) continue;
                // Fractions of the base size (x / base * 65536 + 0.5), then pixels of the drawn sprite.
                int Px(int v, int b, int size) => (int)(((long)(int)(v / (double)b * 65536 + 0.5) * size) >> 16);
                int x0 = Px(rect.x, baseWidth, sw), y0 = Px(rect.y, baseHeight, sh);
                int x1 = x0 + Px(rect.width, baseWidth, sw) - 1, y1 = y0 + Px(rect.height, baseHeight, sh) - 1;
                for (int y = Math.Max(0, y0); y <= Math.Min(height - 1, y1); y++)
                    for (int x = Math.Max(0, x0); x <= Math.Min(width - 1, x1); x++)
                        map[y * width + x] = r;
            }
            return map;
        }

        /// <summary>
        /// Damage scale of a section set when the mech loads (Mech_LoadMek 0x1002f9e0, before the player's armour is
        /// multiplied by 4): low nibble trunc((internal + front) / 5), high nibble trunc((internal + rear) / 5), each 0..15.
        /// </summary>
        public static int DamageScale(int front, int rear, int internalStructure)
        {
            int a = Mathf.Clamp((int)((internalStructure + front) / 5f), 0, 15);
            int b = Mathf.Clamp((int)((internalStructure + rear) / 5f), 0, 15);
            return a | (b << 4);
        }

        /// <summary>
        /// Wireframe colour of a section (0x10060f93): level = 15 - trunc((armour + internal) * 3 / scale) for the front
        /// (low nibble) and the rear (high nibble), the larger one, limited to 0..15 (armour here is the MEK value; the
        /// original stores the player's armour x4 and divides by 4). Destroyed (flag 0x2000) -> 0, level >= 12 -> 0x0b,
        /// level > 0 -> 3, otherwise the sprite colour 6.
        /// </summary>
        public static int DamageColor(int front, int rear, int internalStructure, int scale, bool destroyed)
        {
            if (destroyed) return 0;
            int Level(int armour, int n) => n == 0 ? 0 : Mathf.Clamp(15 - (int)((armour + internalStructure) * 3f / n), 0, 15);
            int level = Math.Max(Level(rear, (scale >> 4) & 0xF), Level(front, scale & 0xF));
            return level >= 12 ? 0x0b : level > 0 ? 3 : 6;
        }

        static bool LeftSide(int section) => section == 5 || section == 3 || section == 7;    // left arm, torso, leg
        static bool RightSide(int section) => section == 4 || section == 1 || section == 6;   // right arm, torso, leg

        /// <summary>
        /// Weapon rows as mercsw.dll 0x10016803 lays them out. Left-side weapons fill gauges 3..9 (continuing after the
        /// right column's weapons when full), right-side weapons fill 10..16 (continuing in the left column when full),
        /// centre torso and head weapons alternate, first to the right column. Then the weapon slots are renumbered:
        /// left rows get the even slots 0, 2, 4..., right rows the odd ones. Returns each weapon's gauge (-1 = none, at most
        /// 14 weapons) and <paramref name="slots"/>, each weapon's new slot. The original array keeps gaps where a column
        /// is shorter; callers that need a dense list sort by slot.
        /// </summary>
        public static int[] AssignWeaponRows(System.Collections.Generic.IList<int> sections, out int[] slots)
        {
            int n = Math.Min(14, sections.Count);
            var gauge = new int[sections.Count];
            for (int i = 0; i < gauge.Length; i++) gauge[i] = -1;
            int rightCount = 0;
            for (int i = 0; i < n; i++) if (RightSide(sections[i])) rightCount++;

            int left = 3, afterRight = 10 + rightCount;
            for (int i = 0; i < n; i++)
            {
                if (LeftSide(sections[i])) gauge[i] = left++;
                if (left == 10) left = afterRight;
            }
            int right = 10;
            for (int i = 0; i < n; i++)
            {
                if (RightSide(sections[i])) gauge[i] = right++;
                if (right > 16) right = left;
            }
            bool toRight = true;
            for (int i = 0; i < n; i++)
            {
                if (sections[i] != 2 && sections[i] != 0) continue;
                int t;
                if (toRight && left < right)
                {
                    t = right;
                    if (right == 17) t = left++;
                    else right++;
                    toRight = false;
                }
                else
                {
                    t = left;
                    if (left == 10) t = right++;
                    else left++;
                    toRight = true;
                }
                gauge[i] = t;
            }

            slots = new int[sections.Count];
            for (int i = 0; i < slots.Length; i++) slots[i] = int.MaxValue;
            int even = 0, odd = 1;
            for (int g = 3; g < 10; g++)
                for (int i = 0; i < n; i++)
                    if (gauge[i] == g) { slots[i] = even; even += 2; }
            for (int g = 10; g < 17; g++)
                for (int i = 0; i < n; i++)
                    if (gauge[i] == g) { slots[i] = odd; odd += 2; }
            return gauge;
        }
    }
    /// <summary>A gauge of 0x1008f598 (rectangle, inclusive) with its text origin of 0x1008f840 (16.16 fractions).</summary>
    [Serializable]
    public sealed class HudGaugeDef
    {
        public int X0, Y0, X1, Y1;
        public int OriginX, OriginY;
    }

    /// <summary>The 34 cockpit gauges of mercsw.dll as the extracted hud.json (the rectangle table's pointer +0 is left out).</summary>
    [Serializable]
    public sealed class HudTableFile
    {
        public const int CurrentFormat = 1;
        public int Format = CurrentFormat;
        public System.Collections.Generic.List<HudGaugeDef> Gauges = new System.Collections.Generic.List<HudGaugeDef>();
    }
}
