using UnityEngine;

namespace DevTools
{
    // The keys the panel can be put on. A curated list rather than KeyCode itself, which the
    // settings window would happily render - enums get a dropdown from the stock field builder -
    // but as a scrolling list of some three hundred entries, most of which are mouse buttons,
    // joystick axes and keys no keyboard has.
    //
    // Values are the KeyCode ones, so the two convert by a cast rather than by a lookup.
    //
    // Nothing here collides with the game's own bindings: QWER, space, tab and escape are all
    // spoken for, and the console has a binding of its own that the player can change.
    public enum PanelHotkey
    {
        F1 = (int)KeyCode.F1,
        F2 = (int)KeyCode.F2,
        F3 = (int)KeyCode.F3,
        F4 = (int)KeyCode.F4,
        F5 = (int)KeyCode.F5,
        F6 = (int)KeyCode.F6,
        F7 = (int)KeyCode.F7,
        F8 = (int)KeyCode.F8,
        F9 = (int)KeyCode.F9,
        F10 = (int)KeyCode.F10,
        F11 = (int)KeyCode.F11,
        F12 = (int)KeyCode.F12,
        Insert = (int)KeyCode.Insert,
        Delete = (int)KeyCode.Delete,
        Home = (int)KeyCode.Home,
        End = (int)KeyCode.End,
        PageUp = (int)KeyCode.PageUp,
        PageDown = (int)KeyCode.PageDown,
        Backslash = (int)KeyCode.Backslash,
        BackQuote = (int)KeyCode.BackQuote,
    }

    // A testing mod for the other two, and deliberately not a published one. It exists because
    // getting a hero to level 20 with a fully upgraded memory, or reaching the result screen, is
    // several minutes of play per attempt - and looking at a UI at seven essence slots means
    // doing that over and over.
    //
    // The panel is a rebuild of tools the other mods carried while they were being written and
    // then had removed, which the README records the shape of. Nothing here is meant to ship.
    public class DevToolsConfig : ModConfig
    {
        // The one real setting. LabelText and Description both take compile-time constants and so
        // can only ever be English, which for a tool nobody else runs is the right trade - the
        // published mods go the long way round precisely because they are published.
        [ModConfig.LabelText("Panel hotkey")]
        [ModConfig.Description("F12 is also Steam's screenshot key by default, so every toggle " +
                               "takes a screenshot when the game is launched through Steam.")]
        public PanelHotkey hotkey = PanelHotkey.F12;

        // State rather than settings, and hidden for the same reason AutoCast hides its toggle
        // states: they belong in the save file and have no business being edited by hand.
        [HideInInspector] public bool panelOpen = true;
        [HideInInspector] public int itemLevel = 1;

        // Which node the room-state repair acts on. Kept with the rest of the panel state so that
        // a number dialled in before opening the map is still there afterwards.
        [HideInInspector] public int roomNode;

        // Kept with the other panel state rather than offered as a setting, for the same reason:
        // the panel is the interface. It does persist, so a session that ended with it on starts
        // with it on - which is the right way round for something turned on to get through a map
        // quickly, and is visible on the panel either way.
        [HideInInspector] public bool godMode;
    }

    // Named DevToolsMod rather than DevTools, unlike its two siblings, because a class with the
    // same name as its namespace cannot be reached from a sibling file without qualifying every
    // use of it. The loader does not care: it takes every ModBehaviour subclass in the assembly.
    public class DevToolsMod : ModBehaviour
    {
        public DevToolsConfig config = new DevToolsConfig();

        private DevPanel _panel;

        private void Awake()
        {
            LoadConfigsToDisk();

            // The first Harmony in this mod, and it earns its keep for one thing: a run ended from
            // the panel has to be worth no mastery, and the only place that can be said is inside
            // the function that works out how much a run earned. See ScorelessRun.cs.
            harmony.PatchAll();

            Debug.Log($"[DevTools] loaded: {mod.metadata.id} - {config.hotkey} toggles the panel");
        }

        private void OnDestroy()
        {
            // Before the panel goes, since it is the panel that would otherwise have handed the
            // bonus back. A hero left with god mode on and nothing left to switch it off would
            // keep it for the rest of the run.
            GodMode.Reset();
            ScorelessRun.Disarm();

            // Pass the id. The stock template's bare UnpatchAll() takes out every patch in the
            // process, other mods' included.
            harmony.UnpatchAll(harmony.Id);

            // The canvas is DontDestroyOnLoad, so nothing else is ever going to take it away.
            if (_panel != null) Destroy(_panel.gameObject);
            Debug.Log("[DevTools] unloaded: " + mod.metadata.id);
        }

        private void Update()
        {
            if (config == null) return;

            // Read every frame rather than cached, so changing it in the settings window takes
            // effect on the way out of it.
            if (Input.GetKeyDown((KeyCode)config.hotkey))
            {
                config.panelOpen = !config.panelOpen;
                SaveConfigsToDisk();
            }

            if (_panel == null)
            {
                // Built on the first frame the game's widget prefabs can be cloned, which is not
                // necessarily the frame the mod loads on.
                if (!config.panelOpen) return;
                _panel = DevPanel.Create(config, SaveConfigsToDisk);
                if (_panel == null) return;
            }

            if (_panel.gameObject.activeSelf != config.panelOpen)
                _panel.gameObject.SetActive(config.panelOpen);
        }
    }
}
