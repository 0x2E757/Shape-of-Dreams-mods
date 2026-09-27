# TransparentEffects

Two sliders — your own effects, and everybody else's — and behind them the game's own resource
variant machinery, used the way the game uses it rather than worked around.

Line numbers are from `Dew.Core` decompiled with `ilspycmd` against the install
`Directory.Build.props` points at, and will drift. The type and method names are the durable part.

## Half of it already ships

This is the first thing to know, and the reason the second slider is worded carefully.

`DewSave.profileMain.gameplay.reduceOtherPlayerEffectsStrength` is a stock setting — declared in
`DewGameplaySettings_User` as `ReduceOtherPlayerEffectsStrength { Low, Medium, High, VeryHigh,
Hide }` — and it feeds `DewResources.TonedDownProcessor`, which multiplies alpha by 1, 0.7, 0.45,
0.25 or 0 along with two further factors, and reaches ordinary renderers and materials rather than
only particle systems.

What picks it is a delegate added in `Entity.Awake`, and it is narrow in exactly the way that
leaves room for a mod. It adds `DewResources.vOtherPlayersTonedDown` only when

- the spawned type `IsSubclassOf(typeof(AbilityInstance))`, and
- the owner is a human player, and
- the local player — or the spectated one, if the camera is following someone else — is **not** that
  owner.

**Your own effects are never toned down by anything the game ships.** That is the half with no stock
answer, and it is the row this mod exists for. The other row is a finer instrument for a control
that already exists — a continuous number instead of five steps — and the two multiply rather than
replace each other, because they are separate variant ids that both land on the same prefab.

## What a variant is

`DewResources` keeps, for each asset and each `VariantDef`, one processed copy of the prefab:

```csharp
public static int GetNextVariantId();                                     // both public
public static void RegisterVariantProcessor(int id, ResourceVariantProcessor p);

public delegate Action ResourceVariantProcessor(UnityEngine.Object obj);
```

The processor is handed a freshly instantiated copy, mutates it, and returns a cleanup action to be
run when that copy is thrown away. Everything spawned with that `VariantDef` is then instantiated
from the copy.

So the alpha is paid for **once per prefab per session**, not once per cast, and nothing walks a
live effect's renderers while it is playing. That is the whole reason to use this machinery instead
of tinting instances: it is the cheap way, and it is the way the game already does it.

The price is that the number is baked in. A variant built at 0.5 stays at 0.5, which is why
`OnConfigChanged` has to throw the cache away.

## `VariantDef` holds six ids and honours four

`VariantDef.Add` accepts ids until six are filled and throws on the seventh. But the code that
actually builds a variant, in `DewResources.GetVariant`, is:

```csharp
item += Process(varDef.id0, gameObject);
item += Process(varDef.id1, gameObject);
item += Process(varDef.id2, gameObject);
item += Process(varDef.id3, gameObject);
```

**`id4` and `id5` are never processed.** An id in the fifth slot is carried around, changes the
cache key so a second identical copy of the prefab is made and kept, and does nothing at all.

For an ability instance the first three slots are usually spoken for — `vQualityAdjusted`, then
`vOtherPlayersTonedDown` if it belongs to someone else, then a skin variant if the owner has one —
which leaves exactly one. This mod adds at most one id per effect, and `VariantChoice` counts the
filled slots and stands down at four rather than adding a fifth that would only cost memory.

## Where the choice is made

`DewResources.GetSuggestedVarDef(Actor parentActor, Type childType)` is the single funnel, and both
halves of the game go through it:

- `Actor.CreateAbilityInstance` → `GetSuggestedResourceLoadSettings` → `GetSuggestedVarDef`, when the
  effect is created;
- `SpawnManager.SpawnFromDewDatabaseHandler` → `GetSuggestedVarDef`, on each client when Mirror
  tells that client the effect exists.

That second one is what makes a client-side mod possible at all: the variant is not chosen by the
server and shipped, it is chosen again on every machine, from that machine's own point of view.

**A postfix there rather than a delegate per entity.** The game adds its own condition to
`Entity.spawnedChildVarDefProcessor`, an instance field on every `Entity`, in `Entity.Awake`. A mod
could do the same, and then it would have to find every entity alive at unload to take the delegate
back out again. One method, patched and unpatched, has no such problem.

The owner is found with `Actor.firstEntity`, which starts at the actor itself and walks up
`parentActor` — the same reach the game gets from `ProcessSpawnedChildVarDefProcessor` running the
group on the actor *and its ancestors*, so an effect spawned by an effect still finds the hero.

## Clearing the cache is blunter than it looks

`DewSave.ApplySettings` ends with

```csharp
DewResources.ClearVariantsOfVarDef(DewResources.vOtherPlayersTonedDown, repairReferences: true);
```

which reads like "clear everything toned down". It is not. `ClearVariantsOfAsset` looks the target
up as a **dictionary key**:

```csharp
else if (value2.TryGetValue(target.Value, out value3)) { ... }
```

and `int` converts to a `VariantDef` with `id0` set and the rest zero. So that call only ever
matches a variant whose entire definition is that one id — and an ability instance always carries
`vQualityAdjusted` as well, so its definition never is. Whatever that line was meant to do, matching
by "contains this id" is not what it does.

This mod therefore calls `ClearAllVariants(repairReferences: true)` instead: everything goes and
rebuilds itself lazily. It runs on Apply and on unload, which is twice in a session, and it is the
same operation the game performs for a graphics setting.

## And `repairReferences` does nothing

The second surprise, and the one that costs a subscriber. Clearing a variant destroys the material
copies its processor made, and anything already spawned from that variant is still holding them — a
fireball in mid-flight when Apply is pressed is left with null materials, which draws as the
shader-missing magenta.

`repairReferences: true` looks like the answer to that and is not:

```csharp
public static void RepairMissingReferences_Prepare() { }
public static void RepairMissingReferences_Repair()  { }
```

Both are empty in the shipped assembly. The flag is threaded through `ClearVariantsOfAsset`,
`ClearVariantsOfVarDef` and `ClearAllVariants` and does nothing at either end.

The mechanism that actually works is the one `OnInit_vTonedDown` installs: a subscriber on
`DewResources.onVariantsCleared` that walks live `Actor`s, matches those whose GameObject name
contains its marker — `"(Other Players Toned Down)"`, appended by the processor — and puts
`DewResources.transparentMat` in place of every null material.

That handler matches on their string and would never find this mod's copies, so `Dimming` marks its
own with `"(TransparentEffects)"` and subscribes a second repairer of the same shape. `GetVariant`
copies the prefab name onto the variant and `SpawnManager.SpawnFromDewDatabaseHandler` copies it
again onto the spawned instance, which is how a marker written on a prefab ends up on the thing
flying across the screen.

## Pooled effect clones outlive a clear

`Repair` covers Actors. It does not cover the other thing holding this mod's materials:
`DewEffect`'s pools. Anything played with `FxPlayNew` — hits, muzzle flashes, most of what a
projectile leaves behind — is a clone kept under the `Pooled Fx` root and handed out again, and a
clone shares its template's materials. The pool is keyed by the parent's asset id and the child's
path, not by variant, and the flush the game hooks to `onVariantsCleared` destroys only clones that
are *inactive* at that moment.

With the settings screen open the game is paused, so the last cast's clones are still active when
Apply clears. They keep their destroyed materials, finish, go back into the pool — and are handed
out on the cast after next, with nothing to draw. (It was suspected for parts of Scattershot going
missing on later shots and ruled out — the purge below never fired then, and that turned out to be
the engine — but the hole is real.)

There is no right material to put back, so `Dimming.PurgePooledFx` destroys every clone under the
pool root that has a destroyed material. `PlayIntoPool` and `EffectAutoDestroy` both skip destroyed
entries, and the pool makes a fresh clone from the live template.

After this mod's own Apply that is only half the job. `PlayIntoPool` hands out any idle clone under
a key without asking which template made it, so a clone still playing when the setting goes from 1.0
to 0.5 keeps the *undimmed* asset materials — nothing destroyed, nothing to detect — and is handed
out at 0.5 from then on. So `Rebuild` purges the whole pool, and it refills as effects play.

## The alpha cascade is copied on purpose

`TonedDownProcessor` walks these shader properties in this order, and `Dimming.Fade` walks the same
list — with the departures in the next section, all of them on opaque surfaces:

```
_Cutoff always, then
  _Surface opaque  ->  _EmissionColor  (rgb down, alpha kept)
  otherwise        ->  the first of _Alpha, Vector1_2C5A3101,
                       Vector1_ba2f839299ad461eb6b76fbb90d387aa, _Opacity,
                       _BaseColor.a, _Color.a, _FinalOpacityPower, _ColorFactor
                       that exists; and _Multiplier if none of them do
```

Two of those names are generated shader-graph ids. That list is not a guess about what opacity is
called — it is the game's own evidence of what its effects were authored with, and the reason to
copy it rather than invent a shorter one is that a shorter one would silently miss a shader family.

Particle systems come along without being named: a `ParticleSystemRenderer` is a `Renderer` and its
material is dimmed with the rest.

## Except on opaque surfaces

The property list is the game's. What is done to two kinds of material is not, because the game's
version draws a visible artifact, and a debug report of what the cascade picked showed where.

`TonedDownProcessor` multiplies `_Cutoff` down on every material and, on an opaque one
(`_Surface` 0), darkens `_EmissionColor`. Scattershot's muzzle flash is made of dust particles on
`Dew/Dew Particles Unlit` at `_Surface` 0, `_ZWrite` 1, queue 2450 — opaque, alpha-clipped. A lower
cutoff lets the soft margin of the dust texture through as solid, and the darkening turns that into
a dark square behind the flash. The stock setting does the same to other players' effects.

So `Dimming.Fade` does this instead:

| Material | Game | This mod |
| --- | --- | --- |
| Opaque, alpha-clipped (`_AlphaClip`, `_ALPHATEST_ON`, or queue 2450–2499) | cutoff × a, emission × a | cutoff raised toward 1: `c + (1 − c)(1 − a)`, which erodes the shape; no darkening |
| Opaque, not clipped | emission × a | emission × a |
| Transparent | cutoff × a, then the cascade | the same, and `_EmissionColor` rgb × a where `_EMISSION` is on |
| Any with `_CMOpacity` (`Dew/Dew Entity`) | treated by `_Surface` like any other | `_CMOpacity` × a, and nothing else |

**Emission on transparent materials** is added to the colour before blending, and on the URP-style
particle shaders it is HDR: Lacerta's bullet glows at (1.4, 1.07, 0) on top of its base colour.
Blended at an alpha of 0.25 that is still brighter than the ground behind it, and after tonemapping
and bloom it looks barely dimmed; at 0.2 and at 1.0 the attack was hard to tell apart.

**`_CMOpacity`** is the entity shader's own opacity, the one `EntityVisual` drives when a hero fades.
A few effects use that shader for solid props — Teal Blade's sword is a `MeshRenderer` on
`Dew/Dew Entity` — and it is opaque by `_Surface`, so the game's rule only darkens it, and a dark
sword is still a solid sword.

**The transparent row is the game's on purpose, and it was learnt the hard way.** Lowering a
transparent material's cutoff looks as if it can only show more, and leaving it alone was tried: the
Dew particle shaders clip in transparent mode too, Lacerta's bullet is `_Surface` 1 with `_Cutoff`
0.5, and at a setting of 0.18 an alpha of 0.18 against a cutoff of 0.5 discards every pixel — the
basic attack disappeared outright. Multiplying both keeps the clipped shape where it was and leaves
the fading to the alpha.

And one addition: a particle renderer where none of the materials matched anything in the cascade
— `Mobile/Particles/Alpha Blended` has no colour property at all — has its `ParticleSystem`'s
start-colour alpha multiplied instead, which reaches any shader that takes vertex colour. Not on a
renderer with no material, though: it draws nothing, and it is usually a root whose start colour
sub-emitters inherit, so fading it would dim those twice.

**Lights go through `FxPointLight` where there is one.** The game multiplies `Light.intensity` and
touches `FxPointLight` only at *Hide*. But an `FxPointLight` that animates intensity rewrites
`Light.intensity` every frame from `_originalIntensity × intensityMultiplier`, the first serialized
on the prefab — so the multiplied intensity is gone on the first frame and the glow a muzzle flash
throws on the ground stays at full strength at every step short of *Hide*. The mod multiplies
`intensityMultiplier` for those lights and `Light.intensity` only for lights nothing animates.

**Emission comes down wherever a surface is thinned**, not only on transparent materials: a raised
cutoff and `_CMOpacity` both leave what remains drawn at full strength, and when it glows the glow
is HDR — Teal Blade's sword at (1.66, 3.0, 1.82), an InnerGlow at 2.8, a hit Glow at 4.9. Only where
`_EMISSION` is on, since a material can carry an `_EmissionColor` it never reads.

That was tried and taken back. The area is already going, and fading the glow on top made those
surfaces fade far ahead of everything else: Scattershot's muzzle dust pulled visibly ahead at a,
at a^0.5 and at a^0.25. The log then showed the glow was barely moving and the erosion was doing
the cutting — at 0.5 the dust's cutoff went from its authored 0.39 to 0.69, and a soft dust texture
is mostly low alpha. So the glow on a thinned surface is left alone (`@thinned-emission` = 0), and
the erosion is eased instead: the cutoff goes to `c + (1 − c)(1 − a^k)` with k = 0.6
(`@thinned-cutoff`), 0.60 at 0.5 and 0.77 at 0.2. Both are in the exponent table, overridable like
the rest.

## Per-shader exponents

Even with all of the above, one number did not look the same on every effect. Scattershot, whose
main parts are URP Particles Unlit and a dissolve graph taking `_Alpha`, looked about as dimmed as
the setting said; Pew and Teal Blade, built on the additive `_add` shader graphs, did not look half
gone until near 0.2–0.3. Additive HDR glow under tonemapping and bloom does not lose brightness in
proportion to its multiplier.

So a material fades by a^k, with k from a table keyed by shader name in `Exponents.cs` — 1.6 for
`Fx_Shoot&Hit_Particle_add`, `Fx_Hit&Slash_add_SG` and `Dew/Dew Entity` to start with, 1 for
anything unlisted. Per shader rather than per effect, so that a new effect on a known shader comes
calibrated. Lights, `FxEntityColor` and the start-colour fallback take the plain a.

Calibrating needs no rebuild: Debug builds read `exponents.txt` beside the mod's config
(`<persistentDataPath>/QuickSave/Mods/<modId>/`) on load and on every Apply, one
`shader name = k` per line, laid over the shipped table. Release builds never read it; numbers that
come out of calibration go into the table.

Debug builds log, per dimmed prefab, one line per material: its path in the prefab, renderer type,
shader, the property that took the multiplier (or `NOTHING SCALED`) and the blend state. It is in
`Player.log` under `[TransparentEffects] dimmed`, and it is how the above was found.

Two things `TonedDownProcessor` also does are left out. It thins particle **emission rates**, which
is not opacity and which this mod does not claim; and it disables renderers at its lowest step,
which is here but only at zero, where there is nothing left to draw.

## The two interfaces

- `IOtherPlayersTonedDownDisable` — an empty marker interface, a hard veto. `Se_HeroKnockedOut`
  carries it. Honoured for **both** rows: it marks the effects that have to stay readable whoever is
  looking, and a player dimming their own screen did not mean to lose a teammate's knockout either.
- `IOtherPlayersTonedDownLimit` — `ReduceOtherPlayerEffectsStrength maxReduction`, a floor rather
  than a veto. Honoured for the **other players'** row only, because that is the sentence it makes;
  the enum is mapped back to a multiplier through the game's own table. Nobody authored an opinion
  about how far you may dim your own effects, so there is nothing to honour on the first row.

## What it does not reach

Only `AbilityInstance` subclasses, because only they go through a variant. A great deal of what is
on screen during a fight does not: `FxPlay` of a plain `GameObject`, world decoration, hit sparks
parented to a victim. The game's own toned-down setting has exactly the same reach, which is a
useful calibration — if the stock setting at *Hide* still leaves something visible, this mod will
too.

Enemies are never touched. The condition requires `owner.isHumanPlayer`, so a monster's telegraph is
outside the mod by construction. That is a deliberate limit and not an oversight: dimming what is
about to hit you is a different mod with a different argument to make.

## In co-op

Nothing goes over the network and nothing needs to agree. The variant is chosen on the machine that
instantiates the effect, from that machine's own idea of who is local and who the camera is
following, and the effect itself is unchanged — same actor, same position, same damage, drawn at a
different alpha.

"Mine" follows the camera rather than the keyboard: while spectating a teammate, their effects are
drawn the way they would see their own. That is `CameraManager.focusedEntity`, and it is the same
choice `Entity.Awake` makes for the stock setting.

What was checked, and why each holds:

- **Host and client each choose for themselves.** On the host, `Actor.CreateAbilityInstance` asks
  `GetSuggestedVarDef` and the server object is built from the host's point of view — which is what
  the host sees, since host mode has no second copy. A remote client builds its own copy in
  `SpawnFromDewDatabaseHandler`, asking again from its point of view. So a client's own effects are
  "mine" on the client and "others" on the host, which is right on both screens.
- **Child effects still resolve across machines.** Effect messages name a child by `pathId`, an FNV
  hash of the names from the root *down*, seeded at the root without its name. Renaming the root with
  the marker does not change it, the marker has no `/` in it, and nothing below the root is renamed,
  added or removed. Disabled renderers at 0 are still there to be found.
- **No one else needs the mod.** `DewMod` marks a mod as gameplay-altering only when it ships an
  `overrides` folder of JSON, and only that feeds the lobby's `isModded` attribute.
- **Prewarm.** `RoomMonsters.ResolveClientPrewarmPrefab` goes through `GetSuggestedVarDef` with the
  owner's hero, so the patch applies and the prewarmed copies are the ones that will be used. Its
  fallback for an owner with no hero builds the game's own definition by hand and leaves this mod's
  id out; those copies go unused, which costs a little memory and draws nothing wrong.
- **Spectating switches mid-effect.** An actor keeps the variant it was spawned with, and a pooled
  clone is keyed by the game's own "toned down" flag at the moment it plays. In the second or so
  after the camera changes hands, a clone from the old point of view can be handed out under the new
  one. The stock setting has the same window; it closes on its own.
