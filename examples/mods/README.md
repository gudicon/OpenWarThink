# Example mods

Each folder is a complete mod. To use one, copy the folder into the `Mods` folder next to the game
(`MW2Mercs.exe`; **Options → Mods → OPEN MODS FOLDER** opens it), then in **Options → Mods** press
**RELOAD**, switch the mod **ON** and press **ACCEPT**. **TEST RANGE** on the same screen opens a mission to try
weapon looks in.

| Folder | What it does | How it is made |
|---|---|---|
| `energy-lasers` | Lasers as wavering energy beams: small red, medium green, large blue; pulse lasers flash three times; coloured flash, light and sparks on impact | JSON only, drawn by the game; no Unity needed |
| `burn-lasers` | Lasers as light: the beam is there at once and burns while the shot is under way, with molten spatter, vapour and cooling armour where it hits | prefabs from an asset bundle (`lasers.windows` for Windows, `lasers.mac` for macOS); `source/MakeBurnLasers.cs` builds them. Copy the mod without the `source` folder |
| `fix-instant-shots` | Every shot of every weapon flies the whole way to the target. In the original a shot slot remembers where its last shot stopped, so every second shot at a target that stands still hits at once; this mod turns that off | `rules/fixes.json`; changes the game, not its look |
| `max-mech-detail` | Every mech and wreck outside the cockpit keeps its most detailed model (LOD1) at any distance; the cockpit, terrain and buildings are unchanged. Many mechs on screen may lower the frame rate | `rules/fixes.json`; changes only the look, but as a rules file it must match in MercNet |

Use one laser mod at a time: if both are on, the one lower in the list on the Mods screen wins.

`rules/fixes.json` is taken whole from the mod lowest in the list that has one: to use `fix-instant-shots` and
`max-mech-detail` together, put `max-mech-detail` lower and add `"fixInstantShots": true` to its `fixes.json`.

A mod with rules (`fix-instant-shots`, `max-mech-detail`) must be the same for every player in a MercNet game; the
game refuses players whose mods differ from the host's. Language mods are not compared.

To make your own looks, see the [Mod Kit](../../modkit/README.md).

To translate the game into another language, see [Translating the game](TRANSLATING.md).
