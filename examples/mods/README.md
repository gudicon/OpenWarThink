# Example mods

Each folder is a complete mod. To use one, copy the folder into the `Mods` folder next to the game
(`MW2Mercs.exe`; **Options → Mods → OPEN MODS FOLDER** opens it), then in **Options → Mods** press
**RELOAD**, switch the mod **ON** and press **ACCEPT**. **TEST RANGE** on the same screen opens a mission to try
weapon looks in.

| Folder | What it does | How it is made |
|---|---|---|
| `energy-lasers` | Lasers as wavering energy beams: small red, medium green, large blue; pulse lasers flash three times; coloured flash, light and sparks on impact | JSON only, drawn by the game; no Unity needed |
| `burn-lasers` | Lasers as light: the beam is there at once and burns while the shot is under way, with molten spatter, vapour and cooling armour where it hits | prefabs from an asset bundle (`lasers.windows` for Windows, `lasers.mac` for macOS); `source/MakeBurnLasers.cs` builds them. Copy the mod without the `source` folder |
| `full-arsenal` | New looks for every weapon: burning laser beams, missiles with flame and smoke trails, autocannon tracers and muzzle blasts, a gauss slug with an ion trail, PPC plasma in crackling arcs, flamer fire, inferno napalm, a NARC beacon | prefabs from an asset bundle (`arsenal.windows`, Windows only for now); `source/MakeFullArsenal.cs` builds them. Copy the mod without the `source` folder |
| `big-explosions` | Bigger destruction of mechs and vehicles: a fireball that climbs into a mushroom, a dust ring, burning chunks and a wreck burning under black smoke; bigger mechs blow up bigger | prefabs from an asset bundle (`booms.windows`, Windows only for now); `source/MakeBigExplosions.cs` builds them. Copy the mod without the `source` folder |
| `modern-light` | Modern lighting with each mission's own sun and sky: metal mechs with highlights, sun shadows, darker corners, reflections of the world in armour and cockpit glass, the cockpit frame lit from inside, smooth edges and glow. Night missions stay dark | `graphics/quality.json`; changes only the look. Heavier on the graphics card |
| `sharp-picture` | Smooth polygon edges, calm distant textures, a little more contrast and a soft glow on bright lights, with the original lighting | `graphics/quality.json`; changes only the look |
| `fix-instant-shots` | Every shot of every weapon flies the whole way to the target. In the original a shot slot remembers where its last shot stopped, so every second shot at a target that stands still hits at once; this mod turns that off | `rules/fixes.json`; changes the game, not its look |
| `max-mech-detail` | Every mech and wreck outside the cockpit keeps its most detailed model (LOD1) at any distance, and their windshields are solid in one colour; the cockpit, terrain and buildings are unchanged. Many mechs on screen may lower the frame rate | `graphics/detail.json`; changes only the look |

Use one weapon look mod at a time (`energy-lasers`, `burn-lasers` or `full-arsenal`), and one picture mod
(`modern-light` or `sharp-picture`): when two mods have the same file, the one lower in the list on the Mods screen
wins. The others combine freely: weapon looks, explosions, picture, mech detail and fixes each use their own files.

Every active mod except a language mod must be the same for every player in a MercNet game; the game refuses
players whose mods differ from the host's.

To make your own looks, see the [Mod Kit](../../modkit/README.md).

To translate the game into another language, see [Translating the game](TRANSLATING.md).
