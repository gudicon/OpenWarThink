using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MW2.Formats;
using UnityEngine;

namespace MW2
{
    /// <summary>
    /// Weapon looks from the active mods (WeaponLook, files effects/weapons/&lt;key&gt;.json) and unit destruction looks
    /// (MechLook, effects/mechs/&lt;key&gt;.json): finds the file for a weapon or a unit,
    /// keeps what it read, draws the muzzle and impact parts (flash sprite, soft light, sparks, prefab), puts the mods'
    /// pictures in place (Mw2ModWeaponView as Mw2WeaponView.Current, Mw2ModUnitView as Mw2UnitView.Current) and reloads while the game
    /// runs: twice a second it looks at the effects folders of the active mods and, when a file there was added, changed
    /// or removed, reads them again and says so at the top left of the screen together with any mistakes in the files.
    /// The next shot uses the new look. Port addition with no counterpart in the original program.
    /// </summary>
    public class Mw2WeaponLooks : MonoBehaviour
    {
        public enum Builtin { Beam, Glow }

        static Mw2WeaponLooks s_host;
        static readonly Dictionary<string, WeaponLook> s_cache = new Dictionary<string, WeaponLook>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, MechLook> s_mechCache = new Dictionary<string, MechLook>(StringComparer.OrdinalIgnoreCase);
        /// <summary>The mods' own random numbers, so their pictures never draw on the game's (UnityEngine.Random).</summary>
        static readonly System.Random s_random = new System.Random();

        /// <summary>A number from 0 to 1 for a mod picture, not taken from the game's UnityEngine.Random.</summary>
        public static float RandomValue() => (float)s_random.NextDouble();
        static readonly HashSet<string> s_reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, Material> s_materials = new Dictionary<string, Material>(StringComparer.OrdinalIgnoreCase);
        static Texture2D s_beamTexture, s_glowTexture;
        static Shader s_shader;

        struct Message { public string Text; public bool Error; public float Until; }
        readonly List<Message> _messages = new List<Message>();
        string _signature;
        float _nextPoll;
        GUIStyle _style, _shadow;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Start()
        {
            if (s_host != null) return;
            var go = new GameObject("MW2 weapon looks") { hideFlags = HideFlags.HideInHierarchy };
            DontDestroyOnLoad(go);
            s_host = go.AddComponent<Mw2WeaponLooks>();
            Mw2WeaponView.Current = new Mw2ModWeaponView();
            Mw2UnitView.Current = new Mw2ModUnitView();
            Mw2Mods.Changed += Forget;
            Mw2ModRules.Apply();
            Mw2Graphics.Apply();
            Mw2Lang.Apply();
        }

        void OnDestroy()
        {
            if (s_host != this) return;
            s_host = null;
            Mw2Mods.Changed -= Forget;
            if (Mw2WeaponView.Current is Mw2ModWeaponView) Mw2WeaponView.Current = null;
            if (Mw2UnitView.Current is Mw2ModUnitView) Mw2UnitView.Current = null;
        }

        /// <summary>Drops everything read, so the next shot reads the files again (the mods screen saved a new choice).</summary>
        public static void Forget()
        {
            s_cache.Clear();
            s_mechCache.Clear();
            s_reported.Clear();
            s_materials.Clear();
            Mw2ModPrefabs.Forget();
            Mw2ModRules.Apply();
            Mw2Graphics.Apply();
            Mw2Lang.Apply();
            if (s_host != null) s_host._signature = null;
        }

        // ---- finding a weapon's look ----

        /// <summary>The look of <paramref name="def"/> from the active mods, or null when no mod has one.</summary>
        public static WeaponLook Get(WeaponDef def)
        {
            if (def == null || string.IsNullOrEmpty(def.Name)) return null;
            if (s_cache.TryGetValue(def.Name, out var look)) return look;
            string path = null;
            try { path = Mw2Mods.Catalog.ResolveFirst(WeaponLook.Paths(def.Name, def.ProjectileType)); }
            catch (Exception e) { Debug.LogWarning("MW2 mods: " + e.Message); }
            look = path != null ? Read(path) : null;
            s_cache[def.Name] = look;
            return look;
        }

        /// <summary>The destruction look of <paramref name="unit"/> from the active mods, or null when no mod has one.</summary>
        public static MechLook Get(Mw2MechCombat unit)
        {
            if (unit == null) return null;
            var paths = MechLook.Paths(unit.ModelName, unit.Tons, unit.GamepieceType);
            string key = string.Join("|", paths);
            if (s_mechCache.TryGetValue(key, out var look)) return look;
            string path = null;
            try { path = Mw2Mods.Catalog.ResolveFirst(paths); }
            catch (Exception e) { Debug.LogWarning("MW2 mods: " + e.Message); }
            look = path != null ? ReadMech(path) : null;
            s_mechCache[key] = look;
            return look;
        }

        static MechLook ReadMech(string path)
        {
            string shown = Shown(path);
            try
            {
                var look = MechLook.Parse(File.ReadAllText(path), path);
                if (s_reported.Add(path))
                    foreach (var w in look.Warnings) Say(shown + ": " + w, true);
                return look;
            }
            catch (Exception e) when (e is MiniJson.Error || e is IOException || e is UnauthorizedAccessException)
            {
                if (s_reported.Add(path)) Say(shown + ": " + e.Message + " (the original look is used)", true);
                return null;
            }
        }

        static WeaponLook Read(string path)
        {
            string shown = Shown(path);
            try
            {
                var look = WeaponLook.Parse(File.ReadAllText(path), path);
                if (s_reported.Add(path))
                    foreach (var w in look.Warnings) Say(shown + ": " + w, true);
                return look;
            }
            catch (Exception e) when (e is MiniJson.Error || e is IOException || e is UnauthorizedAccessException)
            {
                if (s_reported.Add(path)) Say(shown + ": " + e.Message + " (the original look is used)", true);
                return null;
            }
        }

        /// <summary>A file of a mod as the player sees it: the mod's folder name and the path inside it.</summary>
        public static string Shown(string path)
        {
            var mod = Mw2Mods.Catalog.OwnerOf(path);
            return mod == null ? path : Path.GetFileName(mod.Folder) + "/" + path.Substring(mod.Folder.Length + 1).Replace('\\', '/');
        }

        // ---- muzzle and impact ----

        /// <summary>The muzzle part of a look at <paramref name="point"/>, sparks along <paramref name="direction"/>.</summary>
        public static void Muzzle(WeaponLook look, Vector3 point, Vector3 direction) => Flash(look?.Muzzle, point, direction, "muzzle");

        /// <summary>The impact part of a look; <paramref name="direction"/> points back the way the shot came.</summary>
        public static void Impact(WeaponLook look, Vector3 point, Vector3 direction) => Flash(look?.Impact, point, direction, "impact");

        static void Flash(WeaponLook.FlashLook f, Vector3 point, Vector3 direction, string name)
        {
            if (direction.sqrMagnitude < 1e-8f) direction = Vector3.up;
            Flash(f, point, direction, Quaternion.LookRotation(direction.normalized), name);
        }

        /// <summary>
        /// A flash part at <paramref name="point"/>: light, sprite, sparks along <paramref name="direction"/> and the prefab
        /// turned to <paramref name="prefabRotation"/>.
        /// </summary>
        public static void Flash(WeaponLook.FlashLook f, Vector3 point, Vector3 direction, Quaternion prefabRotation, string name)
        {
            if (f == null) return;
            if (direction.sqrMagnitude < 1e-8f) direction = Vector3.up;
            if (f.Light != null) Light(f.Light, point, null);
            if (f.Flash != null && f.Flash.Size > 0f) Sprite(f.Flash, point, name);
            if (f.Sparks != null && f.Sparks.Count > 0) Sparks(f.Sparks, point, direction.normalized, name);
            if (f.Prefab != null) Mw2ModPrefabs.Place(f.Prefab, point, prefabRotation, null);
        }

        /// <summary>A soft light at <paramref name="point"/>, on <paramref name="parent"/> when given (it then lives as long).</summary>
        public static Mw2ModLight Light(WeaponLook.LightLook l, Vector3 point, Transform parent)
        {
            if (l.Range <= 0f || l.Intensity <= 0f) return null;
            var go = new GameObject("mod light");
            if (parent != null) go.transform.SetParent(parent, false);
            go.transform.position = point;
            var light = go.AddComponent<Mw2ModLight>();
            light.Color = ToColor(l.Color);
            light.Intensity = l.Intensity;
            light.Range = l.Range;
            light.Life = parent != null ? 0f : Mathf.Max(l.Time, 0.01f);
            return light;
        }

        static void Sprite(WeaponLook.SpriteLook s, Vector3 point, string name)
        {
            var ps = NewParticles("mod " + name + " flash", point, Quaternion.identity, 1, s.Texture, out var renderer);
            var main = ps.main;
            main.duration = s.Time;
            main.startLifetime = s.Time;
            main.startSpeed = 0f;
            main.startSize = s.Size;
            main.startColor = ToColor(s.Color);
            main.startRotation = (float)(s_random.NextDouble() * Math.PI * 2.0);
            var size = ps.sizeOverLifetime;
            size.enabled = true;
            float m = Mathf.Max(1f, s.Grow);
            size.size = new ParticleSystem.MinMaxCurve(m, AnimationCurve.Linear(0f, 1f / m, 1f, s.Grow / m));
            Fade(ps, Color.white, new Color(1f, 1f, 1f, 0f));
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            ps.gameObject.SetActive(true);
        }

        static void Sparks(WeaponLook.SparksLook s, Vector3 point, Vector3 direction, string name)
        {
            var ps = NewParticles("mod " + name + " sparks", point, Quaternion.LookRotation(direction), s.Count, s.Texture, out var renderer);
            var main = ps.main;
            main.duration = 0.05f;
            main.startLifetime = new ParticleSystem.MinMaxCurve(s.Life * 0.6f, s.Life);
            main.startSpeed = new ParticleSystem.MinMaxCurve(s.Speed * 0.3f, s.Speed);
            main.startSize = new ParticleSystem.MinMaxCurve(s.Size * 0.6f, s.Size);
            main.startColor = Color.white;
            main.gravityModifier = s.Gravity;
            var shape = ps.shape;
            shape.enabled = true;
            if (s.Spread >= 179f) shape.shapeType = ParticleSystemShapeType.Sphere;
            else { shape.shapeType = ParticleSystemShapeType.Cone; shape.angle = Mathf.Min(s.Spread * 0.5f, 90f); }
            shape.radius = 0.05f;
            Fade(ps, ToColor(s.Color), ToColor(s.EndColor));
            renderer.renderMode = ParticleSystemRenderMode.Stretch;
            renderer.velocityScale = 0.04f;
            renderer.lengthScale = 1.5f;
            ps.gameObject.SetActive(true);
        }

        /// <summary>A one-burst particle system, inactive until set up (it plays when activated) and gone when done.</summary>
        static ParticleSystem NewParticles(string name, Vector3 point, Quaternion rotation, int count, string texture, out ParticleSystemRenderer renderer)
        {
            var go = new GameObject(name);
            go.SetActive(false);
            go.transform.SetPositionAndRotation(point, rotation);
            var ps = go.AddComponent<ParticleSystem>();
            var main = ps.main;
            main.loop = false;
            main.playOnAwake = true;
            main.maxParticles = Mathf.Max(1, count);
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.stopAction = ParticleSystemStopAction.Destroy;
            var emission = ps.emission;
            emission.rateOverTime = 0f;
            emission.SetBursts(new[] { new ParticleSystem.Burst(0f, (short)Mathf.Clamp(count, 1, short.MaxValue)) });
            var shape = ps.shape;
            shape.enabled = false;
            renderer = go.GetComponent<ParticleSystemRenderer>();
            renderer.sharedMaterial = Material(texture, Builtin.Glow);
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            return ps;
        }

        static void Fade(ParticleSystem ps, Color from, Color to)
        {
            var col = ps.colorOverLifetime;
            col.enabled = true;
            var g = new Gradient();
            g.SetKeys(new[] { new GradientColorKey(from, 0f), new GradientColorKey(to, 1f) },
                      new[] { new GradientAlphaKey(from.a, 0f), new GradientAlphaKey(to.a, 1f) });
            col.color = g;
        }

        // ---- materials ----

        public static Color ToColor(WeaponLook.Rgba c) => new Color(c.R, c.G, c.B, c.A);

        /// <summary>The additive glow material for a mod picture, or the built-in soft one when there is none.</summary>
        public static Material Material(string texturePath, Builtin builtin)
        {
            string key = texturePath ?? "builtin:" + builtin;
            if (s_materials.TryGetValue(key, out var m) && m != null) return m;
            if (s_shader == null) s_shader = Shader.Find("MW2/ModGlow");
            Texture2D tex = null;
            if (texturePath != null)
            {
                try
                {
                    tex = new Texture2D(2, 2, TextureFormat.RGBA32, true) { name = Path.GetFileName(texturePath), wrapMode = TextureWrapMode.Clamp };
                    if (!tex.LoadImage(File.ReadAllBytes(texturePath))) { Say(Shown(texturePath) + ": not a picture the game can read", true); tex = null; }
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    Say(Shown(texturePath) + ": " + e.Message, true);
                    tex = null;
                }
            }
            if (tex == null) tex = builtin == Builtin.Beam ? BeamTexture() : GlowTexture();
            m = new Material(s_shader != null ? s_shader : Shader.Find("Sprites/Default")) { name = "mod glow " + tex.name, mainTexture = tex };
            s_materials[key] = m;
            return m;
        }

        /// <summary>Soft across the beam (v), fading in at the tail (u = 0) and sharp at the head (u = 1).</summary>
        static Texture2D BeamTexture()
        {
            if (s_beamTexture != null) return s_beamTexture;
            const int w = 64, h = 32;
            var t = new Texture2D(w, h, TextureFormat.RGBA32, false) { name = "mod beam", wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.DontSave };
            var px = new Color32[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float across = 1f - Mathf.Abs((y + 0.5f) / h * 2f - 1f);
                    float u = (x + 0.5f) / w;
                    float ends = Mathf.Clamp01(u / 0.35f) * Mathf.Clamp01((1f - u) / 0.04f);
                    float a = across * across * (3f - 2f * across) * ends;
                    px[y * w + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(a * 255f));
                }
            t.SetPixels32(px);
            t.Apply();
            return s_beamTexture = t;
        }

        /// <summary>A round soft spot for flashes and sparks.</summary>
        static Texture2D GlowTexture()
        {
            if (s_glowTexture != null) return s_glowTexture;
            const int n = 64;
            var t = new Texture2D(n, n, TextureFormat.RGBA32, false) { name = "mod glow", wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.DontSave };
            var px = new Color32[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float dx = (x + 0.5f) / n * 2f - 1f, dy = (y + 0.5f) / n * 2f - 1f;
                    float r = Mathf.Clamp01(1f - Mathf.Sqrt(dx * dx + dy * dy));
                    px[y * n + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(r * r * 255f));
                }
            t.SetPixels32(px);
            t.Apply();
            return s_glowTexture = t;
        }

        // ---- hot reload ----

        void Update()
        {
            Mw2Graphics.Tick();
            if (Time.realtimeSinceStartup < _nextPoll) return;
            _nextPoll = Time.realtimeSinceStartup + 0.5f;
            string now = Signature();
            if (_signature == null) { _signature = now; return; }
            if (now == _signature) return;
            _signature = now;
            Mw2Mods.Reload();
            s_cache.Clear();
            s_mechCache.Clear();
            s_reported.Clear();
            s_materials.Clear();
            Mw2ModPrefabs.Forget();
            Mw2ModRules.Apply();
            Mw2Graphics.Apply();
            Mw2Lang.Apply();
            ReportAll();
        }

        /// <summary>Names, sizes and times of the files in the effects, rules, lang and graphics folders of the active mods.</summary>
        static string Signature()
        {
            var sb = new StringBuilder();
            ModCatalog catalog;
            try { catalog = Mw2Mods.Catalog; }
            catch (Exception) { return ""; }
            foreach (var m in catalog.Active)
            {
                sb.Append(m.Id).Append('|');
                foreach (string folder in new[] { "effects", "rules", "lang", "graphics" })
                {
                    string dir = Path.Combine(m.Folder, folder);
                    try
                    {
                        if (!Directory.Exists(dir)) continue;
                        foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories))
                            sb.Append(f.FullName).Append(':').Append(f.Length).Append(':').Append(f.LastWriteTimeUtc.Ticks).Append(';');
                    }
                    catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
                }
            }
            return sb.ToString();
        }

        /// <summary>After a reload: reads every weapon and unit look of the active mods and tells what was read and what is wrong.</summary>
        static void ReportAll()
        {
            var catalog = Mw2Mods.Catalog;
            int weapons = 0, units = 0;
            foreach (var m in catalog.Active)
                foreach (var f in m.Files)
                {
                    if (!f.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
                    string path = Path.Combine(m.Folder, f.Replace('/', Path.DirectorySeparatorChar));
                    if (f.StartsWith(WeaponLook.Folder + "/", StringComparison.OrdinalIgnoreCase)) { weapons++; Read(path); }
                    else if (f.StartsWith(MechLook.Folder + "/", StringComparison.OrdinalIgnoreCase)) { units++; ReadMech(path); }
                }
            if (weapons + units == 0) Say("Weapon effects reloaded: no effect files in the active mods", false);
            else
            {
                string Files(int n) => $"{n} file{(n == 1 ? "" : "s")}";
                Say("Weapon effects reloaded: " + Files(weapons) + (units > 0 ? ", unit effects: " + Files(units) : ""), false);
            }
        }

        // ---- messages ----

        /// <summary>Shows a line at the top left of the screen for a few seconds and writes it to the log.</summary>
        public static void Say(string text, bool error)
        {
            if (error) Debug.LogWarning("MW2 mods: " + text);
            else Debug.Log("MW2 mods: " + text);
            if (s_host == null) return;
            s_host._messages.Add(new Message { Text = text, Error = error, Until = Time.realtimeSinceStartup + (error ? 12f : 4f) });
            while (s_host._messages.Count > 10) s_host._messages.RemoveAt(0);
        }

        void OnGUI()
        {
            if (_messages.Count == 0) return;
            float now = Time.realtimeSinceStartup;
            _messages.RemoveAll(m => m.Until < now);
            if (_messages.Count == 0 || Event.current.type != EventType.Repaint) return;
            int size = Mathf.Clamp(Screen.height / 50, 11, 24);
            if (_style == null || _style.fontSize != size)
            {
                _style = new GUIStyle(GUI.skin.label) { fontSize = size, wordWrap = true, richText = false };
                _shadow = new GUIStyle(_style) { normal = { textColor = Color.black } };
            }
            GUI.depth = -1000;
            float y = 8f, width = Screen.width * 0.6f;
            foreach (var m in _messages)
            {
                var content = new GUIContent(m.Text);
                float h = _style.CalcHeight(content, width);
                _shadow.Draw(new Rect(10f, y + 2f, width, h), content, false, false, false, false);
                _style.normal.textColor = m.Error ? new Color32(255, 70, 60, 255) : new Color32(236, 236, 236, 255);
                _style.Draw(new Rect(8f, y, width, h), content, false, false, false, false);
                y += h + 2f;
            }
        }
    }
}
