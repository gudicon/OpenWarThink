using System;
using System.Collections.Generic;

namespace MW2.Formats
{
    /// <summary>
    /// Level of detail of the mech models a mod may ask for, file graphics/detail.json in the mod (the lowest active mod
    /// with the file wins, as for every file). It only changes the look, so it lives apart from the fixes of
    /// rules/fixes.json and from the picture of graphics/quality.json: a mod with it combines with a fix mod and with a
    /// picture mod. Without such a file the units switch their models by distance as the original (Mw2LodSwitcher).
    /// "maxMechDetail": every external unit model, wrecks included, keeps its most detailed set (REPR1) at any
    /// distance; the cockpit view keeps its own model.
    /// "opaqueGlass": the windshields of external units (faces of the glass key 0x18c) are drawn solid in one colour (the
    /// average of the glass picture, which scrolls with the sky clouds), without palette alpha and without holes; in missions without a texture on that key they take MCC1CLOU instead of being left out.
    /// The cockpit shell, the insignia, the sky and the scenery keep the original look.
    /// Port addition with no counterpart in the original program.
    /// </summary>
    public sealed class MechDetail
    {
        public const string File = "graphics/detail.json";

        public string Source;
        public bool MaxMechDetail;
        public bool OpaqueGlass;
        public readonly List<string> Warnings = new List<string>();

        /// <summary>Reads the file; a file that is not JSON throws <see cref="MiniJson.Error"/>, unknown names and wrong
        /// values go to <see cref="Warnings"/> and keep the original's behaviour.</summary>
        public static MechDetail Parse(string text, string source)
        {
            var detail = new MechDetail { Source = source };
            if (!(MiniJson.Parse(text) is Dictionary<string, object> root))
                throw new MiniJson.Error("the file must hold one { ... } object");
            foreach (var pair in root)
            {
                if (string.Equals(pair.Key, "comment", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(pair.Key, "maxMechDetail", StringComparison.OrdinalIgnoreCase))
                {
                    if (pair.Value is bool b) detail.MaxMechDetail = b;
                    else detail.Warnings.Add("maxMechDetail: expected true or false");
                }
                else if (string.Equals(pair.Key, "opaqueGlass", StringComparison.OrdinalIgnoreCase))
                {
                    if (pair.Value is bool b) detail.OpaqueGlass = b;
                    else detail.Warnings.Add("opaqueGlass: expected true or false");
                }
                else detail.Warnings.Add($"unknown name \"{pair.Key}\" (known: maxMechDetail, opaqueGlass)");
            }
            return detail;
        }
    }
}
