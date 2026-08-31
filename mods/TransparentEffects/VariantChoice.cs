using System;
using HarmonyLib;

namespace TransparentEffects
{
    // Which of the two variants a spawned effect gets, if either.
    //
    // DewResources.GetSuggestedVarDef is the single place both halves of the game ask that
    // question: Actor.CreateAbilityInstance asks it when the effect is made, and
    // SpawnManager.SpawnFromDewDatabaseHandler asks it again on each client when Mirror tells that
    // client the effect exists. So one postfix covers a host and a guest and needs no second copy
    // for either.
    //
    // **A postfix here rather than a processor added to each Entity**, which is where the game
    // puts its own. Entity.Awake adds a delegate to that entity's spawnedChildVarDefProcessor
    // list, and a mod doing the same would have to find every entity alive at unload to take the
    // delegate back out again. This is one method, patched and unpatched.
    //
    // The condition below is the game's own, mirrored. It reads:
    //
    //   - only AbilityInstance subclasses, which is what "skill effect" means here. A hero's own
    //     model, the monsters, the room itself are all spawned through the same call and none of
    //     them is what this mod is about;
    //   - only effects belonging to a human player;
    //   - and "mine" means the player the camera is following, not the player at the keyboard,
    //     so that spectating a teammate shows their effects the way they would see them.
    [HarmonyPatch(typeof(DewResources), nameof(DewResources.GetSuggestedVarDef))]
    internal static class VariantChoice
    {
        private static void Postfix(Actor parentActor, Type childType, ref VariantDef __result)
        {
            var config = TransparentEffectsMod.Live;
            if (config == null || !Dimming.Ready) return;
            if (parentActor == null || childType == null) return;
            if (!childType.IsSubclassOf(typeof(AbilityInstance))) return;

            // The spawning actor may be the effect of an effect, so the owner is the nearest
            // Entity up the parent chain - firstEntity starts at the actor itself and walks up.
            var source = parentActor.firstEntity;
            if (source == null) return;

            var owner = source.owner;
            if (owner == null || !owner.isHumanPlayer) return;

            var viewer = Viewer();
            if (viewer == null) return;

            bool mine = viewer == owner;
            float alpha = mine ? config.myOwnEffects : config.otherPlayersEffects;
            if (alpha >= Dimming.NoChange) return;

            int id = mine ? Dimming.Mine : Dimming.Others;
            if (__result.Contains(id)) return;

            // A VariantDef holds six ids and DewResources.GetVariant only ever runs the first
            // four, so an id in the fifth slot would be carried around, would change the cache key
            // and would do nothing at all. Better to leave the effect alone than to fill the cache
            // with copies that are identical to the originals.
            if (Filled(__result) >= 4) return;

            __result = __result.Add(id);
        }

        // Whose point of view "mine" is. The local player normally; the spectated player while the
        // camera is following someone else, which is what the game's own toned-down check does.
        private static DewPlayer Viewer()
        {
            var camera = CameraManager.softInstance;
            if (camera != null)
            {
                var focused = camera.focusedEntity;
                if (focused != null && focused.owner != null && focused.owner.isHumanPlayer) return focused.owner;
            }

            return DewPlayer.local;
        }

        private static int Filled(VariantDef def)
        {
            int count = 0;
            if (def.id0 != 0) count++;
            if (def.id1 != 0) count++;
            if (def.id2 != 0) count++;
            if (def.id3 != 0) count++;
            if (def.id4 != 0) count++;
            if (def.id5 != 0) count++;
            return count;
        }
    }
}
