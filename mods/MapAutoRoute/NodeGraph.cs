using System.Collections.Generic;
using UnityEngine;

namespace MapAutoRoute
{
    // The map, read as a graph.
    //
    // Nothing here has to be derived: the game already keeps `ZoneManager.nodeDistanceMatrix`, an
    // N*N SyncList<int> filled during world generation, and adjacency is "the distance between
    // them is 1". So it is a table lookup, and a breadth-first search over it costs nothing worth
    // measuring on a zone of thirty-odd nodes.
    internal static class NodeGraph
    {
        public const int None = -1;

        // The two lines of ZoneManager.IsNodeConnected, spelled out rather than called.
        //
        // That is not duplication for its own sake: this mod *patches* that method, to widen what
        // the game will accept as adjacent - so a search that called it would be reading its own
        // answer back and would find every node one step from everywhere. The searches have to see
        // the real graph, which means reading the matrix the same way the game does.
        //
        // Both orders, because the game checks both: whatever asymmetry the generator can leave in
        // the matrix, IsNodeConnected is the thing that knows about it.
        public static bool Adjacent(ZoneManager zone, int a, int b)
        {
            int count = zone.nodes.Count;
            return zone.nodeDistanceMatrix[count * a + b] == 1 ||
                   zone.nodeDistanceMatrix[count * b + a] == 1;
        }

        // A route may only pass *through* nodes the party has already stood on. The far end is
        // exempt on purpose: travelling to a revealed-but-unvisited node is the ordinary move, and
        // routing to one is that same move with the rooms in between skipped.
        //
        // Sidetrack nodes are excluded because the map does not draw them either - RefreshNodes
        // skips every node whose IsSidetrackNode() is true - and a node the player cannot see is
        // not one they can be asked to route through.
        //
        // A node with a hunter standing in it is not a room to be walked through, which is the
        // third rule and the only one that changes from turn to turn. `avoidHunted: false` asks
        // the same question without it, which is how the tooltip tells "there is no way there"
        // apart from "there is a way and the hunt is sitting on it".
        private static bool CanPassThrough(ZoneManager zone, int index, bool avoidHunted)
        {
            var node = zone.nodes[index];
            if (node.status != WorldNodeStatus.HasVisited || node.IsSidetrackNode()) return false;
            return !avoidHunted || !Hunt.IsHunted(zone, index);
        }

        // The matrix is written once per world and grown by AddSidetrackNode. Between the two, a
        // client can hold a node list that has already grown and a matrix that has not, which
        // would index off the end of it - so the size is checked rather than assumed.
        public static bool IsUsable(ZoneManager zone)
        {
            if (zone == null) return false;

            int count = zone.nodes.Count;
            return count > 0 && zone.nodeDistanceMatrix.Count >= count * count;
        }

        // The nodes between `from` and `to`, excluding the one stood on and including the
        // destination. Null when there is no way through visited rooms.
        //
        // Breadth-first, so what comes back is the fewest rooms crossed. The distance already in
        // the matrix would be a shorter number for the same pair - it counts steps through the
        // whole graph, unvisited nodes included - which is exactly why it is not what is asked.
        public static List<int> FindRoute(ZoneManager zone, int from, int to, bool avoidHunted = true)
        {
            if (!IsUsable(zone)) return null;

            int count = zone.nodes.Count;
            if (from < 0 || from >= count) return null;
            if (to < 0 || to >= count || to == from) return null;

            var cameFrom = new int[count];
            for (int i = 0; i < count; i++) cameFrom[i] = None;
            cameFrom[from] = from;

            var queue = new Queue<int>();
            queue.Enqueue(from);

            while (queue.Count > 0)
            {
                int at = queue.Dequeue();

                for (int next = 0; next < count; next++)
                {
                    if (cameFrom[next] != None) continue;
                    if (!Adjacent(zone, at, next)) continue;

                    cameFrom[next] = at;
                    if (next == to) return Unwind(cameFrom, from, to);

                    // Anything unvisited is a leaf: it can be where a route ends but never part of
                    // the middle of one, so it is marked as seen and not expanded.
                    if (CanPassThrough(zone, next, avoidHunted)) queue.Enqueue(next);
                }
            }

            return null;
        }

        // Every node a route could end at from here. One search rather than one per node, which is
        // what both callers want: the map lights a marker on each of them, and the travel gate is
        // a membership test against exactly this set.
        public static HashSet<int> Reachable(ZoneManager zone, int from, bool avoidHunted = true)
        {
            var found = new HashSet<int>();
            if (!IsUsable(zone)) return found;

            int count = zone.nodes.Count;
            if (from < 0 || from >= count) return found;

            var seen = new bool[count];
            seen[from] = true;

            var queue = new Queue<int>();
            queue.Enqueue(from);

            while (queue.Count > 0)
            {
                int at = queue.Dequeue();

                for (int next = 0; next < count; next++)
                {
                    if (seen[next]) continue;
                    if (!Adjacent(zone, at, next)) continue;

                    seen[next] = true;
                    found.Add(next);
                    if (CanPassThrough(zone, next, avoidHunted)) queue.Enqueue(next);
                }
            }

            return found;
        }

        // The same set, kept for the length of a frame. Two callers ask for it and neither asks
        // once: the gate on every click, and the map's marker once per node while it rebuilds. A
        // frame is a short enough life for it, since the only thing that changes the answer is the
        // party moving.
        private static int _frame = -1;
        private static int _from = None;
        private static HashSet<int> _reachable;

        public static HashSet<int> ReachableThisFrame(ZoneManager zone)
        {
            int from = zone.currentNodeIndex;
            if (Time.frameCount == _frame && from == _from) return _reachable;

            _frame = Time.frameCount;
            _from = from;
            _reachable = Reachable(zone, from);
            return _reachable;
        }

        private static List<int> Unwind(int[] cameFrom, int from, int to)
        {
            var hops = new List<int>();
            for (int at = to; at != from; at = cameFrom[at]) hops.Add(at);
            hops.Reverse();
            return hops;
        }
    }
}
