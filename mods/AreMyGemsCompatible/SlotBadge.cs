using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace AreMyGemsCompatible
{
    // The mark on the slot itself, so that a dead essence is visible without hovering anything.
    //
    // It hangs off UI_InGame_GemSlot.LogicUpdate rather than off Awake, for two reasons. The slot
    // widgets are cloned - MoreGemSlots clones them to build the fifth, sixth and seventh slots,
    // and the edit-skill overlay is a companion component on the same object rather than a second
    // widget - so anything anchored to construction would miss some of them. And LogicUpdate is
    // already where the slot decides whether it is showing an essence at all, which is the same
    // question this has to answer.
    //
    // The verdict is not recomputed every tick: it depends on the essence's type and the memory's,
    // and both are cheap to compare against what was decided last time.
    [HarmonyPatch(typeof(UI_InGame_GemSlot), "LogicUpdate")]
    internal static class SlotBadgePatch
    {
        private static void Postfix(UI_InGame_GemSlot __instance)
        {
            var config = AreMyGemsCompatibleMod.Live;
            if (config == null) return;

            var mark = SlotMark.For(__instance);
            if (mark == null) return;

            mark.Refresh(config.showSlotBadge);
        }
    }

    // Any slot's essence changing can change any other slot's verdict, so every mark is told when
    // one does. UI_InGame_GemSlot.SetTarget is the one place the game notices, and it is reached
    // only from event handlers - OnLocalHeroGemChanged, OnGemSlotClientState, and the hero
    // changing - never from a per-frame path, so a postfix on it costs nothing and fires exactly
    // when something moved.
    [HarmonyPatch(typeof(UI_InGame_GemSlot), "SetTarget")]
    internal static class SlotTargetPatch
    {
        private static void Postfix()
        {
            SlotMark.Invalidate();
        }
    }

    internal sealed class SlotMark : MonoBehaviour
    {
        // Every mark this mod has built, so that unloading takes them all back off again. A slot
        // widget outlives the mod that decorated it. Keyed by the slot rather than found with
        // GetComponent because LogicUpdate runs for every slot of every skill on every tick, and
        // this is the one thing in the mod that is on that path.
        private static readonly Dictionary<UI_InGame_GemSlot, SlotMark> All =
            new Dictionary<UI_InGame_GemSlot, SlotMark>();

        // Bumped whenever any slot anywhere changes what it is holding. A verdict depends on
        // essences *other* than the one in this slot - see Verdict.SuppliedBySiblings - so a slot
        // that has not been touched can still need a new answer, and the first version of this
        // polled twice a second to find out. That is what put a visible lag on the mark appearing
        // and disappearing while essences were being rearranged.
        private static int _generation;

        private UI_InGame_GemSlot _slot;
        private Image _image;

        private Gem _lastGem;
        private SkillTrigger _lastSkill;
        private int _seenGeneration = -1;
        private bool _lastVerdict;

        public static void Invalidate()
        {
            _generation++;
        }

        public static SlotMark For(UI_InGame_GemSlot slot)
        {
            SlotMark mark;
            if (All.TryGetValue(slot, out mark)) return mark;

            if (Badge.Sprite == null) return null;

            mark = slot.gameObject.AddComponent<SlotMark>();
            mark._slot = slot;
            All[slot] = mark;
            return mark;
        }

        public static void RemoveAll()
        {
            foreach (var pair in All)
            {
                var mark = pair.Value;
                if (mark == null) continue;

                // The faded icon belongs to the game's own widget and would stay faded after the
                // mod went away, which is the one thing here that outlives an unload.
                mark.FadeIcon(false);

                if (mark._image != null) Destroy(mark._image.gameObject);
                Destroy(mark);
            }
            All.Clear();
        }

        public void Refresh(bool enabled)
        {
            var gem = CurrentGem();
            var skill = gem != null ? gem.skill : null;

            // Three ways the answer can have changed: this slot's essence, the memory under it -
            // which a memory swap changes without the essence moving - and anything at all
            // elsewhere in the loadout.
            if (gem != _lastGem || skill != _lastSkill || _seenGeneration != _generation)
            {
                _lastGem = gem;
                _lastSkill = skill;
                _seenGeneration = _generation;
                _lastVerdict = Verdict.For(gem, skill) == Compatibility.Dead;
            }

            Show(enabled && _lastVerdict);
        }

        // The essence the slot is drawing, which is not the same as the essence equipped there:
        // the slot shows itself empty while its own essence is being dragged, and a mark left
        // hanging over an empty frame reads as a warning about nothing.
        private Gem CurrentGem()
        {
            var player = DewPlayer.local;
            if (player == null || player.hero == null || _slot == null || _slot.button == null) return null;

            var gem = player.hero.Skill.GetGem(new GemLocation(_slot.button.skillType, _slot.slotIndex));
            if (gem == null || gem.IsNullOrInactive()) return null;

            var editing = ManagerBase<EditSkillManager>.instance;
            if (editing != null && editing.draggingObject == gem) return null;

            return gem;
        }

        private void Show(bool visible)
        {
            // The fade goes on and comes off with the mark, so nothing has to remember to put the
            // icon back: a slot that stops being marked is repainted on its next tick.
            FadeIcon(visible);

            if (!visible)
            {
                if (_image != null) _image.enabled = false;
                return;
            }

            if (_image == null) Build();
            if (_image != null) _image.enabled = true;
        }

        // The essence's own icon, dimmed while it is marked. The game writes this image's sprite,
        // its scale and whether it is active, but never its colour, so there is nothing here to
        // fight with frame by frame.
        private void FadeIcon(bool marked)
        {
            var icon = _slot != null ? _slot.gemIconImage : null;
            if (icon == null) return;

            float target = marked ? Mathf.Clamp01(BadgeAppearance.IconAlpha) : 1f;

            var colour = icon.color;
            if (Mathf.Approximately(colour.a, target)) return;

            colour.a = target;
            icon.color = colour;
        }

        // The pulse, and the one thing here that wants the frame rather than the logic tick:
        // LogicUpdate runs at a fixed rate below the frame rate, and a fade stepped at that rate
        // is visibly stepped.
        private void Update()
        {
            if (_image == null || !_image.enabled) return;

            // Placement as well as the pulse, and both off the same number: the slot's scale is
            // still moving for about a quarter of a second after edit mode is entered or left, and
            // the mark's offset and its pulse both follow it across.
            float editProgress = EditProgress();
            ApplyPlacement(editProgress);

            var colour = _image.color;
            colour.a = BadgeAppearance.PulseAlpha(editProgress);
            _image.color = colour;
        }

        private void Build()
        {
            // Anchored to the essence icon rather than to the slot: the slot's own rect carries
            // the frame and the cooldown fill, and the icon is what the player is looking at.
            var anchor = _slot.gemIconImage != null
                ? _slot.gemIconImage.rectTransform
                : _slot.transform as RectTransform;
            if (anchor == null) return;

            // Sized from the icon's rendered rect, so it cannot be built before a layout has run:
            // anchorMin equals anchorMax below, which makes sizeDelta the size outright, and a
            // zero there is a badge that is never seen again. Bail and let the next tick try.
            if (anchor.rect.size.x <= 0f || anchor.rect.size.y <= 0f) return;

            var go = new GameObject("AreMyGemsCompatible Mark", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            var rect = (RectTransform)go.transform;
            rect.SetParent(anchor, worldPositionStays: false);

            // Pinned to the icon's top-right corner, which is where the game puts its own small
            // overlays and is the one corner the cooldown sweep leaves alone. How far from the
            // corner, and how big, is BadgeAppearance's.
            rect.anchorMin = new Vector2(1f, 1f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.localScale = Vector3.one;
            rect.SetAsLastSibling();

            _image = go.GetComponent<Image>();
            _image.sprite = Badge.Sprite;
            _image.raycastTarget = false;
            _image.preserveAspect = true;

            ApplyPlacement();
        }

        // Size and offset, both read as fractions of the icon the mark hangs on, so a number
        // measured at one resolution is still right at another.
        //
        // Called every frame as well as on a change, because the offset follows the slot's own
        // scale animation into and out of editing mode. Both writes are guarded: a RectTransform
        // assignment is not free, and while nothing is animating there is nothing to write.
        private void ApplyPlacement()
        {
            ApplyPlacement(EditProgress());
        }

        private void ApplyPlacement(float editProgress)
        {
            if (_image == null) return;

            var rect = _image.rectTransform;
            var anchor = rect.parent as RectTransform;
            if (anchor == null) return;

            var size = anchor.rect.size;
            if (size.x <= 0f || size.y <= 0f) return;

            var wanted = size * BadgeAppearance.Scale;
            if (rect.sizeDelta != wanted) rect.sizeDelta = wanted;

            var offset = BadgeAppearance.OffsetFor(editProgress);
            var position = new Vector2(size.x * offset.x, size.y * offset.y);
            if (rect.anchoredPosition != position) rect.anchoredPosition = position;
        }

        // Read off the icon's own scale, which the slot walks between its two authored values.
        private float EditProgress()
        {
            if (_slot == null || _slot.gemIconImage == null) return 0f;

            return BadgeAppearance.EditProgress(_slot.iconScale, _slot.editingIconScale,
                                                _slot.gemIconImage.transform.localScale.x);
        }

        private void OnDestroy()
        {
            if (_slot != null) All.Remove(_slot);

            // Reached when the mark is destroyed on its own rather than through RemoveAll - a
            // reload, or the slot widget going away. Harmless in the second case, since the icon
            // is being destroyed too, and necessary in the first.
            FadeIcon(false);

            if (_image != null) Destroy(_image.gameObject);
        }
    }
}
