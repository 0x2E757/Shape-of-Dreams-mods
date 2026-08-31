# CloserSouls

Where a knocked-out player's soul is left. One method decides it, and the mod replaces the two
lines of that method which pick a node.

Line numbers are from `Dew.Core` decompiled with `ilspycmd` against the install
`Directory.Build.props` points at, and will drift. The type and method names are the durable part.

## The one method

```csharp
// Se_HeroKnockedOut.CheckAndAddHeroSoul(), public, and only ever called behind isServer
if (!disableQuest
    && zone.currentNode.type != WorldNodeType.ExitBoss
    && !zone.nodes.Any(n => n.modifiers.Any(m => m.type == "RoomMod_HeroSoul"
                                              && m.clientData == victim.owner.guid))
    && Dew.GetAliveHeroCount() != 0)
{
    zone.TryGetNodeIndexForNextGoal(new GetNodeIndexSettings {
        desiredDistance   = GameManager.instance.difficulty.lostSoulDistance,
        avoidMainModifier = false,
        preferCloserToExit = true,
    }, out var nodeIndex);
    zone.AddModifier<RoomMod_HeroSoul>(nodeIndex, victim.owner.guid);
}
```

It runs from `OnCreate` — the moment of the knockout — and again from `ClientEventOnRoomLoaded` on
every room load after it, because `Se_HeroKnockedOut` subscribes to `ZoneManager.
ClientEvent_OnRoomLoaded` for the length of the effect.

The mod is a prefix that restates the four guards and, when they all pass, chooses the node itself.
When any of them fails it returns `true` and hands the call back — so the game makes the same
decision it would have made alone, and a fifth guard added by a later patch is still applied.

## `TryGetNodeIndexForNextGoal` cannot answer "here"

This is the finding that shapes the whole mod. Its scoring, in `ZoneManager`, subtracts 10000 for
`i == currentNodeIndex` and another 10000 for `WorldNodeStatus.HasVisited`. The node it returns is
by construction somewhere the party is not and has not been. A distance of nought is not a thing it
can express.

So the zero case names the current node directly:

```csharp
zone.AddModifier<RoomMod_HeroSoul>(zone.currentNodeIndex, guid);
```

**That is supported rather than smuggled.** `ZoneManager.AddModifier` has a branch for exactly this
case:

```csharp
if (nodeIndex == currentNodeIndex && Room.instance != null
    && Room.instance.modifiers != null && Room.instance.modifiers.isRoomActive)
{
    modifierServerData[mod.id] = new ModifierServerData { didCreateInstance = false };
    Room.instance.modifiers.HandleRuntimeAddition(mod.id, beforePrepare);
}
```

`HandleRuntimeAddition` creates the modifier actor into the room as it stands, which fires
`RoomMod_HeroSoul.OnStartServer`, which asks `Room.instance.props.TryGetGoodNodePosition` for a spot
and spawns the shrine. `Shrine_Chaos` and `Shrine_CorruptedChaos` already put a `Shrine_HeroSoul`
into a live room by hand, so nothing downstream is surprised to see one arrive mid-fight.

The `isRoomActive` half of that condition is the reason the mod checks it too. If the room is
between loads, `AddModifier` takes the other branch and files the modifier for *the next time that
node is entered* — which, for the node you are already standing in, means it would not appear until
you came back to it. In that case the mod falls through to one room out instead, which is the
shortest honest answer.

## The count is the mod's own

Nothing in `Se_HeroKnockedOut` counts deaths. The tally is kept per player guid in the mod, and it
is reset when either the `ZoneManager` instance changes (a new run brings a new one) or
`currentZoneIndex` changes (a new region within a run). Both are cheap identity checks made at the
moment of placement, so there is no event to subscribe to and nothing to unsubscribe.

It is not saved and not synced. A host that reloads a save starts the region over at the first
death, which is the forgiving way to be wrong about it.

The count advances **once per soul actually placed**, not once per call — which matters because the
method is called on every room load. The third guard is what makes the repeats free: a soul already
on the map means there is nothing to place, and the prefix hands the call back before it reaches the
counter.

## Two guards worth leaving alone

**`WorldNodeType.ExitBoss`.** No soul is placed at all while the party is in the exit boss room, and
the mod keeps that. What happens instead is that the effect's room-load subscription fires once the
party is somewhere else and the soul is placed then — at which point it is the first placement for
that death and gets the first distance, which is right.

**`Dew.GetAliveHeroCount() != 0`.** By the time `CheckAndAddHeroSoul` runs, `hero.isKnockedOut` has
already been set, so this asks whether anyone is left to come and fetch you.

That second guard has a consequence worth stating plainly: **this mod cannot be seen working
alone.** A solo player who is knocked out leaves nobody alive, the count is nought, no soul is
placed by anyone, and the run ends. Every line of it needs a second player breathing, which makes it
the only one of the seven that cannot be checked in a single-player run at all.

## What the quest already handles

`Quest_LostSoul.GetAppropriateStep` is:

```csharp
return zone.currentNodeIndex != GetSoulNodeIndex() ? "MoveToSoul" : "SaveSoul";
```

so a soul in the room the party is standing in is a case the quest was written for. Nothing about
the quest, the shrine, the revive or the objective text needed changing.

## The hero moves to the shrine, not the other way round

`Shrine_HeroSoul.OnCreate`, server side:

```csharp
targetHero.Control.Teleport(Dew.GetValidAgentPosition(base.position));
targetHero.Visual.DisableRenderers();
```

So the knocked-out hero is pulled to wherever the shrine spawned and hidden. With this mod at its
default that is the room they fell in, which is also where the party is — and that, rather than the
distance number itself, is the thing that changes how a death feels.

Without the mod, or at a distance above nought, the hero stays at the spot it fell until the party
reaches the soul's room. This is worth knowing for a different reason: it is why `BuildWhileDown`
refuses to let a knocked-out player drop anything on the floor.

## In co-op

**The host needs it, and only the host.** `CheckAndAddHeroSoul` runs behind `isServer` and writes
into `ZoneManager.nodes`, a SyncVar list. A guest running this mod is patching a method its own
machine never calls; a guest without it sees the host's placement like everybody else.

So one installed copy changes the run for the whole party, and a party whose host does not have it
plays the stock game however many other people do. That is stated first in the description for the
same reason it is stated here: it is the only thing about this mod a player can get wrong.
