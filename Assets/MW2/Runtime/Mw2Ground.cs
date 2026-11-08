using MW2.Formats;
using UnityEngine;

namespace MW2
{
    /// <summary>
    /// World_GroundHeight 0x100186b1: the height of the walkable surfaces (collision types 0 box top and 5 surface,
    /// height functions FUN_10017e21 / FUN_100183f8) under (x, z): the highest one within 1000 units of the given height,
    /// else the highest one below it, else 0 (_DAT_100871d8). Surfaces are found with a downward ray through the
    /// colliders built for those types (Mw2Surface).
    /// </summary>
    public static class Mw2Ground
    {
        const float Units = PolyModel.UnitsToMeters;
        static RaycastHit[] s_hits = new RaycastHit[32];

        /// <summary>Ground height in metres under <paramref name="point"/> (metres).</summary>
        public static float Height(Vector3 point)
        {
            float near = float.NegativeInfinity, below = float.NegativeInfinity;
            var from = new Vector3(point.x, Mathf.Max(point.y, 0f) + 10000f * Units + 1000f, point.z);
            int n;
            // 100186b1 traverses every candidate. A full Unity buffer may have omitted
            // the only walkable surface, so retry with more space before filtering hits.
            while (true)
            {
                n = Physics.RaycastNonAlloc(from, Vector3.down, s_hits, from.y + 20000f, Mw2HitPart.MovementMask,
                                            QueryTriggerInteraction.Ignore);
                if (n < s_hits.Length) break;
                System.Array.Resize(ref s_hits, s_hits.Length * 2);
            }
            float tolerance = 1000f * Units;
            for (int i = 0; i < n; i++)
            {
                var h = s_hits[i];
                var surface = h.collider.GetComponentInParent<Mw2Surface>();
                if (surface == null || !surface.Walkable) continue;
                float y = h.point.y;
                if (y < point.y && y > below) below = y;
                if (point.y < y + tolerance && y - tolerance < point.y && y > near) near = y;
            }
            if (!float.IsNegativeInfinity(near)) return near;
            if (!float.IsNegativeInfinity(below)) return below;
            return 0f;
        }
    }
}
