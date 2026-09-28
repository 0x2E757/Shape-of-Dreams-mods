using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace DevTools
{
    // Every essence and every memory the game has, searchable, to drop on the ground or put
    // straight into a slot.
    //
    // IMGUI rather than a uGUI panel cloned from the game's widgets, because a searchable,
    // scrolling list of five hundred rows with icons is a page of IMGUI and several of uGUI. The
    // cost is that the game cannot see it - its "is the cursor over the interface" check is a uGUI
    // raycast - which InputBlock and the patches beside it make up for.
    internal sealed class PickerWindow : MonoBehaviour
    {
        private const int WindowId = 0x0DE7_0015;
        private const float Width = 620f;
        private const float Height = 720f;
        private const float RowHeight = 44f;
        private const float IconSize = 38f;

        // Light assets are loaded a few per frame rather than all at once on opening, which would
        // be a hitch of a second or two. The list refines itself as they come in.
        private const int LoadsPerFrame = 12;

        private const string SearchControl = "devtools_search";
        private const string AmountControl = "devtools_amount";

        private static readonly string[] Tabs = { "Essences", "Memories" };
        private static readonly string[] RarityFilters = { "All", "Common", "Rare", "Epic", "Legendary", "Other" };
        private static readonly string[] SlotNames = { "Q", "W", "E", "R", "Identity", "Movement" };
        private static readonly HeroSkillLocation[] Slots =
        {
            HeroSkillLocation.Q, HeroSkillLocation.W, HeroSkillLocation.E, HeroSkillLocation.R,
            HeroSkillLocation.Identity, HeroSkillLocation.Movement,
        };

        private DevToolsConfig _config;
        private Action _save;

        private Rect _rect = new Rect(40f, 40f, Width, Height);
        private Vector2 _scroll;
        private string _query = "";
        private int _rarityFilter;
        private string _status = "";
        private string _selected;
        private bool _dropFocus;

        private List<CatalogEntry> _shown = new List<CatalogEntry>();
        private string _shownKey;
        private int _loadCursor;

        private GUIStyle _nameStyle, _typeStyle, _statusStyle, _descStyle, _rowStyle, _windowStyle;
        private Texture2D _background, _highlight;
        private float _scale = 1f;

        public void Init(DevToolsConfig config, Action save)
        {
            _config = config;
            _save = save;
        }

        private CatalogKind Kind => _config.pickerTab == 0 ? CatalogKind.Essence : CatalogKind.Memory;

        private HeroSkillLocation TargetSlot => Slots[Mathf.Clamp(_config.targetSlot, 0, Slots.Length - 1)];

        private void Update()
        {
            if (_config == null) return;

            if (Input.GetKeyDown((KeyCode)_config.hotkey))
            {
                _config.pickerOpen = !_config.pickerOpen;
                _save?.Invoke();
            }

            if (!_config.pickerOpen)
            {
                InputBlock.Reset();
                return;
            }

            // In GUI space, which is top-down and scaled.
            var mouse = Input.mousePosition;
            var gui = new Vector2(mouse.x, Screen.height - mouse.y) / _scale;
            InputBlock.PointerOverOverlay = _rect.Contains(gui);

            // A click anywhere outside the window gives the keyboard back to the game; otherwise
            // the search box would keep the hero's keys after the player had moved on.
            if (!InputBlock.PointerOverOverlay && (Input.GetMouseButtonDown(0) || Input.GetMouseButtonDown(1)))
                _dropFocus = true;

            LoadSome();
        }

        private void OnDisable() => InputBlock.Reset();

        private void OnDestroy()
        {
            if (_background != null) Destroy(_background);
            if (_highlight != null) Destroy(_highlight);
        }

        private void LoadSome()
        {
            if (!Catalog.Ready) return;
            var entries = Catalog.Of(Kind);
            int loaded = 0;
            while (_loadCursor < entries.Count && loaded < LoadsPerFrame)
            {
                if (!entries[_loadCursor].IsLoaded)
                {
                    entries[_loadCursor].Load();
                    loaded++;
                }
                _loadCursor++;
            }
            // Filters on rarity and on hidden entries need what has just loaded.
            if (loaded > 0 && (_loadCursor >= entries.Count || _loadCursor % 96 < LoadsPerFrame)) _shownKey = null;
        }

        private void OnGUI()
        {
            if (_config == null || !_config.pickerOpen) return;
            EnsureStyles();

            _scale = Mathf.Max(1f, Screen.height / 1080f);
            var saved = GUI.matrix;
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(_scale, _scale, 1f));

            // Kept on screen, since a window dragged off the edge cannot be dragged back.
            _rect.x = Mathf.Clamp(_rect.x, 0f, Mathf.Max(0f, Screen.width / _scale - _rect.width));
            _rect.y = Mathf.Clamp(_rect.y, 0f, Mathf.Max(0f, Screen.height / _scale - 40f));
            _rect = GUI.Window(WindowId, _rect, DrawWindow, "DevTools - " + _config.hotkey + " to close", _windowStyle);

            GUI.matrix = saved;

            if (_dropFocus && Event.current.type == EventType.Layout)
            {
                _dropFocus = false;
                GUI.FocusControl(null);
                GUIUtility.keyboardControl = 0;
            }

            string focused = GUI.GetNameOfFocusedControl();
            InputBlock.KeyboardCaptured = focused == SearchControl || focused == AmountControl;
        }

        private void DrawWindow(int id)
        {
            var e = Event.current;
            if (e.type == EventType.KeyDown && (e.keyCode == KeyCode.Escape || e.keyCode == KeyCode.Return) &&
                InputBlock.KeyboardCaptured)
            {
                GUI.FocusControl(null);
                e.Use();
            }

            GUILayout.BeginVertical();

            int tab = GUILayout.Toolbar(_config.pickerTab, Tabs, GUILayout.Height(28f));
            if (tab != _config.pickerTab)
            {
                _config.pickerTab = tab;
                _loadCursor = 0;
                _scroll = Vector2.zero;
                _save?.Invoke();
            }

            GUILayout.BeginHorizontal();
            GUILayout.Label("Search", GUILayout.Width(52f));
            GUI.SetNextControlName(SearchControl);
            _query = GUILayout.TextField(_query ?? "", GUILayout.ExpandWidth(true));
            if (GUILayout.Button("x", GUILayout.Width(26f))) _query = "";
            GUILayout.EndHorizontal();

            _rarityFilter = GUILayout.Toolbar(_rarityFilter, RarityFilters);

            GUILayout.BeginHorizontal();
            DrawAmount();
            GUILayout.FlexibleSpace();
            bool hidden = GUILayout.Toggle(_config.showHidden, " hidden too");
            if (hidden != _config.showHidden) { _config.showHidden = hidden; _save?.Invoke(); }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("Equip into", GUILayout.Width(72f));
            int slot = GUILayout.Toolbar(_config.targetSlot, SlotNames);
            if (slot != _config.targetSlot) { _config.targetSlot = slot; _save?.Invoke(); }
            GUILayout.EndHorizontal();

            Refilter();
            DrawList();

            var selected = _selected != null ? Catalog.Find(_selected) : null;
            if (selected != null)
                GUILayout.Label(selected.Name + "  (" + selected.typeName + ", " + selected.Rarity + ")\n" + selected.Description,
                                _descStyle, GUILayout.Height(64f));

            GUILayout.Label(StatusLine(), _statusStyle);
            GUILayout.EndVertical();

            GUI.DragWindow(new Rect(0f, 0f, 10000f, 22f));
        }

        private void DrawAmount()
        {
            bool essence = Kind == CatalogKind.Essence;
            int value = essence ? _config.gemQuality : _config.memoryLevel;

            GUILayout.Label(essence ? "Quality %" : "Level", GUILayout.Width(66f));

            // Shift steps by ten for quality and by five for levels - the sizes that matter for each.
            int step = Event.current.shift ? (essence ? 100 : 5) : (essence ? 10 : 1);
            if (GUILayout.Button("-", GUILayout.Width(26f))) value -= step;

            GUI.SetNextControlName(AmountControl);
            string text = GUILayout.TextField(value.ToString(), GUILayout.Width(56f));
            if (int.TryParse(text, out int typed)) value = typed;

            if (GUILayout.Button("+", GUILayout.Width(26f))) value += step;

            if (!essence && value > 1) GUILayout.Label("(+" + (value - 1) + ")", _typeStyle, GUILayout.Width(44f));

            value = Mathf.Max(1, value);
            if (essence && value != _config.gemQuality) { _config.gemQuality = value; _save?.Invoke(); }
            if (!essence && value != _config.memoryLevel) { _config.memoryLevel = value; _save?.Invoke(); }
        }

        private void Refilter()
        {
            string key = _config.pickerTab + "|" + _query + "|" + _rarityFilter + "|" + _config.showHidden + "|" + Catalog.Ready;
            if (key == _shownKey) return;
            _shownKey = key;

            _shown = Catalog.Of(Kind)
                .Where(entry => (_config.showHidden || !entry.IsHidden) && PassesRarity(entry) && entry.Matches(_query))
                .ToList();
        }

        // Only a loaded entry has a rarity worth filtering on; an unloaded one stays in the list
        // until it loads, which LoadSome sees to within a second.
        private bool PassesRarity(CatalogEntry entry)
        {
            if (_rarityFilter == 0 || !entry.IsLoaded) return true;
            switch (_rarityFilter)
            {
                case 1: return entry.Rarity == Rarity.Common;
                case 2: return entry.Rarity == Rarity.Rare;
                case 3: return entry.Rarity == Rarity.Epic;
                case 4: return entry.Rarity == Rarity.Legendary;
                default: return entry.Rarity > Rarity.Legendary;
            }
        }

        // Only the rows in view are drawn - and only their icons loaded - so the list costs the
        // same at five hundred entries as at ten.
        private void DrawList()
        {
            if (!Catalog.Ready)
            {
                GUILayout.Label("The game's content database is not loaded yet.");
                GUILayout.FlexibleSpace();
                return;
            }

            _scroll = GUILayout.BeginScrollView(_scroll, false, true, GUILayout.ExpandHeight(true));
            float viewWidth = _rect.width - 44f;
            var content = GUILayoutUtility.GetRect(viewWidth, _shown.Count * RowHeight);

            int first = Mathf.Max(0, (int)(_scroll.y / RowHeight) - 1);
            int last = Mathf.Min(_shown.Count, first + (int)(_rect.height / RowHeight) + 3);
            for (int i = first; i < last; i++)
                DrawRow(_shown[i], new Rect(content.x, content.y + i * RowHeight, viewWidth, RowHeight - 2f));

            GUILayout.EndScrollView();
            GUILayout.Label(_shown.Count + " of " + Catalog.Of(Kind).Count + (_config.showHidden ? "" : " (hidden left out)"), _typeStyle);
        }

        private void DrawRow(CatalogEntry entry, Rect row)
        {
            bool isSelected = entry.typeName == _selected;
            if (isSelected) GUI.Box(row, GUIContent.none, _rowStyle);

            var icon = entry.Icon;
            var iconRect = new Rect(row.x + 2f, row.y + (row.height - IconSize) / 2f, IconSize, IconSize);
            if (icon != null && icon.texture != null)
            {
                var tex = icon.texture;
                var r = icon.textureRect;
                GUI.DrawTextureWithTexCoords(iconRect, tex,
                    new Rect(r.x / tex.width, r.y / tex.height, r.width / tex.width, r.height / tex.height));
            }

            float buttonsWidth = 150f;
            var textRect = new Rect(iconRect.xMax + 8f, row.y + 2f, row.width - IconSize - buttonsWidth - 16f, row.height - 4f);

            var color = entry.IsLoaded ? Catalog.RarityColor(entry.Rarity) : Color.white;
            _nameStyle.normal.textColor = color;
            GUI.Label(new Rect(textRect.x, textRect.y, textRect.width, 22f), entry.Name, _nameStyle);
            GUI.Label(new Rect(textRect.x, textRect.y + 20f, textRect.width, 18f),
                      entry.typeName + (entry.IsLoaded ? "  -  " + entry.Rarity : ""), _typeStyle);

            if (GUI.Button(new Rect(textRect.x, row.y, textRect.width, row.height), GUIContent.none, GUIStyle.none))
                _selected = entry.typeName;

            var drop = new Rect(row.xMax - buttonsWidth, row.y + 6f, 64f, row.height - 12f);
            var equip = new Rect(drop.xMax + 4f, drop.y, buttonsWidth - 68f, drop.height);
            if (GUI.Button(drop, "Drop")) Spawn(entry, equip: false);
            if (GUI.Button(equip, "Equip " + SlotNames[_config.targetSlot])) Spawn(entry, equip: true);
        }

        private void Spawn(CatalogEntry entry, bool equip)
        {
            _selected = entry.typeName;
            try
            {
                HeroSkillLocation? slot = equip ? TargetSlot : (HeroSkillLocation?)null;
                if (entry.kind == CatalogKind.Essence)
                {
                    var gem = Cheats.SpawnEssence(entry.typeName, _config.gemQuality, slot);
                    _status = (equip ? "equipped " : "dropped ") + Cheats.Describe(gem) + (equip ? " in " + slot : "");
                }
                else
                {
                    var skill = Cheats.SpawnMemory(entry.typeName, _config.memoryLevel, slot);
                    _status = (equip ? "equipped " : "dropped ") + Cheats.Describe(skill) + (equip ? " in " + slot : "");
                }
            }
            catch (DevException refused)
            {
                _status = refused.Message;
            }
            catch (Exception failed)
            {
                _status = "failed: " + failed.Message;
                Debug.LogException(failed);
            }
        }

        private string StatusLine()
        {
            if (!string.IsNullOrEmpty(_status)) return _status;
            if (!GameAccess.IsServer) return "Spawning needs a run you are hosting (solo counts).";
            if (GameAccess.Hero == null) return "No hero yet - start a run.";
            return "Click a row for its description. Shift on -/+ steps further.";
        }

        private void EnsureStyles()
        {
            if (_nameStyle != null) return;

            _nameStyle = new GUIStyle(GUI.skin.label) { fontSize = 15, fontStyle = FontStyle.Bold, clipping = TextClipping.Clip };
            _typeStyle = new GUIStyle(GUI.skin.label) { fontSize = 11, clipping = TextClipping.Clip };
            _typeStyle.normal.textColor = new Color(0.65f, 0.68f, 0.72f);
            _statusStyle = new GUIStyle(GUI.skin.label) { fontSize = 12, wordWrap = true };
            _statusStyle.normal.textColor = new Color(0.8f, 0.85f, 0.6f);
            _descStyle = new GUIStyle(GUI.skin.box) { fontSize = 12, wordWrap = true, alignment = TextAnchor.UpperLeft };
            _descStyle.normal.textColor = new Color(0.9f, 0.9f, 0.92f);

            // The stock skin's window is half transparent, which over a bright room is unreadable.
            _background = Solid(new Color(0.07f, 0.08f, 0.1f, 0.96f));
            _highlight = Solid(new Color(0.25f, 0.32f, 0.45f, 0.6f));
            _windowStyle = new GUIStyle(GUI.skin.window) { fontSize = 13, padding = new RectOffset(10, 10, 26, 10) };
            _windowStyle.normal.background = _background;
            _windowStyle.onNormal.background = _background;
            _windowStyle.normal.textColor = Color.white;
            _windowStyle.onNormal.textColor = Color.white;
            _rowStyle = new GUIStyle { normal = { background = _highlight } };
        }

        private static Texture2D Solid(Color color)
        {
            var texture = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
            texture.SetPixel(0, 0, color);
            texture.Apply();
            return texture;
        }
    }
}
