# Multiplayer

All six published mods work in co-op, and are built along the boundary the game already draws.

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

**FaceTheCursor sends nothing and adds no messages.** It writes `EntityControl._localDesiredAngle`
on the hero this machine owns, and that field is already part of what the game replicates —
`UpdatePositionSyncData` sends it as `desiredAngle` alongside position and velocity, and every
other machine turns the hero to match in `DoMovementObserverFrameUpdate`. So a player running it is
seen turning correctly by everyone, players with no mods included, and two players who both have it
see each other aim without either of them adding a byte. It never touches `overridenDesiredAngle`,
the SyncVar the server owns.

The symmetric consequence is the one worth stating: **it moves nobody's hero but its own owner's.**
A player without it sends the angle the unmodded game chose, and everyone — including the modded
clients — replays exactly that. Showing where an unmodded player aims *is* possible, since the
server is told everyone's cursor in `DewPlayer.cursorWorldPos`, and it was built and then removed;
[facethecursor.md](facethecursor.md) records why, along with the finding that paid for it — an
unknown Mirror message id is a disconnect in both directions, so a modded guest speaking first to a
vanilla host would kick itself out of the run.

**TransparentEffects is a decision each screen makes alone.** A resource variant is chosen on the
machine that instantiates the effect, in `DewResources.GetSuggestedVarDef` — which the host reaches
through `Actor.CreateAbilityInstance` and every client reaches again through
`SpawnManager.SpawnFromDewDatabaseHandler` when Mirror tells it the effect exists. So the mod
answers a question the game was already asking locally, and the answer goes nowhere. The effect
itself is untouched: same actor, same position, same damage, drawn at a different alpha. "Mine"
follows the camera rather than the keyboard, so spectating a teammate shows their effects the way
they would see them, which is the choice the game's own toned-down check makes.

**CloserSouls is host-only in both directions.** `Se_HeroKnockedOut.CheckAndAddHeroSoul` runs behind
`isServer` and writes into `ZoneManager.nodes`, a SyncVar list, so a guest running it is patching a
method its own machine never calls. One installed copy changes the run for the whole party; a party
whose host does not have it plays the stock game however many other people do. It is the only one of
the six where installing it changes somebody else's game, which is why its description says so in
the first line about co-op rather than the last.

**Who needs to install what.** MoreGemSlots is required on both sides: the host decides the slot
counts, but *drawing* them is a local UI patch, and without it a guest's interface cannot render
more than three — the same failure as the "slots vanish at 5+" bug in **Getting past the essence
slot ceiling** in [moregemslots.md](moregemslots.md). AutoCast is not required on both sides; it is input automation and works for
whoever has it. Neither is FaceTheCursor, and for the strongest reason: what it
changes is already replicated, and it changes nothing about anyone else. TransparentEffects is the
same shape — one player, one screen, nothing sent. CloserSouls is the opposite, and the only one of
its kind here: it does nothing at all on a guest, and everything on a host.

Most of the above is read off the code and the game's API rather than watched happening. Several
pieces only exercise in a live two-client session, and one whole mod does: **CloserSouls cannot be
seen working alone at all.** Its own fourth guard is `Dew.GetAliveHeroCount() != 0`, and a solo
player who is knocked out leaves nobody alive, so no soul is ever placed and the run simply ends.
Everything it does needs a second player breathing.

The rest: AutoCast's guest `CmdCast` path, still
unverified; FaceTheCursor's hero as the *other* player sees it, which is the same kind of claim and
is unverified for the same reason; and MapAutoRoute's vote — which has now been seen, since a travel vote needs more than
one player to happen at all (`ShouldVoteOnTravel` is `gamePlayers.Count(...) > 1`). A vote was
started for a node three rooms out and the route drew, which means the server accepted the widened
command and the panel painted; see the screenshot in [mapautoroute.md](mapautoroute.md). What that
does not show is the *other* player's screen, which is the half still taken on trust.
