#if DEBUG
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.AI;

namespace DevTools
{
    // The ground around the hero as a graph, for deciding where to stand rather than which way to
    // lean. A square of cells, one sample each, read from the game's own navmesh:
    //
    //   walk    whether the navmesh is under the cell and it is not hazard ground (lava and the
    //           like, which the game marks as not for playing on) - a hero can stand there
    //   reach   path length in cells from the hero's cell, over edges between neighbours that the
    //           navmesh lets a hero walk straight along (NavMesh.Raycast finds no edge between
    //           them); -1 where the hero cannot get to at all - across a chasm, behind a wall
    //   clear   how far the cell is from the nearest cell that cannot be stood on: room to move
    //
    // Enemies are not in it; /entities has them, and where to stand relative to them is the
    // caller's choice. The arrays are row-major from the grid's corner (origin), x along a row.
    internal static class NavGrid
    {
        private const int MaxSize = 81;

        [Route("GET", "/nav/grid", "A grid of cells around the hero from the navmesh: walkable, path length from the hero (-1 = unreachable), clearance to the nearest obstacle. For planning where to move. Arrays are row-major from origin, x along a row.",
               "radius=12, step=1, x,z? (centre; default the hero)")]
        private static object Grid(Args a)
        {
            var hero = GameAccess.Hero;
            var centre = a.Point() ?? (hero != null ? hero.agentPosition : throw new DevException("no hero; pass x,z"));
            float step = Mathf.Clamp(a.Float("step", 1f), 0.4f, 4f);
            int half = Mathf.Clamp(Mathf.RoundToInt(a.Float("radius", 12f) / step), 2, (MaxSize - 1) / 2);
            int size = half * 2 + 1;
            var origin = new Vector3(centre.x - half * step, centre.y, centre.z - half * step);

            // Ground that hurts to stand on (lava and the like) is on the navmesh - monsters walk it -
            // but the game marks it as not for playing on; such cells count as not standable.
            var hazards = Describe.Actors<Actor>().Where(x => x is INotPlayableOnTop).Select(x => x.transform).ToList();
            int hazardCells = 0;
            // Lava that rises and falls: 'L' under it now, 'l' dry now but under it at its high.
            var lavas = Describe.Actors<LavaLand_Lava>().ToList();
            var hazard = new char[size * size];
            for (int k = 0; k < hazard.Length; k++) hazard[k] = '.';

            var walk = new bool[size * size];
            var points = new Vector3[size * size];
            for (int j = 0; j < size; j++)
            for (int i = 0; i < size; i++)
            {
                var p = new Vector3(origin.x + i * step, centre.y, origin.z + j * step);
                int k = j * size + i;
                if (NavMesh.SamplePosition(p, out var hit, 2.5f, NavMesh.AllAreas) &&
                    new Vector2(hit.position.x - p.x, hit.position.z - p.z).magnitude < step * 0.45f)
                {
                    if (hazards.Count > 0 && OnHazard(hit.position, hazards)) { hazardCells++; hazard[k] = 'L'; continue; }
                    if (lavas.Count > 0 && UnderLavaAtHigh(hit.position, lavas)) hazard[k] = 'l';
                    walk[k] = true;
                    points[k] = hit.position;
                }
            }

            // Path length from the hero, breadth first over walkable neighbours with nothing in
            // between. Diagonals count as one step, which is close enough for choosing a spot.
            var reach = Enumerable.Repeat(-1, size * size).ToArray();
            int start = half * size + half;
            if (!walk[start])
            {
                // The hero stands a little off a sample point; start from the nearest walkable one.
                int best = -1;
                float bestD = float.MaxValue;
                for (int k = 0; k < walk.Length; k++)
                {
                    if (!walk[k]) continue;
                    float dd = (points[k] - centre).sqrMagnitude;
                    if (dd < bestD) { bestD = dd; best = k; }
                }
                start = best;
            }

            if (start >= 0)
            {
                var queue = new Queue<int>();
                reach[start] = 0;
                queue.Enqueue(start);
                while (queue.Count > 0)
                {
                    int k = queue.Dequeue();
                    int ci = k % size, cj = k / size;
                    for (int dj = -1; dj <= 1; dj++)
                    for (int di = -1; di <= 1; di++)
                    {
                        if (di == 0 && dj == 0) continue;
                        int ni = ci + di, nj = cj + dj;
                        if (ni < 0 || nj < 0 || ni >= size || nj >= size) continue;
                        int n = nj * size + ni;
                        if (!walk[n] || reach[n] >= 0) continue;
                        if (NavMesh.Raycast(points[k], points[n], out _, NavMesh.AllAreas)) continue;
                        reach[n] = reach[k] + 1;
                        queue.Enqueue(n);
                    }
                }
            }

            // Clearance: distance to the nearest cell that cannot be stood on, spread outward from
            // all of them at once. The edge of the grid does not count as a wall.
            var clear = Enumerable.Repeat(int.MaxValue, size * size).ToArray();
            var wave = new Queue<int>();
            for (int k = 0; k < walk.Length; k++)
                if (!walk[k]) { clear[k] = 0; wave.Enqueue(k); }
            while (wave.Count > 0)
            {
                int k = wave.Dequeue();
                int ci = k % size, cj = k / size;
                for (int dj = -1; dj <= 1; dj++)
                for (int di = -1; di <= 1; di++)
                {
                    int ni = ci + di, nj = cj + dj;
                    if (ni < 0 || nj < 0 || ni >= size || nj >= size) continue;
                    int n = nj * size + ni;
                    if (clear[n] <= clear[k] + 1) continue;
                    clear[n] = clear[k] + 1;
                    wave.Enqueue(n);
                }
            }

            // Compact: a string of '#' and '.' for walking, arrays of ints for the rest.
            var rows = new StringBuilder(size * size);
            for (int k = 0; k < walk.Length; k++) rows.Append(walk[k] ? '.' : '#');
            int cap = size;
            return new
            {
                origin = new { x = Math.Round(origin.x, 3), z = Math.Round(origin.z, 3) },
                step,
                size,
                hero = start >= 0 ? new { i = start % size, j = start / size } : null,
                walk = rows.ToString(),
                reach,
                clear = clear.Select(c => c == int.MaxValue ? cap : c).ToArray(),
                reachable = reach.Count(r => r >= 0),
                hazardCells,
                hazard = new string(hazard),
                lava = lavas.Select(l => new
                {
                    enableTranslation = l.enableTranslation,
                    interval = l.translationInterval,
                    low = l.translationRange.x,
                    high = l.translationRange.y,
                    y = Math.Round(l.transform.localPosition.y, 3),
                    // where in its cycle: 0 = low, 1 = high, and whether it is rising
                    t = Math.Round(Mathf.Sin((float)Mirror.NetworkTime.time * Mathf.PI / l.translationInterval * 2f) * 0.5f + 0.5f, 3),
                    rising = Mathf.Cos((float)Mirror.NetworkTime.time * Mathf.PI / l.translationInterval * 2f) > 0f,
                }).FirstOrDefault(),
            };
        }

        // The path a move would take, and how much of it is lava: a pure read (CalculatePath plans,
        // nothing moves), for checking a walk before making it, at any distance.
        [Route("GET", "/nav/path", "The path a move to x,z would walk (the navmesh's corners from the hero), each leg with how many metres of it lie on hazard ground (lava and the like). For checking a walk before making it.",
               "x, z")]
        private static object Path(Args a)
        {
            var hero = GameAccess.RequireHero();
            var to = a.Point() ?? throw new DevException("missing x and z");
            var from = hero.agentPosition;
            var destination = Dew.GetValidAgentDestination_Closest(from, to);
            var path = new NavMeshPath();
            bool found = NavMesh.CalculatePath(from, destination, NavMesh.AllAreas, path);
            var hazards = Describe.Actors<Actor>().Where(x => x is INotPlayableOnTop).Select(x => x.transform).ToList();
            var legs = new List<object>();
            float length = 0f, onHazard = 0f;
            var corners = found ? path.corners : new Vector3[0];
            for (int i = 1; i < corners.Length; i++)
            {
                Vector3 p = corners[i - 1], q = corners[i];
                float len = Vector3.Distance(new Vector3(p.x, 0f, p.z), new Vector3(q.x, 0f, q.z));
                int n = Mathf.Max(1, Mathf.CeilToInt(len / 0.5f)), hot = 0;
                if (hazards.Count > 0)
                    for (int s = 0; s <= n; s++)
                        if (OnHazard(Vector3.Lerp(p, q, s / (float)n), hazards)) hot++;
                float hz = len * hot / (n + 1f);
                length += len; onHazard += hz;
                legs.Add(new { from = Describe.Vec(p), to = Describe.Vec(q), length = Math.Round(len, 2), onHazard = Math.Round(hz, 2) });
            }
            return new
            {
                status = found ? path.status.ToString() : "none",
                destination = Describe.Vec(destination),
                length = Math.Round(length, 2),
                onHazard = Math.Round(onHazard, 2),
                legs,
            };
        }

        // Whether the lava would cover p at the top of its rise: p lies in its footprint (a ray down
        // hits its collider, wherever the plane is now) and the ground at p is below where the lava's
        // surface would be at its high. Pure reads.
        private static bool UnderLavaAtHigh(Vector3 p, List<LavaLand_Lava> lavas)
        {
            var down = new Ray(p + Vector3.up * 20f, Vector3.down);
            foreach (var l in lavas)
            {
                if (l == null || !l.enableTranslation) continue;
                var col = l.GetComponentInChildren<Collider>();
                if (col == null || !col.Raycast(down, out var lavaHit, 40f)) continue;
                float ground = float.NegativeInfinity;
                foreach (var h in Physics.RaycastAll(down, 40f, LayerMasks.Ground))
                    if (h.transform != l.transform && !h.transform.IsChildOf(l.transform)) ground = Mathf.Max(ground, h.point.y);
                if (float.IsNegativeInfinity(ground)) continue;
                var parent = l.transform.parent;
                float rise = parent != null
                    ? (parent.TransformPoint(new Vector3(0f, l.translationRange.y, 0f)) - parent.TransformPoint(new Vector3(0f, l.transform.localPosition.y, 0f))).y
                    : l.translationRange.y - l.transform.localPosition.y;
                if (ground < lavaHit.point.y + rise) return true;
            }
            return false;
        }

        // The ground under a point, as the game itself checks for lava: a ray down onto the ground
        // layer, and whether what it hits is one of the hazards.
        private static bool OnHazard(Vector3 p, List<Transform> hazards)
        {
            if (!Physics.Raycast(p + Vector3.up * 20f, Vector3.down, out var hit, 40f, LayerMasks.Ground)) return false;
            foreach (var t in hazards)
                if (t != null && (hit.transform == t || hit.transform.IsChildOf(t))) return true;
            return false;
        }
    }
}
#endif
