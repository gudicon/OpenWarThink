using System;
using System.Collections.Generic;

namespace MW2.Formats
{
    /// <summary>
    /// Fixes a mod may switch on, file rules/fixes.json in the mod (the lowest active mod with the file wins, as for every
    /// file). Unlike a look, a fix changes how the game plays, so every player of a network game needs it too (the mods
    /// fingerprint already demands the same mods). Without such a file everything is the original's.
    /// "fixInstantShots": a shot starts its hit test at its own start point, not where the last shot of its projectile
    /// slot stopped (ShotSlotPool.FreshStart; Shot_Clear 0x10010dcf keeps that point, so in the original every second
    /// shot at a target that stood still hits at once).
    /// Port addition with no counterpart in the original program.
    /// </summary>
    public sealed class GameRules
    {
        public const string File = "rules/fixes.json";

        public string Source;
        public bool FixInstantShots;
        public readonly List<string> Warnings = new List<string>();

        /// <summary>Reads the file; a file that is not JSON throws <see cref="MiniJson.Error"/>, unknown names and wrong
        /// values go to <see cref="Warnings"/> and keep the original's behaviour.</summary>
        public static GameRules Parse(string text, string source)
        {
            var rules = new GameRules { Source = source };
            if (!(MiniJson.Parse(text) is Dictionary<string, object> root))
                throw new MiniJson.Error("the file must hold one { ... } object");
            foreach (var pair in root)
            {
                if (string.Equals(pair.Key, "comment", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(pair.Key, "fixInstantShots", StringComparison.OrdinalIgnoreCase))
                {
                    if (pair.Value is bool b) rules.FixInstantShots = b;
                    else rules.Warnings.Add("fixInstantShots: expected true or false");
                }
                else rules.Warnings.Add($"unknown name \"{pair.Key}\" (known: fixInstantShots)");
            }
            return rules;
        }
    }
}
