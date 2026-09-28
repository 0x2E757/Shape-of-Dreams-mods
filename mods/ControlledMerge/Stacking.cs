using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace ControlledMerge
{
    // Copies that amplify the same hit, each of them.
    //
    // An essence that amplifies a memory's damage or healing marks the hit as done, so that it is
    // not amplified twice: Gem_C_Lethality.Amplify is
    //
    //     if (... !data.IsAmountModifiedBy(this) ...) { data.ApplyAmplification(...); data.SetAmountModifiedBy(this); }
    //
    // and 25 of the shipped essences do the same (Confidence, Guidance, Sulfur, Bleak, Contempt,
    // Might, Omega, Slippery...). But the mark is kept by *type*: DamageData._modifyFlags is an
    // ActorFlags, a List<Type>, and ActorFlags.Add(Actor) adds actor.GetType(). The stock game
    // never has two of a type, so nothing told the difference - with copies it does: the first
    // Lethality in a memory marks the hit and the second, finding Gem_C_Lethality there, stands
    // aside. Watched in a fight, three Lethality in one memory made one amplification per hit.
    //
    // So for an essence the mark is made to mean "this essence", as the call reads. Beside the
    // type the game adds, the essence itself is kept in a table keyed by that very list, and asked
    // whether the hit is marked, an essence is answered for itself: marked only if it is in the
    // table. Everything else stays as it was:
    //
    //   * FinalDamageData and FinalHealData take the list by ShallowCopy - the same list, so the
    //     same entry;
    //   * damage made from another hit (SetAmountOrigin: a ricochet, a converted heal) takes it by
    //     DeepCopy, a new list with no entry, and there the type alone answers, as in the stock
    //     game: an amplifier that already touched the original does not touch what came of it;
    //   * a type marked by SetAmountModifiedBy(Type), or by anything not an essence, answers by
    //     type.
    //
    // The copies then amplify one after another, each by its own value - already cut by the
    // diminishing - which is what two of them are worth.
    internal static class Stacking
    {
        private static readonly ConditionalWeakTable<List<Type>, List<Gem>> Marks = new ConditionalWeakTable<List<Type>, List<Gem>>();

        private static readonly FieldInfo DamageFlags = AccessTools.Field(typeof(DamageData), "_modifyFlags");
        private static readonly FieldInfo HealFlags = AccessTools.Field(typeof(HealData), "_flags");
        private static readonly FieldInfo FlagList = AccessTools.Field(DamageFlags.FieldType, "_flags");

        private static List<Type> ListOf(object data, FieldInfo flags) =>
            FlagList.GetValue(flags.GetValue(data)) as List<Type>;

        private static void Mark(List<Type> list, Actor actor)
        {
            if (list == null || !(actor is Gem gem)) return;
            var gems = Marks.GetOrCreateValue(list);
            if (!gems.Contains(gem)) gems.Add(gem);
        }

        // Is this hit marked by this very essence? Called only when its type is on the list.
        private static bool MarkedBy(List<Type> list, Gem gem)
        {
            if (list == null || !Marks.TryGetValue(list, out var gems)) return true;
            var type = gem.GetType();
            bool anyOfType = false;
            foreach (var g in gems)
            {
                if (ReferenceEquals(g, gem)) return true;
                if (g != null && g.GetType() == type) anyOfType = true;
            }
            // The type is there but no essence of it put it there itself: marked some other way.
            return !anyOfType;
        }

        [HarmonyPatch(typeof(DamageData), nameof(DamageData.SetAmountModifiedBy), typeof(Actor))]
        internal static class DamageMark
        {
            private static void Postfix(ref DamageData __instance, Actor actor)
            {
                if (ControlledMergeMod.Live == null || !(actor is Gem)) return;
                Mark(ListOf(__instance, DamageFlags), actor);
            }
        }

        [HarmonyPatch(typeof(DamageData), nameof(DamageData.IsAmountModifiedBy), typeof(Actor))]
        internal static class DamageMarked
        {
            private static void Postfix(ref DamageData __instance, Actor actor, ref bool __result)
            {
                if (!__result || ControlledMergeMod.Live == null || !(actor is Gem gem)) return;
                __result = MarkedBy(ListOf(__instance, DamageFlags), gem);
            }
        }

        [HarmonyPatch(typeof(HealData), nameof(HealData.SetAmountModifiedBy), typeof(Actor))]
        internal static class HealMark
        {
            private static void Postfix(ref HealData __instance, Actor actor)
            {
                if (ControlledMergeMod.Live == null || !(actor is Gem)) return;
                Mark(ListOf(__instance, HealFlags), actor);
            }
        }

        [HarmonyPatch(typeof(HealData), nameof(HealData.IsAmountModifiedBy), typeof(Actor))]
        internal static class HealMarked
        {
            private static void Postfix(ref HealData __instance, Actor actor, ref bool __result)
            {
                if (!__result || ControlledMergeMod.Live == null || !(actor is Gem gem)) return;
                __result = MarkedBy(ListOf(__instance, HealFlags), gem);
            }
        }
    }
}
