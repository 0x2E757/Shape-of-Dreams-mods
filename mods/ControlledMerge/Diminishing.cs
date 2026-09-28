using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace ControlledMerge
{
    // How much of an essence is left, where it is.
    //
    // Two cuts, multiplied. One for the essence being in more than one memory: every copy of it,
    // in every memory, loses twoMemoriesCut (30% by default) when two memories hold it, and
    // threeMemoriesCut (40%) when three or more do. One for a memory holding more than one copy:
    // those copies lose twoCopiesCut (25%) for two, threeCopiesCut (35%) for three or more. So two
    // copies in Q and one in W leave each copy in Q at 0.7 x 0.75 = 52.5%, and the one in W at 70%.
    //
    // **What is cut is every value of the essence that grows with its quality, and nothing
    // else.** That is what merging used to raise, so it is what wearing a copy instead is paid in.
    // A value grows with quality when its ScalingValue has a per-level multiplier or a per-level
    // term (leveling not NoScaling, or lvlFactor) - which is the same test the game's own data dump
    // makes when it writes basicAddedMultiplierPerLevel beside a number. A duration, a threshold or
    // a "reduced by 50% on yourself" that stays put at every quality stays put here too.
    //
    // Three values of the base class are left alone even when they scale: cooldownTime,
    // rateLimitTime and rateLimitCount. They are when the essence may fire rather than what it
    // does, and cutting a cooldown would shorten it.
    internal struct Share
    {
        public int Memories;
        public int Copies;
        public float MemoryFactor;
        public float CopyFactor;

        public float Factor => MemoryFactor * CopyFactor;
        public bool Cuts => Factor < 0.9999f;

        public static readonly Share None = new Share { Memories = 1, Copies = 1, MemoryFactor = 1f, CopyFactor = 1f };
    }

    internal static class Diminishing
    {
        // Values are read many times a frame, often of the same few essences, and counting walks
        // the hero's whole loadout. So each essence's answer is kept for the frame it was asked in.
        private struct Cached
        {
            public int Frame;
            public float Factor;
        }

        private static readonly Dictionary<Gem, Cached> ByGem = new Dictionary<Gem, Cached>();

        public static void Forget() => ByGem.Clear();

        public static bool Scales(ScalingValue value)
        {
            return value.GetAddedScalingMultiplierPerLevel() > 0f || value.lvlFactor > 0f;
        }

        public static bool IsFiringLimit(Gem gem, ScalingValue value)
        {
            return Same(value, gem.cooldownTime) || Same(value, gem.rateLimitTime) || Same(value, gem.rateLimitCount);
        }

        // Where it is now.
        public static Share Of(Gem gem)
        {
            if (gem == null) return Share.None;
            var hero = gem.owner;
            if (hero == null || hero.Skill == null) return Share.None;
            return If(hero, gem, gem.location.skill, null);
        }

        // As if gem were in memory, and displaced - whatever it would replace there - were gone.
        // The essence itself is left out of the count wherever it is now and counted once, in
        // memory, so this answers for one being dragged as readily as for one already there.
        public static Share If(Hero hero, Gem gem, HeroSkillLocation memory, Gem displaced)
        {
            if (gem == null || hero == null || hero.Skill == null) return Share.None;

            // The host's settings, on every machine: the host's server computes what the essence
            // does. None on a guest whose host has no copy of the mod running.
            if (!HostSettings.TryGet(out var settings)) return Share.None;

            // With AreMyGemsCompatible loaded on the host, a copy that can never fire where it sits
            // is not counted, and one that cannot fire is not cut: there is nothing of it to cut.
            bool leaveOutDead = settings.LeavesOutDeadCopies;
            if (leaveOutDead && !Fit.Fires(gem, hero.Skill.GetSkill(memory))) return Share.None;

            var kind = gem.GetType();
            int memories = 1 << (int)memory;
            int copies = 1;

            foreach (var pair in hero.Skill.gems)
            {
                var other = pair.Value;
                if (other == null || ReferenceEquals(other, gem) || ReferenceEquals(other, displaced)) continue;
                if (other.GetType() != kind) continue;
                if (leaveOutDead && !Fit.Fires(other, hero.Skill.GetSkill(pair.Key.skill))) continue;

                memories |= 1 << (int)pair.Key.skill;
                if (pair.Key.skill == memory) copies++;
            }

            int memoryCount = CountBits(memories);
            return new Share
            {
                Memories = memoryCount,
                Copies = copies,
                MemoryFactor = Cut(memoryCount, settings.TwoMemoriesCut, settings.ThreeMemoriesCut),
                CopyFactor = Cut(copies, settings.TwoCopiesCut, settings.ThreeCopiesCut),
            };
        }

        public static float FactorOf(Gem gem)
        {
            if (gem == null || gem.owner == null) return 1f;

            int frame = Time.frameCount;
            if (ByGem.TryGetValue(gem, out var cached) && cached.Frame == frame) return cached.Factor;

            float factor = Of(gem).Factor;
            if (ByGem.Count > 256) ByGem.Clear();
            ByGem[gem] = new Cached { Frame = frame, Factor = factor };
            return factor;
        }

        // The essence an ability instance works for, if any.
        //
        // An instance an essence makes for itself has the essence as its parent. One it makes
        // through Gem.Create*WithSource has the memory's cast as its parent instead - that is the
        // point of those helpers - and carries the essence in AbilityInstance.gem. Whatever either
        // of those makes in turn (Se_Gem_C_Sharp_ArrowSpawner's arrows) has neither, and finds it
        // by walking up. The walk stops at a memory or an entity: past those it is the hero's.
        //
        // AbilityInstance.gem itself is not used: its getter searches with FindFirstOfType and
        // writes the answer back into a syncvar, and it does not look at a parent's gem.
        public static Gem SourceOf(Actor actor)
        {
            for (int hops = 0; actor != null && hops < 16; hops++)
            {
                if (actor is Gem gem) return gem;
                if (actor is AbilityInstance instance)
                {
                    var own = instance.Network_gem;
                    if (own != null) return own;
                }
                else if (actor is SkillTrigger || actor is Entity)
                {
                    return null;
                }
                actor = actor.parentActor;
            }
            return null;
        }

        private static float Cut(int count, int twoCut, int threeCut)
        {
            if (count >= 3) return 1f - Mathf.Clamp(threeCut, 0, 100) / 100f;
            if (count == 2) return 1f - Mathf.Clamp(twoCut, 0, 100) / 100f;
            return 1f;
        }

        private static int CountBits(int mask)
        {
            int n = 0;
            for (; mask != 0; mask &= mask - 1) n++;
            return n;
        }

        internal static bool Same(ScalingValue a, ScalingValue b)
        {
            return a.leveling == b.leveling && a.scalingMultiplier == b.scalingMultiplier &&
                   a.baseValue == b.baseValue && a.adFactor == b.adFactor && a.apFactor == b.apFactor &&
                   a.lvlFactor == b.lvlFactor && a.armorFactor == b.armorFactor &&
                   a.addedHpFactor == b.addedHpFactor && a.critPercentageFactor == b.critPercentageFactor;
        }
    }

    // The four ways a value of an essence reaches the game.
    //
    // 1. Gem.GetValue - the essence's own code, and its properties built on it
    //    (Gem_C_Efficiency.reducedRatio, Gem_R_Epiphany.refundCooldownRatio ...).
    // 2. AbilityInstance.GetValue - what it creates. Se_Gem_C_Love holds the bonus Love grants, and
    //    reads it with its own level, which was copied from the essence when it was made.
    // 3. AbilityInstance.CreateDamage(type, ScalingValue, ...) - the damage helpers, which hand the
    //    ScalingValue to DamageData's constructor and never pass through GetValue.
    // 4. Three essences that compute from quality or the level themselves, below.
    //
    // Whichever the way, a value OneCut leaves whole is not cut.
    [HarmonyPatch(typeof(Gem), nameof(Gem.GetValue), typeof(ScalingValue))]
    internal static class GemValues
    {
        private static void Postfix(Gem __instance, ScalingValue val, ref float __result)
        {
            // While a description is being written, the numbers are cut where they are
            // evaluated, and this would cut them twice.
            if (ControlledMergeMod.Live == null || Tooltip.Scope.Active) return;
            if (!Diminishing.Scales(val) || Diminishing.IsFiringLimit(__instance, val) || OneCut.Keeps(__instance, val)) return;

            float factor = Diminishing.FactorOf(__instance);
            if (factor < 1f) __result *= factor;
        }
    }

    [HarmonyPatch(typeof(AbilityInstance), nameof(AbilityInstance.GetValue), typeof(ScalingValue))]
    internal static class InstanceValues
    {
        private static void Postfix(AbilityInstance __instance, ScalingValue val, ref float __result)
        {
            if (ControlledMergeMod.Live == null || !Diminishing.Scales(val)) return;

            var gem = Diminishing.SourceOf(__instance);
            if (gem == null || OneCut.Keeps(gem, val)) return;

            float factor = Diminishing.FactorOf(gem);
            if (factor < 1f) __result *= factor;
        }
    }

    [HarmonyPatch(typeof(AbilityInstance), nameof(AbilityInstance.CreateDamage),
        typeof(DamageData.SourceType), typeof(ScalingValue), typeof(float))]
    internal static class InstanceDamage
    {
        private static void Postfix(AbilityInstance __instance, ScalingValue value, ref DamageData __result)
        {
            if (ControlledMergeMod.Live == null || !Diminishing.Scales(value)) return;

            var gem = Diminishing.SourceOf(__instance);
            if (gem == null || OneCut.Keeps(gem, value)) return;

            float factor = Diminishing.FactorOf(gem);
            if (factor < 1f) __result.ApplyRawMultiplier(factor);
        }
    }

    // Essences that grow with quality without a ScalingValue.
    //
    // Abyss turns quality into a chance through 1 - 1/(1 + x); the cut goes on x, the way it goes
    // on the Ability Haste that Efficiency feeds through the same curve, so the answer is read back
    // out of the property rather than restating its constants. Ricochet's chance comes the same
    // way and is left whole: its ricochetRatio is cut instead (OneCut).
    [HarmonyPatch]
    internal static class QualityChances
    {
        private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            yield return AccessTools.PropertyGetter(typeof(Gem_R_Abyss), nameof(Gem_R_Abyss.atkEffectChance));
        }

        private static void Postfix(Gem __instance, ref float __result)
        {
            float factor = Tooltip.Scope.FactorFor(__instance);
            if (factor >= 1f || __result <= 0f || __result >= 1f) return;

            float x = __result / (1f - __result);
            __result = 1f - 1f / (1f + x * factor);
        }
    }

    // Virtuousness adds a charge for every requiredQualityPerCharge of quality, on top of one.
    // The count as a whole is cut and rounded down, and never below the one it starts from.
    [HarmonyPatch(typeof(Gem_E_Virtuousness), nameof(Gem_E_Virtuousness.addedChargeInt), MethodType.Getter)]
    internal static class VirtuousnessCharges
    {
        private static void Postfix(Gem_E_Virtuousness __instance, ref int __result)
        {
            float factor = Tooltip.Scope.FactorFor(__instance);
            if (factor >= 1f || __instance.requiredQualityPerCharge <= 0) return;

            // The game's own count, integer division and all, so the cut starts from what it gives.
            int charges = 1 + __instance.quality / __instance.requiredQualityPerCharge;
            __result = Mathf.Max(1, Mathf.FloorToInt(charges * factor));
        }
    }

    // Might's figure for a player who is not the host, when its synced one is not there yet,
    // reads its ScalingValue at its level directly.
    [HarmonyPatch(typeof(Gem_E_Might), "GetDamageAmpFallback")]
    internal static class MightFallback
    {
        private static void Postfix(Gem_E_Might __instance, ref float __result)
        {
            float factor = Tooltip.Scope.FactorFor(__instance);
            if (factor < 1f) __result *= factor;
        }
    }
}
