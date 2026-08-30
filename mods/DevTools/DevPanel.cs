using System;
using System.Collections.Generic;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace DevTools
{
    // The overlay itself. Two pieces of this were learned the expensive way by the tuning panels
    // that used to live in the other two mods, and the README keeps the account of both:
    //
    //   - It gets a Canvas of its own. DewGUI.canvasTransform belongs to the mod config windows
    //     and is only lit while one of them is open, so a panel hung off it renders in the menus
    //     and vanishes on the way into a run. Screen space, a sorting order above everything, its
    //     own GraphicRaycaster, and DontDestroyOnLoad - then nothing the game does to its UI can
    //     reach it.
    //
    //   - Numbers are driven by a - and a + rather than by a slider. DewGUI.widgetSlider is the
    //     prefab the game ships and never uses, and its graphics do not cover their own rect, so
    //     clicks in the middle of one fall through to whatever is behind. A pair of buttons is
    //     less code than persuading it, and for setting a level to exactly 12 it is better input
    //     anyway.
    //
    // Buttons and labels *are* cloned from DewGUI, which is worth it for one reason that has
    // nothing to do with matching the game's art: they arrive with a TMP font already on them.
    internal sealed class DevPanel : MonoBehaviour, IBeginDragHandler, IDragHandler
    {
        private const float PanelWidth = 340f;
        private const float RowHeight = 42f;
        private const float StepButtonWidth = 46f;
        private const float ReadoutWidth = 62f;
        private const float FontSize = 20f;

        // Holding shift moves a number five at a time, because walking a hero to level 20 one
        // click at a time is the sort of thing that makes a tool go unused.
        private const int FastStep = 5;

        private static readonly Color PanelColor = new Color(0.05f, 0.05f, 0.07f, 0.92f);

        private DevToolsConfig _config;

        // ModBehaviour.SaveConfigsToDisk, handed in rather than reached for: the panel has no
        // business knowing which mod owns it.
        private Action _save;

        private RectTransform _box;
        private TextMeshProUGUI _titleText;
        private TextMeshProUGUI _heroLevelText;
        private TextMeshProUGUI _itemLevelText;
        private TextMeshProUGUI _roomNodeText;
        private TextMeshProUGUI _statusText;
        private Button[] _actionButtons;
        private Button _godButton;
        private string _godLabel = "";

        private string _status = "";

        // Null when the game's widget prefabs are not loaded yet, which the caller retries.
        public static DevPanel Create(DevToolsConfig config, Action save)
        {
            if (DewGUI.widgetButton == null || DewGUI.widgetTextLabel == null) return null;

            var root = new GameObject("DevToolsCanvas");
            DontDestroyOnLoad(root);

            var canvas = root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 30000;

            // Without this the panel is authored-size in raw pixels, which is a postage stamp on
            // a 4K screen and half the display at 720p.
            var scaler = root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            root.AddComponent<GraphicRaycaster>();

            var panel = root.AddComponent<DevPanel>();
            panel._config = config;
            panel._save = save;
            panel.Build(root.transform);
            return panel;
        }

        private void Build(Transform root)
        {
            var box = new GameObject("Panel", typeof(RectTransform));
            box.transform.SetParent(root, false);

            _box = (RectTransform)box.transform;
            _box.anchorMin = new Vector2(0f, 1f);
            _box.anchorMax = new Vector2(0f, 1f);
            _box.pivot = new Vector2(0f, 1f);
            _box.anchoredPosition = new Vector2(24f, -24f);
            _box.sizeDelta = new Vector2(PanelWidth, 0f);

            // The background is also the drag handle and the thing that stops clicks reaching the
            // game underneath, so it is the one graphic here that takes raycasts.
            var background = box.AddComponent<Image>();
            background.color = PanelColor;
            background.raycastTarget = true;

            var column = box.AddComponent<VerticalLayoutGroup>();
            column.padding = new RectOffset(12, 12, 12, 12);
            column.spacing = 6f;
            column.childControlWidth = true;
            column.childControlHeight = true;
            column.childForceExpandWidth = true;
            column.childForceExpandHeight = false;

            // The panel is as tall as what is in it; only the width is decided above.
            var fitter = box.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;

            _titleText = Label(_box, "DevTools", FontSize + 2f);

            _heroLevelText = NumberRow(_box, "Hero level", HeroLevelDelta);
            _itemLevelText = NumberRow(_box, "Item level", ItemLevelDelta);
            _roomNodeText = NumberRow(_box, "Room node", RoomNodeDelta);

            // Left interactable whatever the hero is doing, unlike the buttons below it: it is a
            // switch rather than an action, and setting it before a run starts so that the run
            // starts with it on is a perfectly good thing to want.
            _godButton = ActionButton(_box, "", ToggleGodMode);
            LabelGodButton();

            _actionButtons = new[]
            {
                ActionButton(_box, "Spawn random memory", () => DevActions.SpawnMemory(_config.itemLevel)),
                ActionButton(_box, "Spawn random essence", () => DevActions.SpawnEssence(_config.itemLevel)),
                ActionButton(_box, "Knock out hero", DevActions.KillHero),
                ActionButton(_box, "Forget that node's room",
                             () => DevActions.ClearRoomSaveData(_config.roomNode)),
            };

            _tuningButton = ActionButton(_box, "", ToggleTuning);
            BuildTuning(_box);
            LabelTuningButton();

            _statusText = Label(_box, "", FontSize - 4f);
            _statusText.color = new Color(0.7f, 0.75f, 0.8f, 1f);
        }

        // ----- god mode -------------------------------------------------------------
        //
        // Wording rather than a tick glyph, which is the same choice the two toggles in the
        // tuning section already made: the panel's font is the game's, and what it has a glyph
        // for is not something this file gets to decide.

        private string ToggleGodMode()
        {
            _config.godMode = !_config.godMode;
            _save?.Invoke();

            // Applied by the next frame's Update rather than here, so that turning it on with no
            // hero to put it on is not a different code path from turning it on with one.
            return _config.godMode ? "god mode on" : "god mode off";
        }

        private void LabelGodButton()
        {
            if (_godButton == null) return;

            string label = _config.godMode ? "God mode: on" : "God mode: off";

            // On but not granted is the ordinary state in a menu, between runs, and for a guest
            // in someone else's game - all of which look like a switch that did not take unless
            // the button says otherwise.
            if (_config.godMode && !GodMode.IsApplied) label += "  (waiting for a hero)";

            if (label == _godLabel) return;

            _godLabel = label;
            DewGUI.SetText(_godButton.gameObject, label);
        }

        // ----- the gem tuning section -----------------------------------------------
        //
        // Twenty-odd numbers with a - and a + each, and a button that writes them to the log as
        // C# field initialisers. That is the whole shape of the tuning panels the two published
        // mods were built with and then had removed, and the reason to rebuild it here is the
        // reason the README gives for keeping the pattern: guessing geometry from a decompiler and
        // rebuilding between attempts takes far longer than building the panel does.
        //
        // Rows are built from whatever float fields the arrangement turns out to have, so adding a
        // number over there needs no change here.

        private const float TuningRowHeight = 28f;
        private const float TuningFontSize = 15f;
        private const float TuningReadoutWidth = 74f;

        // Fine by default and coarse on shift, the same direction as the integer rows above: shift
        // multiplies the step by five either way.
        private const float TuningStep = 0.01f;

        private GameObject _tuningSection;
        private Button _tuningButton;
        private Button _targetButton;
        private Button _pinButton;
        private bool _pinScoreboard;
        private bool _tuningFilled;
        private bool _tuningNoticeShown;
        private bool _tuningOnSummary = true;
        // Field *names*, not FieldInfos: reloading MoreGemSlots replaces the type these belong to,
        // and a FieldInfo from the previous copy throws when handed an object of the new one.
        private readonly List<(string name, TextMeshProUGUI readout, GameObject row, int page)>
            _tuningRows = new List<(string, TextMeshProUGUI, GameObject, int)>();

        // Twenty-one numbers plus their buttons come to a panel about a hundred pixels taller than
        // a 1080p screen, and the overflow is silent: the panel simply runs off the bottom, and
        // dragging it up to reach the last buttons pushes the first rows off the top. Which is how
        // the one control for the gap between rows managed to be present and unreachable at once.
        //
        // Two pages, split where the numbers already divide: the rows of the extended arrangement,
        // and the counts the game draws itself. Nine and twelve, both of which fit.
        private static readonly string[] SmallCountPrefixes = { "one", "two", "three" };
        private const int PageCount = 2;
        private int _tuningPage;
        private Button _pageButton;

        private static int PageOf(string field)
        {
            foreach (var prefix in SmallCountPrefixes)
                if (field.StartsWith(prefix, StringComparison.Ordinal)) return 1;
            return 0;
        }

        private static string PageName(int page) => page == 0 ? "extended rows" : "counts 1-3";

        private string TargetName => _tuningOnSummary ? "Summary" : "Hud";

        private string ToggleTuning()
        {
            if (_tuningSection == null) return "";
            _tuningSection.SetActive(!_tuningSection.activeSelf);

            // The rows are built once, on the first opening, because there is no reason to carry
            // twenty-odd widgets around for a section most sessions never open.
            if (_tuningSection.activeSelf && !_tuningFilled) FillTuning();

            LabelTuningButton();
            return "";
        }

        // Says what it does and which way it goes, because a button reading only "Gem tuning" in a
        // column of buttons that all do something immediately does not look like one that opens
        // anything.
        private void LabelTuningButton()
        {
            if (_tuningButton == null) return;

            bool open = _tuningSection != null && _tuningSection.activeSelf;
            DewGUI.SetText(_tuningButton.gameObject,
                           (open ? "▾  " : "▸  ") + "Gem slot arrangement");
        }

        private void BuildTuning(Transform parent)
        {
            _tuningSection = new GameObject("Tuning", typeof(RectTransform));
            _tuningSection.transform.SetParent(parent, false);

            var column = _tuningSection.AddComponent<VerticalLayoutGroup>();
            column.spacing = 2f;
            column.childControlWidth = true;
            column.childControlHeight = true;
            column.childForceExpandWidth = true;
            column.childForceExpandHeight = false;

            _tuningSection.SetActive(false);
        }

        private void FillTuning()
        {
            var parent = _tuningSection.transform;

            if (!GemTuning.Available)
            {
                // Not marked as filled, so opening it again after enabling the mod tries once
                // more - but the notice is written only once either way.
                if (!_tuningNoticeShown)
                {
                    _tuningNoticeShown = true;
                    Label(parent, "MoreGemSlots is not loaded", TuningFontSize);
                }
                return;
            }

            _tuningFilled = true;

            _targetButton = ActionButton(parent, "", () =>
            {
                _tuningOnSummary = !_tuningOnSummary;
                return "tuning " + TargetName;
            });

            _pinButton = ActionButton(parent, "", () =>
            {
                _pinScoreboard = !_pinScoreboard;
                return _pinScoreboard ? "" : "scoreboard released";
            });

            _pageButton = ActionButton(parent, "", () =>
            {
                _tuningPage = (_tuningPage + 1) % PageCount;
                ApplyPage();
                return "";
            });

            foreach (var name in GemTuning.FieldNames)
            {
                var captured = name;
                var readout = NumberRow(parent, Dew.NicifyVariableName(name),
                                        delta => GemTuning.Add(_tuningOnSummary, captured,
                                                               delta * TuningStep),
                                        TuningRowHeight, TuningFontSize, TuningReadoutWidth);
                _tuningRows.Add((captured, readout, readout.transform.parent.gameObject,
                                 PageOf(captured)));
            }

            ActionButton(parent, "Log tuning to player.log",
                         () => GemTuning.Dump(_tuningOnSummary, TargetName));
            ActionButton(parent, "Reset to shipped values",
                         () => { GemTuning.Restore(_tuningOnSummary); return TargetName + " reset"; });

            ApplyPage();
        }

        private void ApplyPage()
        {
            foreach (var (_, _, row, page) in _tuningRows)
            {
                bool shown = page == _tuningPage;
                if (row != null && row.activeSelf != shown) row.SetActive(shown);
            }

            if (_pageButton != null)
                DewGUI.SetText(_pageButton.gameObject,
                               "Page: " + PageName(_tuningPage) + "  (click for the rest)");
        }

        private void RefreshTuning()
        {
            if (_tuningSection == null || !_tuningSection.activeSelf || _tuningRows.Count == 0) return;

            if (_targetButton != null)
                DewGUI.SetText(_targetButton.gameObject, "Editing: " + TargetName + "  (click to swap)");

            if (_pinButton != null)
                DewGUI.SetText(_pinButton.gameObject,
                               _pinScoreboard ? "Scoreboard: pinned open" : "Scoreboard: hold Tab");

            foreach (var (name, readout, row, page) in _tuningRows)
            {
                if (page != _tuningPage) continue;   // hidden rows are not worth reading back
                readout.text = GemTuning.Get(_tuningOnSummary, name).ToString("0.##");
            }
        }

        // ----- rows -----------------------------------------------------------------

        private static TextMeshProUGUI Label(Transform parent, string text, float size)
        {
            var label = Instantiate(DewGUI.widgetTextLabel, parent);

            // SetText rather than assigning .text: the prefab carries a DewLocalizedText that
            // would overwrite whatever is written the next time it updated. SetText drops it,
            // after which .text is ours to write.
            DewGUI.SetText(label.gameObject, text);

            label.fontSize = size;
            label.raycastTarget = false;

            var element = label.GetComponent<LayoutElement>();
            if (element == null) element = label.gameObject.AddComponent<LayoutElement>();
            element.minHeight = size + 8f;
            element.preferredHeight = size + 8f;
            element.flexibleWidth = 1f;

            return label;
        }

        // Label, minus, readout, plus. Returns the readout, which the caller keeps to write into.
        // The sizes default to the ones the three controls at the top use; the tuning section
        // overrides them, having twenty-odd rows to fit rather than two.
        private TextMeshProUGUI NumberRow(Transform parent, string caption, Action<int> apply,
                                          float rowHeight = RowHeight, float fontSize = FontSize,
                                          float readoutWidth = ReadoutWidth)
        {
            var row = new GameObject(caption, typeof(RectTransform));
            row.transform.SetParent(parent, false);

            var group = row.AddComponent<HorizontalLayoutGroup>();
            group.spacing = 6f;
            group.childControlWidth = true;
            group.childControlHeight = true;
            group.childForceExpandWidth = false;
            group.childForceExpandHeight = false;
            group.childAlignment = TextAnchor.MiddleLeft;

            var rowSize = row.AddComponent<LayoutElement>();
            rowSize.minHeight = rowHeight;
            rowSize.preferredHeight = rowHeight;

            var label = Label(row.transform, caption, fontSize);
            label.alignment = TextAlignmentOptions.MidlineLeft;

            StepButton(row.transform, "-", () => apply(-Step), rowHeight);

            var readout = Label(row.transform, "0", fontSize);
            readout.alignment = TextAlignmentOptions.Center;
            var readoutSize = readout.GetComponent<LayoutElement>();
            readoutSize.flexibleWidth = 0f;
            readoutSize.minWidth = readoutWidth;
            readoutSize.preferredWidth = readoutWidth;

            StepButton(row.transform, "+", () => apply(Step), rowHeight);

            return readout;
        }

        private static void StepButton(Transform parent, string caption, Action onClick, float rowHeight = RowHeight)
        {
            var button = Instantiate(DewGUI.widgetButton, parent);
            DewGUI.SetText(button.gameObject, caption);

            var element = button.GetComponent<LayoutElement>();
            if (element == null) element = button.gameObject.AddComponent<LayoutElement>();
            element.ignoreLayout = false;
            element.flexibleWidth = 0f;
            element.minWidth = StepButtonWidth;
            element.preferredWidth = StepButtonWidth;
            element.minHeight = rowHeight - 6f;
            element.preferredHeight = rowHeight - 6f;

            button.onClick.AddListener(() => onClick());
        }

        private Button ActionButton(Transform parent, string caption, Func<string> action)
        {
            var button = Instantiate(DewGUI.widgetButton, parent);
            DewGUI.SetText(button.gameObject, caption);

            var element = button.GetComponent<LayoutElement>();
            if (element == null) element = button.gameObject.AddComponent<LayoutElement>();
            element.ignoreLayout = false;
            element.minHeight = RowHeight;
            element.preferredHeight = RowHeight;
            element.flexibleWidth = 1f;

            button.onClick.AddListener(() => _status = action());
            return button;
        }

        private static int Step =>
            Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift) ? FastStep : 1;

        // ----- the numbers ----------------------------------------------------------

        private void HeroLevelDelta(int delta)
        {
            _status = DevActions.SetHeroLevel(DevActions.HeroLevel + delta);
        }

        private void ItemLevelDelta(int delta)
        {
            // Clamped low only. There is no upper bound worth inventing: a memory or an essence
            // above anything the game hands out is a perfectly good thing to want to look at.
            int wanted = Mathf.Max(1, _config.itemLevel + delta);
            if (wanted == _config.itemLevel) return;

            _config.itemLevel = wanted;
            _status = "";
            _save?.Invoke();
        }

        // Steps by one rather than by Step, unlike the two rows above it. A node index is not a
        // magnitude - the difference between node 16 and node 17 is the whole point - so the row
        // counts rather than scales.
        private void RoomNodeDelta(int delta)
        {
            var zone = ZoneManager.softInstance;
            int last = zone != null ? Mathf.Max(0, zone.nodes.Count - 1) : 0;

            int wanted = Mathf.Clamp(_config.roomNode + Math.Sign(delta), 0, last);
            if (wanted == _config.roomNode) return;

            _config.roomNode = wanted;
            _save?.Invoke();

            // The index alone is unusable - nobody knows which number the shop was. So stepping
            // the row says what is under it, which is also the only way to tell a node that holds
            // a remembered room from one that does not.
            _status = zone == null
                    ? wanted.ToString()
                    : wanted + " " + NodeCaption(zone, wanted);
        }

        private static string NodeCaption(ZoneManager zone, int node)
        {
            string room = zone.nodes[node].room;
            if (string.IsNullOrEmpty(room)) room = "(unexplored)";

            return room + (zone.visitedNodesSaveData[node] != null ? " - remembered" : "");
        }

        // ----- per frame ------------------------------------------------------------

        private void Update()
        {
            bool canAct = DevActions.CanAct(out string reason);

            // Written every frame rather than at build time, because the key is a setting now and
            // the title is where anyone would look to find out what it currently is.
            _titleText.text = "DevTools  -  " + _config.hotkey + " to hide";

            _heroLevelText.text = canAct || DevActions.LocalHero != null
                                ? DevActions.HeroLevel + "/" + DevActions.HeroMaxLevel
                                : "-";
            _itemLevelText.text = _config.itemLevel.ToString();
            _roomNodeText.text = _config.roomNode.ToString();

            // Every frame, because a room load replaces the hero and takes the granted bonus with
            // it. Asking for the state that is wanted rather than for a change is what makes that
            // repair itself instead of looking like the switch turning itself off.
            GodMode.Apply(_config.godMode);
            LabelGodButton();

            foreach (var button in _actionButtons)
                if (button.interactable != canAct) button.interactable = canAct;

            // The tuning section is not gated on a live hero: the scoreboard can be looked at
            // wherever it can be opened, and the numbers apply on their own.
            RefreshTuning();

            // What is stopping the buttons matters more than the last thing that happened, so it
            // takes the line while it applies.
            _statusText.text = canAct ? _status : reason;
        }

        // The scoreboard is hold-to-show: ControlManager.GetScoreboardAndMapInput writes the flag
        // on every tick from the key's state, so holding it open means writing it back afterwards.
        // LateUpdate is the last word in the frame, which is why this is not in Update with
        // everything else. Best effort - if the game ever moves that write later, holding Tab with
        // the left hand and clicking with the right still works, which is what it was before.
        private void LateUpdate()
        {
            if (!_pinScoreboard) return;

            var ui = UIManager.softInstance as InGameUIManager;
            if (ui != null && !ui.isScoreboardDisplayed) ui.isScoreboardDisplayed = true;
        }

        // ----- dragging -------------------------------------------------------------
        //
        // A panel for looking at the HUD that cannot be moved off the part of the HUD being looked
        // at is half a tool. Dragging is on the background, so the buttons keep their own clicks.

        private Vector2 _grab;

        public void OnBeginDrag(PointerEventData eventData)
        {
            RectTransformUtility.ScreenPointToLocalPointInRectangle(
                (RectTransform)_box.parent, eventData.position, eventData.pressEventCamera, out var local);
            _grab = local - _box.anchoredPosition;
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    (RectTransform)_box.parent, eventData.position, eventData.pressEventCamera, out var local))
                _box.anchoredPosition = local - _grab;
        }
    }
}
