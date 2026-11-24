using MW2.Formats;
using UnityEngine;

namespace MW2
{
    /// <summary>
    /// The mods' destruction pictures (Mw2UnitView.Current while the game runs): for a unit with a look in the active mods
    /// (Mw2WeaponLooks.Get, files effects/mechs/&lt;key&gt;.json) it draws the "death", "burst", "section", "blowApart" and
    /// "partBlast" parts — flash sprite, soft light, sparks going up, a prefab standing upright (+Y up, +Z the unit's
    /// forward) — puts the "debris" prefab on each piece the wreck throws, and tells the unit code whether to draw the
    /// original explosions and debris smoke. Units without a look are drawn as in the original.
    /// Port addition with no counterpart in the original program.
    /// </summary>
    public sealed class Mw2ModUnitView : IUnitView
    {
        public bool Died(Mw2MechCombat unit, Vector3 origin) => Draw(unit, Mw2WeaponLooks.Get(unit)?.Death, origin, "death");

        public bool Burst(Mw2MechCombat unit, Vector3 point) => Draw(unit, Mw2WeaponLooks.Get(unit)?.Burst, point, "burst");

        public void SectionLost(Mw2MechCombat unit, int location, Vector3 point) => Draw(unit, Mw2WeaponLooks.Get(unit)?.Section, point, "section");

        public void BlownApart(Mw2MechCombat unit, Vector3 origin) => Draw(unit, Mw2WeaponLooks.Get(unit)?.BlowApart, origin, "blow apart");

        public bool PartBlast(Mw2MechCombat unit, Vector3 point) => Draw(unit, Mw2WeaponLooks.Get(unit)?.PartBlast, point, "part blast");

        /// <summary>The "debris" part: the prefab rides upright with the piece and lets go where it stops.</summary>
        public bool DebrisThrown(Mw2MechCombat unit, Transform piece, out System.Action landed)
        {
            landed = null;
            var look = Mw2WeaponLooks.Get(unit)?.Debris;
            if (look == null) return true;
            if (look.Prefab != null && piece != null)
            {
                var copy = Mw2ModPrefabs.Place(look.Prefab, piece.position, Quaternion.identity, null);
                if (copy != null)
                {
                    copy.Ride(null, false, false);
                    copy.Follow(piece);
                    landed = () => { if (copy != null) copy.Release(copy.transform.position); };
                }
            }
            return !look.HideOriginal;
        }

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
