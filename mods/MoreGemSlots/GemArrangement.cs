using System;
using System.Collections.Generic;
using UnityEngine;

namespace MoreGemSlots
{
    // Where essence slots go, for every screen that draws them. The HUD, the scoreboard on Tab and
    // the end-of-run result screen each keep their own row, and each one caps out at the count the
    // game drew a layout for - so all three need the same answer to the same question, and giving
    // them the same answer is the whole point of this file.
    //
    // Nothing here knows what a gem slot is. It takes the transforms of a row the game authored,
    // measures it, and puts however many transforms are asked for back on it. That is what lets
    // one arrangement serve three widget types that share no base class beyond Component.
    //
    // **Every number below is a multiple of the spacing measured off the authored row**, never a
    // distance. That is what makes the arrangement portable: the same constants that were dialled
    // in against the HUD produce a proportionally identical row on a scoreboard entry a fraction
    // of the size, with nothing to re-tune.
    internal static class GemArrangement
    {
        // The extended arrangement is a fixed four-over-three grid, so seven is everything it can
        // draw:
        //
        //     1 2 3 4
        //      5 6 7
        //
        // Counts of five and six take the first positions of that same grid rather than being laid
        // out on their own, which is what keeps a slot from moving as the next one is added.
        internal const int MaxSupportedSlots = 7;
        private const int BottomRowSize = 3;

        // Counts up to here use the small numbers; the four-slot layout uses the top-row ones,
        // since it doubles as the upper half of the extended layout.
        internal const int SmallLayoutMax = 3;

        // One set of numbers, all of them multiples of the authored spacing. Fields rather than
        // constants for exactly one reason: DevTools drives them from an on-screen panel while
        // the game runs, which is how every value below was arrived at in the first place and the
        // only sane way to arrive at the next one. Nothing in the shipped mods writes them.
        //
        // The extended rows end up nearly straight, because the arc the game draws reads well at
        // four slots but fans out at seven; the small layouts keep most of it, having nothing to
        // fan out into.
        internal sealed class Tuning
        {
            // Gap between the two rows of the extended arrangement, measured from the top row, so
            // that moving the top row carries the bottom one with it.
            public float rowGap = 0.80f;

            public float topSpread = 1.10f;
            public float topDrop = -0.30f;
            public float topCurve = 0.30f;
            public float topRotate = 0f;

            public float bottomSpread = 0.75f;
            public float bottomOffset = 0f;
            public float bottomCurve = 0.30f;
            public float bottomRotate = 0f;

            // The counts the game draws itself get a set *each*. One shared set does not work: the
            // authored layouts have different geometry, so what suits the row of two is wrong for
            // the row of three.
            public float oneSpread = 0.95f;
            public float oneDrop = -0.10f;
            public float oneCurve = 0.75f;
            public float oneRotate = 1f;

            public float twoSpread = 0.80f;
            public float twoDrop = -0.05f;
            public float twoCurve = 0.75f;
            public float twoRotate = 1f;

            public float threeSpread = 0.95f;
            public float threeDrop = -0.10f;
            public float threeCurve = 0.75f;
            public float threeRotate = 1f;

            public void Small(int count, out float spread, out float drop, out float curve,
                              out float rotate)
            {
                switch (Mathf.Clamp(count, 1, SmallLayoutMax))
                {
                    case 1: spread = oneSpread; drop = oneDrop; curve = oneCurve; rotate = oneRotate; return;
                    case 2: spread = twoSpread; drop = twoDrop; curve = twoCurve; rotate = twoRotate; return;
                    default: spread = threeSpread; drop = threeDrop; curve = threeCurve; rotate = threeRotate; return;
                }
            }
        }

        // A set per screen. They started identical and did not stay that way, which is the answer
        // to whether one set could have served both: the numbers are relative to the authored
        // spacing, but a summary row's widgets are a different size *relative to* that spacing
        // than the HUD's, so the same multipliers do not land the same way.
        //
        // Four of the twenty-one differ, all in the direction that says the summary row is tighter
        // for its spacing: less spread on both rows, a smaller gap between them, and a flatter
        // bottom row. The rest were left where the HUD put them - which is a good sign that the
        // shared arrangement was the right call and only needed a second set of dials, not a
        // second implementation.
        private static Tuning MakeHud() => new Tuning();

        private static Tuning MakeSummary() => new Tuning
        {
            rowGap = 0.65f,
            topSpread = 1.00f,
            bottomSpread = 0.63f,
            bottomCurve = 0.20f,
        };

        internal static readonly Tuning Hud = MakeHud();
        internal static readonly Tuning Summary = MakeSummary();

        // What the DevTools panel's reset button calls. It lives here rather than there because
        // only this file knows what each set ships with - and it copies into the existing object
        // rather than replacing it, since the panel holds a reference to it.
        internal static void ResetToShipped(bool summary)
        {
            var target = summary ? Summary : Hud;
            var shipped = summary ? MakeSummary() : MakeHud();

            foreach (var field in typeof(Tuning).GetFields())
                field.SetValue(target, field.GetValue(shipped));

            Invalidate();
        }

        // Set by the tuning panel after it changes a number, so the rows redraw with it. Each
        // caller clears whatever it uses to decide a row is already correct.
        internal static event Action Changed;

        internal static void Invalidate() => Changed?.Invoke();

        // Two or three extras take the fixed positions from the left, so a slot never moves as the
        // next one is added. A lone extra is the exception: on its own at the left end it reads as
        // a mistake, so it goes to the middle position.
        private const bool CenterSingleExtraSlot = true;

        // The authored geometry of one row, captured before anything is touched.
        internal sealed class Shape
        {
            public Transform[] authored;
            public Vector2[] positions;
            public Vector3[] rotations;

            public float rowSpacing;
            public float rotMid;
            public float rotStep;

            // The authored row is an arc in every HUD layout the game ships, so extra rows follow
            // concentric arcs rather than straight lines. The summary screens draw theirs flat,
            // which falls through to the line branch below without needing to be told.
            public bool isArc;
            public Vector2 arcCenter;
            public float arcRadius;
            public float arcMidAngle;
            public float arcStep;
            public float arcDownSign;

            // Fallback for a row whose slots turn out to be collinear.
            public Vector2 lineMid;
            public Vector2 lineStep;
            public Vector2 lineDown;

            public int Count => authored.Length;
        }

        // ----- reading the authored row ---------------------------------------------

        // `authored` must already be in slot order. Returns null when there is nothing to measure.
        internal static Shape Measure(IList<Transform> authored)
        {
            if (authored == null || authored.Count == 0) return null;

            var shape = new Shape
            {
                authored = new Transform[authored.Count],
                positions = new Vector2[authored.Count],
                rotations = new Vector3[authored.Count],
            };

            for (int i = 0; i < authored.Count; i++)
            {
                shape.authored[i] = authored[i];
                shape.positions[i] = authored[i] is RectTransform rt ? rt.anchoredPosition : Vector2.zero;
                shape.rotations[i] = authored[i].localEulerAngles;
            }

            Fit(shape);
            return shape;
        }

        private static void Fit(Shape shape)
        {
            var p = shape.positions;
            int n = p.Length;

            float z0 = Mathf.DeltaAngle(0f, shape.rotations[0].z);
            float zLast = Mathf.DeltaAngle(0f, shape.rotations[n - 1].z);
            shape.rotMid = (z0 + zLast) * 0.5f;
            shape.rotStep = n >= 2 ? (zLast - z0) / (n - 1) : 0f;

            // Spacing between neighbours in the authored row sets the gap between rows, and is the
            // unit every constant in this file is expressed in.
            shape.rowSpacing = n >= 2 ? Vector2.Distance(p[0], p[1]) : 24f;
            if (shape.rowSpacing < 1f) shape.rowSpacing = 24f;

            shape.lineMid = (p[0] + p[n - 1]) * 0.5f;
            shape.lineStep = n >= 2 ? (p[n - 1] - p[0]) / (n - 1) : new Vector2(shape.rowSpacing, 0f);
            var along = shape.lineStep.normalized;
            var perp = new Vector2(-along.y, along.x);
            shape.lineDown = perp.y > 0f ? -perp : perp;

            if (n < 3) { shape.isArc = false; return; }

            Vector2 a = p[0], b = p[n / 2], c = p[n - 1];
            float d = 2f * (a.x * (b.y - c.y) + b.x * (c.y - a.y) + c.x * (a.y - b.y));
            if (Mathf.Abs(d) < 0.0001f) { shape.isArc = false; return; }   // collinear

            float sa = a.sqrMagnitude, sb = b.sqrMagnitude, sc = c.sqrMagnitude;
            var centre = new Vector2(
                (sa * (b.y - c.y) + sb * (c.y - a.y) + sc * (a.y - b.y)) / d,
                (sa * (c.x - b.x) + sb * (a.x - c.x) + sc * (b.x - a.x)) / d);

            float radius = Vector2.Distance(centre, a);
            if (radius < 0.01f || radius > 100000f) { shape.isArc = false; return; }

            float first = Mathf.Atan2(a.y - centre.y, a.x - centre.x);
            float last = Mathf.Atan2(c.y - centre.y, c.x - centre.x);
            float span = Mathf.DeltaAngle(first * Mathf.Rad2Deg, last * Mathf.Rad2Deg) * Mathf.Deg2Rad;

            shape.arcCenter = centre;
            shape.arcRadius = radius;
            shape.arcMidAngle = first + span * 0.5f;
            shape.arcStep = span / (n - 1);

            // Rows stack downwards on screen. Whether that means a smaller or a larger radius
            // depends on which way the authored arc bulges.
            var outward = new Vector2(Mathf.Cos(shape.arcMidAngle), Mathf.Sin(shape.arcMidAngle));
            shape.arcDownSign = outward.y > 0f ? -1f : 1f;
            shape.isArc = true;
        }

        // Puts the authored row back exactly as it was found. Only the caller that moves the
        // game's own widgets needs this; one drawing into a container of its own does not.
        internal static void Restore(Shape shape)
        {
            if (shape?.authored == null) return;

            for (int i = 0; i < shape.authored.Length; i++)
            {
                var widget = shape.authored[i];
                if (widget == null) continue;
                if (widget is RectTransform rt) rt.anchoredPosition = shape.positions[i];
                widget.localEulerAngles = shape.rotations[i];
            }
        }

        // ----- placing --------------------------------------------------------------

        // Lays the first `wanted` of `widgets` out in the arrangement for that count. `widgets`
        // need not be the transforms the shape was measured from - the summary screens measure the
        // row the game drew and then place a copy of it.
        internal static void Lay(IList<Transform> widgets, int wanted, Shape shape, Tuning tuning)
        {
            if (widgets == null || shape == null || wanted <= 0) return;

            int authored = shape.Count;

            if (wanted > authored)
            {
                PlaceExtended(widgets, wanted, shape, tuning);
            }
            else if (wanted <= SmallLayoutMax)
            {
                tuning.Small(wanted, out float spread, out float drop, out float curve, out float rotate);
                PlaceRow(widgets, wanted, shape, spread, drop, curve, rotate);
            }
            else
            {
                PlaceTopRow(widgets, wanted, shape, tuning);
            }
        }

        // The authored row on top, spread by TopRowSpread, and a second row of up to three
        // underneath drawn from a grid that does not change with the count, so adding a slot never
        // shuffles the ones already placed.
        private static void PlaceExtended(IList<Transform> widgets, int wanted, Shape shape, Tuning tuning)
        {
            int top = shape.Count;
            PlaceTopRow(widgets, top, shape, tuning);

            int bottom = Mathf.Min(wanted - top, BottomRowSize);
            float gridHalf = (BottomRowSize - 1) * 0.5f;

            // Measured from the authored line, so moving the top row carries the bottom one with
            // it and the gap between them stays what it was set to.
            float drop = shape.rowSpacing * (tuning.topDrop + tuning.rowGap);

            for (int j = 0; j < bottom; j++)
            {
                int i = top + j;
                if (i >= widgets.Count) break;

                float gridPos = (bottom == 1 && CenterSingleExtraSlot) ? gridHalf : j;

                Place(widgets[i], (gridPos - gridHalf) * tuning.bottomSpread + tuning.bottomOffset,
                      drop, shape, tuning.bottomCurve, tuning.bottomRotate);
            }
        }

        // The authored row, reshaped by the top-row numbers. Used both as the upper half of the
        // extended arrangement and for the four-slot count on its own.
        private static void PlaceTopRow(IList<Transform> widgets, int count, Shape shape, Tuning tuning)
        {
            PlaceRow(widgets, count, shape, tuning.topSpread, tuning.topDrop, tuning.topCurve,
                     tuning.topRotate);
        }

        private static void PlaceRow(IList<Transform> widgets, int count, Shape shape,
                                     float spread, float dropUnits, float curve, float rotate)
        {
            if (count <= 0) return;

            float half = (count - 1) * 0.5f;
            float drop = shape.rowSpacing * dropUnits;

            for (int i = 0; i < count && i < widgets.Count; i++)
                Place(widgets[i], (i - half) * spread, drop, shape, curve, rotate);
        }

        // Positions one widget `steps` slot-widths along the row from its middle, dropped by
        // `drop` towards the bottom of the screen.
        //
        // `curve` blends between a straight row (0) and one that follows the authored arc (1);
        // `rotate` does the same for how much the widget tilts along that arc.
        private static void Place(Transform widget, float steps, float drop, Shape shape,
                                  float curve, float rotate)
        {
            // A widget with no RectTransform is not one this can place, and it does not get its
            // rotation set either - the same all-or-nothing the HUD row has always had.
            if (!(widget is RectTransform rt)) return;

            if (shape.isArc)
            {
                float radius = shape.arcRadius + shape.arcDownSign * drop;
                float angle = shape.arcMidAngle + shape.arcStep * steps;

                var onArc = shape.arcCenter +
                    new Vector2(Mathf.Cos(angle) * radius, Mathf.Sin(angle) * radius);

                // The straight-row version of the same spot: start at the row's midpoint and
                // walk along the tangent by the arc length those steps would have covered.
                var midDir = new Vector2(Mathf.Cos(shape.arcMidAngle), Mathf.Sin(shape.arcMidAngle));
                var tangent = new Vector2(-midDir.y, midDir.x);
                var onLine = shape.arcCenter + midDir * radius +
                             tangent * (steps * shape.arcStep * radius);

                rt.anchoredPosition = Vector2.Lerp(onLine, onArc, curve);
            }
            else
            {
                rt.anchoredPosition = shape.lineMid + shape.lineStep * steps + shape.lineDown * drop;
            }

            var euler = widget.localEulerAngles;
            euler.z = shape.rotMid + shape.rotStep * steps * rotate;
            widget.localEulerAngles = euler;
        }
    }
}
