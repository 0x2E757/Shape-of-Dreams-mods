using HarmonyLib;
using System.Reflection;

namespace ControlledMerge
{
    // The game allows one essence of each kind per hero, and says so in exactly three places, all
    // of them through HeroSkill.TryGetEquippedGemOfSameType:
    //
    //   * Gem.OnInteract, on the server: an essence of a kind already worn is merged into it
    //     (HeroSkill.MergeGem) instead of going to the hand;
    //   * HeroSkill.EquipGem throws "Tried to equip more than one of same type of gem";
    //   * UI_InGame_Interact_Gem shows "Combine" rather than "Equip" over one on the ground.
    //
    // So answering "none worn" is the whole of it, and all three follow: the essence goes to the
    // hand, EditSkillManager opens the slot choice as it does for any new essence
    // (HeroSkill.OnHoldingObjectChanged -> StartEquipGem), the equip goes through, and the prompt
    // says Equip. HeroSkill.CmdSwapSlotGem needs it as well - it takes both essences out and puts
    // them back one at a time, and the second of two copies would otherwise throw.
    //
    // Nothing else in the game calls it. MergeGem itself does not ask, which is what lets merging
    // stay possible below.
    [HarmonyPatch(typeof(HeroSkill), nameof(HeroSkill.TryGetEquippedGemOfSameType))]
    internal static class OneOfEachLifted
    {
        private static bool Prefix(ref bool __result, ref GemLocation loc, ref Gem gem)
        {
            if (ControlledMergeMod.Live == null) return true;

            loc = default(GemLocation);
            gem = null;
            __result = false;
            return false;
        }
    }

    // Merging is kept, as a choice: with a new essence in hand, choosing the slot of one of the
    // same kind merges the two, as picking it up used to. Choosing an empty slot keeps both.
    //
    // In the stock game choosing an occupied slot replaces what is in it and drops that on the
    // ground, which for two of the same kind only ever means throwing the weaker one away - so
    // taking that click for a merge loses nothing a player would have wanted.
    //
    // **It is decided on the server, in the commands the click already sends.** The click
    // (EditSkillManager.DoClickOnGemSlot) sends CmdUnequipGem for the occupied slot and then
    // CmdEquipGem with the essence in hand. HeroSkill.MergeGem is [Server] with no command in front
    // of it, so an earlier version merged on the clicking machine and only ever for the host: a
    // guest's click replaced the copy, the copy dropped went back to the hand when picked up, and
    // a guest could not merge at all. Taking the unequip for the merge instead works for anyone in
    // the host's party, guests without the mod included, and sends nothing the game does not.
    [HarmonyPatch(typeof(HeroSkill), "UserCode_CmdUnequipGem_Internal__GemLocation__Vector3")]
    internal static class MergeByChoosingItsSlot
    {
        private static bool Prefix(HeroSkill __instance, GemLocation loc)
        {
            var config = ControlledMergeMod.Live;
            if (config == null || !config.mergeByChoosingItsSlot) return true;

            var hero = __instance.hero;
            if (hero == null || !hero.isActive) return true;

            var held = __instance.holdingObject as Gem;
            var there = __instance.GetGem(loc);
            if (!Merge.CanMerge(held, there)) return true;

            Merge.Into(hero, held, there);
            Merge.Resync(__instance, loc);
            return false;
        }
    }

    // The CmdEquipGem that follows: its essence was the one merged away (or, on the host, whose
    // own click runs both commands at once, the hand is already empty and it is null). The stock
    // command would throw and log it. The slot is sent again as well: the clicking machine drew
    // the essence in hand there when it sent the command (SetClientState_SetGemSlot), and nothing
    // else about that slot changes.
    [HarmonyPatch(typeof(HeroSkill), "UserCode_CmdEquipGem_Internal__GemLocation__Gem")]
    internal static class AfterMerge
    {
        private static bool Prefix(HeroSkill __instance, GemLocation loc, Gem gem)
        {
            if (ControlledMergeMod.Live == null) return true;
            if (gem != null && gem.isActive && !Merge.WasMerged(gem)) return true;

            Merge.Resync(__instance, loc);
            return false;
        }
    }

    // And for two copies already worn: in the edit screen, dropping one socketed essence on a
    // socket holding another of its kind merges the dragged one into it, where the stock game
    // would swap the two - which for two of a kind moves nothing worth moving.
    //
    // Every drag from socket to socket ends in HeroSkill.CmdSwapSlotGem (EditSkillManager's
    // HandleGemToGem), which passes (target, source); DevTools' /edit/drag sends the command in the
    // same order. So on the server the second essence goes and the first stays. Like the click,
    // this is the command the drag already sends, and it works for guests as for the host.
    [HarmonyPatch(typeof(HeroSkill), "UserCode_CmdSwapSlotGem_Internal__GemLocation__GemLocation")]
    internal static class MergeByDropping
    {
        private static bool Prefix(HeroSkill __instance, GemLocation a, GemLocation b)
        {
            var config = ControlledMergeMod.Live;
            if (config == null || !config.mergeByChoosingItsSlot) return true;

            var stays = __instance.GetGem(a);
            var goes = __instance.GetGem(b);
            if (!Merge.CanMergeWorn(__instance.hero, goes, stays)) return true;

            __instance.UnequipGem(b, __instance.hero.agentPosition);
            Merge.Remember(goes);
            __instance.MergeGem(goes, stays);

            // The dragging machine drew the two swapped; the removal of b redraws that socket,
            // and a needs sending again.
            Merge.Resync(__instance, a);
            return false;
        }
    }

    internal static class Merge
    {
        public static bool CanMergeWorn(Hero hero, Gem a, Gem b)
        {
            return hero != null && a != null && b != null && a != b &&
                   ReferenceEquals(a.owner, hero) && ReferenceEquals(b.owner, hero) &&
                   a.GetType() == b.GetType();
        }

        // The syncvar's own setter, which is what HeroSkill.EquipGem writes to let go of the
        // essence it equips. Its hook hands the essence back (handOwner null, tempOwner set), and
        // MergeGem then destroys it. The name is the compiler's, hence reflection.
        private static readonly PropertyInfo HoldingObject =
            AccessTools.Property(typeof(HeroSkill), "Network<holdingObject>k__BackingField");

        public static bool CanMerge(Gem held, Gem there)
        {
            return held != null && there != null && held != there &&
                   held.owner == null && held.GetType() == there.GetType();
        }

        // The essences merged away in the last moments, which a command that follows may still name.
        private static readonly System.Collections.Generic.List<Gem> Merged = new System.Collections.Generic.List<Gem>();

        public static void Remember(Gem gem)
        {
            if (Merged.Count > 16) Merged.RemoveAt(0);
            Merged.Add(gem);
        }

        public static bool WasMerged(Gem gem) => Merged.Contains(gem);

        public static void Forget() => Merged.Clear();

        // Sends a slot to every client again, as it is. Mirror's SyncDictionary sends a set even
        // when the value is the same, and the game redraws a slot on it (HeroSkill.OnGemChanged ->
        // OnLocalHeroGemChanged), which corrects what a client drew ahead of the server's answer.
        private static readonly FieldInfo Synced = AccessTools.Field(typeof(HeroSkill), "_syncedGems");

        public static void Resync(HeroSkill skill, GemLocation loc)
        {
            try
            {
                var table = Synced?.GetValue(skill);
                if (table == null) return;
                var item = table.GetType().GetProperty("Item");
                var contains = table.GetType().GetMethod("ContainsKey");
                if (item == null || contains == null || !(bool)contains.Invoke(table, new object[] { loc })) return;
                item.SetValue(table, item.GetValue(table, new object[] { loc }), new object[] { loc });
            }
            catch (System.Exception e)
            {
                UnityEngine.Debug.LogException(e);
            }
        }

        public static void Into(Hero hero, Gem held, Gem there)
        {
            Remember(held);
            if (ReferenceEquals(hero.Skill.holdingObject, held))
            {
                if (HoldingObject != null) HoldingObject.SetValue(hero.Skill, null);
                else hero.Skill.StopHoldInHand();
            }

            hero.Skill.MergeGem(held, there);
        }

        // The quality a merge would give, by the game's own rule.
        public static int QualityAfter(Gem held, Gem there)
        {
            return Gem.GetMergedQuality(held.quality, there.quality);
        }
    }
}
