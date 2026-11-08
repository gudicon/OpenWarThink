using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace MW2.Formats
{
    /// <summary>Builds a GameObject hierarchy for a mech from its BWD skeleton and POLY parts.</summary>
    public static class MechAssembler
    {
        /// <param name="lod">1 = most detailed representation, up to 5.</param>
        /// <param name="meshSink">Called for every created mesh (e.g. to save it as an asset).</param>
        /// <param name="allLods">
        /// Also builds every other representation on the same nodes (disabled) and records them in a Mw2LodGroup, so the
        /// LOD can be switched at run time like the engine does (0x1001f099); the player's cockpit view uses LOD5.
        /// </param>
        public static GameObject Build(PrjArchive prj, string mechName, Color32[] palette, Material material,
                                       int lod = 1, Action<string, Mesh> meshSink = null, bool allLods = false,
                                       Func<int, (PolyMeshBuilder.TextureInfo info, Material material)?> textureFor = null, int paint = -1)
        {
            var reps = BwdSkeleton.Decode(prj.Read("BWD", mechName));
            if (lod < 1 || lod >= reps.Count)
                throw new ArgumentOutOfRangeException(nameof(lod), $"{mechName} has {reps.Count - 1} representations");

            var polyById = new Dictionary<int, PrjArchive.Entry>();
            foreach (var e in prj.Entries("POLY")) polyById[e.Id] = e;

            var nodes = new List<BwdNode>(reps[0]);
            nodes.AddRange(reps[lod]);

            var builtMeshes = new Dictionary<(int poly, int item), (Mesh mesh, Material[] materials)>();
            Mesh BuildMesh(PolyObject obj, PrjArchive.Entry entry, int item, out Material[] materials)
            {
                if (builtMeshes.TryGetValue((entry.Id, item), out var built)) { materials = built.materials; return built.mesh; }
                var keys = new List<int>();
                // Use the archive's material indices; mission paint is applied only when supplied by STAR.
                var mesh = PolyMeshBuilder.Build(obj, palette, null,
                    key => textureFor?.Invoke(key)?.info, keys, paint, imageFlags: 0x100);
                materials = keys.Count == 0 ? new[] { material } :
                    keys.Select(key => key < 0 ? material : textureFor?.Invoke(key)?.material ?? material).ToArray();
                meshSink?.Invoke($"{entry.Name}_{entry.Id}_{item}", mesh);
                builtMeshes[(entry.Id, item)] = (mesh, materials);
                return mesh;
            }

            var root = new GameObject($"{mechName} (LOD{lod})");
            var rendererObjects = new Dictionary<Renderer, PolyObject>();
            var transforms = new Dictionary<int, Transform>();
            foreach (var n in nodes)
            {
                string name = n.HasGeometry && polyById.TryGetValue(n.PolyId, out var pe) ? pe.Name : $"mount_{n.Node}";
                var go = new GameObject(name);
                transforms[n.Node] = go.transform;
            }
            foreach (var n in nodes)
            {
                var t = transforms[n.Node];
                // WARHAWK's root node 0 names itself as its parent; Unity cannot parent a node to itself, so it stays under the
                // root (how the engine links such a node is not checked).
                t.SetParent(n.Parent >= 0 && n.Parent != n.Node && transforms.TryGetValue(n.Parent, out var p) ? p : root.transform, false);
                t.localPosition = PolyMeshBuilder.ToUnity(n.Position);
                // MW2 and Unity axes agree (PolyMeshBuilder.ToUnity). Mech rest poses have zero rotations,
                // so the axis order is not yet confirmed against the engine.
                t.localRotation = Quaternion.Euler(n.RotationDegrees.x, n.RotationDegrees.y, n.RotationDegrees.z);

                if (!n.HasGeometry || !polyById.TryGetValue(n.PolyId, out var entry)) continue;
                var objects = PolyModel.Decode(prj.Read(entry));
                for (int k = 0; k < objects.Count; k++)
                {
                    var mesh = BuildMesh(objects[k], entry, k, out var materials);
                    var target = t.gameObject;
                    if (k > 0)
                    {
                        target = new GameObject(objects[k].Name);
                        target.transform.SetParent(t, false);
                    }
                    target.AddComponent<MeshFilter>().sharedMesh = mesh;
                    var renderer = target.AddComponent<MeshRenderer>(); renderer.sharedMaterials = materials;
                    rendererObjects[renderer] = objects[k];
                }
            }

            var lods = new List<Mw2LodGroup.Level> { new Mw2LodGroup.Level { Lod = lod, Renderers = root.GetComponentsInChildren<Renderer>() } };
            // The cockpit shell (a node with flags 0xA0 and everything under it) for the U toggle (DAT_1008fcf4).
            var shell = new List<Renderer>();
            var nodeTransforms = new HashSet<Transform>(transforms.Values);
            foreach (int node in BwdSkeleton.ShellNodes(nodes))
            {
                if (!transforms.TryGetValue(node, out var st)) continue;
                if (st.TryGetComponent<Renderer>(out var sr)) shell.Add(sr);
                // Further objects of the same POLY hang under the node as plain children.
                foreach (Transform child in st)
                    if (!nodeTransforms.Contains(child) && child.TryGetComponent<Renderer>(out var cr)) shell.Add(cr);
            }
            // The cockpit shell is the frame seen on the screen: BET3 original struts are the palette row 4 at its brightest
            // shade 15 ((60,73,85) in the day palette PAL 113, bet3original.jpg), not the shade of the mission light (4).
            ShadeFull(shell);
            if (allLods)
            {
                // One skeleton for all representations, as in the original: Lod_Register 0x1001e729 creates a node record
                // (position +0x30..+0x38) only for the first REPR and later REPRs just add their POLY id; Lod_SwapModel 0x1001eb15
                // creates the scene node once at that position and swaps the model on it. The later REPRs' own OBJ
                // positions (e.g. 14 COMMANDO joints) are therefore never used.
                for (int extra = 1; extra < reps.Count; extra++)
                {
                    if (extra == lod) continue;
                    var renderers = new List<Renderer>();
                    var extraShell = BwdSkeleton.ShellNodes(reps[extra]);
                    foreach (var n in reps[extra])
                    {
                        if (!n.HasGeometry || !transforms.TryGetValue(n.Node, out var t) || !polyById.TryGetValue(n.PolyId, out var entry)) continue;
                        var objects = PolyModel.Decode(prj.Read(entry));
                        for (int k = 0; k < objects.Count; k++)
                        {
                            var mesh = BuildMesh(objects[k], entry, k, out var materials);
                            var go = new GameObject($"LOD{extra} {entry.Name}");
                            go.transform.SetParent(t, false);
                            go.AddComponent<MeshFilter>().sharedMesh = mesh;
                            var r = go.AddComponent<MeshRenderer>();
                            r.sharedMaterials = materials;
                            r.enabled = false;
                            rendererObjects[r] = objects[k];
                            renderers.Add(r);
                            if (extraShell.Contains(n.Node)) shell.Add(r);
                        }
                    }
                    lods.Add(new Mw2LodGroup.Level { Lod = extra, Renderers = renderers.ToArray() });
                }
                lods.Sort((x, y) => x.Lod.CompareTo(y.Lod));
                var group = root.AddComponent<Mw2LodGroup>();
                group.Levels = lods;
                group.Current = lod;
                ShadeFull(shell);
                group.Shell = shell;
            }

            byte[] bwd = prj.Read("BWD", mechName);
            // Mech_InitMovement100380d0 moves the GP model root by MGEO[0].
            // Type2/8 controllers retain a foot-space transform, while their
            // aircraft/vehicle skeleton is local to the GP origin. Carry its
            // render, eye, muzzle and collision nodes at that same origin.
            // SPA1 SHILONEA/CHIPPEWB/STINGRAB use213000 model units, not45m.
            int gpType = BwdSkeleton.DecodeGamepieceType(bwd);
            if (gpType == 2 || gpType == 8)
            {
                float height = BwdSkeleton.OriginHeight(prj, mechName);
                foreach (var t in transforms.Values)
                    if (t.parent == root.transform) t.localPosition += Vector3.up * height;
            }
            // TSK class 4: looping sounds at nodes (aircraft rotors, 0x1000e68f).
            foreach (var (node, range, sound, _) in BwdSkeleton.DecodeSoundTasks(bwd))
                if (transforms.TryGetValue(node, out var soundNode))
                {
                    var ambient = soundNode.gameObject.AddComponent<Mw2AmbientSound>();
                    ambient.Sound = sound;
                    ambient.RangeUnits = range;
                }
            var (animName, tasks) = BwdSkeleton.DecodeAnimation(bwd);
            if (animName != null && tasks.Count > 0 && prj.TryFind("ANIM", animName, out var animEntry))
            {
                var animator = root.AddComponent<Mw2MechAnimator>();
                animator.Setup(animName, prj.Read(animEntry), tasks, transforms);
                // ASND: the unit's own step sound (the player's mech keeps it, 0x10070e92 / 0x10028a20).
                var (stepId, stepName) = BwdSkeleton.DecodeStepSound(bwd);
                if (stepId == -1 && !string.IsNullOrEmpty(stepName) && prj.TryFind("SNDS", stepName, out var stepEntry)) stepId = stepEntry.Id;
                animator.StepSound = stepId;
            }

            // Torso twist: the engine sets an absolute yaw on the first THNG node every frame. A pivot is inserted above
            // that node so the twist does not fight the ANIM tracks that also rotate it (torso sway).
            var things = BwdSkeleton.DecodeThings(bwd);
            if (things.Count > 0 && transforms.TryGetValue(things[0], out var torso))
            {
                var pivot = new GameObject("torso twist").transform;
                pivot.SetParent(torso.parent, false);
                pivot.localPosition = torso.localPosition;
                pivot.localRotation = Quaternion.identity;
                torso.SetParent(pivot, true);
                var twist = root.AddComponent<Mw2TorsoTwist>();
                twist.Pivot = pivot;
                if (things.Count > 1 && transforms.TryGetValue(things[1], out var pitchNode)) twist.SetPitchNode(pitchNode);
                string geometry = BwdSkeleton.DecodeGeometryName(bwd);
                if (geometry != null && prj.TryFind("MGEO", geometry, out var mgeo))
                {
                    byte[] g = prj.Read(mgeo);
                    if (g.Length >= 24) twist.Limit = BitConverter.ToInt32(g, 20) / 65536f;
                }
            }
            int eye = BwdSkeleton.DecodeEye(bwd);
            if (eye >= 0 && transforms.TryGetValue(eye, out var eyeNode))
            {
                var cockpit = root.AddComponent<Mw2Cockpit>();
                cockpit.Eye = eyeNode;
                // World_LoadChunks / Mgeo_Load 0x100242a0: second MGEO word -> mech +0xe04.
                string eyeGeometry = BwdSkeleton.DecodeGeometryName(bwd);
                if (eyeGeometry != null && prj.TryFind("MGEO", eyeGeometry, out var eyeMgeo))
                {
                    byte[] g = prj.Read(eyeMgeo);
                    if (g.Length >= 8) cockpit.EyeVerticalOffset = BitConverter.ToInt32(g, 4) * PolyModel.UnitsToMeters;
                }
            }
            // Hit parts: Lod_SwapModel 1001eb15 sets type 6; 10018ad5 -> 100047ff / 10018ee2
            // uses model spheres and the OBJL section. Retain mesh colliders as metadata/debris handles;
            // FirstHit uses source-vertex sphere bounds for the current representation. Original
            // Lod_SetForGamepiece 1001e9df replaces all attached part models; debris keeps its selected model.
            root.TryGetComponent<Mw2LodGroup>(out var lodGroup);
            var partRenderers = rendererObjects.Keys.ToArray();
            foreach (var r in partRenderers)
            {
                if (r == null || !r.TryGetComponent<MeshFilter>(out var mf) || !PolyMeshBuilder.HasCollisionTriangles(mf.sharedMesh)) continue;
                var c = r.gameObject.AddComponent<MeshCollider>();
                c.sharedMesh = mf.sharedMesh;
                c.isTrigger = false;
                c.convex = false;
                var part = r.gameObject.AddComponent<Mw2HitPart>();
                if (rendererObjects.TryGetValue(r, out var model))
                {
                    // Model_ComputeBounds 0x10003efa and part collision type 6 in Lod_SwapModel 0x1001eb15.
                    var bounds = PolyModel.SphereBounds(model);
                    part.LocalCentre = bounds.center; part.ModelRadius = bounds.radius;
                    part.HitCollider = c; part.SphereReady = true;
                    part.LodGroup = lodGroup;
                    part.Representation = lodGroup != null ? lodGroup.Levels.First(l => Array.IndexOf(l.Renderers, r) >= 0).Lod : lod;
                }
                r.gameObject.layer = Mw2HitPart.Layer;
            }

            // A kinematic body makes the moving part colliders one compound: without it Unity treats each animated part as
            // a static collider and rebuilds its physics structure whenever a part moves.
            if (!root.TryGetComponent<Rigidbody>(out var body)) body = root.AddComponent<Rigidbody>();
            body.isKinematic = true;
            body.useGravity = false;

            var hitSections = BwdSkeleton.DecodeHitSections(bwd);
            if (hitSections.Count > 0)
            {
                var map = root.AddComponent<Mw2HitMap>();
                foreach (var (node, section) in hitSections)
                    if (section >= 0 && section < 8 && transforms.TryGetValue(node, out var t)) { map.Nodes.Add(t); map.Sections.Add(section); }
            }
            var muzzles = BwdSkeleton.DecodeMuzzles(bwd);
            if (muzzles.Count > 0)
            {
                var mounts = root.AddComponent<Mw2WeaponMounts>();
                foreach (var (node, section) in muzzles)
                    if (section >= 0 && section < mounts.Sections.Length && transforms.TryGetValue(node, out var muzzle))
                        mounts.Sections[section] = muzzle;
            }
            return root;
        }

        static readonly int ShadeFullId = Shader.PropertyToID("_ShadeFull");

        static void ShadeFull(List<Renderer> shell)
        {
            var block = new MaterialPropertyBlock();
            block.SetFloat(ShadeFullId, 1f);
            foreach (var r in shell) if (r != null) r.SetPropertyBlock(block);
        }
    }
}
