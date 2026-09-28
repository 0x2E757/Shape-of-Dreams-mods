#if DEBUG
using System;
using System.Linq;
using HarmonyLib;
using Mirror;
using TMPro;
using UnityEngine;

namespace DevTools
{
    // What is on the screen and in the world, as JSON. /state is the one to poll: it says which
    // screen the game is on, whether anything is waiting for an answer, and the hero in brief.
    internal static class StateApi
    {
        [Route("GET", "/state", "Where the game is: scene, UI state, loading, any message or conversation waiting for an answer, the hero in brief, the room.")]
        private static object State(Args a)
        {
            var hero = GameAccess.Hero;
            var zone = NetworkedManagerBase<ZoneManager>.softInstance;
            var transition = ManagerBase<TransitionManager>.softInstance;
            var settings = NetworkedManagerBase<GameSettingsManager>.softInstance;
            var game = NetworkedManagerBase<GameManager>.softInstance;
            var control = ManagerBase<ControlManager>.softInstance;

            return new
            {
                scene = GameAccess.Scene,
                uiState = GameAccess.Ui != null ? GameAccess.Ui.state : null,
                phase = settings != null ? settings.state.ToString() : null,
                loading = (transition != null && transition.state == TransitionManager.StateType.Loading) ||
                          (zone != null && zone.isInAnyTransition),
                server = NetworkServer.active,
                timeScale = Math.Round(Time.timeScale, 3),
                gameConcluded = game != null && game.isGameConcluded,
                acceptsHeroInput = control != null && control.shouldProcessCharacterInput,
                message = Message(),
                conversation = Conversation(),
                floatingWindow = FloatingWindow(),
                edit = EditMode(),
                hero = hero == null ? null : new
                {
                    id = hero.netId,
                    type = hero.GetType().Name,
                    hp = Math.Round(hero.currentHealth, 1),
                    maxHp = Math.Round(hero.maxHealth, 1),
                    level = hero.level,
                    position = Describe.Vec(hero.position),
                    knockedOut = hero.isKnockedOut,
                    inCombat = hero.isInCombat,
                    holding = hero.Skill != null && hero.Skill.holdingObject is Actor held ? Describe.Item(held) : null,
                },
                room = zone == null ? null : SafeRoom(zone),
            };
        }

        public static object Message()
        {
            var messages = ManagerBase<MessageManager>.softInstance;
            if (messages == null || !messages.isShowingMessage) return null;

            var buttons = messages.buttons
                .Select((b, i) => (b, i))
                .Where(p => p.b != null && p.b.activeInHierarchy)
                .Select(p => new
                {
                    // The list is in flag order: Ok, Yes, No, Cancel, Custom0..3 - see ButtonType.
                    button = ((DewMessageSettings.ButtonType)(1 << p.i)).ToString(),
                    text = GameAccess.Rich(p.b.GetComponentInChildren<TMP_Text>(true)?.text),
                });
            return new
            {
                text = GameAccess.Rich(messages.contentText != null ? messages.contentText.text : null),
                buttons,
                answerWith = "POST /message/answer {button}",
            };
        }

        public static object Conversation()
        {
            var ui = ActiveConversation();
            if (ui == null) return null;

            var choices = ui.choicesGroup != null && ui.choicesGroup.interactable
                ? ui.choiceDisplay.GetComponentsInChildren<TMP_Text>(false).Select(t => GameAccess.Rich(t.text)).Where(t => t.Length > 0).ToArray()
                : null;
            return new
            {
                speaker = GameAccess.Rich(ui.nameText != null ? ui.nameText.text : null),
                text = GameAccess.Rich(ui.text != null ? ui.text.text : null),
                canAdvance = ui.advanceGroup != null && ui.advanceGroup.interactable,
                choices,
                answerWith = choices != null ? "POST /conversation/choose {index}" : "POST /conversation/advance",
            };
        }

        public static UI_InGame_Conversations_Instance ActiveConversation()
        {
            foreach (var ui in UnityEngine.Object.FindObjectsByType<UI_InGame_Conversations_Instance>(FindObjectsSortMode.None))
            {
                var settings = Traverse.Create(ui).Field("_settings").GetValue<DewConversationSettings>();
                if (settings != null && settings.player != null && settings.isLocalAuthority) return ui;
            }
            return null;
        }

// The edit screen, when it is open: what a click on a slot means now (EquipSkill and EquipGem
        // while something is held, EditSkillShrine at a well, Sell at a shop) and who opened it.
        private static object EditMode()
        {
            var edit = ManagerBase<EditSkillManager>.softInstance;
            if (edit == null || edit.mode == EditSkillManager.ModeType.None) return null;
            var provider = edit.currentProvider as Component;
            return new
            {
                mode = edit.mode.ToString(),
                provider = provider != null ? provider.GetType().Name : null,
                answerWith = "POST /edit/click {slot, index?}, or /edit/end",
            };
        }

        private static object FloatingWindow()
        {
            var windows = ManagerBase<FloatingWindowManager>.softInstance;
            if (windows == null || windows.currentTarget == null) return null;
            var target = windows.currentTarget as Component;
            return new { target = target != null ? Json.Ref(target) : null, type = windows.currentTarget.GetType().Name };
        }

        // Between rooms the zone manager is half set up for a frame or two; say nothing then.
        private static object SafeRoom(ZoneManager zone)
        {
            try { return Room(zone); }
            catch (NullReferenceException) { return null; }
        }

        private static object Room(ZoneManager zone)
        {
            var exit = Rift_RoomExit.instance;
            var enemies = Describe.Actors<Monster>().Count(m => m.isAlive && m.owner == DewPlayer.creep);
            string cannotTravel = null;
            try { cannotTravel = zone.GetCannotTravelReason().reasonText; } catch (Exception) { }
            return new
            {
                zone = zone.currentZone != null ? zone.currentZone.name : null,
                zoneIndex = zone.currentZoneIndex,
                node = zone.currentNodeIndex,
                nodeType = zone.currentNodeIndex >= 0 && zone.currentNodeIndex < zone.nodes.Count ? zone.nodes[zone.currentNodeIndex].type.ToString() : null,
                room = zone.currentRoom != null ? zone.currentRoom.name : null,
                enemiesAlive = enemies,
                exitOpen = exit != null && exit.isOpen,
                exitPosition = exit != null ? Describe.Vec(exit.position) : null,
                exitId = exit != null ? exit.netId : 0,
                hunted = zone.isCurrentNodeHunted,
                voting = zone.isVoting,
                cannotTravel = string.IsNullOrEmpty(cannotTravel) ? null : GameAccess.Rich(cannotTravel),
                unclaimed = Unclaimed(),
                combatAreas = CombatAreas(),
                cleared = SingletonDewNetworkBehaviour<Room>.instance != null && SingletonDewNetworkBehaviour<Room>.instance.didClearRoom,
                clearsOnEnter = ClearsOnEnter(),
                bossEntry = BossEntry(zone),
            };
        }

        // A boss room's entry: the spot that plays the boss's intro and teleports the heroes into
        // the arena once all of them stand in it - in the Sky boss room the only way from the start
        // to the arena; a player sees it as a portal. waiting: not used yet (the presence zone
        // switches itself off after its one go). Looked up only in a boss room.
        private static object BossEntry(ZoneManager zone)
        {
            if (zone.currentNodeIndex < 0 || zone.currentNodeIndex >= zone.nodes.Count ||
                zone.nodes[zone.currentNodeIndex].type != WorldNodeType.ExitBoss) return null;
            var entry = UnityEngine.Object.FindObjectOfType<DewBossRoomEntry>();
            if (entry == null) return null;
            var presence = entry.GetComponent<DewAllHeroesPresentZone>();
            var teleporter = entry.GetComponent<DewHeroesTeleporter>();
            Vector3? destination = null;
            try { if (teleporter != null && teleporter.destination != null) destination = teleporter.pathablePosition; } catch (Exception) { }
            return new
            {
                position = Describe.Vec(entry.transform.position),
                destination = destination.HasValue ? Describe.Vec(destination.Value) : null,
                waiting = presence != null && presence.enabled,
            };
        }

        // Rooms with no fight left can still be shut until the hero reaches a certain part of it
        // (usually the far end): walking in there is what clears the room.
        private static object ClearsOnEnter()
        {
            var room = SingletonDewNetworkBehaviour<Room>.instance;
            if (room == null || room.sections == null || room.didClearRoom) return null;
            return room.sections
                .Where(s => s != null && s.clearRoomOnEnterFirstTime)
                .Select(s => Describe.Vec(s.pathablePivot))
                .ToList();
        }

        // The parts of the room where a fight is still to come: the exit stays shut until they are
        // cleared, and their monsters appear only when the hero walks in. Where they are is the
        // room's layout, which a player sees; what is in them is not listed.
        private static object CombatAreas()
        {
            var room = SingletonDewNetworkBehaviour<Room>.instance;
            if (room == null || room.sections == null) return null;
            return room.sections
                .Where(s => s != null && s.monsters != null && s.monsters.isMarkedAsCombatArea && !s.monsters.didClearCombatArea)
                .Select(s => new { position = Describe.Vec(s.pathablePivot), active = s.monsters.isCombatActive })
                .ToList();
        }

        // What the world map will call an unclaimed reward (ZoneManager.TravelWithValidationAndConfirmation):
        // a reward shrine still available. Listed so that "you have unclaimed rewards" can be acted on.
        private static object Unclaimed() =>
            Describe.Actors<Actor>()
                .Where(x => x is IRewardActor && !(x is Shrine shrine && !shrine.isAvailable))
                .Select(x => new { id = x.netId, type = x.GetType().Name, name = Describe.EntityName(x), position = Describe.Vec(x.position) })
                .ToList();

        [Route("GET", "/hero", "The local hero in full: health, mana, level, gold, every slot with cooldowns, how it aims and its essences, the basic attack, status effects, what is held in hand.")]
        private static object Hero(Args a)
        {
            var hero = GameAccess.RequireHero();
            var player = GameAccess.Player;
            var status = hero.Status;
            return new
            {
                id = hero.netId,
                type = hero.GetType().Name,
                name = Describe.EntityName(hero),
                level = hero.level,
                maxLevel = hero.maxLevel,
                exp = hero.exp,
                maxExp = hero.maxExp,
                hp = Math.Round(hero.currentHealth, 1),
                maxHp = Math.Round(hero.maxHealth, 1),
                shield = Math.Round(status.currentShield, 1),
                mana = Math.Round(hero.currentMana, 1),
                maxMana = Math.Round(hero.maxMana, 1),
                gold = player != null ? player.gold : 0,
                dreamDust = player != null ? player.dreamDust : 0,
                position = Describe.Vec(hero.position),
                screen = Describe.Screen(hero.position),
                alive = hero.isAlive,
                knockedOut = hero.isKnockedOut,
                inCombat = hero.isInCombat,
                walking = hero.Control.isWalking,
                // Standing on lava, by the game's own test.
                onHazard = Describe.Actors<LavaLand_Lava>().Any(l => l.IsEntityOnLava(hero)),
                stats = new
                {
                    attackDamage = Math.Round(status.attackDamage, 1),
                    abilityPower = Math.Round(status.abilityPower, 1),
                    armor = Math.Round(status.armor, 1),
                    abilityHaste = Math.Round(status.abilityHaste, 1),
                    attackSpeed = Math.Round(status.attackSpeedMultiplier, 2),
                    moveSpeed = Math.Round(status.movementSpeedMultiplier, 2),
                    critChance = Math.Round(status.critChance, 2),
                },
                skills = Enum.GetValues(typeof(HeroSkillLocation)).Cast<HeroSkillLocation>().Select(s => Describe.Skill(hero, s)),
                attack = Describe.Trigger(hero.Ability.attackAbility),
                statusEffects = status.statusEffects.Where(e => e != null).Select(Describe.Status),
                holding = hero.Skill.holdingObject is Actor held ? new { id = held.netId, item = Describe.Item(held) } : null,
            };
        }

        // Where an essence would work: AreMyGemsCompatible's verdict for it in each memory the hero
        // wears that has sockets - the one the tooltip warning shows when a player drags it over a
        // slot. The essence is the one in hand, one on the ground or worn (id), or a type as a
        // merchant's stock or a shrine's offer names it (the game's own template of it).
        [Route("GET", "/hero/fit", "Whether an essence can ever fire in each memory the hero wears with sockets, by AreMyGemsCompatible's verdict (Fine or Dead, why, and what a Dead one is missing: an element, or needs the memory never does), counting the essences already socketed there. With it: the essence's profile (needs, supplies, gate, adds), what each memory does, and the essences socketed in it with their verdicts and profiles. The essence held in hand, or one by id, or a type name (merchant stock, shrine offers). verdict is null when that mod is not loaded.",
               "id? | type? (neither: the one held in hand)")]
        private static object Fit(Args a)
        {
            var hero = GameAccess.RequireHero();
            Gem gem;
            if (a.Has("id"))
            {
                gem = GameAccess.RequireActor(a.Id("id")) as Gem ?? throw new DevException("actor " + a.Id("id") + " is not an essence");
            }
            else if (a.Has("type"))
            {
                var entry = Catalog.Require(a.Str("type"));
                if (entry.kind != CatalogKind.Essence) throw new DevException(entry.typeName + " is a memory, not an essence");
                gem = DewResources.GetByType<Gem>(entry.type) ?? throw new DevException("no asset for " + entry.typeName);
            }
            else
            {
                gem = hero.Skill.holdingObject as Gem ?? throw new DevException("no essence in hand - pass id or type");
            }

            bool loaded = GemFit.Loaded;
            var slots = Enum.GetValues(typeof(HeroSkillLocation)).Cast<HeroSkillLocation>()
                .Select(s =>
                {
                    // An empty slot (a memory being swapped at a shrine) has no socket count to ask for.
                    SkillTrigger skill = null;
                    int sockets = 0;
                    try
                    {
                        if (hero.Skill.TryGetSkill(s, out skill) && skill != null) sockets = hero.Skill.GetMaxGemCount(s);
                    }
                    catch (Exception) { skill = null; }
                    return new { s, skill, sockets };
                })
                .Where(x => x.skill != null && x.sockets > 0)
                .Select(x =>
                {
                    int used = 0;
                    try { used = hero.Skill.gems.Count(p => p.Key.skill == x.s && p.Value != null); } catch (Exception) { }
                    // The essences already in it, each with its own verdict there and what it hands
                    // the memory - so a caller can see which of them the asked essence would wake.
                    // Each part is read on its own: one that throws leaves its field null, never
                    // the whole answer an error.
                    object socketed = null;
                    try
                    {
                        socketed = hero.Skill.gems.Where(p => p.Key.skill == x.s && p.Value != null).OrderBy(p => p.Key.index)
                            .Select(p => new
                            {
                                index = p.Key.index,
                                id = GameAccess.IdOf(p.Value),
                                type = p.Value.GetType().Name,
                                fit = GemFit.Of(p.Value, x.skill),
                                profile = GemFit.Profile(p.Value),
                            }).ToList();
                    }
                    catch (Exception) { }
                    string rarity = null;
                    int level = 0;
                    try { rarity = x.skill.rarity.ToString(); level = x.skill.level; } catch (Exception) { }
                    // Its cooldown and charges as now in effect (haste included), and how it has
                    // been used this run (MemoryUse) - for weighing where an essence gives most.
                    double cooldown = 0;
                    int charges = 0;
                    try { cooldown = Math.Round(x.skill.currentConfigMaxCooldownTime, 2); charges = x.skill.currentConfig != null ? x.skill.currentConfig.maxCharges : 0; } catch (Exception) { }
                    object use = null;
                    try { use = MemoryUse.Of(x.skill.GetType().Name); } catch (Exception) { }
                    return new
                    {
                        slot = x.s.ToString(),
                        memory = x.skill.GetType().Name,
                        rarity,
                        level,
                        sockets = x.sockets,
                        free = Math.Max(0, x.sockets - used),
                        fit = GemFit.Of(gem, x.skill),
                        does = GemFit.Memory(x.skill),
                        cooldown,
                        charges,
                        use,
                        gems = socketed,
                    };
                })
                .ToList();

            // Asked by type, the essence is the game's template of it - never spawned, so it has no
            // id (IdOf answers 0 rather than let Mirror throw) and no owner.
            uint id = GameAccess.IdOf(gem);
            string name = null;
            try { name = Catalog.Find(gem.GetType().Name)?.Name; } catch (Exception) { }
            return new
            {
                gem = new { id = id != 0 ? id : (uint?)null, type = gem.GetType().Name, name, profile = GemFit.Profile(gem), limits = GemFit.Limits(gem) },
                loaded,
                slots,
            };
        }

        [Route("GET", "/entities", "Living things near the hero: monsters, other heroes, summons. Nearest first.",
               "radius=60, kind=all|enemies|monsters|heroes|props (gold and dream dust deposits and the like: attack them to break them), dead=false, limit=100")]
        private static object Entities(Args a)
        {
            var hero = GameAccess.Hero;
            var actors = NetworkedManagerBase<ActorManager>.softInstance ?? throw new DevException("no actors here - not in a run");
            float radius = a.Float("radius", 60f);
            string kind = a.Str("kind", "all").ToLowerInvariant();
            bool dead = a.Bool("dead");
            var origin = hero != null ? hero.position : Vector3.zero;

            var list = actors.allEntities
                .Where(e => e != null && e.isActive && e != hero)
                .Where(e => dead || e.isAlive)
                .Where(e => hero == null || Vector3.Distance(e.position, origin) <= radius)
                .Where(e => kind == "all" || (kind == "monsters" && e is Monster) || (kind == "heroes" && e is Hero) || (kind == "props" && e is PropEntity && !(e is PropEnt_Merchant_Base)) ||
                            (kind == "enemies" && hero != null && Enemy(hero, e)))
                .OrderBy(e => Vector3.Distance(e.position, origin))
                .Take(a.Int("limit", 100))
                .Select(e => Describe.Entity(e, hero))
                .ToList();
            return new { count = list.Count, entities = list };
        }

        private static bool Enemy(Hero hero, Entity e)
        {
            try { return hero.GetRelation(e) == EntityRelation.Enemy; }
            catch (Exception) { return false; }
        }

        [Route("GET", "/interactables", "Things the hero can walk up to and use: memories and essences on the ground, shrines (with their choices), merchants (with stock), rifts and exits; plus gold and experience pickups.",
               "radius=80, limit=100")]
        private static object Interactables(Args a)
        {
            var hero = GameAccess.Hero;
            var actors = NetworkedManagerBase<ActorManager>.softInstance ?? throw new DevException("no actors here - not in a run");
            var origin = hero != null ? hero.position : Vector3.zero;
            float radius = a.Float("radius", 80f);

            var things = actors.allActors
                .Where(x => x != null && x.isActive && x is IInteractable)
                .Where(x => !(x is Gem g) || (g.owner == null && g.handOwner == null))
                .Where(x => !(x is SkillTrigger s) || (s.owner == null && s.handOwner == null))
                .Where(x => !(x is Hero))
                .Where(x => hero == null || Vector3.Distance(x.position, origin) <= radius)
                .OrderBy(x => Vector3.Distance(x.position, origin))
                .Take(a.Int("limit", 100))
                .Select(x => Interactable(x, hero))
                .ToList();

            var pickups = actors.allActors.OfType<PickupInstance>()
                .Where(p => p != null && p.isActive && (hero == null || Vector3.Distance(p.position, origin) <= radius))
                .Select(p => new { id = p.netId, type = p.GetType().Name, position = Describe.Vec(p.position), distance = hero != null ? Describe.Distance(p, origin) : 0 })
                .Take(100)
                .ToList();

            return new { count = things.Count, interactables = things, pickups };
        }

        private static object Interactable(Actor x, Hero hero)
        {
            var i = (IInteractable)x;
            bool can = false;
            try { can = hero != null && i.CanInteract(hero); } catch (Exception) { }

            object details = null;
            switch (x)
            {
                case Gem _:
                case SkillTrigger _:
                    details = Describe.Item(x);
                    break;
                case ChoiceShrine choice:
                    details = new { shrine = ShrineInfo(choice, hero), choices = Choices(choice), offers = Offers(choice) };
                    break;
                case Shrine shrine:
                    details = new { shrine = ShrineInfo(shrine, hero), choices = (object)null, offers = Offers(shrine) };
                    break;
                case PropEnt_Merchant_Base merchant:
                    details = new { stock = Stock(merchant) };
                    break;
                case Rift rift:
                    details = new
                    {
                        open = rift.isOpen,
                        locked = rift.isLocked,
                        exit = rift is Rift_RoomExit,
                        nextNode = rift is Rift_RoomExit re ? re.nextNodeIndex : (int?)null,
                    };
                    break;
            }

            return new
            {
                id = x.netId,
                type = x.GetType().Name,
                name = x is Gem || x is SkillTrigger ? Catalog.Find(x.GetType().Name)?.Name : Describe.EntityName(x),
                kind = Describe.Kind(x),
                position = Describe.Vec(x.position),
                distance = hero != null ? Describe.Distance(x, hero.position) : (float?)null,
                canInteract = can,
                screen = Describe.Screen(x.position),
                details,
            };
        }

        private static object ShrineInfo(Shrine shrine, Hero hero)
        {
            Cost? cost = null;
            try { cost = hero != null ? shrine.GetCost(hero) : null; } catch (Exception) { }
            return new { available = shrine.isAvailable, locked = shrine.isLocked, cost };
        }

        private static object Choices(ChoiceShrine shrine)
        {
            var player = GameAccess.Player;
            if (player == null || !shrine.choices.TryGetValue(player.guid, out var items) || items == null) return null;
            return items.Select((c, index) => new { index, type = c.typeName, name = Catalog.Find(c.typeName)?.Name, c.level });
        }

        // What a shrine that takes CmdChoose is offering this player - ChoiceShrine's choices,
        // Chaos's rewards, and whatever the others call theirs. They all keep it the same way: a
        // synced dictionary from a player's guid to an array, the array's order being the index
        // CmdChoose takes. So any such field is read, whatever it is named.
        private static object Offers(Shrine shrine)
        {
            if (AccessTools.Method(shrine.GetType(), "CmdChoose") == null) return null;
            var player = GameAccess.Player;
            if (player == null) return null;

            for (var t = shrine.GetType(); t != null && t != typeof(Shrine); t = t.BaseType)
            foreach (var field in t.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly))
            {
                var ft = field.FieldType;
                if (!ft.IsGenericType || ft.GetGenericArguments().Length != 2 || ft.GetGenericArguments()[0] != typeof(string) ||
                    !ft.GetGenericArguments()[1].IsArray) continue;
                var dictionary = field.GetValue(shrine);
                if (dictionary == null) continue;
                var tryGet = ft.GetMethod("TryGetValue");
                if (tryGet == null) continue;
                var args = new object[] { player.guid, null };
                if (!(bool)tryGet.Invoke(dictionary, args) || !(args[1] is Array offers)) continue;
                return new
                {
                    field = field.Name,
                    items = offers.Cast<object>().Select((o, index) => new { index, offer = Json.From(o, 2) }),
                    chooseWith = "POST /shrine/choose {id, index} while its window is open",
                };
            }
            return null;
        }

        private static object Stock(PropEnt_Merchant_Base merchant)
        {
            var player = GameAccess.Player;
            if (player == null || !merchant.merchandises.TryGetValue(player.guid, out var items) || items == null) return null;
            return items.Select((m, index) => new
            {
                index,
                type = m.type.ToString(),
                item = m.itemName,
                name = Catalog.Find(m.itemName)?.Name,
                rarity = Catalog.Find(m.itemName)?.Rarity.ToString(),
                m.level,
                price = m.price,
                m.count,
            });
        }

        [Route("GET", "/map", "The zone's nodes: type, status, room, which ones the current node connects to, and why travel is refused if it is.")]
        private static object Map(Args a)
        {
            var zone = NetworkedManagerBase<ZoneManager>.softInstance ?? throw new DevException("no map here - not in a run");
            var ui = InGameUIManager.softInstance as InGameUIManager;
            string cannotTravel = null;
            try { cannotTravel = zone.GetCannotTravelReason().reasonText; } catch (Exception) { }

            int current = zone.currentNodeIndex;
            return new
            {
                zone = zone.currentZone != null ? zone.currentZone.name : null,
                zoneIndex = zone.currentZoneIndex,
                current,
                worldMapShown = ui != null ? ui.isWorldDisplayed.ToString() : null,
                exitOpen = Rift_RoomExit.instance != null && Rift_RoomExit.instance.isOpen,
                cannotTravel = string.IsNullOrEmpty(cannotTravel) ? null : GameAccess.Rich(cannotTravel),
                nodes = zone.nodes.Select((n, i) => new
                {
                    index = i,
                    type = n.type.ToString(),
                    status = n.status.ToString(),
                    room = n.room,
                    current = i == current,
                    reachable = i != current && SafeConnected(zone, current, i),
                    hunted = i < zone.hunterStatuses.Count ? zone.hunterStatuses[i].ToString() : null,
                    // visible: whether the world map shows the player this modifier's icon.
                    modifiers = n.modifiers != null ? n.modifiers.Select((m, mi) => (object)new
                    {
                        m.id,
                        m.type,
                        visible = SafeModifierVisible(n, mi),
                    }).ToList() : null,
                }),
            };
        }

        private static bool SafeModifierVisible(WorldNodeData node, int index)
        {
            try { return node.IsModifierVisible(index); } catch (Exception) { return false; }
        }

        private static bool SafeConnected(ZoneManager zone, int a, int b)
        {
            try { return zone.IsNodeConnected(a, b); } catch (Exception) { return false; }
        }
    }
}
#endif
