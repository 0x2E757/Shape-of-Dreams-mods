# Multiplayer

All three published mods work in co-op, and are built along the boundary the game already draws.

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

**MapAutoRoute needs the host, and only the host decides.** The client's own adjacency check is
widened locally, which gets the command sent; the server checks the same thing again in
`UserCode_CmdTravelToNode` and, without the mod there, refuses it silently — the click does
nothing and says nothing. Everything the travel then does is server-side: crossing the rooms in
between, one turn of the hunt each.

Drawing is the other half and is per-client. Every copy of the mod works the route out for itself
from state the game already syncs — `isVoting`, `voteType`, `voteData`, `currentNodeIndex`, the
node list and the distance matrix are all in `ZoneManager.SerializeSyncVars` or are `SyncList`s —
so the route is recomputed rather than sent, and two players running the mod see the same line
without the mod exchanging a byte. **A player without the mod sees no line at all** under a vote
for a distant node: the game's own vote line is the single edge between the party and the voted
node, and when they are not adjacent there is no such edge to paint.

**Who needs to install what.** MoreGemSlots is required on both sides: the host decides the slot
counts, but *drawing* them is a local UI patch, and without it a guest's interface cannot render
more than three — the same failure as the "slots vanish at 5+" bug in **Getting past the essence
slot ceiling** in [moregemslots.md](moregemslots.md). AutoCast is not required on both sides; it is input automation and works for
whoever has it.

All of the above is read off the code and the game's API. It has not been verified in a live
two-client session — the one piece that only exercises there is the guest's `CmdCast` path.
