using Mirror;
using UnityEngine;

namespace DevTools
{
    // Everything the panel does to the game, with no UI in it. Each action returns the line the
    // panel puts on screen, so that what happened is reported the same way whether it worked or
    // not, and so that this file can be read without reading the other one.
    //
    // All four are server-only, and not by convention: EntityStatus.level throws outright off the
    // server ("Only server can change entity's level"), and spawning or killing touches network
    // objects. A guest gets the buttons disabled rather than an exception.
    internal static class DevActions
    {
        // The loot pool spawns things a couple of metres away, which is where the game's own
        // shrines put a reward.
        private const float SpawnSpread = 2f;

        public static DewPlayer LocalPlayer => DewPlayer.local;

        public static Hero LocalHero
        {
            get
            {
                var player = DewPlayer.local;
                return player != null ? player.hero : null;
            }
        }

        // Whether the actions can run at all, and why not when they cannot. The panel shows this
        // rather than letting a click fail.
        public static bool CanAct(out string reason)
        {
            if (!NetworkServer.active)
            {
                reason = "host only - the server owns levels and spawning";
                return false;
            }

            if (EntityCheck.IsNullInactiveDeadOrKnockedOut(LocalHero))
            {
                reason = "no live hero";
                return false;
            }

            reason = null;
            return true;
        }

        // ----- hero level -----------------------------------------------------------

        public static int HeroLevel
        {
            get
            {
                var hero = LocalHero;
                return hero != null ? hero.level : 0;
            }
        }

        public static int HeroMaxLevel
        {
            get
            {
                var hero = LocalHero;
                return hero != null ? Mathf.Max(1, hero.maxLevel) : 1;
            }
        }

        // Writing the level directly is what the game's own debug command did. It skips
        // everything a real level-up hands out - the choices, the stat gains that come with them -
        // so a hero levelled this way is not the same as one that earned it. For looking at how
        // a UI behaves at level 20 that is exactly what is wanted; for judging balance it is not.
        public static string SetHeroLevel(int level)
        {
            if (!CanAct(out string reason)) return reason;

            var hero = LocalHero;
            level = Mathf.Clamp(level, 1, HeroMaxLevel);
            if (hero.level == level) return "level already " + level;

            hero.Status.level = level;
            return "hero level -> " + level;
        }

        // ----- spawning -------------------------------------------------------------

        // The template comes from the real loot pool, so what appears is something the game could
        // actually have dropped, but the level is ours rather than the one the pool rolled - the
        // point of the panel is to ask for a level, not to be told one.
        public static string SpawnMemory(int level)
        {
            if (!CanAct(out string reason)) return reason;

            var loot = LootManager.softInstance;
            if (loot == null) return "no loot manager";

            // A null rarity is not a missing argument: LootManager rolls one itself when it is
            // not given one, which is the fully random draw this wants.
            loot.SelectSkillAndLevel(null, out SkillTrigger template, out _);
            if (template == null) return "loot pool gave no memory";

            Dew.CreateSkillTrigger(template, SpawnPosition(), Mathf.Max(1, level), LocalPlayer, null);
            return $"memory {template.GetType().Name} +{Mathf.Max(1, level) - 1}";
        }

        public static string SpawnEssence(int quality)
        {
            if (!CanAct(out string reason)) return reason;

            var loot = LootManager.softInstance;
            if (loot == null) return "no loot manager";

            loot.SelectGemAndQuality(null, out Gem template, out _);
            if (template == null) return "loot pool gave no essence";

            Dew.CreateGem(template, SpawnPosition(), Mathf.Max(1, quality), LocalPlayer, null);
            return $"essence {template.GetType().Name} q{Mathf.Max(1, quality)}";
        }

        private static Vector3 SpawnPosition()
        {
            return Dew.GetGoodRewardPosition(LocalHero.agentPosition, SpawnSpread);
        }

        // ----- a node's saved room -------------------------------------------------

        // Forget what a node's room looked like, so that walking back into it builds the room
        // afresh instead of restoring a remembered one.
        //
        // This is a repair tool, and it exists because a save can end up holding *another* room's
        // state under a node. `ZoneManager.LoadNode` saves the room being left into
        // `visitedNodesSaveData[s.from]`, with `s.from` taken from `currentNodeIndex` - so
        // anything that moves that index while the party is standing somewhere else files the
        // room under the wrong node. MapAutoRoute's route replay did exactly that until it
        // learned to put the index back (see Walk.cs). The damage is silent until the party
        // returns to the mis-filed node: `ApplyRoomDataBeforeSpawnObjects` then looks for actors
        // and `RoomSection`s that this room does not have, the missing sections take the NavMesh
        // with them, and the heroes land somewhere they cannot walk out of.
        //
        // Clearing the slot is the honest repair rather than the clever one. The right contents
        // are not recoverable - they were never written anywhere - so the choice is between a
        // room that rebuilds from scratch and a room that strands the party. A rebuilt room comes
        // back with its first-visit state, which for a cleared room means its rewards are there
        // again; that is a real cost and the reason this is not something the published mods do.
        //
        // Server only, and deliberately not gated on a live hero the way the others are: a save
        // is worth repairing from the map screen, with everyone dead or otherwise.
        public static string ClearRoomSaveData(int node)
        {
            if (!NetworkServer.active) return "host only - the server owns the save data";

            var zone = ZoneManager.softInstance;
            if (zone == null) return "no zone manager";
            if (node < 0 || node >= zone.visitedNodesSaveData.Count) return "no node " + node;

            if (node == zone.currentNodeIndex)
            {
                // The room the party is standing in is written out again the moment they leave,
                // so clearing it now achieves nothing and reads as though it did.
                return "node " + node + " is the one you are in - leave it first";
            }

            if (zone.visitedNodesSaveData[node] == null) return "node " + node + " already has none";

            zone.visitedNodesSaveData[node] = null;
            return "node " + node + " (" + zone.nodes[node].room + ") will be built afresh";
        }

        // ----- ending the run -------------------------------------------------------

        // A hero is knocked out rather than removed, and GameManager.CheckGameOver concludes the
        // run once every hero in ActorManager.allHeroes is knocked out - so in a solo run this
        // reaches the result screen a moment later, and in co-op it does nothing until the others
        // are down too. That is the game's rule, not something this works around.
        public static string KillHero()
        {
            if (!CanAct(out string reason)) return reason;

            // Before the kill rather than after: knocking out the last hero can conclude the run
            // in the same frame, and the flag has to be up before anything consumes a result.
            ScorelessRun.Arm();

            LocalHero.Kill();
            return "knocked out, 0 mastery - the result screen follows once every hero is down";
        }
    }
}
