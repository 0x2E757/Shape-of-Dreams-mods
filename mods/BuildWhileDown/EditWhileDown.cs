using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace BuildWhileDown
{
    // Where the game says no, and how each no is turned around.
    //
    // Four refusals stand between a knocked-out player and their own loadout, and they are not the
    // same kind of thing:
    //
    //   1. EditSkillManager.FrameUpdate returns early while spectating, and gates the hold key on
    //      shouldProcessCharacterInput. This is the one that matters; without it nothing opens.
    //   2. EditSkillManager.LogicUpdate closes an open edit as soon as spectating begins.
    //   3. UI_InGame_SkillButtonsBottomBar.UpdateVis fades the bar to alpha 0 while spectating, so
    //      even an open edit would be invisible.
    //   4. Two input triggers - the toggle key and the interact key that leaves edit mode - are
    //      built with lambdas that read shouldProcessCharacterInput.
    //
    // The first three are property reads inside named methods, and they are swapped for the
    // stand-ins in Down.cs by transpiler. The fourth cannot be: a lambda is its own compiler-named
    // method, and matching <InitializeTriggers>b__37_9 by name is a promise this mod cannot keep
    // across a patch. Those two are wrapped after the fact instead, on the public field the game
    // built them into.
    //
    // **A transpiler rather than a gate held open around the call.** Opening a static flag in a
    // prefix and closing it in a postfix - the shape MapAutoRoute uses for IsNodeConnected - would
    // be wrong here and quietly so. Reading it_editSkillToggle.down inside FrameUpdate calls
    // InputManager.PrepareInputs(), which re-evaluates isValidCheck for *every* input trigger in
    // the game; with a flag open, the cast and movement triggers would all be told the hero is
    // fine. Swapping the instruction touches the one method it is written into and nothing that
    // method calls.
    internal static class Targets
    {
        public static readonly MethodInfo ShouldProcessCharacterInput =
            AccessTools.PropertyGetter(typeof(ControlManager), nameof(ControlManager.shouldProcessCharacterInput));

        public static readonly MethodInfo IsSpectating =
            AccessTools.PropertyGetter(typeof(CameraManager), nameof(CameraManager.isSpectating));

        public static readonly MethodInfo OurProcessInput =
            AccessTools.Method(typeof(Down), nameof(Down.ProcessInput));

        public static readonly MethodInfo OurSpectating =
            AccessTools.Method(typeof(Down), nameof(Down.Spectating));
    }

    internal static class Swap
    {
        // Replaces every call to one method with a call to another of the same shape, and says so
        // if the count is not what was expected.
        //
        // The count is the point. A transpiler that silently matches nothing is a mod that does
        // nothing and reports that it loaded, which is the worst of both; a game patch that moves
        // one of these reads should leave a line in the log rather than a mystery.
        public static List<CodeInstruction> Calls(IEnumerable<CodeInstruction> code, MethodInfo from,
                                                  MethodInfo to, int expected, string where)
        {
            var list = new List<CodeInstruction>(code);

            // AccessTools returns null for a member that is not there, and a game that renamed one
            // of these should leave the method it appears in exactly as it was rather than taking
            // the patch down with a NullReferenceException.
            if (from == null || to == null)
            {
                Debug.LogWarning($"[BuildWhileDown] {where}: the method to swap could not be resolved; "
                                 + "that part of the mod is inert.");
                return list;
            }

            int found = 0;

            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].opcode != OpCodes.Call && list[i].opcode != OpCodes.Callvirt) continue;
                if (!(list[i].operand is MethodInfo called)) continue;

                // By declaring type and name rather than by reference: the operand Harmony hands
                // back is not guaranteed to be the same MethodInfo object AccessTools returned.
                if (called.DeclaringType != from.DeclaringType || called.Name != from.Name) continue;

                // Call rather than Callvirt. The stand-ins are static and take the instance as an
                // argument, so the stack is unchanged, but a virtual call to a static method is
                // not a thing the runtime will accept.
                list[i].opcode = OpCodes.Call;
                list[i].operand = to;
                found++;
            }

            if (found != expected)
            {
                Debug.LogWarning($"[BuildWhileDown] {where}: expected {expected} read(s) of {from.Name}, "
                                 + $"found {found}. The game has moved this; that part of the mod is inert.");
            }

            return list;
        }
    }

    // The one that matters. Both reads live here: the early return that refuses to run at all
    // while spectating, and the hold key that opens edit mode.
    [HarmonyPatch(typeof(EditSkillManager), nameof(EditSkillManager.FrameUpdate))]
    internal static class EditSkillFrameUpdatePatch
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> code)
        {
            var once = Swap.Calls(code, Targets.IsSpectating, Targets.OurSpectating,
                                  1, "EditSkillManager.FrameUpdate");
            return Swap.Calls(once, Targets.ShouldProcessCharacterInput, Targets.OurProcessInput,
                              1, "EditSkillManager.FrameUpdate");
        }
    }

    // And the one that would undo it a moment later: LogicUpdate ends any open edit the instant
    // the camera goes to a teammate, which in co-op is about two seconds after the knockout.
    [HarmonyPatch(typeof(EditSkillManager), nameof(EditSkillManager.LogicUpdate), typeof(float))]
    internal static class EditSkillLogicUpdatePatch
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> code)
        {
            return Swap.Calls(code, Targets.IsSpectating, Targets.OurSpectating,
                              1, "EditSkillManager.LogicUpdate");
        }
    }

    // The bar itself. UpdateVis works out an alpha from the edit mode and then flattens it to zero
    // if the camera is spectating; without this the loadout would be open and unlookable.
    //
    // It runs off two events - the mode changing and spectating changing - and both of them fire
    // while the hero is knocked out, which is exactly when the stand-in answers no. There is
    // nothing to force a repaint.
    [HarmonyPatch(typeof(UI_InGame_SkillButtonsBottomBar), "UpdateVis")]
    internal static class BottomBarVisibilityPatch
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> code)
        {
            return Swap.Calls(code, Targets.IsSpectating, Targets.OurSpectating,
                              1, "UI_InGame_SkillButtonsBottomBar.UpdateVis");
        }
    }

    // The two input triggers.
    //
    // Each is built with a lambda of the form "shouldProcessCharacterInput && <one more thing>",
    // and the wrapper below restates that one more thing rather than trying to recover it. Both
    // are a single comparison against the edit mode, copied from the lambdas as the game writes
    // them; they are stated here so that the wrapper cannot say yes in a case the original would
    // have said no to.
    //
    // The tail on the exit trigger is load-bearing in a way the toggle's is not. It shares the
    // interact key with the world, and a trigger that reports itself valid can consume its key -
    // so a wrapper that answered yes while no edit was open would take interact away from doors,
    // shrines and merchants.
    internal static class Triggers
    {
        public static void Wrap(DewInputTrigger trigger, System.Func<bool> tail)
        {
            if (trigger == null || tail == null) return;

            var original = trigger.isValidCheck;
            if (original == null) return;

            // Falls back to exactly the original when the mod is not live, so a wrapper left
            // behind on a manager that outlives the mod is inert rather than wrong.
            trigger.isValidCheck = () =>
            {
                if (original()) return true;

                var control = ControlManager.softInstance;
                if (control == null || !control.shouldProcessCharacterInputAllowKnockedOut) return false;

                return Down.Editing() && tail();
            };
        }

        public static bool NotEditing()
        {
            var edit = EditSkillManager.softInstance;
            return edit != null && edit.mode == EditSkillManager.ModeType.None;
        }

        public static bool Editing()
        {
            var edit = EditSkillManager.softInstance;
            return edit != null && edit.mode != EditSkillManager.ModeType.None;
        }
    }

    // The key that opens and closes edit mode. Built in ControlManager.InitializeTriggers, which
    // Awake calls, so the postfix arrives before anything can press it.
    [HarmonyPatch(typeof(ControlManager), "InitializeTriggers")]
    internal static class EditSkillTogglePatch
    {
        private static void Postfix(ControlManager __instance)
        {
            Triggers.Wrap(__instance.it_editSkillToggle, Triggers.NotEditing);
        }
    }

    // The interact key, which leaves edit mode. Private field, so it is reached by name.
    [HarmonyPatch(typeof(EditSkillManager), "Start")]
    internal static class ExitEditModePatch
    {
        private static readonly AccessTools.FieldRef<EditSkillManager, DewInputTrigger> ExitTrigger =
            AccessTools.FieldRefAccess<EditSkillManager, DewInputTrigger>("it_exitEditMode");

        private static void Postfix(EditSkillManager __instance)
        {
            Triggers.Wrap(ExitTrigger(__instance), Triggers.Editing);
        }
    }

    // Gamepad only, and the reason it is here at all: with a controller the loadout is reached by
    // focusing the bottom bar, and GlobalUIManager will not focus something that says it cannot be.
    // The body below is the game's own, with the first condition answered by the stand-in.
    [HarmonyPatch(typeof(UI_InGame_SkillButtons), nameof(UI_InGame_SkillButtons.CanBeFocused))]
    internal static class SkillButtonsFocusPatch
    {
        private static void Postfix(ref bool __result)
        {
            if (__result) return;

            var control = ControlManager.softInstance;
            if (control == null || !Down.ProcessInput(control)) return;

            __result = !control.isEditSkillDisabled || Triggers.Editing();
        }
    }
}
