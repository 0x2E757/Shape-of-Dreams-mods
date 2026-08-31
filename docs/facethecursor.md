# FaceTheCursor

The hero keeps looking at the cursor instead of along whatever direction it happens to be walking
in. The game already does this for the moment a memory is cast; the mod does it the rest of the
time.

It is three source files and one Harmony prefix, with no state carried between frames — the answer
is recomputed every frame from the pointer, because the pointer is where the answer already is.

    Facing.cs           the prefix, and everything it stands aside for
    FaceTheCursor.cs    the mod and its three settings
    Localization.cs     the three settings rows, in each language the game ships

## Where the angle is decided

`EntityControl._localDesiredAngle`. Every frame, for the hero this machine owns,
`DoMovementProcessorFrameUpdate` ends like this:

```csharp
DoVelocityUpdate(targetVelocity);
...
if (!overridenDesiredAngle.HasValue && targetVelocity.sqrMagnitude > 0.1f && _localVelocity.sqrMagnitude > 0.1f)
    _localDesiredAngle = CastInfo.GetAngle(_localVelocity);
DoRotateTowardsDesiredAngleTick(_localDesiredAngle);
...
UpdatePositionSyncData(new PositionSyncData
{
    timestamp = NetworkTime.time,
    position = vector2,
    velocity = _localVelocity,
    desiredAngle = _localDesiredAngle
});
```

Three things happen in that order and all three matter: the game decides the angle, it turns the
transform a step towards it, and it tells everyone else. **The mod is a prefix on the middle one.**

That is a deliberate choice over the obvious hook, which would be a postfix on
`DoMovementProcessorFrameUpdate` itself. A postfix runs after all three — so the rotation step
would be spending this frame turning towards last frame's answer, and the sync would be carrying
last frame's answer to the other players. Neither is visible on its own at sixty frames a second,
but they are both avoidable for nothing: a prefix on the tick sits between the decision and its two
consumers, which is exactly where a mod that wants to change the decision belongs. The prefix
writes both the `target` argument and the field, because those two consumers read different things
and writing one without the other would either turn a hero nobody else sees turning, or announce a
turn that never happened locally.

The second reason is early returns. `DoMovementProcessorFrameUpdate` leaves without reaching the
tick when the hero is spawning and again when it is being displaced, and a postfix runs after those
returns as well — so it would have to re-test both conditions, and a copy of somebody else's
conditions is a copy that can go stale. Nothing reaches the tick from those paths, so there is
nothing to test.

### The other three callers

`DoRotateTowardsDesiredAngleTick` has four call sites, and the prefix has to answer for all of
them. Three are excluded by conditions the mod checks for its own reasons anyway:

| Caller | Excluded by |
| --- | --- |
| `DoMovementProcessorFrameUpdate` | the one that is wanted |
| `DoMovementObserverFrameUpdate` | `isLocalMovementProcessor` — that branch is *chosen* by it being false |
| `DoDisplacement`, twice | `isDisplacing` — that path is *entered* by it being true |

On a host `isLocalMovementProcessor` is true for every monster in the room as well, which is why
the check that the entity is `DewPlayer.local.hero` is not decoration.

## What not to write

**`overridenDesiredAngle`.** It is the field that looks like it is for this and is not. It is a
Mirror SyncVar the server owns, computed in `DoOverrideRotationLogicUpdate` from `_overrideAngle`,
`_overrideAnglePosition` and `_overrideAngleEntity`, and a client writing it would have the write
undone by the next sync — after changing, in the meantime, what a cast does:

```csharp
if (triggerConfig.faceForward && (!flag || !owner.Control.overridenDesiredAngle.HasValue))
    OnRotateForward(configIndex, info);
```

`_localDesiredAngle` is the client's own and needs no permission. It reaches everyone else on its
own, in the sync data quoted above, so the hero faces the same way on every screen in the game
without this mod sending a byte.

The mod stands aside while `overridenDesiredAngle` has a value, which is the same thing the game
does two lines up. That is what leaves a cast that locks your facing for its duration still locking
it.

## Where the cursor is

`ControlManager.GetWorldPositionOnGroundOnCursor(bool forDirectionalAttacks = false)` — public,
static, and it takes the argument seriously: `false` projects the pointer onto the floor, `true`
onto a plane 0.75 higher.

```csharp
public static Vector3 GetWorldPositionOnGroundFromViewportPoint(Vector2 viewportPoint, bool forDirectionalAttacks)
{
    Vector3 vector = (forDirectionalAttacks ? new Vector3(0f, 0.75f, 0f) : Vector3.zero);
    ...
```

`vector` is the offset of the plane the pointer is cast onto, and that plane is anchored at the
camera's focused entity — so `true` raises it 0.75 above the hero's feet rather than laying it on
them. The mod passes `true`, because that is what the game passes when it asks which way a cast is
aimed and this is the same question. On a camera looking down at an angle the two answers are
several tenths apart near the hero, which is exactly where the answer matters most.

**The cache in that method does not cache.** It reads

```csharp
if (Time.frameCount != _cachedFrame)
    _cachedPosition = GetWorldPositionOnGroundFromScreenPoint(GetMousePositionWithInversionInMind(), forDirectionalAttacks);
return _cachedPosition;
```

and `_cachedFrame` is initialised to `-1` and never assigned anywhere in the class, so the
condition is always true and every call re-projects. Worth knowing in both directions. It costs a
raycast per frame rather than being free — acceptable, since the game already makes several — and
it means the mod cannot be handed a stale answer left over from a call the game made with the other
argument, which a working cache keyed only on the frame number certainly would.

`GetMousePositionWithInversionInMind` is inside that path, so anything that reverses a hero's
controls — `EntityControl.isControlReversed`, a SyncVar — reverses which way it looks too, without
the mod knowing such a thing exists.

### Gamepad

There is no cursor on a gamepad, and `Input.mousePosition` still answers — with wherever the mouse
was last left. A hero told to face that would lock onto one corner of the room and stay there, so
the branch is not optional:

```csharp
if (DewInput.currentMode == InputMode.Gamepad)
{
    var aim = controls.aimDirection;
    towards = aim ?? default(Vector3);
    return aim.HasValue;
}
```

`ControlManager.aimDirection` is the right stick, camera-relative, and is the same source the game
aims cone and arrow casts from in gamepad mode. It is null while the stick is at rest, and then the
mod has no opinion and the game goes on facing the hero along its movement — which is what a player
holding no aim direction is asking for anyway.

## When it stands aside

| Condition | Why |
| --- | --- |
| no `DewPlayer.local.hero`, or a different entity | one hero, the player's own |
| `!isLocalMovementProcessor` | someone else's hero, replayed from sync data |
| `isDisplacing` | a dash or a knockback is moving the hero |
| `overridenDesiredAngle.HasValue` | a cast is holding the facing |
| `!ControlManager.shouldProcessCharacterInput` | menus, cutscenes, zone transitions, knocked out, typing in chat |
| `InGameUIManager.isWorldDisplayed != None` | the world map is up and the pointer is on it |
| pointer nearer than `minCursorDistance` | no direction there, only noise |

`shouldProcessCharacterInput` is read rather than rebuilt. It is the game's own answer to whether
this player is steering right now, it is recomputed every frame in `ControlManager.FrameUpdate`,
and it already accounts for every screen in the game except one.

**The world map is that one.** It leaves character input on — which is why `FrameUpdate` tests
`isWorldDisplayed != 0` separately, right next to `shouldProcessCharacterInput`, when it decides
whether to drop the targeted enemy. Without the same test here the hero would sit turning towards
wherever the pointer happened to be resting on the map overlay.

## The animation, which is the real cost

There is one walk cycle and no strafe or backpedal. `EntityAnimation` hands the animator exactly
one movement parameter:

```csharp
animator.SetFloat("walkSpeedMultiplier", base.entity.Control.walkStrength * model.walkAnimationSpeed * ...);
```

Nothing about direction. So a hero that keeps facing the pointer while it runs the other way
moonwalks, and there is no fixing that from a mod — the animations to blend do not exist.

That is the whole reason **standing still and moving are separate settings**. Standing still, the
game has nothing to say about which way the hero points and the mod is pure gain. Moving, it costs
an animation, and whether that is a price worth paying is a matter of taste rather than of code.
Both default to on, because always facing the cursor is the thing the mod is for.

`minCursorDistance` is the third setting and exists for a smaller reason: with the pointer sitting
on the hero's own feet there is no angle to read, only the noise of it crossing back and forth, and
a hero spinning on the spot is worse than one holding still. Half a world unit, about a hero's
width, and the distance is measured flattened — the pointer is found on a plane and the hero stands
on one, so the height between them is not a distance anyone means.

## Where the facing reaches the game

Aiming does not move. A cast goes where the cursor is and always did:
`ControlManager.CastAbilityAtCursor` builds its `CastInfo` from `GetWorldPositionOnGroundOnCursor`,
and `new CastInfo(caster)` — the `CastMethodType.None` case — sets `angle = 0f` rather than reading
the caster's rotation.

**Two other things do follow the facing, and both were found late.** Neither touches another
player: they are the caster's own facing acting on the caster's own ability.

**`AbilityTrigger.GetInstanceSpawnRotation`** is the one that matters, because it is gameplay
rather than decoration:

```csharp
CastMethodType type = configs[configIndex].castMethod.type;
return (type != CastMethodType.Arrow && type != CastMethodType.Cone)
     ? new Quaternion?(owner.Control.desiredRotation)
     : new Quaternion?(info.rotation);
```

`OnCastComplete` passes that to `CreateAbilityInstance`, so for `Point`, `Target` and `None` the
spawned instance is rotated to wherever the caster is looking. How much the mod changes depends on
the cast method, and the difference is smaller than it first looks:

| Cast method | Vanilla facing at spawn | With the mod |
| --- | --- | --- |
| `Point` | `OnRotateForward` already turned the hero to the cast point, which is the cursor | the same |
| `Target` | turned to the target | the cursor, which is on or beside it |
| `None` | `OnRotateForward` does nothing, so whatever the hero was walking towards | the cursor |

So `CastMethodType.None` — a memory that spawns its effect with no direction of its own — is the
real case. Whether the rotation is visible at all depends on the shape of the instance.

**`ControlManager.CastAbilityInDirectionOfMovement`** is the second, and it is a fallback:

```csharp
Vector3 vector = (isLastMovementDirectionFresh ? (lastMovementDirection * 1f)
                                               : controllingEntity.transform.forward.Flattened().normalized);
```

With no movement input for 0.4 seconds it takes the transform, which the mod is turning. Reaching
it needs all of: the directional movement scheme (`isMovementSchemeDirectional`, latched on the
first use of the movement keys or the stick), a memory whose config sets
`castByMoveDirectionByDefault`, and `dashDirectionWhenDirectionalMovement` on something other than
`AllTowardsCursor`. A click-to-move player never gets there; a WASD player standing still and
dashing does.

Calling either a bug would be a stretch — both send the thing where the player was pointing — but
they are changes, they are the only two, and they are here so that the next person does not have to
find them.

## In co-op

**Two players who both have it see each other aim, and a player who does not have it is not touched
in either direction.** That falls out of the game rather than being built:

- The angle is decided on the machine that owns the hero, written to `_localDesiredAngle`, and sent
  in `UpdatePositionSyncData` → `CmdPositionSyncData` → the `_positionSyncData` SyncVar. So a hero
  whose owner has the mod is *seen* turning by everyone, mods or no mods.
- A hero whose owner does not have the mod sends the angle the unmodded game chose, and every
  observer — modded ones included — replays exactly that in `DoMovementObserverFrameUpdate`.

Nothing is sent by the mod, nothing is asked of the host, and no hero is moved by anybody but its
own owner.

### The half that was built and taken out again

The obvious extra feature is showing where a player *without* the mod is aiming. The data for it
exists: every client reports its cursor to the server thirty times a second in
`DewPlayer.LogicUpdate_InGame`, and the server keeps it in `DewPlayer.cursorWorldPos`, which is how
`ChargingChannel` keeps a charging cast turning with the caster's pointer. It is a plain property,
not a SyncVar, so only the host has it.

**It was built and then removed**, and the reasoning is worth keeping. Two findings came out of it:

- **An unknown Mirror message id is a disconnect, in both directions.** `NetworkClient` and
  `NetworkServer` both log `failed to unpack and invoke message` and then `Disconnect()`. So a
  modded host speaking to a vanilla guest kicks the guest, and a modded guest speaking to a vanilla
  host kicks *itself* out of someone else's game. Either side may only speak first to a peer known
  to be modded, and the game will not say: `isModded` is one lobby bool, and the JSON override
  exchange fires once, at join, and only in a lobby.
- **A second angle fits inside the replicated one.** Every consumer of
  `_positionSyncData.desiredAngle` is invariant under whole turns — `SmoothDampAngle` and
  `DeltaAngle` normalise the target against the current angle, `desiredRotation` is a
  `Quaternion.Euler` — the server's only check is `Dew.FilterNonOkayValues`, which rejects NaN and
  infinity and nothing else, and the wire format is a bare `WriteFloat`. So a host could write
  `display + 360 * (1 + aim)` and a vanilla client would render *provably* the same angle, with no
  new message and no handshake.

It works. It was dropped anyway, because of what it does rather than what it costs: the host would
be computing and distributing an aim direction for a player who never installed anything, and the
host cannot see that player's open map or inventory, so the fabricated angle is wrong exactly when
they are not looking at the game. A mod that only ever moves its own owner's hero is the better
object, and it is also the smaller one.
