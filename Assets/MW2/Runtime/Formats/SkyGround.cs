using System;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace MW2.Formats
{
    /// <summary>
    /// One mission line of SKYGND.PAR in the game folder, parsed like mercsw.dll 0x100538a0 (sscanf format at
    /// 0x1009e998): "mission=bet1 globalintensity=60 tile_ground=15000 grounddelta=60 ground_max=160000
    /// shadowtable=7 gclip=.. amb=r,g,b wa=r,g,b,D,x,y,z wb=r,g,b,D,x,y,z fog=r,g,b density=a,b,c sky=r,g,b
    /// skyAnim=u,v". Text after '#' is a comment. Missions without a line use the DLL defaults
    /// (no fog, ground tile 39.0625, ground size 320000).
    /// Distances in the file are scaled by 2 in the DLL (density c, ground_max), tile_ground is divided by 128,
    /// skyAnim by 181 (ticks per second).
    /// </summary>
    public sealed class SkyGround
    {
        public string Mission;
        public int GlobalIntensity = 0x80;
        public float TileGround = 39.0625f * 128f;
        public float GroundDelta;
        public float GroundMax = 160000f;
        public Color32 Ambient;
        public Color32 Fog;
        /// <summary>density=a,b,c: a = altitude scale of the fog strength, b = its minimum, c = fog distance / 2.</summary>
        public Vector3 Density;
        /// <summary>
        /// Light slots of 0x1002ec0a (records at 0x10097cd4, 0x30 bytes): amb is slot 0 (added as is), wa/wb are slots 1/2,
        /// set by 0x1002f955: type 1 = direction (normalised, lights faces with -dir . n > 0), type 2 = position.
        /// Assumption: the type letter D maps to 1, any other letter to 2 (point); the parser only shows the call.
        /// </summary>
        [Serializable]   // stored on Mw2Atmosphere in the mission scene
        public struct Light
        {
            public Color32 Color;
            public bool IsDirection;
            public Vector3 Vector;
        }

        public Light LightA, LightB;
        public Color32 Sky;
        public Vector2 SkyAnim;

        /// <summary>Fog distance in model units (1 unit = 1 cm); 0 = no fog (0x100538a0: density c * 2).</summary>
        public float FogDistanceUnits => Density.z * 2f;

        /// <summary>Half size of the ground square around the camera, model units (ground_max * 2 * 2, 0x1005521b).</summary>
        public float GroundHalfSizeUnits => GroundMax * 4f;

        /// <summary>Model units per ground texture repeat: half size / (tile_ground / 128) (0x1005521b).</summary>
        public float GroundRepeatUnits => TileGround > 0 ? GroundHalfSizeUnits / (TileGround / 128f) : 0f;

        /// <summary>Ground texture is drawn only when tile_ground / 128 is above 1/128 (0x100538a0 clears DAT_1009e930).</summary>
        public bool HasGround => TileGround / 128f > 0.0078125f;

        public static SkyGround Load(string path, string prefix)
        {
            if (!File.Exists(path)) return null;
            // SkyGround_Load 100538a0 scans every line into the same sscanf destinations.
            // Truncated multiplayer lines inherit amb/wa/wb/sky from earlier full lines.
            var state = new SkyGround();
            foreach (var raw in File.ReadAllLines(path))
            {
                string line = raw;
                int hash = line.IndexOf('#');
                if (hash >= 0) line = line.Substring(0, hash);
                // The original explicitly clears density a/c before each sscanf, but not b.
                state.Density = new Vector3(0, state.Density.y, 0);
                var parsed = Parse(line, state);
                if (parsed != null && string.Equals(parsed.Mission, prefix, StringComparison.OrdinalIgnoreCase))
                    return parsed;
            }
            return null;
        }

        public static SkyGround Parse(string line)
            => Parse(line, new SkyGround());

        static SkyGround Parse(string line, SkyGround result)
        {
            bool any = false;
            foreach (var token in line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = token.IndexOf('=');
                if (eq <= 0) continue;
                string key = token.Substring(0, eq).ToLowerInvariant();
                string[] v = token.Substring(eq + 1).Split(',');
                switch (key)
                {
                    case "mission": result.Mission = v[0]; any = true; break;
                    case "globalintensity": result.GlobalIntensity = (int)Num(v, 0); break;
                    case "tile_ground": result.TileGround = Num(v, 0); break;
                    case "grounddelta": result.GroundDelta = Num(v, 0); break;
                    case "ground_max": result.GroundMax = Num(v, 0); break;
                    case "amb": result.Ambient = Rgb(v); break;
                    case "wa": result.LightA = ParseLight(v); break;
                    case "wb": result.LightB = ParseLight(v); break;
                    case "fog": result.Fog = Rgb(v); break;
                    case "density": result.Density = new Vector3(Num(v, 0), Num(v, 1), Num(v, 2)); break;
                    case "sky": result.Sky = Rgb(v); break;
                    case "skyanim": result.SkyAnim = new Vector2(Num(v, 0), Num(v, 1)); break;
                }
            }
            return any ? result : null;
        }

        static Light ParseLight(string[] v) => new Light
        {
            Color = Rgb(v),
            IsDirection = v.Length > 3 && v[3].Trim().ToUpperInvariant() == "D",
            Vector = new Vector3(Num(v, 4), Num(v, 5), Num(v, 6))
        };

        static float Num(string[] v, int i) =>
            i < v.Length && float.TryParse(v[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : 0f;

        static Color32 Rgb(string[] v) =>
            new Color32((byte)Mathf.Clamp(Num(v, 0), 0, 255), (byte)Mathf.Clamp(Num(v, 1), 0, 255),
                        (byte)Mathf.Clamp(Num(v, 2), 0, 255), 255);
    }
}
