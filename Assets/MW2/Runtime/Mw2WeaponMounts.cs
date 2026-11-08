using UnityEngine;

namespace MW2
{
    /// <summary>
    /// Muzzle nodes of a mech by MEK section (POFO chunks, mech +0xd98[section], 0x10070e92); Weapons_Update
    /// (0x1006bc57) takes the weapon slot's section (+0x28) as the shot origin.
    /// </summary>
    public class Mw2WeaponMounts : MonoBehaviour
    {
        public Transform[] Sections = new Transform[8];

        public Transform MuzzleNode(int section) => section >= 0 && section < Sections.Length ? Sections[section] : null;

        public Vector3 Muzzle(int section, Vector3 fallback) =>
            section >= 0 && section < Sections.Length && Sections[section] != null ? Sections[section].position : fallback;
    }
}
