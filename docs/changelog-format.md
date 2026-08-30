# Changelog format

Every published mod keeps `mods/<Mod>/workshop/changelog.txt`. **Follow this document exactly**;
it exists so that entries written months apart read as one voice and so that a release is never
half-done.

`DevTools` has none, and should not get one. It is not published, so there is nothing to write
change notes for.

## Where it lives, and why there

In `workshop/` beside the translated descriptions, deliberately **outside `about/`** — the game
uploads a mod's whole folder as content, and a changelog is not content the game reads. It is text
to paste into Steam's *Change Notes* box when updating the item.

One file per language, the same shape the descriptions use:

```
workshop/changelog.txt         English
workshop/changelog.ru.txt      the rest, one per language the mod is described in
workshop/changelog.zh-CN.txt
workshop/changelog.zh-TW.txt
```

Nothing here is uploaded. **The game's uploader sets one title and one description and never calls
`SetItemUpdateLanguage`** — `DewMod.UpdateItem` calls `SetItemTitle`, `SetItemDescription`,
`SetItemContent`, `SetItemPreview`, `SetItemVisibility` and `SubmitItemUpdate`, and that is all. So
every translation, of a description or of a changelog, reaches the workshop only by being pasted
into the item's page by hand.

Whether Steam offers a per-language slot for *change notes* specifically, as it does for
descriptions, is not something this repository can establish — check the item page. If it does not,
the translated changelog still earns its place: it is the text for a "what's new" line at the head
of the translated description, which is per-language for certain.

## The template

Newest version first. One section per released version, self-contained, because only that one
section gets pasted into the box.

```
[h2]1.2[/h2]
[list]
[*]One user-visible change per line, in the present tense, saying what the player will notice.
[*]Fixed: a line for each fix, naming what used to happen.
[/list]

[h2]1.1[/h2]
[list]
[*]...
[/list]

[h2]1.0[/h2]
[list]
[*]First release.
[/list]
```

BBCode is the same subset the descriptions use — `[h2]`, `[list]`, `[*]`, `[b]`, `[i]`,
`[url]address[/url]`. Do not use `[url=address]text[/url]`: it drops the address.

## What goes in it

**Only what a player can observe.** A changelog is not a commit log.

- A behaviour that changed, a thing that is now drawn, a setting that appeared or went away.
- A fix, written as *what used to go wrong* rather than as what the cause was — "toggles no longer
  reset when resuming a run", not "read `continueData` after the hero spawns".

**Left out:** refactoring, renamed files, documentation, anything internal, and anything only the
repository can see. Those are what `git log` and `docs/` are for. If a section would be empty by
this rule, the release does not need a version bump either.

Keep each line one sentence. No trailing full stops on list items unless the line is two clauses.

## Version numbers

`modVer` in `about/metadata.json`, and the `[h2]` heading, are the same string and are bumped
together.

| | |
| --- | --- |
| `x.Y` | anything a player would notice: new behaviour, a new setting, a fix |
| `X.0` | the mod does something materially different from what its description promised |

Numbers are per mod. The two published mods are released independently and their versions have no
relation to each other.

## Releasing

In this order, every time:

1. Add the section to `changelog.txt` — first, while the changes are still fresh — **and to every
   `changelog.<lang>.txt` beside it**. A language that is described but whose changelog stops at an
   older version is worse than one that was never translated: it reads as an abandoned mod.
2. Bump `modVer` in `mods/<Mod>/about/metadata.json` to match its heading.
3. Update `about/description.txt` **and all of `workshop/description.<lang>.txt`** if the change
   made any of them untrue. A changed behaviour usually has a sentence somewhere describing the old
   one; the descriptions are the mod's public contract and a stale one costs more than a missing
   changelog line.
4. `.\tools\publish.ps1`, then check the staged `dist/<Mod>/about/metadata.json` carries the new
   version and the Release assembly path.
5. `.\tools\launch.ps1 -ViaSteam -ModDir .\dist`, and publish from the in-game mod manager.
6. On the item's page, paste the new section into *Change Notes*, and paste each
   `changelog.<lang>.txt` / `description.<lang>.txt` into its language.

Step 3 is the one that gets skipped. Check it against the changelog section just written: every
line there is a claim about behaviour, and each one is a place a description may now be wrong.

## Keeping the languages honest

The set of languages is whatever `workshop/description.<lang>.txt` files exist — currently
`ru`, `zh-CN`, `zh-TW`. Adding a language means adding both files for it; there is no point in a
description nobody sees changes for.

```powershell
# every language should have a changelog, and every changelog should reach the current version
Get-ChildItem mods\*\workshop\changelog*.txt |
  ForEach-Object { "{0,-46} {1}" -f $_.FullName.Replace("$PWD\",''), (Select-String '^\[h2\]' $_ | Select-Object -First 1).Line }
```
