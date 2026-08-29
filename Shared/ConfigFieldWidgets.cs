using System;
using System.Collections.Generic;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Shared
{
    // The settings window builds a checkbox for every bool and a text box for every number.
    // DewGUI.fieldBuilders is the sanctioned way to change that: a public list of
    // (condition, builder) pairs, where a pair inserted at the front claims a field before the
    // stock builders see it, and the game still owns reading and writing the field itself.
    //
    // Two are registered: an on/off button in place of the checkbox, and a slider in place of the
    // text box for any number carrying a [Range]. int gets whole steps, float does not.
    //
    // The list is global, shared with the game and every other mod, so the conditions are narrowed
    // to one config type - hence the constructor argument - and the entries come back out on
    // unload.
    //
    // This lives in the shared folder because getting the slider prefab to behave took four rounds
    // the first time and was then rediscovered from scratch the second. Almost every line of
    // BuildSlider is load-bearing; the comments say which and why.
    internal sealed class ConfigFieldWidgets
    {
        private const float ToggleWidth = 200f;
        private const float ToggleHeight = 56f;

        // The prefab stretches its slide area across the widget with fixed insets - roughly 50 on
        // the left and 142 on the right, the latter reserved for its own value text. The usable
        // track is the width minus those, so on a narrow widget the stock insets leave almost no
        // travel at all. They are narrowed here, and the value text with them: it was authored for
        // a percentage and these numbers need far less.
        private const float SliderWidth = 460f;
        private const float SliderHeight = 56f;
        private const float ReadoutWidth = 56f;
        private const float TrackLeftInset = 16f;
        private const float TrackRightInset = ReadoutWidth + 12f;

        private const string WholeFormat = "0";
        private const string FractionFormat = "0.##";

        // The words the game puts on its own toggles, in the Controls screen and everywhere else.
        private const string OnKey = "Generic_On";
        private const string OffKey = "Generic_Off";

        private readonly Type _owner;
        private readonly List<(DewGUI.FieldBuilderCondition, DewGUI.FieldBuilder)> _installed =
            new List<(DewGUI.FieldBuilderCondition, DewGUI.FieldBuilder)>();

        public ConfigFieldWidgets(Type ownerConfigType)
        {
            _owner = ownerConfigType;
        }

        public void Install()
        {
            if (DewGUI.fieldBuilders == null || _installed.Count > 0) return;

            Add((type, info) => Owned(info) && type == typeof(bool), BuildToggle);
            Add((type, info) => Owned(info)
                                && (type == typeof(int) || type == typeof(float))
                                && info.GetCustomAttribute<RangeAttribute>() != null,
                BuildSlider);
        }

        public void Remove()
        {
            foreach (var entry in _installed) DewGUI.fieldBuilders?.Remove(entry);
            _installed.Clear();
        }

        private void Add(DewGUI.FieldBuilderCondition condition, DewGUI.FieldBuilder builder)
        {
            // Ahead of the stock builders, which would otherwise claim the field first.
            var entry = (condition, builder);
            DewGUI.fieldBuilders.Insert(0, entry);
            _installed.Add(entry);
        }

        private bool Owned(FieldInfo info)
        {
            return info != null && info.DeclaringType == _owner;
        }

        // ----- on/off button --------------------------------------------------------

        private static FieldBuildResult BuildToggle(Type type, FieldInfo info, Transform parent)
        {
            var toggle = UnityEngine.Object.Instantiate(DewGUI.widgetToggleButton, parent);

            // UI_ToggleGroup only appears in menus, but if one ever turned up in the parent chain
            // a matching index would stop the toggle being switched off. A distinct index keeps
            // that branch out of the way.
            toggle.index = -1;

            var layout = toggle.GetComponent<LayoutElement>();
            if (layout == null) layout = toggle.gameObject.AddComponent<LayoutElement>();
            layout.ignoreLayout = false;
            layout.minWidth = ToggleWidth;
            layout.preferredWidth = ToggleWidth;
            layout.flexibleWidth = 0f;
            layout.minHeight = ToggleHeight;
            layout.preferredHeight = ToggleHeight;

            var result = new FieldBuildResult { root = toggle.gameObject };

            void ShowValue()
            {
                DewGUI.SetText(toggle.gameObject, OnOffText(toggle.isChecked));

                // The same lines the isChecked setter runs - but that setter returns early when
                // the value has not changed, and neither Awake nor Start touches these, so a
                // toggle whose stored value already matches would never be initialised. This
                // prefab has only an onObject, a highlight; the null check is for the other.
                if (toggle.onObject != null) toggle.onObject.SetActive(toggle.isChecked);
                if (toggle.offObject != null) toggle.offObject.SetActive(!toggle.isChecked);
            }

            // UI_Toggle raises onIsCheckedChanged even when isChecked is set from code, so the
            // window pushing the stored value in would come straight back as a user edit.
            bool pushing = false;

            result.getValue = () => toggle.isChecked;
            result.setValue = value =>
            {
                pushing = true;
                try { toggle.isChecked = Convert.ToBoolean(value); }
                finally { pushing = false; }
                ShowValue();
            };

            // onChanged is null until the window wires itself in, exactly as it is for the stock
            // builders, so it is read at call time rather than captured now.
            toggle.onIsCheckedChanged.AddListener(value =>
            {
                ShowValue();
                if (!pushing) result.onChanged?.Invoke(value);
            });

            ShowValue();
            return result;
        }

        // widgetToggleButton does not carry these words - its onObject is a highlight, it has no
        // offObject, and the one caption it has is whatever you write - so they come from the
        // localization table.
        //
        // Read the value and write it with SetText rather than calling SetTextLocalized, which
        // only assigns the string: the prefab's own DewLocalizedText survives that, and would
        // overwrite the caption with its own key the next time it updated. SetText drops it.
        private static string OnOffText(bool on)
        {
            if (DewLocalization.TryGetUIValue(on ? OnKey : OffKey, out var text)
                && !string.IsNullOrEmpty(text))
                return text;

            // Only if a later patch renames them.
            return on ? "ON" : "OFF";
        }

        // ----- slider ---------------------------------------------------------------

        private static FieldBuildResult BuildSlider(Type type, FieldInfo info, Transform parent)
        {
            var range = info.GetCustomAttribute<RangeAttribute>();
            bool whole = type == typeof(int);
            string format = whole ? WholeFormat : FractionFormat;

            var slider = UnityEngine.Object.Instantiate(DewGUI.widgetSlider, parent);

            // Order matters. The prefab is a 0..1 slider, and assigning a minimum above the
            // current maximum clamps on the way past, leaving a slider with no range at all.
            slider.maxValue = Mathf.Max(range.max, range.min);
            slider.minValue = range.min;
            slider.wholeNumbers = whole;
            slider.interactable = true;

            // The row is a HorizontalLayoutGroup controlling both child width and height, so the
            // LayoutElement decides the size - and every field of it has to be set, because a
            // Slider offers the layout system no size of its own. Height especially: left unset
            // the rect collapses to zero while still looking correct, since the bar, fill and
            // handle are anchored and keep drawing outside the collapsed parent.
            var layout = slider.GetComponent<LayoutElement>();
            if (layout == null) layout = slider.gameObject.AddComponent<LayoutElement>();
            layout.ignoreLayout = false;
            layout.minWidth = SliderWidth;
            layout.preferredWidth = SliderWidth;

            // No flexible width. With it the slider swallows whatever the row has left over -
            // around a thousand units - and preferredWidth stops having any visible effect, which
            // makes the widget look the same size whatever it is set to.
            layout.flexibleWidth = 0f;
            layout.minHeight = SliderHeight;
            layout.preferredHeight = SliderHeight;

            // Several of the prefab's graphics arrive with raycastTarget off, which leaves only
            // the handle draggable rather than the whole track clickable.
            foreach (var graphic in slider.GetComponentsInChildren<Graphic>(true)) graphic.raycastTarget = true;

            if (slider.GetComponent<Graphic>() == null)
            {
                var hitArea = slider.gameObject.AddComponent<Image>();
                hitArea.color = new Color(0f, 0f, 0f, 0f);
                hitArea.raycastTarget = true;
            }

            // The prefab already carries a readout of its own - a UI_SliderValueDisplay writing
            // slider.value.ToString(format), with format set to a percentage. Reusing it and
            // changing the format avoids a second number sitting next to the first.
            var display = slider.GetComponentInChildren<UI_SliderValueDisplay>();
            TextMeshProUGUI readout = null;
            if (display != null)
            {
                display.format = format;
                readout = display.GetComponent<TextMeshProUGUI>();

                // Authored wide enough for a percentage. The space it gives back is track.
                if (readout != null && readout.transform is RectTransform readoutRect)
                {
                    var size = readoutRect.sizeDelta;
                    size.x = ReadoutWidth;
                    readoutRect.sizeDelta = size;
                }
            }

            // Both the fill and the handle live in stretched parents whose horizontal insets are
            // what reserve room for that text. Narrowing them is what keeps the track usable.
            SetHorizontalInsets(slider.fillRect != null ? slider.fillRect.parent as RectTransform : null);
            SetHorizontalInsets(slider.handleRect != null ? slider.handleRect.parent as RectTransform : null);

            var result = new FieldBuildResult { root = slider.gameObject };

            // The display refreshes itself off onValueChanged, which SetValueWithoutNotify
            // deliberately does not raise, so the text is written here as well.
            void ShowValue()
            {
                if (readout != null) readout.text = slider.value.ToString(format);
            }

            // The field is an int or a float, and the window writes back whatever it is handed.
            object Read() => whole ? (object)Mathf.RoundToInt(slider.value) : slider.value;

            result.getValue = Read;
            result.setValue = value =>
            {
                slider.SetValueWithoutNotify(Convert.ToSingle(value));
                ShowValue();
            };

            slider.onValueChanged.AddListener(_ =>
            {
                ShowValue();
                result.onChanged?.Invoke(Read());
            });

            ShowValue();

            // The row was laid out before this widget joined it, and the sizes above only take
            // effect on the next rebuild.
            if (parent is RectTransform row) LayoutRebuilder.ForceRebuildLayoutImmediate(row);

            return result;
        }

        private static void SetHorizontalInsets(RectTransform rt)
        {
            if (rt == null) return;

            var min = rt.offsetMin;
            min.x = TrackLeftInset;
            rt.offsetMin = min;

            var max = rt.offsetMax;
            max.x = -TrackRightInset;
            rt.offsetMax = max;
        }
    }
}
