using MW2.Formats;
using UnityEngine;

namespace MW2
{
    /// <summary>
    /// TSK class 1, FUN_1000df2a 0x1000df2a: writes a shifted palette material to every
    /// polygon of a node. Polygon i takes colour (time/period+i) modulo the sequence length.
    /// FUN_10004631 writes the POLY face material; Unity instead updates the matching face
    /// vertices on a private mesh copy, including the raw material in UV1 for the shader.
    /// </summary>
    [RequireComponent(typeof(MeshFilter))]
    public sealed class Mw2MaterialTask : MonoBehaviour
    {
        public int PeriodTicks = 1;
        public ushort[] Materials;
        public Color32[] MaterialColors;
        public int[] FaceStarts;
        public int[] FaceLengths;

        Mesh _mesh;
        Color32[] _colors;
        Vector2[] _materialIds;
        double _ticks;
        int _phase = -1;

        void Start()
        {
            if (Materials == null || Materials.Length == 0 || FaceStarts == null || FaceLengths == null ||
                FaceStarts.Length != FaceLengths.Length || MaterialColors == null || MaterialColors.Length != Materials.Length)
            {
                enabled = false;
                return;
            }
            _mesh = GetComponent<MeshFilter>().mesh; // task material changes must not touch shared POLY meshes
            _colors = _mesh.colors32;
            _materialIds = _mesh.uv2;
            if (_colors.Length != _mesh.vertexCount || _materialIds.Length != _mesh.vertexCount)
            {
                enabled = false;
                return;
            }
            Apply(0);
        }

        void Update()
        {
            _ticks += Time.deltaTime * MechMotion.TicksPerSecond;
            int phase = (int)((long)_ticks / Mathf.Max(1, PeriodTicks) % Materials.Length);
            if (phase != _phase) Apply(phase);
        }

        void Apply(int phase)
        {
            for (int face = 0; face < FaceStarts.Length; face++)
            {
                int frame = (phase + face) % Materials.Length;
                ushort material = Materials[frame];
                var id = PolyMeshBuilder.MaterialId(material);
                for (int vertex = FaceStarts[face]; vertex < FaceStarts[face] + FaceLengths[face]; vertex++)
                {
                    _colors[vertex] = MaterialColors[frame];
                    _materialIds[vertex] = id;
                }
            }
            _mesh.colors32 = _colors;
            _mesh.uv2 = _materialIds;
            _phase = phase;
        }
    }
}
