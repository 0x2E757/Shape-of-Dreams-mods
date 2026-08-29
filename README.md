# Shape of Dreams mods

Two mods for Shape of Dreams `r.1.3.1.3_s`, built against the game's own assemblies.

| Mod | What it does |
|---|---|
| `AutoCast` | A toggle above each skill cell; while it is lit, that memory is cast as soon as it leaves cooldown. |
| `MoreGemSlots` | Essence slots are earned from hero level and memory level, up to seven, and the UI is extended to draw them. |

## Building

You need a .NET SDK and a copy of the game. Nothing from the game is included here — the projects
reference its assemblies from wherever it is installed — and the build output the mod loader
actually reads is not committed either, so a fresh clone has to be built once before the game can
load anything.

```powershell
.\build.ps1              # Debug
.\build.ps1 -Configuration Release
```

`build.ps1` takes the first `dotnet` that reports having an SDK: the one on `PATH`, or a
user-local one at `%USERPROFILE%\.dotnet`. That fallback is there because this project was set up
with the SDK unpacked into the user profile rather than installed, so nothing on the machine
outside that one folder had to change. Note that finding `dotnet.exe` is not the same as finding
an SDK — a machine with only the runtime has the shared host on `PATH`, and it answers every
build with "No .NET SDKs were found".

`Directory.Build.props` holds the path to the game install, because the template assumes projects
sit inside `<game>\Mods\<name>\` and resolves the game as `..\..`, and these live outside the
Steam folder. It defaults to the usual Steam location and is conditional, so an install elsewhere
needs no edit to a tracked file:

```powershell
dotnet build -p:GameDir="D:\Games\Shape of Dreams"
```

The project template came from the game itself and is registered with
`dotnet new install "<game>\Mods\ModTemplate"` (short name `sodmod`).

Four references from the stock template were removed — `Unity.Mirror.CodeGen`,
`Unity.RenderPipelines.Universal.Runtime.Tests`, `Unity.ShaderGraph.Utilities` and
`Mirror.CompilerSymbols` are editor/codegen assemblies that are not shipped in the retail
build, and referencing them only produced MSB3245 warnings.

`UnityEngine.UIModule` was added, which the stock template omits. It holds `CanvasGroup` and
`Canvas`; `RectTransform` comes from `UnityEngine.CoreModule` and `Button`/`Image` from
`UnityEngine.UI`, which is why the omission only shows up once a mod builds its own UI.

## Running

```powershell
.\launch.ps1
```

This starts the game with `-moddir <this folder>`, which replaces the game's own `Mods`
directory. That is what keeps these projects out of `Program Files`, where writing would
need elevation.

`-steamnorestart` is passed as well: without it the game can relaunch itself through Steam,
and the relaunch would drop `-moddir`.

The same thing can be done permanently through Steam → Shape of Dreams → Properties →
Launch Options:

```
-moddir "<path to this folder>"
```

Then enable both mods in the in-game mod manager.

## Publishing to the workshop

The game does the uploading itself — `DewMod.CreateItem` / `UpdateItem` sit behind the mod
manager, and they hand Steam `SteamUGC.SetItemTitle` from `metadata.name`, `SetItemDescription`
from `description.txt`, `SetItemPreview` from `preview.png`, and `SetItemContent` **from the
mod's whole folder**.

That last one is why `publish.ps1` exists. Uploading the working copy would send `obj/` with it,
and the NuGet files in there carry absolute paths of the form `C:\Users\<name>\.nuget\packages\` —
an account name in a public item. So the script stages a copy in `dist/` holding only what the
loader reads at runtime:

```powershell
.\publish.ps1
.\launch.ps1 -ModDir .\dist
```

Five files per mod: `about/` and one Release assembly, with `metadata.json` rewritten to point at
it. Sources and `assets/` are left out — the sprites are embedded in the dll, and the source is
here.

A **new** item is created private: `UpdateItem` calls `SetItemVisibility(handle, 2)` when
`isNew`, and 2 is `Private`. It stays invisible until you publish it from its Steam page, and
later updates leave the visibility alone.

**Do not lose `about/publishedfileid.txt`.** A first upload asks Steam for an item id and writes
it there, inside the folder that was published; every later upload reads it back and updates that
item instead of creating another. Since the upload happens from `dist/`, that is where the file
lands — so `publish.ps1` copies it back into the working copy before wiping `dist/`, and git keeps
it from then on. Delete it and the next upload publishes a duplicate rather than an update.

## Art tooling

`tools/` holds the two scripts that turn source artwork into what the mods ship. Neither runs at
build time — the artwork changes rarely, and the results are committed.

```powershell
# icon.png (128x128) and preview.png (636x358) for the mod manager, from square and 16:9 sources
.\tools\Make-ModArt.ps1 -IconSource icon.png -PreviewSource wide.png -OutDir AutoCast\about

# the six state sprites AutoCast embeds, from ring/arrows layers sharing one canvas
.\tools\Make-AutoCastIcons.ps1 -LayerDir <folder> -OutDir AutoCast\assets
```

`Make-AutoCastIcons.ps1` expects six files named `autocast_{off,on,locked}_{ring,arrows}.png`. The
source artwork itself is not in the repo; the sprites and images it produces are.

Both share a resampler, and that is the point of them. See **Icons** below for why the obvious
`System.Drawing` call is not good enough.

## Shared code

`Shared/` is compiled into both mods by `<Compile Include="..\Shared\*.cs" />`, **not** shipped as
a library. Two reasons, both about how mods are loaded here:

- `metadata.json` lists a mod's assemblies, so a shared dll would have to travel with each mod —
  two copies of one assembly name loaded from different folders, which is not a question worth
  asking the loader.
- A library published as its own mod would be a hard dependency, and nothing in the workshop
  guarantees a user has it enabled. Source linking gives the same reuse and leaves each mod
  self-contained.

What lives there is what both mods genuinely do identically and what was expensive to work out:

| file | what it is |
| --- | --- |
| `ConfigFieldWidgets.cs` | on/off buttons for `bool`, sliders for `int`/`float` with a `[Range]` |
| `SettingsRows.cs` | pinning the label and input columns, and rewriting labels for localization |
| `LanguageTable.cs` | picking a string by `DewSave.profileMain.language` with an English fallback |

The slider is the reason this folder exists. Getting that prefab to behave took four rounds in
`MoreGemSlots`, and then it was worked out again from scratch in `AutoCast` — the second time
nearly as slowly as the first.

What deliberately stays per-mod: the string tables themselves, and anything about the game rather
than about widgets. The risk with a folder like this is that it becomes a drawer for anything two
files happen to share, and then a change for one mod breaks the other. The bar is *identical, and
costly to rediscover* — not merely similar.

## Notes on the implementation

Both mods use only public engine API; neither needs a Harmony patch.

### On-screen controls

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

#### Icons

Each state is two layers, because the arrows turn and the frame does not. They arrive hand-cut and
already sharing one canvas, so `Make-AutoCastIcons.ps1` only frames and scales them — identically,
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

#### Tooltips

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

#### The tuning panel, which no longer ships

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

### The slot formula

```
base                                        3
hero level >= heroLevelForFirstSlot        +1   (10)
hero level >= heroLevelForSecondSlot       +1   (20)
memory +N  >= memoryUpgradesForFirstSlot   +1   (5)
memory +N  >= memoryUpgradesForSecondSlot  +1   (10)
                                       max  7
```

Seven is both the formula's maximum and exactly what the extended layout can draw — the two
numbers were made to line up. Hero level is `Entity.level`.

The memory thresholds count **upgrades, not levels**. A fresh memory is `SkillTrigger.level == 1`
and displays no `+`, so the number the player reads off it is `level - 1`. Comparing the threshold
against the level directly is off by one, and off by one in the direction that is hard to notice:
everything still works, it just triggers a level early.

Q/W/E/R are governed by the formula; Identity and Movement have no essence slots in the base
game and are left that way.

The four thresholds are the mod's entire config surface. Everything else — the base count, the
ceiling, the whole of the layout geometry — is a constant, because it is either derived from
those numbers or was settled by measurement and has no business being a knob.

### Losing slots

The count falls as well as rises: swap a levelled memory for a fresh one and its earned slots go
with it. `RehomeGems` runs before the count actually changes, moving each stranded essence to the
nearest free slot on the same memory, counting inwards from the edge so it travels as little as
possible. With nothing free it is left on the ground.

The move is `UnequipGem` then `EquipGem`, which is how the game's own slot swap does it — and the
order matters: `UnequipGem` puts the essence into the world first, so if the re-equip fails the
essence is on the floor rather than gone. That is what the `try`/`catch` around it is protecting.

### Localization

The game ships data for thirteen languages, one folder each under `RawData`. `DewLocalization`
holds the table but exposes **lookups only** — `GetUIValue`, `GetSkillName` and so on — with no way
for a mod to register entries of its own. What it does expose is enough:

- `DewSave.profileMain.language` — the selected language code.
- `DewLocalization.data` — the active language's tables, whose dictionaries are public and
  mutable, so entries *can* be injected. Only for the active language, though, so they would have
  to be re-injected whenever it changes.
- `ILangaugeChangedCallback` — the interface the game dispatches on a language change (the typo is
  the game's).

Both mods take the simpler route: their own string table, picked by `DewSave.profileMain.language`,
falling back to English for anything unrecognised — including a language a later patch adds.

Config labels are normally `Dew.NicifyVariableName(field.Name)`, which cannot be localized through
the field name. `MoreGemSlots.BuildWidgets` therefore rewrites them after the base builds the rows,
matching rows by their nicified text rather than by position so that reordering fields or
introducing a header cannot mispair them.

**What cannot be localized:** the mod's name and description in the mod manager. `ModMetadata` has
plain `name` and `author` strings and `ModItem.description` is a single value — the mod format has
no concept of language.

### The config window

Two things about `DewGUI.CreateWidgetsForObject` are worth knowing.

**It sizes both halves of a row to their contents.** Each setting is a horizontal row of label
then widget, and neither has a fixed width, so rows stagger twice over: field names of differing
length start their widgets at differing x, and a one-digit value gets a visibly narrower box than
a two-digit one. Both mods override `ModConfig.BuildWidgets`, let the base build the rows, then
pin the label and input widths. (Its `onChanged`/`requestUpdate` parameters are `out`, not `ref`,
which the metadata does not distinguish — both show as `SafeAction&`.)

**`DewGUI.fieldBuilders` is a public list of `(FieldBuilderCondition, FieldBuilder)` pairs**, and
that is the sanctioned way to render a setting differently. A pair inserted at the front claims a
field before the stock builders see it, while the game keeps ownership of reading and writing the
field itself — the builder only returns `getValue`, `setValue`, `root`, and invokes `onChanged`.

`SliderFieldBuilder` uses it to give `int` fields carrying a `[Range]` attribute a slider. The game
ships a slider prefab (`DewGUI.widgetSlider`) but never uses it; every number, bounded or not,
otherwise gets a text box.

The list is global, so the condition is narrowed to this mod's own config type and the entry is
removed on unload. `onChanged` is null until the window wires itself in, so it is read at call
time rather than captured — the same shape the stock `GenericInputFieldBuilder` uses.

#### Making an unused prefab work

That prefab had never been through a layout, and four separate things had to be put right. Each
looked like the whole problem while it lasted:

- **Range order.** It is a 0..1 slider, so assigning `minValue` above the current `maxValue`
  clamps on the way past and leaves no range at all. Raise the ceiling first.
- **Height.** The row controls child height, and a `Slider` offers the layout system no size of
  its own, so an unset `preferredHeight` collapses the rect to zero. It still *looks* right — bar,
  fill and handle are anchored and keep drawing outside the collapsed parent — but a rect with no
  area cannot be hit by the pointer.
- **`flexibleWidth`.** Left at 1 the slider swallows the row's whole leftover, and
  `preferredWidth` has no visible effect at all: the widget looks identical whatever it is set to.
- **Track insets.** The slide area is stretched with fixed insets, about 50 left and 142 right,
  the latter reserved for the prefab's own value text. Usable track is the width minus those, and
  the handle is 32 wide — so a 240-wide slider has 16 pixels of travel and is indistinguishable
  from a dead one. Narrowing the widget means narrowing the insets and the value text with it.

The lesson that actually shortened this: **measure the rendered rect, a frame after the layout has
run.** Measuring immediately after building returns the preferred size, which is how a width can be
set correctly, measure correctly, and still look unchanged on screen. Two of the four causes above
were only found once a real on-screen number was in hand.

### What the mod was developed with, and no longer ships

Getting here took a set of in-game tools that have since been removed, since none of them belong
in a finished mod. They are worth knowing about if this ever needs revisiting:

- **Tuning panels** built from `DewGUI` widget prefabs, with a `-`/`+` pair per layout number
  applied live, and a button that wrote the whole set to the player log. Every geometry constant
  in `GemLayoutPatch` came out of those, read back from the saved settings file afterwards.
- **A `- N +` row under each skill cell** for driving slot counts by hand before the formula
  existed, backed by per-slot manual overrides in the config.
- **Debug commands** for spawning from the real loot pool (`LootManager.SelectGemAndQuality` /
  `SelectSkillAndLevel`, then `Dew.CreateGem` / `Dew.CreateSkillTrigger` at a position from
  `Dew.GetGoodRewardPosition`) and for setting `hero.Status.level` directly — which has a public
  setter, though writing it skips the usual level-up rewards.

The pattern worth keeping: on-screen controls wired to live values, with a button that dumps
them to the log in a form that can be pasted back into the source. Guessing geometry from a
decompiler and rebuilding between attempts would have taken far longer than building the panel
did.

### Hold-to-charge skills

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

### Getting past the essence slot ceiling

`UI_InGame_SkillButton_GemGroup` does not lay its essence slots out. It picks one of several
layouts drawn by hand in the prefab, indexed by `maxGemCount - 1`:

```csharp
for (int i = 0; i < groups.Length; i++)
    groups[i].SetActive(i == max - 1);
```

The game ships layouts for 1 to 4 (confirmed in game). At 5 no index matches, every layout is
switched off, and the slots disappear from the cell while still working underneath.

`GemLayoutPatch` is a Harmony prefix that replaces that method outright, for every count rather
than only above the ceiling. Deferring to the original for small counts looked tidier and was
wrong: the original rebuilds its slot list *only in the frame it activates a layout*, and above
the ceiling the patch has already left that layout active — so dropping from five slots back to
four kept a five-entry list and drew five. Owning every count keeps the two from disagreeing.

**Seven is a hard ceiling.** The extended arrangement is a fixed four-over-three grid, so seven
is everything it can draw:

```
1 2 3 4
 5 6 7
```

`GemLayoutPatch.MaxSupportedSlots` is the single source of that number. `maxSlots` in the config
is clamped to it, and the periodic apply clamps the live `maxGemCount` down to it as well — so a
Corrupted Chaos shrine granting an extra slot on top of seven cannot push the count to eight and
make the slots vanish again.

Five and six take the **first positions of that same seven-slot grid** rather than being laid out
on their own. Laying each count out independently meant a slot moved every time the next one was
added, which read badly at six. `centerPartialBottomRow` switches back to per-count centring.

Every row is shaped by four numbers: `spread` (spacing relative to the authored step), `drop`
(distance from the skill cell), `curve` (1 follows the authored arc, 0 is a straight row) and
`rotate` (how much the widgets tilt along it). The bottom row adds `offset` for a sideways
shift, and `extraRowSpacing` sets the gap between the two rows — measured from the top row, so
moving that carries the bottom one with it.

Which set applies depends on the count:

| Count | Shaped by |
|---|---|
| 1, 2, 3 | `SmallShapes[1..3]` — a set each |
| 4 | the top-row constants |
| 5, 6, 7 | the top-row constants, plus the bottom-row ones for the extras |

Three separate sets for the small counts because the authored layouts have different geometry:
the same multiplier lands differently on a row of two than on a row of three, and one shared set
could not satisfy both. Four shares the top-row numbers deliberately — it doubles as the upper
half of the extended layout, so splitting them would let the two drift apart.

The extended rows end up nearly straight (`curve` 0.30, no tilt) because the authored arc reads
well at four slots but fans out at seven; the small layouts keep most of it (`curve` 0.75, full
tilt), having nothing to fan out into.

The saved settings live at
`%USERPROFILE%\AppData\LocalLow\Lizard Smoothie\Shape of Dreams\QuickSave\Mods\<mod id>\`.

The authored row's original positions and rotations are captured before anything is touched, so
they can be put back when the count drops. The authored rows are arcs, so the second row follows
a concentric arc at a smaller or larger radius depending on which way the original bulges; a
collinear layout falls back to a straight row. Row gap is `extraRowSpacing`, as a multiple of the
spacing between neighbouring slots.

Cloning the widget is what fixes both screens at once: the edit-skill overlay
(`UI_InGame_GemSlot_EditSkill`) is a companion component on the same object, not a second
layout, so it comes along with the clone.

**The gem group is a sibling of the skill button, not a child of it.** Both sides of that
pairing are resolved through the shared parent:

```csharp
// UI_InGame_SkillButton_GemGroup.Awake
_button = transform.parent.GetComponentInChildren<UI_InGame_SkillButton>();
// UI_InGame_SkillButton.Init
_gemGroup = transform.parent.GetComponentInChildren<UI_InGame_SkillButton_GemGroup>();
```

`GetComponentInParent<UI_InGame_SkillButton>()` from the gem group looks right and returns
`null`, which makes the patch quietly fall through to the original with no error anywhere. The
patch uses the same parent-then-children lookup the game does.

`slotIndex` is the whole identity of a slot widget — `thisSlotLocation` is
`(button.skillType, slotIndex)` and the group sorts by it — so setting it on each clone is what
makes the new slots address real gems. Clones are named `MoreGemSlots_Extra_<i>` and found again
by `GetComponentsInChildren(true)` on the next load, which is what stops a live reload from
stacking duplicates.

The array is only rebuilt when the count is actually wrong, since `LogicUpdate` runs every tick.

Turning the mod off while a run has more than four slots leaves those gems in the save but hides
the slots until it is turned back on, because the stock method has no layout for them.

### A note on `UnpatchAll`

The stock template calls `harmony.UnpatchAll()` in `OnDestroy`. With no argument that removes
every Harmony patch in the process, including other mods'. `MoreGemSlots` passes
`harmony.UnpatchAll(harmony.Id)` so it only removes its own.

## Multiplayer

Both work in co-op, and are built along the boundary the game already draws.

Nothing gates a modded session. `DewMod.isGameplayAltered` feeds exactly one thing — a lobby
attribute named `isModded` — so a lobby is *labelled* as modded, not closed. The authors' code of
conduct asks that gameplay-altering mods not be taken into games with people who have not agreed
to them, which is a matter of manners rather than of the code.

**MoreGemSlots is server-authoritative.** Its `Update` returns immediately unless
`NetworkServer.active`, so only the host computes slot counts — and it computes them for every
player in `DewPlayer.gamePlayers`, not just its own hero. `maxGemCount` is a Mirror SyncVar, so
clients receive the result rather than deciding it, and the rehoming calls (`EquipGem`,
`UnequipGem`) run only on the host, which is why their `Cmd*` twins are not needed.

One consequence worth knowing: **the host's thresholds decide for everyone.** A guest's own
settings are read by code that never runs on a guest, so changing them appears to do nothing.

**AutoCast is per-player.** It acts only on `DewPlayer.local`, so each player automates their own
hero and nobody else's, and it casts the way the game does: straight through when
`hero.isServer`, and through `EntityControl.CmdCast` otherwise. No client-side authority is
assumed anywhere.

**Who needs to install what.** MoreGemSlots is required on both sides: the host decides the slot
counts, but *drawing* them is a local UI patch, and without it a guest's interface cannot render
more than three — the same failure as the "slots vanish at 5+" bug in **Getting past the essence
slot ceiling** below. AutoCast is not required on both sides; it is input automation and works for
whoever has it.

All of the above is read off the code and the game's API. It has not been verified in a live
two-client session — the one piece that only exercises there is the guest's `CmdCast` path.
