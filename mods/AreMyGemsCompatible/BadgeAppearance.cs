using UnityEngine;

namespace AreMyGemsCompatible
{
    // How the mark looks: where it sits on the essence icon, how big it is, how far the icon under
    // it is faded, and how it pulses.
    //
    // Constants rather than settings, and that is a judgement rather than an oversight - the same
    // one MoreGemSlots makes about its slot geometry. Where a badge belongs on a forty-pixel icon
    // is a thing to measure once, not a knob to hand a player.
    //
    // **Every number here was arrived at by nudging it on screen**, through a section in DevTools
    // that drove these as mutable statics and logged them back as the field initialisers below.
    // That section has been taken out again now they are settled; docs/devtools.md records how it
    // worked, which is what to read before rebuilding it after an artwork change.
    internal static class BadgeAppearance
    {
        // ----- placement ------------------------------------------------------------
        //
        // Both offsets are fractions of the essence icon's own size rather than pixels, so a value
        // measured at one resolution is still right at another.

        // Distance from the icon's top-right corner. Positive x is further right and positive y
        // further up, so zero would centre the mark on the corner itself and leave three quarters
        // of it hanging off the icon. Both are negative: the mark is pulled back inside, far
        // enough to sit on the icon and not so far as to cover the artwork it is warning about.
        public const float OffsetX = -0.75f;
        public const float OffsetY = -0.2f;

        // Where it goes instead while the loadout is being edited.
        //
        // **The slot is not the same shape in the two states**, so one pair of numbers cannot serve
        // both. UI_InGame_GemSlot animates its icon between iconScale and editingIconScale - 1.25
        // and 1 as the prefab ships them - and swaps the frame behind it, so a mark that sits right
        // on the resting slot sits wrong on the editing one.
        public const float EditOffsetX = -0.85f;
        public const float EditOffsetY = -0.1f;

        // Side of the mark, as a fraction of the icon. Half is about the smallest that still reads
        // as an exclamation mark rather than a red dot at the size the HUD draws a slot.
        public const float Scale = 0.5f;

        // ----- the icon underneath --------------------------------------------------

        // What the essence's own icon fades to while it is marked. The mark says *that* something
        // is wrong and the fade says which essence is not pulling its weight - which reads across a
        // whole bar in a way a small badge does not. It is the same move the game makes on a slot
        // that is not ready: SetReady drops the material's saturation and brightness rather than
        // drawing anything new.
        //
        // The game never writes gemIconImage.color itself - only its sprite, its scale and whether
        // it is active - so this is ours to own, and it is put back the moment the mark goes.
        public const float IconAlpha = 0.25f;

        // ----- the copy in the tooltip ----------------------------------------------
        //
        // The inline mark is a different drawing job from the one on the slot and gets its own two
        // numbers. TextMeshPro sizes a sprite from an asset with no point size to the font's ascent
        // line, which puts it level with a capital letter - correct, and a shade too assertive
        // beside a sentence.

        // Size relative to the text it sits in.
        public const float TooltipScale = 0.92f;

        // How far to shift it off the baseline, in ems. Negative drops it.
        public const float TooltipRise = -0.06f;

        // ----- the pulse ------------------------------------------------------------
        //
        // A mark that sits still is easy to stop seeing, and this one has to survive being one
        // small thing among a bar full of small things. The alpha walks between the two ends on a
        // sine, so there is no hard edge at either end of the travel.
        //
        // **It runs only while the loadout is not being edited.** The pulse is there to catch an
        // eye that is busy elsewhere; on the editing screen the mark is already being looked at,
        // and a blinking thing you are trying to read is just harder to read.

        public const float PulseMin = 0.3f;
        public const float PulseMax = 1f;

        // Full cycles per second.
        public const float PulseSpeed = 0.5f;

        // How far the slot is into editing mode, 0 at rest and 1 fully editing.
        //
        // **Not the mode flag**, which is what the slot's own FrameUpdate tests. The icon does not
        // jump between the two sizes, it is walked between them by MoveTowards over about a
        // quarter of a second - so anything switched on the flag would snap and then sit still
        // while the icon it belongs to went on moving under it. Reading the progress out of the
        // icon's live scale instead puts everything here on the same clock as the slot itself,
        // without this having to know the speed.
        //
        // `resting` and `editing` are the slot's own iconScale and editingIconScale. Equal values
        // give InverseLerp nothing to work with and it answers 0, which is the resting state - the
        // right answer for a prefab that does not animate at all.
        public static float EditProgress(float resting, float editing, float current)
        {
            return Mathf.InverseLerp(resting, editing, current);
        }

        public static Vector2 OffsetFor(float editProgress)
        {
            return new Vector2(Mathf.Lerp(OffsetX, EditOffsetX, editProgress),
                               Mathf.Lerp(OffsetY, EditOffsetY, editProgress));
        }

        // Where the mark's own alpha should be this frame.
        //
        // Unscaled time, like everything else the slot animates: the essence screens the mark
        // matters most on are exactly the ones that stop the clock.
        //
        // The pulse is faded out by editProgress rather than switched off by it, for the reason
        // the offsets are blended: entering editing mode mid-fade would otherwise jump the mark
        // from wherever the sine had it to full brightness. Fading it out over the same quarter
        // second the slot takes to change shape hides the seam.
        public static float PulseAlpha(float editProgress)
        {
            float phase = (Mathf.Sin(Time.unscaledTime * PulseSpeed * Mathf.PI * 2f) + 1f) * 0.5f;
            float pulsing = Mathf.Lerp(PulseMin, PulseMax, phase);

            // Editing settles on the bright end.
            return Mathf.Lerp(pulsing, PulseMax, editProgress);
        }
    }
}
