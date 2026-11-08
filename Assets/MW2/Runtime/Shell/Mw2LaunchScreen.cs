using System;
using System.IO;
using MW2.Formats;
using UnityEngine;

namespace MW2.Shell
{
    /// <summary>
    /// SimMain 0x100465ae starts the loading display with FUN_100615b0 and stops it with FUN_10061aca.
    /// The screen is LAUNCH/SUPANM6.SHP with its own palette; LAUNCH6.SHP supplies six heartbeat frames.
    /// FUN_100619ef advances the frame every 330000 microseconds, at 34% / 76% of the 640x480 surface.
    /// </summary>
    public sealed class Mw2LaunchScreen : MonoBehaviour
    {
        Texture2D _background;
        Texture2D[] _heartbeat;
        float _started;
        bool _painted;

        // DAT_1009fd00: the first 16 six-bit RGB entries, installed after the SUPANM frame palette.
        static readonly byte[] FirstColours =
        {
            0, 0, 0, 51, 52, 54, 39, 40, 43, 32, 32, 35,
            27, 28, 30, 23, 25, 27, 21, 21, 23, 18, 20, 22,
            17, 18, 20, 16, 16, 18, 14, 16, 18, 13, 14, 16,
            12, 12, 14, 9, 9, 10, 4, 4, 5, 1, 1, 2
        };

        public void Begin(string gameFolder)
        {
            _started = Time.realtimeSinceStartup;
            try
            {
                string folder = Path.Combine(gameFolder, "LAUNCH");
                byte[] backdrop = File.ReadAllBytes(Path.Combine(folder, "SUPANM6.SHP"));
                byte[] animation = File.ReadAllBytes(Path.Combine(folder, "LAUNCH6.SHP"));
                var palette = ReadPalette(backdrop);
                _background = Shape.DecodeFrames(backdrop)[0].ToTexture(palette, "SUPANM6");
                var frames = Shape.DecodeFrames(animation);
                _heartbeat = new Texture2D[frames.Length];
                for (int i = 0; i < frames.Length; i++)
                    _heartbeat[i] = frames[i].ToTexture(palette, $"LAUNCH6 {i}");
                Debug.Log($"MW2 launch display: loaded SUPANM6 and {_heartbeat.Length} LAUNCH6 frames");
            }
            catch (Exception error)
            {
                Debug.LogWarning("MW2 launch display: " + error.Message);
            }
        }

        static Color32[] ReadPalette(byte[] data)
        {
            var colors = new Color32[256];
            for (int i = 0; i < colors.Length; i++) colors[i] = new Color32(0, 0, 0, 255);
            for (int i = 0; i < 16; i++) Set(i, FirstColours[i * 3], FirstColours[i * 3 + 1], FirstColours[i * 3 + 2]);
            int offset = BitConverter.ToInt32(data, 12); // frame 0 palette pointer, read by FUN_1007cc36
            int count = BitConverter.ToInt32(data, offset);
            if (count < 0 || count > 256 || offset + 4 + count * 4 > data.Length)
                throw new InvalidDataException("SUPANM6 palette is invalid");
            for (int i = 0; i < count; i++)
            {
                int p = offset + 4 + i * 4;
                Set(data[p], data[p + 1], data[p + 2], data[p + 3]);
            }
            return colors;

            void Set(int index, byte r, byte g, byte b) =>
                colors[index] = new Color32(Palette.Expand(r), Palette.Expand(g), Palette.Expand(b), 255);
        }

        public void Draw()
        {
            if (Event.current.type != EventType.Repaint) return;
            if (!_painted) { _painted = true; Debug.Log("MW2 launch display: first repaint"); }
            int oldDepth = GUI.depth;
            Color oldColor = GUI.color;
            GUI.depth = -1000;
            GUI.color = Color.white;
            float w = Mathf.Min(Screen.width, Screen.height * 4f / 3f);
            float h = w * 3f / 4f;
            var surface = new Rect((Screen.width - w) * 0.5f, (Screen.height - h) * 0.5f, w, h);
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.blackTexture);
            if (_background != null) GUI.DrawTexture(surface, _background);
            if (_heartbeat != null && _heartbeat.Length > 0)
            {
                int frame = (int)((Time.realtimeSinceStartup - _started) / 0.33f) % _heartbeat.Length;
                var beat = _heartbeat[frame];
                GUI.DrawTexture(new Rect(surface.x + surface.width * 217f / 640f,
                    surface.y + surface.height * 364f / 480f,
                    surface.width * beat.width / 640f, surface.height * beat.height / 480f), beat);
            }
            GUI.depth = oldDepth;
            GUI.color = oldColor;
        }

        void OnDestroy()
        {
            if (_background != null) Destroy(_background);
            if (_heartbeat != null)
                foreach (var frame in _heartbeat) if (frame != null) Destroy(frame);
        }
    }
}
