using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

namespace DevTools
{
    // What the picker window asks of the game's input while it is in the way. It is IMGUI, which
    // the game's own "is the cursor over the interface" raycast cannot see, so without this a
    // click on the list would also walk the hero there and typing "q" in the search box would
    // cast Q.
    internal static class InputBlock
    {
        // Written by the picker every frame.
        public static bool PointerOverOverlay;
        public static bool KeyboardCaptured;

        public static void Reset()
        {
            PointerOverOverlay = false;
            KeyboardCaptured = false;
        }
    }

    // A mouse the server can drive. The game reads mouse buttons and the cursor position through
    // legacy Input, which nothing outside the engine can feed, so the reads are answered here
    // instead by the patches below: DewInput's three button reads, ControlManager's cursor
    // position, and the check that the cursor is over the game rather than the interface.
    //
    // Presses are scheduled by frame rather than held as flags: a press asked for during frame N
    // is "down" in frame N+1, "held" from then on, and "up" in the frame its release lands in -
    // exactly what a real button gives, whatever order this and the game's Update run in.
    internal static class VirtualMouse
    {
        private const int Buttons = 8;

        public static bool Active { get; private set; }
        public static Vector2 Position { get; private set; }

        private static readonly int[] DownFrame = Enumerable.Repeat(-1, Buttons).ToArray();
        private static readonly int[] UpFrame = Enumerable.Repeat(-1, Buttons).ToArray();

        public static void MoveTo(Vector2 screen)
        {
            Active = true;
            Position = screen;
        }

        public static void Down(MouseButton button)
        {
            Active = true;
            DownFrame[(int)button] = Time.frameCount + 1;
            UpFrame[(int)button] = -1;
        }

        // Never in the same frame as the press it ends, or the press would not be seen at all.
        public static void Up(MouseButton button)
        {
            int down = DownFrame[(int)button];
            UpFrame[(int)button] = Mathf.Max(Time.frameCount + 1, down + 1);
        }

        public static bool IsHeld(MouseButton button) => Get(button);

        // Back to the real mouse.
        public static void Release()
        {
            Active = false;
            for (int i = 0; i < Buttons; i++)
            {
                DownFrame[i] = -1;
                UpFrame[i] = -1;
            }
        }

        public static bool Handles(MouseButton button) =>
            Active && button >= MouseButton.Left && button <= MouseButton.Back;

        public static bool GetDown(MouseButton button) => DownFrame[(int)button] == Time.frameCount;

        public static bool Get(MouseButton button)
        {
            int down = DownFrame[(int)button], up = UpFrame[(int)button];
            int now = Time.frameCount;
            return down >= 0 && now >= down && (up < 0 || now < up);
        }

        public static bool GetUp(MouseButton button) => UpFrame[(int)button] == Time.frameCount;
    }

    // A keyboard the server can drive. Unlike the mouse, the game reads keys through the Input
    // System, and in several different ways - wasPressedThisFrame, isPressed, its own per-frame
    // caches - so rather than patch each, the keyboard device itself is given the state: every
    // change queues a full KeyboardState holding every key this has down. All the reads then
    // agree, because they are all reading a real device.
    //
    // A real key pressed at the same time overwrites the state and is overwritten by the next
    // change here; an agent at the controls is the case this is for.
    internal static class VirtualKeyboard
    {
        private static readonly HashSet<Key> Held = new HashSet<Key>();

        public static IReadOnlyCollection<Key> HeldKeys => Held;

        public static void Down(Key key)
        {
            if (Held.Add(key)) Apply();
        }

        public static void Up(Key key)
        {
            if (Held.Remove(key)) Apply();
        }

        public static void ReleaseAll()
        {
            if (Held.Count == 0) return;
            Held.Clear();
            Apply();
        }

        private static void Apply()
        {
            var keyboard = Keyboard.current;
            if (keyboard == null) throw new DevException("no keyboard device");

            // The Input System disables the keyboard when the window loses focus, unless told to
            // ignore focus first - and the window has usually lost it before the server could say
            // so. A disabled device drops every event queued for it.
            if (!keyboard.enabled) InputSystem.EnableDevice(keyboard);

            InputSystem.QueueStateEvent(keyboard, new KeyboardState(Held.ToArray()));
        }

        public static Key Parse(string name)
        {
            if (string.IsNullOrEmpty(name)) throw new DevException("no key given");

            string text = name.Trim();
            switch (text.ToLowerInvariant())
            {
                case "esc": return Key.Escape;
                case "ctrl": case "control": return Key.LeftCtrl;
                case "shift": return Key.LeftShift;
                case "alt": return Key.LeftAlt;
                case "return": return Key.Enter;
                case "del": return Key.Delete;
                case "`": case "backquote": case "tilde": return Key.Backquote;
            }
            if (text.Length == 1 && char.IsDigit(text[0])) return Key.Digit0 + (text[0] - '0');
            if (Enum.TryParse(text, true, out Key key) && key != Key.None) return key;
            throw new DevException("no key named '" + name + "' - Input System key names: Q, Space, Escape, LeftCtrl, F1, Digit1 ...");
        }
    }

    // ----- patches ---------------------------------------------------------------------------
    //
    // One patch per method, reading both the picker's block and the virtual mouse, so that there
    // is never a question of which of two prefixes on the same method wins.

    [HarmonyPatch(typeof(DewInput), "GetMouseButtonDown_Imp")]
    internal static class MouseDownPatch
    {
        private static bool Prefix(MouseButton m, ref bool __result)
        {
            if (VirtualMouse.Handles(m)) { __result = VirtualMouse.GetDown(m); return false; }
            if (InputBlock.PointerOverOverlay && m >= MouseButton.Left && m <= MouseButton.Middle) { __result = false; return false; }
            return true;
        }
    }

    [HarmonyPatch(typeof(DewInput), "GetMouseButton_Imp")]
    internal static class MouseHeldPatch
    {
        private static bool Prefix(MouseButton m, ref bool __result)
        {
            if (VirtualMouse.Handles(m)) { __result = VirtualMouse.Get(m); return false; }
            if (InputBlock.PointerOverOverlay && m >= MouseButton.Left && m <= MouseButton.Middle) { __result = false; return false; }
            return true;
        }
    }

    [HarmonyPatch(typeof(DewInput), "GetMouseButtonUp_Imp")]
    internal static class MouseUpPatch
    {
        private static bool Prefix(MouseButton m, ref bool __result)
        {
            if (VirtualMouse.Handles(m)) { __result = VirtualMouse.GetUp(m); return false; }
            return true;
        }
    }

    // The cursor the game aims, moves and picks targets with. Everything it does with the cursor
    // funnels through this one method.
    [HarmonyPatch(typeof(ControlManager), nameof(ControlManager.GetMousePositionWithInversionInMind))]
    internal static class CursorPositionPatch
    {
        private static bool Prefix(ref Vector2 __result)
        {
            if (!VirtualMouse.Active) return true;
            __result = VirtualMouse.Position;
            return false;
        }
    }

    // Normally a raycast of the interface under the real cursor, to tell a click on the game from
    // a click on a button. The virtual mouse is always "on the game" - interface clicks go through
    // the API's own UI calls instead - and the picker is always "not".
    [HarmonyPatch(typeof(DewInput), nameof(DewInput.IsGameRelatedMouseInputValid))]
    internal static class GameAreaPatch
    {
        private static bool Prefix(ref bool __result)
        {
            if (VirtualMouse.Active) { __result = true; return false; }
            if (InputBlock.PointerOverOverlay) { __result = false; return false; }
            return true;
        }
    }

    // The game already stops reading the hero's keys while a text field has focus - that is how
    // chat works. Saying the picker's search box is one gets the same for free.
    [HarmonyPatch(typeof(ControlManager), nameof(ControlManager.IsInputFieldFocused))]
    internal static class TextFieldPatch
    {
        private static void Postfix(ref bool __result)
        {
            if (InputBlock.KeyboardCaptured) __result = true;
        }
    }
}
