using System.Reflection;
using HarmonyLib;
using Mirror;
using UnityEngine;

namespace MapAutoRoute
{
    // Walking a route rather than jumping it.
    //
    // A travel is a room load plus a short sequence of world updates, and only the load is
    // expensive. The updates are the map moving on by one square, and this is all of them, taken
    // from the routine behind `ZoneManager.LoadNode`:
    //
    //     currentTurnIndex++;
    //     if (settings.newZone == null) AdvanceHunterTurn(false);
    //     if (!isSidetrackTransition) UpdateModifiersByHunterStatus(settings.to);
    //     SetCurrentNodeIndexAndRevealAdjacent(settings.to);
    //
    // So a route is replayed as that sequence once per room crossed, and only the last room is
    // actually loaded. The hunt therefore advances one turn per room, exactly as it would have if
    // every room had been walked through - which is the point. A route is a way of not replaying
    // rooms that are already cleared, not a way of outrunning what is chasing you.
    internal static class Walk
    {
        // The only part of that sequence the game does not make public.
        private static readonly MethodInfo SetTurnIndex =
            AccessTools.PropertySetter(typeof(ZoneManager), "currentTurnIndex");

        public static void StepOnto(ZoneManager zone, int node)
        {
            // Said once rather than swallowed. A missing setter would leave the turn counter
            // standing still while everything else moved, which is the kind of wrong that looks
            // like nothing at all until a quest that counts turns disagrees.
            if (SetTurnIndex == null) Complain();
            else SetTurnIndex.Invoke(zone, new object[] { zone.currentTurnIndex + 1 });

            zone.AdvanceHunterTurn(false);
            zone.UpdateModifiersByHunterStatus(node);
            zone.SetCurrentNodeIndexAndRevealAdjacent(node);
        }

        private static bool _complained;

        private static void Complain()
        {
            if (_complained) return;

            _complained = true;
            Debug.LogWarning("[MapAutoRoute] ZoneManager.currentTurnIndex has no setter to call - " +
                             "routes will cross rooms without counting the turns");
        }
    }

    // The server's travel, with the rooms in between walked through first.
    //
    // Patching here rather than at the command has two reasons. Every path that decides a travel
    // arrives at this method - a click, a co-op vote completing, an event - so there is one place
    // to be right. And every check the command makes has already passed by the time it is called,
    // so nothing is replayed for a travel that then turns out to be refused.
    [HarmonyPatch(typeof(ZoneManager), nameof(ZoneManager.TravelToNode))]
    internal static class TravelToNodeWalkPatch
    {
        // __0 to, __1 advanceTurn, __2 isSidetrackTransition.
        private static void Prefix(ZoneManager __instance, ref int __0, bool __1, bool __2)
        {
            if (!MapAutoRouteMod.IsLive || __instance == null) return;

            // Not every travel is a party walking to the next room. Returning from a sidetrack
            // passes a node nowhere near the current one on purpose, and passes advanceTurn false
            // to say so; neither wants rooms replayed underneath it.
            if (!__1 || __2) return;

            if (!NetworkServer.active) return;
            if (!NodeGraph.IsUsable(__instance)) return;

            int from = __instance.currentNodeIndex;
            if (from < 0 || __0 < 0 || __0 >= __instance.nodes.Count) return;
            if (NodeGraph.Adjacent(__instance, from, __0)) return;      // one room: nothing to replay

            var hops = NodeGraph.FindRoute(__instance, from, __0);
            if (hops == null || hops.Count < 2) return;

            // Shut the gate before anything below runs, and this is not a precaution.
            // AdvanceHunterTurn and SetCurrentNodeIndexAndRevealAdjacent both ask
            // IsNodeConnected what is next to what: with it still widened - and it is, because
            // this runs inside the command that opened it - the hunt would spread across the
            // whole route in a single turn and the map would reveal itself along with it.
            Widen.Close();

            for (int i = 0; i < hops.Count - 1; i++)
            {
                // A room with a hunter in it is not something to be simulated past. That is where
                // the walk ends, and it ends there for real: the travel below is redirected to it,
                // so the party arrives in that room and meets what is waiting.
                if (Hunt.IsHunted(__instance, hops[i]))
                {
                    Debug.Log($"[MapAutoRoute] caught at node {hops[i]} on the way to {__0}");
                    __0 = hops[i];
                    return;
                }

                Walk.StepOnto(__instance, hops[i]);
            }

            // Every room but the last has been crossed, so what the game is about to load is now
            // genuinely one step away.
        }
    }
}
