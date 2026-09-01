using HarmonyLib;
using UnityEngine;

namespace AreMyGemsCompatible
{
    // The line under the essence's own description.
    //
    // UI_Tooltip_GemDescription is the one object that draws an essence's text, and it draws it in
    // every context the essence appears in - the slot on the skill bar, the edit-skill overlay,
    // the shrine, the run result screen, the lobby. So one postfix covers them all, and each
    // context is told apart by what UI_TooltipSection put in currentObjects rather than by which
    // screen is open.
    //
    // Two of those contexts matter and both are already carrying the memory:
    //
    //   * an essence sitting in a slot knows its own memory, on the public Gem.skill syncvar;
    //   * an essence being dragged over a slot has no memory yet, and the game passes the target
    //     memory itself - UI_InGame_GemSlot.ShowTooltip calls ShowGemEquipTooltip(pivot, skill,
    //     currentGem, draggedGem), so currentObjects[0] is the SkillTrigger the essence would land
    //     in. That is the warning worth having: before the swap, not after.
    //
    // The rest resolve to no memory at all and are left alone. The result screen holds a
    // DewGameResult rather than a live Gem; the lobby dejavu tooltip holds an unsocketed one.
    [HarmonyPatch(typeof(UI_Tooltip_GemDescription), "OnSetup")]
    internal static class TooltipWarning
    {
        private static void Postfix(UI_Tooltip_GemDescription __instance)
        {
            var config = AreMyGemsCompatibleMod.Live;
            if (config == null || !config.showTooltipWarning) return;

            var text = __instance.text;
            if (text == null) return;

            var gem = __instance.currentObject as Gem;
            if (gem == null) return;

            var skill = TargetMemory(__instance, gem);
            if (skill == null) return;

            if (Verdict.For(gem, skill) != Compatibility.Dead) return;

            var needs = GemTriggers.Of(gem).Needs;

            // The mark, if it can be drawn here at all. TooltipSprite says so rather than being
            // asked to guess: a <sprite> tag whose name resolves to nothing draws a blank box,
            // which is worse than a line with no icon on it.
            string mark = TooltipSprite.Attach(text) ? TooltipSprite.Tag + " " : string.Empty;

            // The game's own tooltips use rich text throughout, and this is the colour it gives
            // every warning-shaped line it writes itself. The bold is inside the localized string
            // rather than wrapped around part of it here: which clause carries the emphasis, and
            // where it ends, is a question about the sentence and so belongs to whoever wrote it.
            text.text = text.text + "\n\n" + mark +
                        "<color=#ff6b6b>" + Localization.ForNeeds(needs) + "</color>";
        }

        private static SkillTrigger TargetMemory(UI_Tooltip_GemDescription instance, Gem gem)
        {
            // The equip tooltip, where the essence has not moved yet and the memory under the
            // cursor is the one being asked about. currentObjects[0] is only a SkillTrigger in
            // that layout, so the type test is the whole discriminator.
            var objects = instance.currentObjects;
            if (objects != null && objects.Count > 0)
            {
                var target = objects[0] as SkillTrigger;
                if (target != null) return target;
            }

            return gem.skill;
        }
    }
}
