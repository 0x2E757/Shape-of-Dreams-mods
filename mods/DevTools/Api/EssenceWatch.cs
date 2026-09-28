#if DEBUG
using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace DevTools
{
    // Whether an essence actually does anything, watched rather than reasoned about - the answer
    // AreMyGemsCompatible's verdicts are checked against.
    //
    // Two signals per essence, both from the game's own code:
    //
    //   * Gem.NotifyUse, which is how an essence says it has just acted: the flash on its socket
    //     (InvokeFlash) and a use off its rate limit. 86 of the 105 essence types call it where
    //     they trigger, and the amplifiers - Lethality, Guidance - call it inside their processor,
    //     only when the amplification is applied.
    //   * The essence's own actor events. Actor.InvokeOnDealDamage and its siblings walk up
    //     parentActor, so a handler on the essence sees everything done by the essence itself and
    //     by whatever it created - its projectiles, zones, status effects - which catches the
    //     types that never call NotifyUse (Fever's infection, Obsidian's attack, Eternal Flame).
    //
    // And per memory, what it does in the same window, from the memory's events: casts, damage by
    // element, heals, barriers - each split by whether an essence socketed in it did it (a
    // socketed essence's parentActor is the memory, HeroSkill.EquipGem) or the memory itself.
    // What the memory's events carry is exactly what an essence listening there is shown.
    //
    // Only listens. Started by /debug/essences/start, which takes a fresh look at the loadout and
    // zeroes every count; anything the essences did while being equipped is therefore not counted.
    internal static class EssenceWatch
    {
        private sealed class Tally
        {
            public int uses, casts, hits, heals, shields, kills, created, elementals;
            public double damage, healed, shielded;
            public readonly Dictionary<string, int> elements = new Dictionary<string, int>();
            public readonly Dictionary<string, int> kinds = new Dictionary<string, int>();

            public void Count(Dictionary<string, int> into, string key) =>
                into[key] = into.TryGetValue(key, out var n) ? n + 1 : 1;
        }

        private sealed class Watched
        {
            public Actor actor;
            public string slot;
            public int index;
            public string type;
            public readonly Tally own = new Tally();
            // for a memory: what essences socketed in it did through it, by essence type
            public readonly Dictionary<string, Tally> byGem = new Dictionary<string, Tally>();
            public Action<EventInfoDamage> onDamage;
            public Action<EventInfoHeal> onHeal;
            public Action<EventInfoShield> onShield;
            public Action<EventInfoKill> onKill;
            public Action<EventInfoAbilityInstance> onCreate;
            public Action<EventInfoApplyElemental> onElemental;
            public Action<EventInfoCast> onCast;
        }

        private static readonly Dictionary<uint, Watched> Gems = new Dictionary<uint, Watched>();
        private static readonly List<Watched> Memories = new List<Watched>();
        private static float _since;

        public static bool Active => Gems.Count > 0 || Memories.Count > 0;

        [Route("POST", "/debug/essences/start", "Start (or restart) watching every essence and memory the hero wears: zeroes the counts and takes a fresh look at the loadout. Only listens.")]
        private static object Start(Args a)
        {
            Stop();
            var hero = GameAccess.RequireLiveHero();
            foreach (var pair in hero.Skill.gems)
            {
                if (pair.Value == null) continue;
                var w = new Watched { actor = pair.Value, slot = pair.Key.skill.ToString(), index = pair.Key.index, type = pair.Value.GetType().Name };
                Subscribe(w, null);
                Gems[pair.Value.netId] = w;
            }
            foreach (HeroSkillLocation s in Enum.GetValues(typeof(HeroSkillLocation)))
            {
                SkillTrigger skill = null;
                try { if (!hero.Skill.TryGetSkill(s, out skill)) continue; }
                catch (Exception) { continue; }
                if (skill == null) continue;
                var w = new Watched { actor = skill, slot = s.ToString(), index = -1, type = skill.GetType().Name };
                Subscribe(w, skill);
                Memories.Add(w);
            }
            _since = Time.time;
            return Report(a);
        }

        [Route("POST", "/debug/essences/unwatch", "Stop watching.")]
        private static object Unwatch(Args a)
        {
            var report = Report(a);
            Stop();
            return report;
        }

        [Route("GET", "/debug/essences/watch", "What each watched essence and memory has done since the watch started: uses (Gem.NotifyUse, the socket's flash), and everything done by it or anything it created - hits by element, heals, barriers, kills, instances created. Memories split what they did between themselves and each essence in them.")]
        private static object Report(Args a)
        {
            return new
            {
                seconds = Math.Round(Time.time - _since, 1),
                essences = Gems.Values.OrderBy(w => w.slot).ThenBy(w => w.index).Select(w => new
                {
                    slot = w.slot,
                    index = w.index,
                    type = w.type,
                    id = w.actor != null ? w.actor.netId : 0,
                    uses = w.own.uses,
                    did = Describe(w.own),
                }).ToList(),
                memories = Memories.Select(w => new
                {
                    slot = w.slot,
                    type = w.type,
                    casts = w.own.casts,
                    itself = Describe(w.own),
                    byEssence = w.byGem.ToDictionary(p => p.Key, p => Describe(p.Value)),
                }).ToList(),
            };
        }

        private static object Describe(Tally t) => new
        {
            hits = t.hits,
            damage = Math.Round(t.damage),
            elements = t.elements,
            heals = t.heals,
            healed = Math.Round(t.healed, 1),
            shields = t.shields,
            shielded = Math.Round(t.shielded, 1),
            kills = t.kills,
            elementals = t.elementals,
            created = t.created,
            kinds = t.kinds,
        };

        public static void Stop()
        {
            foreach (var w in Gems.Values) Drop(w);
            foreach (var w in Memories) Drop(w);
            Gems.Clear();
            Memories.Clear();
        }

        // For an essence, everything under it is its own. For a memory, each event is put down to
        // the socketed essence it came through, if any, or else to the memory.
        private static void Subscribe(Watched w, SkillTrigger memory)
        {
            Tally To(Actor source)
            {
                if (memory == null) return w.own;
                var gem = GemAbove(source, memory);
                if (gem == null) return w.own;
                var key = gem.GetType().Name;
                if (!w.byGem.TryGetValue(key, out var t)) w.byGem[key] = t = new Tally();
                return t;
            }

            w.onDamage = info => Safe(() =>
            {
                if (info.victim == null || info.victim is Hero) return;
                var t = To(info.actor);
                t.hits++;
                t.damage += info.damage.amount;
                t.Count(t.elements, info.damage.elemental.HasValue ? info.damage.elemental.Value.ToString() : "None");
            });
            w.onHeal = info => Safe(() =>
            {
                var t = To(info.actor);
                t.heals++;
                t.healed += info.amount + info.discardedAmount;
            });
            w.onShield = info => Safe(() =>
            {
                var t = To(info.statusEffect);
                t.shields++;
                t.shielded += info.originalAmount;
            });
            w.onKill = info => Safe(() => To(info.actor).kills++);
            w.onCreate = info => Safe(() =>
            {
                var t = To(info.instance);
                t.created++;
                if (info.instance != null) t.Count(t.kinds, info.instance.GetType().Name);
            });
            w.onElemental = info => Safe(() =>
            {
                var t = To(info.actor);
                t.elementals++;
                t.Count(t.elements, "applied " + info.type);
            });

            var a = w.actor;
            a.ActorEvent_OnDealDamage += w.onDamage;
            a.ActorEvent_OnDoHeal += w.onHeal;
            a.ActorEvent_OnGiveShield += w.onShield;
            a.ActorEvent_OnKill += w.onKill;
            a.ActorEvent_OnAbilityInstanceCreated += w.onCreate;
            a.ActorEvent_OnApplyElemental += w.onElemental;
            if (memory != null)
            {
                w.onCast = _ => Safe(() => w.own.casts++);
                memory.TriggerEvent_OnCastComplete += w.onCast;
            }
        }

        private static void Drop(Watched w)
        {
            try
            {
                var a = w.actor;
                if (a == null) return;
                a.ActorEvent_OnDealDamage -= w.onDamage;
                a.ActorEvent_OnDoHeal -= w.onHeal;
                a.ActorEvent_OnGiveShield -= w.onShield;
                a.ActorEvent_OnKill -= w.onKill;
                a.ActorEvent_OnAbilityInstanceCreated -= w.onCreate;
                a.ActorEvent_OnApplyElemental -= w.onElemental;
                if (w.onCast != null && a is SkillTrigger skill) skill.TriggerEvent_OnCastComplete -= w.onCast;
            }
            catch (Exception) { }
        }

        // The socketed essence an actor descends from, below the given memory, or null.
        private static Gem GemAbove(Actor source, Actor memory)
        {
            // What an essence makes through Gem.Create*WithSource has the memory's cast as its
            // parent and names the essence in AbilityInstance.Network_gem instead.
            for (var a = source; a != null && a != memory; a = a.parentActor)
            {
                if (a is Gem gem) return gem;
                if (a is AbilityInstance instance && instance.Network_gem != null) return instance.Network_gem;
            }
            return null;
        }

        private static void Safe(Action act)
        {
            try { act(); }
            catch (Exception) { }
        }

        [HarmonyPatch(typeof(Gem), nameof(Gem.NotifyUse))]
        private static class Uses
        {
            private static void Postfix(Gem __instance)
            {
                if (Gems.Count == 0 || __instance == null) return;
                if (Gems.TryGetValue(__instance.netId, out var w)) w.own.uses++;
            }
        }
    }
}
#endif
