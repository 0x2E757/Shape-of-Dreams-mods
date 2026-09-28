#if DEBUG
using System;
using System.Linq;
using Mirror;
using UnityEngine;

namespace DevTools
{
    // The game's catalogue, and the cheats that put it in the hero's hands. The same Catalog and
    // Cheats the picker uses, so the two cannot disagree about what exists or how it is given.
    internal static class ContentApi
    {
        [Route("GET", "/content/essences", "Every essence in the game: type, localized name, rarity, short description.",
               "query?, rarity? (Common Rare Epic Legendary Unique), hidden=false (include ones kept out of the loot pool), limit=1000")]
        private static object Essences(Args a) => List(CatalogKind.Essence, a);

        [Route("GET", "/content/memories", "Every memory in the game: type, localized name, rarity, short description. hidden=true adds monster skills and heroes' own kits.",
               "query?, rarity? (Common Rare Epic Legendary Character Identity), hidden=false, limit=1000")]
        private static object Memories(Args a) => List(CatalogKind.Memory, a);

        private static object List(CatalogKind kind, Args a)
        {
            if (!Catalog.Ready) throw new DevException("the content database is not loaded yet");
            Catalog.LoadAll(kind);

            string query = a.Str("query", null);
            string rarity = a.Str("rarity", null);
            bool hidden = a.Bool("hidden");
            var items = Catalog.Of(kind)
                .Where(e => (hidden || !e.IsHidden) && e.Matches(query))
                .Where(e => rarity == null || e.Rarity.ToString().Equals(rarity, StringComparison.OrdinalIgnoreCase))
                .Take(a.Int("limit", 1000))
                .Select(e => new { type = e.typeName, name = e.Name, rarity = e.Rarity.ToString(), hidden = e.IsHidden, description = e.Description })
                .ToList();
            return new { count = items.Count, items };
        }

        [Route("GET", "/content/heroes", "Every hero: type, name, and whether this profile can play it.")]
        private static object Heroes(Args a) =>
            Dew.allHeroes.Where(t => Dew.IsHeroIncludedInGame(t.Name)).Select(t => new
            {
                type = t.Name,
                name = Describe.Loc(t.Name + "_Name", t.Name),
                available = DewSave.profileMain != null && DewSave.profileMain.heroes.TryGetValue(t.Name, out var u) && u.isAvailableInGame,
            });

        // ----- cheats -----------------------------------------------------------------------

        [Route("POST", "/cheat/spawn", "Create a memory or essence by type: on the ground (at x,z or beside the hero), or straight into a slot. Host only.",
               "type, level=1 (memories) | quality=100 (essences), slot?, index? (essence socket), x,z?, destroy_replaced=false")]
        private static object Spawn(Args a)
        {
            var entry = Catalog.Require(a.Str("type"));
            HeroSkillLocation? slot = a.Has("slot") ? GameAccess.ParseSlot(a.Str("slot")) : (HeroSkillLocation?)null;
            bool destroy = a.Bool("destroy_replaced");
            var at = a.Point();

            if (entry.kind == CatalogKind.Memory)
            {
                var skill = Cheats.SpawnMemory(entry.typeName, a.Int("level", 1), slot, destroy, at);
                return new { spawned = Cheats.Describe(skill), id = skill.netId, slot = slot?.ToString() };
            }
            var gem = Cheats.SpawnEssence(entry.typeName, a.Int("quality", 100), slot, a.Has("index") ? a.Int("index") : (int?)null, destroy, at);
            return new { spawned = Cheats.Describe(gem), id = gem.netId, slot = slot?.ToString() };
        }

        [Route("POST", "/cheat/level", "Set the hero's level directly. Skips what a real level-up hands out.", "level")]
        private static object Level(Args a)
        {
            GameAccess.RequireServer();
            var hero = GameAccess.RequireHero();
            int level = Mathf.Clamp(a.Int("level"), 1, Mathf.Max(1, hero.maxLevel));
            hero.Status.level = level;
            return new { level };
        }

        [Route("POST", "/cheat/currency", "Add (or with a negative amount take) gold and dream dust.", "gold=0, dust=0")]
        private static object Currency(Args a)
        {
            GameAccess.RequireServer();
            var player = GameAccess.Player ?? throw new DevException("no local player");
            int gold = a.Int("gold", 0), dust = a.Int("dust", 0);
            if (gold != 0) player.AddGold(gold);
            if (dust != 0) player.AddDreamDust(dust);
            return new { gold = player.gold, dreamDust = player.dreamDust };
        }

        [Route("POST", "/cheat/heal", "Fill the hero's health.")]
        private static object Heal(Args a)
        {
            GameAccess.RequireServer();
            var hero = GameAccess.RequireHero();
            hero.Status.SetHealth(hero.maxHealth);
            return new { hp = hero.currentHealth };
        }

        [Route("POST", "/cheat/kill", "Kill an entity by id, or every enemy in the room, or the hero itself (knocks it out).", "id | enemies=true | hero=true")]
        private static object Kill(Args a)
        {
            GameAccess.RequireServer();
            if (a.Bool("hero"))
            {
                GameAccess.RequireHero().Kill();
                return new { killed = "hero" };
            }
            if (a.Bool("enemies"))
            {
                var hero = GameAccess.RequireHero();
                var enemies = Describe.Actors<Entity>().Where(e => e.isAlive && e != hero && hero.GetRelation(e) == EntityRelation.Enemy).ToList();
                foreach (var e in enemies) e.Kill();
                return new { killed = enemies.Count };
            }
            var target = GameAccess.RequireActor(a.Id("id")) as Entity ?? throw new DevException("that is not an entity");
            target.Kill();
            return new { killed = target.GetType().Name };
        }

        [Route("POST", "/cheat/teleport", "Put the hero at a point.", "x, z")]
        private static object Teleport(Args a)
        {
            GameAccess.RequireServer();
            var hero = GameAccess.RequireHero();
            var point = a.Point() ?? throw new DevException("missing x and z");
            hero.Control.Teleport(Dew.GetValidAgentDestination_Closest(hero.agentPosition, point));
            return new { position = Describe.Vec(hero.position) };
        }

        // The server's travel command, sent directly: no walk to the exit, no world map, none of
        // the questions the map asks. The server still refuses a node that is not adjacent, and a
        // move while another transition is under way.
        [Route("POST", "/cheat/travel", "Travel to an adjacent node from anywhere, without walking to the exit or opening the map - even with the room not cleared.", "node")]
        private static object Travel(Args a)
        {
            var zone = NetworkedManagerBase<ZoneManager>.softInstance ?? throw new DevException("not in a run");
            int node = a.Int("node");
            if (node < 0 || node >= zone.nodes.Count) throw new DevException("no node " + node);
            zone.CmdTravelToNode(node);
            return new { travelling = node, type = zone.nodes[node].type.ToString() };
        }

        [Route("POST", "/cheat/next_zone", "On to the next zone from a boss exit node, without using the rift.")]
        private static object NextZone(Args a)
        {
            var zone = NetworkedManagerBase<ZoneManager>.softInstance ?? throw new DevException("not in a run");
            zone.CmdTravelToNextZone();
            return new { travelling = "next zone" };
        }

        [Route("POST", "/cheat/sockets", "Set how many essence sockets a slot has (Identity and Movement normally have none). MoreGemSlots, if loaded, may write its own count back for Q W E R.",
               "slot, count")]
        private static object Sockets(Args a)
        {
            GameAccess.RequireServer();
            var hero = GameAccess.RequireLiveHero();
            var slot = GameAccess.ParseSlot(a.Str("slot"));
            int count = Mathf.Clamp(a.Int("count"), 0, 16);

            // An essence left in a socket that goes away is one nobody can reach, so it is dropped.
            for (int index = count; index < hero.Skill.GetMaxGemCount(slot); index++)
            {
                var location = new GemLocation(slot, index);
                if (hero.Skill.gems.ContainsKey(location)) hero.Skill.UnequipGem(location, hero.agentPosition);
            }
            hero.Skill.SetMaxGemCount(slot, count);
            return new { slot = slot.ToString(), sockets = hero.Skill.GetMaxGemCount(slot) };
        }
    }
}
#endif
