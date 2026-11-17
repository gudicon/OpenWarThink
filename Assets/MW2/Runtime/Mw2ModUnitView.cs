using MW2.Formats;
using UnityEngine;

namespace MW2
{
    /// <summary>
    /// The mods' destruction pictures (Mw2UnitView.Current while the game runs): for a unit with a look in the active mods
    /// (Mw2WeaponLooks.Get, files effects/mechs/&lt;key&gt;.json) it draws the "death", "burst", "section" and "blowApart"
    /// parts — flash sprite, soft light, sparks going up, a prefab standing upright (+Y up, +Z the unit's forward) — and
    /// tells the unit code whether to draw the original explosions. Units without a look are drawn as in the original.
    /// Port addition with no counterpart in the original program.
    /// </summary>
    public sealed class Mw2ModUnitView : IUnitView
    {
        public bool Died(Mw2MechCombat unit, Vector3 origin) => Draw(unit, Mw2WeaponLooks.Get(unit)?.Death, origin, "death");

        public bool Burst(Mw2MechCombat unit, Vector3 point) => Draw(unit, Mw2WeaponLooks.Get(unit)?.Burst, point, "burst");

        public void SectionLost(Mw2MechCombat unit, int location, Vector3 point) => Draw(unit, Mw2WeaponLooks.Get(unit)?.Section, point, "section");

        public void BlownApart(Mw2MechCombat unit, Vector3 origin) => Draw(unit, Mw2WeaponLooks.Get(unit)?.BlowApart, origin, "blow apart");

        /// <summary>Draws the part when there is one; returns whether the original is drawn.</summary>
        static bool Draw(Mw2MechCombat unit, WeaponLook.FlashLook part, Vector3 point, string name)
        {
            if (part == null) return true;
            var forward = unit != null ? unit.transform.forward : Vector3.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 1e-6f) forward = Vector3.forward;
            Mw2WeaponLooks.Flash(part, point, Vector3.up, Quaternion.LookRotation(forward.normalized, Vector3.up), "unit " + name);
            return !part.HideOriginal;
        }
    }
}
