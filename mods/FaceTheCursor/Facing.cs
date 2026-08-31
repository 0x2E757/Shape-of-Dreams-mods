using HarmonyLib;
using UnityEngine;

namespace FaceTheCursor
{
    // Which way the hero is pointing is `EntityControl._localDesiredAngle`, and on every frame the
    // hero is moving the game overwrites it from the direction of travel:
    //
    //     if (!overridenDesiredAngle.HasValue && targetVelocity.sqrMagnitude > 0.1f
    //                                         && _localVelocity.sqrMagnitude > 0.1f)
    //         _localDesiredAngle = CastInfo.GetAngle(_localVelocity);
    //     DoRotateTowardsDesiredAngleTick(_localDesiredAngle);
    //     ...
    //     UpdatePositionSyncData(new PositionSyncData { ..., desiredAngle = _localDesiredAngle });
    //
    // That call is the last word on the angle, so a prefix on it is the last word too - after the
    // game has decided, and still before the frame's rotation step and before the angle is sent to
    // everyone else. Which is why this is a prefix on the tick rather than a postfix on
    // DoMovementProcessorFrameUpdate, the enclosing method: a postfix runs after the sync as well
    // as after the rotation, so the hero would turn a frame late on this machine and a frame later
    // still on everyone else's. It also runs after the method's two early returns, which would
    // then have to be tested a second time here - and a copy of somebody else's conditions is a
    // copy that can go stale. Nothing reaches the tick from those paths at all.
    //
    // **`overridenDesiredAngle` is not the field to write.** It is a Mirror SyncVar the server
    // owns, fed from `_overrideAngle`, `_overrideAnglePosition` and `_overrideAngleEntity`, and
    // `AbilityTrigger.OnCastStart` reads `overridenDesiredAngle.HasValue` when deciding whether
    // `faceForward` applies. A client writing it would be overwritten on the next sync and would
    // change what a cast does in the meantime. `_localDesiredAngle` is the client's own, and it
    // travels: `UpdatePositionSyncData` sends it alongside position and velocity, so the hero
    // faces the same way on every machine without this mod sending anything.
    [HarmonyPatch(typeof(EntityControl), "DoRotateTowardsDesiredAngleTick")]
    internal static class Facing
    {
        private static readonly AccessTools.FieldRef<EntityControl, float> LocalDesiredAngle =
            AccessTools.FieldRefAccess<EntityControl, float>("_localDesiredAngle");

        private static void Prefix(EntityControl __instance, ref float target)
        {
            if (__instance == null || !TryGetAngle(__instance, out float angle)) return;

            // Both, and neither is redundant. `target` is what this frame's rotation step turns
            // towards; the field is what is sent to the other players a few lines further down.
            // Writing one and not the other would either turn a hero nobody else sees turning, or
            // announce a turn that never happened here.
            LocalDesiredAngle(__instance) = angle;
            target = angle;
        }

        // The tick has four callers, and only one of them is the frame's movement decision. The
        // other three are ruled out rather than tested for directly, which saves this from having
        // to know where they are:
        //
        //   DoMovementObserverFrameUpdate  another player's hero, replayed from sync data
        //                                  - isLocalMovementProcessor is false there by
        //                                    definition, that being the branch it is chosen by
        //   DoDisplacement, twice          a dash or a knockback in progress
        //                                  - isDisplacing is what puts the game in that path
        //
        // On a host isLocalMovementProcessor is true for every monster in the room as well, so
        // the hero check is not decoration.
        private static bool TryGetAngle(EntityControl control, out float angle)
        {
            angle = 0f;

            var config = FaceTheCursorMod.Live;
            if (config == null) return false;

            var player = DewPlayer.local;
            var hero = player != null ? player.hero : null;
            if (hero == null || control.entity != hero) return false;

            if (!control.isLocalMovementProcessor || control.isDisplacing) return false;

            // A cast that overrides rotation holds the angle for its duration, and the game stops
            // deciding for itself while it does. So does this.
            if (control.overridenDesiredAngle.HasValue) return false;

            // Menus, cutscenes, zone transitions, being knocked out, typing in chat: the game's
            // own answer to whether this player is steering right now, read rather than rebuilt.
            var controls = ControlManager.softInstance;
            if (controls == null || !controls.shouldProcessCharacterInput) return false;

            // The world map is the one screen that leaves character input on. The pointer is over
            // the map while it is up, so it is not pointing anywhere in the room.
            var ui = UIManager.softInstance as InGameUIManager;
            if (ui != null && ui.isWorldDisplayed != WorldDisplayStatus.None) return false;

            // 0.1f is the game's own threshold for moving, on the line this one replaces.
            bool moving = control.agentVelocity.sqrMagnitude > 0.1f;
            if (!(moving ? config.whileMoving : config.whileStandingStill)) return false;

            if (!TryGetAimVector(controls, hero, out var towards)) return false;

            // Under the hero there is no direction to speak of, only noise, and a hero spinning on
            // the spot because the pointer is sitting on its feet is worse than one that holds
            // still. Flattened first: the pointer is found on the ground plane and the hero stands
            // on it, so the height between them is not a distance anyone means.
            towards = towards.Flattened();
            if (towards.sqrMagnitude < config.minCursorDistance * config.minCursorDistance) return false;

            angle = CastInfo.GetAngle(towards);
            return true;
        }

        // Where the player is pointing, which is not the same question on the two input devices.
        //
        // On a gamepad there is no cursor: Input.mousePosition still answers, with wherever the
        // mouse was last left, and a hero told to face that would lock onto a corner of the room
        // and stay there. So the stick is asked instead, the same one the game aims cone and arrow
        // casts with, and when it is at rest the mod has no opinion and the game goes on facing
        // the hero along its movement.
        private static bool TryGetAimVector(ControlManager controls, Hero hero, out Vector3 towards)
        {
            if (DewInput.currentMode == InputMode.Gamepad)
            {
                var aim = controls.aimDirection;
                towards = aim ?? default(Vector3);
                return aim.HasValue;
            }

            // forDirectionalAttacks: true is what the game passes when it works out which way a
            // cast is aimed, and that is this same question. What the flag moves is the plane the
            // pointer is cast onto: 0.75 above the hero's feet rather than laid on them, which on
            // a camera looking down at an angle is several tenths of a unit near the hero - which
            // is exactly where the answer matters most.
            //
            // The name says cached and the cache does not work: _cachedFrame is initialised to -1
            // and never assigned, so every call re-projects. Worth knowing in both directions -
            // this costs a raycast per frame rather than being free, and it cannot be handed a
            // stale answer left over from a call the game made with the other argument.
            towards = ControlManager.GetWorldPositionOnGroundOnCursor(forDirectionalAttacks: true)
                    - hero.transform.position;
            return true;
        }
    }
}
