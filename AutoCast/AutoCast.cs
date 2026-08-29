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
            if (player == null) return;

            var hero = player.hero;
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
            if (IsOn(slot) == value) return;

            switch (slot)
            {
                case HeroSkillLocation.Q: config.castQ = value; break;
                case HeroSkillLocation.W: config.castW = value; break;
                case HeroSkillLocation.E: config.castE = value; break;
                case HeroSkillLocation.R: config.castR = value; break;
            }
            SaveConfigsToDisk();
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
