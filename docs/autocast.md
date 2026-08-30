# AutoCast

## On-screen controls

**AutoCast** puts one control above each of the Q/W/E/R cells, found through
`UI_InGame_SkillButtons.softInstance.skillButtons` and matched by each button's `skillType`.
`softInstance` rather than `instance`: the latter falls back to `FindObjectOfType`, which is not
something to run every frame outside a match. (`MoreGemSlots` has no HUD widgets of its own; it
patches the layout of the game's, which is a separate story further down.)

The control is assembled from bare components rather than cloned from `DewGUI.widgetToggleButton`
— that widget is a labelled rectangle sized for a settings row, and what the HUD wants is an icon
with three states. Cloning was what the first version did, and it cost a suppression flag too:
`UI_Toggle.isChecked` fires `onIsCheckedChanged` even when set from code, so every corrective
write from the sync loop also triggered a config save. With a plain `Button` and an explicit
`onClicked`, state flows one way — config to icon — and clicks are the only thing coming back.

Cloning is still the right answer for settings rows, where matching the surrounding UI is the
whole point:

```csharp
Instantiate(DewGUI.widgetToggleButton, parent)   // Resources.Load("DewGUI/Widget Toggle Button")
Instantiate(DewGUI.widgetButton, parent)
Instantiate(DewGUI.widgetTextLabel, parent)
DewGUI.SetText(go, "AUTO")   // drops the localiser component, then sets the TMP text
```

And one piece of the game's widgets is worth taking even when building from scratch: their sound
set. `UI_ButtonAudio` on `DewGUI.widgetToggleButton` carries five `AudioClip`s, and copying them
onto a `UI_ButtonAudio` of our own makes the control click like the rest of the interface. Add it
*after* the `Button` — its `Awake` runs during `AddComponent` and looks for a `Selectable` on the
same object.

Each control gets its own `CanvasGroup` with `ignoreParentGroups = true`. The skill cell fades and
stops taking raycasts in some HUD modes, and without this the control would go with it.

**The control is a sibling of the skill cell, not a child of it.** Unity sends pointer enter and
exit to the entire ancestor chain of whatever is under the cursor — `HandlePointerExitAndEnter`
walks from the hit object up to the common root — and nothing in the chain can stop it partway.
Parented to the cell, every hover of the toggle was also a hover of the skill and popped its
tooltip. So it lives beside the cell and follows it: one `_rect.position` write per frame from the
cell's top edge, plus `LayoutElement.ignoreLayout` in case that container ever grows a layout
group. Worth remembering for any widget pinned to something that is itself hoverable.

The HUD is rebuilt on zone changes, so the mod verifies each frame that its controls are still
parented to a live skill button and rebuilds when they are not, and destroys them in `OnDestroy`,
which is what makes live reload leave nothing behind.

### Icons

Each state is two layers, because the arrows turn and the frame does not. They arrive hand-cut and
already sharing one canvas, so `make-autocast-icons.ps1` only frames and scales them — identically,
so that stacking them at rest reproduces the icon and nothing shifts between states.

**Take the alpha that is there.** A long detour was spent on copies that had been flattened onto
black in transit, where alpha had to be reconstructed from brightness as `max(r, g, b)`. That is
right only for art that is pure glow. These are not: the silver icon is grey metal inside a dark
outline of about 50/255, so brightness-as-alpha left the outline four fifths transparent and the
metal itself at ninety percent, and the shape lost the line that framed it. On screen it read as
"the edges are chopped, as if there is no alpha channel" — which was the right diagnosis of the
wrong cause.

What settled it was counting, not looking. An alpha histogram of the output said **52 fully opaque
pixels out of 3,100 visible**; art that is meant to be solid is not 2% opaque anywhere. The same
count is the check that the fix worked (now 6,559 opaque against 4,168 partial, the partials being
the edge ramp, which is what an anti-aliased silhouette should look like).

Three things have to be right in the scaling:

- **Resample premultiplied, unpremultiply last.** A transparent pixel's colour means nothing, and
  averaging it into its neighbours means nothing either; in the other order every edge keeps a
  dark halo.
- **Use a filter whose support widens with the reduction.** GDI's `HighQualityBicubic` is a fixed
  four-tap kernel, so reducing by five it reads four source pixels out of every twenty-six and
  aliases what it skips. That aliasing is the other thing that looks like a chopped edge. The
  script uses a separable Mitchell (B = C = ⅓) whose support scales with the ratio.
- **Normalise the alpha against its plateau, not its peak.** These layers export a solid interior
  at 253. Scaling by `255/peak` does almost nothing, because the handful of pixels at 254 are
  strays; the number that matters is the mode of the top of the histogram. Guarded by a floor, so
  genuinely translucent art is not blown up to opaque.

The plate behind the icon is a plain white disc, drawn at 4× and scaled down because GDI
antialiasing in one pass leaves a visibly stepped rim. It carries no colour of its own — how grey,
how solid and how large are all decided at runtime.

They are `EmbeddedResource`s inside the dll rather than files beside it. `ModItem.path` does give
a mod its own directory on disk, but a self-contained assembly has no way to arrive without its
art. Decoding needs a reference to `UnityEngine.ImageConversionModule` for `Texture2D.LoadImage`,
and the texture wants a mip chain: the icon draws at roughly a quarter of its stored size at
1080p, and without mips that reduction crawls.

### Tooltips

`IShowTooltip` is the hover contract. Implement it and `UI_TooltipManager` finds the component
under the cursor and calls `ShowTooltip(manager)`, from where
`ShowTitleDescTooltip(settings, title, desc)` takes **raw strings** — so a mod can show text the
localisation table has never heard of, which is the gap `DewLocalization` otherwise leaves.

The interface inherits `IPointerEnterHandler` and `IPointerExitHandler` with default
implementations that do nothing but call `manager.UpdateTooltip()`. Declaring those handlers
yourself replaces the defaults, so that call has to be made by hand — including on `OnDisable`,
or a control hidden from under the cursor leaves its tooltip on screen.

`TooltipSettings.mode = Getter` takes a `Func<Vector2>` returning screen pixels, which on an
overlay canvas is just `transform.position`; `pivot = (0.5, 0)` puts the tooltip above the anchor
instead of over it.

### The tuning panel, which no longer ships

The constants at the top of `AutoCastToggle` — sizes, alphas, spin and settle speeds, the tooltip
gap — were not guessed. They were dialled in on screen with an overlay of sliders, one per number,
with a **LOG** button that wrote the current values out as C# field initialisers to paste back.
Once they were good the panel came out, along with the config checkbox and console command that
opened it. Worth rebuilding the same way if these ever need revisiting; three things it taught:

- **Do not borrow `DewGUI.canvasTransform`.** That canvas belongs to the mod config windows and is
  only lit while one is open, so the panel rendered in the menus and vanished on the way into a
  run. Give an overlay its own `Canvas` (screen space, high `sortingOrder`, its own
  `GraphicRaycaster`) marked `DontDestroyOnLoad`, and nothing the game does to its UI can touch
  it.
- **Do not borrow `DewGUI.widgetSlider` either.** Its graphics did not cover their own rect, so a
  click in the middle of a slider fell through to the panel behind it. Unity's own `Slider` over
  four rects you build yourself is less code than persuading the prefab, and a tool has no reason
  to match the game's art.
- **When a widget is visible but dead, raycast at it and print what comes back.** The panel logged
  its own rects, the `CanvasGroup`/`Canvas`/`GraphicRaycaster` chain above it, and an
  `EventSystem.RaycastAll` at the centre of the first slider. Both faults above fell out of one
  run of that; guessing had already cost several.

**AutoCast** follows the shape of the game's own autocast star effect
(`Se_Star_Bismuth_D_SkillHasteAndAutoCast`): one skill per tick in round-robin order, gated on
`AbilityTrigger.CanBeCast()` — which already accounts for cooldown, charges, minimum delay,
mana and lock state — with targets found via `DewPhysics.OverlapCircleAllEntities` filtered by
the skill's own `targetValidator`, and aimed with `GetPredictedCastInfoToTarget`. The host
casts through `EntityControl.Cast`; a remote client goes through `CmdCast`.

**MoreGemSlots** calls `HeroSkill.SetMaxGemCount`, the same public setter the Corrupted Chaos
shrine uses. The values are Mirror SyncVars, so only the server writes them and clients
receive them automatically.

## Hold-to-charge skills

Some memories charge while their button is held and fire on release. Autocast has no button to
release, so it can only misfire them, and they are skipped.

They are recognised by type rather than by a hard-coded list. The charge is described by a
`ChargingChannelData`, which sits on the thing the skill *spawns* (`Ai_*`, or occasionally a
`Se_*` status effect) rather than on the `St_*` trigger itself, so it is the spawned type that
has to be inspected. `AssetRef` carries `typeName` and `typeAssemblyQualifiedName` as plain
public metadata, so that type can be resolved without loading the asset. Results are cached per
skill type.

As of `r.1.3.1.3_s` this catches eleven: Pew, Shadow Volley, Stygian Rush, Cruel Sun,
Distorted Mind, Precision Shot, Static Discharge, Bone Crusher, Beam of Balance,
`St_Q_BigBorealChunk` and `St_R_BackOff`. Nothing needs updating if that set changes — the
check is structural.

When a hold skill is equipped its toggle reads `HOLD` and goes non-interactive, so the button
never claims to do something it will not.

## What switches a toggle back off

The toggles persist, which is what makes them worth having and also what makes two moments wrong
to persist across.

**A new run.** This took four attempts, and the first three are worth setting out, because each was
a confident reading of the same six lines of `GameManager.OnStartServer` and each was wrong:

```csharp
if (DewNetworkManager.startSettings.continueData == null)
{
    ...
    runId = Guid.NewGuid().ToString();
    ...
}
```

1. *"A resumed run keeps the id it had, so a changed id means a new game."* It does not keep it. The
   branch is skipped, and nothing restores the field.
2. *"Then use `continueData` itself as the answer."* Read at the moment the id changes, it was
   always null — so every start looked new.
3. *"So it must be set later; wait for the hero and read it then."* Also wrong, and this one is
   instructive: the toggles kept clearing with **no log line at all**, which no amount of reading
   the branch would have explained.

What that third failure exposed is the thing the whole boundary turns on. **A resumed run never
gets a run id.** The assignment is skipped and the field is simply left empty. Every version above
began with `if (string.IsNullOrEmpty(runId)) return;` — treating "no id" as "nothing to see" — and
so returned silently on exactly the case being fixed, leaving the previous run's skill bar on file
for `TrackEquipped` to read as four replaced memories. The reset was never coming from the run
check. It was coming from the memory check, downstream, because the run check had bowed out.

So the current version reads it the other way round: **an empty id is not missing information, it
is the mark of a resumed run**, and it is used as one.

The edge is a **hero this file has not seen before**, not a change of id — an id that a resumed run
never receives cannot serve as an edge. A new hero means a new bar either way, so the bar record is
dropped on every one of them; only clearing the toggles is conditional.

`lastRunId` survives as the guard for the other direction: a hero rebuilt *inside* a new-game run —
a mod reload, most often — matches the stored id and does not clear anything twice.

Two notes on method, since three of the four attempts were reasoning where measuring was called
for:

- **The log line carries both signals** — `runId='…' fromSave=…` — so the next reading of this
  boundary needs no investigation to say which branch ran and why.
- **The scenario harness cannot catch this class of bug.** It models the state machine, and the
  state machine was right every time; what was wrong was which game state means what, and when it
  is legible. That is only answerable against a running game.

A guest has no `continueData` of its own, so joining a host's run reads as new and starts clean.
For someone dropping into somebody else's game that is the right answer anyway.

**Both kinds of start forget the skill bar**, and getting that wrong made resuming clear the
toggles even after the run check itself was right. Quitting to the menu does not unload a mod, so
the record of what was in each slot survives the round trip — and a resumed run rebuilds the hero,
so every `SkillTrigger` arriving is a new object. Left on file, the old ones make the arriving
memories read as replacements, and the rule that a replacement switches its slot off does the rest.
The two features are individually correct and wrong together, which is the kind of seam worth
naming.

The last id has to live in the save file rather than in a field, since resuming a run in a later
session has to recognise it — hence `lastRunId` next to the four toggle states, `[HideInInspector]`
for the same reason they are.

One consequence of introducing it: the first run after updating the mod sees a stored id of `""`,
does not recognise the run, and clears the toggles once.

**A different memory in the slot** — but the rule is about the memory, not the slot. **Autocast
belongs to the memory.** A memory carried from one slot to another takes its setting along; a
memory that was not on the bar a moment ago arrives switched off, whatever the slot it lands in
was doing. So the four toggles are not four independent flags: each is answered by asking where
the memory now in that slot came from.

Comparison is by the `SkillTrigger` instance, which is exactly what a move preserves —
`CmdSwapSlotSkill` unequips both slots and re-equips them crossed over, and `UnequipSkill` drops
the trigger into the world rather than destroying it. Levelling the memory already in a slot is
therefore not a change at all, and the whole pass is skipped.

The details that took a test to get right:

- **Every slot is resolved against the previous bar before any of them is written back.** Doing
  them one at a time lets the first write become the second one's answer, and two memories trading
  places both end up with whichever state was read first. All four are read into a scratch array,
  then all four are written.
- **An empty slot is passed over, not treated as a change.** A slot is briefly empty mid-move, and
  on a zone change the whole hero is gone for a few frames; the control is hidden while a slot is
  empty anyway. It also goes on remembering what was in it, which is what lets a memory dropped on
  the ground and picked up again arrive with its setting intact.
- **Unless that memory has turned up in another slot.** Then it lives there and took its setting
  with it, so the slot it left is cleared of both — otherwise the next memory dropped into that
  slot inherits a setting it never earned. This is the case the tests caught.
- **The record of a vacated slot is dropped, but the slot still counts as seen.** Clearing the
  seen flag would make the next memory look like the first this slot has ever held, and a first
  sighting is the one case allowed to keep a setting it did not earn — it is what happens on the
  frame the mod loads mid-run and the frame a continued run comes up on, where the stored setting
  already belongs to the memory that is there. Tracked with a flag rather than by testing the
  remembered trigger against null, because a *destroyed* trigger also reads as null.

Both checks run before the gates on combat, channelling and cutscenes rather than after: a run is
entered dead, and memories are moved outside combat.

The state machine is small and every one of the rules above is a case that can be got wrong
silently, so it was transcribed into a plain console program and run against the scenarios —
a move, a three-way rotation, a move plus a new memory in one frame, a drop and a pickup
elsewhere, a stale record, a mid-run load, a new run, a resumed run. Two failed the first time and
the resume case was added after it was reported from a real session; each was checked to fail
without its fix before being kept, since a scenario that passes either way is worth nothing. That
harness is not in the repository: it needs no Unity and no game, and rewriting it from this section
is a few minutes' work if these rules ever change.

