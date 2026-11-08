using System.IO;
using System.Collections.Generic;
using System.Linq;
using MW2.Formats;
using UnityEditor;
using UnityEngine;

namespace MW2.EditorTools
{
    /// <summary>
    /// Batch-mode check of the format decoders against the real MW2.PRJ:
    /// Unity.exe -batchmode -projectPath ... -executeMethod MW2.EditorTools.Mw2BatchVerify.Run -quit
    /// </summary>
    public static class Mw2BatchVerify
    {
        public static void VerifySkyDrawOrder()
        {
            string game = EditorPrefs.GetString("MW2.GamePath", Mw2GameFolder.Default);
            using var prj = new PrjArchive(Path.Combine(game, "MW2.PRJ"));
            var skyEntry = prj.Entries("POLY").First(e => e.Name == "TL1SKYDM");
            var sky = PolyModel.Decode(prj.Read(skyEntry)).Single();
            var expected = sky.Polygons.Select(p => PolyMeshBuilder.TextureKey(p.material)).ToList();
            var actual = new List<int>();
            var mesh = PolyMeshBuilder.Build(sky, new Color32[256], null,
                _ => new PolyMeshBuilder.TextureInfo(128, 128, 7), actual, preservePolygonOrder: true);
            try
            {
                if (mesh.subMeshCount != expected.Count || !actual.SequenceEqual(expected))
                    throw new System.Exception($"TL1SKYDM draw order differs: expected {expected.Count}, got {mesh.subMeshCount}");
                Debug.Log($"MW2 VERIFY: TL1SKYDM preserves all {expected.Count} POLY draw calls");
            }
            finally { Object.DestroyImmediate(mesh); }
        }

        /// <summary>Type 1 city buildings stop a descending mech at the box top without pulling one up from inside.</summary>
        public static void VerifyType1Roof()
        {
            var building = new GameObject("roof probe building");
            var mech = new GameObject("roof probe mech");
            try
            {
                var box = building.AddComponent<BoxCollider>();
                box.center = new Vector3(0, 10, 0);
                box.size = new Vector3(20, 20, 20);
                building.AddComponent<Mw2Surface>().CollisionType = 1;
                var controller = mech.AddComponent<Mw2MechController>();
                var motionField = typeof(Mw2MechController).GetField("_motion",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                var collide = typeof(Mw2MechController).GetMethod("Collide",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                Physics.SyncTransforms();

                void Check(float previousY, float currentY, float expectedY)
                {
                    var motion = new MechMotion(6, 0) { X = 0, Y = currentY * 100, Z = 0 };
                    motionField.SetValue(controller, motion);
                    collide.Invoke(controller, new object[] { 0f, previousY * 100, 0f });
                    if (Mathf.Abs(motion.Y * PolyModel.UnitsToMeters - expectedY) > 0.01f)
                        throw new System.Exception($"Type 1 roof: {previousY} -> {currentY}, got {motion.Y * PolyModel.UnitsToMeters}, expected {expectedY}");
                }

                Check(30f, 10f, 20f); // A fast descent crosses the roof.
                Check(20f, 20f, 20f); // A mech standing on it stays there.
                Check(10f, 10f, 10f); // A mech already inside is not lifted through it.
                Debug.Log("MW2 VERIFY: type 1 roof supports landing and standing, without lifting from inside");
            }
            finally
            {
                Object.DestroyImmediate(mech);
                Object.DestroyImmediate(building);
            }
        }

        public static void Run()
        {
            VerifySkyDrawOrder();
            VerifyCelAlpha();
            string game = EditorPrefs.GetString("MW2.GamePath", Mw2GameFolder.Default);
            using var prj = new PrjArchive(Path.Combine(game, "MW2.PRJ"));
            var palette = Palette.Decode(prj.Read("PAL", "BROW0NDA"));

            int objs = 0, ok = 0, polyRes = 0;
            foreach (var e in prj.Entries("POLY"))
            {
                try
                {
                    var o = PolyModel.Decode(prj.Read(e));
                    objs += o.Count; ok += o.Count(x => x.ChecksumOk); polyRes++;
                }
                catch (System.Exception ex) { Debug.LogWarning($"POLY {e.Name}: {ex.Message}"); }
            }
            int cels = 0;
            foreach (var e in prj.Entries("CEL"))
            {
                var c = Cel.Decode(prj.Read(e));
                var t = c.ToTexture(palette, e.Name);
                Object.DestroyImmediate(t);
                cels++;
            }
            Debug.Log($"MW2 VERIFY: tags={string.Join(",", prj.Tags)} POLY resources={polyRes} objects={objs} checksumOk={ok} CEL={cels}");

            // Snapshot of the Atlas parts laid out in a row.
            var mat = new Material(Shader.Find("MW2/VertexColorLit"));
            var parts = prj.Entries("POLY").Where(e => e.Name.StartsWith("AT1")).ToList();
            var root = new GameObject("verify");
            float x = 0;
            foreach (var e in parts)
            {
                foreach (var o in PolyModel.Decode(prj.Read(e)))
                {
                    var mesh = PolyMeshBuilder.Build(o, palette);
                    var go = new GameObject(o.Name);
                    go.transform.SetParent(root.transform);
                    go.AddComponent<MeshFilter>().sharedMesh = mesh;
                    go.AddComponent<MeshRenderer>().sharedMaterial = mat;
                    go.transform.position = new Vector3(x - mesh.bounds.min.x, -mesh.bounds.min.y, -mesh.bounds.center.z);
                    x += mesh.bounds.size.x + 1f;
                }
            }
            var light = new GameObject("light").AddComponent<Light>();
            light.type = LightType.Directional;
            light.transform.rotation = Quaternion.Euler(40, -30, 0);
            var cam = new GameObject("cam").AddComponent<Camera>();
            cam.backgroundColor = new Color(0.12f, 0.12f, 0.16f);
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.orthographic = true;
            cam.orthographicSize = x / 4f;
            cam.transform.position = new Vector3(x / 2f, x / 6f, -x);
            cam.transform.LookAt(new Vector3(x / 2f, 2f, 0));
            var rt = new RenderTexture(1600, 800, 24);
            cam.targetTexture = rt;
            cam.Render();
            RenderTexture.active = rt;
            var shot = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
            shot.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            string outPng = Path.Combine(Path.GetDirectoryName(Application.dataPath), "verify_atlas.png");
            File.WriteAllBytes(outPng, shot.EncodeToPNG());
            Debug.Log($"MW2 VERIFY: snapshot {outPng}, parts={parts.Count}");
        }

        public static void VerifyCelAlpha()
        {
            string game = EditorPrefs.GetString("MW2.GamePath", Mw2GameFolder.Default);
            using var prj = new PrjArchive(Path.Combine(game, "MW2.PRJ"));
            foreach (string name in new[] { "IVA1CLOU", "IVA1MNTE" })
            {
                var cel = Cel.Decode(prj.Read("CEL", name));
                var texture = cel.ToTexture(null, name);
                try
                {
                    var pixels = texture.GetPixels32();
                    for (int y = 0; y < cel.Height; y++)
                    for (int x = 0; x < cel.Width; x++)
                    {
                        int index = cel.Indices[y * cel.Width + x];
                        byte actual = pixels[(cel.Height - 1 - y) * cel.Width + x].a;
                        byte expected = index < cel.Colors.Length ? cel.Colors[index].a : (byte)0;
                        if (actual != expected)
                            throw new System.Exception($"{name} alpha differs at ({x}, {y}), index {index}: {actual} != {expected}");
                    }
                }
                finally { Object.DestroyImmediate(texture); }
            }
            Debug.Log("MW2 VERIFY: CEL index zero retains its palette alpha");
        }
    }
}
