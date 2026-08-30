using HarmonyLib;
using UnityEngine;

namespace DevTools
{
    // A run ended from this panel should not be worth anything.
    //
    // What the profile calls points is traveler mastery, and the whole of it goes through one
    // function. `DewSave.ConsumeGameResult` works out what the run earned with
    // `Dew.GetRewardedMasteryPoints(minutes)` - the minutes being combat time, floored at seven
    // per heroic boss kill and one and a half per mini boss - then hands the number to
    // `DewProfileStats.AddMasteryPoints` and reports it on the reward screen as
    // `LastGamePlayReward.heroMasteryPoints`.
    //
    // So returning zero from that one function covers both: the screen shows 0 and the profile
    // gains 0. The screen's delta is `earned - alreadyGranted`, and `alreadyGranted` is zero
    // except when finalising a run that was conceded earlier, so 0 is what it reads.
    //
    // **This is mastery and nothing else.** ConsumeGameResult also accumulates kills, deaths,
    // damage dealt and taken, heals, gold and dream dust into the profile's per-hero statistics,
    // and appends the run to the result history. Those are left alone deliberately: blanking them
    // would mean blanking the result screen too, and reading the result screen is usually the
    // reason for the test run in the first place.
    internal static class ScorelessRun
    {
        // Armed by the knock-out button, disarmed once a result has been consumed - so it covers
        // exactly the run it was asked about and no later one. A run abandoned by closing the
        // game takes the flag with it, which is the right way round for something this cheap.
        public static bool IsArmed { get; private set; }

        public static void Arm()
        {
            IsArmed = true;
            Debug.Log("[DevTools] this run will award no mastery points");
        }

        public static void Disarm() => IsArmed = false;
    }

    [HarmonyPatch(typeof(Dew), nameof(Dew.GetRewardedMasteryPoints))]
    internal static class MasteryPointsPatch
    {
        private static bool Prefix(ref long __result)
        {
            if (!ScorelessRun.IsArmed) return true;

            __result = 0L;
            return false;
        }
    }

    [HarmonyPatch(typeof(DewSave), nameof(DewSave.ConsumeGameResult))]
    internal static class ConsumeGameResultPatch
    {
        private static void Postfix() => ScorelessRun.Disarm();
    }
}
