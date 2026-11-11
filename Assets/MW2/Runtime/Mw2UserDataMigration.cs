using System;
using System.IO;
using UnityEngine;

namespace MW2
{
    /// <summary>
    /// Release adapter, not original behaviour. Builds up to v0.2.0 used Unity's placeholder company name
    /// "DefaultCompany", so the user-data folder (saves, controls.json, MW2SND.CFG / MW2DIF.CFG, mercs.hud, extracted
    /// tables) was .../DefaultCompany/MW2Mercs. On the first start under the real company name the files of the old
    /// folder are copied into the new one: nothing is overwritten and the old folder is left as it is. A marker file
    /// keeps it from running again, so a file the player deletes later (controls.json to restore the controls) does
    /// not come back. The game folder chosen in the shell is a PlayerPrefs value and has to be chosen once more.
    /// </summary>
    public static class Mw2UserDataMigration
    {
        public const string OldCompany = "DefaultCompany";
        const string Marker = "migrated-from-DefaultCompany.txt";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Run()
        {
            try
            {
                string current = Application.persistentDataPath;
                string company = Application.companyName;
                if (string.IsNullOrEmpty(current) || string.IsNullOrEmpty(company) || company == OldCompany) return;
                string old = OldPath(current, company);
                if (old == null || !Directory.Exists(old)) return;
                if (File.Exists(Path.Combine(current, Marker))) return;
                int copied = Copy(old, current);
                Directory.CreateDirectory(current);
                File.WriteAllText(Path.Combine(current, Marker), $"Copied {copied} files from {old} on {DateTime.Now:yyyy-MM-dd HH:mm}.\n");
                Debug.Log($"MW2: copied {copied} user files from {old} to {current}");
            }
            catch (Exception e)
            {
                Debug.LogWarning("MW2: could not copy user files from the old DefaultCompany folder: " + e.Message);
            }
        }

        /// <summary>The same path with the company name (or a bundle id made from it) of its last two parts replaced.</summary>
        static string OldPath(string current, string company)
        {
            string product = Path.GetFileName(current.TrimEnd('/', '\\'));
            string parent = Path.GetDirectoryName(current.TrimEnd('/', '\\'));
            if (string.IsNullOrEmpty(product) || string.IsNullOrEmpty(parent)) return null;
            string companyDir = Path.GetFileName(parent), root = Path.GetDirectoryName(parent);
            if (companyDir == company) return Path.Combine(root, OldCompany, product);          // Windows, macOS
            if (product.Contains(company)) return Path.Combine(parent, product.Replace(company, OldCompany));  // bundle id
            return null;
        }

        static int Copy(string from, string to)
        {
            int count = 0;
            foreach (string file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            {
                string name = Path.GetFileName(file);
                if (name.StartsWith("Player", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".log", StringComparison.OrdinalIgnoreCase)) continue;
                string target = Path.Combine(to, file.Substring(from.Length).TrimStart('/', '\\'));
                if (File.Exists(target)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(file, target);
                count++;
            }
            return count;
        }
    }
}
