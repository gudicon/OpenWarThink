using UnityEditor;
using UnityEngine;

namespace MW2.EditorTools
{
    /// <summary>Saves what Mw2MissionBuilder generates under Assets/MW2Data and registers the scene changes with Undo.</summary>
    public sealed class Mw2EditorAssetSink : Mw2AssetSink
    {
        public override void BeginFolder(string dir)
        {
            if (AssetDatabase.IsValidFolder(dir)) AssetDatabase.DeleteAsset(dir);
            Mw2ImportWindow.EnsureFolder(dir);
        }

        public override void EnsureFolder(string dir) => Mw2ImportWindow.EnsureFolder(dir);
        public override void Save(Object asset, string path) => AssetDatabase.CreateAsset(asset, path);
        public override void Flush() => AssetDatabase.SaveAssets();
        public override void Record(Object target) => Undo.RecordObject(target, "Load mission");
        public override void Created(GameObject go) => Undo.RegisterCreatedObjectUndo(go, "Load mission");
        public override void Select(GameObject go) => Selection.activeGameObject = go;
        public override void Destroy(GameObject go) => Undo.DestroyObjectImmediate(go);
        public override Material PaletteMaterial(Color32[] palette, string path) => Mw2ImportWindow.PaletteMaterial(palette, path);
    }
}
