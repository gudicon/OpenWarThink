using System.Collections.Generic;
using UnityEngine;

namespace MW2
{
    /// <summary>
    /// Sky dome (TL1SKYDM) as the original draws it. Sky_Update (0x1005559a) moves the sky object to the camera's x and z
    /// (height 0) every frame and draws the ground first (Ground_Draw); FUN_1001bea2 then draws the sky object in its own
    /// pass with the far plane at FLT_MAX, and only then the world. The Glide plugin runs without a depth buffer: SimMain
    /// passes flags 0x202 to the plugin's FirstDevice (bit 1, depth, clear; zglide.dll sets grDepthBufferMode / grDepthMask
    /// only with that bit), so the engine paints in this order and the world always covers the sky and the ground, also
    /// beyond the mountain ring (r = 1845 m). Here the ground and the sky render in the background queue without depth
    /// writes, before every other geometry; the world keeps its depth buffer among itself.
    /// The sky object is not lit: Material_Shade (0x1001afb0) skips the lights for objects with flag 0x400 and gives the
    /// sky object (DAT_100b0080) white vertices (255, 170, 0 only in view modes 12..15); fog still applies.
    /// The sky uses its own material copies, which take the cloud scroll (Mw2Atmosphere, 0x1005559a) and the texture
    /// frame (Mw2TextureAnimator) from the shared materials every frame.
    /// Order inside the sky pass: FUN_100562cc walks the object tree (FUN_10056204), Render_Polygon (0x10056eb6) appends
    /// each visible polygon to the draw list in POLY order and the list is drawn front to back of the array without
    /// sorting (FUN_10056455). TL1SKYDM: moon, sun, clouds, then the mountain panels. The mesh keeps one submesh per
    /// texture key, its vertices appended polygon by polygon, so a submesh's lowest vertex index gives the POLY position
    /// of its first polygon; the submeshes are queued in that order.
    /// Ground: lit as one colour by FUN_1005503e (Mw2GroundLight in Mw2Light.cginc).
    /// </summary>
    public class Mw2FollowCameraXZ : MonoBehaviour
    {
        const int GroundQueue = 1000, SkyQueue = 1001;
        static readonly int MainTexId = Shader.PropertyToID("_MainTex");

        readonly List<(Material copy, Material source, bool hasMainTex)> _copies =
            new List<(Material, Material, bool)>();
        readonly List<Mesh> _meshCopies = new List<Mesh>();

        void Awake()
        {
            // Renderers in tree order (node, then its children: FUN_10056204), submeshes by their first POLY polygon.
            int next = SkyQueue;
            foreach (var r in GetComponentsInChildren<Renderer>(true))
            {
                RestorePolygonSubmeshes(r);
                var order = PolyOrder(r);
                var queues = new int[order.Length];
                foreach (int s in order) queues[s] = next++;
                Backdrop(r, true, i => queues[i]);
            }
            // The mission ground (Mw2MissionBuilder.CreateGround) is a sibling named "Ground"; it is drawn before the sky.
            var ground = transform.parent != null ? transform.parent.Find("Ground") : null;
            if (ground != null && ground.TryGetComponent<Renderer>(out var groundRenderer)) Backdrop(groundRenderer, false, _ => GroundQueue);
        }

        /// <summary>Material slots of a renderer ordered by the lowest vertex index of their submesh.</summary>
        static int[] PolyOrder(Renderer r)
        {
            int count = r.sharedMaterials.Length;
            var first = new int[count];
            Mesh mesh = r.TryGetComponent<MeshFilter>(out var filter) ? filter.sharedMesh : null;
            for (int i = 0; i < count; i++)
            {
                first[i] = int.MaxValue - count + i;
                if (mesh == null || !mesh.isReadable || i >= mesh.subMeshCount) continue;
                var indices = mesh.GetIndices(i);
                foreach (int v in indices) if (v < first[i]) first[i] = v;
            }
            var order = new int[count];
            for (int i = 0; i < count; i++) order[i] = i;
            System.Array.Sort(order, (a, b) => first[a].CompareTo(first[b]));
            return order;
        }

        void Backdrop(Renderer r, bool sky, System.Func<int, int> queue)
        {
            var sources = r.sharedMaterials;
            var copies = new Material[sources.Length];
            for (int i = 0; i < sources.Length; i++)
            {
                if (sources[i] == null) continue;
                var m = new Material(sources[i]) { name = sources[i].name };
                if (m.HasProperty("_ZWrite")) m.SetFloat("_ZWrite", 0f);
                if (sky && m.HasProperty("_Unlit")) m.SetFloat("_Unlit", 1f);
                if (!sky && m.HasProperty("_Ground")) m.SetFloat("_Ground", 1f);
                m.renderQueue = queue(i);
                copies[i] = m;
                bool hasMainTex = m.HasProperty(MainTexId) && sources[i].HasProperty(MainTexId);
                _copies.Add((m, sources[i], hasMainTex));
                if (hasMainTex) SyncTexture(m, sources[i]);
            }
            r.sharedMaterials = copies;
        }

        void LateUpdate()
        {
            var cam = Camera.main;
            if (cam != null)
            {
                var p = cam.transform.position;
                transform.position = new Vector3(p.x, transform.position.y, p.z);
            }
            foreach (var (copy, source, hasMainTex) in _copies)
            {
                if (copy == null || source == null || !hasMainTex) continue;
                SyncTexture(copy, source);
            }
        }

        static void SyncTexture(Material copy, Material source)
        {
            copy.SetTexture(MainTexId, source.GetTexture(MainTexId));
            // Preserve the imported orientation. Flipping MNT materials here inverts the mountain silhouette.
            copy.SetTextureScale(MainTexId, source.GetTextureScale(MainTexId));
            copy.SetTextureOffset(MainTexId, source.GetTextureOffset(MainTexId));
        }

        /// <summary>
        /// Older imported SKYDM assets grouped every polygon that used the same texture into one submesh. That changes
        /// the original POLY draw order and lets a later panel's translucent background blend over another panel's opaque
        /// mountain. SKYDM consists entirely of quads, with four private vertices and two triangles per POLY, so the
        /// original calls can be recovered losslessly and ordered by their first vertex. Fresh imports already have one
        /// six-index submesh per polygon and pass through unchanged.
        /// </summary>
        void RestorePolygonSubmeshes(Renderer r)
        {
            if (!r.TryGetComponent<MeshFilter>(out var filter) || filter.sharedMesh == null) return;
            var mesh = filter.sharedMesh;
            var sources = r.sharedMaterials;
            var polygons = new List<(int first, int[] indices, Material material)>();
            for (int s = 0; s < mesh.subMeshCount; s++)
            {
                var indices = mesh.GetIndices(s);
                if (indices.Length == 0 || indices.Length % 6 != 0 || s >= sources.Length) return;
                for (int at = 0; at < indices.Length; at += 6)
                {
                    var quad = new int[6];
                    System.Array.Copy(indices, at, quad, 0, 6);
                    var unique = new HashSet<int>(quad);
                    if (unique.Count != 4) return;
                    int first = int.MaxValue;
                    foreach (int vertex in quad) if (vertex < first) first = vertex;
                    polygons.Add((first, quad, sources[s]));
                }
            }
            if (polygons.Count <= mesh.subMeshCount) return;

            polygons.Sort((a, b) => a.first.CompareTo(b.first));
            var copy = Instantiate(mesh);
            copy.name = mesh.name;
            copy.subMeshCount = polygons.Count;
            var materials = new Material[polygons.Count];
            for (int i = 0; i < polygons.Count; i++)
            {
                copy.SetTriangles(polygons[i].indices, i, false);
                materials[i] = polygons[i].material;
            }
            copy.RecalculateBounds();
            filter.sharedMesh = copy;
            r.sharedMaterials = materials;
            _meshCopies.Add(copy);
        }

        void OnDestroy()
        {
            foreach (var (copy, _, _) in _copies) if (copy != null) Destroy(copy);
            foreach (var mesh in _meshCopies) if (mesh != null) Destroy(mesh);
        }
    }
}
