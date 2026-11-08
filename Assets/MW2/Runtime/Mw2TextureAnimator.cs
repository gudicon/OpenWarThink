using System.Collections.Generic;
using MW2.Formats;
using UnityEngine;

namespace MW2
{
    /// <summary>
    /// Texture animation by key (records DAT_102e9160, 0x12 bytes: row, frame, ticks per frame, mode, start time),
    /// advanced every frame by 0x10001c74 on the 181 Hz clock: while the mode is not 0 the frame steps once per period
    /// (up to 32 frames, an empty frame wraps to 0); mode 2 plays once: when the frame wraps it stops at frame 0.
    /// Texture_SetSpeed (0x10002094) sets the mode and a 45-tick period when none is set; 0x10002106 restarts at a frame.
    /// Keys with more than one frame get their own material, whose texture is switched here.
    /// </summary>
    public class Mw2TextureAnimator : MonoBehaviour
    {
        [System.Serializable]
        public class Row
        {
            public int Key;
            public Material[] Materials;   // the key's lit material and its sprite material (MW2/Sprite)
            public Texture2D[] Frames;
            public int Period;
            public int Mode;
            [System.NonSerialized] public int Frame;
            [System.NonSerialized] public int Start;
        }

        public List<Row> Rows = new List<Row>();
        public static Mw2TextureAnimator Instance;

        readonly Dictionary<int, Row> _byKey = new Dictionary<int, Row>();
        readonly Dictionary<Material, Material> _copies = new Dictionary<Material, Material>();

        /// <summary>The animated copy of a material asset, for renderers created at run time (projectiles).</summary>
        public Material Live(Material m) => m != null && _copies.TryGetValue(m, out var copy) ? copy : m;
        double _clock = 1;

        void OnEnable()
        {
            Instance = this;
            _byKey.Clear();
            // Work on copies so play mode does not write frames into the material assets.
            var copies = _copies;
            copies.Clear();
            foreach (var row in Rows)
            {
                if (row == null || row.Frames == null || row.Frames.Length == 0 || row.Materials == null) continue;
                for (int i = 0; i < row.Materials.Length; i++)
                {
                    var m = row.Materials[i];
                    if (m == null) continue;
                    if (!copies.TryGetValue(m, out var copy))
                        copies[m] = copy = new Material(m) { name = m.name + " (animated)" };
                    row.Materials[i] = copy;
                }
                _byKey[row.Key] = row;
            }
            if (copies.Count == 0) return;
            foreach (var r in FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                var mats = r.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < mats.Length; i++)
                    if (mats[i] != null && copies.TryGetValue(mats[i], out var copy)) { mats[i] = copy; changed = true; }
                if (changed) r.sharedMaterials = mats;
            }
        }

        void OnDisable() { if (Instance == this) Instance = null; }

        /// <summary>Texture_SetSpeed 0x10002094.</summary>
        public void SetSpeed(int key, int mode)
        {
            if (!_byKey.TryGetValue(key, out var row)) return;
            row.Mode = mode;
            if (row.Period == 0) row.Period = 0x2d;
        }

        /// <summary>0x10002106: restart at a frame (frames above 31 become 0) if that frame exists.</summary>
        public void Restart(int key, int frame)
        {
            if (!_byKey.TryGetValue(key, out var row)) return;
            if (frame > 0x1f) frame = 0;
            if (frame >= row.Frames.Length) return;
            row.Frame = frame;
            row.Start = 0;
            Apply(row);
        }

        static void Apply(Row row)
        {
            if (row.Materials == null || row.Frame >= row.Frames.Length || row.Frames[row.Frame] == null) return;
            foreach (var m in row.Materials)
                if (m != null) m.mainTexture = row.Frames[row.Frame];
        }

        void Update()
        {
            _clock += Time.deltaTime * MechMotion.TicksPerSecond;
            int now = (int)_clock;
            foreach (var row in Rows)
            {
                if (row.Mode == 0 || row.Period == 0 || row.Frames == null || row.Frames.Length == 0) continue;
                if (row.Start == 0) row.Start = now;
                int steps = (now - row.Start) / row.Period;
                if (steps <= 0) continue;
                int frame = row.Frame;
                for (int k = 0; k < steps; k++)
                {
                    frame = (frame + 1) % 0x20;
                    if (frame >= row.Frames.Length) frame = 0;
                }
                if (row.Mode == 2 && frame < row.Frame) { row.Mode = 0; frame = 0; }
                row.Frame = frame;
                row.Start = now - ((now - row.Start) - steps * row.Period);
                Apply(row);
            }
        }
    }
}
