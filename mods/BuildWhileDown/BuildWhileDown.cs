using UnityEngine;

namespace BuildWhileDown
{
    // Named BuildWhileDownMod rather than BuildWhileDown for the reason DevTools is named
    // DevToolsMod: a class sharing the name of its namespace cannot be referred to from a sibling
    // file without qualifying every use of it. The loader takes any ModBehaviour in the assembly
    // and does not care what it is called.
    //
    // One idea, and it is a small one: while your own hero is knocked out, the loadout screen goes
    // on working. Nothing else about being down changes - you are still silenced, still stunned,
    // still waiting for someone to reach your soul.
    //
    //     Down.cs           the one question every patch asks, and the two stand-in properties
    //     EditWhileDown.cs  the four places the game says no, and the two it says it in twice
    //     GroundDrops.cs    the one thing that must not be allowed through with the rest
    //
    // **There are no settings**, and that is a decision rather than an omission: the only one it
    // could have is an on-off switch, which is what the mod list already is. ModBehaviour finds
    // configs by reflection - public instance fields whose type is a subclass of ModConfig - so
    // with none declared, LoadConfigsToDisk has nothing to do and the mod manager hides the
    // settings button rather than offering an empty window.
    //
    // **Nobody else needs it, and it changes nobody else's game.** Every gate it opens is a
    // client-side one; the server never had an opinion in the first place. UserCode_CmdEquipGem_
    // Internal and UserCode_CmdEquipSkill_Internal validate ownership and refuse a second essence
    // of the same type, and neither looks at isKnockedOut - so an unmodded host accepts these
    // commands from a modded guest exactly as it accepts them from a standing one.
    public class BuildWhileDownMod : ModBehaviour
    {
        // Whether this copy of the mod is the loaded one. Patch methods are static and a
        // ModBehaviour is not, so a patch reaching for the live mod would be holding a reference
        // to something the loader can destroy under it; one bool is a shorter thing to reason
        // about than a lifetime.
        public static bool IsLive;

        private void Awake()
        {
            IsLive = true;
            harmony.PatchAll();
            Debug.Log("[BuildWhileDown] loaded: " + mod.metadata.id);
        }

        private void OnDestroy()
        {
            IsLive = false;

            // Before unpatching, because the rail is a field on a live manager rather than a
            // patch, and unpatching would leave it standing.
            GroundDrops.Restore();

            // Pass the id. The stock template's bare UnpatchAll() takes out every patch in the
            // process, other mods' included.
            harmony.UnpatchAll(harmony.Id);
            Debug.Log("[BuildWhileDown] unloaded: " + mod.metadata.id);
        }
    }
}
