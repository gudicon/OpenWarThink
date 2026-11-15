using MW2.Formats;
using System.IO;
using UnityEngine;

namespace MW2
{
    /// <summary>
    /// Installation adapter, not part of the original program: where the shell keeps its copy of the Titanium disc
    /// (DiscCopy) and how a disc path given by the player maps to that copy.
    /// </summary>
    public static class Mw2DiscInstall
    {
        /// <summary>The folder the disc is copied to.</summary>
        public static string DefaultTarget => Path.Combine(Application.persistentDataPath, "Mercenaries");

        /// <summary>
        /// The game folder to use for a path given by the player: the copy of the disc when the path is a disc that has
        /// been copied already, otherwise the path itself.
        /// </summary>
        public static string Resolve(string path) =>
            DiscCopy.IsDisc(path) && DiscCopy.IsInstalled(DefaultTarget) ? DefaultTarget : path;
    }
}
