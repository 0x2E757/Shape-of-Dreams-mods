# DevTools

A third mod that exists to test the other two and is deliberately never published. Both of the
things they do are expensive to reach by playing: `MoreGemSlots` only shows its extended row at
five slots, which means a level-20 hero holding a +10 memory, and the result screen is on the far
side of a whole run.

It is largely a rebuild of tools the other two carried while they were being written and then had
removed — **What the mod was developed with, and no longer ships**, below, is the account of
those. This time they live in a mod of their own, so nothing has to be taken back out.

A hotkey toggles the panel, which can be dragged off whatever it is covering. Holding shift makes
the `-`/`+` buttons step by five.

The hotkey is a setting, and it is a setting because the obvious default is a trap: **`F12` is
Steam's screenshot key**, so with the game launched through Steam every toggle also takes a
screenshot. It is still the default, being the one key everyone already associates with a
developer panel, and the dropdown is one click away for anyone who would rather it were `F11`.

The field is a small enum of its own rather than `KeyCode`. The stock field builder does render
any enum as a dropdown — `DewGUI.OnInit` registers a pair whose condition is `type.IsEnum` — but
`KeyCode` would be some three hundred entries of mouse buttons and joystick axes to scroll past.
Members take the `KeyCode` values, so the two convert with a cast.

Both `ModConfig.LabelText` and `ModConfig.Description` are read by
`DewGUI.CreateWidgetsForObject`, and the second is where the screenshot warning lives — the place
someone changing the key is already looking. Both take compile-time constants and so can only ever
be English, which for a tool nobody else runs is the right trade; the published mods go the long
way round precisely because they are published.

| Control | What it does |
| --- | --- |
| Hero level | Writes `Entity.Status.level`, clamped to `Hero.maxLevel` |
| Item level | The level used by the two spawn buttons; no upper bound |
| Spawn random memory | A real loot-pool roll at that level |
| Spawn random essence | The same, at that quality |
| Knock out hero | `Entity.Kill()` |
| Gem tuning | opens the section below |

## What the mod was developed with, and no longer ships

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

## The gem tuning section

A `-`/`+` pair per number in `GemArrangement`, and a button that writes the set to the player log
as C# field initialisers to paste back. That is the same shape as the tuning panels the two
published mods were built with and then had removed, rebuilt for the reason given above for
keeping the pattern: guessing geometry from a decompiler and rebuilding between attempts
takes far longer than building the panel does.

`GemArrangement.Hud` and `GemArrangement.Summary` are **separate sets**, and the panel switches
between them. They started identical and did not stay that way, which answers whether one set could
have served both: the numbers are multiples of the authored spacing, but a summary row's widgets are
a different size *relative to* that spacing than the HUD's, so the same multipliers do not land the
same way.

Four of the twenty-one ended up differing, all saying the same thing — the summary row is tighter
for its spacing:

| Number | HUD | Summary |
| --- | --- | --- |
| `rowGap` | 0.80 | 0.65 |
| `topSpread` | 1.10 | 1.00 |
| `bottomSpread` | 0.75 | 0.63 |
| `bottomCurve` | 0.30 | 0.20 |

The other seventeen were left where the HUD put them, which is a good sign that sharing the
arrangement was right and it only needed a second set of dials rather than a second implementation.

The reset button asks the mod for the shipped values rather than working them out, since the two
sets no longer ship with the same numbers and only that side knows which is which.

Those numbers are fields rather than constants for this and only this. Nothing in either shipped
mod writes them.

Three things worth knowing about how it is wired:

- **Rows are built from whatever float fields the tuning object has**, found by reflection, so a
  number added to `GemArrangement` turns up in the panel without this file hearing about it.
- **DevTools reaches MoreGemSlots by reflection rather than by a project reference.** The loader
  enables the two independently, and a compile-time reference would make DevTools fail to load
  whenever MoreGemSlots is switched off — a poor trade for a tool whose other controls have nothing
  to do with essence slots. Missing, the section says so.
- **Which copy of MoreGemSlots is the live one is the whole difficulty**, and getting it wrong made
  the section look broken while throwing no error at all. `DewMod.Load` calls
  `Assembly.Load(File.ReadAllBytes(...))`, so **every hot reload puts another copy of the assembly
  into the process** and nothing ever takes the old ones out — .NET cannot unload an assembly
  without unloading its domain. Walking `AppDomain.GetAssemblies()` and taking a match lands on a
  dead copy as often as not, and editing its statics does exactly nothing: the live mod is reading
  its own. So the assembly is not guessed. The live mod has a `ModBehaviour` in the scene and the
  dead ones do not, because unloading destroys them — asking the scene which `MoreGemSlots` is
  running answers it outright.
- **Fields are addressed by name, not by `FieldInfo`.** A reload replaces the type, and a
  `FieldInfo` from the previous copy throws when handed an object of the new one. The bridge
  re-resolves every couple of seconds so a reload mid-tuning is picked up on its own, and logs
  which assembly it bound to.
- **The numbers are paged.** All twenty-one at once make a panel about a hundred pixels taller than
  a 1080p screen, and the overflow is silent — it simply runs off the bottom, and dragging it up to
  reach the buttons at the end pushes the first rows off the top. That is how `rowGap`, the one
  control for the distance between the two rows, managed to be present and unreachable at the same
  time. Two pages, split where the numbers already divide: the extended arrangement's rows, and the
  counts the game draws itself.
- **A change has to invalidate the rows.** A row is only re-laid when its slot count changes, so a
  number changing under it would otherwise not show until the player gained a slot.
  `GemArrangement.Changed` is what each caller subscribes to in order to forget what it applied.
  Only that is forgotten — the measured geometry stays, because re-measuring then would read rows
  the mod has already arranged.

**The scoreboard is hold-to-show.** `ControlManager.GetScoreboardAndMapInput` writes
`isScoreboardDisplayed` from the key's state on every tick, so a pin has to write it back
afterwards — hence the one thing in that panel living in `LateUpdate` rather than `Update`. Best
effort: holding Tab with the left hand and clicking with the right works regardless.

**Everything it does is server-only, and not by convention.** `EntityStatus.level` throws outright
off the server — *"Only server can change entity's level"* — and spawning and killing touch
network objects. So the panel checks `NetworkServer.active`, disables its buttons and says why,
rather than letting a click raise an exception. The same check covers there being no live hero.

**The templates come from the real loot pool, the levels do not.**
`LootManager.SelectSkillAndLevel` and `SelectGemAndQuality` both take a `Rarity?`, and passing
`null` is not a missing argument — they roll a rarity themselves when they are not given one,
which is the fully random draw this wants. The level they roll alongside it is then thrown away
in favour of the panel's, since asking for a level is the whole point:

```csharp
loot.SelectSkillAndLevel(null, out SkillTrigger template, out _);
Dew.CreateSkillTrigger(template, Dew.GetGoodRewardPosition(hero.agentPosition, 2f),
                       level, player, null);
```

Writing the level directly is what the game's own debug command did, and it skips everything a
real level-up hands out. For looking at how a UI behaves at level 20 that is exactly right; for
judging balance it is not.

**"Knock out hero" is not "end the run".** A hero is knocked out rather than removed, and
`GameManager.CheckGameOver` concludes the run once *every* hero in `ActorManager.allHeroes` is
knocked out. Solo, that reaches the result screen a moment later. In co-op it does nothing until
the others are down too, which is the game's rule rather than something to work around.

The panel is built the way the removed tuning panels taught, and the two rules from
**The tuning panel, which no longer ships** are load-bearing here:

- **Its own `Canvas`**, screen space, high `sortingOrder`, its own `GraphicRaycaster`,
  `DontDestroyOnLoad`. Not `DewGUI.canvasTransform`, which belongs to the mod config windows and
  is only lit while one is open.
- **No `DewGUI.widgetSlider`.** Numbers are a `-` and a `+`, which is both less code than
  persuading that prefab and better input for setting a level to exactly 12.

Buttons and labels *are* cloned from `DewGUI`, for a reason that has nothing to do with matching
the game's art: they arrive with a TMP font already on them. `DewGUI.SetText` once at build to
drop the prefab's `DewLocalizedText`, after which `.text` is ours to write each frame.

The settings window holds the hotkey and nothing else — the panel is the interface. Whether it is
open and what the item level is are `[HideInInspector]` state next to it, so both survive a
reload without becoming things to edit by hand.

`Input.GetKeyDown` is the legacy input API, which is safe here because the game itself uses it —
`ConsoleManager.FrameUpdate` is built on it.

