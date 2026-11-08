using System;
using System.Collections.Generic;
using UnityEngine;

namespace MW2
{
    /// <summary>All representations (REPR 1..5) of one unit, built on the same skeleton; one level is visible at a time.</summary>
    [ExecuteAlways]
    public class Mw2LodGroup : MonoBehaviour
    {
        [Serializable]
        public class Level
        {
            public int Lod;                 // 1 = most detailed
            public Renderer[] Renderers;
        }

        public List<Level> Levels = new List<Level>();
        public int Current = 1;

        /// <summary>
        /// Renderers of the cockpit shell (BWD node flags 0xA0 and its children, LOD5). Command 0x13 toggles
        /// DAT_1008fcf4 (default 1); off, 0x1001bea2 skips the shell in the cockpit view.
        /// </summary>
        public List<Renderer> Shell = new List<Renderer>();
        bool _shellHidden;

        // Cockpit pass 0x1001bea2 uses full shell shading. Restore MechAssembler's
        // property after prefab loading, including editor previews: property blocks are not serialized.
        void OnEnable()
        {
            var block = new MaterialPropertyBlock();
            int shade = Shader.PropertyToID("_ShadeFull");
            foreach (var renderer in Shell)
                if (renderer != null)
                {
                    renderer.GetPropertyBlock(block);
                    block.SetFloat(shade, 1);
                    renderer.SetPropertyBlock(block);
                    block.Clear();
                }
        }

        public bool ShellHidden
        {
            get => _shellHidden;
            set
            {
                if (_shellHidden == value) return;
                _shellHidden = value;
                foreach (var l in Levels)
                    if (l.Lod == Current)
                        foreach (var r in l.Renderers)
                            if (r != null) r.enabled = Visible(r);
            }
        }

        bool Visible(Renderer r) => !_hidden.Contains(r) && !(_shellHidden && Shell != null && Shell.Contains(r));

        /// <summary>Parts that came off (a destroyed arm or leg): they stay hidden whatever level is shown.</summary>
        readonly HashSet<Renderer> _hidden = new HashSet<Renderer>();

        public void Hide(Renderer r)
        {
            if (r == null) return;
            _hidden.Add(r);
            r.enabled = false;
        }

        /// <summary>FUN_10051da0 rebuilds the node tree at regeneration: nothing stays hidden, and the current level shows.</summary>
        public void ShowAgain()
        {
            _hidden.Clear();
            int current = Current;
            Current = -1; // Show's same-level fast path must not retain the old hidden renderers.
            Show(current);
        }

        public int MaxLod => Levels.Count == 0 ? 1 : Levels[Levels.Count - 1].Lod;

        /// <summary>A "LOD{n} ..." child holds another representation of its node's model (MechAssembler).</summary>
        public static bool IsLodChild(Transform t) =>
            t.name.Length > 4 && t.name.StartsWith("LOD") && char.IsDigit(t.name[3]) && t.name[4] == ' ' && t.parent != null;

        /// <summary>
        /// The mesh the node shows at the current representation (node +0x6c, Lod_SwapModel 0x1001eb15 swaps it on the
        /// node), or null for a mount (whose model is the one-vertex DUMMY, POLY 8697).
        /// </summary>
        public static MeshFilter NodeMesh(Transform node, Mw2LodGroup lods)
        {
            MeshFilter filter = null;
            if (lods != null && lods.Levels.Count > 1)
            {
                var level = lods.Levels.Find(l => l.Lod == lods.Current);
                if (level != null && node.TryGetComponent<MeshRenderer>(out var own) && Array.IndexOf(level.Renderers, own) >= 0)
                    node.TryGetComponent(out filter);
                else
                    foreach (Transform child in node)
                        if (child.name.StartsWith($"LOD{lods.Current} ") && child.TryGetComponent(out filter)) break;
            }
            else node.TryGetComponent(out filter);
            return filter != null && filter.sharedMesh != null ? filter : null;
        }

        static readonly Dictionary<Mesh, float> s_radius = new Dictionary<Mesh, float>();

        /// <summary>Model_ComputeBounds 0x10003efa: the largest distance of the vertices from their bounding-box centre.</summary>
        public static float Radius(Mesh mesh)
        {
            if (mesh == null) return 0f;
            if (s_radius.TryGetValue(mesh, out float r)) return r;
            var centre = mesh.bounds.center;
            r = 0f;
            foreach (var v in mesh.vertices) r = Mathf.Max(r, (v - centre).magnitude);
            s_radius[mesh] = r;
            return r;
        }

        /// <summary>Shows <paramref name="lod"/>, or the closest available level when the model has fewer representations.</summary>
        public void Show(int lod)
        {
            lod = Mathf.Clamp(lod, 1, MaxLod);
            Level pick = null;
            foreach (var l in Levels)
                if (l.Lod <= lod) pick = l;
            if (pick == null || pick.Lod == Current) return;
            foreach (var l in Levels)
                foreach (var r in l.Renderers)
                    if (r != null) r.enabled = l == pick && Visible(r);
            Current = pick.Lod;
            // Lod_SwapModel 1001ee24 registers each new model at the collision-list head.
            // Detached parts keep their model and place in the list when their former owner changes LOD.
            foreach (var renderer in pick.Renderers)
                if (renderer != null && renderer.transform.IsChildOf(transform) &&
                    renderer.TryGetComponent<Mw2HitPart>(out var part) && part.HitCollider != null && part.HitCollider.enabled)
                    part.ModelActivated();
        }
    }
}
