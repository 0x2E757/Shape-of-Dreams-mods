using HarmonyLib;
using Mirror;

namespace ControlledMerge
{
    // The essence's tooltip: its numbers, and a line under them.
    //
    // The game writes an essence's description by evaluating the fields it names -
    // DewLocalization.ConvertDescriptionNodesToText reads each ScalingValue off the essence and
    // calls ScalingValue.GetValue on it, and a property such as Gem_C_Efficiency.reducedRatio is
    // simply called, and calls Gem.GetValue itself. Left alone, the first kind would show the full
    // value and the second the cut one, side by side in one sentence.
    //
    // So for the length of one description the essence being described and its factor are held
    // here, and every scaling value evaluated in that time is cut once, at ScalingValue.GetValue
    // - which both kinds end in - while Gem.GetValue stands aside. The description then says what
    // the essence really does, and the line under it says why that is less than it would be.
    //
    // The factor is the one for where the tooltip puts the essence: its own memory if it is in
    // one, and the memory it is being dragged over if it is being dragged - currentObjects[0] is
    // that memory in UI_TooltipManager.ShowGemEquipTooltip's layout, as AreMyGemsCompatible reads
    // it. That is the one worth having: before the move, not after.
    //
    // **DoInGameTooltip, not OnSetup, and that is not taste.** AreMyGemsCompatible patches OnSetup,
    // and Harmony 2.3.6 finds a patch method again by its module's MVID and token, taking the
    // *first* loaded module with that MVID (HarmonyLib.Patch.PatchMethod). Every reload of the mods
    // loads each assembly again from the same bytes - same MVID - so once a session has reloaded,
    // a second mod patching the same method makes Harmony rebuild the first mod's patch against
    // its oldest, dead copy, whose statics are empty: AreMyGemsCompatible's warning simply stopped
    // appearing. DoInGameTooltip is what OnSetup calls to write a live essence's text, only this
    // mod patches it, and AreMyGemsCompatible's line still lands after ours.
    [HarmonyPatch(typeof(UI_Tooltip_GemDescription), "DoInGameTooltip")]
    internal static class Tooltip
    {
        internal static class Scope
        {
            public static Gem Gem;
            public static float Factor = 1f;

            // Both cuts, for the formula, which shows each of them rather than their product.
            public static Share Share = Share.None;

            // Set while a number is evaluated a second time, uncut, for the formula shown with the
            // details key held (FormulaFactor below). Nothing is cut while it is.
            public static bool Suppressed;

            public static bool Active => Gem != null;

            public static void Enter(Gem gem, Share share)
            {
                Gem = share.Cuts ? gem : null;
                Factor = share.Cuts ? share.Factor : 1f;
                Share = share.Cuts ? share : Share.None;
            }

            public static void Clear()
            {
                Gem = null;
                Factor = 1f;
                Share = Share.None;
                Suppressed = false;
            }

            // The factor for gem: the one this description is using, while it is this gem's,
            // and otherwise the one it has where it is.
            public static float FactorFor(Gem gem)
            {
                if (ControlledMergeMod.Live == null || gem == null || Suppressed) return 1f;
                if (ReferenceEquals(Gem, gem)) return Factor;
                return Diminishing.FactorOf(gem);
            }
        }

        internal struct State
        {
            public Share Share;
            public Gem MergesInto;
            public Gem Held;
        }

        private static void Prefix(UI_Tooltip_GemDescription __instance, out State __state)
        {
            __state = default(State);
            Scope.Clear();

            var config = ControlledMergeMod.Live;
            var gem = __instance.currentObject as Gem;
            if (config == null || gem == null) return;

            var local = DewPlayer.local != null ? DewPlayer.local.hero : null;
            __state.Share = Where(__instance, gem, local);

            Scope.Enter(gem, __state.Share);

            // With a new essence in hand, hovering a slot that holds one of its kind shows that
            // slot's essence, plainly: the click that would merge them is the one this says.
            if (HostSettings.TryGet(out var settings) && settings.MergeByChoosingItsSlot && local != null &&
                ReferenceEquals(gem.owner, local))
            {
                var edit = ManagerBase<EditSkillManager>.instance;
                var held = local.Skill.holdingObject as Gem;
                if (edit != null && edit.mode == EditSkillManager.ModeType.EquipGem && Merge.CanMerge(held, gem))
                {
                    __state.MergesInto = gem;
                    __state.Held = held;
                }
            }

            // A worn copy dragged over a socket holding another of its kind: dropping it there
            // merges it in (MergeByDropping), and the dragged one's description says so.
            var into = DropMergesInto(__instance, gem, local);
            if (into != null)
            {
                __state.MergesInto = into;
                __state.Held = gem;
            }
        }

        // The essence a dragged one would be merged into if dropped here, or null.
        private static Gem DropMergesInto(UI_Tooltip_GemDescription instance, Gem gem, Hero local)
        {
            // The host merges on the drop (MergeByDropping), for a guest as for itself.
            if (!HostSettings.TryGet(out var settings) || !settings.MergeByChoosingItsSlot || local == null) return null;

            var objects = instance.currentObjects;
            if (objects == null || objects.Count != 3 || !(objects[0] is SkillTrigger) || !ReferenceEquals(objects[2], gem)) return null;

            var there = objects[1] as Gem;
            return Merge.CanMergeWorn(local, gem, there) ? there : null;
        }

        private static void Postfix(UI_Tooltip_GemDescription __instance, State __state)
        {
            Scope.Clear();

            var config = ControlledMergeMod.Live;
            var text = __instance.text;
            if (config == null || text == null) return;

            string added = string.Empty;

            if (config.showTooltipLine && __state.Share.Cuts)
                added += "\n\n" + Localization.Paint(Localization.Line(__state.Share));

            if (__state.MergesInto != null && __state.Held != null)
            {
                int before = __state.MergesInto.quality;
                int after = Merge.QualityAfter(__state.Held, __state.MergesInto);
                added += "\n\n<color=#9ad0ff>" + Localization.MergeLine(before, after) + "</color>";
            }

            if (added.Length > 0) text.text = text.text + added;
        }

        // A description that throws must not leave every value in the game cut by its factor.
        private static System.Exception Finalizer(System.Exception __exception)
        {
            Scope.Clear();
            return __exception;
        }

        private static Share Where(UI_Tooltip_GemDescription instance, Gem gem, Hero local)
        {
            // The equip layout: [memory, the essence in the slot or null, the one being dragged].
            var objects = instance.currentObjects;
            if (objects != null && objects.Count == 3 && objects[0] is SkillTrigger memory &&
                ReferenceEquals(objects[2], gem) && local != null &&
                local.Skill.TryGetSkillLocation(memory, out var location))
            {
                var displaced = objects[1] as Gem;

                if (displaced != null && displaced.GetType() == gem.GetType() && ReferenceEquals(gem.owner, local))
                {
                    // Dropped here it merges: what is left is the one in this socket, without the
                    // dragged one anywhere.
                    if (DropMergesInto(instance, gem, local) != null)
                        return Diminishing.If(local, displaced, location, gem);

                    // Otherwise two of a kind trade places, which leaves the loadout as it was.
                    return Diminishing.Of(gem);
                }

                return Diminishing.If(local, gem, location, displaced);
            }

            return Diminishing.Of(gem);
        }
    }

    // Where every number of a description ends up, and where the scope above cuts it.
    [HarmonyPatch(typeof(ScalingValue), nameof(ScalingValue.GetValue),
        typeof(int), typeof(float), typeof(float), typeof(float), typeof(float), typeof(float))]
    internal static class DescriptionNumbers
    {
        private static void Postfix(ref ScalingValue __instance, ref float __result)
        {
            var gem = Tooltip.Scope.Gem;
            if (gem == null || Tooltip.Scope.Suppressed) return;
            if (!Diminishing.Scales(__instance) || Diminishing.IsFiringLimit(gem, __instance) || OneCut.Keeps(gem, __instance)) return;

            __result *= Tooltip.Scope.Factor;
        }
    }

    // A memory's tooltip lists the essences in it, each with its description, through one
    // UI_Tooltip_EquippedGemDescriber_Item.Setup per essence. Those are essences in their own
    // sockets, so each is described where it is, with the same scope as its own tooltip. The line
    // under each is only a short note that it is weakened: the memory's tooltip is a list of
    // several essences, the reasons are in the essence's own tooltip, and several full lines drown
    // the descriptions they are about.
    //
    // Setup(DewGameResult.GemData, ...), the run result screen's, has no live essence and no loadout
    // to count, and is left alone.
    [HarmonyPatch(typeof(UI_Tooltip_EquippedGemDescriber_Item), nameof(UI_Tooltip_EquippedGemDescriber_Item.Setup),
        typeof(Gem), typeof(Hero))]
    internal static class MemoryTooltip
    {
        private static void Prefix(Gem gem, out Share __state)
        {
            __state = Share.None;
            Tooltip.Scope.Clear();
            if (ControlledMergeMod.Live == null || gem == null) return;

            __state = Diminishing.Of(gem);
            Tooltip.Scope.Enter(gem, __state);
        }

        private static void Postfix(UI_Tooltip_EquippedGemDescriber_Item __instance, Share __state)
        {
            Tooltip.Scope.Clear();

            var config = ControlledMergeMod.Live;
            if (config == null || !config.showTooltipLine || !__state.Cuts) return;

            var text = __instance.GetComponent<TMPro.TextMeshProUGUI>();
            if (text == null) return;
            text.text = text.text + "\n<size=90%>" + Localization.Paint(Localization.Get(Localization.Short)) + "</size>";
        }

        private static System.Exception Finalizer(System.Exception __exception)
        {
            Tooltip.Scope.Clear();
            return __exception;
        }
    }

    // The formula shown while the details key (Alt) is held.
    //
    // With the key held, DewLocalization.EvaluateAndRenderExpression writes each number as how it
    // is made - the base, each stat's share, the level-scaling sprite - rather than as its total.
    // Cut inside the scope, that formula would show numbers that are simply smaller, with nothing
    // to say why. So in that mode the number is rendered a second time with the cut held off, and
    // that uncut formula is shown with each cut after it, in the order the line under the
    // description lists them: " 60%<sprite=5> x 70% x 75%". A number the cut does not reach
    // renders the same both times and is left as it is.
    [HarmonyPatch(typeof(DewLocalization), nameof(DewLocalization.EvaluateAndRenderExpression))]
    internal static class FormulaFactor
    {
        private static bool _rendering;

        private static void Postfix(DewInternal.ExpressionData exp, DewLocalization.DescriptionSettings settings,
                                    bool shouldShowDetail, ref string __result)
        {
            if (_rendering || !shouldShowDetail || !Tooltip.Scope.Active || Tooltip.Scope.Suppressed) return;

            string uncut;
            _rendering = true;
            Tooltip.Scope.Suppressed = true;
            try
            {
                uncut = DewLocalization.EvaluateAndRenderExpression(exp, settings, shouldShowDetail);
            }
            finally
            {
                Tooltip.Scope.Suppressed = false;
                _rendering = false;
            }

            if (uncut == __result) return;
            __result = uncut.TrimEnd() + " " + Localization.Paint(Localization.Formula(Tooltip.Scope.Share)) + " ";
        }
    }
}
