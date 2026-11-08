using System;

namespace MW2
{
    /// <summary>
    /// Default location of the user's own MechWarrior 2: Mercenaries installation. Not part of the original
    /// program: the repository ships no game path, so the default comes from the MW2_GAME_FOLDER environment
    /// variable and is empty when it is not set. The shell then asks for the folder; --game-folder and the
    /// saved Editor/Player preference still take priority.
    /// </summary>
    public static class Mw2GameFolder
    {
        public const string EnvironmentVariable = "MW2_GAME_FOLDER";

        public static string Default => Environment.GetEnvironmentVariable(EnvironmentVariable) ?? "";
    }
}
