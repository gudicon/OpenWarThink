using UnityEngine;
using System.Collections.Generic;

namespace MW2
{
    /// <summary>
    /// Unit part hit metadata (0x10018ad5 -> 100047ff / Object_SegmentTest type 6).
    /// Original model-sphere bounds come from 0x10003efa; section comes from Mw2HitMap (OBJL).
    /// </summary>
    public class Mw2HitPart : MonoBehaviour
    {
        // Unity registry for the original scene-object scan in FUN_10018ad5.
        internal static readonly List<Mw2HitPart> All = new List<Mw2HitPart>();
        public bool SphereReady;
        public Vector3 LocalCentre;
        public float ModelRadius;
        public Collider HitCollider;
        /// <summary>Lod_SetForGamepiece 0x1001e9df / Lod_SwapModel 0x1001eb15 replace
        /// each attached part's model. Detached parts freeze their selected model (flags 0x50).</summary>
        public Mw2LodGroup LodGroup;
        public int Representation;
        public bool CurrentModel => LodGroup == null || !transform.IsChildOf(LodGroup.transform) || Representation == LodGroup.Current;
        // 10049159 inserts a newly collision-enabled model at the head of the global list.
        // 10018ad5 retains the first equal-distance hit in the shared GP/GT list.
        internal long CollisionOrder { get; private set; }
        void OnEnable() { CollisionOrder = Formats.SceneCollisionOrder.Activate(); All.Insert(0, this); }
        void OnDisable() => All.Remove(this);
        /// <summary>Lod_SwapModel 0x1001eb15 registers a freshly loaded representation
        /// through 0x10048e5f. Cached Unity representations must take that same list position.</summary>
        internal void ModelActivated()
        {
            if (!isActiveAndEnabled) return;
            All.Remove(this);
            All.Insert(0, this);
            CollisionOrder = Formats.SceneCollisionOrder.Activate();
        }
        /// <summary>Layer of the part colliders, left out of the movement casts (Mw2MechController).</summary>
        public const int Layer = 30;
        public const int UnitRootLayer = 31;
        public const int MovementMask = ~((1 << Layer) | (1 << UnitRootLayer));
    }

    /// <summary>Unity result adapter for FUN_10018ad5: scene collider or original model-sphere hit.
    /// RaycastHit cannot carry a collider for a ray that starts inside a sphere.</summary>
    public struct Mw2ShotHit
    {
        public Collider collider;
        public Vector3 point, normal;
        public float distance;
        public Mw2ShotHit(RaycastHit hit) { collider = hit.collider; point = hit.point; normal = hit.normal; distance = hit.distance; }
    }
}
