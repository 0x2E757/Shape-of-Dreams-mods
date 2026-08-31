# Planned

Seven mods that have names but no code. Six of them have been traced far enough to say where the
mod would hook, whether the host has to install it, and — where it matters — what the game already
does, so that nothing here gets built twice. The last one, `ParagonLevels`, is an idea written down
and not yet looked into; its section says so rather than guessing.

`FaceTheCursor` was the seventh and is now built; its section moved to
[facethecursor.md](facethecursor.md), which is the same notes with the answers filled in.

Everything below was read out of `Dew.Core`, `Dew.Contents` and `Dew.UI` decompiled with
`ilspycmd -p` against the same install `Directory.Build.props` points at. `Dew.Contents` will not
decompile as a whole project — one method, `Se_MorasDomain_MorasCreation.OnCreate`, throws
`BadImageFormatException` and takes the run down with it — so single types come out of it with
`-t <TypeName>` instead.

Line numbers are from that decompilation and will drift with the game. The type and method names
are the durable part.

## MoreLucidDreams

Run modifiers are **lucid dreams**: `LucidDream : GameEffect`, sorted by `LucidDreamType` into
`Good`, `Evil` and `Chaotic`. `Dew.Contents` ships fifteen — `BlandStarSoup`, `BonVoyage`,
`EmbraceMortality`, `FalseLifeline`, `FishScales`, `GrievousWounds`, `HarmlessWhispers`,
`KindArmadillo`, `MadLife`, `MarshOfDestiny`, `Overpopulation`, `PrudentJellyfish`,
`SparklingDreamFlask`, `TheDarkestUrge`, `WILD`.

`GameSettingsManager` owns them at runtime: `AddLucidDream(string)`, `RemoveLucidDream`,
`ClearLucidDreams`, and the two sync lists `activeLucidDreams` and `availableLucidDreams` — the
second being the union of what every human player has unlocked. **There is no cap on how many are
active**: `AddLucidDream` refuses only a name that fails to resolve through
`DewResources.GetByShortTypeName<LucidDream>` and one that is already in the list.

Three quite different mods hide under the one name, and they are worth keeping apart:

- **Retuning the existing fifteen needs no code at all.** `LucidDream_` is one of the twelve
  prefixes in `DewMod.AllowedPrefixesForJsonOverride`, and `float` is an allowed value type.
  `LucidDream_Overpopulation` is a single field, `popMultiplier = 1.5f`, multiplied into
  `GameManager.maxAndSpawnedPopulationMultiplier` on create and divided back out on destroy. A JSON
  override is the whole feature.
- **Unlocking all of them** is trivial and already taken: `Force Lucid Dream Enabler` on the
  workshop does it.
- **New lucid dreams** need a prefab registered in `DewResources` — its `database.typeNameToType`
  and `nameToGuid` are what `GetByShortTypeName` reads. Other mods add new essences and heroes, so
  it is possible, but it is much the heaviest thing on this page.

## PermanentDejavu

Wholly client-side; the server never sees it.

The free period is one dictionary on the profile and one static that reads it:

```csharp
// Dew.IsDejavuFree(string typeName)
DewSave.profileMain.dejavuCostReductionPeriodTimestamp.TryGetValue(typeName, out var value);
return value >= DateTime.UtcNow.ToTimestamp();
```

It is written in exactly one place, `DewPlayer.UserCode_TpcNotifyDejavuUse` — which spends
`profileMain.stardust`, adds the same amount to `spentStardust`, and stamps
`DateTime.UtcNow.AddHours(24.0)`.

Rent is charged by rarity and by how many wins the item already has, in `Dew.GetDejavuCost`:
common 75 down to 50, rare 120 to 80, epic 180 to 90, legendary and unique 350 to 150, with
`Dew.GetDejavuMaxWins` giving the number of steps. **A flat purchase price is therefore worth
thinking about**: a thousand stardust pays for three uses of a legendary and twenty of a common,
which is not the same offer twice.

A postfix on `Dew.IsDejavuFree` plus a button is the whole mod. `UI_Lobby_DejavuWindow` already
draws the remaining time from the same dictionary, so it is where the button belongs.

## CloserSouls

Host-side. One method decides where a soul goes:

```csharp
// Se_HeroKnockedOut.CheckAndAddHeroSoul(), public and server-only
TryGetNodeIndexForNextGoal(new GetNodeIndexSettings {
    desiredDistance = GameManager.instance.difficulty.lostSoulDistance,
    avoidMainModifier = false,
    preferCloserToExit = true,
}, out var nodeIndex);
ZoneManager.instance.AddModifier<RoomMod_HeroSoul>(nodeIndex, victim.owner.guid);
```

It runs from `OnCreate` — the moment of the knockout — and again from `ClientEventOnRoomLoaded` on
every room load after it.

**`TryGetNodeIndexForNextGoal` cannot return the current room.** Its scoring subtracts 10000 for
`i == currentNodeIndex` and another 10000 for `WorldNodeStatus.HasVisited`, so the first death has
to bypass it and call `AddModifier<RoomMod_HeroSoul>(ZoneManager.instance.currentNodeIndex, guid)`
directly. That is supported rather than smuggled: `AddModifier` has a branch for
`nodeIndex == currentNodeIndex && Room.instance.modifiers.isRoomActive` that routes through
`HandleRuntimeAddition`, and `Shrine_Chaos` and `Shrine_CorruptedChaos` both create a
`Shrine_HeroSoul` in the live room already. The second and third deaths can go back through
`TryGetNodeIndexForNextGoal` with `desiredDistance` of `(1, 1)` and `(2, 2)`.

Three things the mod has to carry itself or work around:

- **The death count is the mod's own state**, kept per player guid and cleared when the region
  changes. Nothing in `Se_HeroKnockedOut` counts deaths.
- **No soul is placed at all** when `ZoneManager.instance.currentNode.type` is
  `WorldNodeType.ExitBoss`, and that guard should stay.
- **The room-load call will not add a second soul** — it checks whether any node already carries a
  `RoomMod_HeroSoul` with that guid — so the escalation must be decided at knockout time, not on
  reload.

## TransparentEffects

Client-side, and **half of it is already a stock setting**, which is the first thing to know.

`DewSave.profileMain.gameplay.reduceOtherPlayerEffectsStrength` — declared in
`DewGameplaySettings_User` as `ReduceOtherPlayerEffectsStrength { Low, Medium, High, VeryHigh,
Hide }` — feeds `DewResources.TonedDownProcessor`, which multiplies alpha by 1, 0.7, 0.45, 0.25 or
0 along with two further factors, and reaches renderers and materials rather than only particle
systems. Prefabs opt out of it with `IOtherPlayersTonedDownDisable` (the knockout explosion does)
or cap it with `IOtherPlayersTonedDownLimit.maxReduction`.

What picks the variant is in `Entity.Awake`, in a `spawnedChildVarDefProcessor` delegate, and it is
narrow in exactly the way that leaves room for the mod: it adds `DewResources.vOtherPlayersTonedDown`
only for a spawned type that `IsSubclassOf(typeof(AbilityInstance))`, and only when the local player
— or the spectated one, if the camera is following someone else — is **not** the owner. **Your own
effects are never toned down by anything.**

So the mod is one new variant alongside the game's own. `DewResources.GetNextVariantId()` and
`RegisterVariantProcessor(int, ResourceVariantProcessor)` are both public and static, and
`Entity.spawnedChildVarDefProcessor` is the list to add to; the condition is the mirror image of the
stock one. Against other players the mod's claim is a continuous value instead of five steps, and
the option to ignore the per-prefab caps.

`DewEffect.ChangeColorRecursively(gobj, hue, saturationMult, valueMult, alphaMult)` is the blunter
alternative. Its `alphaMult` is genuinely plumbed through — particle start colour, colour over
lifetime, colour by speed, both custom data channels, `FxEntityColor`, `FxMeshTrail` and
`FxEntityShell` — but unlike the stock processor it never touches an ordinary renderer, so it dims
less than it looks like it should. `DewEffect.TintRecursively` is no use here at all: `TintObject`
opens by forcing `color.a = 1f`.

## BuildWhileDown

Client-side, and the gate is a single expression in `ControlManager.FrameUpdate`:

```csharp
shouldProcessCharacterInput = shouldProcessCharacterInputAllowKnockedOut
    && (!(controllingEntity is Hero hero) || !hero.isKnockedOut);
```

Two callers stand between a knocked-out player and their loadout: `UI_InGame_SkillButtons` and
`UI_InGame_SkillButton_EditSkill`. The pattern to copy is in the same codebase —
`UI_InGame_WorldMap` returns `shouldProcessCharacterInputAllowKnockedOut`, because the map is meant
to work while down. The mod moves those two onto the same flag.

**The server does not care.** `UserCode_CmdEquipGem_Internal` calls `EquipGem` and
`UserCode_CmdEquipSkill_Internal` calls `EquipSkill`, and while those two validate ownership and
refuse a second essence of the same type, neither looks at `isKnockedOut`. So the host does not
need the mod.

Worth confining to rearranging what is already equipped. `CmdMoveGem` and `CmdUnequipGem` take a
world position, and a knocked-out hero has had its renderers disabled and been teleported to its own
soul shrine by `Shrine_HeroSoul.OnCreate` — dropping an essence on the floor from there puts it
somewhere nobody meant.

## AreMyGemsCompatible

Client-side, and the interesting one, because the question it asks turns out to be well posed.

An essence reaches the memory it sits in through `Gem.OnEquipSkill`, and what it subscribes to is
that memory's own events, not the hero's:

```csharp
newSkill.TriggerEvent_OnCastComplete              += OnCastComplete;
newSkill.TriggerEvent_OnCastCompleteBeforePrepare += OnCastCompleteBeforePrepare;
newSkill.ActorEvent_OnDealDamage                  += OnDealDamage;
newSkill.ActorEvent_OnDoHeal                      += OnDoHeal;
```

Those four are the whole slot-scoped vocabulary — `Gem` declares no other virtual hook that a slot
can starve. Which of them an essence actually uses is a reflection question: a `GetMethod` whose
`DeclaringType` is the essence's own class rather than `Gem`. Every memory raises the two cast
events, so an essence built on those is live wherever it goes; `OnDealDamage` and `OnDoHeal` are
the ones a memory can simply never raise, and an essence with nothing else is then dead in that
slot.

**The trap is everything that does not go through the slot at all**, and three real essences show
all three shapes of it:

- `Gem_C_Sulfur` overrides no event. It adds `fireEffectAmpFlat` through
  `newOwner.Status.AddStatBonus` in `OnEquipGem` and is live in any slot whatsoever. So is anything
  with `enableStatBonus` set.
- `Gem_E_Twilight` hooks `newOwner.EntityEvent_OnAttackFiredBeforePrepare` in `OnEquipGem` **and**
  overrides `OnDealDamage`. Half of it ignores the slot entirely, so the worst it can ever be is
  diminished, never dead.
- `Gem_E_Insight` overrides `OnDealDamage` and nothing else. In a memory that deals no damage it
  never once fires, and it is the only one of the three worth a warning.

So the classification is three buckets, not two — slot-scoped, hero-scoped or passive, and mixed —
and only the first can be called incompatible. Getting that wrong in the loud direction is worse
than saying nothing.

`SkillTrigger.tags` and `Gem.tags` are both `DescriptionTags`, a flags enum of `Fire`, `Cold`,
`Dark`, `Light`, `Melee`, `HardCC`, `Heal`, `Shield`, `Attack`, `Mobility` and `Summon`, and
`SkillTrigger` syncs its copy. It is tempting as the whole answer and is not one: tags say what a
thing is, not what it needs. They are worth having as a second opinion, no more.

The honest fallback, and possibly the better product, is to answer the question by watching rather
than by reasoning: subscribe to the same four events, count what each essence in each slot actually
did, and report an essence that has not fired in some number of casts. It cannot be wrong about the
game's behaviour, because it is the game's behaviour. What it cannot do is warn before the run.

## ParagonLevels

Written down, not researched. The idea as it was given, and nothing added to it:

> Global progression for completed cycles.

None of what the six sections above carry has been done for this one — no entry point, no
host-or-client answer, no check on what the game already provides.
