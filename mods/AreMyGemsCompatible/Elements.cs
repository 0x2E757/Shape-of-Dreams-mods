using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace AreMyGemsCompatible
{
    // The four elements as a set. ElementalType is Fire, Cold, Light, Dark in that order.
    [Flags]
    internal enum ElementSet
    {
        None = 0,
        Fire = 1,
        Cold = 2,
        Light = 4,
        Dark = 8,
    }

    // The essences that wait for one element, and which one.
    //
    // **Damage is not one thing to these.** Gem_R_Frost's OnDealDamage opens with
    // `obj.damage.elemental == ElementalType.Cold` and does nothing else; socketed into a memory
    // that deals only Fire damage - Incendiary Rounds, which is where it was noticed - it never
    // fires, and the damage question alone says the memory is fine.
    //
    // A table, where the rest of the mod reads code, because the thing to tell apart is not a
    // member name but the shape of a branch. Fourteen shipped essences compare an element, and
    // most of them only pick a bigger number with it: Gem_R_NightSky, Gem_C_Shatter,
    // Gem_U_GlacialCore. Three look gated and are not, which is the reason reading the comparison
    // alone would be wrong:
    //
    //   - Gem_U_EternalFlame's Fire check guards only its curse; its amplification works on any
    //     damage the memory deals to a burning target, and targets burn from other memories too.
    //   - Gem_E_Umbra's Dark check guards only the heal; the crit amplification is unconditional.
    //   - Gem_R_Flow listens on the hero, not the memory, for Light damage from anywhere.
    //
    // What is left is every essence whose whole effect in the slot sits behind one element. An
    // essence another mod adds is not in the table and is judged as it was before, which is the
    // quiet direction: at worst a warning that was due is missing.
    internal static class ElementGates
    {
        private static readonly Dictionary<string, ElementSet> Gates = new Dictionary<string, ElementSet>
        {
            // Cold damage from the memory, then the extra hit and stun. See GemTriggers for why
            // its OnEquipGem no longer counts as a hook on the hero.
            ["Gem_R_Frost"] = ElementSet.Cold,

            // The armour comes from Cold damage alone. The damage reduction it pays for it is on
            // the hero and always on, so in any other memory the essence is only its drawback.
            ["Gem_E_Apathy"] = ElementSet.Cold,

            // The living bomb, from Fire damage alone.
            ["Gem_E_Fever"] = ElementSet.Fire,

            // Fires on the cast, but all the cast does is spend the cooldown: the lava field comes
            // from Fire damage dealt by that cast's instance.
            ["Gem_R_Lava"] = ElementSet.Fire,
        };

        public static ElementSet Of(Type gemType)
        {
            ElementSet element;
            return gemType != null && Gates.TryGetValue(gemType.Name, out element) ? element : ElementSet.None;
        }

        public static ElementSet From(ElementalType type)
        {
            return (ElementSet)(1 << (int)type);
        }
    }

    // What elements a memory can deal, or null when that cannot be known.
    //
    // Three sources, and the answer is their union, because every one of them errs only by
    // missing an element and a missing element is exactly what makes a false warning:
    //
    //   1. the dump - "Fire damage" in the description, and the element among the tags; see
    //      MemoryData. Incendiary Rounds carries both.
    //   2. the memory's code - a constant handed to DamageData.SetElemental anywhere in its Ai_
    //      and Se_ types, and in whatever those create. An element that comes out of a variable,
    //      as St_D_SharedPain copies the element of the hit it repeats, makes the whole answer
    //      unknown.
    //   3. the prefabs of its DamageInstance types and of the ones they create, whose
    //      applyElemental and elemental fields are authored there and appear in no code at all.
    //
    // An audit of the shipped data found every constant in (2) already named by (1), with Shared
    // Pain the one variable. (3) cannot be audited offline, which is why it is read live.
    //
    // A passive memory is unknown outright: what it deals is the hero's own damage passed on, and
    // that can be anything.
    internal static class MemoryElements
    {
        private static readonly Dictionary<Type, ElementSet?> Cache = new Dictionary<Type, ElementSet?>();

        public static ElementSet? For(SkillTrigger skill, MemoryFacts facts)
        {
            if (skill == null || !facts.IsKnown || !facts.IsCast) return null;

            var type = skill.GetType();
            ElementSet? cached;
            if (Cache.TryGetValue(type, out cached)) return cached;

            var code = FromCode(type);
            ElementSet? result = code.HasValue ? facts.Elements | code.Value : (ElementSet?)null;
            Cache[type] = result;
            return result;
        }

        public static void Reset()
        {
            Cache.Clear();
        }

#if DEBUG
        // Debug builds only: every memory the dump knows, with what its code and prefabs add to
        // what its description and tags say. The prefab half cannot be checked any other way, and
        // an element found there and nowhere in the dump is exactly the case this has to catch.
        public static void Audit()
        {
            var database = DewResources.database;
            if (database == null || database.typeNameToType == null) return;

            int memories = 0, extra = 0, unknown = 0;
            var log = new System.Text.StringBuilder();
            foreach (var pair in database.typeNameToType)
            {
                if (pair.Value == null || !typeof(SkillTrigger).IsAssignableFrom(pair.Value)) continue;
                var facts = MemoryData.Get(pair.Key);
                if (!facts.IsKnown) continue;
                memories++;

                var code = FromCode(pair.Value);
                if (!code.HasValue)
                {
                    unknown++;
                    log.Append("\n  ").Append(pair.Key).Append(": unknown from code");
                }
                else if ((code.Value & ~facts.Elements) != ElementSet.None)
                {
                    extra++;
                    log.Append("\n  ").Append(pair.Key).Append(": dump says ").Append(facts.Elements)
                       .Append(", code and prefabs add ").Append(code.Value & ~facts.Elements);
                }
            }

            Debug.Log("[AreMyGemsCompatible] element audit: " + memories + " memories, " + extra +
                      " with elements beyond the dump, " + unknown + " unknown" + log);
        }
#endif

        // The memory's own Ai_ and Se_ types, found by the naming the game uses throughout:
        // St_Q_IncendiaryRounds has Se_Q_IncendiaryRounds_EmpowerAttacks and
        // Ai_Q_IncendiaryRounds_Attack. Only types with a prefab are listed in the resource
        // database, and those are the only ones that can be spawned.
        private static ElementSet? FromCode(Type skillType)
        {
            var database = DewResources.database;
            if (database == null || database.typeNameToType == null) return null;

            string name = skillType.Name;
            if (!name.StartsWith("St_", StringComparison.Ordinal)) return ElementSet.None;
            string stem = name.Substring(3);

            var found = ElementSet.None;
            foreach (var pair in database.typeNameToType)
            {
                var type = pair.Value;
                if (type == null) continue;
                if (type != skillType && !Owns("Ai_" + stem, pair.Key) && !Owns("Se_" + stem, pair.Key)) continue;

                // Its constants, its prefab, and the same again for whatever it creates - a memory's
                // instance can spawn a type named for some other thing entirely.
                var written = ElementChangers.Written(type);
                if (!written.HasValue) return null;
                found |= written.Value;
            }
            return found;
        }

        // Ai_C_Pew owns Ai_C_Pew_Projectile, not Ai_C_PewPew.
        private static bool Owns(string prefix, string typeName)
        {
            return typeName.StartsWith(prefix, StringComparison.Ordinal)
                   && (typeName.Length == prefix.Length || typeName[prefix.Length] == '_');
        }

        public static ElementSet? FromPrefab(Type type)
        {
            if (!typeof(DamageInstance).IsAssignableFrom(type)) return ElementSet.None;

            // Checked first because GetByType logs an error for a type with no prefab.
            var database = DewResources.database;
            if (database == null || database.typeToGuid == null || !database.typeToGuid.ContainsKey(type)) return ElementSet.None;

            try
            {
                var prefab = DewResources.GetByType(type) as DamageInstance;
                if (prefab == null) return null;
                return prefab.applyElemental ? ElementGates.From(prefab.elemental) : ElementSet.None;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[AreMyGemsCompatible] cannot read the prefab of " + type.Name + ": " + e.Message);
                return null;
            }
        }
    }

    // Anything that could hand a memory's damage an element it does not deal itself, and which.
    //
    // DamageData.SetElemental is the one way an element is written. Found by scanning every method
    // in Dew.Core and Dew.Contents, the callers outside the memories' own code are:
    //
    //   - essences: Sulfur and Abyss make the memory's damage Fire and Dark, Inversion swaps Fire
    //     for Cold and Light for Dark, Twilight, Pain and Frost itself deal elemental damage of
    //     their own through it;
    //   - status effects on the hero: a Husk star that makes her damage Dark, and several
    //     memories' own buffs;
    //   - RoomMod_InversionSigil, a room modifier that swaps every element in the room and gives
    //     damage with none a random one;
    //   - hazards - lava, a fireplace, traps - which deal their own damage and touch nobody's.
    //
    // A sibling in the same memory, a status on the hero and a modifier in the room are all asked
    // at the moment of the verdict, and **what each one can write is added to what the memory
    // deals**: the constants it hands SetElemental, read the same way MemoryElements reads a
    // memory's own code. So Frost beside Inversion in Incendiary Rounds is quiet - Inversion can
    // write Cold - but Frost beside Sulfur is still warned about, since Sulfur only ever writes
    // Fire. Anything that writes an element out of a variable - Pain copying the hit's, the sigil's
    // random pick - makes the answer unknown, and unknown says nothing.
    //
    // Adding rather than replacing over-counts: Inversion in a memory with no element does
    // nothing, and Twilight changes attacks, not memories. Both errors are toward saying nothing.
    // This reads code rather than naming the list above, so a status or an essence another mod
    // adds counts the same way.
    //
    // Asked of what the client can see, deliberately. The damage processors themselves are
    // registered on the server, and a guest's copy of the hero has none of them.
    internal static class ElementChangers
    {
        private const string SetElemental = "SetElemental";

        private const BindingFlags Declared =
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.DeclaredOnly;

        // Where the walk up a type's bases stops: these are the engine, not the thing being asked.
        private static readonly HashSet<string> BaseRoots = new HashSet<string>
        {
            "Actor", "AbilityInstance", "StatusEffect", "BasicEffect", "Entity", "Gem", "SkillTrigger",
            "AbilityTrigger", "StarEffect", "RoomModifierBase", "DamageInstance", "InstantDamageInstance",
            "TickDamageInstance", "StandardProjectile", "Projectile", "PersistentStatBonusEffect",
        };

        private static readonly Dictionary<Type, ElementSet?> Constants = new Dictionary<Type, ElementSet?>();

        // Bodies that exist and would not read. A body that could not be looked at might have
        // held the one call that mattered, so ConstantsIn treats one as unknown. Only differences
        // are looked at; everything here runs on the main thread.
        private static int _unreadable;

        // The elements that something other than the memory could give its damage right now, or
        // null if that cannot be known.
        public static ElementSet? AddedFor(Gem gem, SkillTrigger skill)
        {
            var added = ElementSet.None;
            var owner = skill.owner;

            if (owner != null && owner.Skill != null && owner.Skill.gems != null)
            {
                foreach (var pair in owner.Skill.gems)
                {
                    var other = pair.Value;
                    if (other == null || other == gem || other.skill != skill) continue;

                    // Whatever a sibling makes the memory deal, through its cast, is of no known
                    // element.
                    if ((GemTriggers.Of(other).Supplies & SlotNeed.Damage) != SlotNeed.None) return null;

                    if (!Add(ref added, other.GetType())) return null;
                }
            }

            if (owner != null && owner.Status != null)
            {
                foreach (var effect in owner.Status.statusEffects)
                    if (effect != null && !Add(ref added, effect.GetType())) return null;
            }

            foreach (var modifier in UnityEngine.Object.FindObjectsByType<RoomModifierBase>(FindObjectsSortMode.None))
                if (modifier != null && !Add(ref added, modifier.GetType())) return null;

            return added;
        }

        private static bool Add(ref ElementSet into, Type type)
        {
            var found = Written(type);
            if (!found.HasValue) return false;
            into |= found.Value;
            return true;
        }

        public static void Reset()
        {
            Constants.Clear();
            WrittenCache.Clear();
        }

        // Creation helpers, followed to what they create - the same list GemTriggers follows.
        private static readonly string[] CreateAny =
        {
            "CreateAbilityInstance", "CreateStatusEffect", "CreateBasicEffect", "CreateEntity",
        };

        // How far creation is followed; GemTriggers stops at the same depth.
        private const int MaxCreationDepth = 3;

        private static readonly Dictionary<Type, ElementSet?> WrittenCache = new Dictionary<Type, ElementSet?>();

        // Every element a type can write, including through what it creates. **The creations are
        // most of the answer for essences**: Gem_C_Charcoal's own code has no element in it, and
        // the projectile it fires, Ai_Gem_C_Charcoal_Projectile, deals Fire damage parented under
        // the memory whose hit set it off. Reading the essence alone would warn about Essence of
        // Fever beside it in a memory with no element, and Fever works there. The created types
        // are the generic arguments of the creation calls, and a created DamageInstance is asked
        // for its prefab's element as well.
        public static ElementSet? Written(Type root)
        {
            ElementSet? cached;
            if (WrittenCache.TryGetValue(root, out cached)) return cached;

            var found = ElementSet.None;
            ElementSet? result = null;
            var seen = new HashSet<Type>();
            var frontier = new List<Type> { root };
            bool unknown = false;

            for (int depth = 0; depth < MaxCreationDepth && frontier.Count > 0 && !unknown; depth++)
            {
                var next = new List<Type>();
                foreach (var type in frontier)
                {
                    if (type == null || !seen.Add(type)) continue;

                    var constants = ConstantsIn(type);
                    var prefab = MemoryElements.FromPrefab(type);
                    if (!constants.HasValue || !prefab.HasValue)
                    {
                        unknown = true;
                        break;
                    }
                    found |= constants.Value | prefab.Value;

                    foreach (var method in Methods(type))
                    {
                        foreach (var pair in Body(method))
                        {
                            var called = pair.Value as MethodInfo;
                            if (called == null || !called.IsGenericMethod || !StartsWithAny(called.Name, CreateAny)) continue;
                            foreach (var argument in called.GetGenericArguments())
                                if (argument != null && !argument.IsGenericParameter) next.Add(argument);
                        }
                    }
                }
                frontier = next;
            }

            if (!unknown) result = found;
            WrittenCache[root] = result;
            return result;
        }

        private static bool StartsWithAny(string name, string[] prefixes)
        {
            for (int i = 0; i < prefixes.Length; i++)
                if (name.StartsWith(prefixes[i], StringComparison.Ordinal)) return true;
            return false;
        }

        // The elements a type hands to SetElemental as constants, or null if any call hands it
        // something else. The C# compiler turns `SetElemental(ElementalType.Cold)` into
        // `ldc.i4.1; newobj Nullable<ElementalType>(...); call SetElemental`, so the constant is
        // whatever was last pushed before the call, looking through the one newobj.
        public static ElementSet? ConstantsIn(Type type)
        {
            ElementSet? cached;
            if (Constants.TryGetValue(type, out cached)) return cached;

            int before = _unreadable;
            ElementSet? result = ElementSet.None;
            foreach (var method in Methods(type))
            {
                int? last = null;
                foreach (var pair in Body(method))
                {
                    var op = pair.Key;
                    int? pushed = Pushed(op, pair.Value);
                    if (pushed.HasValue)
                    {
                        last = pushed;
                        continue;
                    }

                    var called = pair.Value as MethodBase;
                    if (called != null && called.Name == SetElemental)
                    {
                        if (!last.HasValue || last.Value < 0 || last.Value > 3)
                        {
                            result = null;
                            break;
                        }
                        result = result.Value | ElementGates.From((ElementalType)last.Value);
                        last = null;
                        continue;
                    }

                    if (op != OpCodes.Newobj) last = null;
                }
                if (!result.HasValue) break;
            }

            if (_unreadable != before) result = null;
            Constants[type] = result;
            return result;
        }

        private static int? Pushed(OpCode op, object operand)
        {
            if (op == OpCodes.Ldc_I4_0) return 0;
            if (op == OpCodes.Ldc_I4_1) return 1;
            if (op == OpCodes.Ldc_I4_2) return 2;
            if (op == OpCodes.Ldc_I4_3) return 3;
            if (op == OpCodes.Ldc_I4_S || op == OpCodes.Ldc_I4) return operand != null ? Convert.ToInt32(operand) : (int?)null;
            return null;
        }

        // The type, everything nested in it, and its bases down to the engine's own classes.
        private static IEnumerable<MethodBase> Methods(Type type)
        {
            for (var current = type; current != null && !BaseRoots.Contains(current.Name); current = current.BaseType)
            {
                foreach (var nested in WithNested(current))
                {
                    foreach (var method in nested.GetMethods(Declared)) yield return method;
                    foreach (var ctor in nested.GetConstructors(Declared)) yield return ctor;
                }
            }
        }

        private static IEnumerable<Type> WithNested(Type type)
        {
            yield return type;
            foreach (var nested in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
                foreach (var found in WithNested(nested))
                    yield return found;
        }

        private static IEnumerable<KeyValuePair<OpCode, object>> Body(MethodBase method)
        {
            // Abstract, extern and runtime-provided methods have no IL, and so nothing to find.
            if (method.IsAbstract || method.GetMethodBody() == null) yield break;

            IEnumerable<KeyValuePair<OpCode, object>> body;
            try
            {
                body = PatchProcessor.ReadMethodBody(method);
            }
            catch (Exception e)
            {
                _unreadable++;
                Debug.LogWarning("[AreMyGemsCompatible] cannot read " + method.DeclaringType?.Name + "." + method.Name + ": " + e.Message);
                yield break;
            }

            foreach (var pair in body) yield return pair;
        }
    }
}
