#if DEBUG
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace DevTools
{
    // Any screen, without knowing the screen: everything clickable that is showing, with its text
    // and where it is, and a click by id. The game's menus are uGUI Buttons wired in the scene;
    // the few that are not (lobby hero portraits, difficulty items, shop entries) expose a public
    // Click(), and are listed too.
    //
    // A click is GlobalUIManager.SimulateClickOnUIElement - pointer down, up, click on the
    // element - which is what the game itself does for a gamepad's confirm button.
    internal static class UiApi
    {
        private const BindingFlags InstancePublic = BindingFlags.Instance | BindingFlags.Public;
        private static readonly Dictionary<Type, MethodInfo> ClickMethods = new Dictionary<Type, MethodInfo>();

        private sealed class Element
        {
            public Component component;
            public string kind;
            public RectTransform rect;
            public Rect screen;
            public float alpha;
            public bool visible;
        }

        private static MethodInfo ClickMethod(Type type)
        {
            if (ClickMethods.TryGetValue(type, out var method)) return method;
            method = null;
            string ns = type.Namespace ?? "";
            if (!ns.StartsWith("UnityEngine", StringComparison.Ordinal) && !ns.StartsWith("TMPro", StringComparison.Ordinal))
                method = type.GetMethod("Click", InstancePublic, null, Type.EmptyTypes, null);
            ClickMethods[type] = method;
            return method;
        }

        private static List<Element> Collect(bool all)
        {
            var byObject = new Dictionary<GameObject, Element>();

            foreach (var selectable in UnityEngine.Object.FindObjectsByType<Selectable>(FindObjectsSortMode.None))
            {
                if (selectable == null || !selectable.isActiveAndEnabled) continue;
                byObject[selectable.gameObject] = Make(selectable, selectable.GetType().Name);
            }

            foreach (var behaviour in UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None))
            {
                if (behaviour == null || !behaviour.isActiveAndEnabled || behaviour is Selectable) continue;
                if (byObject.ContainsKey(behaviour.gameObject)) continue;
                if (!(behaviour.transform is RectTransform)) continue;
                if (ClickMethod(behaviour.GetType()) == null) continue;
                byObject[behaviour.gameObject] = Make(behaviour, behaviour.GetType().Name + ".Click()");
            }

            var list = byObject.Values.Where(e => e != null && (all || e.visible)).ToList();
            list.Sort((a, b) =>
            {
                // Reading order: top to bottom, then left to right.
                int row = -a.screen.center.y.CompareTo(b.screen.center.y);
                return Mathf.Abs(a.screen.center.y - b.screen.center.y) > 8f ? row : a.screen.x.CompareTo(b.screen.x);
            });
            return list;
        }

        private static Element Make(Component component, string kind)
        {
            var rect = component.transform as RectTransform;
            if (rect == null) return null;

            Rect screen;
            try { screen = rect.GetScreenSpaceRect(); }
            catch (Exception) { return null; }

            float alpha = EffectiveAlpha(component.transform, out bool blocks);
            bool onScreen = screen.width > 1f && screen.height > 1f && screen.xMax > 0 && screen.yMax > 0 &&
                            screen.x < Screen.width && screen.y < Screen.height;
            return new Element
            {
                component = component,
                kind = kind,
                rect = rect,
                screen = screen,
                alpha = alpha,
                visible = onScreen && alpha > 0.05f && blocks,
            };
        }

        // CanvasGroups multiply down the hierarchy until one ignores its parents. A group at zero
        // alpha is how the game hides most of its views, rather than deactivating them.
        private static float EffectiveAlpha(Transform t, out bool blocksRaycasts)
        {
            float alpha = 1f;
            blocksRaycasts = true;
            for (var node = t; node != null; node = node.parent)
            {
                if (node.TryGetComponent<CanvasGroup>(out var group) && group.enabled)
                {
                    alpha *= group.alpha;
                    if (!group.blocksRaycasts) blocksRaycasts = false;
                    if (group.ignoreParentGroups) break;
                }
            }
            return alpha;
        }

        private static string TextOf(Component component)
        {
            var parts = component.GetComponentsInChildren<TMP_Text>(false).Select(t => GameAccess.Rich(t.text))
                                 .Concat(component.GetComponentsInChildren<Text>(false).Select(t => GameAccess.Rich(t.text)))
                                 .Where(s => !string.IsNullOrEmpty(s)).Distinct().Take(4).ToArray();
            string text = string.Join(" | ", parts);
            return text.Length > 160 ? text.Substring(0, 160) + "..." : text;
        }

        private static string PathOf(Transform t)
        {
            var names = new List<string>();
            for (var node = t; node != null && names.Count < 6; node = node.parent) names.Add(node.name);
            names.Reverse();
            return string.Join("/", names);
        }

        private static readonly List<RaycastResult> Hits = new List<RaycastResult>();

        // The interface's own raycast at the element's centre, as a real click there would be
        // routed: clickable when the first thing hit is the element or inside it. Otherwise the
        // answer names what is on top, which is usually the reason (a fade, a popup, a tooltip).
        private static bool Clickable(Element e, out string coveredBy)
        {
            coveredBy = null;
            var events = EventSystem.current;
            if (!e.visible || events == null) return false;

            Hits.Clear();
            events.RaycastAll(new PointerEventData(events) { position = e.screen.center }, Hits);
            if (Hits.Count == 0) { coveredBy = "nothing takes a raycast there"; return false; }

            var top = Hits[0].gameObject.transform;
            if (top == e.rect || top.IsChildOf(e.rect)) return true;
            coveredBy = PathOf(top);
            return false;
        }

        private static object Show(Element e)
        {
            var selectable = e.component as Selectable;
            bool clickable = Clickable(e, out string coveredBy);
            return new
            {
                id = Handles.Put(e.component),
                kind = e.kind,
                text = TextOf(e.component),
                path = PathOf(e.component.transform),
                center = new { x = Mathf.RoundToInt(e.screen.center.x), y = Mathf.RoundToInt(e.screen.center.y) },
                size = new { w = Mathf.RoundToInt(e.screen.width), h = Mathf.RoundToInt(e.screen.height) },
                interactable = selectable == null || selectable.IsInteractable(),
                clickable,
                coveredBy,
                isOn = e.component is Toggle toggle ? toggle.isOn : (bool?)null,
                value = e.component is TMP_InputField input ? input.text : null,
            };
        }

        [Route("GET", "/ui", "Everything clickable on screen: buttons, toggles, input fields and the game's Click() items, with id, text, screen position, and whether a click would land (not covered by something else).",
               "all=false (include hidden and off-screen), text? (filter), limit=200")]
        private static object List(Args a)
        {
            string filter = a.Str("text", null);
            var elements = Collect(a.Bool("all"))
                .Where(e => filter == null || TextOf(e.component).IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                            e.component.name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                .Take(a.Int("limit", 200))
                .Select(Show)
                .ToList();
            return new
            {
                scene = GameAccess.Scene,
                uiState = GameAccess.Ui != null ? GameAccess.Ui.state : null,
                screen = new { w = Screen.width, h = Screen.height },
                count = elements.Count,
                elements,
            };
        }

        private static Component Resolve(Args a)
        {
            if (a.Has("id"))
            {
                var o = Handles.Get(a.Int("id"));
                return o as Component ?? (o as GameObject)?.transform ?? throw new DevException("$" + a.Int("id") + " is not a UI element");
            }

            string text = a.Str("text", null);
            string path = a.Str("path", null);
            if (text == null && path == null) throw new DevException("id, text or path");

            var matches = Collect(false)
                .Where(e => text == null || TextOf(e.component).IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0)
                .Where(e => path == null || PathOf(e.component.transform).IndexOf(path, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();
            if (matches.Count == 0) throw new DevException("nothing visible matches - GET /ui lists what is there");

            // An exact text match beats a partial one: "Play" should not find "Play Tutorial" first.
            var exact = matches.Where(e => text != null && TextOf(e.component).Equals(text, StringComparison.OrdinalIgnoreCase)).ToList();
            var pool = exact.Count > 0 ? exact : matches;
            int index = a.Int("index", 0);
            if (index >= pool.Count) throw new DevException(pool.Count + " matches");
            return pool[index].component;
        }

        [Route("POST", "/ui/click", "Click a UI element by id (from GET /ui), or the visible one whose text or path contains the given string.",
               "id | text | path, index=0, force=false (click even if not interactable)")]
        private static object Click(Args a)
        {
            var component = Resolve(a);
            string text = TextOf(component);

            if (component is Selectable selectable)
            {
                if (!selectable.IsInteractable() && !a.Bool("force")) throw new DevException("'" + text + "' is not interactable right now");
                var ui = ManagerBase<GlobalUIManager>.softInstance;
                if (ui != null) ui.SimulateClickOnUIElement(selectable);
                else ExecuteEvents.Execute(selectable.gameObject, new PointerEventData(EventSystem.current), ExecuteEvents.pointerClickHandler);
                return new { clicked = text, kind = selectable.GetType().Name };
            }

            var method = ClickMethod(component.GetType());
            if (method != null)
            {
                method.Invoke(component, null);
                return new { clicked = text, kind = component.GetType().Name + ".Click()" };
            }

            ExecuteEvents.Execute(component.gameObject, new PointerEventData(EventSystem.current) { button = PointerEventData.InputButton.Left },
                                  ExecuteEvents.pointerClickHandler);
            return new { clicked = text, kind = "pointer click" };
        }

        [Route("POST", "/ui/set_text", "Type into an input field (replaces its text).", "id | text | path, value, submit=false")]
        private static object SetText(Args a)
        {
            var component = Resolve(a);
            var input = component as TMP_InputField ?? component.GetComponentInChildren<TMP_InputField>();
            string value = a.Str("value");
            if (input != null)
            {
                input.text = value;
                input.onEndEdit?.Invoke(value);
                if (a.Bool("submit")) input.onSubmit?.Invoke(value);
                return new { set = value };
            }
            var legacy = component as InputField ?? component.GetComponentInChildren<InputField>();
            if (legacy == null) throw new DevException("not an input field");
            legacy.text = value;
            legacy.onEndEdit?.Invoke(value);
            return new { set = value };
        }

        [Route("GET", "/ui/texts", "Every piece of visible text on screen with its position - for reading a screen that has no buttons (results, tooltips, rewards).",
               "contains?, limit=300")]
        private static object Texts(Args a)
        {
            string filter = a.Str("contains", null);
            var texts = UnityEngine.Object.FindObjectsByType<TMP_Text>(FindObjectsSortMode.None)
                .Where(t => t != null && t.isActiveAndEnabled && !string.IsNullOrWhiteSpace(t.text))
                .Select(t =>
                {
                    Rect screen;
                    try { screen = ((RectTransform)t.transform).GetScreenSpaceRect(); }
                    catch (Exception) { screen = default; }
                    float alpha = EffectiveAlpha(t.transform, out _) * t.color.a;
                    return (t, screen, alpha);
                })
                .Where(x => x.alpha > 0.05f && x.screen.width > 0 && x.screen.xMax > 0 && x.screen.yMax > 0 &&
                            x.screen.x < Screen.width && x.screen.y < Screen.height)
                .Select(x => (x.screen, text: GameAccess.Rich(x.t.text)))
                .Where(x => x.text.Length > 0 && (filter == null || x.text.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0))
                .OrderByDescending(x => x.screen.center.y).ThenBy(x => x.screen.x)
                .Take(a.Int("limit", 300))
                .Select(x => new { x.text, x = Mathf.RoundToInt(x.screen.center.x), y = Mathf.RoundToInt(x.screen.center.y) })
                .ToList();
            return new { count = texts.Count, texts };
        }
    }
}
#endif
