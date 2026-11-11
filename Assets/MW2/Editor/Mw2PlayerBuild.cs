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
        const string Scene = "Assets/mercenaries.unity";

        // -executeMethod MW2.EditorTools.Mw2PlayerBuild.Windows --mw2-build-output <new directory>
        public static void Windows()
        {
            string output = Output();
            Build(BuildTarget.StandaloneWindows64, Path.Combine(output, "MW2Mercs.exe"), "Windows");
            if (!File.Exists(Path.Combine(output, "MW2Mercs_Data", "Managed", "MW2.Runtime.dll")))
                throw new FileNotFoundException("Mono runtime assembly is missing from the built Player");
            UnityEngine.Debug.Log("MW2_PLAYER_BUILD_SUCCEEDED " + output);
        }

        // -executeMethod MW2.EditorTools.Mw2PlayerBuild.Mac --mw2-build-output <new directory>
        // A universal (Intel + Apple silicon) Mono app. Built on Windows it is not code-signed: on a Mac run
        // `codesign --force --deep -s - MW2Mercs.app` once (Apple silicon refuses unsigned code).
        public static void Mac()
        {
            string output = Output();
            SetMacArchitecture();
            string app = Path.Combine(output, "MW2Mercs.app");
            Build(BuildTarget.StandaloneOSX, app, "macOS");
            if (!File.Exists(Path.Combine(app, "Contents", "Resources", "Data", "Managed", "MW2.Runtime.dll")))
                throw new FileNotFoundException("Mono runtime assembly is missing from the built app");
            UnityEngine.Debug.Log("MW2_PLAYER_BUILD_SUCCEEDED " + output);
        }

        static string Output()
        {
            var args = Environment.GetCommandLineArgs();
            int at = Array.IndexOf(args, "--mw2-build-output");
            if (at < 0 || at + 1 >= args.Length) throw new ArgumentException("--mw2-build-output is required");
            string output = Path.GetFullPath(args[at + 1]);
            if (Directory.Exists(output) || File.Exists(output)) throw new IOException("Build output must be new: " + output);
            if (!File.Exists(Scene)) throw new FileNotFoundException("Shell entry scene is missing", Scene);
            return output;
        }

        static void Build(BuildTarget target, string location, string name)
        {
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone, ScriptingImplementation.Mono2x);
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { Scene },
                locationPathName = location,
                target = target,
                options = BuildOptions.StrictMode
            });
            if (report.summary.result != BuildResult.Succeeded)
                throw new Exception(name + " Player build failed: " + report.summary.result);
        }

        // UserBuildSettings lives in the Mac build module, which not every editor has installed; set it by reflection
        // so the project still compiles without the module.
        static void SetMacArchitecture()
        {
            var settings = Type.GetType("UnityEditor.OSXStandalone.UserBuildSettings, UnityEditor.OSXStandalone.Extensions");
            var property = settings?.GetProperty("architecture");
            if (property == null) throw new InvalidOperationException("Mac Build Support (Mono) is not installed for this editor");
            property.SetValue(null, Enum.Parse(property.PropertyType, "x64ARM64"));
        }
    }
}
