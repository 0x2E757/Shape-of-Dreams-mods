# Working with the game's UI

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

## Localization

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

## The config window

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

### Making an unused prefab work

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

