using UnityEngine;

namespace MW2.Autopilot
{
    /// <summary>Filters the live registries through player target rules/visibility. Never reads enemy weapons, HP or AI orders.</summary>
    public sealed class AutopilotSensors
    {
        public Mw2MechCombat Unit;
        public Mw2Destructible Structure;
        public bool Clear;
        public Vector3 Point, ObservedVelocity;
        public float Distance;
        public bool InspectTarget, Attackable;
        public bool AllowVisualUnits, ManualVisual;
        public bool PrioritizeDefense;
        public int AimSection = -1, PreferredGamepiece = -1; // Explicit observed target/visible-body-part player orders.
        public int[] AvoidGamepieces = System.Array.Empty<int>();
        public bool PreferVisibleHead; // Explicit tester aiming option; rendered anatomy and ordinary shot rays only.
        public bool PreferMandatoryStructures; // Explicit raid strategy: finish a public reachable installation first.
        public string AvoidUnitName; // Opt-in tester policy using the selectable unit's displayed name only.
        public string PreferredTargetName; // Explicit player target-caption preference; does not grant eligibility.
        public string[] ManualStructureNames = System.Array.Empty<string>(); // Explicit free-aim building captions, not radar selection.
        bool ManualCaption(string name) => System.Array.Exists(ManualStructureNames,
            caption => !string.IsNullOrEmpty(caption) && (name ?? "").IndexOf(caption, System.StringComparison.OrdinalIgnoreCase) >= 0);
        internal static bool TryManualStructure(Mw2MechCombat self, Mw2Destructible target, out Vector3 point)
        {
            point = Vector3.zero;
            if (target == null || target.Destroyed || (target.Flags & 0x800) != 0) return false;
            self.WeaponAimRay(self.GetComponent<Mw2TorsoTwist>(), out var eye, out var look);
            foreach (var renderer in target.GetComponentsInChildren<Renderer>())
            {
                if (!renderer.enabled || !renderer.gameObject.activeInHierarchy) continue;
                var bounds = renderer.bounds;
                foreach (var candidate in new[] { bounds.center, bounds.ClosestPoint(eye) })
                {
                    var delta = candidate - eye; float distance = delta.magnitude;
                    if (distance < 0.01f || distance > Mw2MechCombat.TargetRangeUnits * Formats.PolyModel.UnitsToMeters ||
                        Vector3.Dot(look, delta / distance) < 0.34202015f) continue;
                    if (self.FirstHit(eye, delta / distance, distance + 0.5f, out var hit) &&
                        hit.collider != null && hit.collider.GetComponentInParent<Mw2Destructible>() == target)
                    { point = hit.point; return true; }
                }
            }
            return false;
        }
        bool Prefers(string name) => !string.IsNullOrEmpty(PreferredTargetName) &&
            (name ?? "").IndexOf(PreferredTargetName, System.StringComparison.OrdinalIgnoreCase) >= 0;
        public bool PreferFiringAngles; // Opt-in Mobile tester policy, visible geometry only.
        Component _last;
        Vector3 _previous;
        float _stamp;

        struct StructureChoice
        {
            public Mw2Destructible Target;
            public float Score;
            public bool Clear;
            public void Consider(Mw2Destructible target, float score, bool clear)
            { if (Target == null || score > Score) { Target = target; Score = score; Clear = clear; } }
        }

        public bool HasTarget => Unit != null || Structure != null;
        public string TargetName => Unit != null ? Unit.DisplayName : Structure != null ? Structure.DisplayName : "none";
        // Target_Validate10067668 measures inspection from the selected GP/GT
        // position; the rendered aim centre can be offset from that position.
        public Vector3 InspectionPoint => Unit != null ? Unit.Origin :
            Structure != null ? Structure.transform.position : Point;

        public bool Line(Mw2MechCombat self, Vector3 point, Mw2MechCombat unit = null, Mw2Destructible structure = null)
        {
            var from = self.Origin;
            var delta = point - from;
            if (delta.sqrMagnitude < 0.01f) return true;
            if (!self.FirstHit(from, delta.normalized, delta.magnitude + 0.5f, out var h)) return true;
            if (unit != null && h.collider.GetComponentInParent<Mw2MechCombat>() == unit) return true;
            return structure != null && h.collider.GetComponentInParent<Mw2Destructible>() == structure;
        }

        public void Scan(Mw2MechCombat self, Mw2MissionDirector director, AutopilotObjectives objectives, float now,
                         bool discretionaryStructures = true, bool preferCombatUnits = false)
        {
            var previousUnit = Unit; var previousStructure = Structure;
            Unit = null; Structure = null; ManualVisual = false;
            Vector3 visualPoint = Vector3.zero;
            float best = float.NegativeInfinity;
            foreach (var u in Mw2MechCombat.All)
            {
                if (u == null || u == self || u.Destroyed || System.Array.IndexOf(AvoidGamepieces, u.GamepieceIndex) >= 0) continue;
                bool selectableUnit = self.PilotCanSelect(u), visual = false;
                Vector3 candidatePoint = u.ModelCentre;
                if (!selectableUnit)
                {
                    // Guard may fire manually at solid geometry in its actual
                    // view. This never grants a radar target or missile lock.
                    if (!AllowVisualUnits || !TryVisualUnit(self, u, out candidatePoint)) continue;
                    visual = true;
                }
                bool inspect = objectives.IsInspectTarget(director, u.SourceFile, u.ObjectiveMask);
                bool destroy = objectives.IsDestroyTarget(director, u.SourceFile, u.ObjectiveMask);
                if (!inspect && !destroy && !string.IsNullOrEmpty(AvoidUnitName) &&
                    (u.DisplayName ?? "").IndexOf(AvoidUnitName, System.StringComparison.OrdinalIgnoreCase) >= 0) continue;
                // A public recon group can stay active after this member was
                // identified. Move to another member instead of inspecting it forever.
                if (inspect && !destroy && (u.IdentifiedTeams & (1 << self.Team)) != 0) continue;
                if (u.Side != 1 && !inspect && !destroy) continue;
                bool clear = visual || Line(self, candidatePoint, u);
                if (PreferVisibleHead && TryHeadAim(self, u, out var headPoint))
                { candidatePoint = headPoint; clear = true; }
                // A selectable mech can expose a real part above an obstruction
                // while its aggregate bounds centre is hidden. Mobile tester
                // uses the same eye-cone/first-hit observation as manual Guard.
                if (!clear && PreferFiringAngles && TryVisualUnit(self, u, out var visiblePart))
                { candidatePoint = visiblePart; clear = true; }
                float range = Vector3.Distance(self.Origin, candidatePoint);
                float score = AutopilotPolicy.TargetScore(range, clear,
                    destroy || inspect, u == previousUnit);
                if (Prefers(u.DisplayName)) score += 1000f;
                if (PreferFiringAngles)
                {
                    var delta = u.ModelCentre - self.Origin;
                    float elevation = Mathf.Abs(Mathf.Atan2(delta.y, new Vector2(delta.x, delta.z).magnitude) * Mathf.Rad2Deg);
                    // A plane directly overhead exceeds ordinary player pitch.
                    // Prefer another visible target that can actually be engaged.
                    if (elevation > 55f) score -= 1000f;
                    if (u == previousUnit) score -= 70f;
                    if (range > OwnWeaponRange(self)) score -= 150f;
                    if (PrioritizeDefense && objectives.Defending)
                    {
                        float assetDistance = Vector3.Distance(u.ModelCentre, objectives.DefensePoint);
                        foreach (var asset in objectives.DefenseAssets)
                            assetDistance = Mathf.Min(assetDistance, Vector3.Distance(u.ModelCentre, asset));
                        score -= assetDistance * 0.6f;
                    }
                }
                if (u.GamepieceIndex == PreferredGamepiece) score += 2000f;
                if (AimSection >= 0)
                { clear = TrySectionAim(self, u, AimSection, out var sectionPoint); if (clear) candidatePoint = sectionPoint; }
                if (score > best) { best = score; Unit = u; Structure = null; Clear = clear; ManualVisual = visual; visualPoint = candidatePoint; }
            }
            var objectiveStructure = new StructureChoice();
            var unknownStructure = new StructureChoice();
            var knownStructure = new StructureChoice();
            var preferredStructure = new StructureChoice();
            var manualPoints = new System.Collections.Generic.Dictionary<Mw2Destructible, Vector3>();
            foreach (var s in Mw2Destructible.All)
            {
                if (s == null || s.Destroyed || (s.Flags & 0x800) != 0) continue;
                bool destroy = objectives.IsDestroyTarget(director, s.File, s.ObjectiveMask);
                bool inspect = objectives.IsInspectTarget(director, s.File, s.ObjectiveMask);
                if (inspect && !destroy && (s.IdentifiedTeams & (1 << self.Team)) != 0) continue;
                bool objective = destroy || inspect;
                Vector3 manualPoint = Vector3.zero;
                bool manual = ManualCaption(s.DisplayName) && TryManualStructure(self, s, out manualPoint);
                if (manual) manualPoints[s] = manualPoint;
                // Explicit CombatFirst testing tactic: skip optional structures,
                // including IVA1's Blade power-facility bonus. Only active public
                // mandatory bindings qualify; no hidden orders/health are read.
                if (!discretionaryStructures && !Prefers(s.DisplayName) && !manual &&
                    !objectives.IsDestroyTarget(director, s.File, s.ObjectiveMask, true) &&
                    !objectives.IsInspectTarget(director, s.File, s.ObjectiveMask, true)) continue;
                bool selectable = self.PilotCanSelect(s);
                // Parked aircraft are hostile GTs, not GP units. A normal player can
                // select and attack them without resolving the hidden MTBL group.
                if (!objective && !manual && (s.Side != 1 || !selectable)) continue;
                var p = Mw2MechCombat.StructureCentre(s);
                Vector3 visibleSurface = Vector3.zero;
                bool eyeSurface = PrioritizeDefense && objective && TryStructureAim(self, s, out visibleSurface, true);
                if (eyeSurface) p = visibleSurface;
                if (manual) p = manualPoint;
                float range = Vector3.Distance(self.Origin, p);
                bool clear = manual || eyeSurface || Line(self, p, null, s);
                // A visible objective can be shot manually even if it is absent from the radar/target ring.
                if (range > Mw2MechCombat.TargetRangeUnits * Formats.PolyModel.UnitsToMeters || (!clear && !selectable)) continue;
                float score = AutopilotPolicy.TargetScore(range, clear, objective, s == previousStructure) + (objective ? 30f : 0f);
                if (manual) score += 250f;
                if (Prefers(s.DisplayName)) { score += 1000f; preferredStructure.Consider(s, score, clear); }
                if (objective) objectiveStructure.Consider(s, score, clear);
                else if ((s.IdentifiedTeams & (1 << self.Team)) == 0) unknownStructure.Consider(s, score, clear);
                else knownStructure.Consider(s, score, clear);
            }
            // Survey currently selectable hostile GTs before discretionary fire: splash
            // from an identified truck must not destroy its still-unidentified neighbours.
            // Explicit visible objectives and combat-unit scoring retain their priority.
            var structure = preferredStructure.Target != null ? preferredStructure : unknownStructure.Target != null ? unknownStructure : knownStructure;
            if (objectiveStructure.Target != null && (structure.Target == null || objectiveStructure.Score > structure.Score)) structure = objectiveStructure;
            // Opt-in test-pilot tactic, not original AI: an exposed combat unit takes
            // priority over stationary objectives while within our own weapon reach.
            // Hidden/ineligible units have already been excluded by player rules.
            bool reachableUnit = preferCombatUnits && Unit != null && Clear &&
                Vector3.Distance(self.Origin, Unit.ModelCentre) <= OwnWeaponRange(self);
            bool mandatoryFirst = PreferMandatoryStructures && objectiveStructure.Target != null && objectiveStructure.Clear &&
                objectives.IsDestroyTarget(director, objectiveStructure.Target.File, objectiveStructure.Target.ObjectiveMask, true);
            if (mandatoryFirst)
            {
                var point = Mw2MechCombat.StructureCentre(objectiveStructure.Target);
                if (PrioritizeDefense && TryStructureAim(self, objectiveStructure.Target, out var surface, true)) point = surface;
                mandatoryFirst = Vector3.Distance(self.Origin, point) <= OwnWeaponRange(self, PrioritizeDefense);
                if (mandatoryFirst) structure = objectiveStructure;
            }
            if (structure.Target != null && (mandatoryFirst || structure.Score > best && !reachableUnit))
            { Structure = structure.Target; Unit = null; Clear = structure.Clear; }
            if (!HasTarget) { _last = null; ObservedVelocity = Vector3.zero; return; }
            if (Unit == null) ManualVisual = Structure != null && manualPoints.ContainsKey(Structure);
            Point = Unit != null ? visualPoint : Mw2MechCombat.StructureCentre(Structure);
            if (Structure != null && Clear)
            {
                Vector3 surfacePoint;
                Clear = ManualVisual ? manualPoints.TryGetValue(Structure, out surfacePoint) :
                    TryStructureAim(self, Structure, out surfacePoint, PrioritizeDefense);
                if (Clear) Point = surfacePoint;
            }
            var observedPosition = PrioritizeDefense && Structure != null ? Structure.transform.position : Point;
            if (PreferFiringAngles && Unit != null) observedPosition = Unit.Origin;
            Component current = Unit != null ? (Component)Unit : Structure;
            if (current == _last && Clear && now - _stamp > 0.02f)
            {
                var measured = (observedPosition - _previous) / (now - _stamp);
                ObservedVelocity = measured.magnitude < 120f ? Vector3.Lerp(ObservedVelocity, measured, 0.5f) : Vector3.zero;
            }
            else ObservedVelocity = Vector3.zero;
            _last = current; _previous = observedPosition; _stamp = now;
            Distance = Vector3.Distance(self.Origin, Point);
            bool inspectStructure = Structure != null && objectives.IsInspectTarget(director, Structure.File, Structure.ObjectiveMask);
            bool destroyStructure = Structure != null && objectives.IsDestroyTarget(director, Structure.File, Structure.ObjectiveMask);
            // Public identification comes before discretionary destruction. TRA3 asks to
            // identify ammunition trucks before destroying the convoy; its visible task
            // does not directly bind individual GTs. Never recover that hidden binding.
            bool unknownHostileStructure = Structure != null && Structure.Side == 1 && !destroyStructure &&
                (Structure.IdentifiedTeams & (1 << self.Team)) == 0;
            InspectTarget = Unit != null ? objectives.IsInspectTarget(director, Unit.SourceFile, Unit.ObjectiveMask) :
                inspectStructure || unknownHostileStructure;
            Attackable = Unit != null ? Unit.Side == 1 || objectives.IsDestroyTarget(director, Unit.SourceFile, Unit.ObjectiveMask) :
                (Structure.Side == 1 && !InspectTarget) || destroyStructure;
            if (ManualVisual) { InspectTarget = false; Attackable = true; self.ClearTarget(); }
            else self.PilotSelect(Unit, Structure);
        }

        // Opt-in manual observation. A real enabled mesh must be in the current
        // eye cone and the normal shot ray must reach it first. No radar bypass.
        internal static bool TryHeadAim(Mw2MechCombat self, Mw2MechCombat target, out Vector3 point)
            => TrySectionAim(self, target, 0, out point);

        internal static bool TrySectionAim(Mw2MechCombat self, Mw2MechCombat target, int section, out Vector3 point)
        {
            point = Vector3.zero;
            if (target == null || section < 0 || section > 7 || !target.TryGetComponent<Mw2HitMap>(out var map)) return false;
            self.WeaponAimRay(self.GetComponent<Mw2TorsoTwist>(), out var eye, out var look);
            foreach (var renderer in target.GetComponentsInChildren<Renderer>())
            {
                if (!renderer.enabled || !renderer.gameObject.activeInHierarchy || map.SectionOf(renderer.transform) != section) continue;
                var candidate = renderer.bounds.center; var ray = candidate - eye;
                if (ray.sqrMagnitude < 0.01f || ray.magnitude > Mw2MechCombat.TargetRangeUnits * Formats.PolyModel.UnitsToMeters ||
                    Vector3.Dot(look, ray.normalized) < 0.34202015f) continue;
                if (self.FirstHit(eye, ray.normalized, ray.magnitude + 0.5f, out var hit) &&
                    hit.collider.GetComponentInParent<Mw2MechCombat>() == target && map.SectionOf(hit.collider.transform) == section)
                { point = candidate; return true; }
            }
            return false;
        }

        internal static bool TryVisualUnit(Mw2MechCombat self, Mw2MechCombat target, out Vector3 point)
        {
            point = Vector3.zero;
            self.WeaponAimRay(self.GetComponent<Mw2TorsoTwist>(), out var eye, out var look);
            foreach (var renderer in target.GetComponentsInChildren<Renderer>())
            {
                if (!renderer.enabled || !renderer.gameObject.activeInHierarchy) continue;
                var candidate = renderer.bounds.center;
                var delta = candidate - eye;
                if (delta.sqrMagnitude < 0.01f || delta.magnitude > Mw2MechCombat.TargetRangeUnits * Formats.PolyModel.UnitsToMeters ||
                    Vector3.Dot(look, delta.normalized) < 0.34202015f) continue; //70-degree eye cone
                if (self.FirstHit(eye, delta.normalized, delta.magnitude + 0.5f, out var hit) &&
                    hit.collider.GetComponentInParent<Mw2MechCombat>() == target)
                { point = candidate; return true; }
            }
            return false;
        }

        // Tester geometry policy: aim at a real visible surface, not empty
        // space in a large model's bounds. Native shot/collision rules are unchanged.
        internal static bool TryStructureAim(Mw2MechCombat self, Mw2Destructible target, out Vector3 point, bool nearest = false)
        {
            point = target != null ? Mw2MechCombat.StructureCentre(target) : Vector3.zero;
            if (target == null || target.Destroyed || (target.Flags & 0x800) != 0) return false;
            var origin = self.Origin;
            // Guard's manual aiming must see what its actual weapon eye sees,
            // including surfaces exposed above a low obstruction at GP Origin.
            if (nearest) self.WeaponAimRay(self.GetComponent<Mw2TorsoTwist>(), out origin, out _);
            Vector3 physical = Vector3.zero;
            bool Hits(Vector3 candidate)
            {
                var ray = candidate - origin;
                if (ray.sqrMagnitude <= 0.01f || !self.FirstHit(origin, ray.normalized, ray.magnitude + 0.5f, out var hit) ||
                    hit.collider == null || hit.collider.GetComponentInParent<Mw2Destructible>() != target) return false;
                physical = hit.point;
                return true;
            }
            bool found = Hits(point);
            if (found && !nearest) return true;
            if (found && nearest) point = physical;
            float best = found ? Vector3.SqrMagnitude(point - origin) : float.PositiveInfinity;
            bool geometry = false;
            foreach (var renderer in target.GetComponentsInChildren<Renderer>())
            {
                if (!renderer.enabled || !renderer.gameObject.activeInHierarchy) continue;
                geometry = true;
                var b = renderer.bounds;
                foreach (var candidate in new[] { b.ClosestPoint(origin), b.center,
                    b.center + Vector3.right * b.extents.x * 0.95f, b.center - Vector3.right * b.extents.x * 0.95f,
                    b.center + Vector3.up * b.extents.y * 0.95f, b.center - Vector3.up * b.extents.y * 0.95f,
                    b.center + Vector3.forward * b.extents.z * 0.95f, b.center - Vector3.forward * b.extents.z * 0.95f })
                {
                    float distance = (candidate - origin).sqrMagnitude;
                    if ((!nearest && distance >= best) || !Hits(candidate)) continue;
                    var chosen = nearest ? physical : candidate;
                    distance = (chosen - origin).sqrMagnitude;
                    if (distance >= best) continue;
                    point = chosen; best = distance; found = true;
                    if (!nearest) return true;
                }
            }
            return found || !geometry;
        }

        static float OwnWeaponRange(Mw2MechCombat self, bool structure = false)
        {
            float range = 0;
            for (int i = 0; i < self.Weapons.Count; i++)
            {
                var weapon = self.Weapons[i];
                if (weapon.Def != null && !weapon.Def.IsAms && weapon.Ammo != 0 && self.PilotWeaponWorks(i))
                    range = Mathf.Max(range, AutopilotCombat.ShotRange(weapon.Def, structure));
            }
            return range;
        }
    }
}
