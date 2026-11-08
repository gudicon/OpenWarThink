# Windows x64 Mono Player

Build from the repository root (Unity 6000.3.14f1 with Windows Build Support):

```powershell
./tools/build_player.ps1 -Destination "$PWD/scratchpad/release"
```

The destination folder must not exist yet. The finished game is the whole `player` folder
inside it: move it as a whole, including `MW2Mercs_Data`, `MonoBleedingEdge` and
`UnityPlayer.dll`. `Library`, the imported `MW2Data` and the Unity Editor are not needed to
run it. No original MechWarrior 2: Mercenaries files are included in the build.
`inputs.json`, `player-files.json` and `unity-build.log` record which sources went into the
build and what came out; the build reads the git-tracked files of the current working tree.

If Unity is not in the default Unity Hub folder, pass `-Unity <path to Unity.exe>` or set the
`UNITY_EDITOR` environment variable.

## Running

Run it against your own installed *Mercenaries* folder:

```powershell
./MW2Mercs.exe --game-folder "E:/Games/Mercenaries"
```

The folder must contain `MW2.PRJ`, `mercsw.dll`, `MW2SHELL.DLL`, `DATABASE.MW2`,
`SHELLFIX.MW2` and the `SMK` folder; multiplayer also needs `MISSIONS/*.MWM`.
Leave the other game files (input maps, settings, MEKs, CD) where they are. The game reads
them from there and keeps saves, user MEKs and settings in Unity's per-user
`persistentDataPath`.

If the folder cannot be opened, the Player shows a path field and an **Open game folder**
button. A successful choice is remembered in the user's settings. `--game-folder` sets the
path for the current run only and takes priority over the saved choice; without it the
`MW2_GAME_FOLDER` environment variable is used as the default. Multiplayer on the title
screen passes the chosen folder to the new MercNet process. In batch mode a missing folder
exits with code 2 and a message in the log.

Self-test of a built Player:

```powershell
./MW2Mercs.exe -batchmode -nographics --mw2-selftest --seed 12345 --output <new-folder> --game-folder <game-folder>
```
