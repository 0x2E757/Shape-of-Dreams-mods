using HarmonyLib;

namespace MapAutoRoute
{
    // The map's node tooltip ends its first line with a red "(Too Far To Travel)" for anything
    // more than one step out:
    //
    //     if (isWorldDisplayed == Shown && GetNodeDistance(currentNodeIndex, index) > 1)
    //         text.text += " <color=#caa>" + GetUIValue("InGame_Tooltip_WorldNode_TooFarToTravel") + "</color>";
    //
    // It measures with GetNodeDistance rather than IsNodeConnected, so the gate never reaches it -
    // which is just as well, because the honest replacement is not "you can go here" but how far
    // away here is, and at what risk.
    [HarmonyPatch(typeof(UI_Tooltip_WorldNode_Description),
                  nameof(UI_Tooltip_WorldNode_Description.OnSetup))]
    internal static class NodeTooltipPatch
    {
        private const string TooFarKey = "InGame_Tooltip_WorldNode_TooFarToTravel";

        private const string Clear = "#8ab4ff";      // the route's own blue
        private const string Risky = "#e0b070";      // amber: it can be walked, but not safely
        private const string Blocked = "#cc9999";    // near enough the colour the game refuses in

        private static void Postfix(UI_Tooltip_WorldNode_Description __instance)
        {
            if (!MapAutoRouteMod.IsLive || __instance == null || __instance.text == null) return;
            if (!(__instance.currentObject is int index)) return;

            var zone = ZoneManager.softInstance;
            if (zone == null) return;

            // The span the game appended, rebuilt rather than looked for. It is a concatenation of
            // three things that can each be asked for by name, and matching on the localized text
            // alone would be a guess about which language the player is reading it in.
            string tooFar = " <color=#caa>" + DewLocalization.GetUIValue(TooFarKey) + "</color>";

            string text = __instance.text.text;
            if (string.IsNullOrEmpty(text) || !text.Contains(tooFar)) return;

            int from = zone.currentNodeIndex;

            // The route the mod would actually take, which goes around hunters rather than into
            // them. When there is none, the same search without that rule answers why: a way that
            // exists but for the hunt is a refusal worth explaining, and no way at all is the
            // game's own message, which is left exactly where it is.
            var hops = NodeGraph.FindRoute(zone, from, index);
            if (hops == null || hops.Count == 0)
            {
                if (NodeGraph.FindRoute(zone, from, index, avoidHunted: false) == null) return;

                __instance.text.text = Swap(text, tooFar, Localization.Get(Localization.Prevented), Blocked);
                return;
            }

            // The route costs a turn of the hunt per room crossed, so how long it takes and what
            // it might meet on the way are the same question, and the line answers both. The hunt
            // moves while the party walks: a room clear when they set off can be taken before they
            // reach it, which is what this warns about and what the walk itself then stops at.
            bool risky = Hunt.MayBeCaught(zone, hops);

            string reads = string.Format(
                Localization.Get(risky ? Localization.TravelHunted : Localization.Travel), hops.Count);

            // Amber rather than blue when there is something on the way, since a warning that
            // reads like the rest of the line is not a warning.
            __instance.text.text = Swap(text, tooFar, reads, risky ? Risky : Clear);
        }

        private static string Swap(string text, string tooFar, string reads, string colour)
        {
            return text.Replace(tooFar, " <color=" + colour + ">" + reads + "</color>");
        }
    }
}
