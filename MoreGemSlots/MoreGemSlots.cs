using System;
using System.Collections.Generic;
using Mirror;
using UnityEngine;

namespace MoreGemSlots
{
    // Every public field gets a config widget built for it automatically, so this is the whole
    // settings UI. Only the thresholds are exposed: everything else about the mod is either
    // derived from them or was settled by measurement and has no business being a knob.
    public class MoreGemSlotsConfig : ModConfig
    {
        // Hero level at which every memory gains a slot.
        [Range(1, 40)] public int heroLevelForFirstSlot = 10;
        [Range(1, 40)] public int heroLevelForSecondSlot = 20;

        // How far a memory has to be upgraded to gain a slot of its own, counted the way the game
        // shows it: the +5 on a memory, not its level, which is one higher.
        [Range(1, 20)] public int memoryUpgradesForFirstSlot = 5;
        [Range(1, 20)] public int memoryUpgradesForSecondSlot = 10;

        private const float LabelWidth = 260f;
        private const float InputWidth = 120f;

        // Each setting is a horizontal row of label then input, and the game sizes both to their
        // contents. That staggers the rows twice over: field names of differing length start
        // their inputs at differing x, and values of differing length make the boxes themselves
        // differing widths - a one-digit threshold gets a visibly narrower box than a two-digit
        // one. Pinning both widths lines the whole column up.
        public override void BuildWidgets(Transform parent, out SafeAction onChanged,
                                          out SafeAction requestUpdate)
        {
            int firstOwnRow = parent.childCount;
            base.BuildWidgets(parent, out onChanged, out requestUpdate);

            // The game labels each row with Dew.NicifyVariableName(field.Name), so rows are found
            // by that text rather than by position - which survives fields being reordered, and
            // headers or spacers appearing between them.
            var translated = new Dictionary<string, string>
            {
                [Dew.NicifyVariableName(nameof(heroLevelForFirstSlot))] = Localization.Get(Localization.HeroFirst),
                [Dew.NicifyVariableName(nameof(heroLevelForSecondSlot))] = Localization.Get(Localization.HeroSecond),
                [Dew.NicifyVariableName(nameof(memoryUpgradesForFirstSlot))] = Localization.Get(Localization.MemoryFirst),
                [Dew.NicifyVariableName(nameof(memoryUpgradesForSecondSlot))] = Localization.Get(Localization.MemorySecond),
            };

            Shared.SettingsRows.Polish(parent, firstOwnRow, LabelWidth, InputWidth, translated);
        }
    }

    // Essence slots are earned rather than fixed:
    //
    //   base                                        3
    //   hero level >= heroLevelForFirstSlot        +1
    //   hero level >= heroLevelForSecondSlot       +1
    //   memory +N >= memoryUpgradesForFirstSlot    +1
    //   memory +N >= memoryUpgradesForSecondSlot   +1
    //                                          max  7
    //
    // Seven is also exactly what the extended layout can draw, which is why the two numbers line
    // up. maxGemCount is a Mirror SyncVar, so only the server decides and clients follow.
    public class MoreGemSlots : ModBehaviour
    {
        public MoreGemSlotsConfig config = new MoreGemSlotsConfig();

        private static readonly Shared.ConfigFieldWidgets Widgets =
            new Shared.ConfigFieldWidgets(typeof(MoreGemSlotsConfig));

        private const int BaseSlots = 3;
        private const int MinSlots = 1;

        // Identity and Movement have no essence slots in the base game and are left that way.
        private static readonly HeroSkillLocation[] EarningSlots =
        {
            HeroSkillLocation.Q,
            HeroSkillLocation.W,
            HeroSkillLocation.E,
            HeroSkillLocation.R,
        };

        private void Awake()
        {
            LoadConfigsToDisk();
            harmony.PatchAll();
            Widgets.Install();
            Debug.Log("[MoreGemSlots] loaded: " + mod.metadata.id);
        }

        private void OnDestroy()
        {
            // Pass the id. The template's bare UnpatchAll() removes every patch from every mod in
            // the game, not just this one.
            harmony.UnpatchAll(harmony.Id);
            GemLayoutPatch.Reset();

            // DewGUI.fieldBuilders is shared with the game and every other mod, so the entry has
            // to come back out.
            Widgets.Remove();
            Debug.Log("[MoreGemSlots] unloaded: " + mod.metadata.id);
        }

        private void Update()
        {
            // maxGemCount is a SyncVar: only the server may write it, and clients receive the
            // result, so there is nothing for a remote client to do here.
            if (!NetworkServer.active) return;

            foreach (var player in DewPlayer.gamePlayers) Apply(player);
        }

        private void Apply(DewPlayer player)
        {
            if (player == null) return;

            var hero = player.hero;
            if (EntityCheck.IsNullInactiveDeadOrKnockedOut(hero)) return;

            var skill = hero.Skill;
            if (skill == null) return;

            foreach (var slot in EarningSlots)
            {
                int live = skill.GetMaxGemCount(slot);
                int earned = EarnedSlots(hero, skill, slot);

                // Writing an unchanged value every frame would dirty the SyncVar for nothing.
                if (live == earned) continue;

                // Losing slots strands whatever was in the ones going away, so they are dealt
                // with before the count actually changes.
                if (earned < live) RehomeGems(hero, skill, slot, earned, live);

                skill.SetMaxGemCount(slot, earned);
            }
        }

        private int EarnedSlots(Hero hero, HeroSkill skill, HeroSkillLocation slot)
        {
            int total = BaseSlots;

            int heroLevel = hero.level;
            if (heroLevel >= config.heroLevelForFirstSlot) total++;
            if (heroLevel >= config.heroLevelForSecondSlot) total++;

            var memory = skill.GetSkill(slot);
            if (!ActorCheck.IsNullOrInactive(memory))
            {
                // A fresh memory is level 1 and shows no +, so the number on it is one less than
                // its level. The thresholds are that number - what the player can actually read
                // off the memory - rather than the level behind it.
                int upgrades = memory.level - 1;
                if (upgrades >= config.memoryUpgradesForFirstSlot) total++;
                if (upgrades >= config.memoryUpgradesForSecondSlot) total++;
            }

            return Mathf.Clamp(total, MinSlots, GemLayoutPatch.MaxSupportedSlots);
        }

        // Essences in slots that are about to disappear move to the nearest free slot on the same
        // memory, counting inwards from the edge so they travel as little as possible. If nothing
        // is free they drop at the hero's feet rather than being stranded in a slot nobody can
        // reach.
        private static void RehomeGems(Hero hero, HeroSkill skill, HeroSkillLocation slot,
                                       int newMax, int oldMax)
        {
            for (int index = newMax; index < oldMax; index++)
            {
                var from = new GemLocation(slot, index);
                if (!skill.TryGetGem(from, out var gem) || gem == null) continue;

                int target = NearestFreeSlot(skill, slot, newMax);

                // Unequip first either way. It is how the game's own slot swap moves a gem, and
                // it puts the essence into the world, so a failed re-equip leaves it on the floor
                // rather than losing it.
                skill.UnequipGem(from, hero.agentPosition);

                if (target < 0)
                {
                    Debug.Log($"[MoreGemSlots] {slot} slot {index} lost with nowhere free, " +
                              $"dropped {gem.GetType().Name}");
                    continue;
                }

                try
                {
                    skill.EquipGem(new GemLocation(slot, target), gem);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[MoreGemSlots] could not move {gem.GetType().Name} to " +
                                     $"{slot} slot {target}, left on the ground: {e.Message}");
                }
            }
        }

        private static int NearestFreeSlot(HeroSkill skill, HeroSkillLocation slot, int newMax)
        {
            for (int i = newMax - 1; i >= 0; i--)
            {
                if (!skill.TryGetGem(new GemLocation(slot, i), out var existing) || existing == null)
                    return i;
            }
            return -1;
        }
    }
}
