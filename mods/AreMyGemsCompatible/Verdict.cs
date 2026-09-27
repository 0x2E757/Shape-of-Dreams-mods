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
            ElementSet missing;
            return For(gem, skill, out missing);
        }

        // missing is the element a Dead verdict is about, or None when the verdict is about what
        // the memory does at all - which decides the sentence the tooltip shows.
        public static Compatibility For(Gem gem, SkillTrigger skill, out ElementSet missing)
        {
            missing = ElementSet.None;
            var verdict = ForNeeds(gem, skill);
            if (verdict != Compatibility.Fine || gem == null || skill == null) return verdict;

            var profile = GemTriggers.Of(gem);
            if (profile.Gate == ElementSet.None || profile.AlwaysLive) return Compatibility.Fine;

            // The memory does the thing the essence waits for. For an essence in the element
            // table that is not enough: Essence of Frost wants Cold damage, and a memory that
            // deals only Fire damage leaves it as dead as one that deals none.
            var elements = MemoryElements.For(skill, MemoryData.Get(skill));
            if (!elements.HasValue || (elements.Value & profile.Gate) != ElementSet.None) return Compatibility.Fine;

            // What else could give the memory's damage an element - an essence beside it, a
            // status on the hero, the room - and whether any of that is the one wanted. Frost
            // beside Inversion is fine, Frost beside Sulfur is not. See ElementChangers.
            var added = ElementChangers.AddedFor(gem, skill);
            if (!added.HasValue || (added.Value & profile.Gate) != ElementSet.None) return Compatibility.Fine;

            missing = profile.Gate;
            return Compatibility.Dead;
        }

        // The verdict in words, with what went into it, for DevTools' command server - which
        // calls this by reflection, so that neither mod has to reference the other. The sentence
        // is the tooltip's own, in the game's language, without its markup.
        public static string Describe(Gem gem, SkillTrigger skill)
        {
            if (gem == null || skill == null) return "no essence or no memory";

            ElementSet missing;
            var verdict = For(gem, skill, out missing);
            var profile = GemTriggers.Of(gem);
            var facts = MemoryData.Get(skill);
            var elements = MemoryElements.For(skill, facts);
            var added = profile.Gate != ElementSet.None ? ElementChangers.AddedFor(gem, skill) : ElementSet.None;

            string detail = "needs=" + profile.Needs + (profile.AlwaysLive ? " alwaysLive" : "") +
                            " gate=" + profile.Gate +
                            " memory=" + (facts.IsKnown ? (elements.HasValue ? elements.Value.ToString() : "unknown") : "not in dump") +
                            " added=" + (added.HasValue ? added.Value.ToString() : "unknown");

            if (verdict != Compatibility.Dead) return "fine | " + detail;

            string reason = missing != ElementSet.None
                ? Localization.ForElement(missing)
                : Localization.ForNeeds(profile.Needs);
            return "DEAD: " + System.Text.RegularExpressions.Regex.Replace(reason, "<[^>]+>", "") + " | " + detail;
        }

#if DEBUG
        // Debug builds only: every essence against every memory the dump knows, as prefabs, with
        // nothing beside it - the figures the description and the notes quote. Siblings, statuses
        // and the room are left out, since a pairing is judged here on its own.
        public static string CountPairs()
        {
            var database = DewResources.database;
            if (database == null || database.typeNameToType == null) return "no resource database";

            var gems = new System.Collections.Generic.List<Gem>();
            var memories = new System.Collections.Generic.List<SkillTrigger>();
            foreach (var pair in database.typeNameToType)
            {
                var type = pair.Value;
                if (type == null || type.IsAbstract || !database.typeToGuid.ContainsKey(type)) continue;

                if (typeof(Gem).IsAssignableFrom(type))
                {
                    var gem = DewResources.GetByType(type) as Gem;
                    if (gem != null) gems.Add(gem);
                }
                else if (typeof(SkillTrigger).IsAssignableFrom(type) && MemoryData.Get(pair.Key).IsKnown)
                {
                    var memory = DewResources.GetByType(type) as SkillTrigger;
                    if (memory != null) memories.Add(memory);
                }
            }

            int total = 0, dead = 0, byElement = 0, castTotal = 0, castDead = 0;
            foreach (var memory in memories)
            {
                var facts = MemoryData.Get(memory);
                foreach (var gem in gems)
                {
                    total++;
                    if (facts.IsCast) castTotal++;

                    var profile = GemTriggers.Of(gem);
                    bool isDead = false;
                    if (profile.Needs != SlotNeed.None && !profile.AlwaysLive && facts.IsKnown)
                    {
                        if ((profile.Needs & Supplied(facts)) == SlotNeed.None) isDead = true;
                        else if (profile.Gate != ElementSet.None)
                        {
                            var elements = MemoryElements.For(memory, facts);
                            if (elements.HasValue && (elements.Value & profile.Gate) == ElementSet.None)
                            {
                                isDead = true;
                                byElement++;
                            }
                        }
                    }

                    if (!isDead) continue;
                    dead++;
                    if (facts.IsCast) castDead++;
                }
            }

            return gems.Count + " essences x " + memories.Count + " memories: " + dead + " of " + total +
                   " pairs dead (" + byElement + " of them by element); memories that are cast: " +
                   castDead + " of " + castTotal;
        }
#endif

        private static Compatibility ForNeeds(Gem gem, SkillTrigger skill)
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
            // a memory doing either keeps it alive. Gem_C_Quicksilver fires on the cast *or* on
            // damage, which in an identity memory is a question about damage alone.
            if ((profile.Needs & supplied) != SlotNeed.None) return Compatibility.Fine;

            // The one way a memory does more than its own description says. An essence that fires
            // on every cast and creates something with the cast's own AbilityInstance as the
            // source - Gem_C_Sharp is the plain case - has that something parented under the
            // memory, and Actor.InvokeOnDealDamage walks up parentActor from there. So the
            // *memory* registers as having dealt the damage. Put a damage-on-cast essence into a
            // memory that deals none, and the damage-triggered essence beside it works.
            if ((profile.Needs & SuppliedBySiblings(gem, skill, facts)) != SlotNeed.None) return Compatibility.Fine;

            return Compatibility.Dead;
        }

        private static SlotNeed Supplied(MemoryFacts facts)
        {
            var supplied = SlotNeed.None;
            if (facts.DealsDamage) supplied |= SlotNeed.Damage;
            if (facts.Heals) supplied |= SlotNeed.Heal;
            if (facts.Shields) supplied |= SlotNeed.Shield;
            if (facts.IsCast) supplied |= SlotNeed.Cast;
            return supplied;
        }

        // Read off the memory's owner rather than the essence's, so that the answer is the same
        // whether the essence is already socketed or is being dragged over the slot - in which
        // case it has no owner at all yet.
        private static SlotNeed SuppliedBySiblings(Gem gem, SkillTrigger skill, MemoryFacts facts)
        {
            // Everything a sibling supplies is created through EventInfoCast.instance, and a
            // memory that is never cast never hands out an EventInfoCast. Gem_C_Sharp beside a
            // damage-triggered essence in an identity memory that deals none leaves both dead.
            if (!facts.IsCast) return SlotNeed.None;

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

                // It has to fire on the cast, or be something this mod cannot rule out: an essence
                // itself waiting on damage cannot lift anything out of a memory that never starts
                // it. The memory is cast - that was settled above - so the cast is enough.
                if (!sibling.AlwaysLive && (sibling.Needs & SlotNeed.Cast) == SlotNeed.None) continue;

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
