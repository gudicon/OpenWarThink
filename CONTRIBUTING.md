# Contributing to OpenWarThink

Thank you for helping. Bug reports, comparisons with the original game and code changes are all
welcome.

## Never add game content

OpenWarThink ships no part of *MechWarrior 2: Mercenaries*. Do not add, attach or paste:

- original game files: executables and libraries (`mercsw.dll`, `mercswin.exe`, `MW2SHELL.DLL`,
  network DLLs), archives and data (`MW2.PRJ`, `*.MW2`, `*.MWM`, `SMK/`, …);
- anything extracted from them: textures, models, sounds, music, videos, text, tables or saves;
- disassembly or decompiler output, Ghidra databases or similar analysis files.

`.gitignore` blocks the common cases, but please check your changes before you push. Pull
requests that contain such material will be closed. Screenshots and short clips of the port in
issues are fine.

## Reporting a bug

Please include:

- the mission (for example `BET1`) or shell screen, and the steps to reproduce;
- what the original game does and what the port does instead;
- your Windows version, whether you run the Editor or a built Player, and `Player.log` if there
  is an error.

## Changing code

- Port behaviour from the original program, not from memory of how the game felt. In the class or
  method comment, give the address of the original function the code reproduces (for example
  `Mission_CheckObjectives 0x100421e6`), so others can check it.
- If something cannot be ported exactly, keep the adapter small and say in the comment what
  differs from the original.
- Keep `Assets/MW2/Runtime` free of `UnityEditor` references; editor-only code belongs in
  `Assets/MW2/Editor`.
- Do not commit the generated `Assets/MW2Data` folder.
- Before opening a pull request, make sure the project compiles in Unity 6000.3.14f1 and the
  affected mission or screen still works. A Player built with `tools/build_player.ps1` and run
  with `--mw2-selftest` is a good extra check.

By contributing you agree that your contribution is licensed under the
[GNU General Public License v3.0](LICENSE).
