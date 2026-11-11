# OpenWarThink

▶ [Watch the trailer on YouTube](https://www.youtube.com/watch?v=zy5rsdhyjf8)

OpenWarThink is an open-source reimplementation of the engine of the 1996 PC game
*MechWarrior 2: Mercenaries*, written in Unity (C#). It is the result of reverse engineering
the original program (disassembly and decompilation of the binaries in our own copies of the
game, plus observation of its behaviour) for the purpose of interoperability: running the
original game data on a modern engine. It is **not** a clean-room implementation. Each ported
class or method cites the address of the original function it reproduces, so the port can be
checked against the original.

Reverse engineering for interoperability is treated differently in different countries. This
note explains how the project was made; it is not legal advice.

## How it was made

- The original binaries were analysed with [REA](https://github.com/morluto/rea) and
  [Ghidra](https://github.com/NationalSecurityAgency/ghidra).
- The port was written with the help of the AI coding assistants
  [Codex](https://openai.com/codex/) and [Claude](https://www.anthropic.com/claude), and
  checked against the original program with reference values captured from it and automated
  tests.

**OpenWarThink contains no game content.** To play, you need your own legally obtained
copy of *MechWarrior 2: Mercenaries*. The engine reads the original data files
(`MW2.PRJ`, `DATABASE.MW2`, `SHELLFIX.MW2`, `SMK/`, `MISSIONS/`, …) from your installation
at runtime.

## Status

Work in progress. What works today, with your own copy of the game:

- missions load from `MW2.PRJ` with the original terrain, sky, lighting, structures and units;
- combat: weapons, projectiles and missiles, heat, armour and critical damage, jump jets, MASC,
  falls and mech destruction;
- enemy and allied AI driven by the original AI tables and mission orders, objectives and
  mission success/failure;
- cockpit, HUD, radar and map, sound, mission music;
- the Mercenaries shell: career, contracts, finances, MechLab, repair, salvage, saving and loading,
  Instant Action and random missions;
- MercNet multiplayer over a local network;
- Windows (64-bit) and macOS 12+ (Intel and Apple silicon).

The whole story campaign has been played through to its ending by automated pilots
(`Assets/MW2/Runtime/Autopilot`) in Fair mode (difficulty 2, no invulnerability, no unlimited
ammunition, heat on), using only the ordinary player controls; mission results come from the
engine's own objective rules. Missions were retried with different random seeds and
per-mission tactics, and the career continued from the save before the winning sortie. SIG1
was left through the campaign's own failure branch, and the final mission LUT4 was flown by a
fixed-rule controller.

Still open or approximate:

- a full campaign playthrough by a human player is still to be confirmed;
- some AI behaviour, collision details and visual effects are approximated and marked as such
  in the code;
- the network transport is a modern adaptation, not the original `ANET2.DLL` protocol stack.

Bug reports with the mission name and steps to reproduce are welcome, as are comparisons with
the original game.

## Repository layout

| Path | Contents |
|---|---|
| `Assets/MW2` | The engine: file format decoders, simulation, AI, HUD, shell (career, MechLab), network. |
| `tools/` | `build_player.ps1`, `rip_cue_music.py` and [tools/README.md](tools/README.md): building a Windows Player, running it against a game folder, adding mission music from your own disc image |

## Requirements

- Unity 6000.3.14f1 (built-in render pipeline, legacy Input). `tools/build_player.ps1` looks for it
  in the default Unity Hub folder; set `UNITY_EDITOR` (path to `Unity.exe`) if it is installed elsewhere.

## Quick start

**To play**, download a build for Windows or macOS from
[Releases](https://github.com/gudicon/OpenWarThink/releases) and point it at your *Mercenaries*
folder; the release notes explain each step.

**To work on the code:**

1. Clone the repository and open it in Unity.
2. Use **MW2 → Import Window**, point it at your *Mercenaries* folder and import (you can
   also set the `MW2_GAME_FOLDER` environment variable; the repository has no default path). The
   imported data goes to `Assets/MW2Data`, which is git-ignored and must never be committed.
3. Use **MW2 → Tables → Extract missing** to read the engine tables out of your copy of
   `mercsw.dll` / `MW2SHELL.DLL`.
4. Open `Assets/mercenaries.unity` and press Play.

Player build: `./tools/build_player.ps1 -Destination <new folder>` for Windows, add `-Target Mac`
for a universal macOS app (needs Mac Build Support (Mono) for the editor), see
[tools/README.md](tools/README.md). The scene is also listed in Build Settings, so
**File → Build Profiles → Build** works too.

## Controls

Keys follow the original *Mercenaries* key card; camera keys follow the installed `inputw.map`.

**Remapping:** in the shell, move the mouse to the top of the screen and open **Options → Cockpit
Controls**. Every cockpit action (movement, torso, jump jets, weapons, view) can be bound to any
keys, with Shift or Ctrl, and to mouse buttons; every command (lance orders, HUD, targeting,
MASC, …) to any key with Shift, Ctrl or Alt. Click **+** and press a key to add a binding, click a
binding to remove it. Your bindings are saved to `controls.json` in your user-data folder
(`%USERPROFILE%\AppData\LocalLow\DefaultCompany\MW2Mercs`); the game's own map files are not
changed, and deleting the file restores the original controls. Commands can't use mouse buttons,
cockpit actions can't use Alt, and the mouse axes are not remappable. This screen is an addition
of the port: the original Cockpit Controls offered a fixed set of choices.

The defaults:

| Action | Keys |
|---|---|
| Throttle | `1`…`0` (1 stop, 0 full), `+` / `−` ±10 %, `Backspace` reverse |
| Steer, torso | `←` / `→` steer; `<` / `>` torso twist, `/` centre torso, `↑` / `↓` torso pitch |
| Jump jets, MASC | `J` jump (`Home` / `End` jets forward / back), `V` MASC (if equipped) |
| Fire | `Space` or left mouse button; `\` chain / group fire; `P` alpha strike; `Num Lock`, keypad `/` and `*` fire groups 1–3 |
| Weapons | `Enter` next weapon, `Shift`+`1`…`5` weapon group |
| Targets, navigation | `T` / `R` next / previous target, `E` nearest enemy, `Ctrl`+`T` no target, `N` next nav point |
| Heat, ammunition | `O` override thermal shutdown, `S` manual shutdown / restart, `;` jettison ammunition |
| Lance | `B` or `Ctrl`+`F1` command computer (digits pick, `0` / `Esc` back), `Ctrl`+`F2` / `Ctrl`+`F3` command point 2 / 3 |
| Mission | `F12` objectives |
| Camera | `C` cockpit / external; `Ctrl`+arrows turn the pilot's head (cockpit) or orbit the mech (external); `Z` / `Shift`+`Z` zoom; keypad `7` / `9` glance left / right, keypad `5` centre; `F7` / `F8` rear / down HUD window; `F10` follow a missile |

Titanium cheat codes: during a mission hold `Ctrl`+`Alt`+`Shift` and type `in` (jump jets on
your mech) or `cr` (infinite jump fuel).

### Automated test pilot

The test pilot is off by default and drives the mech through the ordinary player controls.

| Keys / option | What it does |
|---|---|
| `Ctrl`+`Shift`+`F8` | Turn the test pilot on or off in the current mission |
| `Ctrl`+`Shift`+`F9` | Show its debug overlay |
| `Ctrl`+`Shift`+`F10` | Run 10 isolated attempts of the current mission |
| `--mw2-career-playthrough --career-sorties <n>` | Start a real career and play `n` contracts, stopping at the first defeat |
| `--career-save <file.sav>` | Continue a career playthrough from a saved game |
| `--mw2-mission-checks` | Check mission win/loss conditions on synthetic scenarios |

The original `F8`–`F10` bindings stay available. Command-line options apply to a built Player.

## What is not in this repository

Some material from the private development history is deliberately left out of this repository:

- the original executables and libraries (`mercsw.dll`, `mercswin.exe`, `MW2SHELL.DLL`, network DLLs),
- the reverse-engineering workspace: disassembly and decompiler output, function maps, analysis
  scripts and reference values captured from the original program,
- tables, text and other data extracted from the game,
- internal work logs. Some notes still mention them; those references are dead here.

Do not add any of these in a contribution. `.gitignore` blocks the common cases.

## Legal

OpenWarThink is an unofficial fan project. It is not affiliated with, endorsed by, or
sponsored by Microsoft, Activision, Harebrained Schemes, Catalyst Game Labs, Topps, or any
holder of the MechWarrior or BattleTech trademarks. Those names appear only to describe
compatibility with the original game. All trademarks belong to their respective owners.

The source code in this repository is licensed under the [GNU General Public License v3.0](LICENSE).
The license does not cover the original game or any of its data.
