using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace MoreGemSlots
{
    // The skill button does not lay its essence slots out. It picks one of several layouts
    // drawn by hand in the prefab, indexed by (slot count - 1):
    //
    //     for (int i = 0; i < groups.Length; i++)
    //         groups[i].SetActive(i == max - 1);
    //
    // The game ships four of them. At five no index matches, every layout switches off, and the
    // slots vanish from the cell while still working underneath.
    //
    // This patch replaces that method outright rather than deferring to it for small counts.
    // Handing back is what broke going from five slots to four: the original only rebuilds its
    // slot list when it *activates* a layout, and the layout was already active from the patch,
    // so a five-slot list survived into a four-slot loadout.
    //
    // Where the slots actually go is GemArrangement's business, shared with the two summary
    // screens - see SummaryGemRows.cs. What is left here is everything specific to the HUD: the
    // patch itself, finding the button, and building slot widgets that address real gems.
    //
    // Cloning the widget fixes both screens at once, because the edit-skill overlay
    // (UI_InGame_GemSlot_EditSkill) is a companion component on the same object rather than a
    // second layout of its own.
    [HarmonyPatch(typeof(UI_InGame_SkillButton_GemGroup), "LogicUpdate")]
    internal static class GemLayoutPatch
    {
        private const string CloneNamePrefix = "MoreGemSlots_Extra_";

        // Kept here as well because it reads as the HUD's limit at the call sites that clamp
        // against it, and because that is where it was documented.
        internal const int MaxSupportedSlots = GemArrangement.MaxSupportedSlots;

        private sealed class Layout
        {
            public GemArrangement.Shape shape;
            public int appliedCount = -1;
        }

        private static readonly Dictionary<GameObject, Layout> Layouts = new Dictionary<GameObject, Layout>();
        private static readonly HashSet<GameObject> Logged = new HashSet<GameObject>();

        // A row is only re-laid when its count changes, so a tuning number changing under it would
        // otherwise not show until the player gained a slot. Forgetting what was applied is enough
        // to make the next tick redo it; the measured geometry is untouched, since the authored row
        // has not moved.
        internal static void Invalidate()
        {
            foreach (var pair in Layouts) pair.Value.appliedCount = -1;
        }

        internal static void Reset()
        {
            // Put every layout back as it was found and remove the clones, so unloading the mod
            // leaves the HUD exactly as the game authored it.
            foreach (var pair in Layouts)
            {
                if (pair.Key != null)
                {
                    foreach (var slot in pair.Key.GetComponentsInChildren<UI_InGame_GemSlot>(true))
                    {
                        if (slot != null && slot.name.StartsWith(CloneNamePrefix))
                            Object.Destroy(slot.gameObject);
                    }
                }
                GemArrangement.Restore(pair.Value?.shape);
            }

            Layouts.Clear();
            Logged.Clear();
        }

        // The gem group is a *sibling* of the skill button, not a child of it. The game resolves
        // the pairing through the shared parent, and so must this: GetComponentInParent looks
        // like the obvious call, returns null, and fails silently.
        private static UI_InGame_SkillButton FindButton(UI_InGame_SkillButton_GemGroup group)
        {
            var parent = group.transform.parent;
            if (parent == null) return null;

            var button = parent.GetComponentInChildren<UI_InGame_SkillButton>();
            if (button == null) button = parent.GetComponentInChildren<UI_InGame_SkillButton>(true);
            return button;
        }

        // Returning false skips the original method.
        private static bool Prefix(UI_InGame_SkillButton_GemGroup __instance)
        {
            var groups = __instance.groups;
            if (groups == null || groups.Length == 0) return true;

            var player = DewPlayer.local;
            var hero = player != null ? player.hero : null;
            if (hero == null || hero.Skill == null) return true;

            var button = FindButton(__instance);
            if (button == null) return true;

            int max = hero.Skill.GetMaxGemCount(button.skillType);
            if (max <= 0) return true;

            // Use the layout drawn for this count, or the largest one when the count runs past
            // what the game draws.
            int index = Mathf.Min(max, groups.Length) - 1;
            while (index > 0 && groups[index] == null) index--;
            if (groups[index] == null) return true;

            for (int i = 0; i < groups.Length; i++)
            {
                if (groups[i] == null) continue;
                bool active = i == index;
                if (groups[i].activeSelf != active) groups[i].SetActive(active);
            }

            var layout = Capture(groups[index]);
            if (layout == null) return true;

            var slots = __instance.activeGemSlots;
            bool stale = layout.appliedCount != max
                         || slots == null
                         || slots.Length != max
                         || slots[slots.Length - 1] == null;

            if (stale)
            {
                // Logged per layout the first time it is laid out, so the numbers describe that
                // layout rather than whichever one happened to be on screen first.
                if (!Logged.Contains(groups[index]))
                {
                    Logged.Add(groups[index]);
                    Debug.Log($"[MoreGemSlots] layout[{index}] holds {layout.shape.Count} authored " +
                              $"widgets, arc={layout.shape.isArc}, showing {max}");
                }

                __instance.activeGemSlots = Apply(groups[index], layout, max);
                layout.appliedCount = max;
            }

            return false;
        }

        // ----- authored geometry ----------------------------------------------------

        private static Layout Capture(GameObject group)
        {
            if (Layouts.TryGetValue(group, out var existing)) return existing;

            var found = new List<UI_InGame_GemSlot>(group.GetComponentsInChildren<UI_InGame_GemSlot>(true));
            // Anything of ours from an earlier load is not part of the authored layout.
            found.RemoveAll(s => s == null || s.name.StartsWith(CloneNamePrefix));
            if (found.Count == 0) return null;

            found.Sort((a, b) => a.slotIndex.CompareTo(b.slotIndex));

            var transforms = new List<Transform>(found.Count);
            foreach (var slot in found) transforms.Add(slot.transform);

            var shape = GemArrangement.Measure(transforms);
            if (shape == null) return null;

            var layout = new Layout { shape = shape };
            Layouts[group] = layout;
            return layout;
        }

        // ----- applying -------------------------------------------------------------

        private static UI_InGame_GemSlot[] Apply(GameObject group, Layout layout, int wanted)
        {
            var slots = new List<UI_InGame_GemSlot>(group.GetComponentsInChildren<UI_InGame_GemSlot>(true));
            slots.RemoveAll(s => s == null);
            slots.Sort((a, b) => a.slotIndex.CompareTo(b.slotIndex));

            if (wanted > layout.shape.Count && slots.Count < wanted) AddClones(slots, wanted);

            var transforms = new List<Transform>(slots.Count);
            foreach (var slot in slots) transforms.Add(slot.transform);
            GemArrangement.Lay(transforms, wanted, layout.shape, GemArrangement.Hud);

            var result = new UI_InGame_GemSlot[wanted];
            for (int i = 0; i < slots.Count; i++)
            {
                bool used = i < wanted;
                if (slots[i].gameObject.activeSelf != used) slots[i].gameObject.SetActive(used);
                if (used) result[i] = slots[i];
            }
            return result;
        }

        private static void AddClones(List<UI_InGame_GemSlot> slots, int wanted)
        {
            var source = slots[slots.Count - 1];
            var sourceRt = source.transform as RectTransform;
            var parent = source.transform.parent;

            for (int index = slots.Count; index < wanted; index++)
            {
                var clone = Object.Instantiate(source.gameObject, parent);
                clone.name = CloneNamePrefix + index;

                var slot = clone.GetComponent<UI_InGame_GemSlot>();
                if (slot == null) { Object.Destroy(clone); break; }

                // slotIndex is the whole identity of the widget: thisSlotLocation is
                // (button.skillType, slotIndex), and the group sorts by it.
                slot.slotIndex = index;
                slot.button = source.button;

                if (sourceRt != null && clone.transform is RectTransform rt)
                {
                    rt.anchorMin = sourceRt.anchorMin;
                    rt.anchorMax = sourceRt.anchorMax;
                    rt.pivot = sourceRt.pivot;
                    rt.sizeDelta = sourceRt.sizeDelta;
                    rt.localScale = sourceRt.localScale;
                }

                // OnEnable binds the widget to whatever slotIndex it holds at that moment, and
                // Instantiate runs it before the real index can be assigned above - so a clone
                // would come up showing the essence of the slot it was copied from. Cycling the
                // object re-runs OnEnable against the corrected index.
                clone.SetActive(false);
                clone.SetActive(true);

                slots.Add(slot);
            }
        }
    }
}
