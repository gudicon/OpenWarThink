using UnityEngine;
using System.Collections.Generic;

namespace MW2.Autopilot
{
    public struct PilotControls
    {
        public float throttle, turn, pan, pitch;
        public bool reverse, jump, fire;
        public int weapon;
    }

    /// <summary>Test-pilot steering, using the original chassis dynamics and obstacle scanner, never a NavMeshAgent.</summary>
    public sealed class AutopilotNavigation
    {
        readonly StuckDetector _stuck = new StuckDetector();
        float _recoverUntil, _jumpUntil;
        int _side = 1;
        Vector3 _flankPoint;
        float _flankUntil;
        Component _flankTarget;
        public bool Recovering;
        public int Recoveries;
        public bool UseMapRoute; // Explicit tester map-reading strategy; static collision geometry, never enemy state.
        readonly List<Vector3> _mapPath = new List<Vector3>();
        Vector3 _mapGoal;
        float _mapUntil, _mapFailUntil;
        public void Reset() { _stuck.Reset(); _recoverUntil = _jumpUntil = _flankUntil = 0; _flankTarget = null; Recovering = false; Recoveries = 0; _mapPath.Clear(); _mapUntil = _mapFailUntil = 0; }

        // Opt-in mobile tester steering: retain a reachable steering goal rather
        // than moving it sideways every three seconds before a slow mech arrives.
        // Inputs are own position and the already observed/selected target only.
        public Vector3 RepositionWaypoint(Mw2MechCombat self, AutopilotSensors sensors, float now)
        {
            Component target = sensors.Unit != null ? (Component)sensors.Unit : sensors.Structure;
            var remaining = _flankPoint - self.transform.position; remaining.y = 0;
            if (target != _flankTarget || now >= _flankUntil || remaining.sqrMagnitude < 144f)
            {
                var toward = sensors.Point - self.transform.position; toward.y = 0;
                _flankPoint = self.transform.position + Vector3.Cross(Vector3.up, toward.normalized) * 160f;
                _flankPoint.y = Mw2Ground.Height(_flankPoint);
                _flankUntil = now + 20f;
                _flankTarget = target;
            }
            return _flankPoint;
        }

        public void Drive(Mw2MechController controller, Mw2MechCombat combat, Vector3 destination,
                          float radius, float now, ref PilotControls c)
        {
            if (UseMapRoute)
            {
                var goal = destination;
                destination = MapWaypoint(controller, destination, now);
                if (FlatDistance(destination, goal) > 1f) radius = Mathf.Min(radius, 8f);
            }
            var p = controller.transform.position;
            var delta = destination - p; delta.y = 0;
            float bearing = Mathf.DeltaAngle(controller.HeadingDeg, Mathf.Atan2(delta.x, delta.z) * Mathf.Rad2Deg);
            bool move = delta.magnitude > radius;
            c.throttle = move ? Mathf.Abs(bearing) > 100f ? 0.15f : Mathf.Abs(bearing) > 45f ? 0.45f : 0.9f : 0;
            c.turn = Mathf.Clamp(bearing / 45f, -1, 1) * 0.8f;
            if (_stuck.Update(now, p.x, p.z, move && c.throttle > 0.1f) && now >= _recoverUntil)
            {
                Recoveries++; _side = -_side; _recoverUntil = now + 4f;
                _mapUntil = 0;
                if (controller.JumpMp > 0 && !controller.JumpDisabled && controller.JumpFuel > 400 && combat.Heat < 45f)
                    _jumpUntil = now + 2.5f;
                _stuck.Reset();
            }
            Recovering = now < _recoverUntil;
            if (Recovering)
            {
                c.turn = _side * 0.75f; c.throttle = 0.55f;
                c.jump = now < _jumpUntil;
                c.reverse = !c.jump && now < _recoverUntil - 1.5f;
                return;
            }
            float turn = c.turn * 1024f, throttle = c.throttle / 64f;
            if (!UseMapRoute && move && controller.AiAvoid(bearing, ref turn, ref throttle))
            { c.turn = Mathf.Clamp(turn / 1024f, -1, 1); c.throttle = Mathf.Clamp01(throttle * 64f); }
        }

        sealed class MapNode
        {
            public Vector2Int Key;
            public Vector3 Point;
            public float Cost;
            public float Score;
            public MapNode Parent;
        }

        // Tester-only reading of the ordinary static map/colliders. The resulting
        // waypoint is consumed by Drive; this never sets position, health or orders.
        internal Vector3 MapWaypoint(Mw2MechController controller, Vector3 destination, float now)
        {
            var start = controller.transform.position;
            if (now < _mapFailUntil) return start;
            while (_mapPath.Count > 0 && FlatDistance(start, _mapPath[0]) < 12f) _mapPath.RemoveAt(0);
            if (_mapPath.Count == 0 || now >= _mapUntil || FlatDistance(destination, _mapGoal) > 40f)
            {
                _mapPath.Clear(); _mapGoal = destination; _mapUntil = now + 120f;
                if (MapSegment(controller, start, destination)) { _mapPath.Add(destination); return destination; }
                const float step = 25f;
                bool hover = controller.TryGetComponent<Mw2MechCombat>(out var own) && own.GamepieceType == 2;
                var first = new MapNode { Key = Vector2Int.zero, Point = start, Cost = 0, Score = FlatDistance(start, destination) };
                var nodes = new Dictionary<Vector2Int, MapNode> { [first.Key] = first };
                var open = new SortedSet<MapNode>(Comparer<MapNode>.Create((a, b) =>
                {
                    int score = a.Score.CompareTo(b.Score);
                    if (score != 0) return score;
                    int x = a.Key.x.CompareTo(b.Key.x);
                    return x != 0 ? x : a.Key.y.CompareTo(b.Key.y);
                })) { first };
                var closed = new HashSet<Vector2Int>();
                MapNode best = first;
                for (int iteration = 0; open.Count > 0 && iteration < 30000; iteration++)
                {
                    var node = open.Min; open.Remove(node); closed.Add(node.Key);
                    if (FlatDistance(node.Point, destination) < FlatDistance(best.Point, destination)) best = node;
                    if (FlatDistance(node.Point, destination) < step) break;
                    for (int x = -1; x <= 1; x++) for (int z = -1; z <= 1; z++)
                    {
                        if (x == 0 && z == 0) continue;
                        var key = node.Key + new Vector2Int(x, z);
                        if (closed.Contains(key) || Mathf.Abs(key.x) > 220 || Mathf.Abs(key.y) > 220) continue;
                        bool known = nodes.TryGetValue(key, out var next);
                        var point = known ? next.Point : start + new Vector3(key.x * step, 0, key.y * step);
                        if (!known) point.y = hover ? 0f : Mw2Ground.Height(new Vector3(point.x, 10000f, point.z));
                        float cost = node.Cost + Vector3.Distance(node.Point, point);
                        if (known && cost >= next.Cost || !MapSegment(controller, node.Point, point)) continue;
                        if (!known)
                        {
                            next = new MapNode { Key = key, Point = point }; nodes.Add(key, next);
                        }
                        else open.Remove(next);
                        next.Cost = cost; next.Parent = node; next.Score = cost + FlatDistance(point, destination); open.Add(next);
                    }
                }
                for (var node = best; node.Parent != null; node = node.Parent) _mapPath.Add(node.Point);
                _mapPath.Reverse();
                if (_mapPath.Count == 0) _mapFailUntil = now + 2f;
                MissionTelemetry.Note("map_route", "waypoints=" + _mapPath.Count + "; sampled=" + nodes.Count +
                    "; remaining=" + FlatDistance(best.Point, destination).ToString("0") + "; hover=" + hover);
            }
            // Skip intermediate grid corners only when the actual corridor stays clear.
            for (int i = _mapPath.Count - 1; i > 0; i--)
                if (MapSegment(controller, start, _mapPath[i])) { _mapPath.RemoveRange(0, i); break; }
            return _mapPath.Count > 0 ? _mapPath[0] : start;
        }

        static float FlatDistance(Vector3 a, Vector3 b) => Vector2.Distance(new Vector2(a.x, a.z), new Vector2(b.x, b.z));

        internal static bool MapSegment(Mw2MechController controller, Vector3 from, Vector3 to)
        {
            var delta = to - from;
            float flat = FlatDistance(from, to);
            if (flat < 0.1f) return true;
            if (Mathf.Abs(delta.y) > flat * 0.45f + 2f) return false;
            var side = Vector3.Cross(Vector3.up, delta.normalized) * (controller.CollisionRadius + 1.5f);
            bool hover = controller.TryGetComponent<Mw2MechCombat>(out var own) && own.GamepieceType == 2;
            int samples = Mathf.Max(1, Mathf.CeilToInt(flat / 20f));
            var previous = from;
            for (int i = 1; i <= samples; i++)
            {
                var point = Vector3.Lerp(from, to, (float)i / samples);
                float ground = Mw2Ground.Height(new Vector3(point.x, 10000f, point.z));
                if (hover && ground > controller.ProbeHeight - 0.25f) return false;
                point.y = hover ? 0f : ground;
                if (Mathf.Abs(point.y - previous.y) > FlatDistance(point, previous) * 0.45f + 2f) return false;
                var move = point - previous;
                for (int lane = -1; lane <= 1; lane++)
                    if (Physics.Raycast(previous + Vector3.up * controller.ProbeHeight + side * lane,
                        move.normalized, out var hit, move.magnitude + controller.CollisionRadius,
                        Mw2HitPart.MovementMask, QueryTriggerInteraction.Ignore) &&
                        new Vector2(hit.normal.x, hit.normal.z).sqrMagnitude > 1e-6f) return false;
                previous = point;
            }
            return true;
        }

        public Vector3 CoverOrFlank(Mw2MechCombat self, AutopilotSensors sensors, bool seekCover, int side)
        {
            var toward = sensors.Point - self.transform.position; toward.y = 0;
            var lateral = Vector3.Cross(Vector3.up, toward.normalized) * side;
            if (seekCover)
                foreach (float distance in new[] { 18f, 35f, 60f })
                    foreach (int s in new[] { 1, -1 })
                    {
                        var p = self.transform.position + lateral * distance * s;
                        p.y = Mw2Ground.Height(p);
                        var eye = p + Vector3.up * self.OriginHeight;
                        var ray = sensors.Point - eye;
                        var path = p - self.transform.position;
                        if (self.FirstHit(eye, ray.normalized, ray.magnitude, out var hit) &&
                            hit.collider.GetComponentInParent<Mw2MechCombat>() == null &&
                            !self.FirstHit(self.Origin, path.normalized, path.magnitude, out _)) return p;
                    }
            return self.transform.position + lateral * 70f - toward.normalized * (seekCover ? 90f : 0f);
        }
    }
}
