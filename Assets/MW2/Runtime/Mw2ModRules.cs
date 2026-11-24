using System;
using System.IO;
using MW2.Formats;
using UnityEngine;

namespace MW2
{
    /// <summary>
    /// Applies the fixes of the active mods (GameRules, file rules/fixes.json) to the port's neutral switches: at start,
    /// after the mods screen saved a choice and when a rules file changes while the game runs (Mw2WeaponLooks polls).
    /// Without a rules file every switch is off and the game plays as the original. The same moments read the mech detail
    /// of the mods (MechDetail, file graphics/detail.json), a look kept apart from the fixes.
    /// Port addition with no counterpart in the original program.
    /// </summary>
    public static class Mw2ModRules
    {
        /// <summary>The rules in force (all off when no active mod has the file).</summary>
        public static GameRules Current { get; private set; } = new GameRules();

        /// <summary>The mech detail in force (the original's switching by distance when no active mod has the file).</summary>
        public static MechDetail Detail { get; private set; } = new MechDetail();

        public static void Apply()
        {
            ApplyDetail();
            string path = null;
            try { path = Mw2Mods.Resolve(GameRules.File); }
            catch (Exception e) { Debug.LogWarning("MW2 mods: " + e.Message); }
            var rules = new GameRules();
            if (path != null)
            {
                string shown = Mw2WeaponLooks.Shown(path);
                try
                {
                    rules = GameRules.Parse(File.ReadAllText(path), path);
                    foreach (var w in rules.Warnings) Mw2WeaponLooks.Say(shown + ": " + w, true);
                }
                catch (Exception e) when (e is MiniJson.Error || e is IOException || e is UnauthorizedAccessException)
                {
                    Mw2WeaponLooks.Say(shown + ": " + e.Message + " (the original rules are used)", true);
                    rules = new GameRules();
                }
            }
            bool changed = rules.FixInstantShots != ShotSlotPool.FreshStart;
            Current = rules;
            ShotSlotPool.FreshStart = rules.FixInstantShots;
            if (changed || rules.FixInstantShots)
                Debug.Log("MW2 mods: fix instant shots " + (rules.FixInstantShots ? "on (" + Mw2WeaponLooks.Shown(path) + ")" : "off"));
            if (changed) Mw2WeaponLooks.Say("Instant second shots: " + (rules.FixInstantShots ? "fixed by " + Mw2WeaponLooks.Shown(path) : "as in the original"), false);
        }

        static void ApplyDetail()
        {
            string path = null;
            try { path = Mw2Mods.Resolve(MechDetail.File); }
            catch (Exception e) { Debug.LogWarning("MW2 mods: " + e.Message); }
            var detail = new MechDetail();
            if (path != null)
            {
                string shown = Mw2WeaponLooks.Shown(path);
                try
                {
                    detail = MechDetail.Parse(File.ReadAllText(path), path);
                    foreach (var w in detail.Warnings) Mw2WeaponLooks.Say(shown + ": " + w, true);
                }
                catch (Exception e) when (e is MiniJson.Error || e is IOException || e is UnauthorizedAccessException)
                {
                    Mw2WeaponLooks.Say(shown + ": " + e.Message + " (the original detail is used)", true);
                    detail = new MechDetail();
                }
            }
            bool changed = detail.MaxMechDetail != Detail.MaxMechDetail;
            bool glassChanged = detail.OpaqueGlass != Detail.OpaqueGlass;
            Detail = detail;
            // MW2/TexturedAlphaLit reads it on the faces of units (model flag 0x100) outside the cockpit view.
            Shader.SetGlobalFloat("_Mw2OpaqueUnitGlass", detail.OpaqueGlass ? 1f : 0f);
            if (glassChanged)
            {
                Debug.Log("MW2 mods: opaque unit glass " + (detail.OpaqueGlass ? "on (" + Mw2WeaponLooks.Shown(path) + ")" : "off"));
                Mw2WeaponLooks.Say("Unit glass: " + (detail.OpaqueGlass ? "opaque by " + Mw2WeaponLooks.Shown(path) : "as in the original"), false);
            }
            if (changed)
            {
                Debug.Log("MW2 mods: maximum mech detail " + (detail.MaxMechDetail ? "on (" + Mw2WeaponLooks.Shown(path) + ")" : "off"));
                Mw2WeaponLooks.Say("Mech detail: " + (detail.MaxMechDetail ? "maximum by " + Mw2WeaponLooks.Shown(path) : "as in the original"), false);
            }
        }
    }
}
