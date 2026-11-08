using System.IO;
using System.Linq;
using MW2.Formats;
using UnityEditor;
using UnityEngine;

namespace MW2.EditorTools
{
    /// <summary>Imports palettes, CEL textures and POLY models from MW2.PRJ into Assets/MW2Data.</summary>
    public class Mw2ImportWindow : EditorWindow
    {
        const string GamePathKey = "MW2.GamePath";
        const string OutRoot = "Assets/MW2Data";

        string _gamePath;
        string _palette = "BROW0NDA";
        string _polyFilter = "AT1";
        string _celFilter = "";
        string _mech = "ATLAS";
        int _lod = 1;
        string _mek = "ATL00STD";
        string _mission = "BET1";

        [MenuItem("MW2/Import Window")]
        static void Open() => GetWindow<Mw2ImportWindow>("MW2 Import");

        void OnEnable() => _gamePath = EditorPrefs.GetString(GamePathKey, Mw2GameFolder.Default);

        string PrjPath => Path.Combine(_gamePath, "MW2.PRJ");

        void OnGUI()
        {
            EditorGUI.BeginChangeCheck();
            _gamePath = EditorGUILayout.TextField("Game folder", _gamePath);
            if (EditorGUI.EndChangeCheck()) EditorPrefs.SetString(GamePathKey, _gamePath);
            if (!File.Exists(PrjPath))
            {
                EditorGUILayout.HelpBox($"MW2.PRJ not found in {_gamePath}", MessageType.Error);
                return;
            }

            _palette = EditorGUILayout.TextField("World palette (PAL)", _palette);
            if (GUILayout.Button("Import all palettes")) ImportPalettes();

            EditorGUILayout.Space();
            _celFilter = EditorGUILayout.TextField("CEL name prefix (empty = all)", _celFilter);
            if (GUILayout.Button("Import CEL textures")) ImportCels();

            EditorGUILayout.Space();
            _polyFilter = EditorGUILayout.TextField("POLY name prefix (empty = all)", _polyFilter);
            if (GUILayout.Button("Import POLY models")) ImportPolys(spawn: false);
            if (GUILayout.Button("Import POLY models and lay them out in the scene")) ImportPolys(spawn: true);

            EditorGUILayout.Space();
            _mech = EditorGUILayout.TextField("Mech (BWD name)", _mech);
            _lod = EditorGUILayout.IntSlider("Level of detail", _lod, 1, 5);
            _mek = EditorGUILayout.TextField("Loadout (MEK name)", _mek);
            if (GUILayout.Button("Preview mech in the scene")) AssembleMech();
            EditorGUILayout.HelpBox("Preview only. To pilot this mech with its cockpit, weapons and MASC, use Load mission below.", MessageType.Info);

            EditorGUILayout.Space();
            _mission = EditorGUILayout.TextField("Mission (prefix, e.g. BET1)", _mission);
            if (GUILayout.Button("Load mission and pilot this mech"))
            {
                using var prj = new PrjArchive(PrjPath);
                Mw2MissionBuilder.Build(prj, _gamePath, _mission, _mech, _lod, _mek, new Mw2EditorAssetSink());
            }
        }

        internal static Material GetMaterial()
        {
            EnsureFolder(OutRoot);
            var material = AssetDatabase.LoadAssetAtPath<Material>(OutRoot + "/MW2VertexColor.mat");
            if (material != null) return material;
            material = new Material(Shader.Find("MW2/VertexColorLit"));
            AssetDatabase.CreateAsset(material, OutRoot + "/MW2VertexColor.mat");
            return material;
        }

        /// <summary>
        /// Flat-colour material with the palette stored inside it, so material types 1 and 4 can pick their
        /// lit palette row (mercsw.dll 0x1001afb0).
        /// </summary>
        internal static Material PaletteMaterial(Color32[] palette, string path)
        {
            AssetDatabase.DeleteAsset(path);
            var material = new Material(Shader.Find("MW2/VertexColorLit"))
                { name = System.IO.Path.GetFileNameWithoutExtension(path) };
            material.SetFloat("_UsePalette", 1);
            AssetDatabase.CreateAsset(material, path);
            // The texture must become a sub-asset before the material references it, or the reference is saved empty.
            var texture = Palette.ToTexture(palette, material.name + "_Palette");
            AssetDatabase.AddObjectToAsset(texture, material);
            material.SetTexture("_Palette", texture);
            EditorUtility.SetDirty(material);
            AssetDatabase.SaveAssets();
            return material;
        }

        /// <summary>Places the main camera in front of the mech (mechs face +Z) so the whole body fits the Game view.</summary>
        static void FrameMainCamera(Bounds bounds)
        {
            var cam = Camera.main;
            if (cam == null || bounds.size == Vector3.zero) return;
            Undo.RecordObject(cam.transform, "Frame mech");
            float radius = bounds.extents.magnitude;
            float halfFov = cam.fieldOfView * 0.5f * Mathf.Deg2Rad;
            float distance = radius / Mathf.Sin(halfFov) * 1.1f;
            var dir = new Vector3(0.35f, 0.2f, 1f).normalized;   // slightly from the side and above
            cam.transform.position = bounds.center + dir * distance;
            cam.transform.LookAt(bounds.center);
            cam.farClipPlane = Mathf.Max(cam.farClipPlane, distance * 4f);
        }

        void AssembleMech()
        {
            using var prj = new PrjArchive(PrjPath);
            var mission = Mission.Load(prj, _mission);
            var palEntry = mission.PaletteIds != null && mission.PaletteIds.Length > 0
                ? prj.Entries("PAL").FirstOrDefault(e => e.Id == mission.StartPaletteId) : default;
            var palette = palEntry.Id > 0 ? Palette.Decode(prj.Read(palEntry)) : LoadPalette(prj);
            var textureDir = $"{OutRoot}/Mechs/{_mech}_LOD{_lod}";
            var sink = new Mw2EditorAssetSink();
            var textures = new Mw2TextureBank(prj, mission, palette, textureDir, sink);
            EnsureFolder($"{OutRoot}/Mechs");
            var flat = PaletteMaterial(palette, $"{OutRoot}/Mechs/{_mech}_Flat.mat");
            var root = Mw2MissionBuilder.BuildMech(prj, palette, _mech, _lod, _mek, flat, textures, playable: false, sink: sink);
            var bounds = LogMech(root);
            Undo.RegisterCreatedObjectUndo(root, "Assemble mech");
            FrameMainCamera(bounds);
            Selection.activeGameObject = root;
            SceneView.lastActiveSceneView?.FrameSelected();
        }

        static Bounds LogMech(GameObject root)
        {
            var filters = root.GetComponentsInChildren<MeshFilter>();
            var bounds = new Bounds(root.transform.position, Vector3.zero);
            int withMesh = 0;
            foreach (var f in filters)
            {
                if (f.sharedMesh == null || f.sharedMesh.vertexCount == 0 || !f.GetComponent<Renderer>().enabled) continue;
                withMesh++;
                bounds.Encapsulate(f.GetComponent<Renderer>().bounds);
            }
            Debug.Log($"MW2: assembled {root.name}: nodes={root.GetComponentsInChildren<Transform>().Length - 1}, " +
                      $"parts with mesh={withMesh}/{filters.Length}, bounds center={bounds.center} size={bounds.size}, " +
                      $"animator={(root.GetComponent<Mw2MechAnimator>() != null)}");
            return bounds;
        }

        internal static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            EnsureFolder(Path.GetDirectoryName(path).Replace('\\', '/'));
            AssetDatabase.CreateFolder(Path.GetDirectoryName(path).Replace('\\', '/'), Path.GetFileName(path));
        }

        Color32[] LoadPalette(PrjArchive prj) => Palette.Decode(prj.Read("PAL", _palette));

        void ImportPalettes()
        {
            using var prj = new PrjArchive(PrjPath);
            string dir = OutRoot + "/Palettes";
            EnsureFolder(dir);
            foreach (var e in prj.Entries("PAL"))
            {
                var tex = Palette.ToTexture(Palette.Decode(prj.Read(e)), e.Name);
                AssetDatabase.CreateAsset(tex, $"{dir}/{e.Name}.asset");
            }
            AssetDatabase.SaveAssets();
        }

        void ImportCels()
        {
            using var prj = new PrjArchive(PrjPath);
            var palette = LoadPalette(prj);
            string dir = OutRoot + "/Cel";
            EnsureFolder(dir);
            var entries = prj.Entries("CEL").Where(e => e.Name.StartsWith(_celFilter, System.StringComparison.OrdinalIgnoreCase)).ToList();
            try
            {
                for (int i = 0; i < entries.Count; i++)
                {
                    var e = entries[i];
                    if (EditorUtility.DisplayCancelableProgressBar("CEL", e.Name, (float)i / entries.Count)) break;
                    var tex = Cel.Decode(prj.Read(e)).ToTexture(palette, e.Name);
                    File.WriteAllBytes($"{dir}/{e.Name}_{e.Id}.png", tex.EncodeToPNG());
                    DestroyImmediate(tex);
                }
            }
            finally { EditorUtility.ClearProgressBar(); }
            AssetDatabase.Refresh();
        }

        void ImportPolys(bool spawn)
        {
            using var prj = new PrjArchive(PrjPath);
            var palette = LoadPalette(prj);
            string dir = OutRoot + "/Poly";
            EnsureFolder(dir);
            var material = GetMaterial();

            var entries = prj.Entries("POLY").Where(e => e.Name.StartsWith(_polyFilter, System.StringComparison.OrdinalIgnoreCase)).ToList();
            GameObject root = spawn ? new GameObject($"POLY {_polyFilter}*") : null;
            int bad = 0;
            try
            {
                for (int i = 0; i < entries.Count; i++)
                {
                    var e = entries[i];
                    if (EditorUtility.DisplayCancelableProgressBar("POLY", e.Name, (float)i / entries.Count)) break;
                    var objects = PolyModel.Decode(prj.Read(e));
                    bad += objects.Count(o => !o.ChecksumOk);
                    GameObject go = spawn ? new GameObject(e.Name) : null;
                    for (int k = 0; k < objects.Count; k++)
                    {
                        var mesh = PolyMeshBuilder.Build(objects[k], palette);
                        string assetPath = $"{dir}/{e.Name}_{e.Id}_{k}.asset";
                        AssetDatabase.CreateAsset(mesh, assetPath);
                        if (!spawn) continue;
                        var part = new GameObject(objects[k].Name);
                        part.transform.SetParent(go.transform, false);
                        part.AddComponent<MeshFilter>().sharedMesh = mesh;
                        part.AddComponent<MeshRenderer>().sharedMaterial = material;
                    }
                    if (spawn)
                    {
                        go.transform.SetParent(root.transform, false);
                        go.transform.localPosition = new Vector3((i % 8) * 12f, 0, (i / 8) * 12f);
                    }
                }
            }
            finally { EditorUtility.ClearProgressBar(); }
            AssetDatabase.SaveAssets();
            Debug.Log($"MW2: imported {entries.Count} POLY resources from '{_polyFilter}*', checksum mismatches: {bad}");
        }
    }
}
