using System.Collections.Generic;
using UnityEngine;

namespace AreMyGemsCompatible
{
    // Every public field of a ModConfig subclass gets a widget built for it automatically
    // (ModConfig.BuildWidgets -> DewGUI.CreateWidgetsForObject), so the field list is the settings
    // screen. Values live under <persistentDataPath>/QuickSave/Mods/<modId>/.
    //
    // Two switches, and neither of them is a threshold: what counts as a dead essence is not a
    // matter of taste, so the only choice offered is where to be told about it.
    public class AreMyGemsCompatibleConfig : ModConfig
    {
        public bool showSlotBadge = true;
        public bool showTooltipWarning = true;

        private const float LabelWidth = 400f;
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
            // Dew.NicifyVariableName(field.Name), so rows are found by that text and rewritten -
            // which survives fields being reordered, and headers appearing between them.
            var translated = new Dictionary<string, string>
            {
                [Dew.NicifyVariableName(nameof(showSlotBadge))] = Localization.Get(Localization.SettingBadge),
                [Dew.NicifyVariableName(nameof(showTooltipWarning))] = Localization.Get(Localization.SettingTooltip),
            };

            Shared.SettingsRows.Polish(parent, firstOwnRow, LabelWidth, InputWidth, translated);
        }
    }

    // Warns about an essence that can never fire in the memory it is socketed into.
    //
    //     MemoryFacts.cs   what a memory does, read out of the game's own English data dump
    //     GemTriggers.cs   what an essence waits for, read off the live type by reflection
    //     Verdict.cs       the pairing, and the two ways an essence is saved from the verdict
    //     TooltipWarning.cs  the line under the essence's description
    //     SlotBadge.cs     the mark on the slot
    //
    // **Nobody else needs it, and it changes nobody else's game.** Nothing here touches an actor,
    // a stat or a network message: it reads what is already equipped and draws a sentence. It is
    // also, deliberately, quiet - the notes this mod was built from are emphatic that a warning
    // in the wrong direction is worse than no warning, so an essence is left alone unless
    // everything about it is waiting on the memory and the memory is known never to oblige.
    //
    // Named AreMyGemsCompatibleMod rather than AreMyGemsCompatible for the reason DevTools is
    // named DevToolsMod: a class sharing the name of its namespace cannot be referred to from a
    // sibling file without qualifying every use of it. The loader takes any ModBehaviour in the
    // assembly and does not care what it is called.
    public class AreMyGemsCompatibleMod : ModBehaviour
    {
        public AreMyGemsCompatibleConfig config = new AreMyGemsCompatibleConfig();

        // What the patches read, and the only thing they hold. Patch methods are static and a
        // ModBehaviour is not, so a patch reaching for the live mod would be keeping a reference
        // to something the loader can destroy under it. Null means no mod, which is also the
        // answer during the frames between a reload destroying one copy and starting the next.
        public static AreMyGemsCompatibleConfig Live;

        private static readonly Shared.ConfigFieldWidgets Widgets =
            new Shared.ConfigFieldWidgets(typeof(AreMyGemsCompatibleConfig));

        private void Awake()
        {
            // Assigned after the load, not before: LoadConfigsToDisk deserialises into a *new*
            // object and writes it over the field, so a reference taken first would be to the
            // defaults and would go on being the defaults for the life of the mod.
            LoadConfigsToDisk();
            Live = config;

            harmony.PatchAll();
            Widgets.Install();
            Debug.Log("[AreMyGemsCompatible] loaded: " + mod.metadata.id);
        }

        private void OnDestroy()
        {
            Live = null;

            // Patches first, so nothing can ask for a verdict while what answers it is being
            // taken away.
            harmony.UnpatchAll(harmony.Id);

            // The marks are components this mod added to the game's own slot widgets, and those
            // widgets outlive it.
            SlotMark.RemoveAll();

            // So does the sprite asset the tooltip's inline mark was hung off. Left in place, its
            // fallback list would keep a destroyed asset in it for the rest of the session.
            TooltipSprite.Detach();

            // Both caches are keyed by types from assemblies that stay loaded, but a reload should
            // re-read the game's data rather than trust what a previous copy of the mod parsed.
            GemTriggers.ClearCache();
            MemoryData.Reset();

            // DewGUI.fieldBuilders is shared with the game and every other mod, so the entries
            // have to come back out.
            Widgets.Remove();
            Debug.Log("[AreMyGemsCompatible] unloaded: " + mod.metadata.id);
        }
    }
}
