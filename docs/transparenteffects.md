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

## The alpha cascade is copied on purpose

`Dimming.Fade` walks the same shader properties in the same order as `TonedDownProcessor`:

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

`_Cutoff` being multiplied *down* makes an alpha-cutout material show more, not less. That is what
the game does, and it is copied unchanged rather than corrected, on the grounds that a mod's guess
about somebody else's shader convention is worth less than their own code.

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
