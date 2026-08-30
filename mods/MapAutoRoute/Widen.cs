using System.Collections.Generic;
using HarmonyLib;

namespace MapAutoRoute
{
    // The whole mechanism, in one idea: for the length of two of the game's own methods, and for
    // no longer, "adjacent" is widened to mean "reachable through rooms already cleared".
    //
    // Everything else follows from the game's code without being touched. `TravelToNode` stops
    // refusing the click, so the hunter warning and the messages are about the node actually being
    // travelled to; `UserCode_CmdTravelToNode` stops refusing the command, so the server reaches
    // its ordinary `TravelToNode(index, advanceTurn: true, ...)`. What that then does with a
    // distant node - crossing the rooms in between one turn at a time, and loading only the last
    // one - is Walk.cs.
    //
    // The scope is what keeps it honest. `IsNodeConnected` has other callers, and one of them is
    // `RefreshNodes`, which decides *which edges the map draws*: widened there, the map would grow
    // lines between rooms that are not connected at all.
    internal static class Widen
    {
        private static HashSet<int> _reachable;
        private static int _from = NodeGraph.None;

        // Called from the prefix of each method the widening is meant to cover. The set is
        // computed before it is published, so nothing can observe a half-open gate.
        public static void Open(ZoneManager zone)
        {
            Close();
            if (!MapAutoRouteMod.IsLive || zone == null) return;

            int from = zone.currentNodeIndex;
            if (from < 0) return;

            var reachable = NodeGraph.ReachableThisFrame(zone);
            _from = from;
            _reachable = reachable;
        }

        public static void Close()
        {
            _reachable = null;
            _from = NodeGraph.None;
        }

        public static bool Allows(int a, int b)
        {
            if (_reachable == null) return false;
            if (a == _from) return _reachable.Contains(b);
            if (b == _from) return _reachable.Contains(a);
            return false;
        }
    }

    // The gate itself. Shut by default and opened only inside the two methods below, so every
    // other caller - RefreshNodes deciding which edges exist, most of all - sees the real graph.
    [HarmonyPatch(typeof(ZoneManager), nameof(ZoneManager.IsNodeConnected))]
    internal static class IsNodeConnectedPatch
    {
        private static bool Prefix(int __0, int __1, ref bool __result)
        {
            if (!Widen.Allows(__0, __1)) return true;

            __result = true;
            return false;
        }
    }

    // Where the click stops being refused. Nothing is rewritten: the index stays the node the
    // player asked for, so the hunter warning, the messages and any vote are all about the place
    // they are actually going.
    [HarmonyPatch(typeof(UI_InGame_WorldMap), nameof(UI_InGame_WorldMap.TravelToNode))]
    internal static class WorldMapTravelPatch
    {
        private static void Prefix(UI_InGame_WorldMap __instance)
        {
            MapLines.Remember(__instance);

            var ui = InGameUIManager.softInstance;
            if (ui == null) return;

            // A mock exit hands the click to a UnityEvent of its own instead of travelling, and
            // what that event would make of a node three rooms away is its business, not this
            // mod's.
            if (ui.currentMockExit != null) return;

            Widen.Open(ZoneManager.softInstance);
        }

        private static void Postfix() => Widen.Close();
    }

    // And where the server stops refusing the command. This is the generated body behind
    // CmdTravelToNode rather than the command stub, because a host does not necessarily run the
    // two in one call.
    [HarmonyPatch(typeof(ZoneManager), "UserCode_CmdTravelToNode__Int32__NetworkConnectionToClient")]
    internal static class CmdTravelToNodePatch
    {
        private static void Prefix(ZoneManager __instance) => Widen.Open(__instance);

        private static void Postfix() => Widen.Close();
    }
}
