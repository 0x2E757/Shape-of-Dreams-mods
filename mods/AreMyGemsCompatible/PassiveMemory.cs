using System;
using System.Collections.Generic;

namespace AreMyGemsCompatible
{
    // What an identity memory does, read out of its code rather than its description.
    //
    // A Corrupted Chaos shrine can add an essence slot to any memory the hero holds, identity
    // included, and an identity memory is unlike every other in two ways that both decide verdicts.
    //
    // **It is never cast.** Its configs are passive - TriggerConfig.isActive is false, and
    // AbilityTrigger.OnCastStart throws for such a config - so neither cast event is ever raised on
    // it, and an essence that fires on the cast never fires. That is SlotNeed.Cast, and it is read
    // off the live memory's configs rather than off where it sits, so an identity memory that did
    // have something to cast would be treated as the ordinary memory it then is.
    //
    // **Its description does not say what it does.** A passive describes what it changes about the
    // hero, and prose cannot tell the hero's damage from the memory's. St_D_DoubleTap "increases
    // Attack Damage" and makes the next basic attack fire twice - but it is the hero's rifle that
    // fires, parented under the hero's attack, and the identity itself deals nothing.
    // St_D_ConvergencePoint chains the hero's own attack on to more targets; St_D_ScarOfTheWind
    // swaps the attack for one made with Dew.CreateAbilityTrigger, which has no parent at all. The
    // prose regexes read all three as dealing damage, which would be wrong in the quiet direction
    // for every damage-triggered essence placed in them.
    //
    // Unlike a Q/W/E/R memory, a passive one *does* have code to read. Its behaviour is the
    // SkillTrigger and the status effect its config applies, which AbilityTrigger creates with the
    // memory as its parent - so whatever that effect, and whatever it creates, deals or heals or
    // shields is the memory's doing, and its events fire. That is exactly the question
    // GemTriggers.ReadCapabilities already answers for what an essence supplies, and it is asked
    // the same way.
    //
    // What the code reading has to be trusted with was audited against all seventeen identities
    // the game ships; see docs/aremygemscompatible.md. The dump is still required, as it is for
    // every other memory: a memory that is in no shipped data has not been audited, and nothing is
    // said about it.
    internal static class PassiveMemory
    {
        private static readonly Dictionary<Type, MemoryFacts> Cache = new Dictionary<Type, MemoryFacts>();

        // Whether the memory has anything that can be cast. A memory with no configs at all is
        // not something to reason about, and is treated as the ordinary kind.
        public static bool IsPassive(SkillTrigger skill)
        {
            var configs = skill.configs;
            if (configs == null || configs.Length == 0) return false;

            foreach (var config in configs)
                if (config != null && config.isActive) return false;
            return true;
        }

        public static MemoryFacts Read(SkillTrigger skill)
        {
            var type = skill.GetType();
            MemoryFacts cached;
            if (Cache.TryGetValue(type, out cached)) return cached;

            var facts = Build(skill);
            Cache[type] = facts;
            return facts;
        }

        public static void Reset()
        {
            Cache.Clear();
        }

        private static MemoryFacts Build(SkillTrigger skill)
        {
            // The memory's own type - St_D_CircleOfLife heals from the trigger itself - and the
            // passive effect each config applies, which is prefab data and so is read off the live
            // memory. St_D_DoubleTap declares nothing at all; everything it does is Se_D_DoubleTap.
            var roots = new List<Type> { skill.GetType() };
            foreach (var config in skill.configs)
            {
                if (config == null || config.appliedStatusEffect == null) continue;
                roots.Add(config.appliedStatusEffect.GetType());
            }

            bool complete;
            var does = GemTriggers.ReadCapabilities(roots, out complete);

            // A body that would not read might have held the one DealDamage that mattered, and
            // an answer of "nothing" built on it is the loud kind of wrong.
            if (!complete) return default(MemoryFacts);

            return new MemoryFacts
            {
                IsKnown = true,
                IsCast = false,
                DealsDamage = (does & SlotNeed.Damage) != SlotNeed.None,
                Heals = (does & SlotNeed.Heal) != SlotNeed.None,
                Shields = (does & SlotNeed.Shield) != SlotNeed.None,
            };
        }
    }
}
