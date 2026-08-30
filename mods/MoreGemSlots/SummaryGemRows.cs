using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace MoreGemSlots
{
    // The HUD is not the only place essence slots are drawn, and it is not the only place they
    // stop being drawn. Two summary screens keep a row of their own per memory - the scoreboard
    // on Tab, and the result screen at the end of a run - and both cap out exactly the way
    // GemLayoutPatch describes, with an array of hand-authored layouts picked by slot count:
    //
    //     for (int i = 0; i < gemObjects234.Length; i++)
    //         gemObjects234[i].SetActive(max == i + 2);
    //
    // The game's own field name says what the array holds: layouts for two, three and four. Above
    // four no index matches, every layout is switched off, and the row vanishes - the same
    // failure as the HUD's, one screen further out.
    //
    // Neither screen is short of data. The scoreboard reads the live hero, and the result screen
    // reads DewGameResult, which records every entry of hero.Skill.gems and one maxGemCount per
    // location - so slots five to seven are already in there and only the drawing is missing.
    //
    // **Every count is the mod's, not just the ones the game cannot draw.** Otherwise a memory
    // with four slots is arranged one way on the HUD and another here, which is exactly the
    // inconsistency this file exists to remove. How that is done depends on what the original did
    // on the tick, and the two cases are different enough to be worth naming:
    //
    //   - **Two to four: the widgets are placed where they stand.** The original activated the
    //     right container and did nothing else - it never moves the widgets inside one - so they
    //     can simply be arranged in place, which is what GemLayoutPatch does to the HUD. Each of
    //     those containers is measured on its own, because a row authored for two has different
    //     geometry from one authored for three, and the arrangement is built to be told that.
    //
    //   - **Above four: a container of the mod's own**, cloned from the four-slot layout and
    //     parented beside it. Here the original switches every one of its layouts off on every
    //     tick, so a patch that switched one back on would be undoing that work sixty times a
    //     second, and each of those toggles re-runs OnEnable on every widget below it. Owning the
    //     container means the original does exactly what it means to - clear the native row - and
    //     nothing fights over anything.
    //
    // These are postfixes rather than replacements, unlike the HUD patch. The methods they hang
    // off also fill in the skill icon, the charge count and the key binding, and none of that is
    // worth reimplementing to change one row. The trap the HUD patch ran into - the original
    // rebuilding a cached slot list only in the frame it activates a layout - has no counterpart
    // here: neither screen caches anything, the gem widgets update themselves.
    internal static class GemRow
    {
        private const string ContainerName = "MoreGemSlots_SummaryRow";
        private const string CloneNamePrefix = "MoreGemSlots_Summary_";

        internal delegate int GetIndex(Component widget);
        internal delegate void SetIndex(Component widget, int index);

        // The mod's own container, for the counts the game drew no layout for.
        private sealed class Owned
        {
            public GameObject container;
            public int applied;              // 0 while hidden
        }

        // One measurement per container, shared by both cases, and this is load-bearing rather
        // than a saving. The four-slot container is *both* the one arranged in place at four and
        // the one the extended row is measured from - so if each case measured for itself, opening
        // Tab at four slots and then gaining a fifth would measure a row this file had already
        // spread, and apply the spread twice. Measuring once, before anything is moved, is what
        // keeps the two honest.
        private static readonly Dictionary<GameObject, GemArrangement.Shape> Shapes =
            new Dictionary<GameObject, GemArrangement.Shape>();

        // What each of the game's containers is currently arranged for. A container only ever
        // serves one count, so this settles on its first pass and never changes again.
        private static readonly Dictionary<GameObject, int> Arranged = new Dictionary<GameObject, int>();

        private static readonly Dictionary<GameObject, Owned> Owneds = new Dictionary<GameObject, Owned>();
        private static readonly HashSet<GameObject> Logged = new HashSet<GameObject>();

        // See GemLayoutPatch.Invalidate. Only what was applied is forgotten - the measured shapes
        // stay, because the authored rows have not moved and re-measuring now would read rows this
        // file has already arranged.
        internal static void Invalidate()
        {
            Arranged.Clear();
            foreach (var pair in Owneds) pair.Value.applied = -1;
        }

        internal static void Reset()
        {
            // The game's rows are put back where they were found; ours are simply taken away.
            // Restoring a row that was only ever measured writes back what it already holds, so
            // there is nothing to be careful about here.
            foreach (var pair in Shapes) GemArrangement.Restore(pair.Value);
            foreach (var pair in Owneds)
            {
                if (pair.Value?.container != null) UnityEngine.Object.Destroy(pair.Value.container);
            }

            Shapes.Clear();
            Arranged.Clear();
            Owneds.Clear();
            Logged.Clear();
        }

        // ----- picking a case -------------------------------------------------------

        internal static void Show(GameObject[] groups, int max, Type widgetType,
                                  GetIndex getIndex, SetIndex setIndex,
                                  Action<Component> refresh, string what)
        {
            if (groups == null || groups.Length == 0) return;

            var source = groups[groups.Length - 1];
            if (source == null) return;

            max = Mathf.Clamp(max, 0, GemArrangement.MaxSupportedSlots);
            if (max <= 0) { HideOwned(source); return; }

            // Layout i draws i + 2 slots, so the container for this count - if the game drew one -
            // is at max - 2.
            int index = max - 2;
            if (index >= 0 && index < groups.Length && groups[index] != null)
            {
                HideOwned(source);
                Arrange(groups[index], max, widgetType, getIndex, what);
                return;
            }

            Build(source, max, widgetType, getIndex, setIndex, refresh, what);
        }

        // ----- the game's own containers, arranged in place -------------------------

        private static void Arrange(GameObject container, int wanted, Type widgetType,
                                    GetIndex getIndex, string what)
        {
            // The scoreboard comes through here on every tick it is open, for every memory of
            // every player on it, so the settled case is a lookup and an int compare.
            if (Arranged.TryGetValue(container, out int applied) && applied == wanted) return;

            var shape = ShapeOf(container, widgetType, getIndex, what + " native");
            if (shape == null) return;

            var transforms = Transforms(container, widgetType, getIndex);
            if (transforms.Count == 0) return;

            GemArrangement.Lay(transforms, wanted, shape, GemArrangement.Summary);
            Arranged[container] = wanted;
        }

        // ----- the mod's own container ----------------------------------------------

        private static void HideOwned(GameObject source)
        {
            if (!Owneds.TryGetValue(source, out var owned) || owned.applied == 0) return;
            if (owned.container != null && owned.container.activeSelf) owned.container.SetActive(false);
            owned.applied = 0;
        }

        private static void Build(GameObject source, int wanted, Type widgetType,
                                  GetIndex getIndex, SetIndex setIndex,
                                  Action<Component> refresh, string what)
        {
            // Measured off the game's four-slot row, which nothing in this case ever moves.
            var shape = ShapeOf(source, widgetType, getIndex, what + " extended");
            if (shape == null) return;

            var owned = Acquire(source);
            if (owned == null) return;

            var container = owned.container;
            if (owned.applied == wanted && container.activeSelf) return;

            var widgets = Widgets(container, widgetType, getIndex);
            if (widgets.Count == 0) return;

            if (widgets.Count < wanted) AddClones(widgets, wanted, widgetType, setIndex);

            for (int i = 0; i < widgets.Count; i++)
            {
                // The index is the whole identity of one of these: the scoreboard widget looks up
                // GemLocation(skill.type, index), and the result widget searches the recorded gems
                // for the same pair.
                setIndex(widgets[i], i);

                bool used = i < wanted;
                if (widgets[i].gameObject.activeSelf != used) widgets[i].gameObject.SetActive(used);
            }

            var transforms = new List<Transform>(widgets.Count);
            foreach (var widget in widgets) transforms.Add(widget.transform);
            GemArrangement.Lay(transforms, wanted, shape, GemArrangement.Summary);

            // The scoreboard's widget binds itself to whatever index it holds in OnEnable, and
            // Instantiate runs that long before the indices above are written - so without this a
            // clone would come up showing the essence of the slot it was copied from. Cycling the
            // container re-runs it for every widget under it at once. The result screen's widget
            // has no OnEnable and does not mind either way.
            container.SetActive(false);
            container.SetActive(true);

            // The result screen collects its stat items before this row exists, so the ones it
            // could not know about are filled in by hand. Free of side effects on the score: a
            // gem item scores zero whatever it draws.
            if (refresh != null)
            {
                for (int i = 0; i < wanted && i < widgets.Count; i++) refresh(widgets[i]);
            }

            owned.applied = wanted;
        }

        // Cloned from the four-slot layout and parented beside it, so every widget in it resolves
        // the same parents the originals do - GetComponentInParent for the scoreboard's,
        // transform.parent.parent for the result screen's.
        private static Owned Acquire(GameObject source)
        {
            if (Owneds.TryGetValue(source, out var existing) && existing.container != null)
                return existing;

            var parent = source.transform.parent;
            if (parent == null) return null;

            // A live reload leaves the previous one behind; picking it up again is what stops
            // reloads from stacking rows.
            var found = parent.Find(ContainerName);
            var container = found != null ? found.gameObject
                                          : UnityEngine.Object.Instantiate(source, parent);
            container.name = ContainerName;
            container.transform.SetAsLastSibling();

            var owned = new Owned { container = container };
            Owneds[source] = owned;
            return owned;
        }

        private static void AddClones(List<Component> widgets, int wanted, Type widgetType,
                                      SetIndex setIndex)
        {
            var source = widgets[widgets.Count - 1];
            var parent = source.transform.parent;

            for (int index = widgets.Count; index < wanted; index++)
            {
                var clone = UnityEngine.Object.Instantiate(source.gameObject, parent);
                clone.name = CloneNamePrefix + index;

                var widget = clone.GetComponent(widgetType);
                if (widget == null) { UnityEngine.Object.Destroy(clone); break; }

                // Assigned here as well as by the caller, so that no two widgets ever hold the
                // same index. A clone arrives carrying the index of the one it was copied from,
                // and a later pass sorts this list by index - where a duplicate would let two
                // widgets change places for no reason.
                setIndex(widget, index);

                widgets.Add(widget);
            }
        }

        // ----- shared -----------------------------------------------------------------

        private static List<Component> Widgets(GameObject container, Type widgetType, GetIndex getIndex)
        {
            var widgets = new List<Component>(container.GetComponentsInChildren(widgetType, true));
            widgets.RemoveAll(w => w == null);
            widgets.Sort((a, b) => getIndex(a).CompareTo(getIndex(b)));
            return widgets;
        }

        private static List<Transform> Transforms(GameObject container, Type widgetType, GetIndex getIndex)
        {
            var widgets = Widgets(container, widgetType, getIndex);
            var transforms = new List<Transform>(widgets.Count);
            foreach (var widget in widgets) transforms.Add(widget.transform);
            return transforms;
        }

        // Measured once per container and never again, which is what stops one case reading a row
        // the other has already moved. See the note on Shapes.
        private static GemArrangement.Shape ShapeOf(GameObject container, Type widgetType,
                                                    GetIndex getIndex, string what)
        {
            if (Shapes.TryGetValue(container, out var cached)) return cached;

            var shape = Measure(container, widgetType, getIndex);
            if (shape == null) return null;

            Shapes[container] = shape;
            Log(container, shape, what);
            return shape;
        }

        private static GemArrangement.Shape Measure(GameObject container, Type widgetType,
                                                    GetIndex getIndex)
        {
            var authored = new List<Component>(container.GetComponentsInChildren(widgetType, true));
            authored.RemoveAll(w => w == null || w.name.StartsWith(CloneNamePrefix));
            if (authored.Count == 0) return null;

            authored.Sort((a, b) => getIndex(a).CompareTo(getIndex(b)));

            var transforms = new List<Transform>(authored.Count);
            foreach (var widget in authored) transforms.Add(widget.transform);

            return GemArrangement.Measure(transforms);
        }

        private static void Log(GameObject key, GemArrangement.Shape shape, string what)
        {
            if (Logged.Contains(key)) return;
            Logged.Add(key);
            Debug.Log($"[MoreGemSlots] {what}: {shape.Count} authored widgets, " +
                      $"spacing {shape.rowSpacing:0.#}, arc={shape.isArc}");
        }
    }

    // Tab. Redrawn every tick the scoreboard is open, for every player on it.
    [HarmonyPatch(typeof(UI_InGame_Scoreboard_PlayerItem_Skill), "UpdateInfo")]
    internal static class ScoreboardGemRowPatch
    {
        private static void Postfix(UI_InGame_Scoreboard_PlayerItem_Skill __instance)
        {
            var hero = __instance.hero;
            if (ActorCheck.IsNullOrInactive(hero) || hero.Skill == null) return;

            GemRow.Show(__instance.gemObjects234, hero.Skill.GetMaxGemCount(__instance.type),
                        typeof(UI_InGame_Scoreboard_PlayerItem_Skill_Gem),
                        w => ((UI_InGame_Scoreboard_PlayerItem_Skill_Gem)w).index,
                        (w, i) => ((UI_InGame_Scoreboard_PlayerItem_Skill_Gem)w).index = i,
                        null, "scoreboard");
        }
    }

    // The result screen at the end of a run. Redrawn by UI_InGame_ResultView.Refresh, which
    // collects its stat items with GetComponentsInChildren every time it runs, so from the second
    // refresh onwards the widgets added here are updated by the game like any other.
    [HarmonyPatch(typeof(UI_InGame_Result_HeroSkillItem), "UpdateAndGetScore")]
    internal static class ResultGemRowPatch
    {
        // Taken by position rather than by name: parameter names are not something a mod should
        // depend on. __0 is the result, __1 the player index, __2 the score multiplier.
        private static void Postfix(UI_InGame_Result_HeroSkillItem __instance,
                                    DewGameResult __0, int __1, float __2)
        {
            if (__0?.players == null || __1 < 0 || __1 >= __0.players.Count) return;

            var counts = __0.players[__1].maxGemCounts;
            int slot = (int)__instance.type;
            if (counts == null || slot < 0 || slot >= counts.Count) return;

            void Fill(Component widget) =>
                ((UI_InGame_Result_HeroGemItem)widget).UpdateAndGetScore(__0, __1, __2);

            GemRow.Show(__instance.gemObjects234, counts[slot], typeof(UI_InGame_Result_HeroGemItem),
                        w => ((UI_InGame_Result_HeroGemItem)w).index,
                        (w, i) => ((UI_InGame_Result_HeroGemItem)w).index = i,
                        Fill, "result screen");
        }
    }
}
