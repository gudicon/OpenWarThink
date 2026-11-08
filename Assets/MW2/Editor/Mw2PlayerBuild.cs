using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;

namespace MW2.EditorTools
{
    /// <summary>Unity release adapter; no original game algorithm. ShellMain 0x100428d3 is the entry scene.</summary>
    public static class Mw2PlayerBuild
    {
        // -executeMethod MW2.EditorTools.Mw2PlayerBuild.Windows --mw2-build-output <new directory>
        public static void Windows()
        {
            var args = Environment.GetCommandLineArgs();
            int at = Array.IndexOf(args, "--mw2-build-output");
            if (at < 0 || at + 1 >= args.Length) throw new ArgumentException("--mw2-build-output is required");
            string output = Path.GetFullPath(args[at + 1]);
            if (Directory.Exists(output) || File.Exists(output)) throw new IOException("Build output must be new: " + output);
            const string scene = "Assets/mercenaries.unity";
            if (!File.Exists(scene)) throw new FileNotFoundException("Shell entry scene is missing", scene);
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone, ScriptingImplementation.Mono2x);
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { scene },
                locationPathName = Path.Combine(output, "MW2Mercs.exe"),
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.StrictMode
            });
            if (report.summary.result != BuildResult.Succeeded)
                throw new Exception("Windows Player build failed: " + report.summary.result);
            if (!File.Exists(Path.Combine(output, "MW2Mercs_Data", "Managed", "MW2.Runtime.dll")))
                throw new FileNotFoundException("Mono runtime assembly is missing from the built Player");
            UnityEngine.Debug.Log("MW2_PLAYER_BUILD_SUCCEEDED " + output);
        }
    }
}
