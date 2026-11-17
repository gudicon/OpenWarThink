using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace MW2.EditorTools
{
    /// <summary>
    /// Builds a Unity asset bundle for a mod's weapon looks (WeaponLook "prefab"): every prefab in a project folder goes
    /// into one bundle, built for each platform the Editor can build (Windows, macOS, Linux) and written next to each other
    /// as &lt;name&gt;.windows, &lt;name&gt;.mac, &lt;name&gt;.linux. The game loads the one for its platform; a prefab
    /// is found by its file name without ".prefab". Bundles carry no code: a prefab keeps only what draws or sounds.
    /// Port addition (mod tooling) with no counterpart in the original program.
    /// Menu: select the folder in the Project window, then MW2 -> Mods -> Build Asset Bundle.
    /// Command line: -executeMethod MW2.EditorTools.Mw2ModBundleBuilder.FromCommandLine --mw2-bundle-source Assets/MyMod
    /// --mw2-bundle-output &lt;folder&gt; [--mw2-bundle-name name] [--mw2-bundle-platforms windows,mac,linux].
    /// </summary>
    public static class Mw2ModBundleBuilder
    {
        static readonly (string Ending, BuildTarget Target)[] Platforms =
        {
            (".windows", BuildTarget.StandaloneWindows64),
            (".mac", BuildTarget.StandaloneOSX),
            (".linux", BuildTarget.StandaloneLinux64),
        };

        [MenuItem("MW2/Mods/Build Asset Bundle")]
        static void FromMenu()
        {
            string source = SelectedFolder();
            if (source == null)
            {
                EditorUtility.DisplayDialog("Build Asset Bundle", "Select the folder with the mod's prefabs in the Project window first.", "OK");
                return;
            }
            string name = Path.GetFileName(source).ToLowerInvariant().Replace(' ', '-');
            string output = EditorUtility.SaveFolderPanel("Where to write " + name + ".windows / .mac / .linux " +
                                                          "(the mod's effects/weapons folder)", "", "");
            if (string.IsNullOrEmpty(output)) return;
            try
            {
                var written = Build(source, output, name, null);
                EditorUtility.DisplayDialog("Build Asset Bundle", "Written:\n" + string.Join("\n", written), "OK");
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                EditorUtility.DisplayDialog("Build Asset Bundle", e.Message, "OK");
            }
        }

        [MenuItem("MW2/Mods/Build Asset Bundle", true)]
        static bool CanBuild() => SelectedFolder() != null;

        static string SelectedFolder()
        {
            foreach (var o in Selection.GetFiltered<UnityEngine.Object>(SelectionMode.Assets))
            {
                string path = AssetDatabase.GetAssetPath(o);
                if (AssetDatabase.IsValidFolder(path)) return path;
            }
            return null;
        }

        public static void FromCommandLine()
        {
            string source = Arg("--mw2-bundle-source") ?? throw new ArgumentException("--mw2-bundle-source <Assets/...> is missing");
            string output = Arg("--mw2-bundle-output") ?? throw new ArgumentException("--mw2-bundle-output <folder> is missing");
            string name = Arg("--mw2-bundle-name") ?? Path.GetFileName(source).ToLowerInvariant();
            var platforms = Arg("--mw2-bundle-platforms")?.Split(',').Select(p => "." + p.Trim().ToLowerInvariant()).ToArray();
            var written = Build(source, Path.GetFullPath(output), name, platforms);
            Debug.Log("MW2_MOD_BUNDLE_BUILT " + string.Join(" ", written));
        }

        static string Arg(string name)
        {
            var args = Environment.GetCommandLineArgs();
            int at = Array.IndexOf(args, name);
            return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
        }

        /// <summary>Builds the bundle of the prefabs in <paramref name="source"/>; returns the files written.</summary>
        public static List<string> Build(string source, string output, string name, string[] endings)
        {
            if (!AssetDatabase.IsValidFolder(source)) throw new ArgumentException(source + " is not a folder of the project");
            if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new ArgumentException("\"" + name + "\" cannot be a file name");
            // Unity builds nothing while a script of the project does not compile (a pack's demo script that needs a
            // package the project lacks, for one); say so plainly instead of "could not build".
            if (EditorUtility.scriptCompilationFailed)
                throw new InvalidOperationException("A script in this project does not compile, and Unity builds nothing until it does. " +
                    "The Console (red lines) names the file. Often it is a pack's demo script: delete it, or install the package it " +
                    "needs (Window -> Package Manager -> Unity Registry).");
            // Prefabs made or changed a moment ago: save and import them first, or a bundle may be built from a state
            // without their materials.
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            var prefabs = AssetDatabase.FindAssets("t:Prefab", new[] { source }).Select(AssetDatabase.GUIDToAssetPath).Distinct().OrderBy(p => p).ToArray();
            if (prefabs.Length == 0) throw new InvalidOperationException("No prefabs in " + source);
            var same = prefabs.GroupBy(p => Path.GetFileNameWithoutExtension(p), StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).ToList();
            if (same.Count > 0)
                throw new InvalidOperationException("Prefabs are found by name, and these names are used twice: " + string.Join(", ", same.Select(g => g.Key)));
            Directory.CreateDirectory(output);
            string bundleName = name.ToLowerInvariant();
            // Their materials, textures and meshes are named too, so the bundle always carries them.
            var parts = AssetDatabase.GetDependencies(prefabs, true)
                .Where(p => p.StartsWith("Assets/", StringComparison.Ordinal) && !p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                            && !p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && Array.IndexOf(prefabs, p) < 0)
                .Distinct().OrderBy(p => p).ToArray();
            var build = new AssetBundleBuild { assetBundleName = bundleName, assetNames = prefabs.Concat(parts).ToArray() };
            var written = new List<string>();
            foreach (var (ending, target) in Platforms)
            {
                if (endings != null && Array.IndexOf(endings, ending) < 0) continue;
                if (!BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Standalone, target))
                {
                    Debug.LogWarning($"MW2 mod bundle: no {target} support in this Editor, {name}{ending} is not built");
                    continue;
                }
                string temp = Path.Combine(Path.GetTempPath(), "mw2-mod-bundle-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(temp);
                try
                {
                    var manifest = BuildPipeline.BuildAssetBundles(temp, new[] { build },
                        BuildAssetBundleOptions.ChunkBasedCompression | BuildAssetBundleOptions.StrictMode, target);
                    if (manifest == null) throw new InvalidOperationException($"Unity could not build the bundle for {target} (see the Console)");
                    string file = Path.Combine(output, name + ending);
                    File.Copy(Path.Combine(temp, bundleName), file, true);
                    if (target == EditorTarget()) Verify(file, prefabs);
                    written.Add(file);
                }
                finally { try { Directory.Delete(temp, true); } catch (IOException) { } }
            }
            if (written.Count == 0) throw new InvalidOperationException("No platform could be built");
            Debug.Log($"MW2 mod bundle {name}: {prefabs.Length} prefab(s): " + string.Join(", ", prefabs.Select(Path.GetFileNameWithoutExtension)));
            return written;
        }

        static BuildTarget EditorTarget() =>
            Application.platform == RuntimePlatform.WindowsEditor ? BuildTarget.StandaloneWindows64 :
            Application.platform == RuntimePlatform.OSXEditor ? BuildTarget.StandaloneOSX : BuildTarget.StandaloneLinux64;

        /// <summary>
        /// Loads the bundle built for this computer's platform and checks that every renderer of every prefab still has
        /// its materials with a shader that works here; throws with the list of what is missing.
        /// </summary>
        static void Verify(string file, string[] prefabs)
        {
            var bundle = AssetBundle.LoadFromFile(file);
            if (bundle == null) throw new InvalidOperationException("The bundle just built cannot be loaded: " + file);
            var missing = new List<string>();
            try
            {
                foreach (string path in prefabs)
                {
                    string name = Path.GetFileNameWithoutExtension(path);
                    var go = bundle.LoadAsset<GameObject>(name);
                    if (go == null) { missing.Add(name + ": not in the bundle"); continue; }
                    foreach (var r in go.GetComponentsInChildren<Renderer>(true))
                        foreach (var m in r.sharedMaterials)
                        {
                            if (m == null) missing.Add($"{name}/{r.name}: no material");
                            else if (m.shader == null || !m.shader.isSupported) missing.Add($"{name}/{r.name}: material {m.name} has no working shader");
                        }
                }
            }
            finally { bundle.Unload(true); }
            if (missing.Count > 0)
                throw new InvalidOperationException("The bundle came out without parts of the prefabs (they would draw pink in the game):\n" +
                    string.Join("\n", missing.Take(12)) + "\nBuild again; if it repeats, give every renderer a material (and a texture where the shader wants one).");
            Debug.Log($"MW2 mod bundle {Path.GetFileName(file)}: checked, every renderer has its material");
        }
    }
}
