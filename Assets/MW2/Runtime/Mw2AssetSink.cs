using MW2.Formats;
using UnityEngine;

namespace MW2
{
    /// <summary>
    /// Where Mw2MissionBuilder puts what it generates. The base class keeps meshes, textures and materials in memory (a mission
    /// launched by the campaign at run time); the editor's sink saves them under Assets/MW2Data and registers Undo.
    /// </summary>
    public class Mw2AssetSink
    {
        /// <summary>Starts a fresh output folder (the editor deletes the old one: overwriting referenced assets breaks instances).</summary>
        public virtual void BeginFolder(string dir) { }
        public virtual void EnsureFolder(string dir) { }
        public virtual void Save(Object asset, string path) { }
        public virtual void Flush() { }
        public virtual void Record(Object target) { }
        public virtual void Created(GameObject go) { }
        public virtual void Select(GameObject go) { }

        public virtual void Destroy(GameObject go)
        {
            if (Application.isPlaying) Object.Destroy(go);
            else Object.DestroyImmediate(go);
        }

        /// <summary>
        /// Flat-colour material with the palette stored inside it, so material types 1 and 4 can pick their lit palette row
        /// (mercsw.dll 0x1001afb0).
        /// </summary>
        public virtual Material PaletteMaterial(Color32[] palette, string path)
        {
            var material = new Material(Shader.Find("MW2/VertexColorLit")) { name = System.IO.Path.GetFileNameWithoutExtension(path) };
            material.SetFloat("_UsePalette", 1);
            material.SetTexture("_Palette", Palette.ToTexture(palette, material.name + "_Palette"));
            return material;
        }
    }
}
