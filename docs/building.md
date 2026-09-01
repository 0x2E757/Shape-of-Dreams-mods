# Building, running and publishing

## Building

You need a .NET SDK and a copy of the game. Nothing from the game is included here — the projects
reference its assemblies from wherever it is installed — and the build output the mod loader
actually reads is not committed either, so a fresh clone has to be built once before the game can
load anything.

```powershell
.\tools\build.ps1     # Debug
.\tools\build.ps1 -Configuration Release
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
.\tools\launch.ps1
```

This starts the game with `-moddir <repo>\mods`, which replaces the game's own `Mods` directory.
That is what keeps these projects out of `Program Files`, where writing would need elevation.

`mods/` rather than the repository root because **the loader takes each immediate subdirectory of
`-moddir` as a mod and looks no deeper** — `DewMod.AddAllModsInDirectory` calls
`Directory.GetDirectories` with `SearchOption.TopDirectoryOnly`. A subdirectory without an
`about/metadata.json` is skipped, which is what lets `Shared/` sit alongside the mods.

`-steamnorestart` is passed as well: without it the game can relaunch itself through Steam,
and the relaunch would drop `-moddir`.

The same thing can be done permanently through Steam → Shape of Dreams → Properties →
Launch Options:

```
-moddir "<path to the repository>\mods"
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

`publish.ps1` names the mods it stages instead of discovering them, because the list of mods and
the list of things to publish are not the same list — `DevTools` is a testing tool and has no
business on the workshop. `build.ps1` and `launch.ps1` do discover, which is right for them: it is
meant to be built and run, just not shipped.

```powershell
.\tools\publish.ps1
.\tools\launch.ps1 -ModDir .\dist
```

Six files per mod: `about/` and one Release assembly, with `metadata.json` rewritten to point at
it. Sources and `assets/` are left out — the sprites are embedded in the dll, and the source is
here.

A **new** item is created private: `UpdateItem` calls `SetItemVisibility(handle, 2)` when
`isNew`, and 2 is `Private`. It stays invisible until you publish it from its Steam page, and
later updates leave the visibility alone.

**Change notes.** Each published mod keeps `workshop/changelog.txt` plus one
`changelog.<lang>.txt` per language it is described in, and the section for the version being
released is pasted into the item's page by hand — the uploader never sends any of it.
[changelog-format.md](changelog-format.md) has the template and the order a release goes in.

**Descriptions.** The one the game shows is `about/description.txt`, and there is only ever one of
it: `ModItem.description` is a single value and the mod format has no notion of language, so the
in-game manager shows English whatever the player's language is. Steam does allow a description
per language, but only through the item's page — the uploader sets just the one. The translations
for pasting there live in `<mod>/workshop/`, deliberately outside `about/` so they are not
uploaded as content nobody reads.

`DewMod.ConvertBBToRichText` is what the game does with the markup, and it is worth knowing
before writing any: `[url=address]text[/url]` keeps **only the text**, dropping the address, so a
link written that way reads as a bare word in the manager. `[url]address[/url]` keeps the address,
which Steam still renders as a link — that is the form to use. `[h1]`–`[h6]`, `[b]`, `[i]`, `[u]`,
`[strike]` and `[spoiler]` come through; `[img]` and `[hr]` are dropped along with their contents,
as is any tag it does not know; three or more blank lines collapse.

**Do not lose `about/publishedfileid.txt`.** A first upload asks Steam for an item id and writes
it there, inside the folder that was published; every later upload reads it back and updates that
item instead of creating another. Since the upload happens from `dist/`, that is where the file
lands — so `publish.ps1` copies it back into the working copy before wiping `dist/`, and git keeps
it from then on. Delete it and the next upload publishes a duplicate rather than an update.

## Art tooling

`tools/` also holds the two scripts that turn source artwork into what the mods ship. Neither runs
at build time — the artwork changes rarely, and the results are committed.

```powershell
# icon.png (128x128) and preview.png (636x358) for the mod manager, from square and 16:9 sources
.\tools\make-mod-art.ps1 -IconSource icon.png -PreviewSource wide.png -OutDir mods\AutoCast\about

# the six state sprites AutoCast embeds, from ring/arrows layers sharing one canvas
.\tools\make-autocast-icons.ps1 -LayerDir <folder> -OutDir mods\AutoCast\assets
```

`make-autocast-icons.ps1` expects six files named `autocast_{off,on,locked}_{ring,arrows}.png`. The
source artwork itself is not in the repo; the sprites and images it produces are.

Both share a resampler, and that is the point of them. See **Icons** in
[autocast.md](autocast.md) for why the obvious `System.Drawing` call is not good enough.

