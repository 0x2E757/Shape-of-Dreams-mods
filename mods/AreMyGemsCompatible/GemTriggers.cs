using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace AreMyGemsCompatible
{
    [Flags]
    internal enum SlotNeed
    {
        None = 0,
        Damage = 1,
        Heal = 2,
        Shield = 4,
    }

    // What an essence waits for, and whether any part of it is waiting for nothing.
    internal struct GemProfile
    {
        // The union of what would wake this essence up. An essence with more than one is alive as
        // soon as the memory supplies any of them, not all.
        public SlotNeed Needs;

        // Something about this essence works whatever memory it sits in - a stat bonus, a hook on
        // the hero, an effect on every cast. An essence like that can be diminished by the wrong
        // memory but never dead, and nothing is said about it.
        public bool AlwaysLive;

        // What this essence hands to the memory it sits in. An essence that creates something
        // parented under the memory's own cast makes the *memory* the actor for whatever that
        // something does, so a memory whose description promises nothing can still deal damage,
        // heal or shield because of an essence beside it. See Verdict.
        public SlotNeed Supplies;
    }

    // An essence reaches the memory it is socketed into through Gem.OnEquipSkill, and what it
    // subscribes to there is that memory's own events - not the hero's:
    //
    //     newSkill.TriggerEvent_OnCastComplete              += OnCastComplete;
    //     newSkill.TriggerEvent_OnCastCompleteBeforePrepare += OnCastCompleteBeforePrepare;
    //     newSkill.ActorEvent_OnDealDamage                  += OnDealDamage;
    //     newSkill.ActorEvent_OnDoHeal                      += OnDoHeal;
    //
    // **Those four virtuals are not the whole vocabulary, and assuming they were is the mistake
    // this class exists to avoid.** Thirty-three of the ninety-five shipped essences override
    // OnEquipSkill and subscribe to the memory directly, and what they reach for there is wider:
    // dealtDamageProcessor (fifteen of them), dealtHealProcessor (five), dealtShieldProcessor,
    // ActorEvent_OnGiveShield, TrackKills. All of those starve in a memory that never does the
    // thing. AddSkillBonus and TriggerEvent_OnCastStart, in the same overrides, never starve.
    //
    // Reading which of them an essence uses cannot be done by looking at method names alone, so
    // the two equip methods are read as IL - Harmony's PatchProcessor.ReadMethodBody hands back
    // each operand already resolved to a FieldInfo or a MethodBase, so the member names a method
    // touches are simply the operand names. Methods the essence declares on itself are followed
    // one level deeper, because an override that calls its own private helper would otherwise
    // look empty.
    //
    // The whole thing is done by reflection over the live type rather than from a table, so an
    // essence added by another mod is classified the same way as a shipped one.
    internal static class GemTriggers
    {
        private static readonly Dictionary<Type, GemProfile> Cache = new Dictionary<Type, GemProfile>();

        private const BindingFlags Declared =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        // Members on the memory that only ever fire when the memory does a particular thing.
        private static readonly Dictionary<string, SlotNeed> SlotScoped = new Dictionary<string, SlotNeed>
        {
            ["dealtDamageProcessor"] = SlotNeed.Damage,
            ["ActorEvent_OnDealDamage"] = SlotNeed.Damage,
            ["TrackKills"] = SlotNeed.Damage,
            ["dealtHealProcessor"] = SlotNeed.Heal,
            ["ActorEvent_OnDoHeal"] = SlotNeed.Heal,
            ["dealtShieldProcessor"] = SlotNeed.Shield,
            ["ActorEvent_OnGiveShield"] = SlotNeed.Shield,
        };

        // Members that fire, or apply, whatever the memory does. Every memory is cast, so anything
        // hanging off a cast event is live wherever it goes; a skill bonus is applied on equip and
        // never waits for anything at all.
        private static readonly string[] AlwaysLiveOnSkill =
        {
            "AddSkillBonus", "TriggerEvent_", "SetCharge", "LockCooldown", "configs", "abilityIndex",
            "mainConfigOriginalCharge", "specialOverlayColor", "ClientTriggerEvent_",
        };

        // Members on the *hero* - or on the essence's own owner - which is a different lifetime
        // from the slot. Gem_E_Twilight hooks EntityEvent_OnAttackFiredBeforePrepare on the hero
        // and also overrides OnDealDamage; half of it ignores the memory entirely, so the worst
        // the wrong memory can do to it is halve it.
        private static readonly string[] AlwaysLiveOnHero =
        {
            "EntityEvent_", "ActorEvent_", "ClientHeroEvent_", "ClientEntityEvent_", "takenDamageProcessor",
            "AddStatBonus", "CreateStatusEffect", "CreateBasicEffect", "TrackKills", "get_Status", "get_Ability",
        };

        // The three helpers that parent what they create under an actor of the caller's choosing.
        // Each is a thin wrapper - CreateStatusEffectWithSource(source, ...) is source.
        // CreateStatusEffect(...) - so the source becomes the created actor's parentActor, and
        // Actor.InvokeOnDealDamage walks that chain. Their plain counterparts parent under the
        // essence instead, which never reaches the memory.
        private static readonly string[] CreateWithSource =
        {
            "CreateAbilityInstanceWithSource", "CreateStatusEffectWithSource", "CreateBasicEffectWithSource",
        };

        // The field the source has to be, spelled the way a qualified operand name is recorded.
        private const string CastInstance = "EventInfoCast.instance";

        // The three ways an actor does something to somebody, and the two data types that carry
        // the same thing to the same place - DamageInstance ends in `dmg.Dispatch(entity, chain)`
        // rather than in a DealDamage call of its own.
        private const string DealDamage = "DealDamage";
        private const string DoHeal = "DoHeal";
        private const string GiveShield = "GiveShield";
        private const string Dispatch = "Dispatch";
        private const string DamageData = "DamageData";
        private const string HealData = "HealData";

        // Creation helpers of any kind, used to follow a spawner through to the thing it spawns.
        private static readonly string[] CreateAny =
        {
            "CreateAbilityInstance", "CreateStatusEffect", "CreateBasicEffect", "CreateEntity",
        };

        // Where the walk up a created type's base classes stops. These declare DealDamage, DoHeal
        // and GiveShield rather than calling them, so walking into them would make every actor in
        // the game read as doing all three.
        private static readonly HashSet<string> BaseRoots = new HashSet<string>
        {
            "Actor", "AbilityInstance", "StatusEffect", "BasicEffect", "Entity",
            "Gem", "SkillTrigger", "AbilityTrigger",
        };

        // How far a spawner chain is followed. Two hops covers a status effect that spawns an
        // ability instance that does the work, which is the deepest shape the shipped essences use.
        private const int MaxCreationDepth = 3;

        public static GemProfile Of(Gem gem)
        {
            if (gem == null) return default(GemProfile);

            var profile = Of(gem.GetType());

            // enableStatBonus and the StatBonus behind it are prefab data, not code: Gem_E_Might
            // reads as nothing but a damage amplifier until you notice the flat Maximum Health it
            // grants through Gem.OnEquipGem. That half is applied on equip and works in any memory
            // whatsoever, so the essence is never dead.
            if (gem.enableStatBonus) profile.AlwaysLive = true;

            return profile;
        }

        public static GemProfile Of(Type gemType)
        {
            GemProfile cached;
            if (Cache.TryGetValue(gemType, out cached)) return cached;

            var profile = Build(gemType);
            Cache[gemType] = profile;
            return profile;
        }

        public static void ClearCache()
        {
            Cache.Clear();
        }

        private static GemProfile Build(Type gemType)
        {
            var profile = default(GemProfile);

            // Up to but not including Gem itself: the base class declares all four virtuals empty,
            // and a body that does nothing is not a subscription to anything.
            for (var type = gemType; type != null && type != typeof(Gem); type = type.BaseType)
            {
                foreach (var method in type.GetMethods(Declared))
                {
                    switch (method.Name)
                    {
                        case "OnDealDamage": profile.Needs |= SlotNeed.Damage; break;
                        case "OnDoHeal": profile.Needs |= SlotNeed.Heal; break;

                        // Every memory raises both cast events, so an essence built on either is
                        // live in any slot.
                        case "OnCastComplete":
                        case "OnCastCompleteBeforePrepare":
                            profile.AlwaysLive = true;
                            break;

                        case "OnEquipSkill":
                            ReadEquipSkill(method, gemType, ref profile);
                            break;

                        case "OnEquipGem":
                            ReadEquipGem(method, gemType, ref profile);
                            break;
                    }
                }
            }

            profile.Supplies = ReadSupplies(gemType);
            return profile;
        }

        // What an essence hands to the memory it sits in, answered in two steps and without
        // reading a word of anything.
        //
        // **Step one: does it create through the memory's cast at all?** The three
        // Gem.Create*WithSource helpers each parent what they create under the source they are
        // given, so passing `info.instance` puts the new actor under the memory and
        // Actor.InvokeOnDealDamage walks that chain back up to it.
        //
        // Both halves - a *WithSource call and a reach for EventInfoCast.instance - are asked of
        // the whole type rather than of the call site, deliberately, because the source is rarely
        // written at the call. Gem_L_SolarEye copies it into a local first, Gem_U_LastStarlight
        // creates against itself and then assigns `_instance.parentActor = info.instance`, and
        // several put the call inside a lambda the compiler moves into a nested class. Asking
        // whether the type does both answers all three shapes without tracing an argument back to
        // where it came from.
        //
        // What that keeps out is the essence that touches the cast without creating through it.
        // Gem_E_Overload adds amplifying processors to `info.instance` and creates its health
        // cost against itself; amplification of nothing is nothing. Gem_R_Rejuvenation and
        // Gem_R_Composure are the same shape.
        //
        // **Step two: what do the created things do?** Their types come free - they are the
        // generic arguments of the very calls found in step one - and reading them is exact where
        // reading the essence's description is not. See ReadCapabilities.
        private static SlotNeed ReadSupplies(Type gemType)
        {
            bool touchesCast = false;
            var created = new List<Type>();

            foreach (var type in Scanned(gemType))
            {
                foreach (var method in type.GetMethods(Declared))
                {
                    foreach (var operand in Operands(method, gemType))
                    {
                        var field = operand as FieldInfo;
                        if (field != null)
                        {
                            if (field.DeclaringType != null &&
                                field.DeclaringType.Name + "." + field.Name == CastInstance)
                                touchesCast = true;
                            continue;
                        }

                        var called = operand as MethodInfo;
                        if (called == null || !StartsWithAny(called.Name, CreateWithSource)) continue;
                        AddGenericArguments(called, created);
                    }
                }
            }

            if (!touchesCast || created.Count == 0) return SlotNeed.None;
            return ReadCapabilities(created);
        }

        // What a set of created types ends up doing to somebody, following each one up its base
        // classes and onward through whatever it creates in turn.
        //
        // **The base classes are where the answer usually is.** Ai_E_Aftershock_Damage and
        // Ai_Gem_R_Scorched_Meteor declare nothing but an OnHit and a bit of movement; both derive
        // from InstantDamageInstance, and it is the abstract DamageInstance above that which ends
        // in `dmg.Dispatch(entity, chain)`. Reading only a type's own methods finds nothing at all
        // for either, which is a warning left standing where it should have been withdrawn.
        //
        // The walk stops below Actor and its peers, which declare DealDamage, DoHeal and
        // GiveShield rather than calling them - stepping into those would make every actor in the
        // game read as doing all three.
        //
        // Following creation onward is needed for the spawners: Gem_C_Sharp creates
        // Se_Gem_C_Sharp_ArrowSpawner, which is what creates the arrows that do the damage.
        private static SlotNeed ReadCapabilities(List<Type> roots)
        {
            var found = SlotNeed.None;
            var seen = new HashSet<Type>();
            var frontier = roots;

            for (int depth = 0; depth < MaxCreationDepth && frontier.Count > 0; depth++)
            {
                var next = new List<Type>();

                foreach (var root in frontier)
                {
                    if (root == null || !seen.Add(root)) continue;

                    foreach (var type in WithBases(root))
                    {
                        foreach (var method in type.GetMethods(Declared))
                        {
                            foreach (var operand in Operands(method, root))
                            {
                                var called = operand as MethodBase;
                                if (called == null) continue;

                                found |= CapabilityOf(called);

                                var info = called as MethodInfo;
                                if (info != null && StartsWithAny(info.Name, CreateAny))
                                    AddGenericArguments(info, next);
                            }
                        }
                    }
                }

                frontier = next;
            }

            return found;
        }

        private static SlotNeed CapabilityOf(MethodBase called)
        {
            switch (called.Name)
            {
                case DealDamage: return SlotNeed.Damage;
                case DoHeal: return SlotNeed.Heal;
                case GiveShield: return SlotNeed.Shield;
                case Dispatch:
                    var owner = called.DeclaringType != null ? called.DeclaringType.Name : null;
                    if (owner == DamageData) return SlotNeed.Damage;
                    if (owner == HealData) return SlotNeed.Heal;
                    return SlotNeed.None;
                default: return SlotNeed.None;
            }
        }

        private static void AddGenericArguments(MethodInfo method, List<Type> into)
        {
            if (!method.IsGenericMethod) return;
            foreach (var argument in method.GetGenericArguments())
                if (argument != null && !argument.IsGenericParameter) into.Add(argument);
        }

        // A created type, everything nested in it, and the same for each of its base classes down
        // to but not including the roots.
        private static IEnumerable<Type> WithBases(Type type)
        {
            for (var current = type;
                 current != null && !BaseRoots.Contains(current.Name);
                 current = current.BaseType)
                foreach (var found in WithNested(current))
                    yield return found;
        }

        // The essence's own types, up to but not including Gem, plus every nested class the
        // compiler generated underneath them - which is where a good deal of essence code
        // actually lives.
        //
        // **Nesting has to be followed all the way down, not one level.** Gem_E_Aftershock is the
        // case that proves it: its creation call sits in a local `IEnumerator Routine()` inside
        // OnCastComplete, so the compiler puts the captured variables in a display class nested
        // under the essence, and the iterator's actual body in a state machine nested under
        // *that*. One level of nesting reaches the display class, whose only method constructs
        // the state machine, and sees nothing at all.
        private static IEnumerable<Type> Scanned(Type gemType)
        {
            for (var type = gemType; type != null && type != typeof(Gem); type = type.BaseType)
                foreach (var found in WithNested(type))
                    yield return found;
        }

        private static IEnumerable<Type> WithNested(Type type)
        {
            yield return type;
            foreach (var nested in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
                foreach (var found in WithNested(nested))
                    yield return found;
        }

        private static void ReadEquipSkill(MethodBase method, Type gemType, ref GemProfile profile)
        {
            var names = MemberNames(method, gemType);
            bool recognised = false;

            foreach (var name in names)
            {
                SlotNeed need;
                if (SlotScoped.TryGetValue(name, out need))
                {
                    profile.Needs |= need;
                    recognised = true;
                    continue;
                }
                if (StartsWithAny(name, AlwaysLiveOnSkill))
                {
                    profile.AlwaysLive = true;
                    recognised = true;
                }
            }

            // An override that reaches for something not on either list is doing something this
            // mod does not understand. Unknown is not the same as dead, and the notes on this mod
            // are emphatic that getting it wrong in the loud direction is worse than saying
            // nothing, so an unrecognised override silences the essence.
            if (!recognised) profile.AlwaysLive = true;
        }

        private static void ReadEquipGem(MethodBase method, Type gemType, ref GemProfile profile)
        {
            // OnEquipGem is where an essence hooks the hero, and an essence that hooks the hero
            // cannot be dead. But it is also where the cosmetic ones live: Gem_C_Confidence
            // overrides it only to play an aura effect, and is otherwise a pure damage amplifier
            // that a memory dealing no damage really does silence. So the override is read rather
            // than counted.
            foreach (var name in MemberNames(method, gemType))
            {
                if (StartsWithAny(name, AlwaysLiveOnHero))
                {
                    profile.AlwaysLive = true;
                    return;
                }
            }
        }

        private static bool StartsWithAny(string name, string[] prefixes)
        {
            for (int i = 0; i < prefixes.Length; i++)
                if (name.StartsWith(prefixes[i], StringComparison.Ordinal)) return true;
            return false;
        }

        // The names of every member the method touches. Fields come back twice, bare and
        // qualified, because one question needs to know whose field it is: EventInfoCast.instance
        // is the memory's cast, and a bare "instance" would match any number of unrelated fields.
        // The qualified form carries a dot and so can never collide with a bare-name rule.
        private static IEnumerable<string> MemberNames(MethodBase method, Type context)
        {
            foreach (var operand in Operands(method, context))
            {
                var field = operand as FieldInfo;
                if (field != null)
                {
                    yield return field.Name;
                    if (field.DeclaringType != null)
                        yield return field.DeclaringType.Name + "." + field.Name;
                    continue;
                }

                var called = operand as MethodBase;
                if (called != null) yield return called.Name;
            }
        }

        // Every field and method a body refers to, resolved, following calls to the essence's own
        // methods one level down. Depth is capped at one because the point is to see through a
        // private helper, not to build a call graph.
        private static IEnumerable<object> Operands(MethodBase method, Type context)
        {
            foreach (var operand in Read(method, context))
            {
                yield return operand;

                // A helper declared by an essence itself, and not the method that brought us here.
                var called = operand as MethodBase;
                if (called == null || called == method) continue;
                if (called.DeclaringType == null || !typeof(Gem).IsAssignableFrom(called.DeclaringType)) continue;
                if (called.DeclaringType == typeof(Gem)) continue;

                foreach (var deeper in Read(called, context))
                    yield return deeper;
            }
        }

        private static IEnumerable<object> Read(MethodBase method, Type context)
        {
            IEnumerable<KeyValuePair<System.Reflection.Emit.OpCode, object>> body;
            try
            {
                body = PatchProcessor.ReadMethodBody(method);
            }
            catch (Exception e)
            {
                // A body that cannot be read is a body whose contents are unknown, and an
                // unrecognised override is treated as always live. Logged rather than swallowed,
                // because it would otherwise look like a classification result.
                Debug.LogWarning("[AreMyGemsCompatible] cannot read " + context.Name + "." + method.Name + ": " + e.Message);
                yield break;
            }

            foreach (var pair in body)
                if (pair.Value != null) yield return pair.Value;
        }
    }
}
