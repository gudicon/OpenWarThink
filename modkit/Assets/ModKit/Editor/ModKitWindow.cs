using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace OpenWarThink.ModKit
{
    /// <summary>
    /// Mod Kit -> Open Mod Kit: makes a weapon-look or mech-destruction mod for OpenWarThink in three steps, without the
    /// game project.
    /// 1. Example prefabs in a project folder (ExamplePrefabs), to change in the Editor.
    /// 2. The mod's files in the game's Mods folder: mod.json and effects/weapons/&lt;weapon&gt;.json (or
    ///    effects/mechs/&lt;mechs&gt;.json) naming those prefabs.
    /// 3. The asset bundle of that project folder, written into the mod's effects/weapons folder (mech files use it from
    ///    there as "../weapons/&lt;bundle&gt;").
    /// The game reloads changed effect files while it runs; a new mod folder appears after RELOAD on the Mods screen.
    /// Command line (all three steps at once):
    /// -executeMethod OpenWarThink.ModKit.ModKitWindow.FromCommandLine --modkit-mods &lt;Mods folder&gt; [--modkit-id my-mod]
    /// [--modkit-weapon medium-laser] [--modkit-style beam|bolt|impact] [--modkit-mechs mech|light|medium|heavy|assault|unit]
    /// [--modkit-impact MyHit] [--modkit-scale 1] [--modkit-platforms windows,mac,linux]. With --modkit-mechs the mod gets
    /// a mech-destruction file instead of a weapon file, using --modkit-impact as the explosion.
    /// </summary>
    public sealed class ModKitWindow : EditorWindow
    {
        /// <summary>The names an effect file can have, from the most general: weapon families, then classes.</summary>
        public static readonly string[] Weapons =
        {
            "laser", "missile", "ballistic", "ppc", "flamer",
            "large-laser", "medium-laser", "small-laser", "large-pulse-laser", "medium-pulse-laser", "small-pulse-laser",
            "lrm", "srm", "streak-srm", "inferno-srm", "nuke", "torpedo", "arrow-iv", "narc",
            "autocannon", "ultra-autocannon", "lb-autocannon", "machine-gun", "ams", "gauss",
        };

        /// <summary>The names a mech-destruction file can have, apart from a model name (ATLAS.json), which is written by hand.</summary>
        public static readonly string[] Mechs = { "mech", "light", "medium", "heavy", "assault", "unit" };

        const string Prefs = "OpenWarThink.ModKit.";
        string _mods, _id, _name, _author, _description, _folder, _impact;
        int _weapon, _style, _target, _mechs;
        float _scale, _mechScale;
        Vector2 _scroll;
        string _status;

        [MenuItem("Mod Kit/Open Mod Kit")]
        static void Open() => GetWindow<ModKitWindow>("Mod Kit");

        void OnEnable()
        {
            _mods = EditorPrefs.GetString(Prefs + "Mods", "");
            _id = EditorPrefs.GetString(Prefs + "Id", "my-lasers");
            _name = EditorPrefs.GetString(Prefs + "Name", "My Lasers");
            _author = EditorPrefs.GetString(Prefs + "Author", "");
            _description = EditorPrefs.GetString(Prefs + "Description", "My own laser look.");
            _folder = EditorPrefs.GetString(Prefs + "Folder", "Assets/MyMod");
            _weapon = Math.Max(0, Array.IndexOf(Weapons, EditorPrefs.GetString(Prefs + "Weapon", "laser")));
            _style = EditorPrefs.GetInt(Prefs + "Style", 0);
            _impact = EditorPrefs.GetString(Prefs + "Impact", ExamplePrefabs.Hit);
            _scale = EditorPrefs.GetFloat(Prefs + "Scale", 1f);
            _target = EditorPrefs.GetInt(Prefs + "Target", 0);
            _mechs = Math.Max(0, Array.IndexOf(Mechs, EditorPrefs.GetString(Prefs + "Mechs", "mech")));
            _mechScale = EditorPrefs.GetFloat(Prefs + "MechScale", 3f);
        }

        void Remember()
        {
            EditorPrefs.SetString(Prefs + "Mods", _mods);
            EditorPrefs.SetString(Prefs + "Id", _id);
            EditorPrefs.SetString(Prefs + "Name", _name);
            EditorPrefs.SetString(Prefs + "Author", _author);
            EditorPrefs.SetString(Prefs + "Description", _description);
            EditorPrefs.SetString(Prefs + "Folder", _folder);
            EditorPrefs.SetString(Prefs + "Weapon", Weapons[_weapon]);
            EditorPrefs.SetInt(Prefs + "Style", _style);
            EditorPrefs.SetString(Prefs + "Impact", _impact);
            EditorPrefs.SetFloat(Prefs + "Scale", _scale);
            EditorPrefs.SetInt(Prefs + "Target", _target);
            EditorPrefs.SetString(Prefs + "Mechs", Mechs[_mechs]);
            EditorPrefs.SetFloat(Prefs + "MechScale", _mechScale);
        }

        void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            EditorGUILayout.HelpBox("Unity " + Application.unityVersion + ". Bundles must be built with the same Unity version as the game (6000.3.14f1).",
                                    Application.unityVersion == "6000.3.14f1" ? MessageType.Info : MessageType.Warning);

            EditorGUILayout.LabelField("The mod", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                _mods = EditorGUILayout.TextField(new GUIContent("Game's Mods folder", "The Mods folder next to MW2Mercs.exe (make it if there is none)."), _mods);
                if (GUILayout.Button("...", GUILayout.Width(28)))
                {
                    string picked = EditorUtility.OpenFolderPanel("The Mods folder next to MW2Mercs.exe", _mods, "");
                    if (!string.IsNullOrEmpty(picked)) _mods = picked;
                }
            }
            _id = EditorGUILayout.TextField(new GUIContent("Id (folder name)", "Letters, digits and dashes; the mod's folder in Mods."), _id);
            _name = EditorGUILayout.TextField("Name", _name);
            _author = EditorGUILayout.TextField("Author", _author);
            _description = EditorGUILayout.TextField("Description", _description);
            _target = EditorGUILayout.Popup(new GUIContent("Look for", "Weapons: shots, muzzle flashes and hits. Mech destruction: the explosions when a mech is destroyed."),
                                            _target, new[] { "Weapons", "Mech destruction" });
            bool mechs = _target == 1;
            if (!mechs)
            {
                _weapon = EditorGUILayout.Popup(new GUIContent("Weapons", "Which weapons get this look: a family (laser) or a class (medium-laser)."), _weapon, Weapons);
                _style = EditorGUILayout.Popup(new GUIContent("Shot", "Beam: a line from the muzzle to what it hits, at once (lasers). Bolt: something that flies with the game's shot. " +
                                                                      "Impact only: the shot stays as in the game, only the hit gets your prefab (an explosion)."),
                                               _style, new[] { "Beam (MyBeam)", "Bolt (MyBolt)", "Impact only" });
            }
            else
                _mechs = EditorGUILayout.Popup(new GUIContent("Mechs", "Which units get this look: every mech, a weight class (light below 40 t, medium below 60, heavy below 80, assault), " +
                                                                       "or every unit (vehicles and turrets too)."), _mechs, Mechs);
            _folder = EditorGUILayout.TextField(new GUIContent("Prefab folder", "The project folder whose prefabs go into the bundle; its name is the bundle's name."), _folder);
            var prefabs = PrefabsIn(_folder);
            string label = mechs ? "Explosion prefab" : "Impact prefab";
            if (prefabs.Length > 0)
            {
                int at = Math.Max(0, Array.IndexOf(prefabs, _impact));
                _impact = prefabs[EditorGUILayout.Popup(new GUIContent(label, mechs
                                                            ? "The prefab placed where the mech blows up, standing upright (+Y up). Drag your own into the prefab folder to see it here."
                                                            : "The prefab placed where the shot hits (+Z back towards the shooter). Drag your own into the prefab folder to see it here."),
                                                        at, prefabs)];
            }
            else EditorGUILayout.LabelField(label, "no prefabs in " + _folder + " yet (step 1, or drag your own there)");
            if (mechs)
                _mechScale = EditorGUILayout.Slider(new GUIContent("Explosion scale", "Size of the large explosion when the mech is destroyed; the smaller ones on the wreck are 0.4 of it, a lost arm or leg 0.5."),
                                                    _mechScale, 0.05f, 20f);
            else
                _scale = EditorGUILayout.Slider(new GUIContent("Impact scale", "Size of the impact prefab in the game; prefabs from packs are often much bigger than a hit should be."), _scale, 0.05f, 5f);
            if (Mod() != null) EditorGUILayout.LabelField("Mod folder", Mod(), EditorStyles.miniLabel);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Steps", EditorStyles.boldLabel);
            if (GUILayout.Button("1. Create example prefabs in " + _folder)) Run(() => { ExamplePrefabs.Create(_folder); return "Made MyBeam, MyBolt and MyHit in " + _folder + ". Open them and change what you like."; });
            using (new EditorGUI.DisabledScope(Mod() == null))
            {
                if (!mechs && GUILayout.Button("2. Write mod.json and effects/weapons/" + Weapons[_weapon] + ".json")) Run(() => string.Join("\n", WriteMod(Mod(), _id, _name, _author, _description, Weapons[_weapon], Bundle(_folder), (Style)_style, _impact, _scale, false)));
                if (mechs && GUILayout.Button("2. Write mod.json and effects/mechs/" + Mechs[_mechs] + ".json")) Run(() => string.Join("\n", WriteMechMod(Mod(), _id, _name, _author, _description, Mechs[_mechs], Bundle(_folder), _impact, _mechScale, false)));
                if (GUILayout.Button("3. Build the bundle into the mod")) Run(() => "Written:\n" + string.Join("\n", ModBundleBuilder.Build(_folder, Path.Combine(Mod(), "effects", "weapons"), Bundle(_folder), null))
                                                                                    + Missing(Mod(), Bundle(_folder), PrefabsIn(_folder)));
            }
            if (Mod() != null && GUILayout.Button("Show the mod folder")) EditorUtility.RevealInFinder(Path.Combine(Mod(), "mod.json"));

            EditorGUILayout.Space();
            EditorGUILayout.HelpBox("Then start the game: Options -> Mods (RELOAD if the mod is new, ACCEPT) -> TEST RANGE. " +
                                    "Ctrl+Shift+[ / ] picks another weapon, Ctrl+Shift+K destroys the nearest mech. After you change a prefab, press 3 again; after you change a JSON file " +
                                    "just save it - the game reloads effect files while it runs.", MessageType.None);
            if (!string.IsNullOrEmpty(_status)) EditorGUILayout.HelpBox(_status, MessageType.Info);
            EditorGUILayout.EndScrollView();
            if (GUI.changed) Remember();
        }

        /// <summary>What the effect file gives the weapon: a beam, a flying bolt, or only the impact.</summary>
        public enum Style { Beam, Bolt, Impact }

        static string[] PrefabsIn(string folder) => !AssetDatabase.IsValidFolder(folder) ? new string[0] :
            AssetDatabase.FindAssets("t:Prefab", new[] { folder }).Select(g => Path.GetFileNameWithoutExtension(AssetDatabase.GUIDToAssetPath(g)))
                .Distinct().OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray();

        /// <summary>
        /// The effect files of the mod that name a prefab of this bundle which the bundle does not have (a prefab that was
        /// renamed or deleted after step 2): the game would look for it in vain. Empty when all is well.
        /// </summary>
        public static string Missing(string mod, string bundle, string[] prefabs)
        {
            var lines = new List<string>();
            var named = new System.Text.RegularExpressions.Regex("\"bundle\"\\s*:\\s*\"([^\"]*)\"\\s*,\\s*\"asset\"\\s*:\\s*\"([^\"]*)\"");
            // Weapon files name the bundle next to them; mech files name it in effects/weapons.
            foreach (var (folder, name) in new[] { ("weapons", bundle), ("mechs", "../weapons/" + bundle) })
            {
                string dir = Path.Combine(mod, "effects", folder);
                if (!Directory.Exists(dir)) continue;
                foreach (string file in Directory.GetFiles(dir, "*.json", SearchOption.AllDirectories))
                    foreach (System.Text.RegularExpressions.Match m in named.Matches(File.ReadAllText(file)))
                        if (string.Equals(m.Groups[1].Value, name, StringComparison.OrdinalIgnoreCase) &&
                            !prefabs.Any(n => string.Equals(n, m.Groups[2].Value, StringComparison.OrdinalIgnoreCase)))
                            lines.Add($"{folder}/{Path.GetFileName(file)} names \"{m.Groups[2].Value}\", which is not in the bundle");
            }
            return lines.Count == 0 ? "" :
                "\n\nWARNING:\n" + string.Join("\n", lines.Distinct()) + "\nChoose the prefab above and press 2 to write it into the effect file.";
        }

        string Mod() => string.IsNullOrWhiteSpace(_mods) || string.IsNullOrWhiteSpace(_id) ? null : Path.Combine(_mods.Trim(), _id.Trim());

        static string Bundle(string folder) => Path.GetFileName(folder.TrimEnd('/')).ToLowerInvariant().Replace(' ', '-');

        void Run(Func<string> step)
        {
            try { _status = step(); }
            catch (Exception e)
            {
                Debug.LogException(e);
                _status = null;
                EditorUtility.DisplayDialog("Mod Kit", e.Message, "OK");
            }
        }

        /// <summary>
        /// Writes &lt;mod&gt;/mod.json (when there is none) and &lt;mod&gt;/effects/weapons/&lt;weapon&gt;.json (asking before it
        /// replaces one, unless <paramref name="replace"/>); returns what it did.
        /// </summary>
        public static List<string> WriteMod(string mod, string id, string name, string author, string description, string weapon,
                                            string bundle, Style style, string impact, float scale, bool replace)
        {
            var done = WriteInfo(mod, id, name, author, description);
            string weapons = Path.Combine(mod, "effects", "weapons");
            string look = Path.Combine(weapons, weapon + ".json");
            if (File.Exists(look) && !replace &&
                !EditorUtility.DisplayDialog("Mod Kit", look + " exists. Replace it?", "Replace", "Keep"))
            {
                done.Add("Kept " + look);
                return done;
            }
            if (string.IsNullOrWhiteSpace(impact)) impact = ExamplePrefabs.Hit;
            string hit = Prefab(bundle, impact, scale);
            File.WriteAllText(look, style == Style.Beam ? BeamLook(bundle, hit) : style == Style.Bolt ? BoltLook(bundle, hit) : ImpactLook(hit));
            done.Add("Written " + look);
            return done;
        }

        /// <summary>
        /// Writes &lt;mod&gt;/mod.json (when there is none) and &lt;mod&gt;/effects/mechs/&lt;mechs&gt;.json, whose explosions use
        /// <paramref name="explosion"/> from the bundle in effects/weapons (asking before it replaces a file, unless
        /// <paramref name="replace"/>); returns what it did.
        /// </summary>
        public static List<string> WriteMechMod(string mod, string id, string name, string author, string description, string mechs,
                                                string bundle, string explosion, float scale, bool replace)
        {
            var done = WriteInfo(mod, id, name, author, description);
            string dir = Path.Combine(mod, "effects", "mechs");
            Directory.CreateDirectory(dir);
            string look = Path.Combine(dir, mechs + ".json");
            if (File.Exists(look) && !replace &&
                !EditorUtility.DisplayDialog("Mod Kit", look + " exists. Replace it?", "Replace", "Keep"))
            {
                done.Add("Kept " + look);
                return done;
            }
            if (string.IsNullOrWhiteSpace(explosion)) explosion = ExamplePrefabs.Hit;
            string shared = "../weapons/" + bundle;
            File.WriteAllText(look,
                "{\n" +
                "  // Mech destruction. \"death\": the large explosion when the mech is destroyed; \"burst\": each smaller explosion\n" +
                "  // on the wreck over the next 10 seconds; \"section\": an arm, a leg or the torso blown off. hideOriginal hides the\n" +
                "  // game's own explosion (its sound, splash damage and debris stay). The prefab stands upright (+Y up) and goes\n" +
                "  // when its particles are gone. The bundle is the one step 3 builds into effects/weapons.\n" +
                $"  \"death\":   {{ \"hideOriginal\": true, \"prefab\": {Prefab(shared, explosion, scale)} }},\n" +
                $"  \"burst\":   {{ \"hideOriginal\": true, \"prefab\": {Prefab(shared, explosion, scale * 0.4f)} }},\n" +
                $"  \"section\": {{ \"prefab\": {Prefab(shared, explosion, scale * 0.5f)} }}\n" +
                "}\n");
            done.Add("Written " + look);
            return done;
        }

        static string Prefab(string bundle, string asset, float scale) =>
            $"{{ \"bundle\": {Quote(bundle)}, \"asset\": {Quote(asset)}" +
            (Math.Abs(scale - 1f) > 0.001f ? ", \"scale\": " + scale.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) : "") + " }";

        /// <summary>Checks the id and writes &lt;mod&gt;/mod.json when there is none; makes effects/weapons for the bundle.</summary>
        static List<string> WriteInfo(string mod, string id, string name, string author, string description)
        {
            if (string.IsNullOrWhiteSpace(id) || id.IndexOfAny(new[] { ';', '@', '|', '"', '/', '\\' }) >= 0 || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new ArgumentException("The id \"" + id + "\" cannot be a folder name (no ; @ | \" or slashes)");
            var done = new List<string>();
            string weapons = Path.Combine(mod, "effects", "weapons");
            Directory.CreateDirectory(weapons);
            string info = Path.Combine(mod, "mod.json");
            if (!File.Exists(info))
            {
                File.WriteAllText(info,
                    "{\n" +
                    $"  \"id\": {Quote(id)},\n" +
                    $"  \"name\": {Quote(name)},\n" +
                    "  \"version\": \"1.0\",\n" +
                    $"  \"author\": {Quote(author)},\n" +
                    $"  \"description\": {Quote(description)},\n" +
                    "  \"api\": 1\n" +
                    "}\n");
                done.Add("Written " + info);
            }
            else done.Add("Kept " + info + " (change it by hand)");
            return done;
        }

        static string Quote(string s) => "\"" + (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

        static string BeamLook(string bundle, string hit) =>
            "{\n" +
            "  // A beam: MyBeam stands at once where the shot's line meets something (reach \"instant\"), its lines run from\n" +
            "  // the muzzle to there (stretch), it shows at least 0.3 s (hold) and fades in 0.1 s (life). The game's own shot\n" +
            "  // still flies, unseen (hideOriginal); MyHit comes when the beam lets go.\n" +
            "  \"shot\":   { \"hideOriginal\": true,\n" +
            $"              \"prefab\": {{ \"bundle\": \"{bundle}\", \"asset\": \"{ExamplePrefabs.Beam}\", \"reach\": \"instant\", \"stretch\": true, \"hold\": 0.3, \"life\": 0.1, \"flicker\": 0.3 }} }},\n" +
            "  \"muzzle\": { \"hideOriginal\": true },\n" +
            $"  \"impact\": {{ \"hideOriginal\": true, \"prefab\": {hit} }}\n" +
            "}\n";

        static string BoltLook(string bundle, string hit) =>
            "{\n" +
            "  // A bolt: MyBolt rides on the game's shot (hidden by hideOriginal) and stays where it ended; its particles stop\n" +
            "  // and its trail fades. MyHit is placed where the shot hits.\n" +
            "  \"shot\":   { \"hideOriginal\": true,\n" +
            $"              \"prefab\": {{ \"bundle\": \"{bundle}\", \"asset\": \"{ExamplePrefabs.Bolt}\" }} }},\n" +
            $"  \"impact\": {{ \"hideOriginal\": true, \"prefab\": {hit} }}\n" +
            "}\n";

        static string ImpactLook(string hit) =>
            "{\n" +
            "  // Only the hit changes: the shot flies as in the game; where it hits, the original explosion is hidden\n" +
            "  // (hideOriginal; its sound, splash damage and debris stay) and the prefab is placed, +Z back towards the\n" +
            "  // shooter. \"scale\" sizes it; it goes when its particles are gone.\n" +
            $"  \"impact\": {{ \"hideOriginal\": true, \"prefab\": {hit} }}\n" +
            "}\n";

        /// <summary>All three steps from the command line (see the class comment); the effect file is replaced.</summary>
        public static void FromCommandLine()
        {
            string mods = Arg("--modkit-mods") ?? throw new ArgumentException("--modkit-mods <the game's Mods folder> is missing");
            string id = Arg("--modkit-id") ?? "my-lasers";
            string weapon = Arg("--modkit-weapon") ?? "laser";
            if (Array.IndexOf(Weapons, weapon) < 0) throw new ArgumentException("--modkit-weapon: one of " + string.Join(", ", Weapons));
            string mechs = Arg("--modkit-mechs");
            if (mechs != null && Array.IndexOf(Mechs, mechs) < 0) throw new ArgumentException("--modkit-mechs: one of " + string.Join(", ", Mechs));
            string styleArg = Arg("--modkit-style") ?? "beam";
            var style = styleArg == "bolt" ? Style.Bolt : styleArg == "impact" ? Style.Impact : Style.Beam;
            string impact = Arg("--modkit-impact") ?? ExamplePrefabs.Hit;
            float scale = float.Parse(Arg("--modkit-scale") ?? "1", System.Globalization.CultureInfo.InvariantCulture);
            var platforms = Arg("--modkit-platforms")?.Split(',').Select(p => "." + p.Trim().ToLowerInvariant()).ToArray();
            const string folder = "Assets/MyMod";
            ExamplePrefabs.Create(folder);
            string mod = Path.Combine(Path.GetFullPath(mods), id);
            var done = mechs != null
                ? WriteMechMod(mod, id, "My Explosions", "", "My own mech explosions.", mechs, Bundle(folder), impact, scale, true)
                : WriteMod(mod, id, "My Lasers", "", "My own laser look.", weapon, Bundle(folder), style, impact, scale, true);
            done.AddRange(ModBundleBuilder.Build(folder, Path.Combine(mod, "effects", "weapons"), Bundle(folder), platforms));
            string missing = Missing(mod, Bundle(folder), PrefabsIn(folder));
            if (missing.Length > 0) Debug.LogWarning("MODKIT_MISSING " + missing.Trim().Replace("\n", " | "));
            Debug.Log("MODKIT_DONE " + string.Join(" | ", done));
        }

        static string Arg(string name)
        {
            var args = Environment.GetCommandLineArgs();
            int at = Array.IndexOf(args, name);
            return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
        }
    }
}
