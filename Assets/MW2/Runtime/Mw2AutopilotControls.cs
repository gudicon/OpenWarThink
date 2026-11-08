using UnityEngine;

namespace MW2
{
    public partial class Mw2MechController
    {
        public void PrepareTestPilot() { Autopilot = false; _feetToTorso = false; }
    }
    public partial class Mw2MechCombat
    {
        /// <summary>Read-only reuse of the player's existing target filters (0x10067128 / 0x10067406).</summary>
        public bool PilotCanSelect(Mw2MechCombat unit) => CheckUnit(unit, 0);
        public bool PilotCanSelect(Mw2Destructible structure) => CheckStructure(structure, 0);

        /// <summary>Test pilot selects exactly like the target ring; it cannot select hidden/out-of-range entries.</summary>
        public bool PilotSelect(Mw2MechCombat unit, Mw2Destructible structure)
        {
            if (unit != null && PilotCanSelect(unit)) { SetTarget(unit, null); return true; }
            if (structure != null && PilotCanSelect(structure)) { SetTarget(null, structure); return true; }
            return false;
        }
        public bool PilotWeaponWorks(int index) => index >= 0 && index < Weapons.Count && Works(index);
        public string DeathSourceName => _killer != null ? _killer.DisplayName : "unknown";
    }
}
