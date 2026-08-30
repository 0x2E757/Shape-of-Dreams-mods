using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace MapAutoRoute
{
    // What the mod draws on the map: the route, in the map's own line.
    internal static class MapLines
    {
        // The map that draws the whole world, as opposed to the minimap in the corner. Captured
        // when it enables rather than searched for - FindObjectOfType cannot see it while it is
        // switched off, which is most of the time.
        public static UI_InGame_WorldMap MainMap { get; private set; }

        private static readonly AccessTools.FieldRef<UI_InGame_World_Edge, UI_InGame_World_NodeItem>
            EdgeA = AccessTools.FieldRefAccess<UI_InGame_World_Edge, UI_InGame_World_NodeItem>("_a");

        private static readonly AccessTools.FieldRef<UI_InGame_World_Edge, UI_InGame_World_NodeItem>
            EdgeB = AccessTools.FieldRefAccess<UI_InGame_World_Edge, UI_InGame_World_NodeItem>("_b");

        private static readonly AccessTools.FieldRef<UI_InGame_World_Edge, UI_InGame_WorldMap>
            EdgeMap = AccessTools.FieldRefAccess<UI_InGame_World_Edge, UI_InGame_WorldMap>("_parent");

        public static void Remember(UI_InGame_WorldMap map)
        {
            if (map != null && map.isMain) MainMap = map;
        }

        public static void Reset()
        {
            // A Material made with new() is ours to destroy; nothing else will.
            if (_tinted != null) Object.Destroy(_tinted);
            _tinted = null;
            _source = null;
            _tintIsInMaterial = false;

            MainMap = null;
            _hoverFrom = _hoverTo = NodeGraph.None;
            _voteFrom = _voteTo = NodeGraph.None;
            _hoverHops = _voteHops = null;
        }

        // Edges repaint on exactly one signal: onHoveringNodeChanged, which every edge subscribes
        // to in its Setup. A vote starting is not something that raises it, so this raises it by
        // hand rather than the mod growing a painting loop of its own.
        //
        // The game's UpdateStatus ignores both of its arguments and reads _parent.hoveringNode
        // instead, so what is passed is immaterial - it only has to be called.
        public static void Repaint()
        {
            var map = MainMap;
            if (map == null) return;

            int hovering = map.hoveringNode;
            map.onHoveringNodeChanged?.Invoke(hovering, hovering);
        }

        // ----- painting -------------------------------------------------------------

        public static void PaintEdge(UI_InGame_World_Edge edge)
        {
            if (!MapAutoRouteMod.IsLive || edge == null || edge.lineRenderer == null) return;

            var zone = ZoneManager.softInstance;
            if (zone == null) return;

            var a = EdgeA(edge);
            var b = EdgeB(edge);
            if (a == null || b == null) return;

            int from = zone.currentNodeIndex;

            // A vote first, because it is a decision rather than a question. It also cannot draw
            // itself once this mod is loaded: the game's line for a vote is the single edge
            // between the party and the node voted on, and with the gate widened there may not be
            // one. The route stands in for it.
            //
            // Every client works this out from its own copy of the same synced state - isVoting,
            // voteType, voteData, currentNodeIndex, the node list and the distance matrix all
            // cross the wire - so the route is recomputed rather than sent, and everyone running
            // the mod sees the same one.
            if (StepOf(from, VoteRoute(zone), a.index, b.index, out bool reversed))
            {
                Paint(edge, reversed, blue: false);
                return;
            }

            if (StepOf(from, HoverRoute(zone, EdgeMap(edge)), a.index, b.index, out reversed))
                Paint(edge, reversed, blue: true);
        }

        // #8ab4ff, the same blue the tooltip's line uses, so the two say the same thing in the
        // same colour.
        private static readonly Color RouteTint = new Color(0.54f, 0.71f, 1f);

        // matTravel with the blue written into it, made once and owned by the mod. The game's own
        // copy is shared with the vote line on every other edge and is not ours to recolour.
        //
        // Writing Graphic.color instead - which is per instance and would have needed no copy at
        // all - does nothing visible, and asking the running game why answered it outright. The
        // material is `matEdgeTravel` on `AllIn1SpriteShader/AllIn1SpriteShaderUiMask`, and:
        //
        //     _MainTex : none
        //     _Color   : RGBA(1.000, 0.235, 0.149, 1.000)
        //
        // There is no texture. The line is `_Color` and nothing else, so a vertex tint has nothing
        // to tint and the property is the only way in. The list is longer than the one name that
        // turned out to matter so that a shader swapped in a game patch has somewhere to land, and
        // if none of them is there the vertex colour is still tried rather than nothing.
        private static readonly string[] TintProperties =
            { "_Color", "_BaseColor", "_TintColor", "_FaceColor", "_MainColor", "_EmissionColor" };

        private static Material _source;
        private static Material _tinted;
        private static bool _tintIsInMaterial;

        private static Material Tinted(Material source)
        {
            if (source == _source) return _tinted;

            if (_tinted != null) Object.Destroy(_tinted);

            _source = source;
            _tinted = new Material(source) { name = source.name + " (MapAutoRoute)" };
            _tintIsInMaterial = false;

            foreach (string property in TintProperties)
            {
                if (!_tinted.HasProperty(property)) continue;

                _tinted.SetColor(property, RouteTint);
                _tintIsInMaterial = true;
            }

            return _tinted;
        }

        // Two colours, because the two lines are saying different things.
        //
        // A route under the pointer is the mod's own answer to a question the player is still
        // asking, and it is blue. A route under a vote is the game's line - the party is deciding
        // where to go, which is the game's own moment - so it keeps matTravel exactly as authored,
        // red, and the mod only extends it along the rooms in between. Nothing is written to the
        // colour there either: UpdateStatus has already set it, white or the tint between two
        // hunted nodes, and that is the colour it should have.
        private static void Paint(UI_InGame_World_Edge edge, bool reversed, bool blue)
        {
            var material = edge.matTravel;
            if (material == null) return;

            if (blue)
            {
                edge.lineRenderer.material = Tinted(material);

                // White where the material carries the blue, or the two multiply into something
                // much darker than either.
                edge.lineRenderer.color = _tintIsInMaterial ? Color.white : RouteTint;
            }
            else
            {
                edge.lineRenderer.material = material;
            }

            if (!reversed) return;

            // The game does this same swap for the line it draws under a travel vote: the line
            // animates from _a towards _b, so the two ends have to be in travel order for it to
            // run away from the party rather than back at them.
            var a = EdgeA(edge);
            EdgeA(edge) = EdgeB(edge);
            EdgeB(edge) = a;
        }

        // Whether this edge is one step of a chain that starts at `from`, and if so whether the
        // edge is holding its two ends the wrong way round for a line read outwards along it.
        private static bool StepOf(int from, List<int> hops, int a, int b, out bool reversed)
        {
            reversed = false;
            if (hops == null) return false;

            int previous = from;
            foreach (int hop in hops)
            {
                if (a == previous && b == hop) return true;
                if (a == hop && b == previous) { reversed = true; return true; }
                previous = hop;
            }

            return false;
        }

        // ----- the two routes worth drawing -----------------------------------------
        //
        // Two caches rather than one: both are asked for on every edge of every repaint, and a
        // single slot would thrash between them.

        private static int _hoverFrom = NodeGraph.None;
        private static int _hoverTo = NodeGraph.None;
        private static List<int> _hoverHops;

        private static int _voteFrom = NodeGraph.None;
        private static int _voteTo = NodeGraph.None;
        private static List<int> _voteHops;

        private static List<int> HoverRoute(ZoneManager zone, UI_InGame_WorldMap map)
        {
            if (map == null) return null;

            int from = zone.currentNodeIndex;
            int to = map.hoveringNode;                     // -1 when nothing is hovered
            if (!Worth(zone, from, to)) return null;

            if (from == _hoverFrom && to == _hoverTo) return _hoverHops;

            _hoverFrom = from;
            _hoverTo = to;
            _hoverHops = NodeGraph.FindRoute(zone, from, to);
            return _hoverHops;
        }

        private static List<int> VoteRoute(ZoneManager zone)
        {
            if (!zone.isVoting || zone.voteType != VoteType.NextNode) return null;

            int from = zone.currentNodeIndex;
            int to = zone.voteData;
            if (!Worth(zone, from, to)) return null;

            if (from == _voteFrom && to == _voteTo) return _voteHops;

            _voteFrom = from;
            _voteTo = to;
            _voteHops = NodeGraph.FindRoute(zone, from, to);
            return _voteHops;
        }

        // A node already one step away needs nothing from this mod: the game draws that edge
        // itself, in whichever material the situation calls for.
        private static bool Worth(ZoneManager zone, int from, int to)
        {
            if (to < 0 || from < 0 || to == from) return false;
            if (!NodeGraph.IsUsable(zone)) return false;
            return !zone.IsNodeConnected(from, to);
        }
    }

    [HarmonyPatch(typeof(UI_InGame_WorldMap), "OnEnable")]
    internal static class WorldMapEnabledPatch
    {
        private static void Postfix(UI_InGame_WorldMap __instance) => MapLines.Remember(__instance);
    }

    [HarmonyPatch(typeof(UI_InGame_World_Edge), nameof(UI_InGame_World_Edge.UpdateStatus))]
    internal static class EdgeStatusPatch
    {
        private static void Postfix(UI_InGame_World_Edge __instance) => MapLines.PaintEdge(__instance);
    }

    // The marker that says a node can be travelled to. Setup writes
    // IsNodeConnected(current, index) into it with the gate shut, which is exactly the statement
    // this mod makes untrue - so the same marker is lit for anywhere a route reaches.
    [HarmonyPatch(typeof(UI_InGame_World_NodeItem), nameof(UI_InGame_World_NodeItem.Setup))]
    internal static class NodeMarkerPatch
    {
        private static void Postfix(UI_InGame_World_NodeItem __instance)
        {
            if (!MapAutoRouteMod.IsLive || __instance == null || __instance.isMiniMapVariant) return;

            var marker = __instance.canTraverseObject;
            if (marker == null || marker.activeSelf) return;

            var ui = InGameUIManager.softInstance;
            var zone = ZoneManager.softInstance;
            if (ui == null || zone == null) return;

            // The same condition the game puts on the marker: it means "you can go here now", and
            // with the map open only for looking at, nobody can go anywhere.
            if (ui.isWorldDisplayed != WorldDisplayStatus.Shown) return;

            if (NodeGraph.ReachableThisFrame(zone).Contains(__instance.index)) marker.SetActive(true);
        }
    }
}
