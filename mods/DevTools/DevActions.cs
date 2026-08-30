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

        // ----- ending the run -------------------------------------------------------

        // A hero is knocked out rather than removed, and GameManager.CheckGameOver concludes the
        // run once every hero in ActorManager.allHeroes is knocked out - so in a solo run this
        // reaches the result screen a moment later, and in co-op it does nothing until the others
        // are down too. That is the game's rule, not something this works around.
        public static string KillHero()
        {
            if (!CanAct(out string reason)) return reason;

            LocalHero.Kill();
            return "knocked out - the result screen follows once every hero is down";
        }
    }
}
