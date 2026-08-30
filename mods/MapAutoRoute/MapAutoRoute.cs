using UnityEngine;

namespace MapAutoRoute
{
    // Named MapAutoRouteMod rather than MapAutoRoute for the reason DevTools is named DevToolsMod:
    // a class sharing the name of its namespace cannot be referred to from a sibling file without
    // qualifying every use of it. The loader takes any ModBehaviour in the assembly and does not
    // care what it is called.
    //
    // There is almost nothing here, and that is the design. The mod is seven Harmony patches and
    // no state carried between frames, because a travel completes in one:
    //
    //     Widen.cs      what counts as adjacent, for the length of two methods and no longer
    //     Walk.cs       crossing the rooms in between, a turn of the hunt each
    //     MapLines.cs   the route drawn in the map's own line, and the "you can go here" marker
    //     Tooltip.cs    how far it is, and at what risk
    //
    // **There are no settings**, and that is a decision rather than an omission: the only one it
    // ever had was an on-off switch, which is what the mod list already is. `ModBehaviour` finds
    // configs by reflection - public instance fields whose type is a subclass of `ModConfig` - so
    // with none declared, `LoadConfigsToDisk` has nothing to do and the mod manager hides its
    // settings button rather than offering an empty window.
    //
    // **It has to be on the host.** Widening the client's own check only gets the command sent;
    // the server checks adjacency again in UserCode_CmdTravelToNode, and an unmodded host will
    // refuse it without a word.
    public class MapAutoRouteMod : ModBehaviour
    {
        // Whether this copy of the mod is the loaded one. Patch methods are static and a
        // ModBehaviour is not, so a patch reaching for the live mod would be holding a reference
        // to something the loader can destroy under it; one bool is a shorter thing to reason
        // about than a lifetime.
        public static bool IsLive;

        // What the edges were last painted for. A vote is the one thing that changes what should
        // be drawn without the pointer moving, and moving the pointer is the only repaint the game
        // raises by itself.
        private long _paintedVote = long.MinValue;

        private void Awake()
        {
            IsLive = true;
            harmony.PatchAll();
            Debug.Log("[MapAutoRoute] loaded: " + mod.metadata.id);
        }

        private void OnDestroy()
        {
            IsLive = false;
            Widen.Close();

            // Pass the id. The stock template's bare UnpatchAll() takes out every patch in the
            // process, other mods' included.
            harmony.UnpatchAll(harmony.Id);

            // With the patches gone this repaints the edges the way the game would have, so a mod
            // switched off mid-run does not leave a route drawn on the map behind it.
            MapLines.Repaint();
            MapLines.Reset();

            Debug.Log("[MapAutoRoute] unloaded: " + mod.metadata.id);
        }

        private void Update()
        {
            // The gate is meant to be open only inside a single synchronous call, and every method
            // that opens it closes it again on the way out. This is the net under that: a frame
            // boundary is proof nothing is mid-call, and an exception thrown past a postfix is the
            // one way it could have been left open.
            Widen.Close();

            RepaintIfVoteChanged();
        }

        private void RepaintIfVoteChanged()
        {
            var zone = ZoneManager.softInstance;

            long vote = zone != null && zone.isVoting && zone.voteType == VoteType.NextNode
                      ? ((long)zone.currentNodeIndex << 32) | (uint)zone.voteData
                      : -1L;

            if (vote == _paintedVote) return;

            _paintedVote = vote;
            MapLines.Repaint();
        }
    }
}
