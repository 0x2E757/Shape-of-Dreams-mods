# Shape of Dreams mods

Mods for Shape of Dreams `r.1.3.1.3_s`, built against the game's own assemblies.

| Mod | Workshop | What it does |
| --- | --- | --- |
| `AutoCast` | published | A toggle above each skill cell; while it is lit, that memory is cast as soon as it leaves cooldown. |
| `MoreGemSlots` | published | Essence slots are earned from hero level and memory level, up to seven, and three screens are extended to draw them. |
| `DevTools` | no | An overlay for testing the other two: hero level, spawning memories and essences, ending a run, and live tuning of the slot arrangement. |

## Quick start

```powershell
.\tools\build.ps1     # Debug; -Configuration Release for the other
.\tools\launch.ps1    # runs the game with -moddir pointed at mods\
.\tools\publish.ps1   # stages the two published mods into dist\
```

Then enable the mods in the in-game mod manager. The player log is at
`%USERPROFILE%\AppData\LocalLow\Lizard Smoothie\Shape of Dreams\Player.log`.

Nothing from the game is committed here, and neither is build output, so a fresh clone has to be
built once before the game can load anything. The game install is resolved from
`Directory.Build.props`, which defaults to the usual Steam location and is conditional — an
install elsewhere needs no edit to a tracked file:

```powershell
dotnet build -p:GameDir="D:\Games\Shape of Dreams"
```

`tools/` also holds the two art scripts that turn source artwork into what the mods ship. Neither
runs at build time; the results are committed.

## Layout

```
mods/                what -moddir points at
  AutoCast/          published
  MoreGemSlots/      published
  DevTools/          not published
  Shared/            compiled into each mod, not shipped as a library
tools/               build, launch, publish, and the two art scripts
docs/                the notes listed below
images/              screenshots, embedded in the docs
dist/                publish.ps1 output; not committed
```

`mods/` is named for what it is rather than for what is in it: **the loader takes each immediate
subdirectory of `-moddir` as a mod and looks no deeper** (`DewMod.AddAllModsInDirectory`, with
`SearchOption.TopDirectoryOnly`), so that folder *is* the mod directory `launch.ps1` points the
game at. `Shared/` sits inside it and is skipped, having no `about/metadata.json` — the same way
`dist/` and `tools/` were skipped when the root was the mod directory.

## Documentation

These are working notes rather than a manual: what the game's API actually does, which obvious
approach was wrong, and what it cost to find out. They are worth reading before changing the
matching code.

| Document | What is in it |
| --- | --- |
| [building.md](docs/building.md) | Building, running, publishing to the workshop, art tooling |
| [autocast.md](docs/autocast.md) | HUD controls, icons, tooltips, hold-to-charge skills, what resets a toggle |
| [moregemslots.md](docs/moregemslots.md) | The slot formula, losing slots, and getting past the four-slot drawing ceiling on three screens |
| [devtools.md](docs/devtools.md) | The testing overlay, and the live tuning panel for the slot arrangement |
| [game-ui.md](docs/game-ui.md) | Reusable ground: shared widgets, localization, the mod config window |
| [multiplayer.md](docs/multiplayer.md) | How both published mods behave in co-op, and who needs to install what |
| [changelog-format.md](docs/changelog-format.md) | **Follow exactly when releasing.** The changelog template, and the order to do a release in |

A few things that save time and are easy to miss:

- **Both published mods use Harmony**, `MoreGemSlots` heavily. It passes
  `harmony.UnpatchAll(harmony.Id)`, because the stock template's bare `UnpatchAll()` removes every
  patch in the process, including other mods'.
- **`Shared/` is source-linked into each mod**, not shipped as a library: a shared assembly would
  have to travel with both, and a library published as its own mod would be a dependency the
  workshop cannot guarantee is enabled.
- **`publish.ps1` names what it stages** rather than discovering it, so `DevTools` is never
  uploaded, and it stages into `dist/` rather than publishing the working copy — the game uploads a
  mod's *whole folder*, and `obj/` carries absolute paths with the Windows account name in them.
- **Do not lose `about/publishedfileid.txt`.** It is what makes an upload update the existing
  workshop item instead of creating a duplicate.
- **A release is a sequence, not a build.** Each published mod keeps
  `mods/<Mod>/workshop/changelog.txt`, bumped in step with `modVer` and pasted into Steam's change
  notes by hand. [docs/changelog-format.md](docs/changelog-format.md) is the template and the
  order; follow it rather than improvising, particularly the step that re-checks the descriptions
  against what just changed.
