# DevTools

The testing tool for the other mods, never published. It has two halves:

- **The picker**, an in-game window listing every essence and every memory the game has, to drop
  on the ground or put straight into a slot.
- **The agent API**, in a Debug build only: an HTTP server on `127.0.0.1:47653`. Through it an
  agent can read the game's state and play it end to end, from the title screen through a run to
  the result screen and back, without anyone at the keyboard.

Both are rebuilt from nothing. The panel that came before them (hero level and item level steppers,
god mode, the gem-tuning section for MoreGemSlots, *Forget that node's room*, the line-based
`devcmd` server) is gone. Everything it did can still be done through `/reflect` below. Its source
is in git history before the commit that removed it.

## Test runs earn nothing

**While DevTools is loaded, no run leaves anything on the profile.** A run driven by an agent, or
one with a legendary memory dropped from the picker, is a test. A profile that grew from tests
would no longer say how far its owner has actually come. `Game/Sandbox.cs` withholds each of these:

| What a run leaves | Where it is stopped |
| --- | --- |
| Traveler mastery | `Dew.GetRewardedMasteryPoints` answers 0. `DewSave.ConsumeGameResult` both adds that number and shows it on the result screen, so 0 covers both, conceded runs included |
| Stardust | `DewPlayer`'s `RpcGiveStardust` handler, the only way run stardust reaches the profile |
| Achievements | `AchievementManager.StartTrackingAchievements` is skipped, so there is no progress, no completion, no Steam stats. `CompleteAchievement` and `FlushProgressToProfile` are refused as well, for tracking that started before the mod loaded |
| Reveries | Tracking, saving progress and completion are all skipped |
| Collection discoveries | `DewProfile.DiscoverSkill`, `DiscoverGem` and `DiscoverArtifact`, in a run only. Outside a run the same calls are the profile's own upkeep |

It applies to every run while the mod is loaded, not only to runs where a cheat was used. An agent
playing honestly through the API is still running a test, and "was anything done to this run" has
more ways to be answered wrong than right. **To play for real, switch DevTools off in the mod
manager.**

Some things are left alone on purpose:

- **Per-hero statistics and the result history.** These are kills, damage, play count and play
  time, and the last-twenty list. They count things; they are not points. Blanking them would
  also blank the result screen, and reading that screen is often why the test was run.
- **A run that outlives the mod.** A run started with DevTools and finished without it, through
  Continue Dreaming, is scored normally, because nothing is left in the process to stop it.
  The reverse also holds: a real run finished while DevTools is loaded earns nothing.

Each withheld reward is logged as `[DevTools] test run: ... withheld`.

## The picker

`F8` by default. The key is a setting, and `F12` is not offered, because it is Steam's screenshot
key. The window has:

- Two tabs, Essences and Memories.
- A search box that matches the localized name and the type name.
- Rarity filters.
- A *hidden too* switch, which adds what the game keeps out of the loot pool: monster skills,
  heroes' own kits and excluded entries.
- A quality or level field. Shift on `-`/`+` steps by 100 or by 5.
- A row of target slots.

Each row has **Drop**, which spawns the item beside the hero, and **Equip**, which puts it straight
into the chosen slot. Equipping a memory uses `EquipSkill(..., ignoreCanReplace: true)`, so
another hero's kit or an Identity memory goes in too. Equipping an essence takes the first empty
socket, or the last socket if all are full. The game will not let a hero wear two essences of the
same type, so one worn elsewhere is taken off first. Whatever is displaced is dropped at the
hero's feet. Host only, as solo always is.

**IMGUI, not a uGUI panel.** A searchable list of five hundred rows with icons is a page of
IMGUI and several pages of uGUI. The cost is that the game cannot see the window. Its "is the
cursor over the interface" check (`DewInput.IsGameRelatedMouseInputValid`) is a uGUI raycast, so a
click on the list would also walk the hero there. So, while the cursor is over the window, the
game's mouse reads say no. While the search box has focus, `ControlManager.IsInputFieldFocused`
says yes, which is how chat keeps the hero's keys to itself. A click anywhere outside the window
hands the keyboard back.

The list comes from `Dew.allGems` and `Dew.allSkills`, which the game builds by scanning its
assemblies. A type without an asset is left out. Names come from the localisation tables, keyed by
type name, which costs nothing. Rarity, the pool flag and the icon come from the *light* variant of
each asset, which is what the Collection screen and the loot pool load: stripped of models and
effects. The picker loads a dozen per frame instead of all five hundred when it opens, and the
list refines itself as they arrive.

## The agent API

### Security

The server is bound to loopback, so nothing off this machine can reach it. Any process on this
machine can, which is the price of a test tool and the reason the server is compiled into Debug
builds only.

What is shut out is the visitor that is easy to forget: **a web page**. A browser will send a
"simple" POST to `127.0.0.1` from any site without asking. With `/reflect/call` on the other end,
that would let any site run code on this machine. So the server enforces three rules:

- A POST must carry `Content-Type: application/json`. A page can only send that after a CORS
  preflight, which this server never answers.
- Any request carrying `Origin` or `Sec-Fetch-Site` is refused. Only browsers send those headers.
- `Host` must name `127.0.0.1` or `localhost`, which defeats DNS rebinding.

None of the three costs curl, PowerShell or an agent anything.

### Talking to it

```powershell
.\tools\dev.ps1                                  # GET /  - every route with its parameters
.\tools\dev.ps1 state
.\tools\dev.ps1 hero/cast slot=Q x=12.5 z=-3     # key=value makes a JSON POST
.\tools\dev.ps1 flow/start_solo hero=Lacerta -Timeout 180
```

```sh
curl -s localhost:47653/state
curl -s -X POST -H "Content-Type: application/json" -d '{"slot":"Q","target":88}' localhost:47653/hero/cast
```

Conventions:

- A GET takes its arguments as a query string. A POST takes a JSON object; a query string on a
  POST is merged in.
- Every GET route also accepts a POST.
- Replies are `{ok:true, result}` or `{ok:false, error}`. A refusal is a 400 whose `error` says
  why.
- **Actors are addressed by `id`**, which is Mirror's `netId`: stable for the actor's life and
  already on every hero, monster, item, shrine and rift.
- Positions are world space, with the ground plane x/z. Screen positions are pixels from the
  bottom-left, as Unity's `Input` gives them.
- `timeout` (seconds, default 30, max 600) is how long a request waits for the game. Routes that
  wait for something, such as a walk or a run starting, give up a moment before it and answer
  with how far they got, rather than leaving the client to time out.
- `depth` controls how deep objects are written out. Past that depth an object comes back as
  `{"$ref": N}`.

### Routes

`GET /` is the reference that stays current. This table is the map.

| Group | Routes | For |
| --- | --- | --- |
| Where things stand | `GET /state`, `/hero`, `/entities`, `/interactables`, `/map`, `/lobby` | `/state` is the one to poll. It gives the scene, the UI state, whether loading, any message, conversation or edit screen waiting for an answer, the hero in brief, and the room: its exit, enemy count, reward shrines still unused (`unclaimed`), parts of the room with a fight still to come (`combatAreas`) and parts whose entering clears the room (`clearsOnEnter`). `/entities?kind=props` lists deposits (gold, dream dust, a nightmare stone), which are broken by attacking them. `/interactables` gives each shrine's offers and each merchant's stock |
| Essences in memories | `GET /hero/fit`, and `fit` on each essence `/hero` lists | Whether an essence can ever fire in a memory, by AreMyGemsCompatible's own verdict (`Fine` or `Dead`, and why), reached by reflection so neither mod references the other; null when that mod is not loaded. `/hero/fit` answers for the essence in hand, one by `id`, or a `type` as a merchant's stock or a shrine's offer names it, against every worn memory with sockets and how many are free. The verdict already counts the essences socketed beside it; a `Dead` one says what it is `missing` - an `element` its damage trigger answers to, or the `needs` (Damage, Heal, Shield, Cast) the memory never does. With it come the essence's `profile` (what wakes it, what it `supplies` a memory it is cast in, the elements it `adds`), what each memory `does`, and the essences already in each with their own verdicts and profiles - enough to tell which pairing would wake which. Each socketed essence in `/hero` carries its `id`, which it keeps on the ground when a click on its socket in the edit screen (held Ctrl) takes it out. For how much an essence gives where it fires: `/hero/fit` also carries the essence's own `limits` (an internal `cooldown`, a rate limit of `rateCount` uses per `rateSeconds`) and, per memory, its `cooldown` and `charges` in effect and its `use` this run; `GET /hero/use` lists that for every memory worn this run - casts, the hits it landed (every damage event an on-hit essence socketed there would see, since it listens to the same events), the sum of their proc coefficients and damage, the biggest hit, enemies reached per cast, and the seconds it was worn (in combat) |
| The ground and the danger | `GET /nav/grid`, `/nav/probe`, `/threats`, `/damage` | `/nav/grid` is the ground around the hero as a grid from the navmesh: where the hero can stand (lava and other hazard ground excluded), the walking distance to each cell, and the room to move around it. `/threats` is what is about to hit: enemy projectiles in flight (heading, speed, how close they will pass and when) and the red telegraphs on the ground (circles, rings, slices, boxes, how full, seconds left, whether the hero is in one), and blows that land at a point after a delay (`strike`: the damage's own radius and the seconds until it lands - among them a lobbed shot's blast where it comes down, a boss's or elite's explosion carried by a status effect, and ticking ground before its first tick; `zone` while such ground ticks; a `slice` for the part of a sweeping blow still to come; a `box` along a charge or a roll, `left` being when it reaches the hero's point on it; a `poly` - `corners` its outline - for a floor piece about to break, and for Primus's sword swings and the cones of its Jump Attack, from their wind-up (the cones' whole reach is a `strike` round the landing point until it lands); `safe` circles, the only places a coming blow does not reach, such as the shadows of Azurak's pillars during his roar; a `blast`, an explosion with no timer that goes off at the blow that ends a boss's phase, `fill` being how far its health is down). Two lists more: `dashers`, the monsters that dash at the hero (Despair's Phase Bugs) - where each is, how far its dash reaches, its charges, whether it can dash now or is winding one up (the lane it will dash along is then a `box` typed `Windup_At_Mon_Despair_Displacer_Dash`, and the dash itself a `box` while it goes); and `spinners`, a miniboss's spinning arrows - the carrier, the way the last arrow went and the way the next ones turn (`turn`, `step` degrees every `interval` s, `next` s to the next), when they start or are held, and each arrow's speed, radius and reach. The reply's `readers` says which readers the build has (39 and up: those polygons; 41 and up: `dashers` and `spinners`; 42 and up: Primus's Adapt-phase bolt as a `strike` where it is aimed from its wind-up and while it flies, typed `Windup_At_Mon_Primus_BossPrimusAeron_Adapt_Atk` / `Ai_Mon_Primus_BossPrimusAeron_Adapt_Atk`, with its chain's next links as `(next link)` strikes farther out from Primus; its Arbalest's aim line as a `box` typed `... (aim)`, `left` being when the next bolt reaches the hero's point on it; its Rage swipes and Dash Attack from their wind-up; and `primus`: its phase, weapon, health and shield; 46 and up: `doom`, Primus's Doom meteors - each one's centre, `left` until it lands (below 0 after, listed until its last fireballs are past their reach), and the fireball rings it sends when it lands: `n` fireballs a ring flying out along fixed spokes `step` degrees apart from the world's forward (`rings` rings `ringGap` s apart, every other ring turned `half` a step), their `speed`, `reach` and collision `radius`, the meteor's own `strike` radius and `stun`; 49 and up: White Night's Destruction Wave as its real shape, three `poly`s typed `Ai_Mon_Ink_BossWhiteNight_DestructionWave_Wave` in this order - the spike where it is now, `left` until it hurts; the fan it still sweeps, a `circle` past a full turn, `left` until the turn starts; the spike where the turn ends, `left` until no more than 30 degrees are left; 50 and up: Azurak's Atk from its wind-up as a `strike` typed `Windup_At_Mon_Despair_BossAzurak_Atk` where its blow lands - ahead of him, turned as he turns - not on him; 51 and up: Dark Moon's Blade as its real shape, a `poly` typed `Ai_Mon_Ink_BossDarkMoon_Blade` - a crescent round her front and sides where she stands, turned as she faces, `left` from her channel - her ShortDash's lance as a `poly`, and the polygon blows of Dark Moon and Infernus (Infernus's Atk from its wind-up) listed like Primus's; 52 and up: Belphomet's Atk, its dash at the hero, as a `box` typed `Windup_At_Mon_Forest_BossDemon_Atk (dash)` from its wind-up - along the dash, `left` being when the dash reaches the hero's point on it - and as a box typed `Ai_Mon_Forest_BossDemon_Atk` while it dashes; 54 and up: monsters' blows from their wind-up - the Snow Wolf's Pounce as a `box` typed `Windup_At_Mon_SnowMountain_SnowWolf_Pounce (dash)` along its dash (and typed `Ai_Mon_SnowMountain_SnowWolf_Pounce` while it dashes), Big Baam's beam as a `box` typed `Windup_At_Mon_Sky_BigBaam_BeamAtk` (three as a miniboss), `left` being when its growing tip reaches the hero's point on it, the Soul Swordsman's SwiftStep as a `strike` round where it will land behind the hero (typed `Windup_At_Mon_Ink_GhostBlade_SwiftStep`, then `Ai_Mon_Ink_GhostBlade_SwiftStep` while it steps), then its slash as a `poly` typed `Ai_Mon_Ink_GhostBlade_SwiftStep_Atk` and its shots' lines as `box`es typed `Ai_Mon_Ink_GhostBlade_SwiftStep_Projectile (line)`; the Seeker's polygon blows listed like Primus's; Dark Moon's hallucinations' Blade as a `poly` typed `Ai_Mon_Ink_BossDarkMoon_Blade_RageInstance`). `/damage` lists every hit the hero has taken: what dealt it, whose it was, how much, from how far |
| The hero | `POST /hero/move`, `move_dir`, `stop`, `attack`, `attack_in_place`, `cast`, `interact`, `dismantle`, `equip`, `drop_held` | The same `EntityControl.Cmd*` calls the game's own input handlers send. `move_dir` with `attack_in_place` shoots on the run. `dismantle` taps the alt interaction on an item on the ground until it breaks, as holding G does |
| The edit screen | `POST /edit/click`, `/edit/drag`, `/edit/end` | A click on a slot or a socket, meaning whatever the open screen means by it: equip what is held, upgrade at a well, sell at a shop; in the plain screen (LeftCtrl held) a drag from socket to socket, which swaps them |
| Getting between screens | `POST /flow/start_solo`, `wait_playing`, `result_continue`, `to_title`, `quit`, `POST /menu`, `/cutscene/skip` | From the title to a run and back |
| Answering the game | `POST /message/answer`, `/conversation/advance`, `/conversation/choose`, `/shrine/choose`, `/merchant/buy`, `/merchant/refresh`, `/map/travel` | Modal boxes, dialogue, and the windows interacting opens: shrine offers, shops, the world map at the exit |
| Any screen | `GET /ui`, `/ui/texts`, `POST /ui/click`, `/ui/set_text` | Menus nobody wrote a route for |
| Raw input | `GET /input`, `POST /input/key`, `/input/mouse`, `/input/release` | Keys and the cursor, as a player would press them |
| Looking | `POST /screenshot`, `GET /log` | A PNG path to look at; the Unity log since line N |
| The catalogue and cheats | `GET /content/essences`, `/content/memories`, `/content/heroes`, `POST /cheat/spawn`, `monster`, `level`, `currency`, `heal`, `kill`, `teleport`, `sockets`, `travel`, `next_zone`, `time_scale` | The picker's catalogue, and everything a player could not do. `/cheat/monster` spawns monsters the way a room does (the creep player, the zone's level), with extra Maximum Health on request, for a target that has to outlast a test; `EntityAI.DisableAI`, a static the game has, set through `/reflect/set`, stands them still |
| What essences really do | `POST /debug/essences/start`, `GET /debug/essences/watch`, `POST /debug/essences/unwatch` | Listens to every essence and memory the hero wears and counts what each actually did since the start: its `Gem.NotifyUse` calls, and everything done by it or by what it created (hits by element, heals, barriers, kills, instances). A memory's counts are split between itself and each essence in it. See **Watching essences** below |
| Everything else | `GET /reflect/get`, `/reflect/members`, `/reflect/types`, `/reflect/find`, `/handle`, `POST /reflect/set`, `/reflect/call`, `/console` | Any field, property or method in the process, and the game's own debug console |
| Mods | `GET /mods`, `POST /mods/reload` | See below |

A session from nothing looks like this:

```
POST /flow/start_solo {hero:"Lacerta", timeout:180}   -> stage "playing", or a message to answer
GET  /state                                            -> room: exitOpen, enemiesAlive, exitId
GET  /entities?kind=enemies                            -> ids, positions, hp
POST /hero/cast {slot:"W", target:88}
POST /hero/attack {target:88}
GET  /map                                              -> which nodes are reachable
POST /hero/interact {id:<room.exitId>}                -> walks to the exit and opens the world map
POST /map/travel {node:1}      then  POST /flow/wait_playing
POST /screenshot                                       -> {path} to look at
```

### A bot that plays a run

`tools/devbot.mjs` (Node 18+) plays a run through the API, using only the routes a player could
use. It is an example of the API put to work, and a way to test a build over many rooms without
anyone at the keyboard:

```sh
node tools/devbot.mjs auto                            # the hero must already be in a run
DEVBOT_TRACE=boss.log node tools/devbot.mjs auto      # plus a per-tick log of boss fights
```

In each room it fights, loots and moves on. For loot it takes items, uses reward shrines, shops at
merchants, breaks deposits and upgrades at wells. Then it walks to the exit and picks the next room
on the world map, on the way to the boss. It uses the boss's soul, goes through the rift into the
next zone, through the whole cycle to Primus, the final boss, and stops at the ending or when the
hero dies. It has won that run: about 20 minutes from the first room to Primus down. In a fight it circles its
target at shooting range, sideways, rather than backing away. Going round the other way gets it off
a wall. It steps off the line of incoming projectiles and out of the telegraphs, and dashes when
walking would not be quick enough. It shoots all the while. Bosses with chasing orbs and delayed
explosions still beat it.

### What a player could do, and what is a cheat

**Every route outside `/cheat`, `/reflect` and `/console` does only what a player
could do, the way the game's own interface does it.** An earlier version travelled with the
server's `CmdTravelToNode` from anywhere in the room. It chose from shrines and bought from
merchants across the room. It equipped items lying on the ground and upgraded at wells by calling
the well's command directly. The server accepts all of that, and none of it is possible for a
player. So:

- **Travel** is a click on the world map (`UI_InGame_WorldMap.TravelToNode`). The map is open
  only after the hero has walked to the open exit rift and used it, which is `/hero/interact`
  with the exit. The map's hunter warning and its other questions come up as they do for a
  player.
- **Choices and purchases** are the buttons of the window that interacting opened:
  `ClickChoice`, `ClickChaosItem`, `ClickMerchandise`, `ClickRefresh`. A route refuses unless
  `FloatingWindowManager.currentTarget` is that object.
- **Equipping and upgrading** are `EditSkillManager.DoClickOnSkillButton` and
  `DoClickOnGemSlot`, the handlers the slots themselves call. The screen's mode decides what a
  click means. The game's refusals ("not enough dream dust", "slot locked") are read from
  `InGameUIManager.ShowCenterMessageRaw` and returned as `refused`.
- **Game speed, travel without the rift, and moving to the next zone without it** are
  `/cheat/time_scale`, `/cheat/travel` and `/cheat/next_zone`.

### Watching essences

AreMyGemsCompatible answers whether an essence can fire in a memory by reading code. `/debug/essences` answers it by watching, and is what that mod's verdicts were checked against.

Two signals per essence, both the game's own:

- **`Gem.NotifyUse`**, how an essence says it has just acted: the flash on its socket, and a use off its rate limit. 86 of the 105 essence types call it. It is not always the effect, though. Lava, Finality, Talc, Shatter, Responsibility, Pure White, the Celestial, Heart of Gold and Last Starlight call it on the cast that arms them, before the condition that pays out, so for them it means "armed".
- **The essence's own actor events.** `Actor.InvokeOnDealDamage` and its siblings walk up `parentActor`, so a handler on the essence sees everything done by it and by what it created: projectiles, zones, status effects.

What an essence creates *through the cast* (`Create*WithSource(info.instance)`) is parented under the cast rather than the essence, so the essence's own counts never see it. The memory's counts put it down to the essence all the same, under `byEssence`, because such an instance names its essence in `AbilityInstance.Network_gem` (`Ai_R_Lava_LavaField`, Sharp's arrows, Scorched's fireballs). Copies of one kind in one memory share that entry. The memory's counts also give the element of every hit it dealt, which settles "does this memory deal Cold" by observation.

The route only listens. Nothing is patched but a postfix on `Gem.NotifyUse`, which no other mod here touches.

### How casting works

`/hero/cast` builds the `CastInfo` the cursor would have produced, going by the skill's
`castMethod`:

| `castMethod` | `CastInfo` |
| --- | --- |
| `None` | the caster alone |
| `Cone`, `Arrow` | an angle toward the point |
| `Point` | the point |
| `Target` | an entity. Given only `x,z`, the nearest valid entity within 4 m of that point |

The route then calls `ControlManager.CastAbility`, which is what a key press ends in. The server's
own checks (range, cooldown, silence) apply to it as they would to a player.

**Hold-to-charge skills** such as Lacerta's Precision Shot start *sampling* once cast. Every
frame, the game re-aims them from the cursor, and it fires them on a left click. So for the length
of the cast, the virtual cursor sits on the target. When the skill starts sampling, a left click
is sent after `charge` seconds. The reply's `charged` says whether that happened.

### Input: two devices, two routes in

The game reads **keys through the Input System** and **mouse buttons and the cursor through
legacy `Input`**, which nothing outside the engine can feed. So each gets a different treatment:

- **The keyboard** is given its state directly. `InputSystem.QueueStateEvent` queues a full
  `KeyboardState` of every key held, and every read the game makes then agrees, because all of
  them are reading a real device. One trap here: **the Input System disables the keyboard when the
  window loses focus**, and the window has always lost focus by the time a terminal sends
  anything. Setting `backgroundBehavior` to `IgnoreFocus` afterwards does not re-enable it, and a
  disabled device silently drops every event. So the keyboard is re-enabled before each state it
  is given.
- **The mouse** is answered by Harmony patches. They cover the three `DewInput.GetMouseButton*_Imp`
  reads, `ControlManager.GetMousePositionWithInversionInMind` (every cursor-to-world query funnels
  through it), and the game-area check. Presses are scheduled by frame: a press asked for in
  frame N is *down* in N+1, and its release lands at least one frame later. That is exactly what a
  real button gives, whichever of this and the game's `Update` runs first.

The virtual mouse drives the game, not the interface. Menus read the real pointer through the
EventSystem, so a button is clicked with `/ui/click`.

**Bindings are the profile's.** The profile this was built on moves with WASD and has *move*
unbound, so a right-click there does nothing, just as it would for the player. `/hero/move` does
not care, because it sends the command itself. `/input` users should read
`DewSave.profileMain.controls` first.

### UI without knowing the screen

`/ui` lists every active `Selectable` (buttons, toggles, input fields), plus every component with a
public parameterless `Click()`. The lobby's hero portraits and difficulty items, and the shop and
choice entries, are not Buttons at all. Each entry comes with its text, its path, its position on
screen, and **whether a click there would land**. The API raycasts the interface at the element's
centre. When something else is on top, `coveredBy` names it. That is how the first screen of a
fresh session gave itself away: every title button was covered by `UI_PlayRewardAnnouncer/Next
Button`, the mastery-reward screen, and its Next button was the one thing clickable.

A click is `GlobalUIManager.SimulateClickOnUIElement`, which sends pointer down, up and click on
the element. It is what the game itself does for a gamepad's confirm.

### Reflection

A path is a root followed by members:

```
ZoneManager.instance.currentNodeIndex                 a type, then statics, then instance members
$hero.Skill.gems        $player.gold                  the local hero and player
$12.Status.statusEffects[0].remainingDuration         $N: an object shown earlier as {"$ref": N}
#88.isAlive                                           #id: an actor by netId
DewSave.profileMain.heroes["Hero_Lacerta"]            indexers take numbers or quoted strings
$hero.GetComponent(HeroSkill)                         calls with literal arguments; a bare word is a type
```

The same forms work as arguments to `/reflect/call` (`"$12"`, `"#88"`, `"$hero"`), along with
`{x,y,z}` for a vector and a string for an enum or a type. Overloads are tried from the fewest
parameters up, and the first one the arguments convert to is called. `/reflect/members` lists
what a path points at, private members included on request.

`ZoneManager.instance` and its like work on every manager, because `ManagerBase<T>.instance` is
found through the generic base.

**A reloaded mod has several copies of its assembly loaded**, because .NET cannot unload one. A
short type name resolves to the last assembly that defines it, which is the live one.

### Things learned building it

- **A LINQ query is an `IEnumerator` too.** The server runs a route as a coroutine when its
  *declared* return type is `IEnumerator`, not when the returned object happens to be one. The
  first version got that wrong: `/content/heroes` came back as `null`, because its `Select` had
  been driven as a coroutine.
- **Unity stops calling `Update` when the window loses focus**, so `runInBackground` is on while
  the server is loaded.
- **The game reloads mods itself after a rebuild, but only while its window is focused** (see
  `DewMod`'s auto-reload `CheckRoutine`). `POST /mods/reload` does the same reload on request. It
  unloads this mod as well, so it answers first and reloads a moment later, from a coroutine the
  game owns.
- **A reload with the window in the background used to kill the keyboard for the session.** The
  server sets the Input System's `backgroundBehavior` to `IgnoreFocus` and the unloading copy puts
  the old behaviour back - at which moment, unfocused, the Input System disables every device that
  does not run in the background. The new copy's `IgnoreFocus` then means nothing ever turns them
  back on; the mouse kept working only because the game reads it through legacy `Input`. The server
  now re-enables any disabled device right after setting `IgnoreFocus` - the game never disables one
  itself.
- **Unity's own objects are written by name only.** Reading their properties blind is not safe:
  `Renderer.material` copies the material on every read. `GameObject` and `Transform` get a short
  summary. The game's own types are written in full.
- **Newtonsoft cannot serialise Unity types.** `Vector3.normalized` is itself a `Vector3`, and the
  serialiser recurses until it throws. `Server/Json.cs` walks objects itself, with a depth limit.
