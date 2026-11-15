using System.Collections.Generic;
using MW2.Formats;
using UnityEngine;

namespace MW2
{
    /// <summary>
    /// LOD chain of a scene node whose POLY has flag 0x1000 (Poly_ReadObject 0x10073d2f), e.g.
    /// s-1cntr_1 / s-2cntr_22500 / s-3cntr_65000. Every scene pass the original calls Model_SelectLod 0x10055950 with the
    /// node's view depth, the camera-forward component of (model centre +0x34 - camera position) computed by the
    /// visibility tests (FUN_1006ad7b 0x1006ad7b, FUN_1004c734 0x1004c734), and draws only the selected step.
    /// The centre is that of Model_ComputeBounds 0x10003efa, taken from the chain head (+0x1c).
    /// Hiding uses Renderer.forceRenderingOff so that the enabled flags kept by rubble and destruction stay untouched;
    /// colliders of every step stay as before.
    /// </summary>
    public sealed class Mw2SceneLod : MonoBehaviour
    {
        /// <summary>Renderers of all steps; <see cref="RendererSteps"/> gives the step of each.</summary>
        public Renderer[] Renderers;
        public int[] RendererSteps;
        /// <summary>Step thresholds in model units, ascending (chain order).</summary>
        public float[] Thresholds;
        /// <summary>Head bounds centre in the node's local space.</summary>
        public Vector3 LocalCentre;

        int _shown = -1;

        static readonly List<Mw2SceneLod> Active = new List<Mw2SceneLod>();

        void OnEnable() { Active.Add(this); _shown = -1; }
        void OnDisable() => Active.Remove(this);

        /// <summary>Called once per frame by <see cref="Mw2LodSwitcher"/> with camera[+0xb8].</summary>
        public static void UpdateAll(Camera cam, float focalScale)
        {
            var origin = cam.transform.position;
            var forward = cam.transform.forward;
            for (int i = Active.Count - 1; i >= 0; i--)
            {
                var lod = Active[i];
                if (lod == null) { Active.RemoveAt(i); continue; }
                float depth = Vector3.Dot(lod.transform.TransformPoint(lod.LocalCentre) - origin, forward) / PolyModel.UnitsToMeters;
                lod.Show(PolyModel.SelectLod(lod.Thresholds, depth, focalScale));
            }
        }

        public void Show(int step)
        {
            if (step == _shown || Renderers == null) return;
            _shown = step;
            for (int i = 0; i < Renderers.Length; i++)
                if (Renderers[i] != null) Renderers[i].forceRenderingOff = RendererSteps[i] != step;
        }
    }
}
