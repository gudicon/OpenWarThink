using System.Collections.Generic;
using System.Linq;
using MW2.Formats;
using UnityEngine;

namespace MW2
{
    /// <summary>
    /// Builds a mission scene: scenery from the WLD1 areas, sky dome, nav points, and the player's mech at the start point.
    /// Planet gravity and temperature are read from PLNT (World_LoadChunks 0x10070e92). Runs in the editor (assets saved by
    /// the editor's sink) and at run time for a campaign mission (assets kept in memory by the base Mw2AssetSink).
    /// </summary>
    public static class Mw2MissionBuilder
    {
        const string OutRoot = "Assets/MW2Data/Missions";

        /// <summary>The room's values over the mission's own, before anything reads them (World_LoadChunks 0x10070e92 with DAT_100a6c3c).</summary>
        public static System.Action<Mission> NetOverride;

        public static GameObject Build(PrjArchive prj, string gameFolder, string prefix, string mech, int lod, string mek,
                                       Mw2AssetSink sink = null, byte[] playerMekData = null,
                                       System.Func<string, byte[]> careerMek = null)
        {
            sink ??= new Mw2AssetSink();
            var mission = Mission.Load(prj, prefix);
            NetOverride?.Invoke(mission);
            // World_LoadChunks10073313/10073491 builds the human GPS's model/MEK.
            // A campaign normally supplies it through userstar, but SIG2 authors
            // its own Pegasus instead. An unrelated written roster MEK cannot
            // replace that GPS or receive the scripted vehicle's return damage.
            if (!Mw2MechCombat.NetGame && playerMekData != null)
            {
                int humanAt = mission.Spawns.FindIndex(s => s.Player);
                if (humanAt >= 0)
                {
                    var human = mission.Spawns[humanAt];
                    if (!string.Equals(human.Model, mech, System.StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(human.Loadout, System.IO.Path.GetFileNameWithoutExtension(mek), System.StringComparison.OrdinalIgnoreCase))
                    {
                        mech = human.Model; mek = human.Loadout;
                        playerMekData = careerMek?.Invoke(mek);
                    }
                }
            }
            // Mech_LoadMek scales the armour by the difficulty of mw2dif.cfg +5 (LoadCfgDifficulty 0x10024cf0).
            Mw2MechCombat.Difficulty = DifficultyConfig.Load(gameFolder).Difficulty;
            if (mission.PaletteIds == null || mission.PaletteIds.Length == 0)
            {
                Debug.LogError($"MW2: mission {prefix}: no palette group found (missing: {string.Join(", ", mission.Missing)})");
                return null;
            }
            // The palette of the starting time of day (INIT period -> PALG mode, 0x1001f6a1); entry 0 is daytime.
            var palEntry = prj.Entries("PAL").FirstOrDefault(e => e.Id == mission.StartPaletteId);
            var palette = Palette.Decode(prj.Read(palEntry));

            string dir = $"{OutRoot}/{mission.Prefix}";
            sink.BeginFolder(dir);
            var polyById = prj.Entries("POLY").ToDictionary(e => e.Id);
            var flatMaterial = sink.PaletteMaterial(palette, $"{dir}/Flat.mat");
            var material = flatMaterial;
            var textures = new Mw2TextureBank(prj, mission, palette, dir, sink, flatMaterial.GetTexture("_Palette") as Texture2D);
            (PolyMeshBuilder.TextureInfo info, Material material)? TextureFor(int key) => textures.Get(key);

            var meshCache = new Dictionary<int, List<(Mesh mesh, Material[] materials)>>();
            List<(Mesh mesh, Material[] materials)> MeshesFor(int polyId)
            {
                if (meshCache.TryGetValue(polyId, out var list)) return list;
                list = new List<(Mesh, Material[])>();
                if (polyById.TryGetValue(polyId, out var entry))
                {
                    var objects = PolyModel.Decode(prj.Read(entry));
                    for (int k = 0; k < objects.Count; k++)
                    {
                        var keys = new List<int>();
                        // The original submits TL1SKYDM one POLY face at a time. Repeated mountain texture keys are
                        // deliberately interleaved around the ring, so grouping them by material makes a later panel's
                        // translucent background blend over an earlier panel's opaque mountain.
                        bool skyDrawOrder = entry.Name.EndsWith("SKYDM", System.StringComparison.OrdinalIgnoreCase);
                        var mesh = PolyMeshBuilder.Build(objects[k], palette, null, key => TextureFor(key)?.info, keys,
                            preservePolygonOrder: skyDrawOrder);
                        sink.Save(mesh, $"{dir}/{entry.Name}_{entry.Id}_{k}.asset");
                        var materials = keys.Count == 0
                            ? new[] { flatMaterial }
                            : keys.Select(key => key < 0 ? flatMaterial : TextureFor(key)?.material ?? flatMaterial).ToArray();
                        list.Add((mesh, materials));
                    }
                }
                meshCache[polyId] = list;
                return list;
            }

            // A loaded mission has a separate player object. Keep only one player/controller and mission root:
            // otherwise both LateUpdate hooks move Camera.main, and firing may still belong to the old player.
            foreach (var old in Object.FindObjectsByType<Mw2MissionDirector>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (old.Player != null) sink.Destroy(old.Player.gameObject);
                sink.Destroy(old.gameObject);
            }
            // Earlier preview objects could have a keyboard controller but no combat component.
            foreach (var preview in Object.FindObjectsByType<Mw2MechController>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (!preview.TryGetComponent<Mw2MechCombat>(out _))
                {
                    preview.KeyboardControl = false;
                    preview.FollowCamera = false;
                }

            var root = new GameObject($"Mission {mission.Prefix} ({palEntry.Name})");
            var audio = root.AddComponent<Mw2Audio>();
            audio.PrjPath = prj.FilePath;
            audio.CdTrack = mission.CdTrack;
            audio.ClimateSoundRange = mission.SoundRange;
            int placed = 0, skipped = 0, destructibles = 0;
            foreach (var (file, nodes) in mission.Areas)
            {
                var area = new GameObject(file);
                area.transform.SetParent(root.transform, false);
                var areaNodes = new Dictionary<int, Transform>();
                var (p, s) = PlaceNodes(nodes, area.transform, polyById, MeshesFor, areaNodes);
                placed += p;
                skipped += s;
                // TSK class 4: looping sounds at area nodes (fires, windmills, taxiing jets, crowds; 0x1000e68f).
                if (prj.TryFind("BWD", file, out var areaEntry))
                {
                     var areaData = prj.Read(areaEntry);
                     AttachMaterialTasks(prj, polyById, nodes, areaNodes, palette, areaData);
                    // TSK class 0 (0x1000e170): the valid IVA1ARE1 windmill tasks rotate model Z.
                     foreach (var (node, angle, ticks) in BwdSkeleton.DecodeRotationTasks(areaData))
                        if (angle.x == 0 && angle.y == 0 && areaNodes.TryGetValue(node, out var rotatingNode))
                        {
                            var rotation = rotatingNode.gameObject.AddComponent<Mw2RotationTask>();
                            rotation.ZDegrees = angle.z;
                            rotation.PeriodTicks = ticks;
                        }
                     // TSK class 2, FUN_1000e3d0: SCRI crowd sound nodes circle their parent.
                     foreach (var (node, degrees, speed) in BwdSkeleton.DecodeOrbitTasks(areaData))
                         if (areaNodes.TryGetValue(node, out var orbitNode))
                         {
                             var orbit = orbitNode.gameObject.AddComponent<Mw2OrbitTask>();
                             orbit.DegreesPerSecond = degrees;
                             orbit.UnitsPerSecond = speed;
                         }
                     foreach (var (node, range, sound, _) in BwdSkeleton.DecodeSoundTasks(areaData))
                        if (areaNodes.TryGetValue(node, out var soundNode))
                        {
                            var ambient = soundNode.gameObject.AddComponent<Mw2AmbientSound>();
                            ambient.Sound = sound;
                            ambient.RangeUnits = range;
                        }
                }
                // TSK class 5 (0x1000e89e): nodes driven along PTBL paths (BET2 governor's cars).
                foreach (var task in mission.PathTasks.Where(t => t.Path != null &&
                             string.Equals(t.File, file, System.StringComparison.OrdinalIgnoreCase)))
                    if (areaNodes.TryGetValue(task.Node, out var pathNode))
                    {
                        var mover = pathNode.gameObject.AddComponent<Mw2PathTask>();
                        mover.PathName = task.PathName;
                        mover.Mode = task.Mode;
                        mover.Rotate = task.Rotate;
                        mover.Points = task.Path.Points;
                        mover.Angles = task.Path.Angles;
                        mover.Ticks = task.Path.Ticks;
                    }
                foreach (var gt in mission.Destructibles.Where(g => string.Equals(g.File, file, System.StringComparison.OrdinalIgnoreCase)))
                {
                    if (!areaNodes.TryGetValue(gt.Node, out var intact)) continue;
                    var destructible = intact.gameObject.AddComponent<Mw2Destructible>();
                    destructible.File = gt.File;
                    destructible.DisplayName = string.IsNullOrEmpty(gt.Name) ? intact.name : gt.Name;
                    destructible.ObjectiveMask = gt.ObjectiveMask;
                    destructible.Strength = gt.Strength;
                    destructible.Flags = gt.Flags;
                    destructible.Contents = gt.Contents;
                    destructible.Index = mission.Destructibles.IndexOf(gt);
                    destructible.Side = mission.SideOfAffiliation(gt.Affiliation);
                    // AfterWorldLoader 0x100739f9: GT +0x10 = radius (+0x40) of the intact model, the largest distance
                    // of its vertices from their bounding-box centre (FUN_10003efa 0x10003efa).
                    // The node's POLY may sit on the node or on its "<node>_" mesh children (see AddCollider).
                    var intactMesh = intact.GetComponent<MeshFilter>();
                    if (intactMesh == null)
                        for (int ci = 0; ci < intact.childCount && intactMesh == null; ci++)
                            if (intact.GetChild(ci).name.StartsWith(intact.name + "_")) intactMesh = intact.GetChild(ci).GetComponent<MeshFilter>();
                    if (intactMesh != null && intactMesh.sharedMesh != null)
                    {
                        var centre = intactMesh.sharedMesh.bounds.center;
                        foreach (var v in intactMesh.sharedMesh.vertices)
                            destructible.Radius = Mathf.Max(destructible.Radius, (v - centre).magnitude);
                    }
                    // GT +2: the replacement object (FUN_1005f27b 0x1005f27b hides it with 0x800 at load; FUN_1005f301
                    // 0x1005f301 shows it on destruction with the intact object's matrix), e.g. the BET2 armored car's
                    // wreck VCBCOP under VA1COP. In all 78 SCN1 trees it is never above the intact node.
                    if (gt.BaseNode >= 0 && areaNodes.TryGetValue(gt.BaseNode, out var baseNode))
                    {
                        destructible.Replacement = baseNode;
                        destructible.Debris = baseNode.GetComponentsInChildren<Renderer>(true)
                            .Where(r => !r.transform.IsChildOf(intact)).ToArray();
                        foreach (var r in destructible.Debris) r.enabled = false;
                    }
                    destructibles++;
                }
            }

            var skyObjects = new List<GameObject>();
            foreach (var (file, nodes) in mission.Sky)
            {
                var sky = new GameObject(file);
                sky.transform.SetParent(root.transform, false);
                PlaceNodes(nodes, sky.transform, polyById, MeshesFor);
                skyObjects.Add(sky);
            }

            string skyGroundPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(prj.FilePath), "SKYGND.PAR");
            var skyGround = SkyGround.Load(skyGroundPath, mission.Prefix);
            if (skyGround == null) Debug.LogWarning($"MW2: {skyGroundPath} has no line for {mission.Prefix}; DLL defaults (no fog)");
            var ground = skyGround == null || skyGround.HasGround ? TextureFor(0x100 + 0x4a) : null;
            // Ground_DrawTextured 0x100019c1 draws nothing when key 0x14a has no texture (Texture_Lookup = -1).
            if (ground != null)
                CreateGround(root.transform, ground.Value.material, dir, sink, (skyGround ?? new SkyGround()).GroundRepeatUnits * PolyModel.UnitsToMeters);
            // AddComponent invokes Awake immediately in campaign missions. The backdrop setup must see
            // the ground sibling so it can draw it before the sky without writing depth.
            foreach (var sky in skyObjects) sky.AddComponent<Mw2FollowCameraXZ>();

            // WTHR (FUN_1006eb46): rain or snow around the camera; MLIT: point lights of the light pool.
            if (mission.Weather != null)
            {
                var weather = root.AddComponent<Mw2Weather>();
                weather.Type = mission.Weather.Type;
                weather.Rate = mission.Weather.Rate;
                weather.Velocity = mission.Weather.Velocity;
            }
            foreach (var light in mission.Lights)
            {
                var go = new GameObject("MLIT point light");
                go.transform.SetParent(root.transform, false);
                go.transform.position = PolyMeshBuilder.ToUnity(light.Position);
                var pl = go.AddComponent<Mw2PointLight>();
                pl.Radius = light.Radius;
                pl.Color = light.Color;
            }

            Mw2PaletteFade.Add(root, prj, mission);     // the palette on the screen: start fade-in, time of day, infrared, flashes
            var atmosphere = root.AddComponent<Mw2Atmosphere>();
            if (skyGround != null)
            {
                atmosphere.FogColor = skyGround.Fog;
                atmosphere.SkyColor = skyGround.Sky;
                atmosphere.DensityA = skyGround.Density.x;
                atmosphere.DensityB = skyGround.Density.y;
                atmosphere.FogDistance = skyGround.FogDistanceUnits * PolyModel.UnitsToMeters;
                atmosphere.CloudSpeed = skyGround.SkyAnim;
                atmosphere.Ambient = skyGround.Ambient;
                atmosphere.LightA = skyGround.LightA;
                atmosphere.LightB = skyGround.LightB;
                atmosphere.GlobalIntensity = skyGround.GlobalIntensity;
            }
            else atmosphere.DensityA = 0f;
            atmosphere.CloudMaterial = TextureFor(0x18c)?.material;
            // Lights need both the LITE chunk (sun, ambient level) and the SKYGND.PAR line (amb/wa/wb).
            atmosphere.LightsSet = mission.HasLight && skyGround != null;
            atmosphere.AmbientLevel = mission.AmbientLevel;
            atmosphere.SunEnabled = mission.SunEnabled;
            atmosphere.SunIsDirection = mission.SunIsDirection;
            atmosphere.Sun = mission.SunVector;

            var navRoot = new GameObject("Nav points");
            navRoot.transform.SetParent(root.transform, false);
            foreach (var nav in mission.NavPoints)
            {
                var go = new GameObject($"{nav.File}: {nav.Name}");
                go.transform.SetParent(navRoot.transform, false);
                go.transform.localPosition = PolyMeshBuilder.ToUnity(nav.Position);
                go.transform.localRotation = Quaternion.Euler(0, nav.Heading, 0);
            }
            sink.Flush();

            var weaponTable = WeaponTable.Get(gameFolder);
            if (Mw2MechCombat.NetGame) Mw2MechCombat.NetWeaponTable = weaponTable;

            GameObject player = null;
            // DoFirstObjtv 0x10040733: every team starts at its own first order. Outside a network game the player's team is 0.
            NavPoint start = default;
            bool hasStart = Mw2MechCombat.NetGame ? mission.TryGetTeamStart(Mw2MissionDirector.PlayerTeam, out start) : mission.TryGetStart(out start);
            if (!string.IsNullOrEmpty(mech) && hasStart)
            {
                player = BuildMech(prj, palette, mech, lod, mek, material, textures, sink: sink, mekData: playerMekData,
                    paint: mission.Spawns.Where(sp => sp.Player).Select(sp => mission.TeamPaint[sp.Team & 15]).DefaultIfEmpty(-1).First());
                player.transform.position = PolyMeshBuilder.ToUnity(start.Position);
                player.transform.rotation = Quaternion.Euler(0, start.Heading, 0);
                PlaceCameraBehind(player.transform, sink);
                // Shell FUN_10024a97 writes the active MEK; Mech_LoadMek 0x1002f9e0 consumes that configuration.
                byte[] playerLoadout = playerMekData;
                if (playerLoadout == null && prj.TryFind("MEK", mek, out var playerMek)) playerLoadout = prj.Read(playerMek);
                if (playerLoadout != null)
                {
                    Mw2MechCombat.Setup(player, MekLoadout.Decode(playerLoadout), weaponTable,
                        mission.SideOf(mission.Spawns.Where(s => s.Player).Select(s => s.Team).FirstOrDefault()), true, $"{mech} ({mek})");
                    // Units_WriteMeks 0x100598b1 writes back the MEK of a unit loaded from a shell-written file (flag
                    // +0xe42 & 2); the campaign launch hands the shell's MEK over in memory.
                    if (player.TryGetComponent<Mw2MechCombat>(out var careerUnit))
                    {
                        careerUnit.MekBytes = playerLoadout;
                        careerUnit.MekName = mek;
                        careerUnit.CareerUnit = playerMekData != null;
                    }
                }
                if (player.TryGetComponent<Mw2MechController>(out var playerController)) playerController.PlanetGravity = mission.Gravity;
                if (player.TryGetComponent<Mw2MechCombat>(out var playerCombat))
                {
                    playerCombat.SourceFile = "userstar";
                    foreach (var spawn in mission.Spawns.Where(s => s.Player))
                    {
                        playerCombat.ObjectiveMask = spawn.ObjectiveMask;
                        playerCombat.UnitFlags = spawn.Flags;
                        playerCombat.InspectText = spawn.InspectText;
                        playerCombat.GamepieceIndex = mission.Spawns.IndexOf(spawn);
                        // Objective_AddTarget1004045b binds the actual GPS file,
                        // including scripted sig2star as well as en??star/userstar.
                        playerCombat.SourceFile = spawn.File;
                        playerCombat.Team = spawn.Team;
                        break;
                    }
                    playerCombat.PlanetTemperature = mission.Temperature;
                    playerCombat.HostileAtmosphere = mission.HostileAtmosphere;
                    playerCombat.GamepieceType = BwdSkeleton.DecodeGamepieceType(prj.Read("BWD", mech));
                    playerCombat.OriginHeight = BwdSkeleton.OriginHeight(prj, mech);
                    playerCombat.ModelRadius = BwdSkeleton.UnitRadius(prj, mech);
                }
                if (player.TryGetComponent<Mw2Cockpit>(out var cockpit))
                {
                    Texture2D Hud(string name)
                    {
                        if (!prj.TryFind("SHP", name, out var e)) return null;
                        var tex = Shape.Decode(prj.Read(e)).ToTexture(palette, name);
                        sink.Save(tex, $"{dir}/{name}.asset");
                        return tex;
                    }
                    Mw2Cockpit.HudSprite HudHot(string name)
                    {
                        var tex = Hud(name);
                        if (tex == null) return null;
                        var shape = Shape.Decode(prj.Read("SHP", name));
                        return new Mw2Cockpit.HudSprite { Texture = tex, HotX = shape.HotX, HotY = shape.HotY };
                    }
                    cockpit.Compass = HudHot("COMPASS6");
                    for (int k = 0; k < 4; k++)
                    {
                        cockpit.BracketFriendly[k] = HudHot($"TGTGP{k + 1}6");
                        cockpit.BracketEnemy[k] = HudHot($"TGTGP{k + 1}E6");
                        cockpit.BracketNeutral[k] = HudHot($"TGTGP{k + 1}N6");
                    }
                    cockpit.OffFriendly = HudHot("TGTOFFF6");
                    cockpit.OffEnemy = HudHot("TGTOFFE6");
                    cockpit.OffNeutral = HudHot("TGTOFFN6");
                    cockpit.Reticle = HudHot("RETICLE6");
                    cockpit.ReticleOnTarget = HudHot("RCLTGT6");
                    cockpit.ReticleSrm = HudHot("RCLLOCK6");
                    cockpit.ReticleNoLock = HudHot("RCLNOLK6");
                    cockpit.ReticleLocking = HudHot("RCLPLOC6");
                    cockpit.ReticleLocked = HudHot("RCLGLOC6");
                    cockpit.ReticleNotReady = HudHot("RCLINOP6");
                    cockpit.CompassTarget = HudHot("CMPMKR16");
                    cockpit.CompassTargetAhead = HudHot("CMPMKR26");
                    cockpit.CompassCentre = HudHot("CMPMKR36");
                    cockpit.CompassCentreAhead = HudHot("CMPMKR46");
                    cockpit.CompassUp = HudHot("CRTUP6");
                    cockpit.CompassDown = HudHot("CRTDOWN6");
                    cockpit.CompassLeft = HudHot("CRTLFT6");
                    cockpit.CompassRight = HudHot("CRTRGHT6");
                    cockpit.Damage = Hud("WIREDMG6");
                    cockpit.NavMarker = HudHot("TGTNP6");
                    cockpit.PausedSprite = HudHot("PAUSED6");
                    // Main menu window 4 (0x10099e68): background SHP 0xe8 + 1, slider list 0x10099368 (0x2b, 0x28, 0x2e, 0x3a) + 1.
                    cockpit.MenuBackground = HudHot("WESCSIM6");
                    cockpit.SliderLeft = HudHot("MENDL6");
                    cockpit.SliderBar = HudHot("MBAR6");
                    cockpit.SliderRight = HudHot("MENDR6");
                    cockpit.SliderKnob = HudHot("MSLIDE6");
                    // Window view icons (FUN_1003f263): SHP 0xd6 / 0xd3 / 0xd9 + 1.
                    cockpit.ViewRearIcon = HudHot("VP_RER6");
                    cockpit.ViewDownIcon = HudHot("VP_DWN6");
                    cockpit.ViewMissileIcon = HudHot("VP_WPN6");
                    cockpit.RadarUser = Hud("RUSERGP6");
                    cockpit.RadarFriendly = Hud("RGPF6");
                    cockpit.RadarEnemy = Hud("RGPE6");
                    cockpit.RadarNeutral = Hud("RGPN6");
                    cockpit.RadarTargetFriendly = Hud("RTGTGPF6");
                    cockpit.RadarTargetEnemy = Hud("RTGTGPE6");
                    cockpit.RadarTargetNeutral = Hud("RTGTGPN6");
                    cockpit.RadarOffFriendly = Hud("RTGTOFF6");
                    cockpit.RadarOffEnemy = Hud("RTGTOFE6");
                    cockpit.RadarOffNeutral = Hud("RTGTOFN6");
                    cockpit.RadarTargetHiddenFriendly = Hud("RTGTGTF6");
                    cockpit.RadarTargetHiddenEnemy = Hud("RTGTGTE6");
                    cockpit.RadarTargetHiddenNeutral = Hud("RTGTGTN6");
                    cockpit.RadarNav = Hud("RTGTNP6");
                    cockpit.RadarNavTarget = Hud("RNP6");
                    cockpit.RadarNavVisited = Hud("RRTGTNP6");
                    cockpit.RadarNavTargetVisited = Hud("RRNP6");
                    cockpit.TargetNavIcon = Hud("VTGT_NP6");
                    cockpit.TargetNavVisitedIcon = Hud("VTGT_NR6");
                    cockpit.TargetViewShader = Shader.Find("MW2/TargetView");

                    // Gauge layout: CPIT resource named by the mech's CPTF chunk over the DLL defaults (HudLayout).
                    byte[] cpit = null, hudRes = null;
                    var mechBwd = prj.Read("BWD", mech);
                    foreach (var (tag, b, len) in BwdChunks.Enumerate(mechBwd))
                    {
                        if (tag == "CPTF" && prj.TryFind("CPIT", BwdChunks.ReadString(mechBwd, b + 2, len - 2), out var cpitEntry))
                            cpit = prj.Read(cpitEntry);
                        if (tag == "HUDF" && prj.TryFind("HUD", BwdChunks.ReadString(mechBwd, b + 2, len - 2), out var hudEntry))
                            hudRes = prj.Read(hudEntry);
                    }
                    string hudFolder = System.IO.Path.GetDirectoryName(prj.FilePath);
                    cockpit.Layout = HudLayout.Load(HudLayout.GetTable(hudFolder), cpit, hudRes);

                    // Damage wireframe: base sprite (WIREDM) for the region scale, its 640x480 variant (next id) drawn.
                    var shapes = prj.Entries("SHP").ToDictionary(e => e.Id);
                    if (cockpit.Layout.DamageShapeId >= 0 && shapes.TryGetValue(cockpit.Layout.DamageShapeId, out var wireBase) &&
                        shapes.TryGetValue(cockpit.Layout.DamageShapeId + 1, out var wireEntry))
                    {
                        var baseShape = Shape.Decode(prj.Read(wireBase));
                        var wire = Shape.Decode(prj.Read(wireEntry));
                        cockpit.WireWidth = wire.Width;
                        cockpit.WireHeight = wire.Height;
                        cockpit.WirePixels = wire.Pixels;
                        cockpit.WireRegions = cockpit.Layout.DamageRegionMap(wire.Width, wire.Height, baseShape.HeaderWidth, baseShape.HeaderHeight,
                                                                             wire.HeaderWidth, wire.HeaderHeight);
                    }
                    if (prj.TryFind("FONT", "BASE6X76", out var fontEntry))
                    {
                        var font = HudFont.Decode(prj.Read(fontEntry));
                        cockpit.FontAtlas = font.ToAtlas("BASE6X76", out var glyphX);
                        sink.Save(cockpit.FontAtlas, $"{dir}/BASE6X76.asset");
                        cockpit.GlyphX = glyphX;
                        cockpit.GlyphWidth = font.Widths;
                        cockpit.FontHeight = font.Height;
                    }
                    cockpit.Palette = palette;
                    cockpit.MessageBar = Hud("MSGBAR6");
                }
            }

            // Projectile objects get flag 0x400 (0x10070e92: 0x1005acfd(object, 0x400)); with it Material_Shade
            // (0x1001afb0) returns lit types 1 / 4 unlit, at the row's brightest shade (the baked vertex colour).
            Mesh Unlit(Mesh mesh)
            {
                var ids = new List<Vector2>();
                mesh.GetUVs(1, ids);
                if (!ids.Any(id => (int)id.y >> 4 == 1 || (int)id.y >> 4 == 4)) return mesh;
                var copy = Object.Instantiate(mesh);
                copy.name = mesh.name + "_0x400";
                for (int i = 0; i < ids.Count; i++)
                {
                    int high = (int)ids[i].y;
                    if (high >> 4 == 1 || high >> 4 == 4) ids[i] = new Vector2(ids[i].x, high & 0x0F);
                }
                copy.SetUVs(1, ids);
                sink.Save(copy, $"{dir}/{copy.name}.asset");
                return copy;
            }

            // Unit shadows (Glow type 1, FUN_1005d44e): the units' polygons flattened to the ground, black, alpha 0x80.
            var shadowMaterial = new Material(Shader.Find("MW2/Shadow")) { name = "MW2 shadow" };
            sink.Save(shadowMaterial, $"{dir}/MW2 shadow.mat");
            root.AddComponent<Mw2UnitShadows>().Material = shadowMaterial;

            // Projectile models: the shot file's BTHG slots (MERCSHT1), first node of each projectile type.
            var shots = root.AddComponent<Mw2ShotModels>();
            if (prj.TryFind("BWD", "MERCSHT1", out var shotEntry))
            {
                byte[] shotBwd = prj.Read(shotEntry);
                var shotNodes = BwdSkeleton.Decode(shotBwd).SelectMany(r => r).GroupBy(n => n.Node).ToDictionary(g => g.Key, g => g.First());
                foreach (var slot in BwdSkeleton.DecodeShotSlots(shotBwd))
                    shots.Slots.Add(new Mw2ShotModels.Slot { Type = slot.type, Node = slot.node,
                        HasNode = shotNodes.TryGetValue(slot.node, out var node) && polyById.ContainsKey(node.PolyId) });
                foreach (var kv in BwdSkeleton.DecodeShotNodes(shotBwd))
                    if (shotNodes.TryGetValue(kv.Value, out var shotNode))
                        foreach (var (mesh, materials) in MeshesFor(shotNode.PolyId))
                            shots.Parts.Add(new Mw2ShotModels.Part { Type = kv.Key, Mesh = Unlit(mesh), Materials = materials, Scale = NodeScale(shotNode) });
                foreach (var kv in BwdSkeleton.DecodeShotNodes(shotBwd))
                {
                    // 0x1006c554 / 0x1005c2eb: the shot object's first polygon and first vertex give the light colour.
                    if (!shotNodes.TryGetValue(kv.Value, out var glowNode) || !polyById.TryGetValue(glowNode.PolyId, out var glowPoly)) continue;
                    var glowObjects = PolyModel.Decode(prj.Read(glowPoly));
                    if (glowObjects.Count == 0 || glowObjects[0].Polygons.Count == 0 || glowObjects[0].VertexA.Length == 0 ||
                        (glowObjects[0].Polygons[0].material & 0xf000) != 0x4000) continue;
                    var entry = palette[(int)glowObjects[0].VertexA[0] & 0xff];
                    // Palette.Expand gives (v << 2) | (v >> 4); the table holds v << 2.
                    shots.Glows.Add(new Mw2ShotModels.Glow { Type = kv.Key, Rgb = new Color32((byte)(entry.r & 0xfc), (byte)(entry.g & 0xfc), (byte)(entry.b & 0xfc), 255) });
                }

                // Explosion slots: XPLO chunks of the shot file (model, texture key, effect type), hidden until used.
                var effects = root.AddComponent<Mw2Effects>();
                effects.PlanetGravity = mission.Gravity;
                effects.SunFlashes = mission.ExplosionFlashes;
                effects.Palette = palette;
                effects.Defs = EffectTable.Get(System.IO.Path.GetDirectoryName(prj.FilePath));
                var effectRoot = new GameObject("effects").transform;
                effectRoot.SetParent(root.transform, false);
                foreach (var (node, key, effect) in BwdSkeleton.DecodeExplosions(shotBwd))
                {
                    GameObject model = null;
                    if (node >= 0 && shotNodes.TryGetValue(node, out var xploNode))
                    {
                        var parts = MeshesFor(xploNode.PolyId);
                        if (parts.Count > 0)
                        {
                            model = new GameObject($"effect {effect} {parts[0].mesh.name}");
                            model.transform.SetParent(effectRoot, false);
                            model.transform.localScale = NodeScale(xploNode);
                            foreach (var (mesh, materials) in parts)
                            {
                                var part = new GameObject(mesh.name);
                                part.transform.SetParent(model.transform, false);
                                part.AddComponent<MeshFilter>().sharedMesh = mesh;
                                var r = part.AddComponent<MeshRenderer>();
                                r.sharedMaterials = materials;
                                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                            }
                            model.SetActive(false);
                        }
                    }
                    // Model radius (+0x40): the farthest POLY vertex from the origin, times the node scale (sprites keep
                    // only their C point in the mesh, so the mesh bounds would understate it).
                    float radius = 0f;
                    if (model != null && polyById.TryGetValue(shotNodes[node].PolyId, out var xploPoly))
                        foreach (var obj in PolyModel.Decode(prj.Read(xploPoly)))
                            foreach (var v in obj.Vertices) radius = Mathf.Max(radius, v.magnitude * model.transform.localScale.x);
                    effects.Slots.Add(new Mw2Effects.Slot { Effect = effect, Key = key, Model = model, Radius = radius });
                }
            }

            root.AddComponent<Mw2LodSwitcher>();
            var director = root.AddComponent<Mw2MissionDirector>();
            director.Teams = mission.Teams;
            director.Player = player != null ? player.transform : null;
            director.GameFolder = gameFolder;
            director.MissionId = mission.Prefix;
            director.PlayerModel = mech;
            director.PlayerMek = mek;
            director.AiTables = AiTables.Load(prj);
            // FTBL formations in load order (the lance menu sets formation n of this table, 0x1002a29e).
            director.Formations = mission.Formations.Select(f => new Mw2MissionDirector.FormationInfo
            {
                Name = f.Name,
                Slots = f.Slots.Select(s => s.offset).ToArray()
            }).ToList();
            director.InitialFormationNames = (string[])mission.InitialFormationNames.Clone();
            foreach (var nav in mission.NavPoints)
                director.Navs.Add(new Mw2MissionDirector.NavInfo
                {
                    File = nav.File,
                    Name = nav.Name,
                    Position = PolyMeshBuilder.ToUnity(nav.Position),
                    Radius = nav.Radius,
                    Type = nav.Type,
                    Flags = nav.Flags,
                    Team = nav.Team
                });

            int units = SpawnUnits(prj, mission, palette, flatMaterial, lod, root.transform, dir, director, weaponTable, textures, sink, out var unitNotes,
                                   careerMek, player != null ? player.transform : null);
            // Animated texture keys (after every model has asked for its textures).
            root.AddComponent<Mw2TextureAnimator>().Rows = textures.AnimatedRows;
            sink.Flush();

            sink.Created(root);
            if (player != null) player.AddComponent<Autopilot.MechAutopilot>();
            if (player != null) sink.Created(player);
            sink.Select(player != null ? player : root);

            Debug.Log($"MW2: mission {mission.Prefix}: palette {palEntry.Name}, areas={mission.Areas.Count}, " +
                      $"objects placed={placed} (rubble hidden={skipped}, destructible={destructibles}), meshes={meshCache.Values.Sum(l => l.Count)}, textures={textures.FoundCount}/{textures.RequestedCount}, " +
                      $"nav points={mission.NavPoints.Count}, spawns={string.Join("; ", mission.Spawns.Select(s => $"{s.DisplayName} [{s.Model}/{s.Loadout}]"))}" +
                      $", units spawned={units}" + (unitNotes.Count > 0 ? $" ({string.Join("; ", unitNotes)})" : "") +
                      (mission.Missing.Count > 0 ? $", missing files: {string.Join(", ", mission.Missing)}" : ""));
            return root;
        }

        /// <summary>Assembles a mech with animator and controller; meshes go to &lt;MW2Data&gt;/Mechs/&lt;mech&gt;_LOD&lt;n&gt;.</summary>
        public static GameObject BuildMech(PrjArchive prj, Color32[] palette, string mech, int lod, string mek, Material material,
                                           Mw2TextureBank textures = null, bool playable = true, Mw2AssetSink sink = null,
                                           byte[] mekData = null, int paint = -1)
        {
            sink ??= new Mw2AssetSink();
            string dir = $"Assets/MW2Data/Mechs/{mech}_LOD{lod}";
            sink.BeginFolder(dir);
            var root = MechAssembler.Build(prj, mech, palette, material, lod,
                (name, mesh) => sink.Save(mesh, $"{dir}/{name}.asset"), allLods: true,
                textureFor: textures == null ? null : textures.Get, paint: paint);
            sink.Flush();

            // MEK: i32 tonnage, i32 walking MP, ... (mech loader 0x1002f9e0: max speed = MEK[1] * 105.49).
            int walkingMp = -1;
            int jumpMp = 0;
            // Mech_LoadMek 0x1002f9e0: a shell launch can supply the written MEK rather than an archive template.
            if (mekData == null && prj.TryFind("MEK", mek, out var mekEntry)) mekData = prj.Read(mekEntry);
            if (mekData != null)
            {
                walkingMp = System.BitConverter.ToInt32(mekData, 4);
                jumpMp = System.BitConverter.ToInt32(mekData, 8);
            }
            if (playable && root.GetComponent<Mw2MechAnimator>() != null)
            {
                var controller = root.AddComponent<Mw2MechController>();
                // +0xe1c = MGEO[6] (Mgeo_Load 0x100242a0); a model without MGEO keeps the size estimate.
                float mgeoRadius = BwdSkeleton.UnitRadius(prj, mech);
                var size = MechBounds(root).size;
                if (mgeoRadius > 0f) controller.CollisionRadius = mgeoRadius;
                else if (size.x > 0) controller.CollisionRadius = 0.25f * (size.x + size.z);
                if (walkingMp >= 0) controller.WalkingMp = walkingMp;
                else Debug.LogWarning($"MW2: MEK '{mek}' not found, using {controller.WalkingMp} MP");
                controller.JumpMp = Mathf.Max(0, jumpMp);
            }
            return root;
        }

        public static Bounds MechBounds(GameObject root)
        {
            var bounds = new Bounds(root.transform.position, Vector3.zero);
            foreach (var r in root.GetComponentsInChildren<Renderer>()) if (r.enabled) bounds.Encapsulate(r.bounds);   // disabled renderers report empty bounds at the origin
            return bounds;
        }

        /// <summary>
        /// Creates the OBJ node hierarchy. Destruction debris (POLY names starting with SCH) is skipped: it is shown
        /// only after the parent object is destroyed (judged from the models, the GT logic is not ported yet).
        /// </summary>
        /// <summary>OBJ node scale (+6, i32 x3): used by the shot file (explosions x2..x10, projectiles); 0 counts as 1.</summary>
        static Vector3 NodeScale(BwdNode n) =>
            new Vector3(n.Scale.x == 0 ? 1 : n.Scale.x, n.Scale.y == 0 ? 1 : n.Scale.y, n.Scale.z == 0 ? 1 : n.Scale.z);

        /// <summary>FUN_1000df2a 0x1000df2a / FUN_10004631 0x10004631: bind class-1 material tasks
        /// to the vertex spans of their POLY faces. All mission class-1 targets are flat faces; the vertex
        /// count check leaves a task unattached if a mesh's layout differs from its POLY face order.</summary>
        static void AttachMaterialTasks(PrjArchive prj, Dictionary<int, PrjArchive.Entry> polyById,
            List<BwdNode> nodes, Dictionary<int, Transform> transforms, Color32[] palette, byte[] data)
        {
            foreach (var (node, period, materials) in BwdSkeleton.DecodeMaterialTasks(data))
            {
                var source = nodes.Find(n => n.Node == node);
                if (source == null || !polyById.TryGetValue(source.PolyId, out var entry) ||
                    !transforms.TryGetValue(node, out var target)) continue;
                var objects = PolyModel.Decode(prj.Read(entry));
                for (int objectIndex = 0; objectIndex < objects.Count; objectIndex++)
                {
                    var filter = objectIndex == 0 ? target.GetComponent<MeshFilter>()
                        : target.Find($"{entry.Name}_{objectIndex}")?.GetComponent<MeshFilter>();
                    if (filter == null) continue;
                    var obj = objects[objectIndex];
                    var starts = new int[obj.Polygons.Count];
                    var lengths = new int[obj.Polygons.Count];
                    int count = 0;
                    bool flat = true;
                    for (int face = 0; face < obj.Polygons.Count; face++)
                    {
                        var polygon = obj.Polygons[face];
                        starts[face] = count;
                        lengths[face] = polygon.indices.Length;
                        if ((polygon.indices.Length != 1 && polygon.indices.Length < 3) ||
                            polygon.indices.Any(i => i >= obj.Vertices.Length) ||
                            polygon.material >> 12 == 3 || PolyMeshBuilder.TextureKey(polygon.material) >= 0)
                        {
                            flat = false;
                            break;
                        }
                        count += lengths[face];
                    }
                    if (!flat || filter.sharedMesh.vertexCount != count) continue;
                    var task = filter.gameObject.AddComponent<Mw2MaterialTask>();
                    task.PeriodTicks = period;
                    task.Materials = materials;
                    task.MaterialColors = materials.Select(m => palette[PolyMeshBuilder.FlatColorIndex(m)]).ToArray();
                    task.FaceStarts = starts;
                    task.FaceLengths = lengths;
                }
            }
        }

        static (int placed, int skipped) PlaceNodes(List<BwdNode> nodes, Transform parent,
            Dictionary<int, PrjArchive.Entry> polyById, System.Func<int, List<(Mesh mesh, Material[] materials)>> meshesFor,
            Dictionary<int, Transform> transforms = null)
        {
            transforms ??= new Dictionary<int, Transform>();
            int placed = 0, skipped = 0;
            foreach (var n in nodes)
            {
                // World_LoadObj 0x10070785 builds the POLY of every area / sky OBJ whatever its flags (they only go to the
                // object's +2): 0x800 marks the flat ground pieces, e.g. the TL1CBLOK city blocks with the streets of
                // BET2ARE4 (BET2 and BET3), 0x10 smoke and fire objects.
                bool hasPoly = polyById.TryGetValue(n.PolyId, out var entry);
                string name = hasPoly ? polyById[n.PolyId].Name : $"node_{n.Node}";
                var go = new GameObject(name);
                transforms[n.Node] = go.transform;
                if (!hasPoly) continue;
                // Rubble (SCH*) appears only once its GT object is destroyed: built, but hidden.
                bool rubble = name.StartsWith("SCH");
                if (rubble) skipped++;
                var meshes = meshesFor(n.PolyId);
                for (int k = 0; k < meshes.Count; k++)
                {
                    var target = go;
                    if (k > 0)
                    {
                        target = new GameObject($"{name}_{k}");
                        target.transform.SetParent(go.transform, false);
                    }
                    target.AddComponent<MeshFilter>().sharedMesh = meshes[k].mesh;
                    var renderer = target.AddComponent<MeshRenderer>();
                    renderer.sharedMaterials = meshes[k].materials;
                    renderer.enabled = !rubble;
                    var imageNode = target.AddComponent<Mw2ImageNode>();
                    imageNode.Flags = (int)(n.Flags & 0xffff); imageNode.Apply();
                }
                if (!rubble) placed++;
            }
            foreach (var n in nodes)
            {
                var t = transforms[n.Node];
                t.SetParent(n.Parent >= 0 && n.Parent != n.Node && transforms.TryGetValue(n.Parent, out var p) ? p : parent, false);
                t.localPosition = PolyMeshBuilder.ToUnity(n.Position);
                t.localRotation = Quaternion.Euler(n.RotationDegrees.x, n.RotationDegrees.y, n.RotationDegrees.z);
            }
            foreach (var n in nodes) AddCollider(transforms[n.Node], n.CollisionType);
            return (placed, skipped);
        }

        /// <summary>
        /// Places every non-player GPS unit: the team leader at the team's start point, the others in formation slots
        /// rotated by the start heading (0x1002a44b / 0x1002a57c). STAR/GRP selects each team's initial FTBL formation.
        /// </summary>
        static int SpawnUnits(PrjArchive prj, Mission mission, Color32[] palette, Material material, int lod, Transform parent, string dir,
                              Mw2MissionDirector director, List<WeaponDef> weaponTable, Mw2TextureBank textures, Mw2AssetSink sink,
                              out List<string> notes, System.Func<string, byte[]> careerMek = null, Transform playerUnit = null)
        {
            notes = new List<string>();
            var unitsRoot = new GameObject("Units");
            unitsRoot.transform.SetParent(parent, false);
            string unitDir = $"{dir}/Units";
            sink.EnsureFolder(unitDir);
            int spawned = 0, serial = 0;

            foreach (var team in mission.Spawns.Where(sp => !sp.Player).GroupBy(sp => sp.Team))
            {
                string formationName = team.Key < mission.InitialFormationNames.Length ? mission.InitialFormationNames[team.Key] : null;
                var formation = mission.Formations.FirstOrDefault(f => string.Equals(f.Name, formationName, System.StringComparison.OrdinalIgnoreCase))
                    ?? mission.Formations.FirstOrDefault();
                if (!mission.TryGetTeamStart(team.Key, out var start))
                {
                    notes.Add($"team {team.Key}: no start point");
                    continue;
                }
                var members = team.OrderByDescending(sp => sp.Leader).ToList();
                Transform leader = null;
                // userstar's wingmen: the player leads the team from slot 0 and they take the following slots.
                int first = 0;
                if (playerUnit != null && mission.Spawns.Any(sp => sp.Player && sp.Team == team.Key))
                {
                    leader = playerUnit;
                    first = 1;
                }
                for (int k = 0; k < members.Count; k++)
                {
                    var unit = members[k];
                    int slot = k + first;
                    Vector3 offset = Vector3.zero;
                    float slotHeading = slot == 0 ? start.Heading : 0f;
                    if (formation != null && formation.Slots.Count > 0)
                    {
                        offset = formation.InitialOffset(slot);
                        slotHeading = formation.InitialHeading(slot, start.Heading);
                    }

                    double h = start.Heading * System.Math.PI / 180.0;
                    float cos = (float)System.Math.Cos(h), sin = (float)System.Math.Sin(h);
                    var world = start.Position + new Vector3(offset.x * cos + offset.z * sin, 0, -offset.x * sin + offset.z * cos);

                    GameObject go;
                    try
                    {
                        int reps = BwdSkeleton.Decode(prj.Read("BWD", unit.Model)).Count;
                        int unitLod = Mathf.Clamp(lod, 1, Mathf.Max(1, reps - 1));
                        int id = serial++;
                        go = MechAssembler.Build(prj, unit.Model, palette, material, unitLod,
                            (name, mesh) => sink.Save(mesh, $"{unitDir}/{id}_{name}.asset"), allLods: true,
                            textureFor: textures.Get, paint: mission.TeamPaint[unit.Team & 15]);
                    }
                    catch (System.Exception e)
                    {
                        notes.Add($"{unit.DisplayName} [{unit.Model}]: {e.Message}");
                        continue;
                    }
                    go.name = $"{unit.DisplayName} [{unit.Model}/{unit.Loadout}] team {unit.Team}{(slot == 0 ? " leader" : "")}";
                    go.transform.SetParent(unitsRoot.transform, false);
                    go.transform.position = PolyMeshBuilder.ToUnity(world);
                    go.transform.rotation = Quaternion.Euler(0, slotHeading, 0);
                    bool walkingMech = go.GetComponent<Mw2MechAnimator>() != null;
                    PrjArchive.Entry? mek = prj.TryFind("MEK", unit.Loadout, out var mekEntry) ? mekEntry : (PrjArchive.Entry?)null;
                    // A wingman of the campaign flies the MEK the shell wrote for him (Mech_LoadMek reads mek\<name>).
                    byte[] written = careerMek?.Invoke(unit.Loadout);
                    byte[] mekBytes = written ?? (mek != null ? prj.Read(mek.Value) : null);
                    if (walkingMech)
                    {
                        var controller = go.AddComponent<Mw2MechController>();
                        controller.KeyboardControl = false;
                        controller.FollowCamera = false;
                        controller.PlanetGravity = mission.Gravity;
                        if (mekBytes != null)
                        {
                            controller.WalkingMp = System.BitConverter.ToInt32(mekBytes, 4);
                            controller.JumpMp = System.Math.Max(0, System.BitConverter.ToInt32(mekBytes, 8));
                        }
                        var bounds = new Bounds(go.transform.position, Vector3.zero);
                        foreach (var r in go.GetComponentsInChildren<Renderer>()) if (r.enabled) bounds.Encapsulate(r.bounds);   // disabled renderers report empty bounds at the origin
                        // +0xe1c = MGEO[6] (Mgeo_Load); a model without MGEO keeps the size estimate.
                        float mgeoRadius = BwdSkeleton.UnitRadius(prj, unit.Model);
                        if (mgeoRadius > 0f) controller.CollisionRadius = mgeoRadius;
                        else if (bounds.size.x > 0) controller.CollisionRadius = 0.25f * (bounds.size.x + bounds.size.z);
                    }
                    if (mekBytes != null)
                    {
                        // World_LoadChunks 0x10070e92 assigns a GP to mechs, vehicles, aircraft and targets;
                        // Mech_Destroy 0x10035ef6 handles all of them. Their shipped MEKs carry damage sections.
                        // Side of the unit's team from STAR (FUN_1002a7c1 0x1002a7c1 -> DAT_1024f5d4 + team*0x3c).
                        int side = mission.SideOf(unit.Team);
                        Mw2MechCombat.Setup(go, MekLoadout.Decode(mekBytes), weaponTable, side, false, unit.DisplayName);
                        var combat = go.GetComponent<Mw2MechCombat>();
                        combat.MekBytes = mekBytes;
                        combat.MekName = unit.Loadout;
                        combat.CareerUnit = written != null;    // Units_WriteMeks 0x100598b1 writes it back after the mission
                        combat.SourceFile = unit.File;
                        combat.ObjectiveMask = unit.ObjectiveMask;
                        combat.UnitFlags = unit.Flags;
                        combat.InspectText = unit.InspectText;
                        combat.GamepieceIndex = mission.Spawns.IndexOf(unit);
                        combat.Team = unit.Team;
                        combat.GamepieceType = BwdSkeleton.DecodeGamepieceType(prj.Read("BWD", unit.Model));
                        combat.OriginHeight = BwdSkeleton.OriginHeight(prj, unit.Model);
                        combat.ModelRadius = BwdSkeleton.UnitRadius(prj, unit.Model);
                        combat.PlanetTemperature = mission.Temperature;
                        combat.HostileAtmosphere = mission.HostileAtmosphere;
                    }
                    // Network players are other machines' mechs: FirstExternalCtrl 0x10020739 gives them no pilot.
                    if (walkingMech && !Mw2MechCombat.NetGame)
                    {
                        var ai = go.AddComponent<Mw2MechAI>();
                        ai.Director = director;
                        ai.Team = unit.Team;
                        ai.Leader = leader;
                        ai.FormationOffset = offset;
                        ai.PilotStyleId = Mathf.Clamp(unit.AiStyle, 0, AiTables.Styles.Length - 1);
                        // 0x10007f35 / FUN_1006280a: skill 0 -> 1, clamped 1..6; FUN_100081c1: temper 0 / 1 -> +0x1ac = 33,
                        // 2 -> 100; other values (4 in two GPS) leave the field at its zero start.
                        ai.PilotSkill = Mathf.Clamp(unit.PilotSkill == 0 ? 1 : unit.PilotSkill, 1, 6);
                        ai.FleePercent = unit.PilotTemper == 2 ? 100 : unit.PilotTemper == 0 || unit.PilotTemper == 1 ? 33 : 0;
                        // 0x10007f35: +0x186 = parameter 0 (0 -> 1), then + 1 after the style is applied.
                        ai.FireChance = (unit.AiFireChance == 0 ? 1 : unit.AiFireChance) + 1;
                        ai.PilotVoiceId = unit.PilotVoice;
                        ai.NearRange = unit.AiNearRange == 0 ? 250 : unit.AiNearRange;
                        ai.FarRange = unit.AiFarRange == 0 ? 250 : unit.AiFarRange;
                        ai.DetectRange = unit.AiDetectRange == 0 ? 250 : unit.AiDetectRange;
                    }
                    if (leader == null) leader = go.transform;     // first unit that assembled leads
                    spawned++;
                }
            }
            return spawned;
        }

        /// <summary>
        /// Colliders by OBJ collision type (tests in the table at 0x1008faf0): 0/1 world-axis box around the object
        /// (0x10019d1f builds it from the geometry), 5 walkable surface and 7 polygon obstacle as mesh colliders,
        /// 6 sphere; the rest (-1/3/4) has no collision.
        /// </summary>
        static void AddCollider(Transform node, int type)
        {
            var filters = node.GetComponents<MeshFilter>().Concat(
                Enumerable.Range(0, node.childCount).Select(i => node.GetChild(i))
                          .Where(c => c.name.StartsWith(node.name + "_")).SelectMany(c => c.GetComponents<MeshFilter>())).ToList();
            if (filters.Count == 0) return;
            switch (type)
            {
                case 0:
                case 1:
                case 6:
                {
                    var bounds = filters[0].GetComponent<Renderer>().bounds;
                    foreach (var f in filters) bounds.Encapsulate(f.GetComponent<Renderer>().bounds);
                    var go = new GameObject("collider");
                    go.transform.SetParent(node, false);
                    go.transform.position = bounds.center;
                    go.transform.rotation = Quaternion.identity;
                    if (type == 6) go.AddComponent<SphereCollider>().radius = bounds.extents.magnitude;
                    else go.AddComponent<BoxCollider>().size = bounds.size;
                    var surface = go.AddComponent<Mw2Surface>(); surface.CollisionType = type;
                    if (type == 0 || type == 1)
                    {
                        surface.AxisBox = go.GetComponent<BoxCollider>();
                        surface.BoxSources = filters.Select(f => f.GetComponent<Renderer>()).ToArray();
                    }
                    break;
                }
                case 5:
                case 7:
                case 2:
                    foreach (var f in filters)
                    {
                        if (!PolyMeshBuilder.HasCollisionTriangles(f.sharedMesh)) continue;
                        f.gameObject.AddComponent<MeshCollider>().sharedMesh = f.sharedMesh;
                        f.gameObject.AddComponent<Mw2Surface>().CollisionType = type;
                    }
                    break;
            }
        }

        /// <summary>
        /// Ground at y = 0 (the engine falls back to height 0 where no surface polygon exists, 0x100186b1), textured like
        /// 0x1005521b: texture key 0x4a (+0x100, GRASS00 in BET1) with world-anchored UVs, one repeat per
        /// <paramref name="repeatMeters"/> (from SKYGND.PAR). Approximation: the engine draws a square of
        /// ground_max * 4 units around the camera; here it is a fixed 40 km square. Without a texture a neutral colour is used.
        /// </summary>
        static void CreateGround(Transform parent, Material material, string dir, Mw2AssetSink sink, float repeatMeters)
        {
            Color32 color = repeatMeters > 0 ? new Color32(255, 255, 255, 255) : new Color32(70, 70, 64, 255);
            const float half = 20000f;
            var corners = new[]
            {
                new Vector3(-half, -0.02f, -half), new Vector3(-half, -0.02f, half),
                new Vector3(half, -0.02f, half), new Vector3(half, -0.02f, -half)
            };
            var mesh = new Mesh { name = "Ground" };
            mesh.SetVertices(corners);
            mesh.SetColors(new List<Color32> { color, color, color, color });
            if (repeatMeters > 0)
                mesh.SetUVs(0, corners.Select(c => new Vector2(c.x / repeatMeters, c.z / repeatMeters)).ToList());
            mesh.SetTriangles(new[] { 0, 1, 2, 0, 2, 3 }, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            sink.Save(mesh, $"{dir}/Ground.asset");
            var go = new GameObject("Ground");
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = material;
        }

        /// <summary>Mechs face local +Z, so "behind" is -Z.</summary>
        static void PlaceCameraBehind(Transform mech, Mw2AssetSink sink)
        {
            var cam = Camera.main;
            if (cam == null) return;
            sink.Record(cam.transform);
            sink.Record(cam);
            cam.transform.position = mech.TransformPoint(new Vector3(0, 14f, -32f));     // mechs face local +Z
            cam.transform.LookAt(mech.position + Vector3.up * 7f);
            cam.farClipPlane = 30000f;
            cam.nearClipPlane = 0.5f;
        }
    }

    /// <summary>Resolves MAP1 texture keys for scenery and mech POLY materials, saving each CEL only once.</summary>
    public sealed class Mw2TextureBank
    {
        readonly Mw2AssetSink _sink;
        readonly PrjArchive _prj;
        readonly Mission _mission;
        readonly Color32[] _palette;
        readonly string _dir;
        readonly Dictionary<int, PrjArchive.Entry> _celById;
        readonly Dictionary<int, (PolyMeshBuilder.TextureInfo info, Material material)?> _byKey = new();
        readonly Dictionary<int, (PolyMeshBuilder.TextureInfo info, Material material)> _byCel = new();

        public int RequestedCount => _byKey.Count;
        public int FoundCount => _byKey.Values.Count(value => value != null);

        readonly Texture2D _paletteTexture;

        /// <param name="paletteTexture">The mission's palette (256 × 1): the CELs of the world palette are kept as indices
        /// and coloured by it, or by the live palette of Mw2PaletteFade; null keeps them as RGBA.</param>
        public Mw2TextureBank(PrjArchive prj, Mission mission, Color32[] palette, string dir, Mw2AssetSink sink = null,
                              Texture2D paletteTexture = null)
        {
            _paletteTexture = paletteTexture;
            _sink = sink ?? new Mw2AssetSink();
            _prj = prj;
            _mission = mission;
            _palette = palette;
            _dir = dir;
            _celById = prj.Entries("CEL").ToDictionary(e => e.Id);
        }

        public (PolyMeshBuilder.TextureInfo info, Material material)? Get(int key)
        {
            if (_byKey.TryGetValue(key, out var cached)) return cached;
            if ((key & PolyMeshBuilder.SpriteFlag) != 0) return _byKey[key] = Sprite(key & 0xFFFF);
            (PolyMeshBuilder.TextureInfo info, Material material)? result = null;
            if (_mission.TextureKeys.TryGetValue(key, out var frames) && frames.Count > 0 &&
                _celById.TryGetValue(frames[0], out var entry))
            {
                var loaded = Load(entry);
                result = loaded;
                if (frames.Count > 1)
                {
                    // Animated key (0x10001c74): its own material, whose texture Mw2TextureAnimator switches.
                    var mat = new Material(loaded.material) { name = $"{loaded.material.name}_K{key:x}" };
                    _sink.Save(mat, $"{_dir}/{entry.Name}_{entry.Id}_K{key:x}.mat");
                    _mission.TexturePeriods.TryGetValue(key, out int period);
                    _mission.TextureModes.TryGetValue(key, out int mode);
                    if (mode != 0 && period == 0) period = 0x2d;    // Texture_SetSpeed 0x10002094
                    AnimatedRows.Add(new Mw2TextureAnimator.Row
                    {
                        Key = key,
                        Materials = new[] { mat },
                        Frames = frames.Select(id => _celById.TryGetValue(id, out var e) ? (Texture2D)Load(e).material.mainTexture : null).ToArray(),
                        Period = period,
                        Mode = mode
                    });
                    result = (loaded.info, mat);
                }
            }
            _byKey[key] = result;
            return result;
        }

        /// <summary>Material of a type 3 sprite (MW2/Sprite) with the key's texture; animated with the key.</summary>
        (PolyMeshBuilder.TextureInfo info, Material material)? Sprite(int key)
        {
            var lit = Get(key);
            if (lit == null) return null;
            var mat = new Material(Shader.Find("MW2/Sprite")) { name = $"{lit.Value.material.name}_S{key:x}", mainTexture = lit.Value.material.mainTexture };
            mat.SetFloat("_Paletted", lit.Value.material.GetFloat("_Paletted"));
            mat.SetTexture("_Palette", lit.Value.material.GetTexture("_Palette"));
            _sink.Save(mat, $"{_dir}/{lit.Value.material.name}_S{key:x}.mat");
            var row = AnimatedRows.Find(r => r.Key == key);
            if (row != null) row.Materials = row.Materials.Append(mat).ToArray();
            return (lit.Value.info, mat);
        }

        /// <summary>Animation rows of the keys with several frames, for Mw2TextureAnimator.</summary>
        public readonly List<Mw2TextureAnimator.Row> AnimatedRows = new List<Mw2TextureAnimator.Row>();

        (PolyMeshBuilder.TextureInfo info, Material material) Load(PrjArchive.Entry entry)
        {
            if (_byCel.TryGetValue(entry.Id, out var loaded)) return loaded;
            var cel = Cel.Decode(_prj.Read(entry));
            // A CEL of the world palette is kept as indices and coloured by the live palette (Mw2Palette.cginc); one with
            // its own colours is an RGBA texture.
            bool paletted = !cel.HasOwnColors && _paletteTexture != null;
            var tex = paletted ? cel.ToIndexTexture(entry.Name) : cel.ToTexture(_palette, entry.Name, transparentIndex: -1);
            tex.wrapMode = TextureWrapMode.Repeat;
            string name = $"{entry.Name}_{entry.Id}";
            _sink.Save(tex, $"{_dir}/{name}.asset");
            // mercsw.dll 0x10001081 sends kind 0 through draw mode 2 and
            // positive kinds through mode 8, which enables alpha blending in zglide.dll.
            var shader = cel.Kind > 0 ? "MW2/TexturedAlphaLit" : "MW2/TexturedLit";
            var mat = new Material(Shader.Find(shader)) { name = entry.Name, mainTexture = tex };
            if (paletted)
            {
                mat.SetFloat("_Paletted", 1);
                mat.SetTexture("_Palette", _paletteTexture);
            }
            _sink.Save(mat, $"{_dir}/{name}.mat");
            loaded = (new PolyMeshBuilder.TextureInfo(cel.Width, cel.Height, cel.HasOwnColors ? cel.Kind : 0), mat);
            _byCel[entry.Id] = loaded;
            return loaded;
        }
    }
}
