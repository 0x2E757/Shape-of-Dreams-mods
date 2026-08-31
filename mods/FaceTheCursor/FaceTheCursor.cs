using System.Collections.Generic;
using UnityEngine;

namespace FaceTheCursor
{
    // Every public field of a ModConfig subclass gets a widget built for it automatically
    // (ModConfig.BuildWidgets -> DewGUI.CreateWidgetsForObject), so the field list is the settings
    // screen. Values live under <persistentDataPath>/QuickSave/Mods/<modId>/.
    //
    // Three fields, and the split between the first two is the whole design. Standing still and
    // moving are separate settings because they are separate bargains: standing still, the game
    // has nothing to say about which way the hero points and the mod is pure gain; moving, it
    // costs an animation. There is one walk cycle and no strafe or backpedal blend - the animator
    // is given walkSpeedMultiplier and nothing about direction - so a hero that keeps facing the
    // pointer while it runs the other way moonwalks. Some people want exactly that and some cannot
    // watch it, which is what makes it a setting rather than a decision.
    public class FaceTheCursorConfig : ModConfig
    {
        public bool whileStandingStill = true;
        public bool whileMoving = true;

        // How near the pointer has to be before the hero stops taking it as a direction, in world
        // units - about one hero's width. Under it there is no angle worth reading, only the noise
        // of the pointer crossing the hero's own feet, and the hero would spin on the spot.
        [Range(0f, 3f)] public float minCursorDistance = 0.5f;

        private const float LabelWidth = 400f;
        private const float InputWidth = 120f;

        // Each setting is a horizontal row of label then control, and the game sizes both to their
        // contents, which staggers the rows twice over. Pinning both widths lines the column up.
        // The label column is wider here than in the other mods because these three rows are
        // sentences rather than names.
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
                [Dew.NicifyVariableName(nameof(whileStandingStill))] = Localization.Get(Localization.Standing),
                [Dew.NicifyVariableName(nameof(whileMoving))] = Localization.Get(Localization.Moving),
                [Dew.NicifyVariableName(nameof(minCursorDistance))] = Localization.Get(Localization.MinDistance),
            };

            Shared.SettingsRows.Polish(parent, firstOwnRow, LabelWidth, InputWidth, translated);
        }
    }

    // Named FaceTheCursorMod rather than FaceTheCursor for the reason DevTools is named
    // DevToolsMod: a class sharing the name of its namespace cannot be referred to from a sibling
    // file without qualifying every use of it. The loader takes any ModBehaviour in the assembly
    // and does not care what it is called.
    //
    // **Nobody else needs it, and it changes nobody else's hero.** The angle is decided on the
    // machine that owns the hero and sent out with its position, so a client running this alone
    // turns the way it means to on every screen in the game, and two players who both have it see
    // each other aim without either of them sending anything the game does not already send. A
    // player who does not have it is neither asked for anything nor moved by anyone: their hero
    // goes on facing the way it walks, on every screen including the modded ones.
    public class FaceTheCursorMod : ModBehaviour
    {
        public FaceTheCursorConfig config = new FaceTheCursorConfig();

        // What the patch reads, and the only thing it holds. Patch methods are static and a
        // ModBehaviour is not, so a patch reaching for the live mod would be keeping a reference
        // to something the loader can destroy under it. Null means no mod, which is also the
        // answer during the frames between a reload destroying one copy and starting the next.
        public static FaceTheCursorConfig Live;

        private static readonly Shared.ConfigFieldWidgets Widgets =
            new Shared.ConfigFieldWidgets(typeof(FaceTheCursorConfig));

        private void Awake()
        {
            // Assigned after the load, not before: LoadConfigsToDisk deserialises into a *new*
            // object and writes it over the field, so a reference taken first would be to the
            // defaults and would go on being the defaults for the life of the mod.
            LoadConfigsToDisk();
            Live = config;

            harmony.PatchAll();
            Widgets.Install();
            Debug.Log("[FaceTheCursor] loaded: " + mod.metadata.id);
        }

        private void OnDestroy()
        {
            Live = null;

            // Pass the id. The stock template's bare UnpatchAll() takes out every patch in the
            // process, other mods' included.
            harmony.UnpatchAll(harmony.Id);

            // DewGUI.fieldBuilders is shared with the game and every other mod, so the entries
            // have to come back out.
            Widgets.Remove();
            Debug.Log("[FaceTheCursor] unloaded: " + mod.metadata.id);
        }
    }
}
