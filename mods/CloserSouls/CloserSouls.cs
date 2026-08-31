using System.Collections.Generic;
using UnityEngine;

namespace CloserSouls
{
    // Every public field of a ModConfig subclass gets a widget built for it automatically
    // (ModConfig.BuildWidgets -> DewGUI.CreateWidgetsForObject), so the field list is the settings
    // screen. Values live under <persistentDataPath>/QuickSave/Mods/<modId>/.
    //
    // Three numbers, and between them they are the whole rule: where the first soul of a region
    // goes, how much further each one after it goes, and where that stops. The defaults - 0, 1, 2 -
    // are the game's own escalation shifted down to start at your feet: die once and the party can
    // pick you up where you fell; keep dying and the walk comes back.
    //
    // Distances are in rooms, counted along the map's own connections, which is the same unit the
    // game states its own lostSoulDistance in.
    public class CloserSoulsConfig : ModConfig
    {
        [Range(0, 5)] public int roomsAwayOnFirstDeath = 0;
        [Range(0, 3)] public int extraRoomsPerDeath = 1;
        [Range(0, 8)] public int maxRoomsAway = 2;

        private const float LabelWidth = 460f;
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
                [Dew.NicifyVariableName(nameof(roomsAwayOnFirstDeath))] = Localization.Get(Localization.First),
                [Dew.NicifyVariableName(nameof(extraRoomsPerDeath))] = Localization.Get(Localization.Extra),
                [Dew.NicifyVariableName(nameof(maxRoomsAway))] = Localization.Get(Localization.Max),
            };

            Shared.SettingsRows.Polish(parent, firstOwnRow, LabelWidth, InputWidth, translated);
        }
    }

    // Named CloserSoulsMod rather than CloserSouls for the reason DevTools is named DevToolsMod: a
    // class sharing the name of its namespace cannot be referred to from a sibling file without
    // qualifying every use of it. The loader takes any ModBehaviour in the assembly and does not
    // care what it is called.
    //
    // **This one has to be on the host, and only on the host.** Where a soul goes is decided in
    // Se_HeroKnockedOut.CheckAndAddHeroSoul, which runs behind an isServer check and writes to a
    // SyncVar list; a guest running this mod is patching a method its own machine never calls, and
    // a guest without it sees the host's placement like everyone else. So one installed copy
    // changes the run for the whole party, and a party whose host does not have it is playing the
    // stock game no matter who else does.
    public class CloserSoulsMod : ModBehaviour
    {
        public CloserSoulsConfig config = new CloserSoulsConfig();

        // What the patch reads, and the only thing it holds. Patch methods are static and a
        // ModBehaviour is not, so a patch reaching for the live mod would be keeping a reference
        // to something the loader can destroy under it. Null means no mod, which is also the
        // answer during the frames between a reload destroying one copy and starting the next.
        public static CloserSoulsConfig Live;

        private static readonly Shared.ConfigFieldWidgets Widgets =
            new Shared.ConfigFieldWidgets(typeof(CloserSoulsConfig));

        private void Awake()
        {
            // Assigned after the load, not before: LoadConfigsToDisk deserialises into a *new*
            // object and writes it over the field, so a reference taken first would be to the
            // defaults and would go on being the defaults for the life of the mod.
            LoadConfigsToDisk();
            Live = config;

            harmony.PatchAll();
            Widgets.Install();
            Debug.Log("[CloserSouls] loaded: " + mod.metadata.id);
        }

        private void OnDestroy()
        {
            Live = null;
            SoulPlacement.Forget();

            // Pass the id. The stock template's bare UnpatchAll() takes out every patch in the
            // process, other mods' included.
            harmony.UnpatchAll(harmony.Id);

            // DewGUI.fieldBuilders is shared with the game and every other mod, so the entries
            // have to come back out.
            Widgets.Remove();
            Debug.Log("[CloserSouls] unloaded: " + mod.metadata.id);
        }
    }
}
