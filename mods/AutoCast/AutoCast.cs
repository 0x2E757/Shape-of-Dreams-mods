using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace AutoCast
{
    // Every field of a ModConfig subclass gets a widget built for it automatically
    // (ModConfig.BuildWidgets -> DewGUI.CreateWidgetsForObject), so the field list is the settings
    // screen. Values are persisted under <persistentDataPath>/QuickSave/Mods/<modId>/.
    //
    // Two attributes shape that screen. ModConfig.LabelText names a row, in place of the nicified
    // field name. HideInInspector keeps a field out of the window without keeping it out of the
    // save file, which is what the four toggle states at the bottom need: they belong in the save
    // and have no business being edited by hand.
    public class AutoCastConfig : ModConfig
    {
        // Whether the control appears above each cell at all. Hiding one also silences it: a
        // button you cannot see is not one you can turn off.
        public bool showForQ = true;
        public bool showForW = true;
        public bool showForE = true;
        public bool showForR = true;

        // Casting outside combat wastes cooldowns on nothing, and the game's own autocast
        // gates on this too.
        public bool onlyInCombat = true;

        // Skills that charge while the button is held and fire on release. Autocast has no
        // button to let go of, so it can only misfire them. Left on unless you really want
        // to see what happens.
        public bool skipHoldSkills = true;

        // Minimum delay between two auto casts. Without it the hero tries to fire everything
        // at once and just fights its own cast animations. The [Range] is what turns the row
        // into a slider - see ConfigWidgets.
        [Range(0.05f, 2f)]
        public float castInterval = 0.35f;

        // Ignore enemies that spawned very recently, so the hero does not react
        // inhumanly fast to something materialising next to it.
        [Range(0f, 1f)]
        public float targetDetectDelay = 0.2f;

        // State rather than settings: what the on-screen controls write to, kept here so that
        // which ones are switched on survives a restart.
        [HideInInspector] public bool castQ = false;
        [HideInInspector] public bool castW = false;
        [HideInInspector] public bool castE = false;
        [HideInInspector] public bool castR = false;

        // The new game those four belong to, so that a hero rebuilt inside the same run - a mod
        // reload, most often - is not mistaken for a fresh start and does not clear them again.
        // Only new games have an id at all; see TrackRun.
        //
        // It has to live in the save file rather than in a field: recognising the run in a later
        // session is the point, and a field would have forgotten by then.
        [HideInInspector] public string lastRunId = "";

        private const float LabelWidth = 260f;
        private const float InputWidth = 120f;

        // Each setting is a horizontal row of label then input, and the game sizes both to their
        // contents. That staggers the rows twice over: field names of differing length start
        // their inputs at differing x, and values of differing length make the boxes themselves
        // differing widths. Pinning both widths lines the whole column up.
        public override void BuildWidgets(Transform parent, out SafeAction onChanged,
                                          out SafeAction requestUpdate)
        {
            int firstOwnRow = parent.childCount;
            base.BuildWidgets(parent, out onChanged, out requestUpdate);

            // ModConfig.LabelText would name these rows, but it takes a compile-time constant and
            // so can only ever be one language. The game labels each row with
            // Dew.NicifyVariableName(field.Name), so rows are found by that text and rewritten -
            // which survives fields being reordered, and headers or spacers appearing between
            // them.
            var translated = new Dictionary<string, string>
            {
                [Dew.NicifyVariableName(nameof(showForQ))] = Localization.Get(Localization.ShowQ),
                [Dew.NicifyVariableName(nameof(showForW))] = Localization.Get(Localization.ShowW),
                [Dew.NicifyVariableName(nameof(showForE))] = Localization.Get(Localization.ShowE),
                [Dew.NicifyVariableName(nameof(showForR))] = Localization.Get(Localization.ShowR),
                [Dew.NicifyVariableName(nameof(onlyInCombat))] = Localization.Get(Localization.OnlyInCombat),
                [Dew.NicifyVariableName(nameof(skipHoldSkills))] = Localization.Get(Localization.SkipHold),
                [Dew.NicifyVariableName(nameof(castInterval))] = Localization.Get(Localization.CastInterval),
                [Dew.NicifyVariableName(nameof(targetDetectDelay))] = Localization.Get(Localization.TargetDelay),
            };

            Shared.SettingsRows.Polish(parent, firstOwnRow, LabelWidth, InputWidth, translated);
        }
    }

    public class AutoCast : ModBehaviour
    {
        public AutoCastConfig config = new AutoCastConfig();

        private static readonly Shared.ConfigFieldWidgets Widgets =
            new Shared.ConfigFieldWidgets(typeof(AutoCastConfig));

        // The four the loop walks, which are also the four that get a control. A hero has two
        // more slots - Identity and Movement - and neither belongs here: autocasting the
        // movement memory fires a dash the instant its cooldown ends, wherever the cursor
        // happens to be, and leaves the hero impossible to steer.
        private static readonly HeroSkillLocation[] Slots =
        {
            HeroSkillLocation.Q,
            HeroSkillLocation.W,
            HeroSkillLocation.E,
            HeroSkillLocation.R,
        };

        private int _index;
        private float _lastCastTime;

        // The bar as it was last looked at, so that a memory arriving in a slot can be traced to
        // wherever it came from. Parallel to Slots. `_seen` is separate from a null check because
        // a destroyed trigger also reads as null, and "the slot was emptied" and "this slot has
        // never been looked at" have to stay distinguishable.
        private readonly SkillTrigger[] _equipped = new SkillTrigger[Slots.Length];
        private readonly bool[] _seen = new bool[Slots.Length];

        // Scratch for one pass of TrackEquipped, kept as fields so that watching the bar every
        // frame allocates nothing.
        private readonly SkillTrigger[] _current = new SkillTrigger[Slots.Length];
        private readonly bool[] _wasOn = new bool[Slots.Length];

        // The hero this file last saw. A different one means a run has begun; see TrackRun.
        private Hero _lastHero;

        private readonly List<SlotToggle> _toggles = new List<SlotToggle>();

        private sealed class SlotToggle
        {
            public HeroSkillLocation slot;
            public UI_InGame_SkillButton button;
            public AutoCastToggle control;
        }

        private void Awake()
        {
            LoadConfigsToDisk();
            Widgets.Install();
            Debug.Log("[AutoCast] loaded: " + mod.metadata.id);
        }

        private void OnDestroy()
        {
            // Live reload destroys and recreates the mod, so the widgets have to go with it,
            // otherwise every reload leaves another dead toggle on the HUD.
            DestroyToggles();

            // DewGUI.fieldBuilders is shared with the game and every other mod, so the entries
            // have to come back out.
            Widgets.Remove();
            Debug.Log("[AutoCast] unloaded: " + mod.metadata.id);
        }

        private void Update()
        {
            SyncToggles();

            if (config == null) return;

            var player = DewPlayer.local;
            var hero = player != null ? player.hero : null;

            // Before the gameplay gates below, all of which have frames where they return early -
            // a new run is entered dead, and memories are swapped out of combat.
            TrackRun(hero);
            TrackEquipped(hero);

            if (EntityCheck.IsNullInactiveDeadOrKnockedOut(hero)) return;
            if (config.onlyInCombat && !hero.isInCombat) return;

            // Do not cut into a channel, a zone transition or a cutscene.
            if (hero.Control == null || hero.Control.ongoingChannels.Count > 0) return;
            if (ZoneManager.softInstance == null || ZoneManager.softInstance.isInAnyTransition) return;
            if (CameraManager.softInstance != null && CameraManager.softInstance.isPlayingCutscene) return;

            if (Time.time - _lastCastTime < config.castInterval) return;

            // One skill per tick, round robin over the slots, the same shape the game's own
            // autocast star effect uses.
            for (int i = 0; i < Slots.Length; i++)
            {
                _index = (_index + 1) % Slots.Length;
                var slot = Slots[_index];
                if (!IsCasting(slot)) continue;
                if (!TryCast(hero, hero.Skill.GetSkill(slot))) continue;

                _lastCastTime = Time.time;
                return;
            }
        }

        // ----- resetting ------------------------------------------------------------

        // A new game starts with nothing automated. Resuming one does not.
        //
        // GameManager.runId marks the boundary but does not answer the question. It looks like a
        // run's identity and is not one: a resumed run comes back with a *different* id every
        // time it is loaded, which the player log settles - three consecutive sessions of one
        // continue save, three different ids. So it is used here only as an edge, for "a run just
        // started", and something else has to say which kind.
        //
        // That something is DewNetworkStartSettings.continueData, which is what GameManager reads
        // to make the same decision: set when the run came off a save, and cleared only when the
        // network manager is destroyed, so it stays readable for the whole run rather than for
        // the frame it was consumed in.
        //
        // A guest has no continueData of its own, so joining a host's run reads as new and starts
        // clean. For someone dropping into somebody else's game that is the right answer anyway.
        // The edge is **a hero this file has not seen before**, not a change of run id. The id is
        // no good as an edge because a resumed run never gets one: GameManager.OnStartServer
        // assigns a fresh guid *only* in the branch it takes when the run did not come off a save,
        // so a resumed run leaves it empty. Watching the id therefore misses exactly the case that
        // matters - and worse, misses it silently, leaving the previous run's skill bar on file for
        // TrackEquipped to read as four replaced memories.
        //
        // An empty id is thus not "no information". It is the mark of a resumed run, and is used
        // as one below, alongside the continue data itself.
        private void TrackRun(Hero hero)
        {
            if (hero == null || hero == _lastHero) return;
            _lastHero = hero;

            // A new hero means a new bar, whichever kind of start this is: every trigger on it is
            // a new object, and the records from the last one would read as replacements.
            ForgetBar();

            var game = GameManager.softInstance;
            string runId = game != null ? game.runId : null;

            var settings = DewNetworkManager.startSettings;
            bool fromSave = settings != null && settings.continueData != null;
            bool resumed = fromSave || string.IsNullOrEmpty(runId);

            // Logged with both signals in it. Three readings of this boundary have been wrong, and
            // each cost a round trip to work out which one - a line that carries its own evidence
            // is cheaper than another investigation.
            string why = $"runId='{runId}' fromSave={fromSave}";

            if (resumed)
            {
                Debug.Log($"[AutoCast] resumed run ({why}), autocast kept");
                return;
            }

            if (runId == config.lastRunId)
            {
                // A run already accounted for - the mod was reloaded mid-run, or the hero was
                // rebuilt within it. The bar has been forgotten, which is all that was needed.
                Debug.Log($"[AutoCast] same run ({why}), autocast kept");
                return;
            }

            config.lastRunId = runId;

            // Directly rather than through SetOn, which saves on every write; one save covers the
            // four of them and the id together.
            config.castQ = false;
            config.castW = false;
            config.castE = false;
            config.castR = false;

            SaveConfigsToDisk();
            Debug.Log($"[AutoCast] new run ({why}), autocast cleared");
        }

        // Forget what was on the skill bar, so that whatever turns up next counts as a first
        // sighting and keeps the setting stored for its slot rather than being read as a swap.
        private void ForgetBar()
        {
            for (int i = 0; i < _seen.Length; i++)
            {
                _equipped[i] = null;
                _seen[i] = false;
            }
        }

        // Autocast belongs to the memory rather than to the slot it sits in. A memory carried from
        // one slot to another takes its setting along; a memory that was not on the bar a moment
        // ago arrives switched off, whatever the slot it lands in was doing.
        //
        // So each slot is answered by asking where its memory just came from, and that question is
        // put to the *previous* bar for every slot before any of them is written back. Resolving
        // them one at a time would let the first write become the second one's answer - two
        // memories trading places would then both end up with whichever state was read first.
        //
        // An empty slot is passed over rather than treated as a change. A slot is momentarily
        // empty while a memory is being moved, and on a zone change the whole hero is gone for a
        // few frames; the control is hidden while a slot is empty anyway, and a memory put back
        // later is supposed to bring its setting with it.
        private void TrackEquipped(Hero hero)
        {
            var skill = hero != null ? hero.Skill : null;
            if (skill == null) return;

            bool changed = false;
            for (int i = 0; i < Slots.Length; i++)
            {
                var equipped = skill.GetSkill(Slots[i]);
                _current[i] = ActorCheck.IsNullOrInactive(equipped) ? null : equipped;
                if (_current[i] != null && (!_seen[i] || _equipped[i] != _current[i])) changed = true;
            }

            if (!changed) return;

            // Read every state before writing any of them, for the reason in the comment above.
            for (int i = 0; i < Slots.Length; i++) _wasOn[i] = IsOn(Slots[i]);

            bool wrote = false;
            for (int i = 0; i < Slots.Length; i++)
            {
                var equipped = _current[i];
                if (equipped == null) continue;
                if (_seen[i] && _equipped[i] == equipped) continue;

                int from = PreviousSlotOf(equipped);

                // A slot being looked at for the first time keeps what was saved for it: that is
                // the frame the mod loads on, and the frame a continued run comes up on, and in
                // both the stored setting already belongs to the memory that is there.
                bool on = from >= 0 ? _wasOn[from]
                        : _seen[i] ? false
                        : _wasOn[i];

                wrote |= Assign(Slots[i], on);
            }

            wrote |= Commit();

            // One save for the whole pass rather than one per slot, since a move writes two.
            if (wrote) SaveConfigsToDisk();
        }

        // Which slot held this memory a moment ago, or -1 if it was not on the bar at all.
        private int PreviousSlotOf(SkillTrigger skill)
        {
            for (int i = 0; i < Slots.Length; i++)
                if (_seen[i] && _equipped[i] == skill) return i;
            return -1;
        }

        // Returns whether it switched anything off.
        private bool Commit()
        {
            bool wrote = false;
            for (int i = 0; i < Slots.Length; i++)
            {
                if (_current[i] != null)
                {
                    _equipped[i] = _current[i];
                    _seen[i] = true;
                    continue;
                }

                // An emptied slot goes on remembering what was in it, which is what lets a memory
                // dropped and picked up again arrive with its setting intact.
                //
                // Unless that memory has turned up in another slot. Then it lives there and took
                // its setting with it, so this slot is left holding neither - and the setting has
                // to go, or the next memory dropped in here would inherit it.
                if (_seen[i] && IsOnBar(_equipped[i]))
                {
                    _equipped[i] = null;
                    wrote |= Assign(Slots[i], false);
                }

                // _seen stays true either way. Clearing it would make the next memory to arrive
                // look like the first one this slot has ever held, which is the one case that is
                // allowed to keep a setting it did not earn.
            }
            return wrote;
        }

        private bool IsOnBar(SkillTrigger skill)
        {
            if (skill == null) return false;
            for (int i = 0; i < Slots.Length; i++)
                if (_current[i] == skill) return true;
            return false;
        }

        // ----- autocast -------------------------------------------------------------

        // Whether the slot has a control on screen at all.
        private bool IsShown(HeroSkillLocation slot)
        {
            switch (slot)
            {
                case HeroSkillLocation.Q: return config.showForQ;
                case HeroSkillLocation.W: return config.showForW;
                case HeroSkillLocation.E: return config.showForE;
                case HeroSkillLocation.R: return config.showForR;
                default: return false;
            }
        }

        // Whether its control is switched on.
        private bool IsOn(HeroSkillLocation slot)
        {
            switch (slot)
            {
                case HeroSkillLocation.Q: return config.castQ;
                case HeroSkillLocation.W: return config.castW;
                case HeroSkillLocation.E: return config.castE;
                case HeroSkillLocation.R: return config.castR;
                default: return false;
            }
        }

        // Both, because a hidden control cannot be switched off and so must not keep firing.
        private bool IsCasting(HeroSkillLocation slot)
        {
            return IsShown(slot) && IsOn(slot);
        }

        private void SetOn(HeroSkillLocation slot, bool value)
        {
            if (Assign(slot, value)) SaveConfigsToDisk();
        }

        // The write on its own, so that a pass changing several slots can save once at the end
        // rather than once per slot. Returns whether anything actually moved.
        private bool Assign(HeroSkillLocation slot, bool value)
        {
            if (IsOn(slot) == value) return false;

            switch (slot)
            {
                case HeroSkillLocation.Q: config.castQ = value; break;
                case HeroSkillLocation.W: config.castW = value; break;
                case HeroSkillLocation.E: config.castE = value; break;
                case HeroSkillLocation.R: config.castR = value; break;
                default: return false;
            }
            return true;
        }

        private bool TryCast(Hero hero, SkillTrigger skill)
        {
            if (ActorCheck.IsNullOrInactive(skill)) return false;

            var cfg = skill.currentConfig;
            if (cfg == null || !cfg.isActive) return false;

            if (config.skipHoldSkills && IsHoldSkill(skill)) return false;

            // CanBeCast already accounts for cooldown, charges, minimum delay, mana and locks,
            // so there is no reason to recompute any of that here.
            if (!skill.CanBeCast()) return false;
            if (!skill.CanBeReserved()) return false;

            // Do not queue a second cast of a skill that is already queued.
            if (hero.Control.queuedActions.Any(a => a is ActionCast ac && ac.trigger == skill)) return false;

            if (cfg.castMethod.type == CastMethodType.None)
            {
                Cast(hero, skill, new CastInfo(hero));
                return true;
            }

            var target = FindTarget(hero, cfg);
            if (target == null) return false;

            // Built-in aim prediction, the same one the monster AI uses, so projectiles
            // actually lead a moving target.
            Cast(hero, skill, skill.GetPredictedCastInfoToTarget(target));
            return true;
        }

        // ----- hold-to-charge detection ---------------------------------------------

        private static readonly Dictionary<Type, bool> HoldSkillCache = new Dictionary<Type, bool>();

        // A hold skill charges while its button is down and fires when it is released.
        // Autocast has no button to release, so all it can do is misfire one.
        //
        // The charge itself is described by a ChargingChannelData, which lives on whatever the
        // skill spawns rather than on the trigger, so the spawned thing's type is what has to
        // be inspected. AssetRef carries the type name as plain metadata, which means this can
        // be answered without loading the asset at all. Cached per skill type either way.
        private static bool IsHoldSkill(SkillTrigger skill)
        {
            var key = skill.GetType();
            if (HoldSkillCache.TryGetValue(key, out bool cached)) return cached;

            bool isHold = false;
            var configs = skill.configs;
            if (configs != null)
            {
                foreach (var cfg in configs)
                {
                    if (cfg == null) continue;

                    var spawned = ResolveType(cfg.spawnedInstanceRef.typeAssemblyQualifiedName,
                                              cfg.spawnedInstanceRef.typeName);
                    if (HasChargingChannel(spawned)) { isHold = true; break; }

                    // A few of them charge from a status effect instead of an ability instance.
                    var applied = cfg.appliedStatusEffectRef;
                    if (applied != null && HasChargingChannel(applied.GetType())) { isHold = true; break; }
                }
            }

            HoldSkillCache[key] = isHold;
            return isHold;
        }

        private static Type ResolveType(string assemblyQualifiedName, string typeName)
        {
            if (!string.IsNullOrEmpty(assemblyQualifiedName))
            {
                var direct = Type.GetType(assemblyQualifiedName, false);
                if (direct != null) return direct;
            }

            if (!string.IsNullOrEmpty(typeName))
            {
                var db = DewResources.database;
                if (db?.typeNameToType != null && db.typeNameToType.TryGetValue(typeName, out var mapped))
                    return mapped;
            }

            return null;
        }

        private static bool HasChargingChannel(Type type)
        {
            if (type == null) return false;
            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (field.FieldType == typeof(ChargingChannelData)) return true;
            }
            return false;
        }

        private void Cast(Hero hero, SkillTrigger skill, CastInfo info)
        {
            // Solo play and hosting go straight through; a remote client has to ask the server.
            if (hero.isServer)
                hero.Control.Cast(skill, skill.currentConfigIndex, info, false, false);
            else
                hero.Control.CmdCast(skill, skill.currentConfigIndex, info, false, false);
        }

        private Entity FindTarget(Hero hero, TriggerConfig cfg)
        {
            var settings = new CollisionCheckSettings
            {
                sortComparer = CollisionCheckSettings.DistanceFromCenter,
            };

            // The list is pooled, so it has to be handed back.
            var candidates = DewPhysics.OverlapCircleAllEntities(
                out ListReturnHandle<Entity> handle, hero.position, cfg.effectiveRange, settings);
            try
            {
                foreach (var candidate in candidates)
                {
                    if (candidate == null) continue;

                    // The skill's own validator decides what counts as a legal target,
                    // which keeps allies and neutrals out of it per skill.
                    if (cfg.targetValidator == null || !cfg.targetValidator.Evaluate(hero, candidate)) continue;
                    if (candidate.Visual != null && candidate.Visual.isSpawning) continue;
                    if (Time.time - candidate.creationTime < config.targetDetectDelay) continue;

                    return candidate;
                }
            }
            finally
            {
                handle.Return();
            }

            return null;
        }

        // ----- on-screen toggles ----------------------------------------------------

        private void SyncToggles()
        {
            var buttons = UI_InGame_SkillButtons.softInstance;
            if (config == null || buttons == null || buttons.skillButtons == null)
            {
                DestroyToggles();
                return;
            }

            // Rebuild whenever the HUD has been torn down and remade, which happens on
            // zone changes. Cheap to verify, and it keeps the toggles from vanishing.
            if (!IsIntact(buttons))
            {
                DestroyToggles();
                BuildToggles(buttons);
            }

            var localHero = DewPlayer.local != null ? DewPlayer.local.hero : null;
            var heroSkill = localHero != null ? localHero.Skill : null;

            // What the player is doing with the skill bar decides both questions here.
            //
            // Regular is the inspect mode held on a key, where the cells are already the thing
            // being looked at: the only moment these controls should answer a click, so that one
            // aimed at a skill during a fight cannot land on a toggle by mistake.
            //
            // The rest - equipping an essence, swapping a memory, selling, a shrine - put their
            // own prompts over the cells, in the same place these sit, so they get out of the way
            // entirely and come back afterwards.
            var editing = EditSkillManager.softInstance;
            var mode = editing != null ? editing.mode : EditSkillManager.ModeType.None;
            bool inspecting = mode == EditSkillManager.ModeType.Regular;
            bool overlaid = mode != EditSkillManager.ModeType.None && !inspecting;

            foreach (var entry in _toggles)
            {
                var equipped = heroSkill != null ? heroSkill.GetSkill(entry.slot) : null;

                // No control for a slot the settings hide, and none for an empty slot either -
                // better than one offering to automate something that is not there.
                bool wanted = !overlaid
                              && IsShown(entry.slot)
                              && !ActorCheck.IsNullOrInactive(equipped);
                if (entry.control.gameObject.activeSelf != wanted)
                    entry.control.gameObject.SetActive(wanted);
                if (!wanted) continue;

                entry.control.SetInteractive(inspecting);

                // A hold skill shows the locked icon rather than a control that can be switched
                // on and then silently does nothing.
                bool isHold = config.skipHoldSkills && IsHoldSkill(equipped);
                entry.control.SetState(isHold ? AutoCastToggle.State.Locked
                                       : IsOn(entry.slot) ? AutoCastToggle.State.On
                                       : AutoCastToggle.State.Off);
            }
        }

        private bool IsIntact(UI_InGame_SkillButtons buttons)
        {
            if (_toggles.Count == 0) return false;
            foreach (var entry in _toggles)
            {
                if (entry.control == null || entry.button == null) return false;

                // Beside the cell, not inside it - see AutoCastToggle.Follow.
                if (entry.control.transform.parent != entry.button.transform.parent) return false;
            }
            return true;
        }

        private void BuildToggles(UI_InGame_SkillButtons buttons)
        {
            foreach (var button in buttons.skillButtons)
            {
                if (button == null) continue;
                if (!Slots.Contains(button.skillType)) continue;

                var anchor = button.transform as RectTransform;
                if (anchor == null || anchor.parent == null) continue;

                var slot = button.skillType;
                var control = AutoCastToggle.Create(anchor, "AutoCastToggle_" + slot);
                control.onClicked = () => SetOn(slot, !IsOn(slot));

                _toggles.Add(new SlotToggle { slot = slot, button = button, control = control });
            }
        }

        private void DestroyToggles()
        {
            foreach (var entry in _toggles)
            {
                if (entry.control != null) Destroy(entry.control.gameObject);
            }
            _toggles.Clear();
        }
    }
}
