using System.Collections.Generic;
using UnityEngine;

namespace ControlledMerge
{
    // Every public field of a ModConfig subclass gets a widget built for it automatically
    // (ModConfig.BuildWidgets -> DewGUI.CreateWidgetsForObject), so the field list is the settings
    // screen. Values live under <persistentDataPath>/QuickSave/Mods/<modId>/.
    //
    // Four numbers, each what is taken off an essence's scaling values, in percent. Two for how
    // many memories hold the same essence, two for how many copies of it one memory holds; an
    // essence that is both shared and doubled up has both taken off, one after the other.
    public class ControlledMergeConfig : ModConfig
    {
        [Range(0, 90)] public int twoMemoriesCut = 30;
        [Range(0, 90)] public int threeMemoriesCut = 40;
        [Range(0, 90)] public int twoCopiesCut = 25;
        [Range(0, 90)] public int threeCopiesCut = 35;

        public bool mergeByChoosingItsSlot = true;
        public bool showTooltipLine = true;

        private const float LabelWidth = 520f;
        private const float InputWidth = 120f;

        // Each setting is a horizontal row of label then control, and the game sizes both to their
        // contents, which staggers the rows twice over. Pinning both widths lines the column up.
        public override void BuildWidgets(Transform parent, out SafeAction onChanged,
                                          out SafeAction requestUpdate)
        {
            int firstOwnRow = parent.childCount;
            base.BuildWidgets(parent, out onChanged, out requestUpdate);

            // ModConfig.LabelText would name these rows, but it takes a compile-time constant and
            // so can only ever be one language. The game labels each row with
            // Dew.NicifyVariableName(field.Name), so rows are found by that text and rewritten.
            var translated = new Dictionary<string, string>
            {
                [Dew.NicifyVariableName(nameof(twoMemoriesCut))] = Localization.Word(Localization.SettingTwoMemories),
                [Dew.NicifyVariableName(nameof(threeMemoriesCut))] = Localization.Word(Localization.SettingThreeMemories),
                [Dew.NicifyVariableName(nameof(twoCopiesCut))] = Localization.Get(Localization.SettingTwoCopies),
                [Dew.NicifyVariableName(nameof(threeCopiesCut))] = Localization.Get(Localization.SettingThreeCopies),
                [Dew.NicifyVariableName(nameof(mergeByChoosingItsSlot))] = Localization.Get(Localization.SettingMerge),
                [Dew.NicifyVariableName(nameof(showTooltipLine))] = Localization.Get(Localization.SettingTooltip),
            };

            Shared.SettingsRows.Polish(parent, firstOwnRow, LabelWidth, InputWidth, translated);
        }
    }

    // Picking up an essence you already have no longer merges it into the one you have: it goes to
    // your hand, and you choose a slot for it the way you would for any new essence. So the same
    // essence can be worn more than once - and each copy is weaker for it.
    //
    //     Copies.cs        the three places the game insists on one of each, and merging by choice
    //     SharedEffects.cs what copies do share: Supersymmetry's hold on the hero's health
    //     OwnState.cs      what copies found by type or by owner and so shared: each keeps its own
    //     Diminishing.cs   how much is taken off, and the four ways a value reaches the game
    //     Refresh.cs       essences that write their numbers down once, told when their cut changes
    //     OneCut.cs        essences that multiply two of their values into one effect: one is left whole
    //     Stacking.cs      copies that amplify the same hit, each of them - the game marks a hit by type
    //     Fit.cs           AreMyGemsCompatible's verdict, when it is loaded: copies that never fire do not count
    //     HostSettings.cs  the host's settings, sent to every guest's copy through the game's own synced data
    //     Tooltip.cs       the numbers in the essence's description, and the line under it
    //
    // **The host decides.** Picking up, equipping and every number an essence produces are the
    // server's, so the rules are whatever the host's copy of the mod says; a guest's copy only
    // draws the tooltip, and draws it from the guest's own settings. Merging by choosing a slot
    // needs the player to be the host, since it has no command of its own to send.
    //
    // Named ControlledMergeMod rather than ControlledMerge for the reason DevTools is named
    // DevToolsMod: a class sharing the name of its namespace cannot be referred to from a sibling
    // file without qualifying every use of it.
    public class ControlledMergeMod : ModBehaviour
    {
        public ControlledMergeConfig config = new ControlledMergeConfig();

        // What the patches read, and the only thing they hold. Null means no mod, which is also the
        // answer during the frames between a reload destroying one copy and starting the next.
        public static ControlledMergeConfig Live;

        private static readonly Shared.ConfigFieldWidgets Widgets =
            new Shared.ConfigFieldWidgets(typeof(ControlledMergeConfig));

        private void Awake()
        {
            // Assigned after the load, not before: LoadConfigsToDisk deserialises into a *new*
            // object and writes it over the field.
            LoadConfigsToDisk();
            Live = config;

            harmony.PatchAll();
            Widgets.Install();
            Debug.Log("[ControlledMerge] loaded: " + mod.metadata.id);

#if DEBUG
            try { Audit.Log(); }
            catch (System.Exception e) { Debug.LogWarning("[ControlledMerge] audit failed: " + e); }
#endif
        }

        private void Update()
        {
            Refresh.Tick();
            HostSettings.Publish();
        }

        // Stacks several copies consumed in one frame go once they all have (OwnState.cs, 5).
        private void LateUpdate()
        {
            SharedConsumption.Flush();
        }

        private void OnDestroy()
        {
            HostSettings.Withdraw();
            Live = null;

            // Pass the id. The stock template's bare UnpatchAll() takes out every patch in the
            // process, other mods' included.
            harmony.UnpatchAll(harmony.Id);

            SupersymmetryBase.Forget();
            Refresh.Forget();
            OneCut.Forget();
            SharedConsumption.Flush();
            OwnState.Forget();
            Diminishing.Forget();
            Merge.Forget();
            Fit.Forget();
            Tooltip.Scope.Clear();

            // DewGUI.fieldBuilders is shared with the game and every other mod, so the entries
            // have to come back out.
            Widgets.Remove();
            Debug.Log("[ControlledMerge] unloaded: " + mod.metadata.id);
        }
    }
}
