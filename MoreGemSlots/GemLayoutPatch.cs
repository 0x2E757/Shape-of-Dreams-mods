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
    // Extra slots go on a second row underneath, staggered between the ones above:
    //
    //     1 2 3 4
    //      5 6 7
    //
    // The authored row is never moved, which is what keeps the group from drifting sideways as
    // slots are added.
    //
    // Cloning the widget fixes both screens at once, because the edit-skill overlay
    // (UI_InGame_GemSlot_EditSkill) is a companion component on the same object rather than a
    // second layout of its own.
    [HarmonyPatch(typeof(UI_InGame_SkillButton_GemGroup), "LogicUpdate")]
    internal static class GemLayoutPatch
    {
        private const string CloneNamePrefix = "MoreGemSlots_Extra_";

        // The extended layout is a fixed four-over-three arrangement, so seven is everything it
        // can draw. Counts of five and six take the first positions of that same grid rather
        // than being laid out on their own, which is what keeps a slot from moving as the next
        // one is added.
        internal const int MaxSupportedSlots = 7;
        private const int BottomRowSize = 3;

        // All of the geometry below was dialled in on screen against the real HUD rather than
        // chosen on paper. The extended rows end up nearly straight, because the arc the game
        // draws reads well at four slots but fans out at seven; the small layouts keep most of
        // it, having nothing to fan out into.
        private const float RowSpacingScale = 0.80f;
        private const float TopRowSpread = 1.10f;
        private const float TopRowDrop = -0.30f;
        private const float TopRowCurve = 0.30f;
        private const float TopRowRotate = 0f;

        private struct RowShape
        {
            public float spread;
            public float drop;
            public float curve;
            public float rotate;

            public RowShape(float spread, float drop, float curve, float rotate)
            {
                this.spread = spread;
                this.drop = drop;
                this.curve = curve;
                this.rotate = rotate;
            }
        }

        // The counts the game draws itself get a set of numbers *each*. One shared set does not
        // work: the authored layouts have different geometry, so what suits the row of two is
        // wrong for the row of three. Indexed by slot count, entry 0 unused.
        private static readonly RowShape[] SmallShapes =
        {
            default,
            new RowShape(0.95f, -0.10f, 0.75f, 1f),
            new RowShape(0.80f, -0.05f, 0.75f, 1f),
            new RowShape(0.95f, -0.10f, 0.75f, 1f),
        };

        // Counts up to here use the small numbers; the four-slot layout uses the top-row ones,
        // since it doubles as the upper half of the extended layout.
        internal const int SmallLayoutMax = 3;
        private const float BottomRowSpread = 0.75f;
        private const float BottomRowOffset = 0f;
        private const float BottomRowCurve = 0.30f;
        private const float BottomRowRotate = 0f;

        // Two or three extras take the fixed positions from the left, so a slot never moves as
        // the next one is added. A lone extra is the exception: on its own at the left end it
        // reads as a mistake, so it goes to the middle position.
        private const bool CenterSingleExtraSlot = true;

        // The authored geometry of one layout, captured before anything is touched.
        private sealed class Layout
        {
            public UI_InGame_GemSlot[] authored;
            public Vector2[] positions;
            public Vector3[] rotations;
            public int appliedCount = -1;

            public float rowSpacing;
            public float rotMid;
            public float rotStep;

            // The authored row is an arc in every layout the game ships, so extra rows follow
            // concentric arcs rather than straight lines.
            public bool isArc;
            public Vector2 arcCenter;
            public float arcRadius;
            public float arcMidAngle;
            public float arcStep;
            public float arcDownSign;

            // Fallback for a layout whose slots turn out to be collinear.
            public Vector2 lineMid;
            public Vector2 lineStep;
            public Vector2 lineDown;
        }

        private static readonly Dictionary<GameObject, Layout> Layouts = new Dictionary<GameObject, Layout>();
        private static readonly HashSet<GameObject> Logged = new HashSet<GameObject>();

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
                Restore(pair.Value);
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
                    Debug.Log($"[MoreGemSlots] layout[{index}] holds {layout.authored.Length} authored " +
                              $"widgets, arc={layout.isArc}, showing {max}");
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

            var layout = new Layout
            {
                authored = found.ToArray(),
                positions = new Vector2[found.Count],
                rotations = new Vector3[found.Count],
            };

            for (int i = 0; i < found.Count; i++)
            {
                var rt = found[i].transform as RectTransform;
                layout.positions[i] = rt != null ? rt.anchoredPosition : Vector2.zero;
                layout.rotations[i] = found[i].transform.localEulerAngles;
            }

            Measure(layout);
            Layouts[group] = layout;
            return layout;
        }

        private static void Measure(Layout layout)
        {
            var p = layout.positions;
            int n = p.Length;

            float z0 = Mathf.DeltaAngle(0f, layout.rotations[0].z);
            float zLast = Mathf.DeltaAngle(0f, layout.rotations[n - 1].z);
            layout.rotMid = (z0 + zLast) * 0.5f;
            layout.rotStep = n >= 2 ? (zLast - z0) / (n - 1) : 0f;

            // Spacing between neighbours in the authored row sets the gap between rows.
            layout.rowSpacing = n >= 2 ? Vector2.Distance(p[0], p[1]) : 24f;
            if (layout.rowSpacing < 1f) layout.rowSpacing = 24f;

            layout.lineMid = (p[0] + p[n - 1]) * 0.5f;
            layout.lineStep = n >= 2 ? (p[n - 1] - p[0]) / (n - 1) : new Vector2(layout.rowSpacing, 0f);
            var along = layout.lineStep.normalized;
            var perp = new Vector2(-along.y, along.x);
            layout.lineDown = perp.y > 0f ? -perp : perp;

            if (n < 3) { layout.isArc = false; return; }

            Vector2 a = p[0], b = p[n / 2], c = p[n - 1];
            float d = 2f * (a.x * (b.y - c.y) + b.x * (c.y - a.y) + c.x * (a.y - b.y));
            if (Mathf.Abs(d) < 0.0001f) { layout.isArc = false; return; }   // collinear

            float sa = a.sqrMagnitude, sb = b.sqrMagnitude, sc = c.sqrMagnitude;
            var centre = new Vector2(
                (sa * (b.y - c.y) + sb * (c.y - a.y) + sc * (a.y - b.y)) / d,
                (sa * (c.x - b.x) + sb * (a.x - c.x) + sc * (b.x - a.x)) / d);

            float radius = Vector2.Distance(centre, a);
            if (radius < 0.01f || radius > 100000f) { layout.isArc = false; return; }

            float first = Mathf.Atan2(a.y - centre.y, a.x - centre.x);
            float last = Mathf.Atan2(c.y - centre.y, c.x - centre.x);
            float span = Mathf.DeltaAngle(first * Mathf.Rad2Deg, last * Mathf.Rad2Deg) * Mathf.Deg2Rad;

            layout.arcCenter = centre;
            layout.arcRadius = radius;
            layout.arcMidAngle = first + span * 0.5f;
            layout.arcStep = span / (n - 1);

            // Rows stack downwards on screen. Whether that means a smaller or a larger radius
            // depends on which way the authored arc bulges.
            var outward = new Vector2(Mathf.Cos(layout.arcMidAngle), Mathf.Sin(layout.arcMidAngle));
            layout.arcDownSign = outward.y > 0f ? -1f : 1f;
            layout.isArc = true;
        }

        // ----- applying -------------------------------------------------------------

        private static UI_InGame_GemSlot[] Apply(GameObject group, Layout layout, int wanted)
        {
            var slots = new List<UI_InGame_GemSlot>(group.GetComponentsInChildren<UI_InGame_GemSlot>(true));
            slots.RemoveAll(s => s == null);
            slots.Sort((a, b) => a.slotIndex.CompareTo(b.slotIndex));

            int authored = layout.authored.Length;
            if (wanted > authored)
            {
                if (slots.Count < wanted) AddClones(slots, wanted);
                PlaceExtended(slots, wanted, layout);
            }
            else if (wanted <= SmallLayoutMax)
            {
                var shape = SmallShapes[Mathf.Clamp(wanted, 1, SmallLayoutMax)];
                PlaceRow(slots, wanted, layout, shape.spread, shape.drop, shape.curve, shape.rotate);
            }
            else
            {
                PlaceTopRow(slots, wanted, layout);
            }

            var result = new UI_InGame_GemSlot[wanted];
            for (int i = 0; i < slots.Count; i++)
            {
                bool used = i < wanted;
                if (slots[i].gameObject.activeSelf != used) slots[i].gameObject.SetActive(used);
                if (used) result[i] = slots[i];
            }
            return result;
        }

        private static void Restore(Layout layout)
        {
            if (layout?.authored == null) return;

            for (int i = 0; i < layout.authored.Length; i++)
            {
                var slot = layout.authored[i];
                if (slot == null) continue;
                if (slot.transform is RectTransform rt) rt.anchoredPosition = layout.positions[i];
                slot.transform.localEulerAngles = layout.rotations[i];
            }
        }

        // The extended arrangement: the authored row on top, spread by TopRowSpread, and a
        // second row of up to three underneath drawn from a grid that does not change with the
        // count, so adding a slot never shuffles the ones already placed.
        private static void PlaceExtended(List<UI_InGame_GemSlot> slots, int wanted, Layout layout)
        {
            int top = layout.authored.Length;
            PlaceTopRow(slots, top, layout);

            int bottom = Mathf.Min(wanted - top, BottomRowSize);
            float gridHalf = (BottomRowSize - 1) * 0.5f;

            // Measured from the authored line, so moving the top row carries the bottom one with
            // it and the gap between them stays what it was set to.
            float drop = layout.rowSpacing * (TopRowDrop + RowSpacingScale);

            for (int j = 0; j < bottom; j++)
            {
                int i = top + j;
                if (i >= slots.Count) break;

                // Two or three extras take the fixed positions from the left, so a slot never
                // moves as the next one is added. A lone extra is the exception: on its own at
                // the left end it reads as a mistake, so it goes to the middle position.
                float gridPos = (bottom == 1 && CenterSingleExtraSlot) ? gridHalf : j;

                Place(slots[i], (gridPos - gridHalf) * BottomRowSpread + BottomRowOffset, drop,
                      layout, BottomRowCurve, BottomRowRotate);
            }
        }

        // The authored row, reshaped by the top-row numbers. Used both as the upper half of the
        // extended layout and, when ApplyToSmallLayouts is on, for the counts the game draws.
        private static void PlaceTopRow(List<UI_InGame_GemSlot> slots, int count, Layout layout)
        {
            PlaceRow(slots, count, layout, TopRowSpread, TopRowDrop, TopRowCurve, TopRowRotate);
        }

        private static void PlaceRow(List<UI_InGame_GemSlot> slots, int count, Layout layout,
                                     float spread, float dropUnits, float curve, float rotate)
        {
            if (count <= 0) return;

            float half = (count - 1) * 0.5f;
            float drop = layout.rowSpacing * dropUnits;

            for (int i = 0; i < count && i < slots.Count; i++)
                Place(slots[i], (i - half) * spread, drop, layout, curve, rotate);
        }

        // Positions one slot `steps` slot-widths along the row from its middle, dropped by
        // `drop` towards the bottom of the screen.
        //
        // `curve` blends between a straight row (0) and one that follows the authored arc (1);
        // `rotate` does the same for how much the widget tilts along that arc.
        private static void Place(UI_InGame_GemSlot slot, float steps, float drop, Layout layout,
                                  float curve, float rotate)
        {
            var rt = slot.transform as RectTransform;
            if (rt == null) return;

            if (layout.isArc)
            {
                float radius = layout.arcRadius + layout.arcDownSign * drop;
                float angle = layout.arcMidAngle + layout.arcStep * steps;

                var onArc = layout.arcCenter +
                    new Vector2(Mathf.Cos(angle) * radius, Mathf.Sin(angle) * radius);

                // The straight-row version of the same spot: start at the row's midpoint and
                // walk along the tangent by the arc length those steps would have covered.
                var midDir = new Vector2(Mathf.Cos(layout.arcMidAngle), Mathf.Sin(layout.arcMidAngle));
                var tangent = new Vector2(-midDir.y, midDir.x);
                var onLine = layout.arcCenter + midDir * radius +
                             tangent * (steps * layout.arcStep * radius);

                rt.anchoredPosition = Vector2.Lerp(onLine, onArc, curve);
            }
            else
            {
                rt.anchoredPosition = layout.lineMid + layout.lineStep * steps + layout.lineDown * drop;
            }

            var euler = slot.transform.localEulerAngles;
            euler.z = layout.rotMid + layout.rotStep * steps * rotate;
            slot.transform.localEulerAngles = euler;
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
