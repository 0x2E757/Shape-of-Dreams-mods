#if DEBUG
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace DevTools
{
    // How each memory the hero wears is actually used: how often it is cast, how many hits it
    // lands and on how many enemies a cast, how much damage those hits carry and their proc
    // coefficients. Read from the very events an essence socketed in that memory listens to
    // (Gem.OnEquipSkill: the memory's TriggerEvent_OnCastComplete and ActorEvent_OnDealDamage -
    // the latter raised for everything parented under the memory's cast, Actor.InvokeOnDealDamage
    // walking up the parents), so "hits" is exactly how many chances an on-hit essence gets there.
    //
    // Only listens: a handler added to those events changes nothing in the game. Counted per
    // memory type for the whole run (a memory taken off and put back keeps its numbers), with the
    // seconds it was worn in combat (Hero.isInCombat) so that rates can be compared between a
    // memory worn since the start and one picked up late.
    internal static class MemoryUse
    {
        private sealed class Use
        {
            public int casts;
            public int hits;
            public double proc;
            public double damage;
            public float maxHit;
            public float combat;
            public float worn;
            // Distinct enemies hit since the last cast, and the sum of those counts over the casts
            // closed so far: enemies reached per cast.
            public readonly HashSet<int> window = new HashSet<int>();
            public int windows;
            public int targets;
        }

        private sealed class Hook
        {
            public SkillTrigger skill;
            public string type;
            public Action<EventInfoCast> onCast;
            public Action<EventInfoDamage> onDamage;
        }

        private static readonly Dictionary<string, Use> ByType = new Dictionary<string, Use>();
        private static readonly List<Hook> Hooks = new List<Hook>();
        private static Hero _hero;
        private static float _nextLook;
        private static float _lastLook;

        // Called every frame by the server. Every half second: hooks on memories newly worn, off
        // the ones taken off; the worn/combat seconds of what is worn now.
        public static void Tick()
        {
            float now = Time.unscaledTime;
            if (now < _nextLook) return;
            float dt = _lastLook > 0 ? Mathf.Min(now - _lastLook, 2f) : 0f;
            _lastLook = now;
            _nextLook = now + 0.5f;
            try
            {
                var hero = GameAccess.Hero;
                if (hero != _hero)
                {
                    Unsubscribe();
                    _hero = hero;
                    ByType.Clear();
                }
                if (hero == null) return;

                var worn = new List<SkillTrigger>();
                foreach (HeroSkillLocation s in new[] { HeroSkillLocation.Q, HeroSkillLocation.W, HeroSkillLocation.E, HeroSkillLocation.R })
                {
                    try { if (hero.Skill.TryGetSkill(s, out var k) && k != null) worn.Add(k); }
                    catch (Exception) { }
                }

                for (int i = Hooks.Count - 1; i >= 0; i--)
                {
                    if (worn.Contains(Hooks[i].skill)) continue;
                    Drop(Hooks[i]);
                    Hooks.RemoveAt(i);
                }
                bool combat = false;
                try { combat = hero.isInCombat; } catch (Exception) { }
                foreach (var k in worn)
                {
                    var type = k.GetType().Name;
                    var use = For(type);
                    use.worn += dt;
                    if (combat) use.combat += dt;
                    if (Hooks.Any(h => h.skill == k)) continue;
                    var hook = new Hook { skill = k, type = type };
                    hook.onCast = _ => OnCast(type);
                    hook.onDamage = info => OnDamage(type, info);
                    k.TriggerEvent_OnCastComplete += hook.onCast;
                    k.ActorEvent_OnDealDamage += hook.onDamage;
                    Hooks.Add(hook);
                }
            }
            catch (Exception) { }
        }

        public static void Unsubscribe()
        {
            foreach (var h in Hooks) Drop(h);
            Hooks.Clear();
            _hero = null;
        }

        private static void Drop(Hook h)
        {
            try
            {
                if (h.skill == null) return;
                h.skill.TriggerEvent_OnCastComplete -= h.onCast;
                h.skill.ActorEvent_OnDealDamage -= h.onDamage;
            }
            catch (Exception) { }
        }

        private static Use For(string type)
        {
            if (!ByType.TryGetValue(type, out var use)) ByType[type] = use = new Use();
            return use;
        }

        private static void OnCast(string type)
        {
            try
            {
                var use = For(type);
                use.casts++;
                if (use.casts > 1) { use.windows++; use.targets += use.window.Count; }
                use.window.Clear();
            }
            catch (Exception) { }
        }

        private static void OnDamage(string type, EventInfoDamage info)
        {
            try
            {
                if (info.victim == null || info.victim is Hero) return;
                var use = For(type);
                use.hits++;
                use.proc += info.damage.procCoefficient;
                use.damage += info.damage.amount;
                if (info.damage.amount > use.maxHit) use.maxHit = info.damage.amount;
                use.window.Add(info.victim.GetInstanceID());
            }
            catch (Exception) { }
        }

        // The numbers for one memory type, or null when it has not been worn this run.
        public static object Of(string type)
        {
            if (type == null || !ByType.TryGetValue(type, out var u)) return null;
            int windows = u.windows + (u.window.Count > 0 ? 1 : 0);
            int targets = u.targets + u.window.Count;
            return new
            {
                casts = u.casts,
                hits = u.hits,
                proc = Math.Round(u.proc, 2),
                damage = Math.Round(u.damage),
                maxHit = Math.Round(u.maxHit),
                combatSeconds = Math.Round(u.combat, 1),
                wornSeconds = Math.Round(u.worn, 1),
                targetsPerCast = windows > 0 ? Math.Round((double)targets / windows, 2) : 0,
            };
        }

        [Route("GET", "/hero/use", "How each memory worn this run has been used: casts, hits landed (every damage event an on-hit essence in it would see), the sum of their proc coefficients and damage, the biggest hit, enemies reached per cast, and the seconds it was worn (in combat). Only listens to the memory's own events.")]
        private static object List(Args a)
        {
            return new { memories = ByType.Keys.OrderBy(k => k).Select(k => new { memory = k, use = Of(k) }).ToList() };
        }
    }
}
#endif
