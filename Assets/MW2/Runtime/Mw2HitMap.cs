using System.Collections.Generic;
using UnityEngine;

namespace MW2
{
    /// <summary>Body section of the model nodes (OBJL chunks, 0x1001f074): node -> MEK section 0..7.</summary>
    public class Mw2HitMap : MonoBehaviour
    {
        public List<Transform> Nodes = new List<Transform>();
        public List<int> Sections = new List<int>();
        int[] _levels;      // model word +0 & 0xf0 of each mapped node (FUN_10003bc4), 0 at load

        /// <summary>FUN_10051da0 -> FUN_10052148 / FUN_1001f48b reloads the undamaged model at regeneration.</summary>
        public void ResetDamage() => _levels = null;

        /// <summary>Section of the nearest mapped node at or above <paramref name="t"/>, or -1.</summary>
        public int SectionOf(Transform t)
        {
            for (; t != null && t != transform; t = t.parent)
            {
                int i = Nodes.IndexOf(t);
                if (i >= 0) return Sections[i];
            }
            return -1;
        }

        /// <summary>
        /// The part's own node has an OBJL section (no inheritance from the parents, as the per-record section +0x20 of
        /// 0x1001f074). A renderer is its node, or a "LOD{n} ..." child that carries another representation of it.
        /// </summary>
        public bool HasOwnSection(Transform part)
        {
            return Nodes.Contains(Mw2LodGroup.IsLodChild(part) ? part.parent : part);
        }

        /// <summary>Damage level (0..15) of the part's own node; 0 for a node without a section.</summary>
        public int LevelOf(Transform part)
        {
            int i = Nodes.IndexOf(Mw2LodGroup.IsLodChild(part) ? part.parent : part);
            return i >= 0 && _levels != null && i < _levels.Length ? _levels[i] : 0;
        }

        /// <summary>
        /// FUN_1005b367 0x1005b367 from the unit's root object: only for 0 < level < 16; a node whose own level is lower takes
        /// the level when its model carries the section (+0x16) and passes it on to its children; a node at or above the
        /// level stops the walk for its whole subtree (so a damaged centre torso can hold back a lower leg level).
        /// </summary>
        public void RaiseDamage(int level, int section)
        {
            if (level <= 0 || level >= 16) return;
            if (_levels == null || _levels.Length != Nodes.Count) _levels = new int[Nodes.Count];
            foreach (Transform child in transform)
                if (!Mw2LodGroup.IsLodChild(child)) Raise(child, level, section);
        }

        void Raise(Transform node, int level, int section)
        {
            int i = Nodes.IndexOf(node);
            if (i >= 0 && _levels[i] >= level) return;
            if (i >= 0 && Sections[i] == section) _levels[i] = level;
            foreach (Transform child in node)
                if (!Mw2LodGroup.IsLodChild(child)) Raise(child, level, section);
        }
    }
}
