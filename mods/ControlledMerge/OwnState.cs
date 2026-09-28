using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace ControlledMerge
{
    // What copies of one essence used to share without meaning to.
    //
    // The stock game never has two of a kind, so an essence finds what it made by its type or by
    // its owner, and that is exact. With copies it is one thing between them. Watched in play: two
    // Wind in one memory gave the hero one Se_Gem_C_Wind, the second's; two Chaos Apples in one
    // memory turned it twice and left 3 casts where each promised 2. Each case below makes every
    // copy keep its own, so copies add up - which is what two tooltips say.
    //
    // 1. A buff on the hero found by type (OwnEffects). Wind destroys "the" Se_Gem_C_Wind before
    //    making its own; Composure resets "its" timer, Liberty takes "it" off when unequipped. The
    //    lookup is made to find only an effect this very essence made (Diminishing.SourceOf).
    //    Aftershock's shockwave, which does the same, is made to as well.
    //    Left shared on purpose: Frost's Se_Gem_R_Frost_Stat, Predation's and Culinary's stat
    //    bonuses, which gather what every copy adds; effects that are not the essence's own (Twilight's
    //    Se_Elm_Dark, Supersymmetry's one-shot protection); the Guiding Compass's curse.
    // 2. A mark on the target found by its caster, the hero (OwnMarks): Wound's mark, Love's buff
    //    on an ally, Fever's living bomb. The lookup also asks that this essence made it.
    // 3. An effect found by name (OwnNames): Void's and Dusk's empowered attack, which the next
    //    one of the same name replaces. The name is made the essence's own.
    // 4. A cooldown per enemy kept per hero (OwnEnemyCooldowns): Frost and Blade keep, on each
    //    enemy, when the hero last triggered them there. It is kept per essence as well.
    // 5. Stacks the first copy consumes (SharedConsumption): Twilight's Darkness on an enemy,
    //    Purity's elements on the hero. They are destroyed at the end of the frame instead of at
    //    once, so every copy that acts on them in that frame counts them - once each.
    // 6. Chaos Apple, which turns its memory into another on entering a room (ChaosAppleTurnsOnce,
    //    ChaosApples): only the first copy in a memory turns it, with the casts of all of them.
    //
    // The code changed is the essences' own, by transpiler, and a helper that stands in for a
    // lookup is handed what the method runs on (ldarg.0). That is the essence itself, or the
    // closure or coroutine it made, and GemIn finds the essence in it.
    internal static class OwnState
    {
        private static readonly Dictionary<Type, FieldInfo[]> PathTo = new Dictionary<Type, FieldInfo[]>();

        public static Gem GemIn(object holder)
        {
            if (holder is Gem gem) return gem;
            if (holder == null) return null;

            // What an essence created works for it (Diminishing.SourceOf).
            if (holder is AbilityInstance instance) return Diminishing.SourceOf(instance);

            var type = holder.GetType();
            if (!PathTo.TryGetValue(type, out var path)) PathTo[type] = path = FindPath(type, 3);
            if (path == null) return null;

            object at = holder;
            foreach (var field in path)
            {
                at = field.GetValue(at);
                if (at == null) return null;
            }
            return at as Gem;
        }

        // A field holding the essence, or a compiler-made class (named "<...>") that holds one.
        private static FieldInfo[] FindPath(Type type, int depth)
        {
            var fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            foreach (var field in fields)
                if (typeof(Gem).IsAssignableFrom(field.FieldType)) return new[] { field };
            if (depth <= 1) return null;
            foreach (var field in fields)
            {
                if (!field.FieldType.Name.StartsWith("<", StringComparison.Ordinal)) continue;
                var rest = FindPath(field.FieldType, depth - 1);
                if (rest != null) return new[] { field }.Concat(rest).ToArray();
            }
            return null;
        }

        public static IEnumerable<Type> WithNested(Type type)
        {
            yield return type;
            foreach (var nested in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
                foreach (var inner in WithNested(nested))
                    yield return inner;
        }

        public static IEnumerable<MethodInfo> Declared(Type type) =>
            type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public |
                            BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Where(m => !m.IsAbstract && !m.ContainsGenericParameters && m.GetMethodBody() != null);

        public static bool Calls(MethodBase method, Func<object, bool> operand) =>
            PatchProcessor.ReadMethodBody(method).Any(p => operand(p.Value));

        public static bool Mine(Actor made, Gem gem) => ReferenceEquals(Diminishing.SourceOf(made), gem);

        // Replaces each call matched with: the method's ldarg.0, then a call to the helper, which
        // takes the original call's arguments and that holder last.
        public static IEnumerable<CodeInstruction> Redirect(IEnumerable<CodeInstruction> instructions,
                                                            Func<object, MethodInfo> helperFor)
        {
            foreach (var instruction in instructions)
            {
                var helper = (instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt)
                    ? helperFor(instruction.operand) : null;
                if (helper == null)
                {
                    yield return instruction;
                    continue;
                }
                yield return new CodeInstruction(OpCodes.Ldarg_0).WithLabels(instruction.ExtractLabels());
                yield return new CodeInstruction(OpCodes.Call, helper);
            }
        }

        public static void Forget() => PathTo.Clear();
    }

    // 1.
    [HarmonyPatch]
    internal static class OwnEffects
    {
        private static readonly Type[] Kinds =
        {
            typeof(Gem_C_Wind), typeof(Gem_C_Quicksilver), typeof(Gem_C_Regeneration), typeof(Gem_R_Insatiable),
            typeof(Gem_E_Obsidian), typeof(Gem_E_Insight), typeof(Gem_R_Epiphany), typeof(Gem_R_Panic),
            typeof(Gem_L_Liberty), typeof(Gem_R_Composure), typeof(Gem_E_Apathy), typeof(Gem_E_Protection),
            typeof(Gem_E_Reflex), typeof(Gem_R_Hedgehog), typeof(Gem_E_Insensitivity), typeof(Gem_E_Aftershock),
            typeof(Gem_R_Slippery), typeof(Gem_U_SoulPrison),
        };

        // What the essences create that looks up by type as well: Aftershock's shockwave takes every
        // Se_E_Aftershock_Armor off the hero before giving its own.
        private static readonly Type[] Instances = { typeof(Ai_E_Aftershock_Damage) };

        private static readonly MethodInfo Own = AccessTools.Method(typeof(OwnEffects), nameof(TryGetOwn));

        private static IEnumerable<MethodBase> TargetMethods() =>
            Kinds.Concat(Instances).SelectMany(OwnState.Declared)
                 .Where(m => !m.IsStatic && OwnState.Calls(m, o => IsLookup(o as MethodInfo)));

        private static bool IsLookup(MethodInfo method) =>
            method != null && method.IsGenericMethod && method.DeclaringType == typeof(EntityStatus) &&
            method.Name == nameof(EntityStatus.TryGetStatusEffect) && method.GetParameters().Length == 1;

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
            OwnState.Redirect(instructions, o => IsLookup(o as MethodInfo)
                ? Own.MakeGenericMethod(((MethodInfo)o).GetGenericArguments()[0]) : null);

        public static bool TryGetOwn<T>(EntityStatus status, out T effect, object holder) where T : StatusEffect
        {
            var gem = OwnState.GemIn(holder);
            if (ControlledMergeMod.Live == null || gem == null) return status.TryGetStatusEffect(out effect);

            var effects = status.statusEffects;
            for (int i = 0; i < effects.Count; i++)
            {
                if (effects[i] is T found && OwnState.Mine(found, gem))
                {
                    effect = found;
                    return true;
                }
            }
            effect = null;
            return false;
        }
    }

    // 2.
    [HarmonyPatch]
    internal static class OwnMarks
    {
        private static readonly (Type gem, Type mark)[] Kinds =
        {
            (typeof(Gem_R_Wound), typeof(Se_Gem_R_Wound_Wounded)),
            (typeof(Gem_C_Love), typeof(Se_Gem_C_Love)),
            (typeof(Gem_E_Fever), typeof(Se_Gem_E_Fever_LivingBomb)),
        };

        // The predicate handed to FindStatusEffect: a lambda taking the mark and answering bool.
        private static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (var (gem, mark) in Kinds)
                foreach (var type in OwnState.WithNested(gem))
                    foreach (var method in OwnState.Declared(type))
                    {
                        var parameters = method.GetParameters();
                        if (!method.IsStatic && method.ReturnType == typeof(bool) && parameters.Length == 1 &&
                            parameters[0].ParameterType == mark && method.Name.Contains("b__"))
                            yield return method;
                    }
        }

        private static void Postfix(object __instance, object[] __args, ref bool __result)
        {
            if (!__result || ControlledMergeMod.Live == null) return;
            var gem = OwnState.GemIn(__instance);
            if (gem != null && __args[0] is Actor mark && !OwnState.Mine(mark, gem)) __result = false;
        }
    }

    // 3.
    [HarmonyPatch]
    internal static class OwnNames
    {
        private static readonly string[] Names = { "void_empowerattack", "dusk_empower" };

        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(Gem_C_Void), "OnCastComplete");
            yield return AccessTools.Method(typeof(Gem_R_Dusk), "OnCastComplete");
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            foreach (var instruction in instructions)
            {
                yield return instruction;
                if (instruction.opcode == OpCodes.Ldstr && Names.Contains(instruction.operand as string))
                {
                    yield return new CodeInstruction(OpCodes.Ldarg_0);
                    yield return CodeInstruction.Call(typeof(OwnNames), nameof(For));
                }
            }
        }

        public static string For(string name, Gem gem) =>
            ControlledMergeMod.Live == null || gem == null ? name : name + "#" + gem.netId;
    }

    // 4.
    [HarmonyPatch]
    internal static class OwnEnemyCooldowns
    {
        private static readonly Type[] Kinds = { typeof(Gem_R_Frost), typeof(Gem_R_Blade) };

        private static readonly MethodInfo TryGet = AccessTools.Method(typeof(Dictionary<Entity, float>), nameof(Dictionary<Entity, float>.TryGetValue));
        private static readonly MethodInfo SetItem = AccessTools.PropertySetter(typeof(Dictionary<Entity, float>), "Item");
        private static readonly MethodInfo Add = AccessTools.Method(typeof(Dictionary<Entity, float>), nameof(Dictionary<Entity, float>.Add));

        // Beside the game's table on the enemy, keyed by hero, one keyed by essence.
        private static readonly ConditionalWeakTable<Dictionary<Entity, float>, Dictionary<Gem, float>> ByGem =
            new ConditionalWeakTable<Dictionary<Entity, float>, Dictionary<Gem, float>>();

        private static IEnumerable<MethodBase> TargetMethods() =>
            Kinds.SelectMany(OwnState.WithNested).SelectMany(OwnState.Declared)
                 .Where(m => !m.IsStatic && OwnState.Calls(m, o => Equals(o, TryGet)));

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
            OwnState.Redirect(instructions, o =>
                Equals(o, TryGet) ? AccessTools.Method(typeof(OwnEnemyCooldowns), nameof(TryGetFor)) :
                Equals(o, SetItem) ? AccessTools.Method(typeof(OwnEnemyCooldowns), nameof(SetFor)) :
                Equals(o, Add) ? AccessTools.Method(typeof(OwnEnemyCooldowns), nameof(AddFor)) : null);

        public static bool TryGetFor(Dictionary<Entity, float> table, Entity hero, out float time, object holder)
        {
            var gem = OwnState.GemIn(holder);
            if (ControlledMergeMod.Live == null || gem == null) return table.TryGetValue(hero, out time);
            time = 0f;
            return ByGem.TryGetValue(table, out var mine) && mine.TryGetValue(gem, out time);
        }

        public static void SetFor(Dictionary<Entity, float> table, Entity hero, float time, object holder)
        {
            table[hero] = time;
            var gem = OwnState.GemIn(holder);
            if (ControlledMergeMod.Live != null && gem != null) ByGem.GetOrCreateValue(table)[gem] = time;
        }

        public static void AddFor(Dictionary<Entity, float> table, Entity hero, float time, object holder)
        {
            table.Add(hero, time);
            var gem = OwnState.GemIn(holder);
            if (ControlledMergeMod.Live != null && gem != null) ByGem.GetOrCreateValue(table)[gem] = time;
        }
    }

    // 5.
    [HarmonyPatch]
    internal static class SharedConsumption
    {
        private static readonly MethodInfo Destroy = AccessTools.Method(typeof(Actor), nameof(Actor.Destroy), Type.EmptyTypes);
        private static readonly MethodInfo Stacks = AccessTools.Method(typeof(EntityStatus), nameof(EntityStatus.GetElementalStack));

        // What is to go at the end of the frame, and which essences have already consumed it.
        private static readonly Dictionary<Actor, List<Gem>> Pending = new Dictionary<Actor, List<Gem>>();

        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(Gem_R_Purity), "OnCastComplete");
            foreach (var method in OwnState.WithNested(typeof(Gem_E_Twilight)).SelectMany(OwnState.Declared))
                if (!method.IsStatic && method.Name == "MoveNext" && OwnState.Calls(method, o => Equals(o, Stacks)))
                    yield return method;
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
            OwnState.Redirect(instructions, o =>
                Equals(o, Destroy) ? AccessTools.Method(typeof(SharedConsumption), nameof(Consume)) :
                Equals(o, Stacks) ? AccessTools.Method(typeof(SharedConsumption), nameof(StacksFor)) : null);

        // Twilight's count of Darkness: none for an essence that has consumed it already.
        public static int StacksFor(EntityStatus status, ElementalType type, object holder)
        {
            var gem = OwnState.GemIn(holder);
            if (ControlledMergeMod.Live != null && gem != null && status != null)
            {
                foreach (var pair in Pending)
                    if (pair.Key is ElementalStatusEffect e && !e.IsNullOrInactive() && e.victim == status.entity &&
                        pair.Value.Contains(gem))
                        return 0;
            }
            return status.GetElementalStack(type);
        }

        public static void Consume(Actor effect, object holder)
        {
            var gem = OwnState.GemIn(holder);
            if (ControlledMergeMod.Live == null || gem == null)
            {
                effect.Destroy();
                return;
            }
            if (!Pending.TryGetValue(effect, out var by)) Pending[effect] = by = new List<Gem>();
            if (!by.Contains(gem)) by.Add(gem);
        }

        public static void Flush()
        {
            if (Pending.Count == 0) return;
            var batch = Pending.Keys.ToArray();
            Pending.Clear();
            foreach (var effect in batch)
            {
                try
                {
                    if (!effect.IsNullOrInactive()) effect.Destroy();
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                }
            }
        }
    }

    // 6. The copy that turns the memory: the first by socket among those in it.
    [HarmonyPatch(typeof(Gem_L_ChaosApple), "ClientEventOnRoomLoaded")]
    internal static class ChaosAppleTurnsOnce
    {
        private static bool Prefix(Gem_L_ChaosApple __instance)
        {
            if (ControlledMergeMod.Live == null) return true;
            return ReferenceEquals(ChaosApples.First(__instance), __instance);
        }
    }

    // And it turns it with the casts per room of every copy there, each as its tooltip says.
    [HarmonyPatch(typeof(Gem_L_ChaosApple), "ApplyChaosToSkill")]
    internal static class ChaosApples
    {
        private static readonly MethodInfo GetValue = AccessTools.Method(typeof(Gem), nameof(Gem.GetValue), new[] { typeof(ScalingValue) });

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            foreach (var instruction in instructions)
            {
                if (Equals(instruction.operand, GetValue))
                {
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(ChaosApples), nameof(CastsPerRoom))).WithLabels(instruction.ExtractLabels());
                    continue;
                }
                yield return instruction;
            }
        }

        public static float CastsPerRoom(Gem gem, ScalingValue value)
        {
            if (ControlledMergeMod.Live == null) return gem.GetValue(value);
            int total = 0;
            foreach (var copy in InSameMemory(gem)) total += Mathf.RoundToInt(copy.GetValue(value));
            return total;
        }

        public static Gem First(Gem gem)
        {
            Gem first = gem;
            foreach (var copy in InSameMemory(gem))
                if (copy.location.index < first.location.index) first = copy;
            return first;
        }

        private static IEnumerable<Gem> InSameMemory(Gem gem)
        {
            var hero = gem.owner;
            if (hero == null || hero.Skill == null)
            {
                yield return gem;
                yield break;
            }
            foreach (var pair in hero.Skill.gems)
                if (pair.Value != null && pair.Value.GetType() == gem.GetType() && pair.Key.skill == gem.location.skill)
                    yield return pair.Value;
        }
    }
}
