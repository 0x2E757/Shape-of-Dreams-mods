using System.Collections.Generic;
using UnityEngine;

namespace TransparentEffects
{
    // Every public field of a ModConfig subclass gets a widget built for it automatically
    // (ModConfig.BuildWidgets -> DewGUI.CreateWidgetsForObject), so the field list is the settings
    // screen. Values live under <persistentDataPath>/QuickSave/Mods/<modId>/.
    //
    // Two numbers, and the split between them is the whole mod. 1 is the game untouched, 0 is
    // gone; both default to 1, so installing it changes nothing until you say what you want.
    //
    // **The second one is not the game's own setting again.** Options -> Gameplay already carries
    // "reduce other players' effects" in five steps, and this does not replace or read it - the
    // two multiply. What this adds there is a continuous value instead of five steps; what it adds
    // that the game has no answer for at all is the first row, because your own effects are never
    // toned down by anything the game ships.
    public class TransparentEffectsConfig : ModConfig
    {
        [Range(0f, 1f)] public float myOwnEffects = 1f;
        [Range(0f, 1f)] public float otherPlayersEffects = 1f;

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
                [Dew.NicifyVariableName(nameof(myOwnEffects))] = Localization.Get(Localization.Mine),
                [Dew.NicifyVariableName(nameof(otherPlayersEffects))] = Localization.Get(Localization.Others),
            };

            Shared.SettingsRows.Polish(parent, firstOwnRow, LabelWidth, InputWidth, translated);
        }
    }

    // Named TransparentEffectsMod rather than TransparentEffects for the reason DevTools is named
    // DevToolsMod: a class sharing the name of its namespace cannot be referred to from a sibling
    // file without qualifying every use of it. The loader takes any ModBehaviour in the assembly
    // and does not care what it is called.
    //
    //     Dimming.cs        two variants of the game's own kind, and what they do to a prefab
    //     VariantChoice.cs  which of them a spawned effect gets, if either
    //
    // **Nobody else needs it, and it changes nobody else's game.** A variant is chosen on the
    // machine that instantiates the effect, from that machine's own point of view - which player
    // is local, who the camera is following - so this is a decision each client makes alone and
    // sends nowhere. Nothing about the effect's behaviour changes either: it is the same actor at
    // the same place doing the same damage, drawn with lower alpha.
    public class TransparentEffectsMod : ModBehaviour
    {
        public TransparentEffectsConfig config = new TransparentEffectsConfig();

        // What the patch reads, and the only thing it holds. Patch methods are static and a
        // ModBehaviour is not, so a patch reaching for the live mod would be keeping a reference
        // to something the loader can destroy under it. Null means no mod, which is also the
        // answer during the frames between a reload destroying one copy and starting the next.
        public static TransparentEffectsConfig Live;

        private static readonly Shared.ConfigFieldWidgets Widgets =
            new Shared.ConfigFieldWidgets(typeof(TransparentEffectsConfig));

        private void Awake()
        {
            // Assigned after the load, not before: LoadConfigsToDisk deserialises into a *new*
            // object and writes it over the field, so a reference taken first would be to the
            // defaults and would go on being the defaults for the life of the mod.
            LoadConfigsToDisk();
            Live = config;

            // Before PatchAll, and that order is load-bearing: the patch hands out variant ids and
            // DewResources looks each one up in a dictionary when it builds the variant. An id
            // emitted before its processor is registered is a KeyNotFoundException per effect
            // spawned - caught and logged by the game, so it would arrive as log spam rather than
            // a crash, which is worse.
            Dimming.Register();

            harmony.PatchAll();
            Widgets.Install();
            Debug.Log("[TransparentEffects] loaded: " + mod.metadata.id);
        }

        // Called by the mod manager when Apply is pressed, after the edited values have been
        // copied back onto config. The alpha is baked into a cached copy of the prefab at the
        // moment that copy is made, so a new number means the cache has to go.
        public override void OnConfigChanged()
        {
            Dimming.Rebuild();
        }

        private void OnDestroy()
        {
            Live = null;

            // Patches first, so that nothing can ask for a variant id while the processors behind
            // them are being taken away.
            harmony.UnpatchAll(harmony.Id);
            Dimming.Unregister();

            // DewGUI.fieldBuilders is shared with the game and every other mod, so the entries
            // have to come back out.
            Widgets.Remove();
            Debug.Log("[TransparentEffects] unloaded: " + mod.metadata.id);
        }
    }
}
