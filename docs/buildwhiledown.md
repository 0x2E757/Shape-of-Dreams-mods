# BuildWhileDown

The loadout screen goes on working while your own hero is knocked out. Everything else about being
down is untouched.

Line numbers are from `Dew.Core` and `Dew.UI` decompiled with `ilspycmd` against the install
`Directory.Build.props` points at, and will drift. The type and method names are the durable part.

## The server never cared

Worth establishing first, because it decides how much of this can be a client-side mod.
`UserCode_CmdEquipGem_Internal` calls `EquipGem`, `UserCode_CmdEquipSkill_Internal` calls
`EquipSkill`, and while those validate ownership and refuse a second essence of the same type,
**neither looks at `isKnockedOut`**. An unmodded host accepts these commands from a knocked-out
guest exactly as it accepts them from a standing one.

So every refusal this mod removes is a client-side one, and nothing about it needs to be agreed with
anybody.

## The gate, and the four places it is read

```csharp
// ControlManager.FrameUpdate
shouldProcessCharacterInputAllowKnockedOut = GetShouldProcessCharacterInputAllowKnockedOut();
shouldProcessCharacterInput = shouldProcessCharacterInputAllowKnockedOut
    && (!(controllingEntity is Hero hero) || !hero.isKnockedOut);
```

The pattern for what the mod wants is in the same codebase: `UI_InGame_WorldMap` returns
`shouldProcessCharacterInputAllowKnockedOut`, because the map is meant to work while down.
`Down.ProcessInput` is that same expression — the second line without its last clause — which is why
a cutscene, an open world map or a message on screen still closes character input here.

Four refusals stand between a knocked-out player and their own loadout, and they are not one kind of
thing:

| Where | What it is |
| --- | --- |
| `EditSkillManager.FrameUpdate` | early return while spectating; and the hold key gated on `shouldProcessCharacterInput` |
| `EditSkillManager.LogicUpdate` | `if (mode != 0 && isSpectating) EndEdit()` |
| `UI_InGame_SkillButtonsBottomBar.UpdateVis` | fades the bar to alpha 0 while spectating |
| `ControlManager.InitializeTriggers`, `EditSkillManager.Start` | two input triggers built with lambdas that read `shouldProcessCharacterInput` |

`UI_InGame_SkillButtons.CanBeFocused` is a fifth, and gamepad-only: with a controller the loadout is
reached by focusing the bottom bar, and `GlobalUIManager` will not focus something that says it
cannot be.

**Everything past that point is already open.** `UI_InGame_SkillButton_EditSkill.OnPointerClick`,
`UI_InGame_GemSlot_EditSkill.OnPointerDown` and `EditSkillManager.DropDraggingObject` check
`isEditSkillDisabled` and the edit mode, and none of them asks whether the hero is standing. Once
edit mode is open, clicking and dragging work.

## The spectate camera is half the problem

This is the part the planning notes missed. `CameraManager` starts spectating a couple of seconds
after a knockout, in co-op, whenever anybody is still alive:

```csharp
else if (DewPlayer.local.hero.isKnockedOut && !GameManager.instance.isGameConcluded
         && DewPlayer.gamePlayers.Count > 1)
```

and three separate places take `isSpectating` to mean "this player is a bystander now" — the two
`EditSkillManager` methods above and the bottom bar. Without those three, the mod would work in
single player and for about two seconds of a co-op death, which is a worse thing to ship than
nothing.

`Down.Spectating` is the stand-in, and it lies **only** to those three. The camera itself goes on
spectating and should: following a living teammate is the right place for it, and the bottom bar
draws your own loadout regardless of who the camera is on.

`UI_InGame_VisibilityOnSpectate` also hides things on spectate, from a per-prefab `hideOnSpectate`
flag, and is deliberately left alone — the existence of `UI_InGame_SkillButtonsBottomBar`, whose
whole job is the skill bar's visibility including the spectate case, is the evidence that the skill
bar is not also under one. **This is the thing to check first in a live co-op test.**

## Transpilers, and why not a scoped gate

`MapAutoRoute` opens a static flag in a prefix and closes it in a postfix, and patches the property
to consult it — a good pattern, and the wrong one here.

Reading `it_editSkillToggle.down` inside `EditSkillManager.FrameUpdate` calls
`InputManager.PrepareInputs()`, and that method re-evaluates `isValidCheck` for **every input
trigger in the game**:

```csharp
foreach (DewInputTrigger inputTrigger in _inputTriggers)
    inputTrigger._isValid = inputTrigger.isValidCheck == null || inputTrigger.isValidCheck();
```

It is guarded to once a frame, so whether it lands inside the gate depends on which manager updates
first — and if it does land there, every cast and movement trigger in the game is told the hero is
fine. A knocked-out player who can suddenly cast is a considerably worse bug than one who cannot
reach their essences.

Swapping the instruction has no such reach. `Swap.Calls` replaces `callvirt get_X` with `call
OurX(instance)` — same arity, same stack, `Call` rather than `Callvirt` because the stand-in is
static — inside the one method it is written into, and nothing that method calls. It also counts
what it replaced and logs a warning if the count is not what was expected, so a game patch that
moves one of these reads leaves a line in the log instead of a mystery.

## The two lambdas

The input triggers cannot be transpiled. Each is a lambda, which is its own compiler-named method —
`<InitializeTriggers>b__37_9` and the like — and matching that by name is a promise a mod cannot
keep across a patch.

They are wrapped after the fact instead, on the public field the game built them into:

```csharp
trigger.isValidCheck = () => original() || (allowKnockedOut && Down.Editing() && tail());
```

`tail` is the rest of the original condition, restated: `mode == ModeType.None` for the toggle key,
`mode != ModeType.None` for the interact key that leaves edit mode. Restating it is a small
duplication of the game's logic and is accepted for one reason — the exit trigger shares the
interact key with the world, and a trigger that reports itself valid can consume its key. A wrapper
that answered yes while no edit was open would quietly take *interact* away from doors, shrines and
merchants.

When the mod is not live the wrapper degrades to exactly `original()`, so one left behind on a
manager that outlives the mod is inert rather than wrong.

## Ground drops stay shut

The one thing that must not come through the door with the rest.

Rearranging what is equipped is safe from anywhere — the server moves an essence between two of your
own slots and no world position is involved. Dropping is not, and the reason is where a knocked-out
hero actually stands. Four of the five drop paths pass `hero.position + Random.insideUnitSphere`:

```
EditSkillManager 883, 922, 1000, 1009        and  UI_InGame_SkillButton_EditSkill 198
```

and until a teammate reaches your soul that position is wherever you fell, which is very often a
room the party has already left (`Shrine_HeroSoul.OnCreate` teleports the hero only when the shrine
spawns). An essence dropped there is gone. The fifth path drops at the cursor, which while
spectating is somebody else's room and no better.

`ControlManager.dropConstraint` is the game's own answer to this question rather than a lever being
repurposed: a `Func<Object, bool>` that every drop path consults, and that `UI_InGame_FloatingSkill`
reads to decide whether to draw the discard prompt at all. Setting it means the refusal is visible
while dragging rather than a click that does nothing.

Nothing in `Dew.Core` or `Dew.UI` assigns it, but `Dew.Contents` was only decompiled in part, so it
is treated as shared: the previous value is remembered, and put back only if the field is still ours
when the time comes.

It is maintained from a postfix on `ControlManager.FrameUpdate`, which is a per-frame poll and is
there because there is no client-side event for "the local hero was knocked out" — the knockout
arrives as a SyncVar. It costs three null checks.

## What still refuses, and should

Being down is otherwise unchanged: `Se_HeroKnockedOut.OnCreate` calls `DoStun`, `DoSilence`,
`DoInvulnerable`, `DoUntargetable`, `DoUncollidable` and `DoInvisible`, and none of them is touched.
Buying, selling and the essence shrines all need a live hero next to a prop, which a knocked-out one
is not.

## In co-op

Nobody else needs it and nobody else can tell. It is client-side to the last line: the server was
never asked.
