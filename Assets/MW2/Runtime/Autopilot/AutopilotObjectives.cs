using System;
using System.Collections.Generic;
using MW2.Formats;
using UnityEngine;

namespace MW2.Autopilot
{
    /// <summary>Visible briefing objectives and public NAVs only. Hidden MTBL dependencies belong to the director.</summary>
    public sealed class AutopilotObjectives
    {
        public string Description = "Search public route", LastNav = "";
        public Vector3 Destination;
        public float ArrivalRadius = 30f;
        public bool HasDestination, PreferDestination;
        public bool Defending;
        public Vector3 DefensePoint;
        public readonly List<Vector3> DefenseAssets = new List<Vector3>();
        public bool CloseNav; // Tester policy: approach public NAV centres instead of stopping in a wide arrival circle.
        public string SearchNav = ""; // Explicit tester search anchor; must be a selectable public NAV.
        public string[] RouteNavs = Array.Empty<string>(); // Explicit public waypoints before normal objective steering.
        int _routeIndex;
        int _patrol;
        float _patrolUntil;
        float _patrolDistance;
        readonly HashSet<string> _searchedNavs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public bool IsInspectTarget(Mw2MissionDirector director, string file, int targetMask, bool mandatoryOnly = false)
        {
            var team = director.Teams.Find(t => t.Team == 0);
            for (int i = 0; team != null && i < team.Orders.Count; i++)
            {
                var o = team.Orders[i];
                if (o.Vital && (!mandatoryOnly || o.Mandatory) && director.StateOf(0, i) == Mw2MissionDirector.State.Active && (o.Mask == 8 || o.Mask == 0x20) &&
                    (o.Mask & (uint)targetMask) != 0 && string.Equals(o.Target, file, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        public bool IsDestroyTarget(Mw2MissionDirector director, string file, int targetMask, bool mandatoryOnly = false)
        {
            var team = director.Teams.Find(t => t.Team == 0);
            for (int i = 0; team != null && i < team.Orders.Count; i++)
            {
                var o = team.Orders[i];
                if (o.Vital && (!mandatoryOnly || o.Mandatory) && director.StateOf(0, i) == Mw2MissionDirector.State.Active && (o.Mask & 3u) != 0 &&
                    (o.Mask & (uint)targetMask) != 0 && string.Equals(o.Target, file, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        public void Update(Mw2MissionDirector director, Mw2MechCombat self, float now, PilotTactic tactic = PilotTactic.Balanced)
        {
            Vector3 position = self.transform.position;
            DefenseAssets.Clear();
            Defending = false;
            HasDestination = false;
            PreferDestination = false;
            while (_routeIndex < RouteNavs.Length)
            {
                var publicNavs = director.PlayerNavs;
                int routeAt = publicNavs.FindIndex(n => string.Equals(n.File, RouteNavs[_routeIndex], StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(n.Name, RouteNavs[_routeIndex], StringComparison.OrdinalIgnoreCase));
                if (routeAt < 0) { _routeIndex++; continue; } // A route cannot reveal hidden/script-only NAVs.
                var routeNav = publicNavs[routeAt];
                Set(routeNav);
                if (Vector2.Distance(new Vector2(position.x, position.z), new Vector2(routeNav.Position.x, routeNav.Position.z)) <= ArrivalRadius)
                { _routeIndex++; continue; }
                Description = "Explicit public NAV route: " + routeNav.Name;
                PreferDestination = true;
                return;
            }
            var team = director.Teams.Find(t => t.Team == 0);
            // Select visible active NAV/hold objectives only if the NAV itself is public and selectable.
            for (int i = 0; team != null && i < team.Orders.Count; i++)
            {
                var o = team.Orders[i];
                if (!o.Vital || director.StateOf(0, i) != Mw2MissionDirector.State.Active) continue;
                Description = o.Text;
                if ((o.Mask & (MissionOrder.GotoNav | MissionOrder.Hold | MissionOrder.HoldWith | MissionOrder.Protect)) != 0)
                    foreach (var n in director.PlayerNavs)
                        if (string.Equals(n.File, o.Target, StringComparison.OrdinalIgnoreCase))
                        {
                            Set(n);
                            PreferDestination = AutopilotPolicy.PreferMandatoryNav(team.Orders, i,
                                index => director.StateOf(0, index) == Mw2MissionDirector.State.Succeeded, tactic);
                            return;
                        }
                if ((o.Mask & (MissionOrder.Protect | MissionOrder.HoldWith)) != 0)
                {
                    foreach (var u in Mw2MechCombat.All)
                        if (u != null && !u.IsPlayer && self.PilotCanSelect(u) &&
                            string.Equals(o.Target, u.SourceFile, StringComparison.OrdinalIgnoreCase))
                        { Destination = u.transform.position; ArrivalRadius = 100; HasDestination = true; SetDefense(self, Destination); return; }
                    foreach (var s in Mw2Destructible.All)
                        if (s != null && VisibleProtection(self, s) &&
                            string.Equals(o.Target, s.File, StringComparison.OrdinalIgnoreCase))
                        { Destination = s.transform.position; ArrivalRadius = 100; HasDestination = true; SetDefense(self, Mw2MechCombat.StructureCentre(s)); return; }
                }
            }
            // Never chase script-only dummy NAVs such as BET3GOU1 at 40 km.
            var routeNavs = director.PlayerNavs;
            var anchor = routeNavs.FindIndex(n => string.Equals(n.File, SearchNav, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(n.Name, SearchNav, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(SearchNav) && anchor >= 0) routeNavs = new List<Mw2MissionDirector.NavInfo> { routeNavs[anchor] };
            foreach (var n in routeNavs)
            {
                int index = director.Navs.FindIndex(x => x.File == n.File);
                if (director.NavVisited(index) || _searchedNavs.Contains(n.File)) continue;
                Set(n);
                // Reaching a public search point need not complete its waiting
                // mission order. Remember only tester travel, never mark a NAV
                // visited in the director or change objective state.
                if (Vector2.Distance(new Vector2(position.x, position.z), new Vector2(n.Position.x, n.Position.z)) > ArrivalRadius) return;
                _searchedNavs.Add(n.File);
            }
            // A public NAV can activate reinforcements; search its surroundings after visiting the route.
            if (routeNavs.Count > 0)
            {
                var navs = routeNavs;
                Vector3 SearchPoint(int step)
                {
                    float angle = (step % 8) * 45f * Mathf.Deg2Rad;
                    float radius = anchor >= 0 && !string.IsNullOrEmpty(SearchNav) ? 220f * (1 + Math.Min(3, step / (8 * navs.Count))) : 220f;
                    return navs[(step / 8) % navs.Count].Position + new Vector3(Mathf.Sin(angle), 0, Mathf.Cos(angle)) * radius;
                }
                var point = SearchPoint(_patrol);
                float distance = Vector2.Distance(new Vector2(position.x, position.z), new Vector2(point.x, point.z));
                // A long trip between public NAVs can take minutes. Expire only
                // a point with no progress, not a waypoint we are still nearing.
                if (_patrolUntil == 0 || distance < _patrolDistance - 5f)
                { _patrolDistance = distance; _patrolUntil = now + 45f; }
                if (distance < 40f ||
                    _patrolUntil > 0 && now >= _patrolUntil)
                {
                    _patrol++; _patrolUntil = now + 45f;
                    point = SearchPoint(_patrol);
                    _patrolDistance = Vector2.Distance(new Vector2(position.x, position.z), new Vector2(point.x, point.z));
                }
                var n = navs[(_patrol / 8) % navs.Count];
                Destination = SearchPoint(_patrol);
                LastNav = n.Name + " (" + n.File + ")";
                Description = "Search public route near " + n.Name;
                ArrivalRadius = 30f; HasDestination = true;
            }
        }
        // Guard the visible friendly/neutral installation around the public
        // protection anchor. No enemy orders, asset health or hidden bindings.
        void SetDefense(Mw2MechCombat self, Vector3 anchor)
        {
            Defending = true; DefensePoint = anchor;
            DefenseAssets.Add(anchor);
            foreach (var asset in Mw2Destructible.All)
            {
                if (asset == null || asset.Side == 1 || !VisibleProtection(self, asset)) continue;
                var point = Mw2MechCombat.StructureCentre(asset);
                if (Vector3.Distance(point, anchor) < 1750f) DefenseAssets.Add(point);
            }
        }

        // Tester observation rule: an explicit public protection task can name a
        // neutral/non-radar building. It must still be visible and within range.
        static bool VisibleProtection(Mw2MechCombat self, Mw2Destructible target)
        {
            if ((target.Flags & 0x800) != 0 || target.Destroyed) return false;
            var ray = Mw2MechCombat.StructureCentre(target) - self.Origin;
            if (ray.magnitude > Mw2MechCombat.TargetRangeUnits * PolyModel.UnitsToMeters) return false;
            if (self.PilotCanSelect(target) || ray.sqrMagnitude < 0.01f) return true;
            return !self.FirstHit(self.Origin, ray.normalized, ray.magnitude + 0.5f, out var hit) ||
                hit.collider.GetComponentInParent<Mw2Destructible>() == target;
        }

        void Set(Mw2MissionDirector.NavInfo n)
        {
            Destination = n.Position; ArrivalRadius = n.Radius > 0f ? n.Radius * 0.65f : 100f;
            if (CloseNav) ArrivalRadius = Mathf.Min(ArrivalRadius, 60f);
            HasDestination = true; LastNav = n.Name + " (" + n.File + ")";
        }
    }
}
