using System.Collections.Generic;
using HarmonyLib;

namespace ControlledMerge
{
    // What copies of an essence really do share: Supersymmetry's hold on the hero's health.
    //
    // Supersymmetry sets the hero's Maximum Health to 1 and gives a barrier of its share of the
    // Maximum Health the hero had. Every copy adds a FinalStats processor that reads
    // data.maxHealth as that base and writes 1 over it - so the second copy's processor, running
    // after the first's, read the 1. Watched in play: with two copies the second had a base of 1 and
    // a barrier of 0.4, where the first had 1193 and 447. The base is now the one the first
    // processor of the pass saw, for every copy.
    //
    // It also remembers the share of Health the hero had, to give it back when taken off. A copy
    // put on beside another found the hero at 1 of 1, and remembered all of it: with both taken
    // off, a hero put on them at 597 of 1193 stood at 1193. A copy put on beside another now
    // remembers what that one does.
    //
    // This file used to hold something else: when a copy left, the copy that stayed was taken off
    // and put back, to make again an effect the two had found by type and the leaver had taken
    // with it. OwnState.cs made those effects each copy's own, and putting a copy back undid its
    // own state instead - a Thunder in W lost its 4 charges when the Thunder in Q was taken off.
    [HarmonyPatch(typeof(Gem_L_Supersymmetry), "FinalStatProcessor")]
    internal static class SupersymmetryBase
    {
        private static readonly AccessTools.FieldRef<Gem_L_Supersymmetry, float> BaseMaxHealth =
            AccessTools.FieldRefAccess<Gem_L_Supersymmetry, float>("_baseMaxHealth");

        // The Maximum Health the first processor of the latest pass saw, per hero.
        private static readonly Dictionary<Hero, float> Seen = new Dictionary<Hero, float>();

        private static bool Prefix(Gem_L_Supersymmetry __instance, ref FinalStats data)
        {
            if (ControlledMergeMod.Live == null) return true;
            var hero = __instance.owner;
            if (hero == null) return true;

            // 1 is what a copy writes; any other value is the hero's own, before any copy.
            if (data.maxHealth != 1f) Seen[hero] = data.maxHealth;
            BaseMaxHealth(__instance) = Seen.TryGetValue(hero, out float seen) ? seen : data.maxHealth;
            data.maxHealth = 1f;
            return false;
        }

        public static void Forget() => Seen.Clear();
    }

    [HarmonyPatch(typeof(Gem_L_Supersymmetry), nameof(Gem_L_Supersymmetry.OnEquipGem))]
    internal static class SupersymmetryHealth
    {
        private static readonly AccessTools.FieldRef<Gem_L_Supersymmetry, float?> Remembered =
            AccessTools.FieldRefAccess<Gem_L_Supersymmetry, float?>("_escrowedNormalizedHealth");

        private static void Prefix(Gem_L_Supersymmetry __instance, Hero newOwner)
        {
            if (ControlledMergeMod.Live == null || newOwner == null || newOwner.Skill == null) return;
            if (Remembered(__instance).HasValue) return;

            foreach (var pair in newOwner.Skill.gems)
            {
                if (pair.Value is Gem_L_Supersymmetry other && !ReferenceEquals(other, __instance) &&
                    other.isActive && Remembered(other).HasValue)
                {
                    Remembered(__instance) = Remembered(other);
                    return;
                }
            }
        }
    }
}
