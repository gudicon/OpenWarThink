# OpenWarThink Mod Kit

Make your own weapon looks for OpenWarThink: lasers, bolts, impacts, muzzle flashes, and the explosions of a mech
that is destroyed. You do not need the game's source
project or any change to the game. A mod is a folder that you put into the game's `Mods` folder.

This kit is a small Unity project. It makes example prefabs you can change, writes the mod's files and builds the asset
bundle straight into your mod.

- [What a mod can change](#what-a-mod-can-change)
- [What you need](#what-you-need)
- [Your first mod in ten minutes](#your-first-mod-in-ten-minutes)
- [Making it your own](#making-it-your-own)
- [The effect file](#the-effect-file)
- [Mech explosions](#mech-explosions)
- [Without Unity: JSON-only looks](#without-unity-json-only-looks)
- [Testing](#testing)
- [Sharing your mod](#sharing-your-mod)
- [Troubleshooting](#troubleshooting)

## What a mod can change

Weapon looks: the shot in flight, the flash at the muzzle and the impact, for any weapon, a class of weapons (all
medium lasers) or a family (all lasers).

Mech explosions: the large explosion when a mech is destroyed, the smaller ones on the wreck after it, an arm or a leg
blown off, and a wreck that breaks apart when it lands, for one mech, a weight class or every mech.

The picture: smoother edges, calmer distant textures, the fog distance, colour, sharpness and glow of the 3D view
(see "Picture settings" below; no Unity needed).

A look changes pictures only. Damage, heat, range, speed, rate of fire, sounds, splash damage and debris stay as in
the original game, and a mod cannot change who gets hit or how a mech dies. Mods for sounds, music, mech paint and weapon numbers are
planned but do not exist yet.

## What you need

- OpenWarThink with your copy of MechWarrior 2: Mercenaries, as for playing.
- **Unity 6000.3.14f1** (install it from Unity Hub, "Install Editor" -> "Archive"). Asset bundles only load in the game
  when they are built with the same Unity version as the game.
- In Unity Hub, the build support modules for the platforms you want your mod to work on: Windows Build Support (Mono),
  Mac Build Support (Mono), Linux Build Support (Mono). The kit builds a bundle for each one it finds and skips the others.

## Your first mod in ten minutes

1. **Open the kit.** Unity Hub -> Add -> "Add project from disk" -> choose this `modkit` folder -> open it with
   6000.3.14f1. The first start takes a minute or two.
2. **Open the Mod Kit window:** menu **Mod Kit -> Open Mod Kit**.
3. **Game's Mods folder:** click `...` and choose the `Mods` folder next to `MW2Mercs.exe`. If there is none, make one
   (on macOS: next to `MW2Mercs.app`, not inside it).
4. Fill in **Id** (your mod's folder name, for example `my-lasers`), **Name**, **Author** and **Description**.
5. **Weapons:** `laser` (all lasers) is a good start. **Shot:** `Beam` for lasers, `Bolt` for things that fly,
   `Impact only` to change just the hit. **Impact prefab** is what appears where the shot hits.
6. Click the three buttons in order:
   1. **Create example prefabs** makes `MyBeam`, `MyBolt` and `MyHit` in `Assets/MyMod`;
   2. **Write mod.json and effects/weapons/laser.json** writes your mod's files;
   3. **Build the bundle into the mod** writes `mymod.windows` (and `.mac`, `.linux`) next to the JSON file.
7. **Start the game:** Options -> Mods. Click RELOAD if your mod is not listed, make sure it is ON, then ACCEPT and
   **TEST RANGE**. Fire at the mechs standing in front of you. `Ctrl+Shift+[` and `Ctrl+Shift+]` switch between all
   weapons. In the top right corner the range shows which file gives the current weapon its look.

Your mod folder now looks like this:

```
Mods/
  my-lasers/
    mod.json
    effects/
      weapons/
        laser.json
        mymod.windows
        mymod.mac
        mymod.linux
```

## Making it your own

Open `Assets/MyMod` in the Project window and double-click a prefab to edit it. Change colours, sizes, particle
counts and lights, add particle systems, or bring your own textures and meshes. When you are done, click **3. Build the
bundle into the mod** again. The game notices the new bundle while it runs, and your next shot uses it.

### Editing a prefab, step by step

1. Show the **Project** window if you cannot see it: menu **Window -> General -> Project** (Ctrl+5).
2. In it open `Assets` -> `MyMod` and double-click **MyBeam** (or select it and click **Open** at the top right of the
   Inspector; a single click shows only the root, "Open for full editing support"). Unity opens the prefab; the
   **Hierarchy** shows its parts (click the triangle next to MyBeam if they are folded):

   ```
   MyBeam          the beam's core (Line Renderer)
     Sheath        the beam's coloured sheath (Line Renderer)
     Spot          the burning spot on the target (particles and a light)
       Sparks      sparks thrown from the spot
     Muzzle        the glow at the muzzle (particles)
   ```

3. Click a part in the Hierarchy and change it in the **Inspector**:
   - **Sheath** -> *Line Renderer* -> **Color**: click the colour bar, click a marker above it and pick a colour. The
     sheath gives the beam most of its colour.
   - **MyBeam** -> *Line Renderer* -> **Color**: the core, now nearly white. The left end of the colour bar is at the
     muzzle, the right end at the target. **Width** makes the beam thicker or thinner.
   - **Spot**, **Sparks**, **Muzzle** -> *Particle System* -> **Color over Lifetime**: click the colour bar.
   - **Spot** -> *Light* -> **Color**: the light the spot throws around.
4. Save with Ctrl+S (or tick **Auto Save** at the top of the Scene view). The arrow **<** at the top of the Hierarchy
   leaves the prefab.
5. In the Mod Kit window click **3. Build the bundle into the mod**. If the game is running, the next shot uses it.

Every prefab in `Assets/MyMod` goes into the bundle, and the bundle is named after the folder (`mymod`). Prefab names
must differ, because the game finds a prefab by its name.

### Using a prefab from the Asset Store (or anywhere else)

Effects made for the **Built-in Render Pipeline** work; ones made only for URP or HDRP do not (they draw pink). For
example, to put an explosion pack on missile hits:

1. **Import** the pack into the kit: Window -> Package Manager -> My Assets -> the pack -> Download -> Import.
2. **Pick an explosion:** drag its prefab into the Scene to look at it, then drag the prefab (or a copy, Ctrl+D) into
   your prefab folder (`Assets/MyMod`). Its materials and textures can stay where they are: the bundle takes everything
   the prefab uses.
3. In the Mod Kit window: **Weapons** `missile` (or `srm`, `lrm`, `autocannon`, ...), **Shot** `Impact only`,
   **Impact prefab** the explosion, **Impact scale** about 0.3..1 to start with (packs often make big explosions).
4. Click **2** and **3**, then try it in the test range. **2** writes which prefab the effect file uses, **3** only
   builds the bundle: after you pick another prefab, press **2** again (step 3 warns when a file names a prefab the
   bundle no longer has). Change the scale and click 2 again: the game reloads the effect
   file while it runs.

The shot keeps flying as in the game; only the hit changes. Scripts in the prefab are taken off (bundles carry no
code), so a part that needed one (a flickering light, a camera shake) is lost; the rest plays, and the game removes the
copy when its particles are gone. Many pack explosions have **Play On Awake** off and a script that starts them: when
none of a prefab's particle systems plays on awake, the game starts them itself (all but sub-emitters), as such a script
does. Turn **Looping** off for an explosion, or every copy stays for 30 seconds. Step 3 lists what will act otherwise in
the game under **IN THE GAME**. The original sound of the hit stays, so a sound in the prefab plays on top of it.
Keep explosions light: an LRM20 volley places 20 of them at once.

**Licences:** assets from the Asset Store are for your own projects under the Asset Store licence. Before you share a
mod with them, check the pack's licence or ask its author: a bundle can be unpacked by anyone who has it. Your own
assets, or ones under CC0, are always safe to share.

### How the game places a prefab

| part | where its root stands | +Z (blue arrow) points |
|---|---|---|
| `shot` with `"reach": "instant"` (MyBeam) | where the shot's line first meets something, at once | along the shot |
| `shot` with `"reach": "shot"` (MyBolt) | on the flying shot; it stays where the shot ended | along the flight |
| `impact` (MyHit) | at the hit | back towards the shooter |
| `muzzle` | at the muzzle | along the shot |

- **Beams:** with `"stretch": true` the game sets the points of every LineRenderer in the shot prefab from the muzzle
  to the far end, every frame, so the beam stays on the arm when the torso turns.
- **A child named `Muzzle`** in a shot prefab is kept at the muzzle (MyBeam's glowing lens).
- **When the shot ends**, the prefab's particle systems stop emitting, its trails stop and its lines and lights fade
  over `life` seconds. The copy goes when its particles are gone.
- **Lights:** a Unity point or spot Light becomes the game's soft light (colour, intensity, range). Up to 16 are drawn,
  the ones nearest to the camera.

### What a prefab may contain

Only things that draw or sound: ParticleSystem, LineRenderer, TrailRenderer, MeshFilter and MeshRenderer,
SkinnedMeshRenderer, BillboardRenderer, LODGroup, Animator, Animation, AudioSource, and Light (see above). Anything
else (colliders, rigid bodies, scripts) is taken off before the copy wakes up, and the game says so in red in the top
left corner. Bundles carry no code.

Use built-in shaders. The examples use `Legacy Shaders/Particles/Additive` and `Alpha Blended`, which always work. The
game has no HDR or bloom, so colours brighter than white just turn white.

At most 256 copies are alive at once; more are not placed.

## The effect file

A look is a JSON file in your mod at `effects/weapons/<name>.json`. You can write `// comments` in it.

### Which name

The game tries three names for every weapon, from the most exact, and the first file found wins:

1. **the weapon's own name**: `MLAS`, `cerMLAS`, `PPC`, `LBAC10`, ...;
2. **its class**: `large-laser`, `medium-laser`, `small-laser`, `large-pulse-laser`, `medium-pulse-laser`,
   `small-pulse-laser`, `lrm`, `srm`, `streak-srm`, `inferno-srm`, `nuke`, `torpedo`, `arrow-iv`, `narc`, `autocannon`,
   `ultra-autocannon`, `lb-autocannon`, `machine-gun`, `ams`, `gauss`, `ppc`, `flamer`;
3. **its family**: `laser`, `missile`, `ballistic`, `ppc`, `flamer`.

Weapon names (Clan weapons start with `c`):

| family | names |
|---|---|
| laser | `erLLAS` `LLAS` `MLAS` `SLAS` `LPLAS` `MPLAS` `SPLAS`, Clan: `cerLLAS` `cerMLAS` `cerSLAS` `cLPLAS` `cMPLAS` `cSPLAS` |
| missile | `LRM5` `LRM10` `LRM15` `LRM20` `SRM2` `SRM4` `SRM6` `SSRM2` `Inf SRM` `InfSSRM` `NARC` `ArrowIV`, Clan: `cLRM5`..`cLRM20` `cSRM2` `cSRM4` `cSRM6` `cSSRM2` `cSSRM4` `cSSRM6` `cInfSRM` `cInfSSRM` `cNUKE` `cTorpedo` `cNARC` `cArrowIV` |
| ballistic | `AC2` `AC5` `AC10` `AC20` `uAC5` `LBAC10` `GAUSS` `MGun` `AMS`, Clan: `cuAC2` `cuAC5` `cuAC10` `cuAC20` `cLBAC2` `cLBAC5` `cLBAC10` `cLBAC20` `cGAUSS` `cMGun` `cAMS` |
| ppc | `PPC` `erPPC`, Clan: `cerPPC` |
| flamer | `FLAMER`, Clan: `cFLAMER` |

The whole file is used; parts of different files are not mixed.

### Parts

```json
{
  "shot":   { "hideOriginal": true, "prefab": { "bundle": "mymod", "asset": "MyBeam", "reach": "instant", "stretch": true, "hold": 0.3, "life": 0.1 } },
  "muzzle": { "hideOriginal": true },
  "impact": { "hideOriginal": true, "prefab": { "bundle": "mymod", "asset": "MyHit" } }
}
```

Every part and every field is optional. A part you leave out stays as in the original game.

- `hideOriginal`: on `shot`, hides the original shot model, its light and its mark on the ground; on `muzzle` and
  `impact`, hides the original explosion and its light, and on `impact` also the small pieces a missile or autocannon
  hit throws (they are only a picture). Sound and splash damage stay either way.

### `prefab`

| field | parts | default | what it does |
|---|---|---|---|
| `bundle` | all | | the bundle's name, next to the JSON file (or in a subfolder): the game loads `<name>.windows`, `.mac` or `.linux` for its platform |
| `asset` | all | | the prefab's name in the bundle |
| `scale` | all | 1 | size multiplier, 0.01..100 |
| `life` | all | -1 | seconds over which lines and lights fade (for a shot: after the shot ends); -1 keeps them until the particles are gone; at most 30 |
| `reach` | shot | `"shot"` | `"shot"`: rides on the flying shot. `"instant"`: stands at once where the shot's line meets something |
| `stretch` | shot | false | LineRenderers run from the muzzle to the far end (a beam) |
| `flicker` | shot | 0 | 0..1: line width wavers from frame to frame by this much |
| `hold` | shot | 0 | 0..5 s: the least time it shows fully, even when the game's shot ends sooner; the `impact` pictures then come when it lets go |

Why `hold`: as in the original game, a shot that goes through the same slot as a shot that stopped at a target hits on
its first step. Against a target that stands still, every second laser shot therefore hits at once, and a beam without
`hold` would only flash for one frame. Damage and the moment of the hit are the game's either way.

## Mech explosions

A mech-explosion look is a JSON file in your mod at `effects/mechs/<name>.json`.

**With the Mod Kit:** set **Look for** to `Mech destruction`, choose **Mechs** and the **Explosion prefab** (your own or
one from a pack, as for impacts), set **Explosion scale** (3 is a good start for `MyHit`; the smaller explosions get 0.4
of it, a lost arm or leg 0.5), and click **2** and **3**. Step 2 writes `effects/mechs/mech.json`; step 3 builds the
bundle into `effects/weapons` as always, and the mech file uses it from there (`"bundle": "../weapons/mymod"`), so one
bundle serves weapons and mechs alike. In the test range, `Ctrl+Shift+K` destroys the nearest mech.

**Which name.** The game tries, from the most exact:

1. **the mech's model**: `ATLAS`, `COMMANDO`, `TIMBRWLF`, ... (the model's name in the game files);
2. **its weight class**: `light` (below 40 tons), `medium` (40 to 55), `heavy` (60 to 75), `assault` (80 and more);
3. `mech`: every mech;
4. `unit`: every unit with armour, vehicles and turrets too.

The test range shows the names it tries for the nearest target.

**Parts.** Four, each optional and written like `impact` of a weapon look (`flash`, `light`, `sparks`, `prefab`,
`hideOriginal`):

```json
{
  "death":     { "hideOriginal": true, "prefab": { "bundle": "../weapons/mymod", "asset": "MyHit", "scale": 3 },
                 "light": { "color": "#ffa040", "range": 40, "intensity": 2, "time": 1.5 } },
  "burst":     { "hideOriginal": true, "prefab": { "bundle": "../weapons/mymod", "asset": "MyHit", "scale": 1.2 } },
  "section":   { "prefab": { "bundle": "../weapons/mymod", "asset": "MyHit", "scale": 1.5 } },
  "blowApart": { "sparks": { "count": 60, "spread": 180, "speed": 20 } },
  "partBlast": { "hideOriginal": true, "prefab": { "bundle": "../weapons/mymod", "asset": "MyHit", "scale": 0.6 } },
  "debris":    { "hideOriginal": true, "prefab": { "bundle": "../weapons/mymod", "asset": "MyTrail", "life": 0.3 } }
}
```

| part | when | where |
|---|---|---|
| `death` | the mech is destroyed | at the mech's middle |
| `burst` | each smaller explosion on the wreck, for about 10 seconds after | near the mech's middle |
| `section` | an arm, a leg, a side or the centre torso is blown off | in the middle of the part that comes off |
| `blowApart` | the wreck lands hard and its remaining parts fly off | at the mech's middle |
| `partBlast` | a part that flew off (an arm, a leg, a torso) is shot or blasted apart | at the part's middle |
| `debris` | the wreck's explosions throw small pieces | rides with each piece while it flies |

- **Prefabs stand upright**: +Y up, +Z the way the mech faces (unlike impacts, whose +Z points back at the shooter).
  Most explosions from packs are made this way.
- **Sparks** fly up in a cone of `spread` degrees; 180 sends them every way.
- `hideOriginal` on `death`, `burst` and `partBlast` hides the game's own explosion and its light. The sound, the splash
  damage, the debris thrown and the parts flying off stay. `section` and `blowApart` have no explosion of their own in
  the game.
- `debris` takes only `hideOriginal` and `prefab`. `hideOriginal` leaves out the game's pixel smoke behind each piece;
  the pieces themselves still fly. The prefab follows the piece (upright, it does not spin with it) — give it particles
  that stay in World space, emitted by distance, for a trail. Where the piece stops, its particles stop emitting and it
  fades over `life` seconds.
- The mech dies exactly as in the game: when, how long the explosions go on and what they hit do not change.

## Without Unity: JSON-only looks

Some looks need no bundle at all. These fields go into the same parts and can be mixed with `prefab`. Sizes are
metres, times are seconds. A colour is `"#rgb"`, `"#rrggbb"`, `"#rrggbbaa"` or `[r, g, b, a]` from 0 to 1. Pictures
are `.png` or `.jpg` files next to the JSON; they are drawn added to what is behind them, so black is see-through.

- `shot.beam`: a glowing beam behind the shot: `length`, `width`, `color`, `coreWidth`, `coreColor`, `texture`.
- `shot.light`, `muzzle.light`, `impact.light`: a soft light: `color`, `range`, `intensity`, `time` (muzzle and impact).
- `muzzle.flash`, `impact.flash`: a flash sprite: `size`, `grow`, `color`, `time`, `texture`.
- `muzzle.sparks`, `impact.sparks`: `count`, `speed`, `size`, `life`, `spread`, `gravity`, `color`, `endColor`, `texture`.
- `laser`: a wavy energy beam from the muzzle, drawn by the game: `reach` (`"shot"` or `"instant"`), `width`, `color`,
  `glow`, `halo`, `strands`, `waver`, `duration`, `pulses` (`count`, `duration`, `interval`), `lights`, and more.

If you write a name the game does not know, it tells you in red in the top left corner, with the list of names it
knows there. A value out of range is replaced by the nearest allowed one, with a warning.

## Picture settings: `graphics/quality.json`

A mod can also change how the 3D world is drawn, with no Unity at all: put `graphics/quality.json` into the mod
folder. Every name is optional; a name you leave out stays as in the original game.

```json
{
  "antiAliasing": 8,       // 0, 2, 4 or 8 samples on polygon edges
  "textureSamples": 4,     // 1..8: far textures stop shimmering (1 = original)
  "fogDistance": 1,        // 0.25..8: factor on the mission's fog distance
  "brightness": 1, "contrast": 1.12, "saturation": 1.1, "gamma": 1,
  "sharpen": 0.25,         // 0..2
  "vignette": 0.2,         // 0..1
  "bloom": { "intensity": 0.5, "threshold": 0.75, "soft": 0.5, "radius": 5 }
}
```

- Only the look changes: the HUD, the menus, the rules, the sensors and the frame rate stay as they are. Fades to and
  from black and the infrared view still work.

### Modern lighting

Add a `lighting` section to the same file to light the world the modern way, with the mission's own sun and sky:
metal with highlights, real sun shadows, darker corners (ambient occlusion) and the sky seen in surfaces. Night
missions stay dark. The infrared view keeps the original light.

```json
"lighting": {
  "sun": 1.15, "sky": 1.0,        // 0..4: factors on the mission's sun and sky light
  "fill": 0.35,                   // 0..2: the mission's second light, set against the sun (lights the shaded side)
  "bounce": 0.35,                 // 0..1: light from the ground below
  "reflections": 1.0,             // 0..2: the sky seen in surfaces
  "shadows": 0.75,                // 0..1: strength of sun shadows (they replace the flat shadows under units)
  "shadowDistance": 350,          // 20..2000 metres
  "occlusion": 0.55,              // 0..1: darker corners and contacts
  "occlusionRadius": 1.5,         // 0.2..10 metres
  "units":      { "metallic": 0.25, "roughness": 0.45 },   // mechs and vehicles
  "structures": { "metallic": 0.0,  "roughness": 0.75 },   // buildings and the rest
  "ground":     { "metallic": 0.0,  "roughness": 0.95 }
}
```

Two more parts of the section:

```json
"probe": { "enabled": true, "resolution": 256, "facesPerFrame": 1 },   // the world seen in the surfaces
"lights": { "intensity": 1.5, "range": 0.2, "always": true }           // light of shots in flight and explosions
```

More parts of the section, and a picture setting beside it:

```json
"rays": { "intensity": 0.6, "decay": 0.96, "length": 0.6 },          // sun rays through gaps in clouds and buildings
"ssr": { "intensity": 1.0, "distance": 150, "thickness": 2 },        // reflections of the world on smooth surfaces
"emissive": 2.0                                                      // fire, explosions and glows brighter
```

```json
"hdr": { "exposure": 1.0, "tonemapping": "aces" }   // top level: light above white is kept, then tonemapped
```

With `hdr` on, a bloom `threshold` above 1 makes only the really bright light glow. Screen-space reflections only show
on smooth surfaces: lower a surface's `roughness` to see more of them.

The probe looks around from the camera a face or a few at a time (higher `facesPerFrame` reacts faster and costs
more). `lights` makes the game's own shot and explosion lights round and soft; `range` is a factor on their size, and
`always` turns them on even when Multiple Lights is off in the game's options.

Mechs and vehicles can have a different surface for each kind of part. The game tells the parts apart by its own
textures: painted plates, bare metal, vents and grilles, weapon textures and the windshields:

```json
"unitParts": {
  "paint":     { "metallic": 0.05, "roughness": 0.55 },                  // painted armour and insignia
  "metal":     { "metallic": 0.75, "roughness": 0.4 },                   // bare metal
  "mechanics": { "metallic": 0.45, "roughness": 0.75, "albedo": 0.8 },   // vents, ribs, grilles (a bit darker)
  "weapons":   { "metallic": 0.6,  "roughness": 0.35 },                  // weapon textures
  "glass":     { "metallic": 0.0,  "roughness": 0.1 },                   // cockpit windshields
  "rows": "weapons"                                                      // faces in a plain palette colour: barrels, trims
},
"contactShadows": { "strength": 0.6, "length": 1.5 }                     // small sun shadows under feet, in joints
```

Each class may list its own `textures` by the names of the game's CEL files, with `*` for any letters and `?` for one
(the defaults: paint `PLATE*`, `TH*`; metal `SSMETSU*`, `SLMETSPC`, `SLPLAINC`; mechanics `S?VENT*`, `S?VERT*`,
`S?MESH`, `SLHORIC`; weapons `SWEP*`; glass `*CLOU`). A class the file does not name, and every other face, takes
`units`.

`"enabled": false` in the section turns it off again. Strong metal (above 0.5) darkens the painted textures, because
there is no detailed world around to reflect.
- The game reads the file again within half a second when you save it, also in the middle of a mission.
- If two active mods have the file, the one lower in the list wins as a whole.
- A `fogDistance` above 1 lets players see farther than the mission intends; in a network game everybody needs the
  same mods anyway.

## Testing

- **Test range:** Options -> Mods -> TEST RANGE. The other mechs stand still, you cannot be hurt and weapons need no
  ammunition. `Ctrl+Shift+[` / `]`: previous / next weapon. `Ctrl+Shift+F`: fire on its own every 0.6 s.
  `Ctrl+Shift+K`: destroy the nearest mech at once (there are only a few; open the range again for more). Leave
  through the cockpit menu.
- **Live changes:** while the game runs (in the range or in a mission), it checks the `effects` folders of active mods
  twice a second. Save a JSON file or build the bundle again, and the next shot uses it ("Weapon effects reloaded").
  A new mod folder needs RELOAD on the Mods screen.
- **Mistakes** show in red in the top left corner and in the game's log. A broken JSON file names its line and column,
  and that weapon keeps its original look until you fix it.

## Sharing your mod

- Zip the mod's folder (`my-lasers`, with `mod.json` inside). Players unzip it into their `Mods` folder; the game
  shows a zip that is not unpacked as an error.
- Include `.windows`, `.mac` and `.linux` bundles if you can, so that everyone sees the same thing.
- `mod.json`:

  ```json
  {
    "id": "my-lasers",
    "name": "My Lasers",
    "version": "1.0",
    "author": "You",
    "description": "What it changes.",
    "api": 1
  }
  ```

  `id` must be unique, without `; @ |` or quotes. Raise `version` when you publish a change. `api` is the mod format
  version the mod was made for, now `1`.
- **Order:** when two mods give a look to the same weapon, the one lower in the list on the Mods screen wins.
- **Multiplayer:** everyone in a game needs the same mods, in the same order, with the same versions. The game says
  what differs.

## Troubleshooting

| what you see | why, and what to do |
|---|---|
| "not an asset bundle this game can read" | the bundle was built with another Unity version or for another platform: build it with 6000.3.14f1 |
| "no file mymod (or mymod.windows, .mac, .linux)" | the bundle is not next to the JSON file, or `bundle` is misspelled |
| a list of names after the prefab's name | `asset` does not match any prefab in the bundle; the list shows the ones it has |
| "taken off: BoxCollider" (or similar) | the prefab has a component a look may not have; remove it from the prefab |
| the hit shows nothing, the log has "The referenced script (…) is missing" | that is the pack's script, which a bundle cannot carry; it is harmless. Before version 0.7.1 the game did not start particle systems that a script started: turn **Play On Awake** on for them, press **3** |
| an explosion stays on the ground for half a minute | its particle systems loop: turn **Looping** off and press **3** |
| nothing changes | the mod is OFF or not accepted on the Mods screen, or a more exact file of a mod lower in the list wins (the range shows which file is used) |
| pink beams or particles | a renderer has no material, or its material's shader does not work in the game. The kit checks every bundle it builds for your own platform and refuses one with missing materials, naming the prefab and the renderer; use built-in shaders |
| white or grey squares instead of soft particles | the particle material has no texture: give it one (a round soft dot, like `glow.png` of the examples) |
| "A script in this project does not compile" or "Unity could not build the bundle" | a script that came with a pack does not compile (the Console names it), and Unity builds nothing until it does. Packs often bring demo scripts: delete them, or install the package they need (Window -> Package Manager -> Unity Registry). The kit already has Unity UI, which most demo scripts use |
| the game looks for a prefab you deleted or renamed | the effect file still names it: choose the prefab in the Mod Kit window and press **2**, then **3** |
| "must be a file next to the JSON file, in a folder below it, or elsewhere in the mod's effects folder" | a `bundle` or picture path with `..` leads out of the mod's `effects` folder; keep it inside (`../weapons/mymod` is fine) |
| a mech explosion is huge or tiny | packs make explosions of very different sizes: change **Explosion scale** and press **2** |
| the beam only flashes | add `"hold": 0.3` to the shot prefab |
| the Mods screen switches the mod off with a message about its version | its `api` is not one this game knows; you can still turn it ON, and it loads as it is |

Building from the command line (for scripts):

```
Unity -batchmode -quit -projectPath <modkit> -executeMethod OpenWarThink.ModKit.ModKitWindow.FromCommandLine
      --modkit-mods <the game's Mods folder> [--modkit-id my-lasers] [--modkit-weapon laser] [--modkit-style beam|bolt|impact]
      [--modkit-impact MyHit] [--modkit-scale 1] [--modkit-platforms windows,mac,linux]
```

With `--modkit-mechs mech` (or `light`, `medium`, `heavy`, `assault`, `unit`) it writes a mech-explosion file instead
of a weapon file, with `--modkit-impact` as the explosion and `--modkit-scale` as its size.

This makes the example prefabs, writes the mod files (replacing the effect file) and builds the bundle. To build your
own folder instead, select it and use **Mod Kit -> Build Asset Bundle From Selected Folder**.
