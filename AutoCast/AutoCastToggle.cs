using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace AutoCast
{
    // The control that sits above a skill cell: a grey plate, the icon frame, and the arrows on
    // their own layer so they can turn while the frame stays put.
    //
    // Built from bare components rather than cloned from DewGUI.widgetToggleButton: that widget is
    // a labelled rectangle sized for a settings row, and what the HUD wants is an icon with three
    // states. The one thing worth taking from the widget is its sound set, which is copied onto a
    // UI_ButtonAudio of our own so the control clicks like the rest of the interface.
    //
    // IShowTooltip is the hover-tooltip contract: the manager raycasts, finds components that
    // implement it and calls ShowTooltip on the top one. Its two pointer members have default
    // implementations that do nothing but poke the manager, which is what RefreshTooltip does.
    internal sealed class AutoCastToggle : MonoBehaviour, IShowTooltip,
        IPointerEnterHandler, IPointerExitHandler, IPointerDownHandler, IPointerUpHandler
    {
        internal enum State
        {
            Off = 0,
            On = 1,
            Locked = 2,
        }

        // Dialled in on screen with a slider panel that no longer ships; see the README.

        // Canvas units: the edge of the square the icon draws in, and the gap above the cell.
        private const float Size = 39.5f;
        private const float Offset = 6f;

        // The plate, as a fraction of that square. A shade over it, and black rather than grey,
        // so it reads as a shadow the icon sits on rather than a button it sits in.
        private const float PlateScale = 1.01f;
        private static readonly Color PlateColor = new Color(0f, 0f, 0f, 0.35f);

        // Only the off state is held back, so that four of these over the skill bar do not shout;
        // the pointer brings it up to full. On and locked are solid - the colour of the artwork
        // already says which is which, and dimming as well only made them hard to read.
        private const float OffAlpha = 0.6f;
        private const float FullAlpha = 1f;

        private const float HoverScale = 1.15f;
        private const float PressScale = 0.92f;
        private const float TweenSpeed = 14f;

        // Degrees per second: the arrows while autocast is on, and how fast they settle when it
        // goes off.
        private const float SpinSpeed = 60f;
        private const float SpinSettleSpeed = 360f;

        // The gap between the top of the icon and the bottom of its tooltip.
        private const float TooltipOffset = 8f;

        public Action onClicked;

        private RectTransform _rect;
        private RectTransform _anchor;
        private Image _hitArea;
        private Image _plate;
        private RectTransform _plateRect;
        private Image _ring;
        private Image _arrows;
        private RectTransform _arrowsRect;
        private Button _button;

        private State _state = State.Off;
        private bool _interactive;
        private bool _hovered;
        private bool _pressed;
        private float _alpha;
        private float _scale = 1f;
        private float _angle;

        // Parented beside the skill cell rather than inside it, and given the cell to follow.
        // See Follow for why.
        public static AutoCastToggle Create(RectTransform anchor, string name)
        {
            var gobj = new GameObject(name, typeof(RectTransform));
            gobj.transform.SetParent(anchor.parent, false);
            gobj.transform.SetAsLastSibling();

            var rect = (RectTransform)gobj.transform;
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0f);
            rect.sizeDelta = new Vector2(Size, Size);
            rect.localScale = Vector3.one;
            rect.localRotation = Quaternion.identity;

            // In case the container ever grows a layout group: this one places itself.
            var element = gobj.AddComponent<LayoutElement>();
            element.ignoreLayout = true;

            // The skill cell fades and stops taking raycasts in some HUD modes; this keeps the
            // control clickable regardless of what the parent group is doing.
            var group = gobj.AddComponent<CanvasGroup>();
            group.ignoreParentGroups = true;
            group.interactable = true;
            group.blocksRaycasts = true;
            group.alpha = 1f;

            // The clickable surface is the whole box, not whichever layer happens to be under the
            // pointer, so it is its own invisible graphic and the layers below take no raycasts.
            // Off until SetInteractive says otherwise, so there is no frame in which it can be
            // clicked before anyone has decided it should be.
            var hitArea = gobj.AddComponent<Image>();
            hitArea.color = new Color(0f, 0f, 0f, 0f);
            hitArea.raycastTarget = false;

            // Transition None because the state colours are the whole visual language, and a tint
            // layered on top of them would only fight with it. The Button is still worth having
            // for its click handling and for the interactable flag UI_ButtonAudio reads.
            var button = gobj.AddComponent<Button>();
            button.transition = Selectable.Transition.None;
            button.targetGraphic = hitArea;

            // Added after the Button, because its Awake runs on AddComponent and looks for one.
            var audio = gobj.AddComponent<UI_ButtonAudio>();
            CopySounds(audio);

            // The plate is sized as a fraction of the box, so it stays a disc under the ring
            // whatever the box does; the two icon layers simply fill it.
            var plate = AddLayer(rect, "Plate", Icons.Plate, false);
            var ring = AddLayer(rect, "Ring", Icons.Ring(0), true);
            var arrows = AddLayer(rect, "Arrows", Icons.Arrows(0), true);

            var control = gobj.AddComponent<AutoCastToggle>();
            control._rect = rect;
            control._anchor = anchor;
            control._hitArea = hitArea;
            control._plate = plate;
            control._plateRect = (RectTransform)plate.transform;
            control._ring = ring;
            control._arrows = arrows;
            control._arrowsRect = (RectTransform)arrows.transform;
            control._button = button;
            control._alpha = OffAlpha;
            control.Redraw();

            button.onClick.AddListener(control.HandleClick);
            return control;
        }

        // stretch: fill the parent box. Otherwise the layer is centred and sized in Redraw.
        private static Image AddLayer(RectTransform parent, string name, Sprite sprite, bool stretch)
        {
            var gobj = new GameObject(name, typeof(RectTransform));
            gobj.transform.SetParent(parent, false);

            var rect = (RectTransform)gobj.transform;
            if (stretch)
            {
                rect.anchorMin = Vector2.zero;
                rect.anchorMax = Vector2.one;
                rect.offsetMin = Vector2.zero;
                rect.offsetMax = Vector2.zero;
            }
            else
            {
                rect.anchorMin = new Vector2(0.5f, 0.5f);
                rect.anchorMax = new Vector2(0.5f, 0.5f);
            }
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = Vector2.zero;
            rect.localScale = Vector3.one;

            var image = gobj.AddComponent<Image>();
            image.sprite = sprite;
            image.preserveAspect = true;
            image.raycastTarget = false;
            return image;
        }

        // Whether the control answers the pointer at all. Off during ordinary play, so that a
        // click meant for the skill bar cannot land on it and a stray hover cannot raise its
        // tooltip; the state it shows stays readable either way.
        public void SetInteractive(bool value)
        {
            if (_interactive == value) return;
            _interactive = value;
            _hitArea.raycastTarget = value;

            if (value) return;

            // Going quiet under the pointer would otherwise leave the hover and its tooltip
            // behind, with nothing coming to clear them.
            bool wasHovered = _hovered;
            _hovered = false;
            _pressed = false;
            if (wasHovered) RefreshTooltip();
        }

        public void SetState(State state)
        {
            if (_state == state) return;
            _state = state;

            _ring.sprite = Icons.Ring((int)state);
            _arrows.sprite = Icons.Arrows((int)state);
            _button.interactable = state != State.Locked;

            if (state == State.Locked) _pressed = false;

            // A tooltip already on screen is describing the state we just left.
            if (_hovered) RefreshTooltip();
        }

        private void HandleClick()
        {
            if (_state == State.Locked) return;
            onClicked?.Invoke();
        }

        private void Update()
        {
            float targetAlpha = _state == State.Off && !_hovered ? OffAlpha : FullAlpha;

            float targetScale = _state == State.Locked ? 1f
                              : _pressed ? PressScale
                              : _hovered ? HoverScale
                              : 1f;

            // Two clocks. Hover and press run unscaled, so the control still answers the pointer
            // while the game is paused; the arrows run on scaled time, because they are saying
            // something about the game and should hold still when the game does.
            float unscaled = Time.unscaledDeltaTime;
            float scaled = Time.deltaTime;

            float t = Mathf.Clamp01(unscaled * TweenSpeed);
            _alpha = Mathf.Lerp(_alpha, targetAlpha, t);
            _scale = Mathf.Lerp(_scale, targetScale, t);

            if (_state == State.On)
            {
                // Negative Z is clockwise on screen, which is the way the arrowheads point.
                _angle -= SpinSpeed * scaled;
                if (_angle <= -360f || _angle >= 360f) _angle %= 360f;
            }
            else
            {
                // The arrows are symmetric under a half turn, so settling on the nearest multiple
                // of 180 looks like they were never moved rather than like they snapped back.
                _angle = Mathf.MoveTowards(_angle, Mathf.Round(_angle / 180f) * 180f,
                                           SpinSettleSpeed * scaled);
            }

            Redraw();
        }

        private void Redraw()
        {
            var box = new Vector2(Size, Size);
            if (_rect.sizeDelta != box) _rect.sizeDelta = box;

            Follow();
            _rect.localScale = new Vector3(_scale, _scale, 1f);

            var plate = box * PlateScale;
            if (_plateRect.sizeDelta != plate) _plateRect.sizeDelta = plate;
            _plate.color = PlateColor;

            var tint = new Color(1f, 1f, 1f, _alpha);
            _ring.color = tint;
            _arrows.color = tint;
            _arrowsRect.localRotation = Quaternion.Euler(0f, 0f, _angle);
        }

        // The control sits above the skill cell but is deliberately not a child of it. Unity sends
        // pointer enter and exit to the whole ancestor chain of whatever is under the cursor, and
        // there is no way to stop that partway, so a control parented to the cell would count as
        // a hover of the cell too and pop the skill tooltip every time the pointer crossed it.
        // Living beside the cell and following it keeps the two hovers apart, at the cost of one
        // position write per frame.
        private void Follow()
        {
            if (_anchor == null) return;

            var box = _anchor.rect;
            var top = _anchor.TransformPoint(new Vector3(box.center.x, box.yMax, 0f));
            _rect.position = top + new Vector3(0f, Offset * _anchor.lossyScale.y, 0f);
        }

        private void OnDisable()
        {
            // Otherwise a control hidden from under the pointer leaves its tooltip behind.
            bool wasHovered = _hovered;
            _hovered = false;
            _pressed = false;
            if (wasHovered) RefreshTooltip();
        }

        // ----- pointer --------------------------------------------------------------

        public void OnPointerEnter(PointerEventData eventData)
        {
            _hovered = true;
            RefreshTooltip();
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            _hovered = false;
            _pressed = false;
            RefreshTooltip();
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            if (_state != State.Locked) _pressed = true;
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            _pressed = false;
        }

        // ----- tooltip --------------------------------------------------------------

        public void ShowTooltip(UI_TooltipManager tooltip)
        {
            string state = _state == State.On ? Localization.StateOn
                         : _state == State.Locked ? Localization.StateLocked
                         : Localization.StateOff;

            string body = _state == State.On ? Localization.Get(Localization.TooltipOn)
                        : _state == State.Locked ? Localization.Get(Localization.TooltipLocked)
                        : Localization.Get(Localization.TooltipOff);

            var settings = new TooltipSettings
            {
                mode = TooltipPositionMode.Getter,
                getter = Anchor,
                pivot = new Vector2(0.5f, 0f),
            };

            tooltip.ShowTitleDescTooltip(settings, Localization.Title(state), body);
        }

        // Where the bottom edge of the tooltip goes. The manager wants screen pixels, which is
        // what an overlay canvas puts in transform.position; lossyScale turns canvas units into
        // pixels, and picks up the hover scale as well, so the tooltip keeps its distance while
        // the icon grows.
        //
        // The whole height, not half of it: this rect is pivoted at its bottom edge, so
        // transform.position is already the bottom and half a height only reached the middle of
        // the icon - which is where the tooltip used to sit, over the top half of the button.
        private Vector2 Anchor()
        {
            Vector3 p = _rect.position;
            float scale = _rect.lossyScale.y;
            return new Vector2(p.x, p.y + (_rect.rect.height + TooltipOffset) * scale);
        }

        private static void RefreshTooltip()
        {
            var manager = UI_TooltipManager.softInstance;
            if (manager != null) manager.UpdateTooltip();
        }

        // ----- sounds ---------------------------------------------------------------

        private static void CopySounds(UI_ButtonAudio target)
        {
            var source = FindSounds();
            if (source == null) return;

            target.sfxMouseOver = source.sfxMouseOver;
            target.sfxMouseExit = source.sfxMouseExit;
            target.sfxMouseDown = source.sfxMouseDown;
            target.sfxMouseUp = source.sfxMouseUp;
            target.sfxClick = source.sfxClick;
        }

        private static UI_ButtonAudio FindSounds()
        {
            var toggle = DewGUI.widgetToggleButton;
            if (toggle != null)
            {
                var audio = toggle.GetComponentInChildren<UI_ButtonAudio>(true);
                if (audio != null) return audio;
            }

            var button = DewGUI.widgetButton;
            if (button != null) return button.GetComponentInChildren<UI_ButtonAudio>(true);

            return null;
        }
    }
}
