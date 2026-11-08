using System.Collections.Generic;
using MW2.Formats;
using UnityEngine;
using UnityEngine.Rendering;

namespace MW2
{
    /// <summary>Option_Set 1006a18d mode 1: Material_Shade 1001afb0 and black-fill/polygon-boundary raster 1005581d.
    /// Unity depth/line rasterization replaces Glide; boundaries use polygon indices, never triangle diagonals.</summary>
    public sealed class Mw2ImageEnhancement : MonoBehaviour
    {
        static bool _enabled;
        public static bool Enabled
        {
            get => _enabled;
            set
            {
                _enabled = value;
                if (value && Camera.main != null && !Camera.main.TryGetComponent<Mw2ImageEnhancement>(out _))
                    Camera.main.gameObject.AddComponent<Mw2ImageEnhancement>();
                if (!value) Shader.SetGlobalFloat("_Mw2Enhanced", 0);
            }
        }
        struct Edge { public int a, b, material, flags; }
        sealed class Geometry { public Vector3[] vertices; public Edge[] edges; }
        readonly Dictionary<Mesh, Geometry> _geometry = new Dictionary<Mesh, Geometry>();
        MeshRenderer[] _renderers;
        Material _lines;
        readonly MaterialPropertyBlock _block = new MaterialPropertyBlock();

        static Geometry Build(Mesh mesh)
        {
            var attributes = new List<Vector2>(); mesh.GetUVs(4, attributes);
            if (attributes.Count != mesh.vertexCount) return null;
            var counts = new Dictionary<(int a, int b), int>();
            for (int s = 0; s < mesh.subMeshCount; s++)
            {
                if (mesh.GetTopology(s) != MeshTopology.Triangles) continue;
                var indices = mesh.GetIndices(s);
                for (int i = 0; i + 2 < indices.Length; i += 3)
                    for (int e = 0; e < 3; e++)
                    {
                        int a = indices[i + e], b = indices[i + (e + 1) % 3];
                        var key = a < b ? (a, b) : (b, a);
                        counts.TryGetValue(key, out int n); counts[key] = n + 1;
                    }
            }
            var edges = new List<Edge>();
            foreach (var item in counts)
                if (item.Value == 1)
                    edges.Add(new Edge { a = item.Key.a, b = item.Key.b,
                        material = (int)attributes[item.Key.a].x, flags = (int)attributes[item.Key.a].y });
            var original = new List<Vector3>(); mesh.GetUVs(5, original);
            return new Geometry { vertices = original.Count == mesh.vertexCount ? original.ToArray() : mesh.vertices, edges = edges.ToArray() };
        }

        void OnPreRender()
        {
            if (Camera.current != Camera.main) return;
            Shader.SetGlobalFloat("_Mw2Enhanced", Enabled ? 1 : 0);
            if (!Enabled) return;
            _renderers = FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None);
            foreach (var r in _renderers)
            {
                if (r.TryGetComponent<Mw2ImageNode>(out var node)) node.Apply();
                var map = r.GetComponentInParent<Mw2HitMap>();
                r.GetPropertyBlock(_block);
                _block.SetFloat("_Mw2EnhancedDamage", map != null ? map.LevelOf(r.transform) : 0);
                r.SetPropertyBlock(_block); _block.Clear();
            }
        }

        void OnPostRender()
        {
            try
            {
            if (!Enabled || Camera.current != Camera.main || _renderers == null) return;
            if (_lines == null)
            {
                var shader = Shader.Find("MW2/EnhancedBoundary");
                if (shader == null) return;
                _lines = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            }
            foreach (var r in _renderers)
            {
                if (r == null || !r.enabled || !r.gameObject.activeInHierarchy || !r.TryGetComponent<MeshFilter>(out var filter)) continue;
                var mesh = filter.sharedMesh;
                if (mesh == null || !mesh.isReadable) continue;
                r.GetPropertyBlock(_block);
                bool shell = _block.GetFloat("_ShadeFull") > 0.5f;
                bool overridden = _block.GetFloat("_Mw2EnhancedOverride") > 0.5f;
                int overrideFlags = (int)_block.GetFloat("_Mw2EnhancedFlags"); _block.Clear();
                if (!_geometry.TryGetValue(mesh, out var geo)) { geo = Build(mesh); _geometry[mesh] = geo; }
                if (geo == null) continue;
                var map = r.GetComponentInParent<Mw2HitMap>();
                int damage = map != null ? map.LevelOf(r.transform) : 0;
                foreach (var material in r.sharedMaterials)
                    if (material != null && material.HasProperty("_Palette")) { _lines.SetTexture("_Palette", material.GetTexture("_Palette")); break; }
                _lines.SetPass(0);
                GL.PushMatrix(); GL.MultMatrix(r.localToWorldMatrix); GL.Begin(GL.LINES);
                foreach (var edge in geo.edges)
                {
                    int color = ImageEnhancement.Boundary(ImageEnhancement.Shade(shell ? 0x400 : overridden ? overrideFlags : edge.flags, damage, edge.material));
                    if (color <= 0) continue;
                    GL.Color(new Color(color / 255f, 0, 0, 1));
                    GL.Vertex(geo.vertices[edge.a]); GL.Vertex(geo.vertices[edge.b]);
                }
                GL.End(); GL.PopMatrix();
            }
            }
            finally { Shader.SetGlobalFloat("_Mw2Enhanced", 0); }
        }

        void OnDisable() => Shader.SetGlobalFloat("_Mw2Enhanced", 0);

        void OnDestroy()
        {
            if (_lines != null) Destroy(_lines);
            _geometry.Clear();
            _enabled = false; Shader.SetGlobalFloat("_Mw2Enhanced", 0);
        }
    }
}
