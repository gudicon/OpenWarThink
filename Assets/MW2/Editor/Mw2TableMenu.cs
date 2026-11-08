using System.IO;
using System.Linq;
using MW2.Formats;
using MW2.Shell;
using UnityEditor;
using UnityEngine;

namespace MW2.EditorTools
{
    /// <summary>
    /// MW2 → Tables: writes the tables the original kept in its own code out to editable files, so the game's
    /// libraries are not needed at run time and the values can be modified.
    ///
    /// Extracting happens on its own the first time a mission loads, so this menu is for doing it deliberately —
    /// and for replacing files after a change to a decoder, which is the one case that needs an overwrite.
    /// </summary>
    public static class Mw2TableMenu
    {
        const string GamePathKey = "MW2.GamePath";
        static string DefaultGamePath => Mw2GameFolder.Default;

        [MenuItem("MW2/Tables/Extract missing")]
        static void ExtractMissing() => Extract(false);

        [MenuItem("MW2/Tables/Re-extract (overwrites edits)")]
        static void ReExtract()
        {
            if (EditorUtility.DisplayDialog("Re-extract tables",
                    $"This replaces the table files in {TableStore.Default.Directory} with values read from the " +
                    "game's libraries. Any edits to them are lost.", "Replace", "Cancel"))
                Extract(true);
        }

        [MenuItem("MW2/Tables/Show folder")]
        static void Show()
        {
            string dir = TableStore.Default.Directory;
            Directory.CreateDirectory(dir);
            EditorUtility.RevealInFinder(dir);
        }

        static void Extract(bool overwrite)
        {
            string game = EditorPrefs.GetString(GamePathKey, DefaultGamePath);
            if (!File.Exists(Path.Combine(game, "mercsw.dll")) || !File.Exists(Path.Combine(game, "MW2SHELL.DLL")))
            {
                Debug.LogError($"No mercsw.dll / MW2SHELL.DLL in {game}. Set the game folder in MW2 → Import Window first.");
                return;
            }

            var store = TableStore.Default;
            if (overwrite)
                foreach (string name in new[] { WeaponTable.TableName, EffectTable.TableName, SoundTables.TableName, InputSystem.TableName, HudLayout.TableName }.Concat(ShellTables.TableNames))
                    if (store.Has(name)) File.Delete(store.PathFor(name));

            // Each Get extracts its file when it is missing and leaves an existing one alone.
            int weapons = WeaponTable.Get(game, store).Count, effects = EffectTable.Get(game, store).Count;
            ShellTables.FromStore(game, store);
            SoundTables.Get(game, store);
            InputSystem.Get(game, store);
            HudLayout.GetTable(game, store);

            AssetDatabase.Refresh();
            Debug.Log($"MW2 tables in {store.Directory}: {weapons} weapons, {effects} effects, sound, input, hud, " +
                      $"shell: {string.Join(", ", ShellTables.TableNames)}.");
        }
    }
}
