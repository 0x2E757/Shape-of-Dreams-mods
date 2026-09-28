using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Mirror;
using UnityEngine;

namespace ControlledMerge
{
    // Values an essence writes down once instead of reading each time.
    //
    // Most essences ask Gem.GetValue whenever they act, and the cut reaches them there. A few work
    // their number out when they are equipped and keep it: Gem_C_Efficiency and Gem_R_Lightweight
    // put a SkillBonus.cooldownMultiplier on their memory, Gem_L_Perfect and Gem_E_Might fill their
    // StatBonus, Gem_E_Virtuousness a SkillBonus.addedCharge. Each works it out again only in
    // OnQualityChange - in the stock game quality is the one thing that changes it. Here the cut
    // changes it too, whenever a copy is put on, taken off or moved, and a copy already worn went
    // on with the number it had: two Efficiency in one memory gave the first one's full 36 Ability
    // Haste and the second one's 27, while both tooltips said 18.9.
    //
    // So the server looks, a few times a second, at the cut each worn essence of such a kind was
    // last worked out with, and when it has changed runs that essence's OnQualityChange with the
    // same quality - the essence's own "my numbers changed". Gem.OnQualityChange itself stands aside
    // meanwhile: it lifts the essence's merchant sell price cap (maxSellGold), flashes the socket and
    // announces an upgrade, none of which has happened.
    //
    // Two kinds turn into another essence in OnQualityChange once quality passes a threshold, and
    // are left out; neither has a value the cut reaches.
    [HarmonyPatch(typeof(Gem), "OnQualityChange")]
    internal static class Refresh
    {
        private const float Interval = 0.25f;

        private static readonly MethodInfo QualityChange = AccessTools.Method(typeof(Gem), "OnQualityChange");

        private static readonly Dictionary<Gem, float> Applied = new Dictionary<Gem, float>();
        private static readonly Dictionary<Type, bool> Recomputes = new Dictionary<Type, bool>();
        private static readonly List<Gem> Gone = new List<Gem>();

        private static bool _refreshing;
        private static float _next;

        // The base part, while an essence is only being told its numbers changed.
        private static bool Prefix()
        {
            return !_refreshing;
        }

        public static void Tick()
        {
            if (ControlledMergeMod.Live == null || !NetworkServer.active) return;
            if (Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + Interval;

            var actors = NetworkedManagerBase<ActorManager>.instance;
            if (actors == null) return;

            foreach (var hero in actors.allHeroes)
            {
                if (hero == null || !hero.isActive || hero.Skill == null) continue;
                foreach (var pair in hero.Skill.gems)
                {
                    var gem = pair.Value;
                    if (gem == null || !gem.isActive || !RecomputesOnQuality(gem.GetType())) continue;

                    float factor = Diminishing.FactorOf(gem);
                    if (Applied.TryGetValue(gem, out float applied) && Mathf.Approximately(applied, factor)) continue;

                    // Seen for the first time, it is worked out once all the same: when it was put
                    // on, the loadout may not yet have been what it is now.
                    Applied[gem] = factor;
                    Tell(gem);
                }
            }

            Gone.Clear();
            foreach (var gem in Applied.Keys)
                if (gem == null || !gem.isActive || gem.owner == null) Gone.Add(gem);
            foreach (var gem in Gone) Applied.Remove(gem);
        }

        private static void Tell(Gem gem)
        {
            _refreshing = true;
            try
            {
                QualityChange.Invoke(gem, new object[] { gem.quality, gem.quality });
            }
            catch (Exception e)
            {
                Debug.LogException(e, gem);
            }
            finally
            {
                _refreshing = false;
            }
        }

        private static bool RecomputesOnQuality(Type kind)
        {
            if (!Recomputes.TryGetValue(kind, out bool recomputes))
            {
                var method = kind.GetMethod("OnQualityChange", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
                                            null, new[] { typeof(int), typeof(int) }, null);
                recomputes = method != null && method.DeclaringType != typeof(Gem) &&
                             kind != typeof(Gem_E_OurStory_Unfinished) && kind != typeof(Gem_U_GuidingCompass_NotCharged);
                Recomputes[kind] = recomputes;
            }
            return recomputes;
        }

        public static void Forget()
        {
            Applied.Clear();
            Recomputes.Clear();
            _refreshing = false;
        }
    }
}
