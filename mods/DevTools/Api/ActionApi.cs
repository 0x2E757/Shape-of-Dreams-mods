#if DEBUG
using System;
using System.Collections;
using System.Linq;
using UnityEngine;

namespace DevTools
{
    // The hero's actions, through the same EntityControl commands the game's own input handlers
    // send: a move is CmdMoveToDestination, a cast is ControlManager.CastAbility with the CastInfo
    // the cursor would have produced. So everything the server checks for a player - range,
    // cooldown, being stunned - it checks for these too.
    internal static class ActionApi
    {
        private static Hero Hero => GameAccess.RequireLiveHero();

        private static ControlManager Control =>
            GameAccess.RequireManager(ManagerBase<ControlManager>.softInstance, "control manager");

        [Route("POST", "/hero/move", "Walk to a point (as a right-click). With wait, answers on arrival, on stopping, or at the timeout.",
               "x, z, wait=false, within=0.6")]
        private static IEnumerator Move(Args a)
        {
            var hero = Hero;
            var point = a.Point() ?? throw new DevException("missing x and z");
            var destination = Dew.GetValidAgentDestination_Closest(hero.agentPosition, point);
            hero.Control.CmdMoveToDestination(destination, true);

            if (a.Bool("wait"))
            {
                float within = a.Float("within", 0.6f);
                float started = Time.realtimeSinceStartup;
                yield return new WaitForSecondsRealtime(0.15f);
                while (hero != null && Flat(hero.agentPosition, destination) > within && hero.Control.isWalking &&
                       Time.realtimeSinceStartup - started < a.Float("timeout", 30f) - 1f)
                    yield return null;
            }
            yield return new Reply(new
            {
                destination = Describe.Vec(destination),
                position = hero != null ? Describe.Vec(hero.position) : null,
                remaining = hero != null ? Math.Round(Flat(hero.agentPosition, destination), 2) : -1,
            });
        }

        private static float Flat(Vector3 a, Vector3 b) => Vector2.Distance(new Vector2(a.x, a.z), new Vector2(b.x, b.z));

        [Route("POST", "/hero/move_dir", "Keep walking in a direction (as WASD would), or stop with x=0 z=0.", "x, z")]
        private static object MoveDirection(Args a)
        {
            var direction = new Vector3(a.Float("x"), 0f, a.Float("z"));
            if (direction.sqrMagnitude < 0.0001f) Hero.Control.CmdClearMovement();
            else Hero.Control.CmdMoveWithDirection(Vector3.ClampMagnitude(direction, 1f));
            return new { direction = Describe.Vec(direction) };
        }

        // How much room there is around a point: in each of N directions, how far the hero could
        // actually walk before a wall, a cliff or the edge of the navmesh stops it. Answered with
        // Dew.GetValidAgentDestination_Closest, the same clamp every move order goes through, so
        // "free" here is what a move there would really get. Enemies in each direction are
        // counted too - the two together are what telling "back away" from "break out" needs.
        [Route("GET", "/nav/probe", "Free walking distance in each of N directions around the hero (or x,z), with the enemies lying in each direction - for choosing where to kite or break out.",
               "x,z?, radius=7, dirs=16")]
        private static object Probe(Args a)
        {
            var hero = GameAccess.Hero;
            var origin = a.Point() ?? (hero != null ? hero.agentPosition : throw new DevException("no hero; pass x,z"));
            float radius = Mathf.Clamp(a.Float("radius", 7f), 1f, 40f);
            int count = Mathf.Clamp(a.Int("dirs", 16), 4, 64);

            var enemies = hero == null ? new System.Collections.Generic.List<Entity>() :
                Describe.Actors<Entity>().Where(e => e.isAlive && e != hero && hero.GetRelation(e) == EntityRelation.Enemy &&
                                                     Flat(e.position, origin) < radius + 4f).ToList();
            float half = 180f / count;

            var directions = Enumerable.Range(0, count).Select(i =>
            {
                float angle = i * 360f / count;
                var dir = Quaternion.Euler(0f, angle, 0f) * Vector3.forward;
                var reached = Dew.GetValidAgentDestination_Closest(origin, origin + dir * radius);
                var inCone = enemies.Where(e =>
                {
                    var to = e.position - origin;
                    to.y = 0f;
                    return to.sqrMagnitude > 0.01f && Vector3.Angle(dir, to) <= half * 1.5f;
                }).ToList();
                return new
                {
                    angle = Mathf.RoundToInt(angle),
                    x = Math.Round(dir.x, 3),
                    z = Math.Round(dir.z, 3),
                    free = Math.Round(Flat(origin, reached), 2),
                    enemies = inCone.Count,
                    nearestEnemy = inCone.Count > 0 ? Math.Round(inCone.Min(e => Flat(e.position, origin)), 2) : (double?)null,
                };
            }).ToList();
            return new { origin = Describe.Vec(origin), radius, directions };
        }

        [Route("POST", "/hero/stop", "Stop moving, attacking and casting.")]
        private static object Stop(Args a)
        {
            Hero.Control.CmdStop();
            return new { stopped = true };
        }

        [Route("POST", "/hero/attack", "Basic attack: an entity by id (chasing it), or attack-move to a point (as A-click).",
               "target (id) | x,z")]
        private static object Attack(Args a)
        {
            var hero = Hero;
            if (a.Has("target"))
            {
                var target = GameAccess.RequireActor(a.Id("target")) as Entity ?? throw new DevException("that is not an entity");
                hero.Control.CmdAttack(target, true);
                return new { attacking = target.netId, type = target.GetType().Name };
            }
            var point = a.Point() ?? throw new DevException("target or x,z");
            hero.Control.CmdAttackMove(point, false);
            return new { attackMove = Describe.Vec(point) };
        }

        // What the attack-in-place key sends, frame after frame, while it is held
        // (ControlManager.DoAttackInPlaceAtWorldPosition): a cast of the basic attack that never
        // walks - at the entity when it is in range, otherwise in its direction when the attack
        // can be fired untargeted. Movement is a separate command, so a hero moving with
        // /hero/move_dir keeps moving while this fires - shooting on the run, as a player does.
        [Route("POST", "/hero/attack_in_place", "Basic attack without walking, as the attack-in-place key: at the target if in range, else toward it or the point. Does not interrupt /hero/move_dir - send both to shoot while running. Repeat it; the key sends one per frame.",
               "target (id) | x,z")]
        private static object AttackInPlace(Args a)
        {
            var hero = Hero;
            var attack = hero.Ability.attackAbility as AttackTrigger ?? throw new DevException("this hero has no basic attack");
            var config = attack.currentConfig;

            Entity target = null;
            if (a.Has("target"))
                target = GameAccess.RequireActor(a.Id("target")) as Entity ?? throw new DevException("target is not an entity");
            var position = target != null ? target.position : a.Point() ?? throw new DevException("target or x,z");

            if (target != null && config.CheckRange(hero, target))
            {
                hero.Control.CmdCast(attack, attack.currentConfigIndex, new CastInfo { caster = hero, target = target }, false, true);
                return new { shot = "target", target = target.netId };
            }

            if (!attack.allowNonTargetedCast)
                return new { shot = (string)null, note = "out of range, and this attack needs a target" };

            var from = hero.agentPosition;
            var toward = position - from;
            toward.y = 0f;
            var point = from + Vector3.ClampMagnitude(toward, config.effectiveRange);
            hero.Control.CmdCast(attack, attack.currentConfigIndex,
                                 new CastInfo { caster = hero, angle = CastInfo.GetAngle(toward), point = point }, false, false);
            return new { shot = "direction", at = Describe.Vec(point) };
        }

        // A skill at a point, at an entity, or in a direction - whichever its aim is, the same
        // CastInfo the cursor would give. Hold-to-charge skills start "sampling" once cast: the
        // game re-aims them from the cursor every frame and fires on a left click. So the virtual
        // cursor is put on the target for the length of the cast, and for those skills a left
        // click is sent after 'charge' seconds.
        [Route("POST", "/hero/cast", "Cast a memory. Aims at target (id), else x,z, else angle (degrees), else straight ahead. Skills that charge are released after 'charge' seconds.",
               "slot (Q W E R Identity Movement), target? | x,z? | angle?, move=true (walk into range), charge=0.5, sample=0.35 (seconds to look for a charging skill; 0 = do not look)")]
        private static IEnumerator Cast(Args a)
        {
            var hero = Hero;
            var control = Control;
            var slot = GameAccess.ParseSlot(a.Str("slot"));
            if (!hero.Skill.TryGetSkill(slot, out var skill) || skill == null) throw new DevException("nothing in " + slot);

            var config = skill.currentConfig;
            if (config == null || !config.isActive) throw new DevException(slot + " (" + skill.GetType().Name + ") is passive or inactive right now");
            if (!skill.CanBeReserved())
                throw new DevException(slot + " is not ready: cooldown " + Math.Round(skill.currentConfigCooldownTime, 2) +
                                       "s, charges " + skill.currentConfigCurrentCharge);

            Entity target = null;
            if (a.Has("target"))
                target = GameAccess.RequireActor(a.Id("target")) as Entity ?? throw new DevException("target is not an entity");

            Vector3 point;
            if (target != null) point = target.position;
            else if (a.Point() is Vector3 p) point = p;
            else if (a.Has("angle")) point = hero.position + Quaternion.Euler(0f, a.Float("angle"), 0f) * Vector3.forward * 5f;
            else point = hero.position + hero.transform.forward * 5f;

            var info = BuildCastInfo(hero, config, target, point);

            bool hadMouse = VirtualMouse.Active;
            var previous = VirtualMouse.Position;
            var screen = GameAccess.ToScreen(point);
            if (screen.HasValue) VirtualMouse.MoveTo(screen.Value);

            control.CastAbility(skill, info, a.Bool("move", true));

            // A charging skill shows itself by starting to sample within a frame or two of the cast
            // reaching the server - solo, almost at once. 'sample' caps the look (0: do not look).
            float look = Mathf.Clamp(a.Float("sample", 0.35f), 0f, 1f);
            bool charged = false;
            float started = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - started < look)
            {
                if (control.localSampleContext.HasValue) { charged = true; break; }
                yield return null;
            }

            if (charged)
            {
                yield return new WaitForSecondsRealtime(Mathf.Clamp(a.Float("charge", 0.5f), 0f, 10f));
                var again = target != null ? GameAccess.ToScreen(target.position) : screen;
                if (again.HasValue) VirtualMouse.MoveTo(again.Value);
                VirtualMouse.Down(MouseButton.Left);
                yield return null;
                yield return null;
                VirtualMouse.Up(MouseButton.Left);
                yield return null;
                yield return null;
            }

            if (hadMouse) VirtualMouse.MoveTo(previous);
            else VirtualMouse.Release();

            yield return new Reply(new
            {
                cast = skill.GetType().Name,
                slot = slot.ToString(),
                aim = config.castMethod.type.ToString(),
                at = Describe.Vec(point),
                target = target != null ? target.netId : 0,
                charged,
            });
        }

        private static CastInfo BuildCastInfo(Hero hero, TriggerConfig config, Entity target, Vector3 point)
        {
            switch (config.castMethod.type)
            {
                case CastMethodType.None:
                    return new CastInfo(hero);

                case CastMethodType.Cone:
                case CastMethodType.Arrow:
                    return new CastInfo(hero, CastInfo.GetAngle(point - hero.transform.position)) { point = point };

                case CastMethodType.Point:
                    return new CastInfo(hero, point);

                case CastMethodType.Target:
                {
                    // An aimed skill needs an entity; given only a point, the nearest valid one to it.
                    var chosen = target ?? Describe.Actors<Entity>()
                        .Where(e => e.isAlive && Valid(config, hero, e))
                        .OrderBy(e => Vector3.Distance(e.position, point))
                        .FirstOrDefault(e => Vector3.Distance(e.position, point) < 4f);
                    if (chosen == null) throw new DevException("this skill targets an entity - pass target (id), or x,z near one");
                    if (!Valid(config, hero, chosen)) throw new DevException(chosen.GetType().Name + " is not a valid target for this skill");
                    return new CastInfo(hero, chosen);
                }

                default:
                    throw new DevException("unknown aim " + config.castMethod.type);
            }
        }

        private static bool Valid(TriggerConfig config, Hero hero, Entity e)
        {
            try { return config.targetValidator == null || config.targetValidator.Evaluate(hero, e); }
            catch (Exception) { return false; }
        }

        [Route("POST", "/hero/interact", "Walk to something and use it, as F does: pick up a memory or essence, use a shrine, open a merchant, enter a rift. alt=true is G (dismantle an item).",
               "id, alt=false")]
        private static object Interact(Args a)
        {
            var actor = GameAccess.RequireActor(a.Id("id"));
            if (!(actor is IInteractable interactable)) throw new DevException(actor.GetType().Name + " cannot be interacted with");
            var hero = Hero;
            bool can = false;
            try { can = interactable.CanInteract(hero); } catch (Exception) { }
            hero.Control.CmdInteract(interactable, a.Bool("alt"), false);
            return new
            {
                interacting = actor.netId,
                type = actor.GetType().Name,
                canInteractNow = can,
                note = can ? null : "the game says it cannot be used right now - the hero may still walk to it",
            };
        }

        // ----- the edit screen ----------------------------------------------------------
        //
        // Everything that changes the loadout goes through EditSkillManager's own click handlers,
        // the ones the skill buttons and gem sockets call - so the same things are refused for
        // the same reasons (a locked slot, a full row, dream dust short), and nothing can be done
        // here that the screen in front of a player would not let them do. The mode it is in says
        // what a click means: holding a memory or an essence, an upgrade well's offer, selling.

        private static EditSkillManager Edit => GameAccess.RequireManager(ManagerBase<EditSkillManager>.softInstance, "edit-skill manager");

        [Route("POST", "/edit/click", "Click a skill slot, or an essence socket (slot + index), on the edit screen - whatever it means in the current mode: equip what is held, upgrade at a well, sell at a shop. /state shows the mode.",
               "slot, index? (a socket)")]
        private static IEnumerator EditClick(Args a)
        {
            var edit = Edit;
            if (edit.mode == EditSkillManager.ModeType.None)
                throw new DevException("the edit screen is not open - pick an item up, or use a well or a shop, first");
            var slot = GameAccess.ParseSlot(a.Str("slot"));
            var mode = edit.mode;
            int from = CenterMessages.Count;

            if (a.Has("index")) edit.DoClickOnGemSlot(new GemLocation(slot, a.Int("index")));
            else edit.DoClickOnSkillButton(slot);

            // The screen answers a refused click with a message in the middle of the screen;
            // a frame later it is there to read.
            yield return null;
            yield return new Reply(new
            {
                mode = mode.ToString(),
                modeNow = edit.mode.ToString(),
                refused = CenterMessages.Since(from),
            });
        }

        // A socketed essence dragged onto another socket, as a player drags it in the plain edit
        // screen (LeftCtrl held). EditSkillManager's drag handler is a local function that raycasts
        // the cursor, so this sends the command it sends (HandleGemToGem -> CmdSwapSlotGem) after
        // the checks the screen itself makes: the plain screen open, an essence to drag, a socket
        // the memory has. The two sockets swap; the target may be empty.
        [Route("POST", "/edit/drag", "Drag a socketed essence onto another socket in the plain edit screen (hold LeftCtrl first), as a player drags it: the two sockets swap, the target may be empty.",
               "slot, index, toSlot, toIndex")]
        private static IEnumerator EditDrag(Args a)
        {
            var edit = Edit;
            if (edit.mode != EditSkillManager.ModeType.Regular)
                throw new DevException("the plain edit screen is not open (hold LeftCtrl) - mode " + edit.mode);
            var hero = GameAccess.RequireHero();
            var from = new GemLocation(GameAccess.ParseSlot(a.Str("slot")), a.Int("index"));
            var to = new GemLocation(GameAccess.ParseSlot(a.Str("toSlot")), a.Int("toIndex"));
            if (!hero.Skill.gems.TryGetValue(from, out var moving) || moving == null)
                throw new DevException("no essence in " + from.skill + " " + from.index);
            if (to.index < 0 || to.index >= hero.Skill.GetMaxGemCount(to.skill))
                throw new DevException(to.skill + " has no socket " + to.index);
            if (from.Equals(to)) throw new DevException("the same socket");
            int msgs = CenterMessages.Count;

            hero.Skill.CmdSwapSlotGem(to, from);

            // The command runs on the host within a frame or two.
            yield return null;
            yield return null;
            yield return new Reply(new
            {
                from = new { slot = from.skill.ToString(), index = from.index },
                to = new { slot = to.skill.ToString(), index = to.index },
                nowFrom = hero.Skill.gems.TryGetValue(from, out var gf) && gf != null ? (uint?)gf.netId : null,
                nowTo = hero.Skill.gems.TryGetValue(to, out var gt) && gt != null ? (uint?)gt.netId : null,
                refused = CenterMessages.Since(msgs),
            });
        }

        [Route("POST", "/edit/end", "Close the edit screen (as Escape does). Anything still held in hand is dropped.")]
        private static object EditEnd(Args a)
        {
            Edit.EndEdit();
            return new { mode = Edit.mode.ToString() };
        }

        // Dismantling is tapped, not pressed: each G tap adds 0.4 to the item's progress, taps
        // closer than 0.075 s apart do not count, and progress decays after a second without one
        // (SkillTrigger.DismantleTap*). So this taps as a player does - walks up with the first,
        // then keeps tapping until the item is gone.
        [Route("POST", "/hero/dismantle", "Dismantle a memory or essence on the ground into dream dust, tapping the dismantle key (G) until it breaks. Walks to it first.",
               "id, taps=12")]
        private static IEnumerator Dismantle(Args a)
        {
            var hero = Hero;
            var actor = GameAccess.RequireActor(a.Id("id"));
            if (!(actor is SkillTrigger) && !(actor is Gem)) throw new DevException(actor.GetType().Name + " cannot be dismantled");
            var item = (IInteractable)actor;
            int taps = Mathf.Clamp(a.Int("taps", 12), 1, 40);
            uint id = actor.netId;

            // Near enough first: the walk is the first interaction's, as with any other.
            hero.Control.CmdInteract(item, true, false);
            float started = Time.realtimeSinceStartup;
            while (actor != null && Flat(hero.agentPosition, actor.position) > 3.2f && Time.realtimeSinceStartup - started < 8f)
                yield return null;

            int sent = 1;
            while (actor != null && sent < taps)
            {
                yield return new WaitForSecondsRealtime(0.12f);
                if (actor == null) break;
                hero.Control.CmdInteract(item, true, false);
                sent++;
            }
            yield return new WaitForSecondsRealtime(0.3f);
            yield return new Reply(new { dismantled = actor == null, taps = sent, id });
        }

        [Route("POST", "/hero/equip", "Put what the hero holds in hand into a slot (a memory) or a socket (an essence), as clicking that slot does. Pick an item up with /hero/interact first; one that fits a free slot is equipped by picking it up.",
               "slot, index? (essences: a particular socket)")]
        private static IEnumerator Equip(Args a)
        {
            var hero = Hero;
            if (!(hero.Skill.holdingObject is Actor)) throw new DevException("nothing in hand - /hero/interact with an item first");
            return EditClick(a);
        }

        [Route("POST", "/hero/drop_held", "Put down whatever the hero is holding in hand (a memory with no free slot). Travel is refused while holding one.")]
        private static object DropHeld(Args a)
        {
            var hero = Hero;
            var held = hero.Skill.holdingObject as Actor;
            if (held == null) return new { dropped = (string)null };
            hero.Skill.CmdStopHoldInHand();
            return new { dropped = held.GetType().Name, id = held.netId };
        }
    }
}
#endif
