using UnityEngine;
using System.Collections.Generic;

namespace MW2
{
    /// <summary>Scenery collision type (OBJ +4) and common model-list activation
    /// order 0x10049159, used by ground and first-hit queries 0x10018ad5.</summary>
    public class Mw2Surface : MonoBehaviour
    {
        // Common collision-model lifecycle 0x10049159 / 0x100492b6.
        internal static readonly List<Mw2Surface> All = new List<Mw2Surface>();
        public int CollisionType;
        [HideInInspector] public BoxCollider AxisBox;
        [HideInInspector] public Renderer[] BoxSources;
        internal long CollisionOrder { get; private set; }
        void OnEnable() { CollisionOrder = Formats.SceneCollisionOrder.Activate(); All.Insert(0, this); }
        void OnDisable() => All.Remove(this);
        internal bool UsesNativeBox => (CollisionType == 0 || CollisionType == 1) && AxisBox != null;

        /// <summary>Object box bounds 0x10019d1f / 0x10019e70 use transformed world
        /// vertices. A path rotation must change the world-axis box, not rotate its old shape.</summary>
        public bool RefreshWorldBox()
        {
            if (AxisBox == null || !AxisBox.enabled || BoxSources == null) return false;
            Bounds bounds = default; bool found = false;
            foreach (var renderer in BoxSources)
            {
                if (renderer == null) continue;
                if (found) bounds.Encapsulate(renderer.bounds); else { bounds = renderer.bounds; found = true; }
            }
            if (!found) return false;
            AxisBox.transform.SetPositionAndRotation(bounds.center, Quaternion.identity);
            var scale = AxisBox.transform.lossyScale;
            AxisBox.center = Vector3.zero;
            AxisBox.size = new Vector3(scale.x != 0 ? bounds.size.x / Mathf.Abs(scale.x) : 0,
                scale.y != 0 ? bounds.size.y / Mathf.Abs(scale.y) : 0, scale.z != 0 ? bounds.size.z / Mathf.Abs(scale.z) : 0);
            return true;
        }

        /// <summary>Types with a height function in the table at 0x1008faf0: 0 (box top) and 5 (surface).</summary>
        public bool Walkable => CollisionType == 0 || CollisionType == 5;
    }
}
