using System;
using System.Collections.Generic;
using System.Globalization;

namespace MW2.Formats
{
    /// <summary>
    /// Picture settings a mod may set, file graphics/quality.json in the mod (the lowest active mod with the file wins, as
    /// for every file). Every name is optional; a name that is left out keeps the original's picture, and without the file
    /// the game draws exactly as before. Only how the world looks changes: the simulation, the sensors and the frame rate
    /// cap stay the original's (smoke drift and the mission table pass run once per frame, so a higher frame rate would
    /// change the game).
    /// - "antiAliasing": 0, 2, 4 or 8 multisamples on the edges of polygons (QualitySettings.antiAliasing).
    /// - "textureSamples": 1..8, how many bilinear samples per axis a texture may take across one screen pixel. The CEL
    ///   textures hold palette indices and have no mipmaps (the live palette makes the colours), so far textures shimmer;
    ///   the samples average the pixel's whole footprint. 1 is the original (one bilinear sample, as Glide).
    /// - "fogDistance": 0.25..8, a factor on the fog distance of SKYGND.PAR (1 is the original).
    /// - "brightness", "contrast", "saturation", "gamma": colour of the finished frame (1 is unchanged). Every one keeps
    ///   black black, so the fades of the palette from and to black still work.
    /// - "sharpen": 0..2, "vignette": 0..1 (0 is off).
    /// - "bloom": { "intensity" 0..4, "threshold" 0..1, "soft" 0..1, "radius" 1..8 }: bright parts glow.
    /// - "lighting": { ... }: modern lighting of the world (Mw2Modern.cginc) with the mission's own lights: "enabled"
    ///   (true), "sun" and "sky" 0..4 (factors on the direction lights and the ambient light of SKYGND.PAR), "fill" 0..2
    ///   (a factor on the second direction light, which the missions set against the sun to light the shaded side; it
    ///   lights without highlights), "bounce" 0..1
    ///   (light from the ground below), "reflections" 0..2 (the sky in the surfaces), "shadows" 0..1 (strength of real sun
    ///   shadows; above 0 they replace the flat unit shadows of the original), "shadowDistance" 20..2000 metres,
    ///   "occlusion" 0..1 and "occlusionRadius" 0.2..10 metres (darker corners, screen-space ambient occlusion),
    ///   "contactShadows" { "strength" 0..1, "length" 0.1..5 metres } (small sun shadows traced through the depth of the
    ///   view: under feet, in the joints of armour), and the
    ///   surfaces "units", "structures", "ground", each { "metallic" 0..1, "roughness" 0.05..1 }, "cockpit" (the same
    ///   and "light" 0..1: the frame of the cockpit seen from inside is lit like the world, keeping that much of the
    ///   original's flat full shade), "probe" { "enabled",
    ///   "resolution" 64..1024, "facesPerFrame" 1..6 } (a reflection probe at the camera: the world seen in the surfaces)
    ///   and "lights" { "intensity" 0..8, "range" 0.05..2, "always" } (the original's point lights of shots and
    ///   explosions with a soft falloff; range is a factor on their cube size; "always" makes them also when Multiple
    ///   Lights is off, for this lighting only), "rays" { "intensity" 0..4, "decay" 0.8..1, "length" 0.1..1 } (sun rays
    ///   through gaps in clouds and buildings), "ssr" { "intensity" 0..2, "distance" 5..1000 metres, "thickness"
    ///   0.1..20 metres } (screen-space reflections on smooth surfaces) and "emissive" 1..8 (explosions, glows and other
    ///   effect sprites brighter, for an HDR picture). The infrared view keeps the original lighting.
    ///   "unitParts" { "paint", "metal", "mechanics", "weapons", "glass": { "metallic", "roughness", "albedo" 0.25..2,
    ///   "textures": [ names ] }, "rows": a class name }: surfaces for the parts of units by the texture on the face, named
    ///   as the mission's texture table names its CEL ("*" any letters, "?" one letter). The defaults follow the names of
    ///   the game's own textures: PLATE* (the painted plates of Material_ApplyPaint 0x100745eb) and TH* (the insignia)
    ///   paint, SSMETSU*, SLMETSPC and SLPLAINC metal, the vents, ribs, grilles and SLHORIC mechanics, SWEP* weapons and
    ///   the cloud texture of the windshields (*CLOU) glass; the windshield key 0x18c is glass whatever it shows. "rows" gives a class to the faces coloured by a palette row
    ///   (types 1 and 4: barrels, trims). Without "unitParts", or for a class it does not name, a face takes "units".
    /// - "hdr": { "exposure" 0.1..8, "tonemapping" "aces" | "reinhard" | "none" }: the camera draws in high dynamic
    ///   range and the frame is brought back to the screen by the tonemapping (bloom then takes the light above 1).
    /// The cockpit HUD is drawn after the frame and keeps its colours. Port addition with no counterpart in the original
    /// program.
    /// </summary>
    public sealed class GraphicsQuality
    {
        public const string File = "graphics/quality.json";

        public string Source;
        /// <summary>-1 keeps the setting of the quality level.</summary>
        public int AntiAliasing = -1;
        public int TextureSamples = 1;
        public float FogDistance = 1f;
        public float Brightness = 1f, Contrast = 1f, Saturation = 1f, Gamma = 1f, Sharpen, Vignette;
        public float BloomIntensity, BloomThreshold = 0.8f, BloomSoft = 0.5f;
        public int BloomRadius = 5;

        public bool Lighting;
        public float Fill = 1f;
        public float Sun = 1f, Sky = 1f, Bounce = 0.35f, Reflections = 1f, Shadows = 0.8f, ShadowDistance = 300f;
        public float Occlusion = 0.6f, OcclusionRadius = 1.5f;
        public float ContactShadows, ContactLength = 1.5f;
        public bool Probe = true;
        public int ProbeResolution = 256, ProbeFacesPerFrame = 1;
        public float LightsIntensity = 1.5f, LightsRange = 0.2f;
        public bool LightsAlways = true;
        public float RaysIntensity, RaysDecay = 0.96f, RaysLength = 0.6f;
        public float SsrIntensity, SsrDistance = 150f, SsrThickness = 2f;
        public float Emissive = 1f;
        public bool Hdr;
        public float Exposure = 1f;
        /// <summary>0 none, 1 ACES, 2 Reinhard.</summary>
        public int Tonemapping = 1;
        public Surface Units = new Surface(0.25f, 0.45f), Structures = new Surface(0f, 0.75f), Ground = new Surface(0f, 0.95f);
        /// <summary>"lighting.cockpit": the surface of the cockpit shell seen from inside; null keeps it at the full shade of
        /// the original's cockpit pass (0x1001bea2), unlit.</summary>
        public Surface? Cockpit;
        /// <summary>"lighting.cockpit.light" 0..1: how much of the original's flat full shade the shell keeps under the
        /// modern lighting (its faces mostly look away from the sun, so the sky alone leaves them dark).</summary>
        public float CockpitLight = 0.7f;

        public struct Surface
        {
            public float Metallic, Roughness, Albedo;
            public Surface(float metallic, float roughness, float albedo = 1f) { Metallic = metallic; Roughness = roughness; Albedo = albedo; }
        }

        /// <summary>The classes of "unitParts", in the order of their numbers in the shaders (1..5; 0 is "units").</summary>
        public static readonly string[] PartNames = { "paint", "metal", "mechanics", "weapons", "glass" };

        /// <summary>A class of "unitParts": its surface and the texture names it takes.</summary>
        public sealed class Part
        {
            public bool Enabled;
            public Surface Surface;
            public string[] Textures;
        }

        /// <summary>The classes of "unitParts" (PartNames order), with the defaults; only the named ones are enabled.</summary>
        public readonly Part[] Parts =
        {
            new Part { Surface = new Surface(0.05f, 0.55f), Textures = new[] { "PLATE*", "TH*" } },
            new Part { Surface = new Surface(0.8f, 0.35f), Textures = new[] { "SSMETSU*", "SLMETSPC", "SLPLAINC" } },
            new Part { Surface = new Surface(0.5f, 0.75f, 0.8f), Textures = new[] { "S?VENT*", "S?VERT*", "S?MESH", "SLHORIC" } },
            new Part { Surface = new Surface(0.65f, 0.3f), Textures = new[] { "SWEP*" } },
            new Part { Surface = new Surface(0f, 0.08f), Textures = new[] { "*CLOU" } },
        };

        /// <summary>The class (1..5) of the faces coloured by a palette row, 0 for "units".</summary>
        public int RowsPart;

        /// <summary>The class (1..5) of a texture of a unit part by its CEL name, 0 when no enabled class takes it.</summary>
        public int PartOf(string celName)
        {
            if (string.IsNullOrEmpty(celName)) return 0;
            for (int i = 0; i < Parts.Length; i++)
                if (Parts[i].Enabled)
                    foreach (string pattern in Parts[i].Textures)
                        if (Matches(pattern, celName)) return i + 1;
            return 0;
        }

        /// <summary>"*" any letters, "?" one letter, case ignored.</summary>
        public static bool Matches(string pattern, string name)
        {
            int p = 0, n = 0, star = -1, mark = 0;
            while (n < name.Length)
            {
                if (p < pattern.Length && (pattern[p] == '?' || char.ToUpperInvariant(pattern[p]) == char.ToUpperInvariant(name[n]))) { p++; n++; }
                else if (p < pattern.Length && pattern[p] == '*') { star = p++; mark = n; }
                else if (star >= 0) { p = star + 1; n = ++mark; }
                else return false;
            }
            while (p < pattern.Length && pattern[p] == '*') p++;
            return p == pattern.Length;
        }
        public readonly List<string> Warnings = new List<string>();

        /// <summary>True when the finished frame needs the colour pass (Mw2GraphicsPost).</summary>
        public bool NeedsPost =>
            Brightness != 1f || Contrast != 1f || Saturation != 1f || Gamma != 1f || Sharpen > 0f || Vignette > 0f || BloomIntensity > 0f
            || NeedsViewDepth || Hdr;

        /// <summary>True when the frame needs the ambient occlusion pass.</summary>
        public bool NeedsOcclusion => Lighting && Occlusion > 0f;
        public bool NeedsRays => Lighting && RaysIntensity > 0f;
        public bool NeedsSsr => Lighting && SsrIntensity > 0f;
        public bool NeedsContact => Lighting && ContactShadows > 0f;
        /// <summary>True when the post passes need the depth (and the surfaces) of the view (Mw2GraphicsDepth.DrawView).</summary>
        public bool NeedsViewDepth => NeedsOcclusion || NeedsRays || NeedsSsr || NeedsContact;

        /// <summary>True when nothing differs from the original picture.</summary>
        public bool IsOriginal => AntiAliasing < 0 && TextureSamples <= 1 && FogDistance == 1f && !NeedsPost && !Lighting && !Hdr;

        const string Known = "antiAliasing, textureSamples, fogDistance, brightness, contrast, saturation, gamma, sharpen, vignette, bloom, lighting, hdr";
        const string KnownBloom = "intensity, threshold, soft, radius";
        const string KnownLighting = "enabled, sun, sky, fill, bounce, reflections, shadows, shadowDistance, contactShadows, occlusion, occlusionRadius, units, structures, ground, cockpit, unitParts, probe, lights, rays, ssr, emissive";

        /// <summary>Reads the file; a file that is not JSON throws <see cref="MiniJson.Error"/>. Unknown names and wrong
        /// types go to <see cref="Warnings"/> and keep the original; numbers out of range are brought into it, with a
        /// warning.</summary>
        public static GraphicsQuality Parse(string text, string source)
        {
            var q = new GraphicsQuality { Source = source };
            if (!(MiniJson.Parse(text) is Dictionary<string, object> root))
                throw new MiniJson.Error("the file must hold one { ... } object");
            foreach (var pair in root)
            {
                string key = pair.Key;
                switch (key.ToLowerInvariant())
                {
                    case "comment": break;
                    case "antialiasing":
                        if (pair.Value is double aa && (aa == 0 || aa == 1 || aa == 2 || aa == 4 || aa == 8))
                            q.AntiAliasing = aa == 1 ? 0 : (int)aa;
                        else q.Warnings.Add("antiAliasing: expected 0, 2, 4 or 8");
                        break;
                    case "texturesamples": q.TextureSamples = (int)Math.Round(q.Number(key, pair.Value, 1, 8, 1)); break;
                    case "fogdistance": q.FogDistance = q.Number(key, pair.Value, 0.25, 8, 1); break;
                    case "brightness": q.Brightness = q.Number(key, pair.Value, 0, 4, 1); break;
                    case "contrast": q.Contrast = q.Number(key, pair.Value, 0.25, 4, 1); break;
                    case "saturation": q.Saturation = q.Number(key, pair.Value, 0, 4, 1); break;
                    case "gamma": q.Gamma = q.Number(key, pair.Value, 0.25, 4, 1); break;
                    case "sharpen": q.Sharpen = q.Number(key, pair.Value, 0, 2, 0); break;
                    case "vignette": q.Vignette = q.Number(key, pair.Value, 0, 1, 0); break;
                    case "bloom":
                        if (pair.Value is Dictionary<string, object> bloom) q.ReadBloom(bloom);
                        else q.Warnings.Add("bloom: expected { \"intensity\": ..., \"threshold\": ... }");
                        break;
                    case "hdr": q.ReadHdr(key, pair.Value); break;
                    case "lighting":
                        if (pair.Value is Dictionary<string, object> lighting) q.ReadLighting(lighting);
                        else q.Warnings.Add("lighting: expected { \"shadows\": ..., \"units\": { ... } }");
                        break;
                    default: q.Warnings.Add($"unknown name \"{key}\" (known: {Known})"); break;
                }
            }
            return q;
        }

        void ReadBloom(Dictionary<string, object> bloom)
        {
            foreach (var pair in bloom)
            {
                string key = "bloom." + pair.Key;
                switch (pair.Key.ToLowerInvariant())
                {
                    case "comment": break;
                    case "intensity": BloomIntensity = Number(key, pair.Value, 0, 4, 0); break;
                    case "threshold": BloomThreshold = Number(key, pair.Value, 0, 8, 0.8); break;
                    case "soft": BloomSoft = Number(key, pair.Value, 0, 1, 0.5); break;
                    case "radius": BloomRadius = (int)Math.Round(Number(key, pair.Value, 1, 8, 5)); break;
                    default: Warnings.Add($"unknown name \"{key}\" (known in bloom: {KnownBloom})"); break;
                }
            }
        }

        void ReadLighting(Dictionary<string, object> lighting)
        {
            Lighting = true;
            foreach (var pair in lighting)
            {
                string key = "lighting." + pair.Key;
                switch (pair.Key.ToLowerInvariant())
                {
                    case "comment": break;
                    case "enabled":
                        if (pair.Value is bool on) Lighting = on;
                        else Warnings.Add(key + ": expected true or false");
                        break;
                    case "sun": Sun = Number(key, pair.Value, 0, 4, 1); break;
                    case "sky": Sky = Number(key, pair.Value, 0, 4, 1); break;
                    case "fill": Fill = Number(key, pair.Value, 0, 2, 1); break;
                    case "bounce": Bounce = Number(key, pair.Value, 0, 1, 0.35); break;
                    case "reflections": Reflections = Number(key, pair.Value, 0, 2, 1); break;
                    case "shadows": Shadows = Number(key, pair.Value, 0, 1, 0.8); break;
                    case "shadowdistance": ShadowDistance = Number(key, pair.Value, 20, 2000, 300); break;
                    case "occlusion": Occlusion = Number(key, pair.Value, 0, 1, 0.6); break;
                    case "contactshadows":
                        foreach (var r in Section(key, pair.Value))
                            switch (r.Key.ToLowerInvariant())
                            {
                                case "comment": break;
                                case "strength": ContactShadows = Number(key + ".strength", r.Value, 0, 1, 0); break;
                                case "length": ContactLength = Number(key + ".length", r.Value, 0.1, 5, 1.5); break;
                                default: Warnings.Add($"unknown name \"{key}.{r.Key}\" (known: strength, length)"); break;
                            }
                        break;
                    case "occlusionradius": OcclusionRadius = Number(key, pair.Value, 0.2, 10, 1.5); break;
                    case "units": Units = ReadSurface(key, pair.Value, Units); break;
                    case "structures": Structures = ReadSurface(key, pair.Value, Structures); break;
                    case "ground": Ground = ReadSurface(key, pair.Value, Ground); break;
                    case "cockpit": ReadCockpit(key, pair.Value); break;
                    case "unitparts": ReadParts(key, pair.Value); break;
                    case "probe": ReadProbe(key, pair.Value); break;
                    case "lights": ReadLights(key, pair.Value); break;
                    case "rays":
                        foreach (var r in Section(key, pair.Value))
                            switch (r.Key.ToLowerInvariant())
                            {
                                case "comment": break;
                                case "intensity": RaysIntensity = Number(key + ".intensity", r.Value, 0, 4, 0); break;
                                case "decay": RaysDecay = Number(key + ".decay", r.Value, 0.8, 1, 0.96); break;
                                case "length": RaysLength = Number(key + ".length", r.Value, 0.1, 1, 0.6); break;
                                default: Warnings.Add($"unknown name \"{key}.{r.Key}\" (known: intensity, decay, length)"); break;
                            }
                        break;
                    case "ssr":
                        foreach (var r in Section(key, pair.Value))
                            switch (r.Key.ToLowerInvariant())
                            {
                                case "comment": break;
                                case "intensity": SsrIntensity = Number(key + ".intensity", r.Value, 0, 2, 0); break;
                                case "distance": SsrDistance = Number(key + ".distance", r.Value, 5, 1000, 150); break;
                                case "thickness": SsrThickness = Number(key + ".thickness", r.Value, 0.1, 20, 2); break;
                                default: Warnings.Add($"unknown name \"{key}.{r.Key}\" (known: intensity, distance, thickness)"); break;
                            }
                        break;
                    case "emissive": Emissive = Number(key, pair.Value, 1, 8, 1); break;
                    default: Warnings.Add($"unknown name \"{key}\" (known in lighting: {KnownLighting})"); break;
                }
            }
        }

        /// <summary>The names of an object section; a warning and nothing when the value is not an object.</summary>
        IEnumerable<KeyValuePair<string, object>> Section(string key, object value)
        {
            if (value is Dictionary<string, object> o) return o;
            Warnings.Add(key + ": expected { ... }");
            return new Dictionary<string, object>();
        }

        void ReadHdr(string key, object value)
        {
            Hdr = true;
            foreach (var pair in Section(key, value))
                switch (pair.Key.ToLowerInvariant())
                {
                    case "comment": break;
                    case "enabled":
                        if (pair.Value is bool b) Hdr = b;
                        else Warnings.Add(key + ".enabled: expected true or false");
                        break;
                    case "exposure": Exposure = Number(key + ".exposure", pair.Value, 0.1, 8, 1); break;
                    case "tonemapping":
                        string t = pair.Value as string;
                        if (string.Equals(t, "aces", StringComparison.OrdinalIgnoreCase)) Tonemapping = 1;
                        else if (string.Equals(t, "reinhard", StringComparison.OrdinalIgnoreCase)) Tonemapping = 2;
                        else if (string.Equals(t, "none", StringComparison.OrdinalIgnoreCase)) Tonemapping = 0;
                        else Warnings.Add(key + ".tonemapping: expected \"aces\", \"reinhard\" or \"none\"");
                        break;
                    default: Warnings.Add($"unknown name \"{key}.{pair.Key}\" (known: enabled, exposure, tonemapping)"); break;
                }
        }

        void ReadParts(string key, object value)
        {
            foreach (var pair in Section(key, value))
            {
                string name = pair.Key.ToLowerInvariant();
                if (name == "comment") continue;
                if (name == "rows")
                {
                    string cls = (pair.Value as string)?.ToLowerInvariant();
                    int at = cls == null ? -1 : Array.IndexOf(PartNames, cls);
                    if (cls == "units" || cls == "") RowsPart = 0;
                    else if (at >= 0) RowsPart = at + 1;
                    else Warnings.Add($"{key}.rows: expected \"units\" or one of {string.Join(", ", PartNames)}");
                    continue;
                }
                int i = Array.IndexOf(PartNames, name);
                if (i < 0) { Warnings.Add($"unknown name \"{key}.{pair.Key}\" (known: {string.Join(", ", PartNames)}, rows)"); continue; }
                var part = Parts[i];
                string sub = key + "." + PartNames[i];
                if (!(pair.Value is Dictionary<string, object> o)) { Warnings.Add(sub + ": expected { \"metallic\": ..., \"roughness\": ... }"); continue; }
                part.Enabled = true;
                var rest = new Dictionary<string, object>();
                foreach (var f in o)
                {
                    switch (f.Key.ToLowerInvariant())
                    {
                        case "textures":
                            if (f.Value is List<object> list && list.Count > 0 && list.TrueForAll(x => x is string t && t.Length > 0))
                                part.Textures = list.ConvertAll(x => (string)x).ToArray();
                            else Warnings.Add(sub + ".textures: expected a list of texture names, such as [\"PLATE*\"]");
                            break;
                        case "albedo": part.Surface.Albedo = Number(sub + ".albedo", f.Value, 0.25, 2, part.Surface.Albedo); break;
                        default: rest[f.Key] = f.Value; break;
                    }
                }
                part.Surface = ReadSurface(sub, rest, part.Surface, true);
            }
        }

        void ReadProbe(string key, object value)
        {
            if (!(value is Dictionary<string, object> o)) { Warnings.Add(key + ": expected { \"enabled\": ..., \"resolution\": ... }"); return; }
            foreach (var pair in o)
                switch (pair.Key.ToLowerInvariant())
                {
                    case "comment": break;
                    case "enabled":
                        if (pair.Value is bool b) Probe = b;
                        else Warnings.Add(key + ".enabled: expected true or false");
                        break;
                    case "resolution":
                        int r = (int)Math.Round(Number(key + ".resolution", pair.Value, 64, 1024, 256));
                        int pow = 64;
                        while (pow * 2 <= r) pow *= 2;
                        if (pow != r) Warnings.Add($"{key}.resolution: {r} is not a power of two, {pow} is used");
                        ProbeResolution = pow;
                        break;
                    case "facesperframe": ProbeFacesPerFrame = (int)Math.Round(Number(key + ".facesPerFrame", pair.Value, 1, 6, 1)); break;
                    default: Warnings.Add($"unknown name \"{key}.{pair.Key}\" (known: enabled, resolution, facesPerFrame)"); break;
                }
        }

        void ReadLights(string key, object value)
        {
            if (!(value is Dictionary<string, object> o)) { Warnings.Add(key + ": expected { \"intensity\": ..., \"range\": ... }"); return; }
            foreach (var pair in o)
                switch (pair.Key.ToLowerInvariant())
                {
                    case "comment": break;
                    case "intensity": LightsIntensity = Number(key + ".intensity", pair.Value, 0, 8, 1.5); break;
                    case "range": LightsRange = Number(key + ".range", pair.Value, 0.05, 2, 0.2); break;
                    case "always":
                        if (pair.Value is bool b) LightsAlways = b;
                        else Warnings.Add(key + ".always: expected true or false");
                        break;
                    default: Warnings.Add($"unknown name \"{key}.{pair.Key}\" (known: intensity, range, always)"); break;
                }
        }

        void ReadCockpit(string key, object value)
        {
            var s = Cockpit ?? new Surface(0.3f, 0.5f);
            if (!(value is Dictionary<string, object> o))
            {
                Warnings.Add(key + ": expected { \"metallic\": ..., \"roughness\": ..., \"light\": ... }");
                return;
            }
            foreach (var pair in o)
                switch (pair.Key.ToLowerInvariant())
                {
                    case "comment": break;
                    case "metallic": s.Metallic = Number(key + ".metallic", pair.Value, 0, 1, s.Metallic); break;
                    case "roughness": s.Roughness = Number(key + ".roughness", pair.Value, 0.05, 1, s.Roughness); break;
                    case "light": CockpitLight = Number(key + ".light", pair.Value, 0, 1, CockpitLight); break;
                    default: Warnings.Add($"unknown name \"{key}.{pair.Key}\" (known: metallic, roughness, light)"); break;
                }
            Cockpit = s;
        }

        Surface ReadSurface(string key, object value, Surface s, bool part = false)
        {
            if (!(value is Dictionary<string, object> o))
            {
                Warnings.Add(key + ": expected { \"metallic\": ..., \"roughness\": ... }");
                return s;
            }
            foreach (var pair in o)
                switch (pair.Key.ToLowerInvariant())
                {
                    case "comment": break;
                    case "metallic": s.Metallic = Number(key + ".metallic", pair.Value, 0, 1, s.Metallic); break;
                    case "roughness": s.Roughness = Number(key + ".roughness", pair.Value, 0.05, 1, s.Roughness); break;
                    default: Warnings.Add($"unknown name \"{key}.{pair.Key}\" (known: metallic, roughness{(part ? ", albedo, textures" : "")})"); break;
                }
            return s;
        }

        float Number(string key, object value, double min, double max, double original)
        {
            if (!(value is double d) || double.IsNaN(d))
            {
                Warnings.Add($"{key}: expected a number from {Show(min)} to {Show(max)}");
                return (float)original;
            }
            if (d < min || d > max)
            {
                double clamped = Math.Max(min, Math.Min(max, d));
                Warnings.Add($"{key}: {Show(d)} is out of {Show(min)}..{Show(max)}, {Show(clamped)} is used");
                d = clamped;
            }
            return (float)d;
        }

        static string Show(double d) => d.ToString("0.###", CultureInfo.InvariantCulture);
    }
}
