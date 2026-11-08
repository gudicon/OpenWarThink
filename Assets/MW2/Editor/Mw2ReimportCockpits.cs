using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Security.Cryptography;
using MW2.Formats;
using UnityEditor;
using UnityEngine;

namespace MW2.EditorTools
{
    /// <summary>Reimports native BWD/REPR5 cockpit meshes, EYEO and POLY materials
    /// from the installed archive (Lod_Register 0x1001e729, cockpit pass 0x1001bea2).
    /// Editor asset workflow; no substitute cockpit geometry or preview paint.</summary>
    public static class Mw2ReimportCockpits
    {
        static string Game => UnityEditor.EditorPrefs.GetString("MW2.GamePath", Mw2GameFolder.Default);
        const string Folder = "Assets/MW2Data/Mechs";
        [Serializable] class Model { public string name, bwdSha256; public int eye, shellRenderers, meshes; public string prefab; }
        [Serializable] class Report { public string archive, archiveSha256; public Model[] models; }
        static string Hash(byte[] bytes) { using var sha = SHA256.Create(); return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", ""); }

        [MenuItem("MW2/Reimport original cockpits")]
        public static void Run()
        {
            string archive = Path.Combine(Game, "MW2.PRJ");
            string digest = Hash(File.ReadAllBytes(archive));
            if (digest != "1F4410C0B80F5AB62BFF64F1919B44E9123D8822365C4E77FD0D10A7FA178985") throw new Exception("Original archive version differs");
            using var prj = new PrjArchive(archive);
            var models = prj.Entries("BWD").Where(e =>
            {
                byte[] data = prj.Read(e);
                if (BwdSkeleton.DecodeEye(data) < 0) return false;
                var reps = BwdSkeleton.Decode(data);
                return reps.Count > 5 && BwdSkeleton.ShellNodes(reps[0].Concat(reps[5]).ToList()).Count > 0;
            }).OrderBy(e => e.Name).ToArray();
            if (models.Length != 65) throw new Exception("Native cockpit inventory differs: " + models.Length);
            // The user requested replacement; preserve generated output before deleting it.
            string backup = Path.GetFullPath("scratchpad/cockpits-before-reimport-2026-10-29/Mechs");
            if (Directory.Exists(Folder) && !Directory.Exists(backup)) throw new Exception("Cockpit backup is missing");
            AssetDatabase.DeleteAsset(Folder);
            Mw2ImportWindow.EnsureFolder(Folder);
            var mission = Mission.Load(prj, "BET3");
            var palette = Palette.Decode(prj.Read(prj.Entries("PAL").First(e => e.Id == mission.StartPaletteId)));
            var flat = Mw2ImportWindow.PaletteMaterial(palette, Folder + "/OriginalPalette.mat");
            var sink = new Mw2EditorAssetSink();
            var imported = new List<Model>();
            foreach (var entry in models)
            {
                string dir = Folder + "/" + entry.Name + "_LOD1";
                // BuildMech creates this folder before texture requests are evaluated.
                var textures = new Mw2TextureBank(prj, mission, palette, dir, sink);
                var root = Mw2MissionBuilder.BuildMech(prj, palette, entry.Name, 1, null, flat, textures, playable: false, sink: sink);
                try
                {
                    var cockpit = root.GetComponent<Mw2Cockpit>();
                    var lods = root.GetComponent<Mw2LodGroup>();
                    if (cockpit == null || cockpit.Eye == null || lods == null) throw new Exception(entry.Name + ": native eye/LOD missing");
                    lods.Show(5);
                    int shell = lods.Levels.First(l => l.Lod == 5).Renderers.Count(r => lods.Shell.Contains(r));
                    if (shell == 0) throw new Exception(entry.Name + ": native LOD5 shell is empty");
                    foreach (var filter in root.GetComponentsInChildren<MeshFilter>(true))
                        if (filter.sharedMesh != null && !AssetDatabase.Contains(filter.sharedMesh)) throw new Exception(entry.Name + ": unsaved native mesh");
                    string prefab = dir + "/OriginalCockpit.prefab";
                    PrefabUtility.SaveAsPrefabAsset(root, prefab);
                    imported.Add(new Model { name = entry.Name, bwdSha256 = Hash(prj.Read(entry)), eye = BwdSkeleton.DecodeEye(prj.Read(entry)),
                        shellRenderers = shell, meshes = root.GetComponentsInChildren<MeshFilter>(true).Length, prefab = prefab });
                    Debug.Log("MW2 NATIVE COCKPIT: " + entry.Name + " shell=" + shell + " " + prefab);
                }
                finally { UnityEngine.Object.DestroyImmediate(root); }
            }
            AssetDatabase.SaveAssets();
            Directory.CreateDirectory("scratchpad/native-cockpit-reimport");
            File.WriteAllText("scratchpad/native-cockpit-reimport/report.json", JsonUtility.ToJson(new Report { archive = archive, archiveSha256 = digest, models = imported.ToArray() }, true));
            Debug.Log("MW2 NATIVE COCKPITS REIMPORTED: " + imported.Count);
        }

        public static void VerifyAndRender()
        {
            if (!Application.isBatchMode) throw new Exception("Run verification in batchmode; it uses a temporary empty scene");
            using var prj = new PrjArchive(Path.Combine(Game, "MW2.PRJ"));
            var report = JsonUtility.FromJson<Report>(File.ReadAllText("scratchpad/native-cockpit-reimport/report.json"));
            if (Hash(File.ReadAllBytes(report.archive)) != report.archiveSha256) throw new Exception("Original archive changed after import");
            var polys = prj.Entries("POLY").ToDictionary(e => e.Id);
            int verified = 0;
            foreach (var model in report.models)
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(model.prefab);
                if (prefab == null) throw new Exception("Missing imported cockpit " + model.name);
                var lods = prefab.GetComponent<Mw2LodGroup>();
                var cockpit = prefab.GetComponent<Mw2Cockpit>();
                var data = prj.Read("BWD", model.name);
                var reps = BwdSkeleton.Decode(data);
                var shell = BwdSkeleton.ShellNodes(reps[5]);
                int expected = reps[5].Where(n => n.HasGeometry && shell.Contains(n.Node)).Sum(n => PolyModel.Decode(prj.Read(polys[n.PolyId])).Count);
                int actual = lods.Levels.First(l => l.Lod == 5).Renderers.Count(r => lods.Shell.Contains(r));
                if (Hash(data) != model.bwdSha256 || expected != actual || cockpit.Eye == null)
                    throw new Exception(model.name + ": original shell/eye differs, expected=" + expected + " actual=" + actual);
                foreach (var filter in prefab.GetComponentsInChildren<MeshFilter>(true))
                    if (filter.sharedMesh == null || !AssetDatabase.Contains(filter.sharedMesh)) throw new Exception(model.name + ": missing imported mesh");
                verified++;
            }
            if (verified != 65) throw new Exception("Incomplete native cockpit verification");
            Debug.Log("MW2 NATIVE COCKPITS VERIFIED: " + verified + "; original BWD hashes, LOD5 POLY counts, meshes and EYEO");
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) return;
            var scene = UnityEditor.SceneManagement.EditorSceneManager.NewScene(UnityEditor.SceneManagement.NewSceneSetup.EmptyScene,
                UnityEditor.SceneManagement.NewSceneMode.Single);
            foreach (string name in new[] { "ATLAS", "COMMANDO", "JENNER", "TIMBRWLF" })
            {
                var item = report.models.FirstOrDefault(m => m.name == name);
                if (item == null) continue;
                var root = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(item.prefab));
                var camObject = new GameObject("Native cockpit verification camera");
                var camera = camObject.AddComponent<Camera>();
                var eye = root.GetComponent<Mw2Cockpit>().Eye;
                camera.transform.SetPositionAndRotation(eye.position, Quaternion.identity);
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(0.14f, 0.2f, 0.25f);
                camera.nearClipPlane = 0.3f; camera.farClipPlane = 200;
                camera.fieldOfView = Camera.HorizontalToVerticalFieldOfView(90, 4f / 3f);
                var texture = new RenderTexture(640, 480, 24);
                camera.targetTexture = texture; camera.Render();
                var previous = RenderTexture.active; RenderTexture.active = texture;
                var pixels = new Texture2D(640, 480, TextureFormat.RGB24, false);
                pixels.ReadPixels(new Rect(0, 0, 640, 480), 0, 0); pixels.Apply();
                File.WriteAllBytes("scratchpad/native-cockpit-reimport/" + name + ".png", pixels.EncodeToPNG());
                RenderTexture.active = previous; camera.targetTexture = null;
                UnityEngine.Object.DestroyImmediate(pixels); UnityEngine.Object.DestroyImmediate(texture);
                UnityEngine.Object.DestroyImmediate(camObject); UnityEngine.Object.DestroyImmediate(root);
            }
        }
    }
}
