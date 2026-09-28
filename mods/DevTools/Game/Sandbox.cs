using HarmonyLib;
using UnityEngine;

namespace DevTools
{
    // While DevTools is loaded, no run earns anything that lasts: a run driven by an agent, or one
    // with a legendary memory dropped from the picker, is a test, and a profile that grew from
    // tests would no longer say how far its owner has actually come.
    //
    // It is every run while the mod is loaded, not only runs where a cheat was used, because an
    // agent playing honestly through the API is still a test, and "was anything done to this run"
    // is a question with more ways to be wrong than to be right. To play for real, switch DevTools
    // off in the mod manager.
    //
    // What a run can leave on the profile, and where each is stopped:
    //
    //   traveler mastery   DewSave.ConsumeGameResult asks Dew.GetRewardedMasteryPoints how much
    //                      the run earned, then adds it and reports it on the result screen -
    //                      zero there covers both. A conceded run is finalised the same way.
    //   stardust           reaches the profile only through DewPlayer's RpcGiveStardust handler
    //                      (boss souls and the like).
    //   achievements       tracked from run start by AchievementManager, which also hands out
    //                      the heroes, memories, essences and lucid dreams they unlock. Not
    //                      tracked means neither progress nor completion - Steam's included.
    //   reveries           the daily and special quests, tracked the same way, which pay stardust.
    //   discoveries        picking up or dismantling an unseen memory or essence marks it found
    //                      in the collection; a picker that can spawn any of them would otherwise
    //                      fill the collection in a minute.
    //
    // **What is left alone:** the per-hero statistics (kills, damage, play count and time) and the
    // result history, which the result screen is built from. Those count things; they are not
    // points, and blanking them would mean blanking the screen a test often exists to read.
    internal static class Sandbox
    {
        public static void Note(string what) => Debug.Log("[DevTools] test run: " + what + " withheld");

        public static bool InRun => NetworkedManagerBase<GameManager>.softInstance != null;
    }

    [HarmonyPatch(typeof(Dew), nameof(Dew.GetRewardedMasteryPoints))]
    internal static class NoMasteryPatch
    {
        private static bool Prefix(ref long __result)
        {
            __result = 0L;
            Sandbox.Note("traveler mastery");
            return false;
        }
    }

    [HarmonyPatch(typeof(DewPlayer), "UserCode_RpcGiveStardust__Int32")]
    internal static class NoStardustPatch
    {
        private static bool Prefix(int amount)
        {
            Sandbox.Note(amount + " stardust");
            return false;
        }
    }

    [HarmonyPatch(typeof(AchievementManager), nameof(AchievementManager.StartTrackingAchievements))]
    internal static class NoAchievementTrackingPatch
    {
        private static bool Prefix()
        {
            Sandbox.Note("achievement tracking");
            return false;
        }
    }

    // Belt and braces: nothing should reach it with tracking off, and an achievement is the one
    // thing here that cannot be taken back.
    [HarmonyPatch(typeof(AchievementManager), nameof(AchievementManager.CompleteAchievement))]
    internal static class NoAchievementPatch
    {
        private static bool Prefix(DewAchievementItem item)
        {
            Sandbox.Note("achievement " + (item != null ? item.name : "?"));
            return false;
        }
    }

    // Tracking that had already started when the mod loaded - a reload mid-run - writes what it
    // counted to the profile and to Steam's stats when it stops. This is where it writes.
    [HarmonyPatch(typeof(DewAchievementItem), nameof(DewAchievementItem.FlushProgressToProfile))]
    internal static class NoAchievementProgressPatch
    {
        private static bool Prefix(ref bool __result)
        {
            __result = false;
            return false;
        }
    }

    [HarmonyPatch(typeof(DewReverieItem), nameof(DewReverieItem.SaveReverieStateToData))]
    internal static class NoReverieProgressPatch
    {
        private static bool Prefix() => false;
    }

    [HarmonyPatch(typeof(DewReverieItem), nameof(DewReverieItem.OnComplete))]
    internal static class NoReverieCompletionPatch
    {
        private static bool Prefix() => false;
    }

    [HarmonyPatch(typeof(InGameReverieManager), "StartTrackingReveries")]
    internal static class NoReverieTrackingPatch
    {
        private static bool Prefix()
        {
            Sandbox.Note("reverie tracking");
            return false;
        }
    }

    [HarmonyPatch(typeof(InGameReverieManager), nameof(InGameReverieManager.CompleteReverie))]
    internal static class NoReveriePatch
    {
        private static bool Prefix() => false;
    }

    // Only in a run: outside one, the same calls are the profile's own upkeep at load.
    [HarmonyPatch(typeof(DewProfile), nameof(DewProfile.DiscoverSkill))]
    internal static class NoSkillDiscoveryPatch
    {
        private static bool Prefix(ref bool __result)
        {
            if (!Sandbox.InRun) return true;
            __result = false;
            return false;
        }
    }

    [HarmonyPatch(typeof(DewProfile), nameof(DewProfile.DiscoverGem))]
    internal static class NoGemDiscoveryPatch
    {
        private static bool Prefix(ref bool __result)
        {
            if (!Sandbox.InRun) return true;
            __result = false;
            return false;
        }
    }

    [HarmonyPatch(typeof(DewProfile), nameof(DewProfile.DiscoverArtifact))]
    internal static class NoArtifactDiscoveryPatch
    {
        private static bool Prefix() => !Sandbox.InRun;
    }
}
