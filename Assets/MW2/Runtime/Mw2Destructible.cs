using System;
using System.Collections.Generic;
using UnityEngine;

namespace MW2
{
    /// <summary>
    /// Destructible scenery object from a GT chunk: takes "Strength" points of damage, then hides the intact model and
    /// shows its replacement object (GT +2, hidden from the start, moved to the intact object's pose: 0x1005f301). The objective mask ties it to MTBL orders with the same bit.
    /// Damage rules are a stand-in until the engine's structure damage code is ported.
    /// GT record fields used by targeting (0x10066c17 / 0x10067406 / 0x10067668): +0 flags, +2 identifying teams,
    /// +0xC affiliation (side through Gt_Side 0x1002a7dc), +0x10 model radius, +0x14 name, +0x2A contents.
    /// </summary>
    public class Mw2Destructible : MonoBehaviour
    {
        /// <summary>GT records in load order (index of FUN_10070e0e 0x10070e0e, DAT_1025f3e0 + index * 0x40).</summary>
        public static readonly List<Mw2Destructible> All = new List<Mw2Destructible>();

        public string File;
        public string DisplayName;
        [Tooltip("GT +0x2A (chunk +0x2E): the text Hud_DrawTargetText 0x10065990 shows after inspecting it with I.")]
        public string Contents;
        public int ObjectiveMask;
        [Tooltip("GT +0 flags from the chunk (0x400 targetable / on radar, 0x800 never a player target, 0x1000 any range).")]
        public int Flags;
        [Tooltip("Gt_Side 0x1002a7dc: side of the AFFL affiliation (0 player's side, 1 enemy, 2 neutral / none).")]
        public int Side = 2;
        [Tooltip("Load order of the GT (its index in DAT_1025f3e0).")]
        public int Index;
        [Tooltip("GT +0x10: radius of the intact model (+0x40, AfterWorldLoader 0x100739f9), metres.")]
        public float Radius;
        public float Strength = 25;
        public bool Destroyed;
        [Tooltip("GT +2: the replacement object, hidden until destruction (FUN_1005f27b 0x1005f27b).")]
        public Transform Replacement;
        public Renderer[] Debris = new Renderer[0];
        /// <summary>GT +2: teams that have identified it (bit = team), set with the flag 0x20 by 0x10067668.</summary>
        [NonSerialized] public int IdentifiedTeams;

        /// <summary>GT +0 as the engine sees it: the chunk flags, 0x4 once destroyed (0x1005f301), 0x20 once identified.</summary>
        public int GtFlags => Flags | (Destroyed ? 4 : 0) | (IdentifiedTeams != 0 ? 0x20 : 0);

        void OnEnable() { if (!All.Contains(this)) All.Add(this); All.Sort((a, b) => a.Index.CompareTo(b.Index)); }
        void OnDestroy() => All.Remove(this);

        /// <summary>
        /// Building_Damage 0x10013010: a destroyed GT (+0 & 4) or one whose strength +8 is 0 takes no damage; otherwise
        /// +8 -= _ftol(damage) (0x100865dc, truncation toward zero), and at 0 or below it is destroyed. Area damage
        /// (0x10012d1a) passes ticks * damage per tick every update, so a fraction below 1 does nothing, as in the
        /// original. Not ported: a node whose type (+2 & 0xf0) is 0xb0 drops to 0 at any hit and uses impact effect 0xd.
        /// </summary>
        public void ApplyDamage(float damage, Mw2MechCombat source)
        {
            if (Destroyed || Strength == 0) return;
            Strength -= (int)damage;
            if (Strength > 0) return;
            Strength = 0;
            CountDestroyed(source);
            DestroyGt(source != null ? $" by {source.DisplayName}" : "");
        }

        /// <summary>
        /// FUN_1005f301 0x1005f301 for this GT, then the FUN_1005f52b 0x1005f52b pass that FUN_1005f481 0x1005f481 runs
        /// after it: every scene node whose parent node carries 0x200 (destroyed) and which itself lacks it gets
        /// FUN_1005f301 too, so a GT node placed under a destroyed GT node is destroyed with it (GT +0 |= 4, its own
        /// replacement shown). Hiding alone left it Destroyed = false, invisible and without colliders: SIG1's four
        /// sig1are1 s-1apgls GTs sit under the Control Tower node 10, so destroying the tower first made the mandatory
        /// air-field order unreachable. The cascade calls neither FUN_10012e9d's statistics nor its 0x40 hook.
        /// The single index-ordered pass of 1005f52b reaches deeper GTs at once only when their parents precede them;
        /// this recursion reaches them at once in every case (the next pass of the original would finish the rest).
        /// </summary>
        void DestroyGt(string cause)
        {
            Destroyed = true;
            foreach (var r in GetComponentsInChildren<Renderer>()) r.enabled = false;
            foreach (var c in GetComponentsInChildren<Collider>()) c.enabled = false;
            // FUN_1005f301 0x1005f301: the replacement takes the intact object's matrix (a moving car leaves its wreck
            // where it was hit) and becomes visible.
            if (Replacement != null) Replacement.SetPositionAndRotation(transform.position, transform.rotation);
            foreach (var r in Debris) if (r != null) r.enabled = true;
            Debug.Log($"MW2: {DisplayName} ({File}) destroyed{cause}");
            for (int i = 0; i < transform.childCount; i++)
                if (transform.GetChild(i).TryGetComponent<Mw2Destructible>(out var child) && !child.Destroyed)
                    child.DestroyGt($" with its parent {DisplayName}");
        }

        /// <summary>
        /// FUN_10012e9d 0x10012e9d, statistics DAT_101fd6e0: a structure destroyed by the player counts in +0xd/+0xf/+0x11,
        /// one destroyed by a member of the player's team in +0x24/+0x26/+0x28, by Gt_Side.
        /// </summary>
        void CountDestroyed(Mw2MechCombat source)
        {
            if (source == null) return;
            var stats = Mw2MissionDirector.Stats;
            if (source.IsPlayer)
                stats.AddBySide(Side, Formats.CombatCareer.FriendlyBldgKills, Formats.CombatCareer.EnemyBldgKills,
                                Formats.CombatCareer.NeutralBldgKills);
            var player = Mw2MechCombat.PlayerUnit;
            if (player != null && source.Team == player.Team)
                stats.AddBySide(Side, Formats.CombatCareer.StarFriendlyBldgKills, Formats.CombatCareer.StarEnemyBldgKills,
                                Formats.CombatCareer.StarNeutralBldgKills);
        }
    }
}
