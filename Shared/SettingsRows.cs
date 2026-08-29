using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Shared
{
    // Two things every mod's settings screen needs, and neither is available any other way.
    //
    // Alignment: each setting is a horizontal row of label then control, and the game sizes both to
    // their contents. That staggers the rows twice over - field names of differing length start
    // their controls at differing x, and values of differing length make the boxes themselves
    // differing widths. Pinning both widths lines the whole column up.
    //
    // Labels: the game names each row Dew.NicifyVariableName(field.Name). ModConfig.LabelText can
    // replace that, but it takes a compile-time constant and so can only ever be one language, so a
    // localized label has to be written in afterwards. Rows are matched by their text rather than
    // by position, which survives fields being reordered and headers appearing between them.
    internal static class SettingsRows
    {
        public static void Polish(Transform parent, int firstOwnRow, float labelWidth, float inputWidth,
                                  IDictionary<string, string> translated = null)
        {
            for (int i = firstOwnRow; i < parent.childCount; i++)
            {
                var row = parent.GetChild(i);

                // Only the setting rows; headers and spacers are not layout groups.
                if (row.GetComponent<HorizontalLayoutGroup>() == null) continue;

                // The label is added before the control, so it is the first text in the row.
                var label = row.GetComponentInChildren<TextMeshProUGUI>();
                if (label != null)
                {
                    DewGUI.SetWidth(label, labelWidth);
                    if (translated != null && translated.TryGetValue(label.text, out var text)) label.text = text;
                }

                // Nothing to pin once a field is a slider or a button, but a plain number still
                // gets a text box and still needs it.
                var input = row.GetComponentInChildren<TMP_InputField>();
                if (input != null) DewGUI.SetWidth(input, inputWidth);
            }
        }
    }
}
