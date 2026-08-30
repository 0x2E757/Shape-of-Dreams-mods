using System.Collections.Generic;
using UnityEngine;

namespace MapAutoRoute
{
    // What the mod knows about the hunt.
    //
    // `ZoneManager.hunterStatuses` is one `HunterStatus` per node, and the values are not
    // consecutive: None = 0, AboutToBeTaken = 1, then Level1 = 100, Level2 = 101, Level3 = 102.
    // The gap is load-bearing - the game's own test for "there is a hunter here" is a comparison
    // against Level1, so a node that is only *about* to be taken does not count yet.
    //
    // A turn of the hunt, from AdvanceHunterTurn, is three things in order: every status is
    // promoted one step, the nodes next to a taken one become candidates, and as many candidates
    // as the accumulated credit allows are marked AboutToBeTaken.
    //
    //     float pressure = isCurrentNodeHunted ? Mathf.Lerp(0.5f, 0.9f, currentHuntLevel / 5f) : 1f;
    //     float chance   = Mathf.Clamp01(difficulty.hunterSpreadChance * pressure * hunterSpreadMultiplier);
    //     credit += chance * candidates.Count;
    //     while (credit >= 1 && candidates.Count > 0) { credit--; take the best-scoring candidate; }
    //
    // Which candidate is taken is scored and fuzzed, so where the hunt goes next cannot be known.
    // How *far* it can have gone by a given turn can: one node per turn, at the very best.
    internal static class Hunt
    {
        private const HunterStatus Taken = HunterStatus.Level1;

        // The game's own rule, from get_isCurrentNodeHunted: `hunterStatuses[i] >= Level1`.
        public static bool IsHunted(ZoneManager zone, int node)
        {
            if (zone == null || node < 0 || node >= zone.hunterStatuses.Count) return false;
            return zone.hunterStatuses[node] >= Taken;
        }

        // Whether walking this route could put the party in a room with a hunter in it.
        //
        // This is a "may", not a probability, and deliberately so. The number of nodes the hunt
        // takes each turn is arithmetic and could be predicted; *which* ones it takes is a scored
        // pick with fuzziness in it and cannot be. So the answer given is the honest worst case:
        // the front advances one node per turn, and a room is at risk if the front could reach it
        // by the turn the party would walk in.
        public static bool MayBeCaught(ZoneManager zone, List<int> hops)
        {
            if (zone == null || hops == null || !NodeGraph.IsUsable(zone)) return false;

            // Nothing moves while the hunt is switched off, and nothing moves for as long as it
            // is still sitting out its skipped turns.
            if (zone.isHuntAdvanceDisabled) return false;
            int grace = Mathf.Max(0, zone.hunterSkippedTurns);

            for (int i = 0; i < hops.Count; i++)
            {
                // hops[i] is walked into on the (i + 1)th turn of the route.
                if (CouldBeHuntedIn(zone, hops[i], i + 1 - grace)) return true;
            }

            return false;
        }

        private static bool CouldBeHuntedIn(ZoneManager zone, int node, int turns)
        {
            if (IsHunted(zone, node)) return true;
            if (turns <= 0) return false;

            // Distance through the whole graph rather than through cleared rooms: a hunter is not
            // bound by where the party has been.
            //
            // The two lists are the same length in practice and the shorter of them is used
            // anyway, because GetNodeDistance indexes a matrix sized from the other one and this
            // runs on every hover.
            int count = Mathf.Min(zone.hunterStatuses.Count, zone.nodes.Count);

            for (int from = 0; from < count; from++)
            {
                if (zone.hunterStatuses[from] == HunterStatus.None) continue;
                if (zone.GetNodeDistance(from, node) <= turns) return true;
            }

            return false;
        }
    }
}
