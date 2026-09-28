#if DEBUG
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace DevTools
{
    // How the semantic routes show game objects: flat, named, with an id to act on. The raw
    // objects are always one /reflect/get away (#id), so these carry what an agent decides with
    // rather than everything there is.
    internal static class Describe
    {
        public static string Loc(string key, string fallback)
        {
            string value;
            try { value = DewLocalization.GetUIValue(key); }
            catch (Exception) { return fallback; }
            return string.IsNullOrEmpty(value) || value.StartsWith("ui.!", StringComparison.Ordinal) ? fallback : GameAccess.Rich(value);
        }

        public static string EntityName(Actor actor) => actor == null ? null : Loc(actor.GetType().Name + "_Name", actor.GetType().Name);

        public static object Vec(Vector3 v) => new { x = Math.Round(v.x, 2), y = Math.Round(v.y, 2), z = Math.Round(v.z, 2) };

        public static object Screen(Vector3 world)
        {
            var p = GameAccess.ToScreen(world);
            if (!p.HasValue) return null;
            var s = p.Value;
            bool on = s.x >= 0 && s.y >= 0 && s.x <= UnityEngine.Screen.width && s.y <= UnityEngine.Screen.height;
            return new { x = Mathf.RoundToInt(s.x), y = Mathf.RoundToInt(s.y), onScreen = on };
        }

        public static float Distance(Actor a, Vector3 from) => a == null ? -1f : (float)Math.Round(Vector3.Distance(a.position, from), 2);

        public static string Kind(Actor actor)
        {
            switch (actor)
            {
                case Hero _: return "hero";
                case Monster _: return "monster";
                case Gem _: return "essence";
                case SkillTrigger _: return "memory";
                case Shrine _: return "shrine";
                case PropEnt_Merchant_Base _: return "merchant";
                case Rift _: return "rift";
                case PickupInstance _: return "pickup";
                case PropEntity _: return "prop";
                case Summon _: return "summon";
                case Entity _: return "entity";
                case IInteractable _: return "interactable";
                default: return "actor";
            }
        }

        public static object Entity(Entity e, Hero viewer)
        {
            var monster = e as Monster;
            string relation = null;
            if (viewer != null && viewer != e)
            {
                try { relation = viewer.GetRelation(e).ToString(); } catch (Exception) { }
            }
            var status = e.Status;
            return new
            {
                id = e.netId,
                type = e.GetType().Name,
                name = EntityName(e),
                kind = Kind(e),
                relation,
                monsterType = monster != null ? monster.type.ToString() : null,
                hunter = monster != null && monster.isHunter ? true : (bool?)null,
                position = Vec(e.position),
                distance = viewer != null ? Distance(e, viewer.position) : (float?)null,
                hp = Math.Round(e.currentHealth, 1),
                maxHp = Math.Round(e.maxHealth, 1),
                shield = status != null ? Math.Round(status.currentShield, 1) : 0,
                alive = e.isAlive,
                level = e.level,
                stunned = status != null && status.hasStun ? true : (bool?)null,
                invulnerable = status != null && status.hasInvulnerable ? true : (bool?)null,
                // What the game's own damage check throws a hit away on (Invulnerable or Protected), and what its own
                // targeting skips. null when not.
                immune = status != null && status.hasDamageImmunity ? true : (bool?)null,
                untargetable = status != null && status.hasUntargetable ? true : (bool?)null,
                // A boss's or miniboss's status effects - its phases are effects (an Eclipse, a PhaseChange, a shield).
                // null for everyone else, to keep the list small.
                effects = monster != null && status != null &&
                          (monster.type == Monster.MonsterType.Boss || monster.type == Monster.MonsterType.MiniBoss)
                    ? status.statusEffects.Where(x => x != null).Select(Status).ToList()
                    : null,
                screen = Screen(e.position),
            };
        }

        public static object Item(Actor actor)
        {
            switch (actor)
            {
                case Gem gem:
                {
                    var entry = Catalog.Find(gem.GetType().Name);
                    return new
                    {
                        type = gem.GetType().Name,
                        name = entry?.Name,
                        rarity = gem.rarity.ToString(),
                        quality = gem.quality,
                        onGround = gem.owner == null && gem.handOwner == null,
                        lockedForMe = gem.isLocked,
                    };
                }
                case SkillTrigger skill:
                {
                    var entry = Catalog.Find(skill.GetType().Name);
                    return new
                    {
                        type = skill.GetType().Name,
                        name = entry?.Name,
                        rarity = skill.rarity.ToString(),
                        level = skill.level,
                        onGround = skill.owner == null && skill.handOwner == null,
                        lockedForMe = skill.isLocked,
                    };
                }
                default:
                    return actor == null ? null : new { type = actor.GetType().Name, name = EntityName(actor), rarity = (string)null };
            }
        }

        public static object Skill(Hero hero, HeroSkillLocation slot)
        {
            hero.Skill.TryGetSkill(slot, out var skill);
            var gems = hero.Skill.gems.Where(p => p.Key.skill == slot).OrderBy(p => p.Key.index)
                           .Select(p => new
                           {
                               index = p.Key.index,
                               // Taken out of its socket (a click on it while editing), it lies on the ground under this id.
                               id = GameAccess.IdOf(p.Value),
                               type = p.Value != null ? p.Value.GetType().Name : null,
                               name = p.Value != null ? Catalog.Find(p.Value.GetType().Name)?.Name : null,
                               quality = p.Value != null ? p.Value.quality : 0,
                               cooldown = p.Value != null ? Math.Round(p.Value.currentCooldown, 2) : 0,
                               // Whether it can ever fire in this memory, and what it misses if not (AreMyGemsCompatible; null without it).
                               fit = GemFit.Of(p.Value, skill),
                           }).ToList();

            if (skill == null)
                return new { slot = slot.ToString(), type = (string)null, sockets = hero.Skill.GetMaxGemCount(slot), gems };

            return new
            {
                slot = slot.ToString(),
                type = skill.GetType().Name,
                name = Catalog.Find(skill.GetType().Name)?.Name,
                id = skill.netId,
                level = skill.level,
                rarity = skill.rarity.ToString(),
                trigger = Trigger(skill),
                sockets = hero.Skill.GetMaxGemCount(slot),
                gems,
            };
        }

        // What casting needs to know: whether it can go now, how it aims, how far it reaches.
        public static object Trigger(AbilityTrigger trigger)
        {
            if (trigger == null) return null;
            var config = trigger.currentConfig;
            bool canCast = false;
            try { canCast = config != null && config.isActive && trigger.CanBeReserved() && trigger.CanBeCast(); } catch (Exception) { }

            float range = 0f;
            try { range = config != null ? config.effectiveRange : 0f; } catch (Exception) { }

            return new
            {
                canCast,
                active = config != null && config.isActive,
                cooldown = Math.Round(trigger.currentConfigCooldownTime, 2),
                maxCooldown = Math.Round(trigger.currentConfigMaxCooldownTime, 2),
                charges = trigger.currentConfigCurrentCharge,
                maxCharges = config != null ? config.maxCharges : 0,
                manaCost = config != null ? Math.Round(config.manaCost, 1) : 0,
                aim = config != null && config.castMethod != null ? config.castMethod.type.ToString() : null,
                range = Math.Round(range, 2),
                configIndex = trigger.currentConfigIndex,
            };
        }

        public static object Status(StatusEffect effect) => new
        {
            type = effect.GetType().Name,
            beneficial = effect.isBeneficialBuff,
            remaining = effect.remainingDuration,
            max = effect.maxDuration,
        };

        public static IEnumerable<T> Actors<T>() where T : Actor
        {
            var actors = NetworkedManagerBase<ActorManager>.softInstance;
            if (actors == null) return Enumerable.Empty<T>();
            return actors.allActors.OfType<T>().Where(a => a != null && a.isActive).ToList();
        }
    }
}
#endif
