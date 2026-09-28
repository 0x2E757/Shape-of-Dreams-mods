using System.Linq;
using UnityEngine;

namespace DevTools
{
    // Spawning and equipping, shared by the picker and the API. Everything here is server-side,
    // which solo always is.
    internal static class Cheats
    {
        // Where the game's own shrines put a reward: a couple of metres off, on the navmesh.
        private const float DropSpread = 2f;

        public static Vector3 DropPosition(Hero hero) => Dew.GetGoodRewardPosition(hero.agentPosition, DropSpread);

        // A memory at a level, on the ground or straight into a slot. Equipping goes through
        // EquipSkill with ignoreCanReplace, because a test memory is often one the ordinary path
        // refuses - another hero's own, or something for the Identity slot. Whatever was in the
        // slot is dropped at the hero's feet by EquipSkill, or destroyed when asked.
        public static SkillTrigger SpawnMemory(string typeName, int level, HeroSkillLocation? slot,
                                               bool destroyReplaced = false, Vector3? at = null)
        {
            GameAccess.RequireServer();
            var hero = GameAccess.RequireLiveHero();
            var entry = Catalog.Require(typeName);
            if (entry.kind != CatalogKind.Memory) throw new DevException(typeName + " is an essence, not a memory");

            var template = DewResources.GetByType<SkillTrigger>(entry.type);
            if (template == null) throw new DevException("no asset for " + typeName);

            level = Mathf.Max(1, level);
            var position = slot.HasValue ? hero.agentPosition : at ?? DropPosition(hero);
            var skill = Dew.CreateSkillTrigger(template, position, level, GameAccess.Player, null);

            if (slot.HasValue)
            {
                hero.Skill.TryGetSkill(slot.Value, out var old);
                hero.Skill.EquipSkill(slot.Value, skill, ignoreCanReplace: true);
                if (destroyReplaced && old != null && old != skill) old.Destroy();
            }
            return skill;
        }

        // An essence at a quality, on the ground or into a socket. With no index, the first empty
        // socket of the slot, or the last one when all are full. The game will not have one hero
        // wear two essences of a type, so one already worn elsewhere is taken off first - asked
        // through TryGetEquippedGemOfSameType, the game's own check, so that a mod lifting that
        // rule (ControlledMerge) keeps the one worn elsewhere.
        public static Gem SpawnEssence(string typeName, int quality, HeroSkillLocation? slot, int? index = null,
                                       bool destroyReplaced = false, Vector3? at = null)
        {
            GameAccess.RequireServer();
            var hero = GameAccess.RequireLiveHero();
            var entry = Catalog.Require(typeName);
            if (entry.kind != CatalogKind.Essence) throw new DevException(typeName + " is a memory, not an essence");

            var template = DewResources.GetByType<Gem>(entry.type);
            if (template == null) throw new DevException("no asset for " + typeName);

            quality = Mathf.Max(1, quality);
            GemLocation location = default;
            if (slot.HasValue)
            {
                var skills = hero.Skill;
                int max = skills.GetMaxGemCount(slot.Value);
                if (max <= 0) throw new DevException(slot.Value + " has no essence sockets");

                int socket = index ?? skills.GetEmptyGemSlot(slot.Value);
                if (socket < 0) socket = max - 1;
                if (socket >= max) throw new DevException(slot.Value + " has " + max + " sockets");
                location = new GemLocation(slot.Value, socket);

                bool oneOfEach = skills.TryGetEquippedGemOfSameType(entry.type, out _, out _);
                foreach (var pair in skills.gems.ToList())
                {
                    if (pair.Value == null) continue;
                    if (!pair.Key.Equals(location) && !(oneOfEach && pair.Value.GetType() == entry.type)) continue;
                    var removed = skills.UnequipGem(pair.Key, hero.agentPosition);
                    if (destroyReplaced && removed != null) removed.Destroy();
                }
            }

            var position = slot.HasValue ? hero.agentPosition : at ?? DropPosition(hero);
            var gem = Dew.CreateGem(template, position, quality, GameAccess.Player, null);
            if (slot.HasValue) hero.Skill.EquipGem(location, gem);
            return gem;
        }

        public static string Describe(SkillTrigger skill) =>
            skill.GetType().Name + " +" + (skill.level - 1);

        public static string Describe(Gem gem) =>
            gem.GetType().Name + " " + gem.quality + "%";
    }
}
