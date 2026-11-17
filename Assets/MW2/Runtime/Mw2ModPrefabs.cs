using System;
using System.Collections.Generic;
using System.IO;
using MW2.Formats;
using UnityEngine;

namespace MW2
{
    /// <summary>
    /// Prefabs of mod weapon looks from Unity asset bundles (WeaponLook.PrefabLook): loads a mod's bundle once, takes the
    /// prefab, keeps only what draws or sounds (see <see cref="Allowed"/>; a Unity Light becomes a soft Mw2ModLight) and
    /// places a copy. Bundles carry no code of their own, and anything else a prefab holds (colliders, rigid bodies, the
    /// game's own scripts) is taken off before the copy wakes, so a prefab can never touch hits or the game's state.
    /// <see cref="Forget"/> unloads every bundle and removes the copies (the files changed, or another choice of mods).
    /// At most <see cref="MaxAlive"/> copies at once; further ones are not placed.
    /// Port addition with no counterpart in the original program.
    /// </summary>
    public static class Mw2ModPrefabs
    {
        public const int MaxAlive = 256;

        static readonly Dictionary<string, AssetBundle> s_bundles = new Dictionary<string, AssetBundle>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, GameObject> s_prefabs = new Dictionary<string, GameObject>(StringComparer.OrdinalIgnoreCase);
        static readonly HashSet<string> s_reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static readonly List<Mw2ModPrefab> s_alive = new List<Mw2ModPrefab>();
        static GameObject s_holder;

        static readonly Type[] Allowed =
        {
            typeof(Transform), typeof(ParticleSystem), typeof(ParticleSystemRenderer), typeof(LineRenderer), typeof(TrailRenderer),
            typeof(MeshFilter), typeof(MeshRenderer), typeof(SkinnedMeshRenderer), typeof(BillboardRenderer), typeof(LODGroup),
            typeof(Animator), typeof(Animation), typeof(AudioSource), typeof(Light),
        };

        /// <summary>The platform ending tried first for a bundle file (WeaponLook.PrefabLook.Endings).</summary>
        public static string Ending
        {
            get
            {
                switch (Application.platform)
                {
                    case RuntimePlatform.OSXPlayer: case RuntimePlatform.OSXEditor: return ".mac";
                    case RuntimePlatform.LinuxPlayer: case RuntimePlatform.LinuxEditor: return ".linux";
                    default: return ".windows";
                }
            }
        }

        /// <summary>Unloads every bundle and removes every placed copy; the next use loads the files again.</summary>
        public static void Forget()
        {
            foreach (var p in s_alive.ToArray()) if (p != null) UnityEngine.Object.Destroy(p.gameObject);
            s_alive.Clear();
            s_prefabs.Clear();
            foreach (var b in s_bundles.Values) if (b != null) b.Unload(true);
            s_bundles.Clear();
            s_reported.Clear();
        }

        /// <summary>
        /// Places a copy of <paramref name="look"/> at <paramref name="point"/> turned by <paramref name="rotation"/>; on
        /// <paramref name="parent"/> when given (a shot: it then follows it until <see cref="Mw2ModPrefab.Release"/>).
        /// Null when the prefab cannot be had or too many copies are alive.
        /// </summary>
        public static Mw2ModPrefab Place(WeaponLook.PrefabLook look, Vector3 point, Quaternion rotation, Transform parent)
        {
            if (look == null) return null;
            s_alive.RemoveAll(p => p == null);
            if (s_alive.Count >= MaxAlive) return null;
            var prefab = Prefab(look);
            if (prefab == null) return null;
            if (s_holder == null)
            {
                s_holder = new GameObject("MW2 mod prefabs (waiting)") { hideFlags = HideFlags.HideAndDontSave };
                s_holder.SetActive(false);
                UnityEngine.Object.DontDestroyOnLoad(s_holder);
            }
            // Made under an inactive holder, so nothing on it wakes before it is cleaned.
            var go = UnityEngine.Object.Instantiate(prefab, s_holder.transform, false);
            go.name = prefab.name;
            Clean(go, look);
            var placed = go.AddComponent<Mw2ModPrefab>();
            placed.Setup(look);
            if (parent != null) go.transform.SetParent(parent, false);
            else go.transform.SetParent(null, false);
            go.transform.SetPositionAndRotation(point, rotation);
            go.transform.localScale = prefab.transform.localScale * look.Scale;
            if (!go.activeSelf) go.SetActive(true);
            s_alive.Add(placed);
            return placed;
        }

        internal static void Gone(Mw2ModPrefab p) => s_alive.Remove(p);

        static GameObject Prefab(WeaponLook.PrefabLook look)
        {
            string key = look.Bundle + "|" + look.Asset;
            if (s_prefabs.TryGetValue(key, out var prefab)) return prefab;
            var bundle = Bundle(look.Bundle);
            if (bundle != null)
            {
                try { prefab = bundle.LoadAsset<GameObject>(look.Asset); }
                catch (Exception e) { Debug.LogException(e); }
                if (prefab == null)
                    Report(key, $"{Path.GetFileName(look.Bundle)}: no prefab \"{look.Asset}\" in it (it has: {string.Join(", ", Names(bundle))})");
            }
            s_prefabs[key] = prefab;
            return prefab;
        }

        static AssetBundle Bundle(string path)
        {
            string file = File.Exists(path + Ending) ? path + Ending : File.Exists(path) ? path : null;
            if (file == null)
            {
                Report(path, $"{Path.GetFileName(path)}: no bundle for this platform ({Path.GetFileName(path)}{Ending} or {Path.GetFileName(path)})");
                return null;
            }
            if (s_bundles.TryGetValue(file, out var bundle)) return bundle;
            try { bundle = AssetBundle.LoadFromFile(file); }
            catch (Exception e) { Debug.LogException(e); }
            if (bundle == null)
                Report(file, $"{Path.GetFileName(file)}: not an asset bundle this game can read (made by another Unity version, or for another platform?)");
            s_bundles[file] = bundle;
            return bundle;
        }

        static IEnumerable<string> Names(AssetBundle b)
        {
            foreach (var n in b.GetAllAssetNames()) yield return Path.GetFileNameWithoutExtension(n);
        }

        static void Report(string key, string text, bool leftOut = true)
        {
            if (s_reported.Add(key)) Mw2WeaponLooks.Say(leftOut ? text + " (the prefab is left out)" : text, true);
        }

        /// <summary>Takes off everything but pictures and sound; a Unity Light becomes a soft light of the game.</summary>
        static void Clean(GameObject go, WeaponLook.PrefabLook look)
        {
            var removed = new SortedSet<string>(StringComparer.Ordinal);
            // Twice: a component another one needs goes only after that one is gone.
            for (int pass = 0; pass < 2; pass++)
                foreach (var c in go.GetComponentsInChildren<Component>(true))
                {
                    if (c == null) continue;
                    var t = c.GetType();
                    if (Array.IndexOf(Allowed, t) >= 0) continue;
                    removed.Add(t.Name);
                    UnityEngine.Object.DestroyImmediate(c);
                }
            foreach (var l in go.GetComponentsInChildren<Light>(true))
            {
                if (l.type == LightType.Point || l.type == LightType.Spot)
                {
                    var soft = l.gameObject.AddComponent<Mw2ModLight>();
                    soft.Color = l.color;
                    soft.Intensity = l.intensity;
                    soft.Range = l.range;
                    soft.enabled = l.enabled;
                }
                UnityEngine.Object.DestroyImmediate(l);
            }
            // Missing scripts (null components) cannot be listed by type; they never run.
            if (removed.Count > 0)
                Report("clean|" + look.Bundle + "|" + look.Asset,
                       $"{look.Asset}: only pictures and sound are used; taken off: {string.Join(", ", removed)}", false);
        }
    }

    /// <summary>
    /// A placed copy of a mod prefab (Mw2ModPrefabs.Place). On a shot it waits for the shot to end (<see cref="Release"/>);
    /// then (or from placing, for muzzle and impact) its lines and soft lights fade out and thin over "life" seconds, and it
    /// goes once that time is over and its particles are gone (a prefab with neither lasts one second; at most 30 seconds).
    /// With "stretch" its line renderers run from the muzzle to the far end, their width wavering by "flicker"; a child named
    /// "Muzzle" is kept at the muzzle. With "hold" a shot's copy shows fully at least that long from placing, even when
    /// the shot ends sooner. Port addition with no counterpart in the original program.
    /// </summary>
    public class Mw2ModPrefab : MonoBehaviour
    {
        const float MaxLife = 30f;

        struct Line { public LineRenderer R; public float Width; public Gradient Colors; }

        WeaponLook.PrefabLook _look;
        ParticleSystem[] _particles;
        TrailRenderer[] _trails;
        Line[] _lines;
        Mw2ModLight[] _lights;
        float[] _lightIntensity;
        Transform[] _muzzles;
        bool _riding, _follows, _stretch;
        Vector3 _from, _to;
        Mw2ModWeaponView.Muzzle _muzzle;
        float _born, _released = -1f;
        bool _ending;
        Vector3 _end;
        Action _then;

        internal void Setup(WeaponLook.PrefabLook look)
        {
            _look = look;
            _particles = GetComponentsInChildren<ParticleSystem>(true);
            _trails = GetComponentsInChildren<TrailRenderer>(true);
            var lines = GetComponentsInChildren<LineRenderer>(true);
            _lines = new Line[lines.Length];
            for (int i = 0; i < lines.Length; i++)
                _lines[i] = new Line { R = lines[i], Width = lines[i].widthMultiplier, Colors = lines[i].colorGradient };
            _lights = GetComponentsInChildren<Mw2ModLight>(true);
            _lightIntensity = new float[_lights.Length];
            for (int i = 0; i < _lights.Length; i++) _lightIntensity[i] = _lights[i].Intensity;
            var muzzles = new System.Collections.Generic.List<Transform>();
            foreach (var t in GetComponentsInChildren<Transform>(true))
                if (t != transform && string.Equals(t.name, "Muzzle", StringComparison.OrdinalIgnoreCase)) muzzles.Add(t);
            _muzzles = muzzles.ToArray();
            _born = Time.time;
        }

        /// <summary>
        /// Belongs to a shot until <see cref="Release"/>: <paramref name="muzzle"/> is where it left, followed on the firing
        /// part every frame (the start of stretched lines, the place of "Muzzle" children); <paramref name="follows"/>: it
        /// rides on the shot's object.
        /// </summary>
        public void Ride(Mw2ModWeaponView.Muzzle muzzle, bool stretch, bool follows)
        {
            _riding = true;
            _follows = follows;
            _stretch = stretch && _lines.Length > 0;
            _muzzle = muzzle;
            _from = muzzle != null ? muzzle.Position : transform.position;
            if (_stretch) foreach (var l in _lines) l.R.useWorldSpace = true;
            LateUpdate();
        }

        /// <summary>The shot ended at <paramref name="point"/>: stop emitting, then fade out. A copy riding on the shot stays
        /// where it ended; one placed at once (reach "instant") stays where it was. With "hold" not before that time from
        /// placing. <paramref name="then"/> runs when it lets go (at once without a hold).</summary>
        public void Release(Vector3 point, Action then = null)
        {
            if (!_riding || _released >= 0f || _ending) { then?.Invoke(); return; }
            if (_follows)
            {
                transform.SetParent(null, true);
                transform.position = point;
            }
            _ending = true; _end = point; _then = then;
            if (Time.time - _born >= _look.Hold) LetGo();
        }

        void LetGo()
        {
            _released = Time.time;
            _to = transform.position;
            foreach (var p in _particles) if (p != null) p.Stop(false, ParticleSystemStopBehavior.StopEmitting);
            foreach (var t in _trails) if (t != null) t.emitting = false;
            LateUpdate();
            var then = _then; _then = null;
            then?.Invoke();
        }

        /// <summary>1 while it shows fully, down to 0 over "life" from the shot's end (or from placing).</summary>
        float Strength()
        {
            if (_look.Life < 0f || _riding && _released < 0f) return 1f;
            float from = _riding ? _released : _born;
            return _look.Life <= 0f ? 0f : Mathf.Clamp01(1f - (Time.time - from) / _look.Life);
        }

        void LateUpdate()
        {
            if (_muzzle != null) _from = _muzzle.Position;
            if (_riding) foreach (var m in _muzzles) if (m != null) m.position = _from;
            float k = Strength();
            if (_look.Life >= 0f)
                for (int i = 0; i < _lights.Length; i++) if (_lights[i] != null) _lights[i].Intensity = _lightIntensity[i] * k;
            if (_lines.Length == 0) return;
            var head = _released >= 0f ? _to : transform.position;
            foreach (var l in _lines)
            {
                if (l.R == null) continue;
                float wave = _look.Flicker > 0f ? 1f - _look.Flicker * Mw2WeaponLooks.RandomValue() : 1f;
                l.R.widthMultiplier = l.Width * k * wave;
                if (_look.Life >= 0f) l.R.colorGradient = Faded(l.Colors, k);
                if (!_stretch) continue;
                int n = Mathf.Max(2, l.R.positionCount);
                if (l.R.positionCount != n) l.R.positionCount = n;
                for (int i = 0; i < n; i++) l.R.SetPosition(i, Vector3.Lerp(_from, head, i / (float)(n - 1)));
            }
        }

        static Gradient Faded(Gradient g, float k)
        {
            if (k >= 1f) return g;
            var alphas = g.alphaKeys;
            for (int i = 0; i < alphas.Length; i++) alphas[i].alpha *= k;
            var f = new Gradient { mode = g.mode };
            f.SetKeys(g.colorKeys, alphas);
            return f;
        }

        void Update()
        {
            float now = Time.time;
            if (now - _born > MaxLife + (_riding && _released < 0f ? 60f : 0f)) { Destroy(gameObject); return; }
            if (_ending && _released < 0f && now - _born >= _look.Hold) LetGo();
            if (_riding && _released < 0f) return;
            float from = _riding ? _released : _born;
            if (_look.Life >= 0f && now - from < _look.Life) return;
            // Then until its particles are gone; a prefab with none and no life lasts one second.
            if (_particles.Length == 0) { if (_look.Life >= 0f || now - from >= 1f) Destroy(gameObject); return; }
            foreach (var p in _particles) if (p != null && p.IsAlive(false)) return;
            Destroy(gameObject);
        }

        void OnDestroy() => Mw2ModPrefabs.Gone(this);
    }
}
