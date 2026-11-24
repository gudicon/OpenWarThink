using System;
using System.Collections.Generic;
using System.IO;

namespace MW2.Formats
{
    /// <summary>
    /// How a mod draws the destruction of a unit: a JSON file in the mod at effects/mechs/&lt;key&gt;.json, where the key is
    /// the unit's model (ATLAS, COMMANDO), its weight class (light, medium, heavy, assault; mechs only), "mech" (every mech) or
    /// "unit" (every unit with armour: mechs, vehicles, aircraft, turrets) — see <see cref="Keys"/>. Four parts, each optional
    /// and written like the "impact" part of a weapon look (WeaponLook.FlashLook: flash, light, sparks, prefab, hideOriginal):
    /// "death" — the large explosion when the unit is destroyed (Mech_UpdateControl 0x1003a449 state 4, effect 0xd at the
    /// unit's origin); "burst" — each of the smaller explosions over the next 10 seconds (effect 3; the debris throws in
    /// between, effect 0x10b, stay as they are); "section" — a section blown off (an arm, a leg, the centre torso; the
    /// original draws nothing of its own there, only the thrown parts); "blowApart" — a dead unit that lands hard throws its
    /// remaining parts (Mech_BlowApart 0x1003551a); "partBlast" — a thrown-off part that is shot or blasted apart in the
    /// air or on the ground (Part_ShotApart 0x10014fba, effect 7 at the part's centre). hideOriginal hides the original
    /// explosion model and its light; the sound, sun flash, area damage and debris stay. Prefabs stand upright: +Y up, +Z the
    /// unit's forward. A fifth part, "debris", is for the small pieces the wreck throws (effect 0x10b from the explosions on
    /// the wreck, Debris_Move 0x100150c7): "hideOriginal" leaves out their pixel smoke trail, and "prefab" (bundle, asset,
    /// life, scale) rides upright with each piece while it flies and lets go where it stops (its particles stop emitting, then
    /// it fades over "life"). The pieces themselves, their number and their flight stay. Only the look changes.
    /// Port addition with no counterpart in the original program.
    /// </summary>
    public sealed class MechLook
    {
        public const string Folder = "effects/mechs";

        /// <summary>The "debris" part: the pieces thrown by the wreck.</summary>
        public sealed class DebrisLook
        {
            public bool HideOriginal;       // the pixel smoke trail of a flying piece (0x1004e460)
            public WeaponLook.PrefabLook Prefab;
        }

        public string Source;           // full path of the JSON file
        public WeaponLook.FlashLook Death, Burst, Section, BlowApart, PartBlast;
        public DebrisLook Debris;
        public readonly List<string> Warnings = new List<string>();

        /// <summary>
        /// The file names tried for a unit, most specific first: its model name, then for a mech (gamepiece type 1) its weight
        /// class by tonnage (light below 40 t, medium below 60 t, heavy below 80 t, assault from 80 t) and "mech", then "unit".
        /// </summary>
        public static List<string> Keys(string model, int tons, int gamepieceType)
        {
            var keys = new List<string>();
            void Add(string k)
            {
                if (!string.IsNullOrEmpty(k) && !keys.Exists(x => string.Equals(x, k, StringComparison.OrdinalIgnoreCase))) keys.Add(k);
            }
            Add(model);
            if (gamepieceType == 1)
            {
                if (tons > 0) Add(WeightClass(tons));
                Add("mech");
            }
            Add("unit");
            return keys;
        }

        /// <summary>The BattleTech weight class of a mech of <paramref name="tons"/>.</summary>
        public static string WeightClass(int tons) => tons < 40 ? "light" : tons < 60 ? "medium" : tons < 80 ? "heavy" : "assault";

        /// <summary>The mod-relative paths of <see cref="Keys"/>.</summary>
        public static List<string> Paths(string model, int tons, int gamepieceType) =>
            Keys(model, tons, gamepieceType).ConvertAll(k => Folder + "/" + k + ".json");

        /// <summary>
        /// Reads a unit look. A file that is not JSON throws <see cref="MiniJson.Error"/>; values that are wrong or unknown keep
        /// their default and go to <see cref="Warnings"/>. Pictures and bundles are found next to the JSON file, below it, or
        /// elsewhere in the mod's effects folder ("../weapons/mymod").
        /// </summary>
        public static MechLook Parse(string text, string source)
        {
            var look = new MechLook { Source = source };
            if (!(MiniJson.Parse(text) is Dictionary<string, object> root))
                throw new MiniJson.Error("the file must hold one { ... } object");
            var r = new WeaponLook.Reader(look.Warnings, source != null ? Path.GetDirectoryName(source) : null);
            r.Known(root, "", "death", "burst", "section", "blowApart", "partBlast", "debris", "comment");
            look.Death = r.Flash(root, "death");
            look.Burst = r.Flash(root, "burst");
            look.Section = r.Flash(root, "section");
            look.BlowApart = r.Flash(root, "blowApart");
            look.PartBlast = r.Flash(root, "partBlast");
            var debris = r.Object(root, "debris", "debris");
            if (debris != null)
            {
                r.Known(debris, "debris", "hideOriginal", "prefab", "comment");
                look.Debris = new DebrisLook { HideOriginal = r.Flag(debris, "hideOriginal", "debris", false) };
                var prefab = r.Object(debris, "prefab", "debris.prefab");
                if (prefab != null) look.Debris.Prefab = r.Prefab(prefab, "debris.prefab", false);
            }
            return look;
        }
    }
}
