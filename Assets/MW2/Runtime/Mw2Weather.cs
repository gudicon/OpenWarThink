using System.Collections.Generic;
using MW2.Formats;
using UnityEngine;

namespace MW2
{
    /// <summary>
    /// Rain and snow of a WTHR chunk (FUN_1006eb46 0x1006eb46), simulated like the original.
    ///
    /// FUN_1006e1e0 0x1006e1e0 sets up nine emitters on a 3 x 3 grid around the camera: x and z offsets -16384, 0, 16384
    /// units, height 10000 units above the ground plane (absolute, not the camera's), spawn box 20000 x 0 x 20000, 45 particle
    /// slots each. Every frame (FUN_1006e516 0x1006e516) each emitter adds ticks * rate / 181 to an accumulator and spawns its
    /// whole part: position = emitter offset + RandomInt(20000) - 10000 in x and z + the camera's x / z, velocity
    /// (vx, vy, vx) — the original passes the x component again for z (0x1006e6c4..0x1006e6e5); only within the camera's far
    /// plane and with a free slot. A particle (rain FUN_1006f283 0x1006f283, snow FUN_1006f86f 0x1006f86f) is a node with 64
    /// points at (k + 1) * v / 16 + (RandomInt(10) - 5, RandomInt(50) - 25, RandomInt(10) - 5); snow adds a drift
    /// (RandomInt(500) - 250, 0, RandomInt(500) - 250). Its update (FUN_1006ef1a / FUN_1006f520) removes it at height 0 and
    /// otherwise moves it by (v + drift) * ticks / 181, the y velocity less RandomInt(10). Drawing: rain (FUN_1006f053) two
    /// streaks from points 0 and 1 to point + v / 16 in white with alpha 0x20; snow (FUN_1006f659) one line from point 0 to
    /// point + drift / 4 in grey 180 (its palette lookup by depth is computed but not used).
    /// Approximations: the streaks are 1-pixel lines (the original rasterises a 1-pixel-wide triangle).
    /// </summary>
    public class Mw2Weather : MonoBehaviour
    {
        public int Type = 1;                // 1 rain, 2 snow
        public float Rate = 14f;            // particles per second per emitter
        public Vector3 Velocity = new Vector3(0, -10000, 0);   // model units per second

        const float Units = PolyModel.UnitsToMeters;
        const int Emitters = 9, Slots = 45;

        sealed class Particle
        {
            public Vector3 Position;        // model units
            public Vector3 Velocity, Drift;
            public Vector3 Offset0, Offset1;
        }

        readonly List<Particle>[] _particles = new List<Particle>[Emitters];
        readonly float[] _accumulator = new float[Emitters];
        double _clock;
        int _now;
        Mesh _mesh;
        Material _material;
        readonly List<Vector3> _vertices = new List<Vector3>();
        readonly List<Color> _colors = new List<Color>();
        readonly List<int> _indices = new List<int>();

        static readonly float[] GridX = { -16384, 0, 16384, -16384, 0, 16384, -16384, 0, 16384 };
        static readonly float[] GridZ = { -16384, -16384, -16384, 0, 0, 0, 16384, 16384, 16384 };

        void Awake()
        {
            for (int i = 0; i < Emitters; i++) _particles[i] = new List<Particle>();
        }

        static int RandomInt(int n) => n <= 0 ? 0 : Random.Range(0, n);

        void Update()
        {
            var cam = Camera.main;
            if (cam == null) return;
            _clock += Time.deltaTime * MechMotion.TicksPerSecond;
            int now = (int)_clock, dt = now - _now;
            _now = now;
            if (dt <= 0) return;
            // Rain_Spawn / Snow_Spawn take their particles through FUN_1004d1c9 (none while Particles is off), and
            // switching Particles off removes the live ones (FUN_1004d25a(0)).
            if (!Mw2Options.Particles) { foreach (var l in _particles) l.Clear(); return; }

            var camUnits = cam.transform.position / Units;
            float far = cam.farClipPlane / Units;
            for (int e = 0; e < Emitters; e++)
            {
                var list = _particles[e];
                // Update before spawning, as the object list runs after the emitters only on the next frame.
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    var p = list[i];
                    if (p.Position.y <= (Type == 2 ? 0f : 1e-7f)) { list.RemoveAt(i); continue; }
                    p.Position += new Vector3((p.Velocity.x + p.Drift.x) * dt / 181f,
                                              (p.Velocity.y - RandomInt(10) + p.Drift.y) * dt / 181f,
                                              (p.Velocity.z + p.Drift.z) * dt / 181f);
                }
                _accumulator[e] += dt * (Rate / 181f);
                int n = (int)_accumulator[e];
                if (n == 0) continue;
                _accumulator[e] -= n;
                for (int k = 0; k < n; k++)
                {
                    var pos = new Vector3(GridX[e] + (RandomInt(20000) - 10000) + camUnits.x, 10000f,
                                          GridZ[e] + (RandomInt(20000) - 10000) + camUnits.z);
                    if ((pos - camUnits).magnitude > far || list.Count >= Slots) continue;
                    var v = new Vector3(Velocity.x, Velocity.y, Velocity.x);
                    var p = new Particle { Position = pos, Velocity = v };
                    if (Type == 2) p.Drift = new Vector3(RandomInt(500) - 250, 0, RandomInt(500) - 250);
                    var step = v / 16f;
                    p.Offset0 = step + new Vector3(RandomInt(10) - 5, RandomInt(0x32) - 25, RandomInt(10) - 5);
                    p.Offset1 = step * 2f + new Vector3(RandomInt(10) - 5, RandomInt(0x32) - 25, RandomInt(10) - 5);
                    list.Add(p);
                }
            }
        }

        void LateUpdate()
        {
            if (_mesh == null)
            {
                _mesh = new Mesh { name = "MW2 weather" };
                _mesh.MarkDynamic();
                _material = new Material(Shader.Find("Hidden/Internal-Colored")) { hideFlags = HideFlags.HideAndDontSave };
                _material.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                _material.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                _material.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
                _material.SetInt("_ZWrite", 0);
                _material.renderQueue = 3000;
            }
            _vertices.Clear();
            _colors.Clear();
            _indices.Clear();
            var rain = new Color(1f, 1f, 1f, 0x20 / 255f);
            var snow = new Color(180 / 255f, 180 / 255f, 180 / 255f, 1f);
            void Line(Vector3 a, Vector3 b, Color c)
            {
                _indices.Add(_vertices.Count);
                _vertices.Add(a * Units);
                _colors.Add(c);
                _indices.Add(_vertices.Count);
                _vertices.Add(b * Units);
                _colors.Add(c);
            }
            foreach (var list in _particles)
                foreach (var p in list)
                {
                    if (Type == 2)
                    {
                        var a = p.Position + p.Offset0;
                        Line(a, a + p.Drift / 4f, snow);
                    }
                    else
                    {
                        var a0 = p.Position + p.Offset0;
                        var a1 = p.Position + p.Offset1;
                        Line(a0, a0 + p.Velocity / 16f, rain);
                        Line(a1, a1 + p.Velocity / 16f, rain);
                    }
                }
            _mesh.Clear();
            if (_vertices.Count == 0) return;
            _mesh.SetVertices(_vertices);
            _mesh.SetColors(_colors);
            _mesh.SetIndices(_indices, MeshTopology.Lines, 0);
            _mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 100000f);
            Graphics.DrawMesh(_mesh, Matrix4x4.identity, _material, 0);
        }

        void OnDestroy()
        {
            if (_mesh != null) Destroy(_mesh);
            if (_material != null) Destroy(_material);
        }
    }
}
