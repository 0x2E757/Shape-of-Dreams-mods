using System;
using HarmonyLib;

namespace BuildWhileDown
{
    // The one thing that must not come through the door with the rest.
    //
    // Rearranging what is equipped is safe from anywhere - the server moves an essence between two
    // of your own slots and no world position is involved. Dropping something on the floor is not,
    // and the reason is where a knocked-out hero actually stands. Four of the five drop paths pass
    // hero.position, and until a teammate reaches your soul that position is wherever you fell,
    // which is very often a room the party has already left. An essence dropped there is gone.
    //
    // The fifth path drops at the cursor, which is at least in the room somebody is looking at -
    // but while spectating that is a teammate's room and still not yours, so it goes the same way.
    //
    // **ControlManager.dropConstraint is the game's own answer to this question**, not a lever
    // being repurposed: a Func<Object, bool> that every drop path consults before acting, and that
    // UI_InGame_FloatingSkill reads to decide whether to draw the discard prompt at all. Setting
    // it means the refusal is visible while dragging rather than a click that does nothing.
    //
    // It is one field on a shared manager, so it is put back the way it was found - and only if it
    // is still ours when the time comes, since nothing stops something else claiming it in between.
    internal static class GroundDrops
    {
        // A single cached delegate rather than a fresh lambda each time, so that "is the field
        // still ours?" is a reference comparison with an answer.
        private static readonly Func<UnityEngine.Object, bool> Refuse = _ => false;

        private static ControlManager _installedOn;
        private static Func<UnityEngine.Object, bool> _previous;

        public static void Update(ControlManager control)
        {
            if (control == null) return;

            if (Down.Editing()) Install(control);
            else Restore();
        }

        private static void Install(ControlManager control)
        {
            if (ReferenceEquals(_installedOn, control)) return;

            Restore();
            _previous = control.dropConstraint;
            control.dropConstraint = Refuse;
            _installedOn = control;
        }

        public static void Restore()
        {
            // Unity's == rather than ReferenceEquals: a manager destroyed with the scene compares
            // equal to null, and there is nothing left to put back on it.
            if (_installedOn != null && ReferenceEquals(_installedOn.dropConstraint, Refuse))
            {
                _installedOn.dropConstraint = _previous;
            }

            _installedOn = null;
            _previous = null;
        }
    }

    // Every frame, on the manager that owns the field. There is no event for "the local hero was
    // knocked out" on the client - the knockout arrives as a SyncVar - so the state is read rather
    // than waited for. It costs three null checks.
    [HarmonyPatch(typeof(ControlManager), nameof(ControlManager.FrameUpdate))]
    internal static class DropConstraintPatch
    {
        private static void Postfix(ControlManager __instance) => GroundDrops.Update(__instance);
    }
}
