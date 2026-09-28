using UnityEngine;

namespace DevTools
{
    // The keys the picker can be put on. A short list rather than KeyCode, which the settings
    // window would render as a dropdown of some three hundred entries. Values are the KeyCode
    // ones, so the two convert by a cast.
    //
    // F12 is left out on purpose: it is Steam's screenshot key, so with the game launched through
    // Steam every toggle would also take a screenshot.
    public enum PickerHotkey
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
        Insert = (int)KeyCode.Insert,
        Home = (int)KeyCode.Home,
        End = (int)KeyCode.End,
        PageUp = (int)KeyCode.PageUp,
        PageDown = (int)KeyCode.PageDown,
        BackQuote = (int)KeyCode.BackQuote,
    }

    public class DevToolsConfig : ModConfig
    {
        // LabelText and Description take compile-time constants and so can only be English, which
        // for a tool nobody else runs is the right trade.
        [ModConfig.LabelText("Picker hotkey")]
        [ModConfig.Description("Opens and closes the essence and memory picker.")]
        public PickerHotkey hotkey = PickerHotkey.F8;

        // Picker state rather than settings: kept across sessions, never edited by hand.
        [HideInInspector] public bool pickerOpen;
        [HideInInspector] public int pickerTab;
        [HideInInspector] public int memoryLevel = 1;
        [HideInInspector] public int gemQuality = 100;
        [HideInInspector] public int targetSlot;
        [HideInInspector] public bool showHidden;
    }

    // A testing tool, never published. Two halves:
    //
    //   - the picker (Picker/), an in-game window listing every essence and every memory the game
    //     has, to drop on the ground or equip into a chosen slot;
    //   - in a Debug build only, an HTTP server on 127.0.0.1 (Server/, Api/) through which an agent
    //     can read the game's state and drive it end to end - the title screen, the lobby, a run,
    //     the result screen - without anyone at the keyboard. docs/devtools.md is its reference.
    //
    // Named DevToolsMod rather than DevTools because a class with its namespace's name cannot be
    // reached from a sibling file without qualifying every use. The loader takes every
    // ModBehaviour subclass in the assembly, so the name is ours to choose.
    public class DevToolsMod : ModBehaviour
    {
        public DevToolsConfig config = new DevToolsConfig();

        public static DevToolsMod Instance { get; private set; }

        private PickerWindow _picker;

        private void Awake()
        {
            Instance = this;
            LoadConfigsToDisk();

            // A key from before this list existed (the old panel defaulted to F12) loads as a bare
            // number, which the settings dropdown cannot show.
            if (!System.Enum.IsDefined(typeof(PickerHotkey), config.hotkey))
            {
                config.hotkey = PickerHotkey.F8;
                SaveConfigsToDisk();
            }

            // The input patches: the picker needs clicks and keys over it kept away from the hero,
            // and the server's virtual mouse needs the game's cursor reads answered from it.
            harmony.PatchAll();

            // On the mod's own object, so that both go when the mod does.
            _picker = gameObject.AddComponent<PickerWindow>();
            _picker.Init(config, SaveConfigsToDisk);

#if DEBUG
            gameObject.AddComponent<HttpServer>();
#endif

            Debug.Log($"[DevTools] loaded: {mod.metadata.id} - {config.hotkey} opens the picker");
        }

        private void OnDestroy()
        {
            InputBlock.Reset();
            VirtualMouse.Release();

            // Pass the id. The template's bare UnpatchAll() takes out every patch in the process,
            // other mods' included.
            harmony.UnpatchAll(harmony.Id);

            if (Instance == this) Instance = null;
            Debug.Log("[DevTools] unloaded: " + mod.metadata.id);
        }

        public void Save() => SaveConfigsToDisk();
    }
}
