#if DEBUG
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace DevTools
{
    // Whether an essence can ever fire in a memory, by AreMyGemsCompatible's own Verdict - the
    // answer its tooltip warning gives a player. Reached by reflection, so that neither mod
    // references the other: with that mod not loaded, every answer is null, never an error.
    //
    // Beside the verdict, what went into it, so that a caller can ask "would this essence fire
    // there if that one sat beside it" without the game: what a Dead essence is missing (Verdict's
    // `missing` - an element - or else the things the memory never does that the essence waits
    // for), what an essence waits for and what it hands the memory it sits in (GemTriggers'
    // profile, and the elements it writes - ElementChangers), and what a memory does
    // (MemoryData, MemoryElements).
    internal static class GemFit
    {
        private const string Namespace = "AreMyGemsCompatible.";
        private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static MethodInfo _for;
        private static MethodInfo _forMissing;
        private static MethodInfo _describe;
        private static MethodInfo _profile;
        private static MethodInfo _written;
        private static MethodInfo _facts;
        private static MethodInfo _elements;

        // The assemblies are looked through again only when their number changes: a reload of the
        // mods loads new ones beside the old, and the last one loaded is the one in use.
        private static int _seenAssemblies = -1;

        public static bool Loaded => Find();

        private static bool Find()
        {
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            if (assemblies.Length == _seenAssemblies) return _for != null;
            _seenAssemblies = assemblies.Length;
            Recent.Clear();
            _for = _forMissing = _describe = _profile = _written = _facts = _elements = null;

            for (int i = assemblies.Length - 1; i >= 0; i--)
            {
                Type type;
                try { type = assemblies[i].GetType(Namespace + "Verdict", false); }
                catch (Exception) { continue; }
                var verdict = type?.GetMethod("For", Static, null, new[] { typeof(Gem), typeof(SkillTrigger) }, null);
                if (verdict == null) continue;
                _for = verdict;
                _describe = type.GetMethod("Describe", Static, null, new[] { typeof(Gem), typeof(SkillTrigger) }, null);

                // The rest is optional: an older AreMyGemsCompatible without one of them still
                // answers the verdict, and only the parts it lacks are null.
                var asm = assemblies[i];
                try
                {
                    var elementSet = asm.GetType(Namespace + "ElementSet", false);
                    if (elementSet != null)
                        _forMissing = type.GetMethod("For", Static, null, new[] { typeof(Gem), typeof(SkillTrigger), elementSet.MakeByRefType() }, null);
                    _profile = asm.GetType(Namespace + "GemTriggers", false)?.GetMethod("Of", Static, null, new[] { typeof(Gem) }, null);
                    _written = asm.GetType(Namespace + "ElementChangers", false)?.GetMethod("Written", Static, null, new[] { typeof(Type) }, null);
                    _facts = asm.GetType(Namespace + "MemoryData", false)?.GetMethod("Get", Static, null, new[] { typeof(SkillTrigger) }, null);
                    var factsType = asm.GetType(Namespace + "MemoryFacts", false);
                    if (factsType != null)
                        _elements = asm.GetType(Namespace + "MemoryElements", false)?.GetMethod("For", Static, null, new[] { typeof(SkillTrigger), factsType }, null);
                }
                catch (Exception) { }
                return true;
            }
            return false;
        }

        // /hero lists a verdict for every socketed essence and is polled several times a second in
        // a fight, while a verdict can look through the room's modifiers (FindObjectsByType). So an
        // answer is kept for a second per essence and memory - long enough to spare the polling,
        // short enough that a sibling socketed beside it shows by the next decision.
        private const float KeepFor = 1f;
        private static readonly Dictionary<long, KeyValuePair<float, object>> Recent = new Dictionary<long, KeyValuePair<float, object>>();

        // verdict: Fine or Dead (Compatibility's names); why: Verdict.Describe's sentence and what
        // went into it; missing (Dead only): { element } - the element the essence answers to and
        // the memory's damage lacks - or { needs } - what the essence waits for (Damage, Heal,
        // Shield, Cast) that the memory never does. The verdict already counts the essences
        // socketed beside it in that memory. Null when the mod is not loaded or there is no essence
        // or no memory.
        public static object Of(Gem gem, SkillTrigger skill)
        {
            if (gem == null || skill == null) return null;
            long key = ((long)gem.GetInstanceID() << 32) ^ (uint)skill.GetInstanceID();
            float now = Time.realtimeSinceStartup;
            if (Recent.TryGetValue(key, out var kept) && now - kept.Key < KeepFor) return kept.Value;
            if (Recent.Count > 256) Recent.Clear();
            var answer = Find() ? Ask(gem, skill) : null;
            Recent[key] = new KeyValuePair<float, object>(now, answer);
            return answer;
        }

        private static object Ask(Gem gem, SkillTrigger skill)
        {
            try
            {
                object verdict;
                object missingElement = null;
                if (_forMissing != null)
                {
                    var args = new object[] { gem, skill, null };
                    verdict = _forMissing.Invoke(null, args);
                    missingElement = args[2];
                }
                else verdict = _for.Invoke(null, new object[] { gem, skill });

                string why = _describe != null ? _describe.Invoke(null, new object[] { gem, skill }) as string : null;
                string name = verdict != null ? verdict.ToString() : null;
                object missing = null;
                if (name == "Dead" && _forMissing != null)
                {
                    var element = Flags(missingElement);
                    missing = element != null && element.Length > 0
                        ? new { needs = new string[0], element }
                        : new { needs = Flags(ProfileField(gem, "Needs")), element = new string[0] };
                }
                return new { verdict = name, why, missing };
            }
            catch (Exception e)
            {
                return new { verdict = (string)null, why = "threw " + Inner(e), missing = (object)null };
            }
        }

        // What an essence waits for and what it hands the memory it sits in, by GemTriggers:
        // needs (any one wakes it), alwaysLive (part of it works anywhere - never Dead), supplies
        // (what it makes the memory do, when the memory is cast and it fires on the cast or is
        // always live), gate (the one element its damage trigger answers to), adds (the elements
        // it writes into damage - null when that cannot be known, which the verdict counts as
        // any). Null when the mod is not loaded.
        public static object Profile(Gem gem)
        {
            if (gem == null || !Find() || _profile == null) return null;
            try
            {
                var profile = _profile.Invoke(null, new object[] { gem });
                object adds = null;
                if (_written != null) adds = _written.Invoke(null, new object[] { gem.GetType() });
                return new
                {
                    needs = Flags(Field(profile, "Needs")),
                    alwaysLive = Field(profile, "AlwaysLive") as bool? ?? false,
                    supplies = Flags(Field(profile, "Supplies")),
                    gate = Flags(Field(profile, "Gate")),
                    adds = _written != null ? Flags(adds) : null,
                };
            }
            catch (Exception e)
            {
                return new { error = "threw " + Inner(e) };
            }
        }

        // What a memory does, as the verdict reads it: known (the game's data describes it -
        // unknown memories are never called Dead), cast (false for a passive identity: nothing
        // beside it is started by a cast), damage, heal, shield, and the elements its damage can
        // have (null: cannot be known, which the verdict counts as any).
        public static object Memory(SkillTrigger skill)
        {
            if (skill == null || !Find() || _facts == null) return null;
            try
            {
                var facts = _facts.Invoke(null, new object[] { skill });
                object elements = _elements != null ? _elements.Invoke(null, new object[] { skill, facts }) : null;
                return new
                {
                    known = Field(facts, "IsKnown") as bool? ?? false,
                    cast = Field(facts, "IsCast") as bool? ?? false,
                    damage = Field(facts, "DealsDamage") as bool? ?? false,
                    heal = Field(facts, "Heals") as bool? ?? false,
                    shield = Field(facts, "Shields") as bool? ?? false,
                    elements = Flags(elements),
                };
            }
            catch (Exception e)
            {
                return new { error = "threw " + Inner(e) };
            }
        }

        // An essence's own limits, as its prefab and quality set them: an internal cooldown in
        // seconds (isCooldownEnabled - "every 8 seconds the cast is empowered", "once every 3
        // seconds") and a rate limit (rateCount uses refilled over rateSeconds). How often it can
        // fire at most, whatever the memory does - so that a memory hitting ten times a second is
        // worth no more to it than one hitting once in its cooldown. Needs nothing of
        // AreMyGemsCompatible. Null for no essence; each part null when that limit is off.
        public static object Limits(Gem gem)
        {
            if (gem == null) return null;
            try
            {
                return new
                {
                    cooldown = gem.isCooldownEnabled ? Math.Round(gem.GetValue(gem.cooldownTime), 2) : (double?)null,
                    rateCount = gem.isRateLimited ? Math.Round(gem.GetValue(gem.rateLimitCount), 2) : (double?)null,
                    rateSeconds = gem.isRateLimited ? Math.Round(gem.GetValue(gem.rateLimitTime), 2) : (double?)null,
                };
            }
            catch (Exception e)
            {
                return new { error = "threw " + Inner(e) };
            }
        }

        private static object ProfileField(Gem gem, string name)
        {
            if (_profile == null) return null;
            try { return Field(_profile.Invoke(null, new object[] { gem }), name); }
            catch (Exception) { return null; }
        }

        private static object Field(object boxed, string name)
        {
            return boxed?.GetType().GetField(name, Instance)?.GetValue(boxed);
        }

        // A [Flags] enum as its names: None is an empty list, a null (unknown) stays null.
        private static string[] Flags(object value)
        {
            if (value == null) return null;
            string text = value.ToString();
            if (text == "None" || text == "0") return new string[0];
            return text.Split(new[] { ", " }, StringSplitOptions.RemoveEmptyEntries);
        }

        private static string Inner(Exception e)
        {
            var inner = e is TargetInvocationException && e.InnerException != null ? e.InnerException : e;
            return inner.GetType().Name + ": " + inner.Message;
        }
    }
}
#endif
