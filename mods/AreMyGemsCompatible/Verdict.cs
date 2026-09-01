using System.Collections.Generic;

namespace AreMyGemsCompatible
{
    // The answer for one essence in one memory. There are deliberately only two: an essence that
    // still does *something* is not worth a word, because the interesting cases are drowned by a
    // warning that appears on half the loadout.
    internal enum Compatibility
    {
        Fine,
        Dead,
    }

    internal static class Verdict
    {
        // The memory an essence is socketed into, or null while it is still on the ground.
        public static Compatibility For(Gem gem, SkillTrigger skill)
        {
            if (gem == null || skill == null) return Compatibility.Fine;

            var profile = GemTriggers.Of(gem);

            // Nothing about this essence is waiting on the memory, or something about it is not.
            if (profile.Needs == SlotNeed.None || profile.AlwaysLive) return Compatibility.Fine;

            var facts = MemoryData.Get(skill);

            // A memory the shipped data does not describe - one from a later patch, or from
            // another mod - is unknown, not inert.
            if (!facts.IsKnown) return Compatibility.Fine;

            var supplied = Supplied(facts);

            // Needs is a union, not a checklist: Gem_R_Ricochet fires on damage *or* healing, and
            // a memory doing either keeps it alive.
            if ((profile.Needs & supplied) != SlotNeed.None) return Compatibility.Fine;

            // The one way a memory does more than its own description says. An essence that fires
            // on every cast and creates something with the cast's own AbilityInstance as the
            // source - Gem_C_Sharp is the plain case - has that something parented under the
            // memory, and Actor.InvokeOnDealDamage walks up parentActor from there. So the
            // *memory* registers as having dealt the damage. Put a damage-on-cast essence into a
            // memory that deals none, and the damage-triggered essence beside it works.
            if ((profile.Needs & SuppliedBySiblings(gem, skill)) != SlotNeed.None) return Compatibility.Fine;

            return Compatibility.Dead;
        }

        private static SlotNeed Supplied(MemoryFacts facts)
        {
            var supplied = SlotNeed.None;
            if (facts.DealsDamage) supplied |= SlotNeed.Damage;
            if (facts.Heals) supplied |= SlotNeed.Heal;
            if (facts.Shields) supplied |= SlotNeed.Shield;
            return supplied;
        }

        // Read off the memory's owner rather than the essence's, so that the answer is the same
        // whether the essence is already socketed or is being dragged over the slot - in which
        // case it has no owner at all yet.
        private static SlotNeed SuppliedBySiblings(Gem gem, SkillTrigger skill)
        {
            var owner = skill.owner;
            if (owner == null || owner.Skill == null) return SlotNeed.None;

            var gems = owner.Skill.gems;
            if (gems == null) return SlotNeed.None;

            var supplied = SlotNeed.None;
            foreach (var pair in gems)
            {
                var other = pair.Value;
                if (other == null || other == gem) continue;
                if (other.skill != skill) continue;

                var sibling = GemTriggers.Of(other);

                // It has to fire whatever the memory does: an essence itself waiting on the
                // memory cannot lift anything out of a memory that never starts it.
                if (!sibling.AlwaysLive) continue;

                // Supplies is read out of what the sibling creates and what those things then do,
                // never out of what it says about itself. Essence descriptions cannot answer this
                // question: they describe the essence entire, damage *taken* and stat bonuses and
                // amplifications included. Gem_E_Protection's says "reducing damage taken" and it
                // deals none; Gem_R_Insatiable's says "Attack Damage is increased" and that is a
                // stat; Gem_E_Overload's promises damage and healing and only amplifies both.
                supplied |= sibling.Supplies;
            }
            return supplied;
        }
    }
}
