using MW2.Shell;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace MW2.EditorTools
{
    /// <summary>MW2 → Mercenaries Shell: a new scene with the shell (Mw2Shell) reading the game folder, then Play.</summary>
    public static class Mw2ShellMenu
    {
        [MenuItem("MW2/Mercenaries Shell (Play)")]
        static void Open()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            var go = new GameObject("MW2 Shell");
            go.AddComponent<Mw2Shell>().GameFolder = EditorPrefs.GetString("MW2.GamePath", Mw2GameFolder.Default);
            EditorApplication.isPlaying = true;
        }
    }
}
