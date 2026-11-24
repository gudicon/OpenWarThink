using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace MW2.Formats
{
    /// <summary>
    /// How a mod draws one weapon: a JSON file in the mod at effects/weapons/&lt;key&gt;.json, where the key is the weapon's
    /// own name (MLAS, cerMLAS), its class (medium-laser) or its family (laser) — see <see cref="Keys"/>. Three parts, each
    /// optional: "shot" (the flying shot: a glowing trail, a light), "laser" (a wavering energy beam from the muzzle to where
    /// the shot ends, see <see cref="LaserLook"/>), "muzzle" (when it fires) and "impact" (where it hits), the last two a flash
    /// sprite, a light and sparks. "shot", "muzzle" and "impact" may also place a prefab of the mod's own from a Unity asset
    /// bundle (see <see cref="PrefabLook"/>). Every part may hide the original look ("hideOriginal": true); what a part leaves
    /// out keeps the original. Only the look changes: damage, speed, range, sound and area damage stay the weapon's.
    /// Port addition with no counterpart in the original program.
    /// </summary>
    public sealed class WeaponLook
    {
        public const string Folder = "effects/weapons";

        public string Source;           // full path of the JSON file
        public ShotLook Shot;
        public LaserLook Laser;
        public FlashLook Muzzle, Impact;
        /// <summary>Things in the file that were not understood (unknown names, wrong values), with their place.</summary>
        public readonly List<string> Warnings = new List<string>();

        public struct Rgba
        {
            public float R, G, B, A;
            public Rgba(float r, float g, float b, float a = 1f) { R = r; G = g; B = b; A = a; }
            public override string ToString() => string.Format(CultureInfo.InvariantCulture, "({0:0.###}, {1:0.###}, {2:0.###}, {3:0.###})", R, G, B, A);
        }

        public sealed class BeamLook
        {
            public float Length = 6f;       // metres behind the shot (never further back than the muzzle)
            public float Width = 0.6f;      // metres, the glow around the core
            public Rgba Color = new Rgba(1f, 0.2f, 0.1f, 0.8f);
            public float CoreWidth = 0.3f;  // part of Width
            public Rgba CoreColor = new Rgba(1f, 1f, 1f, 1f);
            public string Texture;          // full path, or null for the built-in soft beam
        }

        public sealed class LightLook
        {
            public Rgba Color = new Rgba(1f, 0.3f, 0.2f);
            public float Range = 12f;       // metres
            public float Intensity = 1.5f;
            public float Time = 0.15f;      // seconds, muzzle and impact lights only (fades out)
        }

        public sealed class SpriteLook
        {
            public float Size = 1.5f;       // metres
            public float Grow = 1.5f;       // size at the end / size at the start
            public Rgba Color = new Rgba(1f, 0.6f, 0.4f, 1f);
            public float Time = 0.12f;      // seconds
            public string Texture;
        }

        public sealed class SparksLook
        {
            public int Count = 20;
            public float Speed = 8f;        // metres per second, the fastest spark
            public float Size = 0.12f;      // metres
            public float Life = 0.45f;      // seconds
            public float Spread = 60f;      // degrees of the cone
            public float Gravity = 1f;      // 1 = the planet's pull as in Unity units (9.81 m/s^2)
            public Rgba Color = new Rgba(1f, 0.8f, 0.5f, 1f);
            public Rgba EndColor = new Rgba(1f, 0.2f, 0f, 0f);
            public string Texture;
        }

        /// <summary>
        /// A laser beam (port of the "Energy Beams" look): a narrow bright core inside a wider halo of the colour, one or more
        /// strands wavering about the line and held still at its ends, on a strip that turns about the line to face the eye.
        /// It runs from the muzzle to where the shot ends. Reach "shot" (default): the far end is the flying shot itself, so the
        /// beam grows at the shot's speed and stops where the game's shot hits (or runs out); it then lasts Duration and goes
        /// out. Reach "instant": the far end is found at once by a ray of its own along the shot's line (the first thing the
        /// shot could hit, else the shot's full reach); that ray only draws, and the game's shot still flies and hits on its
        /// own, possibly elsewhere when the target moves. Pulses: Count flashes, Interval seconds apart, each PulseTime long.
        /// </summary>
        public sealed class LaserLook
        {
            public const string ReachShot = "shot", ReachInstant = "instant";
            public string Reach = ReachShot;
            public bool HideShot = true;        // the flying shot's model, light and ground glow
            public float Width = 0.15f;         // metres, the core
            public float Room = 40f;            // the strip is Width x Room high: room for the waver and the halo
            public Rgba Color = new Rgba(0.25f, 0.95f, 0.3f);      // the halo
            public Rgba? CoreColor;             // null: Color mixed with white by CoreMix
            public float CoreMix = 0.7f;
            public float Halo = 2.5f;           // halo thickness in core thicknesses
            public float Glow = 4f, HaloGlow = 1.8f;
            public int Strands = 2;
            public float StrandDifference;      // 0: later strands half as thick, 1: as thick as the first
            public float Waver = 2f;            // how far the strands stray
            public int Roughness = 3;           // noise octaves
            public float Frequency = 10f;       // ripples per Ripple metres
            public float Ripple = 25f;          // metres
            public float Speed = 4f;
            public float Edge = 0.05f;          // part of the length at each end where the beam comes to rest
            public float Appear = 0.015f;       // seconds to full brightness
            public float Duration = 0.18f;      // seconds a flash takes to go out
            public float Thin = 1f;             // 1: thins to nothing going out, 0: keeps its width
            public float BreakUp = 1f;          // how much it breaks up going out
            public float Dim = 0f;              // 1: also fades in brightness going out
            public int Pulses = 1;
            public float PulseTime = -1f;       // seconds; below 0: Duration
            public float Interval = 0.085f;     // seconds between pulse starts
            public float NearFrom = 1f, NearTo = 5f;   // metres from the eye over which the beam comes up out of nothing
            public BeamLights Lights;

            public Rgba Core => CoreColor ?? new Rgba(Color.R + (1 - Color.R) * CoreMix, Color.G + (1 - Color.G) * CoreMix,
                                                      Color.B + (1 - Color.B) * CoreMix, 1f);
            public float FlashTime => Pulses > 1 && PulseTime >= 0f ? PulseTime : Duration;
        }

        /// <summary>Lights along a laser for each flash: from the muzzle to the far end, one per Spacing metres, at least 2.</summary>
        public sealed class BeamLights
        {
            public Rgba? Color;                 // null: the laser's colour
            public float Intensity = 1.5f;
            public float Range = 20f;           // metres
            public int Max = 4;                 // 0..8 per flash
            public float Spacing = -1f;         // metres; below 0: 2 x Range
        }

        /// <summary>
        /// A prefab from a Unity asset bundle of the mod, made with the same Unity version as the game (the Editor menu
        /// MW2 -> Mods -> Build Asset Bundle). "bundle" names the bundle file next to the JSON file (or below it): the game
        /// first tries the name with its platform's ending (.windows, .mac, .linux), then the name itself. "asset" is the
        /// prefab's name in the bundle. Only pictures and sound are kept from it: particle systems, line, trail and mesh
        /// renderers, animations, audio sources; a Unity Light becomes a soft light of the game; anything else (colliders,
        /// rigid bodies, scripts) is removed. Muzzle: placed at the muzzle, +Z along the shot. Impact: at the hit, +Z back the
        /// way the shot came. Shot: its root is the far end, +Z along the flight; with "reach" "shot" (default) it rides on the
        /// flying shot and stays where the shot ended; with "reach" "instant" it is placed at once where the shot's line first
        /// meets something (a ray of its own, as the laser's, picture only) or at the shot's full reach, and stays there while
        /// the game's shot flies. When the shot ends its particles stop emitting and its trails stop. A child named "Muzzle"
        /// is kept at the muzzle. "stretch": the line renderers of a shot prefab run from the muzzle to the far end (a beam);
        /// "flicker" (0..1) makes their width waver from frame to frame. "hold" (shot prefabs, 0..5 s): the least time it
        /// shows fully, even when the game's shot ends sooner (a shot through a slot whose last shot stopped at the target
        /// hits on its first move, as in the original); the impact pictures of the look then come when it lets go. "life": seconds over which its lines and lights fade
        /// out and thin (for a shot, after the shot ends); the copy then goes once its particles are gone too; below 0
        /// (default) the lines and lights stay until the particles are gone. A copy lasts at most 30 seconds (a shot's copy
        /// waits for the shot first). "scale" multiplies its size.
        /// </summary>
        public sealed class PrefabLook
        {
            public static readonly string[] Endings = { ".windows", ".mac", ".linux" };
            public string Bundle;           // full path without the platform ending (as written in the file)
            public string Asset;
            public float Life = -1f;
            public float Scale = 1f;
            public bool Stretch;
            public bool Instant;            // "reach": "instant" (shot prefabs)
            public float Flicker;
            public float Hold;              // "hold": least seconds shown fully (shot prefabs)
        }

        public sealed class ShotLook
        {
            public bool HideOriginal;       // the shot model, its point light and its ground glow
            public BeamLook Beam;
            public LightLook Light;
            public PrefabLook Prefab;
        }

        public sealed class FlashLook
        {
            public bool HideOriginal;       // the original effect model and its light (and the debris of missile and autocannon hits); sound, area damage stay
            public SpriteLook Flash;
            public LightLook Light;
            public SparksLook Sparks;
            public PrefabLook Prefab;
        }

        // ---- keys ----

        /// <summary>
        /// The file names tried for a weapon, most specific first: its name from the weapon table, its class and its family
        /// (lasers: large-laser, large-pulse-laser ... laser; missiles: lrm, srm, streak-srm, inferno-srm, nuke, torpedo,
        /// arrow-iv, narc ... missile; autocannon, ultra-autocannon, lb-autocannon, machine-gun, ams, gauss ... ballistic;
        /// ppc; flamer).
        /// </summary>
        public static List<string> Keys(string name, int projectileType)
        {
            name = name ?? "";
            string n = name.ToUpperInvariant();
            string cls, family;
            switch (projectileType)
            {
                case 0: case 1: case 2:
                    cls = (projectileType == 0 ? "large" : projectileType == 1 ? "medium" : "small") + (n.Contains("PLAS") ? "-pulse-laser" : "-laser");
                    family = "laser";
                    break;
                case 3: case 4:
                    cls = n.Contains("NUKE") ? "nuke" : n.Contains("INF") ? "inferno-srm" : n.Contains("SSRM") ? "streak-srm"
                        : n.Contains("SRM") ? "srm" : "lrm";
                    family = "missile";
                    break;
                case 5:
                    cls = n.Contains("MGUN") ? "machine-gun" : n.Contains("AMS") ? "ams" : n.Contains("LBAC") ? "lb-autocannon"
                        : n.Contains("UAC") ? "ultra-autocannon" : "autocannon";
                    family = "ballistic";
                    break;
                case 6: cls = family = "ppc"; break;
                case 7: cls = "gauss"; family = "ballistic"; break;
                case 8: cls = family = "flamer"; break;
                case 9: cls = "torpedo"; family = "missile"; break;
                case 10: cls = "arrow-iv"; family = "missile"; break;
                case 12: cls = "narc"; family = "missile"; break;
                default: cls = family = "other"; break;
            }
            var keys = new List<string>();
            foreach (string k in new[] { name, cls, family })
                if (k.Length > 0 && !keys.Exists(x => string.Equals(x, k, StringComparison.OrdinalIgnoreCase))) keys.Add(k);
            return keys;
        }

        /// <summary>The mod-relative paths of <see cref="Keys"/>.</summary>
        public static List<string> Paths(string name, int projectileType) =>
            Keys(name, projectileType).ConvertAll(k => Folder + "/" + k + ".json");

        // ---- reading ----

        /// <summary>
        /// Reads a weapon look. A file that is not JSON throws <see cref="MiniJson.Error"/>; values that are wrong or unknown
        /// keep their default and go to <see cref="Warnings"/>. Texture names are file names next to the JSON file (or below it).
        /// </summary>
        public static WeaponLook Parse(string text, string source)
        {
            var look = new WeaponLook { Source = source };
            if (!(MiniJson.Parse(text) is Dictionary<string, object> root))
                throw new MiniJson.Error("the file must hold one { ... } object");
            var r = new Reader(look.Warnings, source != null ? Path.GetDirectoryName(source) : null);
            r.Known(root, "", "shot", "laser", "muzzle", "impact", "comment");
            var shot = r.Object(root, "shot", "shot");
            if (shot != null)
            {
                r.Known(shot, "shot", "hideOriginal", "beam", "light", "prefab", "comment");
                look.Shot = new ShotLook { HideOriginal = r.Flag(shot, "hideOriginal", "shot", false) };
                var beam = r.Object(shot, "beam", "shot.beam");
                if (beam != null) look.Shot.Beam = r.Beam(beam, "shot.beam");
                var light = r.Object(shot, "light", "shot.light");
                if (light != null) look.Shot.Light = r.Light(light, "shot.light");
                var prefab = r.Object(shot, "prefab", "shot.prefab");
                if (prefab != null) look.Shot.Prefab = r.Prefab(prefab, "shot.prefab", true);
            }
            look.Muzzle = r.Flash(root, "muzzle");
            look.Impact = r.Flash(root, "impact");
            var laser = r.Object(root, "laser", "laser");
            if (laser != null) look.Laser = r.Laser(laser, "laser");
            return look;
        }

        /// <summary>Reads the parts of a look file; also used by <see cref="MechLook"/>. Paths are relative to the JSON file's folder.</summary>
        internal sealed class Reader
        {
            readonly List<string> _warnings;
            readonly string _dir;

            public Reader(List<string> warnings, string dir) { _warnings = warnings; _dir = dir; }

            void Warn(string where, string what) => _warnings.Add((where.Length > 0 ? where + ": " : "") + what);

            public void Known(Dictionary<string, object> o, string where, params string[] names)
            {
                foreach (var key in o.Keys)
                    if (Array.FindIndex(names, n => string.Equals(n, key, StringComparison.OrdinalIgnoreCase)) < 0)
                        Warn(where, $"unknown name \"{key}\" (known: {string.Join(", ", Array.FindAll(names, n => n != "comment"))})");
            }

            public Dictionary<string, object> Object(Dictionary<string, object> o, string key, string where)
            {
                if (!o.TryGetValue(key, out var v) || v == null) return null;
                if (v is Dictionary<string, object> d) return d;
                Warn(where, "expected { ... }");
                return null;
            }

            public bool Flag(Dictionary<string, object> o, string key, string where, bool fallback)
            {
                if (!o.TryGetValue(key, out var v) || v == null) return fallback;
                if (v is bool b) return b;
                Warn(where + "." + key, "expected true or false");
                return fallback;
            }

            public float Number(Dictionary<string, object> o, string key, string where, float fallback, float min, float max)
            {
                if (!o.TryGetValue(key, out var v) || v == null) return fallback;
                if (!(v is double d)) { Warn(where + "." + key, "expected a number"); return fallback; }
                if (d < min || d > max)
                {
                    Warn(where + "." + key, string.Format(CultureInfo.InvariantCulture, "{0} is outside {1:0.####}..{2:0.####}, using {3:0.####}", d, min, max,
                        Math.Max(min, Math.Min(max, d))));
                    d = Math.Max(min, Math.Min(max, d));
                }
                return (float)d;
            }

            public Rgba Color(Dictionary<string, object> o, string key, string where, Rgba fallback)
            {
                if (!o.TryGetValue(key, out var v) || v == null) return fallback;
                if (TryColor(v, out var c)) return c;
                Warn(where + "." + key, "expected a colour: \"#rrggbb\", \"#rrggbbaa\" or [r, g, b, a] from 0 to 1");
                return fallback;
            }

            /// <summary>A file name next to the JSON file or below it, as written; null (with a warning) when it is not one.</summary>
            string Relative(Dictionary<string, object> o, string key, string where)
            {
                if (!o.TryGetValue(key, out var v) || v == null) return null;
                if (!(v is string s) || s.Length == 0) { Warn(where + "." + key, "expected a file name"); return null; }
                string rel = s.Replace('\\', '/');
                bool up = Array.IndexOf(rel.Split('/'), "..") >= 0;
                if (rel.StartsWith("/") || rel.Contains(":") || up && !InsideEffects(rel))
                {
                    Warn(where + "." + key, $"\"{s}\" must be a file next to the JSON file, in a folder below it, or elsewhere in the mod's effects folder");
                    return null;
                }
                return rel;
            }

            /// <summary>
            /// Whether <paramref name="rel"/> with ".." stays inside the mod's "effects" folder (the nearest folder of that name
            /// above the JSON file), so effects/mechs/x.json can use a bundle of effects/weapons ("../weapons/mymod").
            /// </summary>
            bool InsideEffects(string rel)
            {
                if (_dir == null) return false;
                string effects = _dir;
                while (!string.IsNullOrEmpty(effects) && !string.Equals(Path.GetFileName(effects), "effects", StringComparison.OrdinalIgnoreCase))
                    effects = Path.GetDirectoryName(effects);
                if (string.IsNullOrEmpty(effects)) return false;
                string full = Path.GetFullPath(Path.Combine(_dir, rel.Replace('/', Path.DirectorySeparatorChar)));
                string root = Path.GetFullPath(effects).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
            }

            public PrefabLook Prefab(Dictionary<string, object> o, string where, bool shot)
            {
                if (shot) Known(o, where, "bundle", "asset", "life", "scale", "stretch", "reach", "flicker", "hold", "comment");
                else Known(o, where, "bundle", "asset", "life", "scale", "comment");
                if (!o.ContainsKey("bundle")) { Warn(where, "\"bundle\" is missing (the asset bundle file next to the JSON file)"); return null; }
                string rel = Relative(o, "bundle", where);
                if (rel == null) return null;
                if (!(o.TryGetValue("asset", out var a) && a is string asset && asset.Length > 0))
                {
                    Warn(where + ".asset", "expected the prefab's name in the bundle");
                    return null;
                }
                var p = new PrefabLook { Asset = asset, Bundle = rel };
                if (_dir != null)
                {
                    p.Bundle = Path.Combine(_dir, rel.Replace('/', Path.DirectorySeparatorChar));
                    if (!File.Exists(p.Bundle) && !Array.Exists(PrefabLook.Endings, e => File.Exists(p.Bundle + e)))
                    {
                        Warn(where + ".bundle", $"no file {rel} (or {rel}.windows, .mac, .linux) next to the JSON file");
                        return null;
                    }
                }
                p.Life = Number(o, "life", where, p.Life, -1f, 30f);
                p.Scale = Number(o, "scale", where, p.Scale, 0.01f, 100f);
                if (shot)
                {
                    p.Stretch = Flag(o, "stretch", where, false);
                    p.Flicker = Number(o, "flicker", where, 0f, 0f, 1f);
                    p.Hold = Number(o, "hold", where, 0f, 0f, 5f);
                    if (o.TryGetValue("reach", out var reach) && reach != null)
                    {
                        if (string.Equals(reach as string, "instant", StringComparison.OrdinalIgnoreCase)) p.Instant = true;
                        else if (!string.Equals(reach as string, "shot", StringComparison.OrdinalIgnoreCase))
                            Warn(where + ".reach", "expected \"shot\" or \"instant\", using \"shot\"");
                    }
                }
                return p;
            }

            public string Texture(Dictionary<string, object> o, string key, string where)
            {
                string rel = Relative(o, key, where);
                if (rel == null) return null;
                if (_dir == null) return rel;
                string path = Path.Combine(_dir, rel.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(path)) { Warn(where + "." + key, $"no file {rel} next to the JSON file"); return null; }
                string ext = Path.GetExtension(path).ToLowerInvariant();
                if (ext != ".png" && ext != ".jpg" && ext != ".jpeg") { Warn(where + "." + key, $"{rel}: only .png and .jpg pictures are read"); return null; }
                return path;
            }

            public BeamLook Beam(Dictionary<string, object> o, string where)
            {
                Known(o, where, "length", "width", "color", "coreWidth", "coreColor", "texture", "comment");
                var b = new BeamLook();
                b.Length = Number(o, "length", where, b.Length, 0f, 1000f);
                b.Width = Number(o, "width", where, b.Width, 0f, 50f);
                b.Color = Color(o, "color", where, b.Color);
                b.CoreWidth = Number(o, "coreWidth", where, b.CoreWidth, 0f, 1f);
                b.CoreColor = Color(o, "coreColor", where, b.CoreColor);
                b.Texture = Texture(o, "texture", where);
                return b;
            }

            public LightLook Light(Dictionary<string, object> o, string where)
            {
                Known(o, where, "color", "range", "intensity", "time", "comment");
                var l = new LightLook();
                l.Color = Color(o, "color", where, l.Color);
                l.Range = Number(o, "range", where, l.Range, 0f, 500f);
                l.Intensity = Number(o, "intensity", where, l.Intensity, 0f, 20f);
                l.Time = Number(o, "time", where, l.Time, 0f, 30f);
                return l;
            }

            public int Int(Dictionary<string, object> o, string key, string where, int fallback, int min, int max)
            {
                if (!o.TryGetValue(key, out var v) || v == null) return fallback;
                if (!(v is double d) || Math.Abs(d - Math.Round(d)) > 1e-9) { Warn(where + "." + key, "expected a whole number"); return fallback; }
                return (int)Math.Round(Number(o, key, where, fallback, min, max));
            }

            public LaserLook Laser(Dictionary<string, object> o, string w)
            {
                Known(o, w, "reach", "hideShot", "width", "room", "color", "coreColor", "coreMix", "halo", "glow", "haloGlow", "strands",
                      "strandDifference", "waver", "roughness", "frequency", "ripple", "speed", "edge", "appear", "duration", "thin",
                      "breakUp", "dim", "pulses", "nearFade", "lights", "comment");
                var l = new LaserLook();
                if (o.TryGetValue("reach", out var reach) && reach != null)
                {
                    string s = reach as string;
                    if (string.Equals(s, LaserLook.ReachShot, StringComparison.OrdinalIgnoreCase)) l.Reach = LaserLook.ReachShot;
                    else if (string.Equals(s, LaserLook.ReachInstant, StringComparison.OrdinalIgnoreCase)) l.Reach = LaserLook.ReachInstant;
                    else Warn(w + ".reach", "expected \"shot\" or \"instant\", using \"shot\"");
                }
                l.HideShot = Flag(o, "hideShot", w, l.HideShot);
                l.Width = Number(o, "width", w, l.Width, 0.005f, 5f);
                l.Room = Number(o, "room", w, l.Room, 4f, 200f);
                l.Color = Color(o, "color", w, l.Color);
                if (o.TryGetValue("coreColor", out var core) && core != null) l.CoreColor = Color(o, "coreColor", w, l.Core);
                l.CoreMix = Number(o, "coreMix", w, l.CoreMix, 0f, 1f);
                l.Halo = Number(o, "halo", w, l.Halo, 0f, 20f);
                l.Glow = Number(o, "glow", w, l.Glow, 0f, 20f);
                l.HaloGlow = Number(o, "haloGlow", w, l.HaloGlow, 0f, 20f);
                l.Strands = Int(o, "strands", w, l.Strands, 1, 4);
                l.StrandDifference = Number(o, "strandDifference", w, l.StrandDifference, 0f, 1f);
                l.Waver = Number(o, "waver", w, l.Waver, 0f, 10f);
                l.Roughness = Int(o, "roughness", w, l.Roughness, 1, 6);
                l.Frequency = Number(o, "frequency", w, l.Frequency, 0.1f, 50f);
                l.Ripple = Number(o, "ripple", w, l.Ripple, 1f, 500f);
                l.Speed = Number(o, "speed", w, l.Speed, 0f, 50f);
                l.Edge = Number(o, "edge", w, l.Edge, 0f, 0.5f);
                l.Appear = Number(o, "appear", w, l.Appear, 0f, 2f);
                l.Duration = Number(o, "duration", w, l.Duration, 0.01f, 5f);
                l.Thin = Number(o, "thin", w, l.Thin, 0f, 1f);
                l.BreakUp = Number(o, "breakUp", w, l.BreakUp, 0f, 5f);
                l.Dim = Number(o, "dim", w, l.Dim, 0f, 1f);
                var pulses = Object(o, "pulses", w + ".pulses");
                if (pulses != null)
                {
                    string pw = w + ".pulses";
                    Known(pulses, pw, "count", "duration", "interval", "comment");
                    l.Pulses = Int(pulses, "count", pw, l.Pulses, 1, 8);
                    if (pulses.ContainsKey("duration")) l.PulseTime = Number(pulses, "duration", pw, l.Duration, 0.01f, 5f);
                    l.Interval = Number(pulses, "interval", pw, l.Interval, 0f, 2f);
                }
                if (o.TryGetValue("nearFade", out var near) && near != null)
                {
                    if (near is List<object> n && n.Count == 2 && n[0] is double a && n[1] is double b && a >= 0 && b >= a && b <= 100)
                    { l.NearFrom = (float)a; l.NearTo = (float)b; }
                    else Warn(w + ".nearFade", "expected [from, to] in metres with 0 <= from <= to <= 100, using [1, 5]");
                }
                var lights = Object(o, "lights", w + ".lights");
                if (lights != null)
                {
                    string lw = w + ".lights";
                    Known(lights, lw, "color", "intensity", "range", "max", "spacing", "comment");
                    var b = new BeamLights();
                    if (lights.TryGetValue("color", out var lc) && lc != null) b.Color = Color(lights, "color", lw, l.Color);
                    b.Intensity = Number(lights, "intensity", lw, b.Intensity, 0f, 20f);
                    b.Range = Number(lights, "range", lw, b.Range, 0f, 200f);
                    b.Max = Int(lights, "max", lw, b.Max, 0, 8);
                    if (lights.ContainsKey("spacing")) b.Spacing = Number(lights, "spacing", lw, 2f * b.Range, 1f, 1000f);
                    l.Lights = b;
                }
                return l;
            }

            public FlashLook Flash(Dictionary<string, object> root, string key)
            {
                var o = Object(root, key, key);
                if (o == null) return null;
                Known(o, key, "hideOriginal", "flash", "light", "sparks", "prefab", "comment");
                var f = new FlashLook { HideOriginal = Flag(o, "hideOriginal", key, false) };
                var flash = Object(o, "flash", key + ".flash");
                if (flash != null)
                {
                    string w = key + ".flash";
                    Known(flash, w, "size", "grow", "color", "time", "texture", "comment");
                    var s = new SpriteLook();
                    s.Size = Number(flash, "size", w, s.Size, 0f, 200f);
                    s.Grow = Number(flash, "grow", w, s.Grow, 0f, 20f);
                    s.Color = Color(flash, "color", w, s.Color);
                    s.Time = Number(flash, "time", w, s.Time, 0.01f, 30f);
                    s.Texture = Texture(flash, "texture", w);
                    f.Flash = s;
                }
                var light = Object(o, "light", key + ".light");
                if (light != null) f.Light = Light(light, key + ".light");
                var prefab = Object(o, "prefab", key + ".prefab");
                if (prefab != null) f.Prefab = Prefab(prefab, key + ".prefab", false);
                var sparks = Object(o, "sparks", key + ".sparks");
                if (sparks != null)
                {
                    string w = key + ".sparks";
                    Known(sparks, w, "count", "speed", "size", "life", "spread", "gravity", "color", "endColor", "texture", "comment");
                    var s = new SparksLook();
                    s.Count = (int)Math.Round(Number(sparks, "count", w, s.Count, 0f, 500f));
                    s.Speed = Number(sparks, "speed", w, s.Speed, 0f, 500f);
                    s.Size = Number(sparks, "size", w, s.Size, 0f, 50f);
                    s.Life = Number(sparks, "life", w, s.Life, 0.01f, 30f);
                    s.Spread = Number(sparks, "spread", w, s.Spread, 0f, 180f);
                    s.Gravity = Number(sparks, "gravity", w, s.Gravity, -10f, 10f);
                    s.Color = Color(sparks, "color", w, s.Color);
                    s.EndColor = Color(sparks, "endColor", w, s.EndColor);
                    s.Texture = Texture(sparks, "texture", w);
                    f.Sparks = s;
                }
                return f;
            }
        }

        /// <summary>"#rgb", "#rrggbb", "#rrggbbaa" or a list [r, g, b] / [r, g, b, a] of numbers from 0 to 1.</summary>
        public static bool TryColor(object v, out Rgba c)
        {
            c = new Rgba(1, 1, 1, 1);
            if (v is string s)
            {
                s = s.Trim();
                if (!s.StartsWith("#")) return false;
                s = s.Substring(1);
                if (s.Length == 3) s = new string(new[] { s[0], s[0], s[1], s[1], s[2], s[2] });
                if (s.Length != 6 && s.Length != 8) return false;
                if (!uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint x)) return false;
                if (s.Length == 6) x = (x << 8) | 0xff;
                c = new Rgba(((x >> 24) & 0xff) / 255f, ((x >> 16) & 0xff) / 255f, ((x >> 8) & 0xff) / 255f, (x & 0xff) / 255f);
                return true;
            }
            if (v is List<object> list && (list.Count == 3 || list.Count == 4))
            {
                var f = new float[4] { 1, 1, 1, 1 };
                for (int i = 0; i < list.Count; i++)
                {
                    if (!(list[i] is double d) || d < 0 || d > 1) return false;
                    f[i] = (float)d;
                }
                c = new Rgba(f[0], f[1], f[2], f[3]);
                return true;
            }
            return false;
        }
    }
}
