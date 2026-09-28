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

        // The memory being cast at all. Every Q/W/E/R memory and every Movement memory is, so this
        // is met wherever an essence could go until the game let identity memories take one: those
        // are passive, AbilityTrigger.OnCastStart throws for a config that is not isActive, and
        // neither cast event is ever raised on them.
        Cast = 8,
    }

    // What an essence waits for, and whether any part of it is waiting for nothing.
    internal struct GemProfile
    {
        // The union of what would wake this essence up. An essence with more than one is alive as
        // soon as the memory supplies any of them, not all.
        public SlotNeed Needs;

        // Something about this essence works whatever memory it sits in - a stat bonus, a hook on
        // the hero. An essence like that can be diminished by the wrong memory but never dead, and
        // nothing is said about it. An effect on every cast is not this any more: it is
        // SlotNeed.Cast, because an identity memory is never cast.
        public bool AlwaysLive;

        // What this essence hands to the memory it sits in. An essence that creates something
        // parented under the memory's own cast makes the *memory* the actor for whatever that
        // something does, so a memory whose description promises nothing can still deal damage,
        // heal or shield because of an essence beside it. See Verdict.
        public SlotNeed Supplies;

        // What this essence does by itself - its own DealDamage, Heal(...).Dispatch, the barrier
        // of a status effect it creates - and so hands to the memory whenever it fires. A socketed
        // essence's parentActor *is* the memory (HeroSkill.EquipGem), and Actor.ProcessDealtHeal
        // and Actor.InvokeOnDoHeal, like their damage and barrier twins, walk up parentActor: so
        // Essence of Blossoming's heal is amplified by Essence of Guidance beside it, and Essence
        // of Love reacts to it, exactly as if the memory had healed. Watched in a fight: Guidance
        // beside Blossom in Teal Blade, which heals nothing itself, amplified every one of
        // Blossom's heals. Counts only while the essence itself fires - see Verdict.
        public SlotNeed OwnSupplies;

        // The one element this essence's damage trigger answers to, or None for any damage at
        // all. See ElementGates.
        public ElementSet Gate;
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
    // thing. AddSkillBonus and TriggerEvent_OnCastStart, in the same overrides, starve only in a
    // memory that is never cast - an identity memory.
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

        // How many method bodies have failed to read, ever. Only differences are looked at, so it
        // never needs resetting; everything here runs on the main thread.
        private static int _unreadable;

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

        // Members that only matter to a memory that is cast. The three TriggerEvent_ events are
        // raised after AbilityTrigger's own check that the config is not passive, and a SkillBonus
        // is nothing but a cooldown multiplier, a cooldown offset and added charges - all of which
        // a passive memory has no use for. Gem_E_Direness is only a SkillBonus, and in an identity
        // memory it does nothing at all.
        private static readonly string[] CastScopedOnSkill =
        {
            "TriggerEvent_", "AddSkillBonus", "SetCharge", "LockCooldown", "mainConfigOriginalCharge",
        };

        // Members whose effect this mod has not pinned down, and which are therefore treated as
        // working in any memory. Getting it wrong this way round only costs a warning.
        private static readonly string[] AlwaysLiveOnSkill =
        {
            "configs", "abilityIndex", "specialOverlayColor", "ClientTriggerEvent_",
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
        //
        // Two helpers do the same under another name. Actor.DoBasicAttackHit builds and dispatches
        // the hit's DamageData as the calling actor; StatusEffect.DoShield registers a
        // ShieldEffect, which raises InvokeOnGiveShield on the effect that made it -
        // Se_M_DreamyWaltz_Buff is the barrier a Dreamy Waltz grants.
        private const string DealDamage = "DealDamage";
        private const string DoBasicAttackHit = "DoBasicAttackHit";
        private const string DoHeal = "DoHeal";
        private const string GiveShield = "GiveShield";
        private const string DoShield = "DoShield";
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
            bool castHandler = false;
            var fromEquip = SlotNeed.None;

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

                        // Live in every memory that is cast, which is every memory but an identity.
                        case "OnCastComplete":
                        case "OnCastCompleteBeforePrepare":
                            profile.Needs |= SlotNeed.Cast;
                            castHandler = true;
                            break;

                        case "OnEquipSkill":
                            var before = profile.Needs;
                            ReadEquipSkill(method, gemType, ref profile);
                            fromEquip |= profile.Needs & ~before;
                            break;

                        case "OnEquipGem":
                            ReadEquipGem(method, gemType, ref profile);
                            break;
                    }
                }
            }

            profile.Supplies = ReadSupplies(gemType);

            // **An empowered cast is armed by the cast and paid out by its damage.** Essence of
            // Talc, Shatter, Finality, Responsibility, Pure White, the Celestial, Heart of Gold,
            // Overload and Rejuvenation all override a cast handler, and all it does is reach into
            // that cast - `info.instance.dealtDamageProcessor.Add(...)`,
            // `info.instance.ActorEvent_OnDealDamage += ...` - and wait for what the cast then deals
            // or heals. In a memory whose cast deals nothing they are armed and never pay: watched
            // in Undo and Mass Protection, each spent its cooldown on every cast and did nothing.
            // So an essence whose cast handler reaches the cast's own damage or healing needs that
            // damage or healing, not the cast.
            //
            // What the essence creates is no exception: the Celestial and Pure White create
            // through the cast, but from inside the handler they hang on the cast's damage, so
            // their meteors and shards come only from a cast that hits. An essence that creates on
            // the cast itself - Stillness, Last Starlight - never reaches the cast's damage and is
            // not touched by this.
            if (castHandler)
            {
                var paidBy = CastPaidBy(gemType);
                if (paidBy != SlotNeed.None)
                    profile.Needs = (profile.Needs & ~SlotNeed.Cast) | paidBy | (fromEquip & SlotNeed.Cast);
            }

            profile.OwnSupplies = ReadCapabilities(new List<Type> { gemType });

            // An essence in the element table is there because everything it does in the slot
            // sits behind that element, so nothing read out of its code makes it live anywhere
            // else. That matters for one of them: Gem_R_Frost's OnEquipGem creates
            // Se_Gem_R_Frost_Stat on the hero, which read as a hook on the hero and silenced the
            // essence in every memory. It is a PersistentStatBonusEffect created empty - the
            // Maximum Health it holds is added only when the essence fires. enableStatBonus,
            // which is prefab data, is still honoured in Of(Gem).
            profile.Gate = ElementGates.Of(gemType);
            if (profile.Gate != ElementSet.None) profile.AlwaysLive = false;

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

        // What of the cast's own doing an essence waits for: the needs of every member it reaches
        // on EventInfoCast.instance - asked per method, since the reach is `info.instance.X` in
        // one body, and in the essence and everything nested in it, since a lambda or a coroutine
        // is where it usually sits.
        private static SlotNeed CastPaidBy(Type gemType)
        {
            var found = SlotNeed.None;
            foreach (var type in Scanned(gemType))
            {
                foreach (var method in type.GetMethods(Declared))
                {
                    bool cast = false;
                    var needs = SlotNeed.None;
                    foreach (var name in MemberNames(method, gemType))
                    {
                        if (name == CastInstance) cast = true;
                        SlotNeed need;
                        if (SlotScoped.TryGetValue(name, out need)) needs |= need;
                    }
                    if (cast) found |= needs;
                }
            }
            return found;
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
        //
        // PassiveMemory asks the same question of an identity memory's own code, and for it a body
        // that could not be read matters: "found nothing" is a verdict there, and "could not look"
        // must not become one. So it is told whether everything was read.
        public static SlotNeed ReadCapabilities(List<Type> roots, out bool complete)
        {
            int before = _unreadable;
            var found = ReadCapabilities(roots);
            complete = _unreadable == before;
            return found;
        }

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
                case DealDamage:
                case DoBasicAttackHit:
                    return SlotNeed.Damage;
                case DoHeal: return SlotNeed.Heal;
                case GiveShield:
                case DoShield:
                    return SlotNeed.Shield;
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
            bool recognised = false;
            bool touchesOthers = false;

            foreach (var name in SkillMemberNames(method, gemType))
            {
                if (name == null) continue;
                touchesOthers = true;

                SlotNeed need;
                if (SlotScoped.TryGetValue(name, out need))
                {
                    profile.Needs |= need;
                    recognised = true;
                    continue;
                }
                if (StartsWithAny(name, CastScopedOnSkill))
                {
                    profile.Needs |= SlotNeed.Cast;
                    recognised = true;
                }
                if (StartsWithAny(name, AlwaysLiveOnSkill))
                {
                    profile.AlwaysLive = true;
                    Because(gemType, "OnEquipSkill reaches " + name);
                    recognised = true;
                }
                else if (!recognised) Because(gemType, "OnEquipSkill reaches " + name + ", which is on no list");
            }

            // An override that reaches for something not on either list is doing something this
            // mod does not understand. Unknown is not the same as dead, and the notes on this mod
            // are emphatic that getting it wrong in the loud direction is worse than saying
            // nothing, so an unrecognised override silences the essence.
            //
            // One that touches nothing but the essence's own state is not that: Essence of
            // Finality's OnEquipSkill only zeroes its own charge and number display, and reading
            // it as "unknown" made it live everywhere - in a fight, in memories that deal no
            // damage, it armed on every cast and never amplified anything.
            if (!recognised && touchesOthers) profile.AlwaysLive = true;
        }

        // MemberNames, less what is the essence's own business: its fields, its properties
        // (numberDisplay, isServer, a SyncVar's generated setter) and the base call it chains to.
        // Those come back as null, so that a body made of nothing else reads as touching nothing.
        // Any other method, even one the essence inherits, still counts - CreateStatusEffect is
        // an Actor method and may well be a hook on the hero.
        private static IEnumerable<string> SkillMemberNames(MethodBase method, Type gemType)
        {
            foreach (var operand in Operands(method, gemType))
            {
                // Declared by Gem or below it, never by Actor and up: dealtDamageProcessor is an
                // Actor field, and reached on the memory it is the whole point.
                var field = operand as FieldInfo;
                if (field != null)
                {
                    if (IsGemDeclared(field.DeclaringType))
                    {
                        yield return null;
                        continue;
                    }
                    yield return field.Name;
                    if (field.DeclaringType != null) yield return field.DeclaringType.Name + "." + field.Name;
                    continue;
                }

                var called = operand as MethodBase;
                if (called == null) continue;
                bool accessor = called.Name.StartsWith("get_", StringComparison.Ordinal) ||
                                called.Name.StartsWith("set_", StringComparison.Ordinal);
                // Something being made is nothing yet - a delegate, a SkillBonus, the Nullable<int>
                // behind `numberDisplay = 0` - and whatever it is handed to is a call of its own.
                bool made = called is ConstructorInfo;
                bool own = (IsGemDeclared(called.DeclaringType) && (accessor || called.Name == method.Name)) ||
                           Array.IndexOf(NetworkFlags, called.Name) >= 0 || made;
                yield return own ? null : called.Name;
            }
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
                    Because(gemType, "OnEquipGem reaches " + name);
                    return;
                }
            }
        }

        // Why an essence reads as live in any memory, for the Debug build's own questions:
        // GemTriggers.WhyAlive("Gem_E_Omega") through DevTools' /reflect/call.
        private static readonly Dictionary<Type, string> Reasons = new Dictionary<Type, string>();

        [System.Diagnostics.Conditional("DEBUG")]
        private static void Because(Type gemType, string reason)
        {
            if (!Reasons.ContainsKey(gemType)) Reasons[gemType] = reason;
        }

        public static string WhyAlive(string typeName)
        {
            foreach (var pair in Reasons)
                if (pair.Key.Name == typeName) return pair.Value;
            return null;
        }

        // The guards every server-side override opens with, and what a SyncVar's generated setter
        // does inside - Essence of Finality zeroes one in OnEquipSkill.
        private static readonly string[] NetworkFlags =
        {
            "get_isServer", "get_isClient", "get_isValid", "get_isActive", "GeneratedSyncVarSetter",
        };

        private static bool IsGemDeclared(Type type) => type != null && typeof(Gem).IsAssignableFrom(type);

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
                // because it would otherwise look like a classification result - and counted, for
                // the one caller to whom an empty answer means something.
                _unreadable++;
                Debug.LogWarning("[AreMyGemsCompatible] cannot read " + context.Name + "." + method.Name + ": " + e.Message);
                yield break;
            }

            foreach (var pair in body)
                if (pair.Value != null) yield return pair.Value;
        }
    }
}
