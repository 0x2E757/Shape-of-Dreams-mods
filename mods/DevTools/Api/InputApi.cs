#if DEBUG
using System;
using System.Collections;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace DevTools
{
    // Raw input, for whatever the other routes do not reach: the keys go to the Input System's
    // keyboard device, the mouse to VirtualMouse (see VirtualInput for why the two differ). The
    // virtual mouse takes over the cursor from the first call until /input/release; while it has
    // it, the real mouse's clicks do not reach the game.
    //
    // It drives the game, not the interface: menus read the real pointer through the EventSystem,
    // so clicking a button is /ui/click.
    internal static class InputApi
    {
        [Route("GET", "/input", "What the virtual mouse and keyboard are doing, and where the real cursor is.")]
        private static object State(Args a) => new
        {
            virtualMouse = VirtualMouse.Active ? new
            {
                x = Mathf.RoundToInt(VirtualMouse.Position.x),
                y = Mathf.RoundToInt(VirtualMouse.Position.y),
                left = VirtualMouse.IsHeld(MouseButton.Left),
                right = VirtualMouse.IsHeld(MouseButton.Right),
                ground = GameAccess.ScreenToGround(VirtualMouse.Position) is Vector3 g ? Describe.Vec(g) : null,
            } : null,
            heldKeys = VirtualKeyboard.HeldKeys.Select(k => k.ToString()),
            realCursor = new { x = Mathf.RoundToInt(Input.mousePosition.x), y = Mathf.RoundToInt(Input.mousePosition.y) },
            screen = new { w = Screen.width, h = Screen.height },
        };

        [Route("POST", "/input/key", "Press keys on the keyboard device. tap presses and releases (all together, like a chord); down and up hold and let go.",
               "key | keys [names], action=tap|down|up, hold=0.1 (seconds, for tap)")]
        private static IEnumerator Keys(Args a)
        {
            var names = a.Token("keys") is JArray list ? list.Select(k => (string)k).ToArray() : new[] { a.Str("key") };
            var keys = names.Select(VirtualKeyboard.Parse).ToArray();
            string action = a.Str("action", "tap").ToLowerInvariant();

            switch (action)
            {
                case "down":
                    foreach (var k in keys) VirtualKeyboard.Down(k);
                    break;
                case "up":
                    foreach (var k in keys) VirtualKeyboard.Up(k);
                    break;
                case "tap":
                    foreach (var k in keys) VirtualKeyboard.Down(k);
                    // Queued events land at the start of the next frame, so the press needs two
                    // frames to be seen as one before the release is queued.
                    yield return null;
                    yield return null;
                    yield return new WaitForSecondsRealtime(Mathf.Clamp(a.Float("hold", 0.1f), 0f, 30f));
                    foreach (var k in keys.Reverse()) VirtualKeyboard.Up(k);
                    yield return null;
                    yield return null;
                    break;
                default:
                    throw new DevException("action is tap, down or up");
            }
            yield return new Reply(new { keys = keys.Select(k => k.ToString()), action, held = VirtualKeyboard.HeldKeys.Select(k => k.ToString()) });
        }

        [Route("POST", "/input/mouse", "Move the virtual cursor to a screen point (x,y pixels from bottom-left) or a world point (wx,wz), and optionally click or press a button there.",
               "x,y | wx,wz, action=move|click|down|up, button=left|right|middle, hold=0.05")]
        private static IEnumerator MouseAction(Args a)
        {
            Vector2 screen;
            if (a.Has("wx") || a.Has("wz"))
            {
                var world = GameAccess.Ground(a.Float("wx"), a.Float("wz"), a.Has("wy") ? a.Float("wy") : (float?)null);
                screen = GameAccess.ToScreen(world) ?? throw new DevException("that point is behind the camera");
            }
            else if (a.Has("x") || a.Has("y"))
            {
                screen = new Vector2(a.Float("x"), a.Float("y"));
            }
            else
            {
                screen = VirtualMouse.Active ? VirtualMouse.Position : (Vector2)Input.mousePosition;
            }

            VirtualMouse.MoveTo(screen);
            var button = ParseButton(a.Str("button", "left"));
            string action = a.Str("action", "move").ToLowerInvariant();

            switch (action)
            {
                case "move":
                    break;
                case "down":
                    VirtualMouse.Down(button);
                    break;
                case "up":
                    VirtualMouse.Up(button);
                    break;
                case "click":
                    VirtualMouse.Down(button);
                    yield return null;
                    yield return null;
                    yield return new WaitForSecondsRealtime(Mathf.Clamp(a.Float("hold", 0.05f), 0f, 30f));
                    VirtualMouse.Up(button);
                    yield return null;
                    yield return null;
                    break;
                default:
                    throw new DevException("action is move, click, down or up");
            }

            yield return new Reply(new
            {
                x = Mathf.RoundToInt(screen.x),
                y = Mathf.RoundToInt(screen.y),
                ground = GameAccess.ScreenToGround(screen) is Vector3 g ? Describe.Vec(g) : null,
                action,
                button = button.ToString(),
            });
        }

        private static MouseButton ParseButton(string name)
        {
            switch (name.Trim().ToLowerInvariant())
            {
                case "left": case "l": case "0": return MouseButton.Left;
                case "right": case "r": case "1": return MouseButton.Right;
                case "middle": case "m": case "2": return MouseButton.Middle;
                default: throw new DevException("button is left, right or middle");
            }
        }

        [Route("POST", "/input/release", "Let go of every virtual key and button, and give the cursor back to the real mouse.")]
        private static object Release(Args a)
        {
            VirtualMouse.Release();
            VirtualKeyboard.ReleaseAll();
            return new { released = true };
        }
    }
}
#endif
