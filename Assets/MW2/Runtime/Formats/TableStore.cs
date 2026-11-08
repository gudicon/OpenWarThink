using System;
using System.IO;
using UnityEngine;

namespace MW2.Formats
{
    /// <summary>
    /// Where the engine keeps the tables it extracted from the game's libraries, and how it reads them back.
    ///
    /// Some of the original's tables — weapons, effects, sound assignment, input bindings, HUD layout, the campaign
    /// economy — were compiled into mercsw.dll and MW2SHELL.DLL instead of being stored in MW2.PRJ. Reading them from
    /// those libraries at fixed addresses works, but it ties the engine to one build of the game and leaves the data
    /// unmoddable. So each table is extracted once into a file here, and everything afterwards reads the file.
    ///
    /// The game's own folder is never written to: these files live in the engine's data directory, which is
    /// `Assets/MW2Data/tables` in the editor and a folder under the player's persistent data otherwise. Pass
    /// `--mw2-tables &lt;directory&gt;` to put them somewhere else.
    ///
    /// An existing file is never overwritten, so edits to it survive re-importing. <see cref="Extract{T}"/> is what
    /// writes one, and only when it is absent.
    /// </summary>
    public sealed class TableStore
    {
        /// <summary>Command-line switch that overrides the directory.</summary>
        public const string DirectoryArgument = "--mw2-tables";

        static TableStore _default;

        public string Directory { get; }

        public TableStore(string directory)
        {
            if (string.IsNullOrEmpty(directory)) throw new ArgumentException("A table directory is required");
            Directory = directory;
        }

        /// <summary>The store the engine uses unless one is passed explicitly.</summary>
        public static TableStore Default
        {
            get
            {
                if (_default == null) _default = new TableStore(DefaultDirectory());
                return _default;
            }
            set { _default = value; }
        }

        static string DefaultDirectory()
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == DirectoryArgument && !string.IsNullOrEmpty(args[i + 1]))
                    return args[i + 1];
#if UNITY_EDITOR
            return Path.Combine("Assets", "MW2Data", "tables");
#else
            return Path.Combine(Application.persistentDataPath, "tables");
#endif
        }

        public string PathFor(string name) => Path.Combine(Directory, name + ".json");

        public bool Has(string name) => File.Exists(PathFor(name));

        /// <summary>Whether every one of <paramref name="names"/> is extracted (then its library is not needed).</summary>
        public bool HasAll(System.Collections.Generic.IEnumerable<string> names)
        {
            foreach (string name in names) if (!Has(name)) return false;
            return true;
        }

        /// <summary>The files extracted from mercsw.dll (MW2SHELL.DLL's are ShellTables.TableNames).</summary>
        public static readonly string[] MercswTables =
            { WeaponTable.TableName, EffectTable.TableName, SoundTables.TableName, InputSystem.TableName, HudLayout.TableName };

        /// <summary>Reads a table written earlier; the caller checks <see cref="Has"/> first.</summary>
        public T Read<T>(string name) where T : class
        {
            string path = PathFor(name);
            var value = JsonUtility.FromJson<T>(File.ReadAllText(path));
            if (value == null) throw new InvalidDataException($"{path} is not a readable {typeof(T).Name}");
            return value;
        }

        /// <summary>Writes a table, replacing whatever is there. Prefer <see cref="Extract{T}"/>.</summary>
        public void Write<T>(string name, T value) where T : class
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(PathFor(name), JsonUtility.ToJson(value, true));
        }

        /// <summary>
        /// The table as it stands: the file if it exists, otherwise <paramref name="extract"/> run against the game
        /// and written out for next time. An existing file wins, so edits to it are not overwritten by a re-import.
        /// A fresh extraction is read back from the file it wrote, so the first run sees exactly what every later run
        /// sees (fields that are not serialized, such as the weapon hook addresses, are not carried over from memory).
        /// </summary>
        public T Extract<T>(string name, Func<T> extract) where T : class
        {
            if (!Has(name)) Write(name, extract());
            return Read<T>(name);
        }
    }
}
